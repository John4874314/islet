using System;
using System.Collections.Generic;
using System.Linq;

namespace Islet.Core
{
    /// <summary>
    /// 单槽优先级仲裁器。整个产品的灵魂：同一时刻只让一件事占据主槽位。
    /// 纯逻辑，无 UI 依赖，可单测。
    /// </summary>
    public sealed class IslandSlotManager : IIslandSink
    {
        /// <summary>队列上限。溢出时丢弃分数最低者。</summary>
        public const int MaxQueue = 8;

        /// <summary>被用户关闭后进入冷却的时长。</summary>
        public static readonly TimeSpan CooldownDuration = TimeSpan.FromSeconds(60);

        private readonly object _gate = new object();
        private readonly Dictionary<string, IslandEvent> _pending =
            new Dictionary<string, IslandEvent>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTimeOffset> _cooldowns =
            new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        private IslandEvent _current;
        private double _currentScore;
        private bool _preemptionSuspended;

        /// <summary>当前槽位事件变化。参数为 null 表示槽位空。</summary>
        public event EventHandler<IslandSlotChangedEventArgs> CurrentChanged;

        /// <summary>
        /// 暂停抢占。用户正在与卡片交互时调用（PRD P3：不打断人）。
        /// 暂停期间仍接受新事件，但只排队、不夺槽；恢复后立即重新仲裁。
        /// </summary>
        public void SuspendPreemption()
        {
            lock (_gate) { _preemptionSuspended = true; }
        }

        /// <summary>恢复抢占并立即重新仲裁。</summary>
        public void ResumePreemption()
        {
            IslandEvent toRaise = null;
            bool changed = false;

            lock (_gate)
            {
                _preemptionSuspended = false;
                toRaise = RebalanceLocked(out changed);
            }

            if (changed) Raise(toRaise);
        }

        public bool IsPreemptionSuspended
        {
            get { lock (_gate) { return _preemptionSuspended; } }
        }

        /// <summary>排队事件数量，供调试与设置页展示。</summary>
        public int QueueDepth
        {
            get { lock (_gate) { return _pending.Count; } }
        }

        public IslandEvent Current
        {
            get { lock (_gate) { return _current; } }
        }

        // ---------------------------------------------------------------
        // IIslandSink
        // ---------------------------------------------------------------

        public void Publish(IslandEvent evt)
        {
            if (evt == null) throw new ArgumentNullException("evt");

            IslandEvent toRaise = null;
            bool changed = false;

            lock (_gate)
            {
                if (IsCoolingDown(evt.Id, DateTimeOffset.Now))
                {
                    return; // 用户刚关掉它，尊重决定
                }

                // 同 Id 更新：若正占据槽位，就地替换并保留原始创建时间
                if (_current != null && string.Equals(_current.Id, evt.Id, StringComparison.Ordinal))
                {
                    _current = Rebase(evt, _current.CreatedAt);
                    _currentScore = IslandScore.Compute(_current, DateTimeOffset.Now, false);
                    toRaise = _current;
                    changed = true;
                }
                else
                {
                    IslandEvent existing;
                    if (_pending.TryGetValue(evt.Id, out existing))
                    {
                        // 保留最早的创建时间，避免靠刷新不断吃新鲜加成
                        _pending[evt.Id] = Rebase(evt, existing.CreatedAt);
                    }
                    else
                    {
                        _pending[evt.Id] = evt;
                    }
                }

                if (!changed)
                {
                    toRaise = RebalanceLocked(out changed);
                }
            }

            if (changed) Raise(toRaise);
        }

        public void Retract(string id)
        {
            if (string.IsNullOrEmpty(id)) return;

            IslandEvent toRaise = null;
            bool changed = false;

            lock (_gate)
            {
                _pending.Remove(id);

                if (_current != null && string.Equals(_current.Id, id, StringComparison.Ordinal))
                {
                    _current = null;
                    _currentScore = 0;
                    toRaise = RebalanceLocked(out changed);
                    // 槽位由有变无必须通知 UI，见 Tick() 里的同类说明
                    changed = true;
                }
            }

            if (changed) Raise(toRaise);
        }

        public void RetractSource(IslandSource source)
        {
            IslandEvent toRaise = null;
            bool changed = false;

            lock (_gate)
            {
                List<string> doomed = _pending
                    .Where(kv => kv.Value.Source == source)
                    .Select(kv => kv.Key)
                    .ToList();
                foreach (string id in doomed) _pending.Remove(id);

                if (_current != null && _current.Source == source)
                {
                    _current = null;
                    _currentScore = 0;
                    toRaise = RebalanceLocked(out changed);
                    // 同上：槽位清空必须通知 UI
                    changed = true;
                }
            }

            if (changed) Raise(toRaise);
        }

        // ---------------------------------------------------------------
        // 生命周期
        // ---------------------------------------------------------------

        /// <summary>用户主动关闭当前事件：进入冷却，交还槽位。</summary>
        public void DismissCurrent()
        {
            IslandEvent toRaise = null;
            bool changed = false;

            lock (_gate)
            {
                if (_current == null) return;
                _cooldowns[_current.Id] = DateTimeOffset.Now + CooldownDuration;
                _pending.Remove(_current.Id);
                _current = null;
                _currentScore = 0;
                toRaise = RebalanceLocked(out changed);
            }

            if (changed) Raise(toRaise);
        }

        /// <summary>用户固定/取消固定当前事件。</summary>
        public void SetPinned(bool pinned)
        {
            IslandEvent toRaise = null;
            bool changed = false;

            lock (_gate)
            {
                if (_current == null) return;
                _current = _current.WithPinned(pinned);
                _currentScore = IslandScore.Compute(_current, DateTimeOffset.Now, false);
                toRaise = _current;
                changed = true;
            }

            if (changed) Raise(toRaise);
        }

        /// <summary>
        /// 时间推进。清理过期项、冷却记录，并在槽位空出时补位。
        /// 由宿主定时（建议 1 秒）调用；仲裁本身不依赖高频轮询。
        /// </summary>
        public void Tick(DateTimeOffset now)
        {
            IslandEvent toRaise = null;
            bool changed = false;

            lock (_gate)
            {
                PruneCooldownsLocked(now);

                if (_current != null && _current.IsExpired(now))
                {
                    _current = null;
                    _currentScore = 0;
                    toRaise = RebalanceLocked(out changed);

                    // 关键：槽位从「有」变成「无」本身就是一次状态变化，
                    // 必须通知 UI。RebalanceLocked 在队列也空时返回 null 且
                    // changed=false —— 若不在这里强制置位，UI 永远收不到
                    // 「槽位空了」的消息，胶囊会一直挂着已过期的事件。
                    changed = true;
                }
                else if (_current == null)
                {
                    toRaise = RebalanceLocked(out changed);
                }
            }

            if (changed) Raise(toRaise);
        }

        // ---------------------------------------------------------------
        // 内部
        // ---------------------------------------------------------------

        /// <summary>在锁内重新选出槽位主人。返回新主人（可能为 null）。</summary>
        private IslandEvent RebalanceLocked(out bool changed)
        {
            changed = false;
            DateTimeOffset now = DateTimeOffset.Now;

            // 剔除过期排队项
            List<string> expired = _pending
                .Where(kv => kv.Value.IsExpired(now))
                .Select(kv => kv.Key)
                .ToList();
            foreach (string id in expired) _pending.Remove(id);

            // 队列上限：丢弃分数最低者
            while (_pending.Count > MaxQueue)
            {
                string worst = _pending
                    .OrderBy(kv => IslandScore.Compute(kv.Value, now, false))
                    .Select(kv => kv.Key)
                    .First();
                _pending.Remove(worst);
            }

            // 选出最高分候选
            IslandEvent best = null;
            double bestScore = double.MinValue;
            foreach (KeyValuePair<string, IslandEvent> kv in _pending)
            {
                double s = IslandScore.Compute(kv.Value, now, false);
                if (s > bestScore)
                {
                    bestScore = s;
                    best = kv.Value;
                }
            }

            if (best == null) return null;

            if (_current == null)
            {
                _pending.Remove(best.Id);
                _current = best;
                _currentScore = bestScore;
                changed = true;
                return best;
            }

            if (IslandScore.ShouldPreempt(best, bestScore, _current, _currentScore))
            {
                // P3：用户正在看卡片时只排队，不夺槽
                if (_preemptionSuspended) return null;

                // 被抢占者回到队列（若未过期），并可标记冷却扣分
                if (!_current.IsExpired(now) && !_current.Pinned)
                {
                    _pending[_current.Id] = _current;
                }
                _pending.Remove(best.Id);
                _current = best;
                _currentScore = bestScore;
                changed = true;
                return best;
            }

            return null;
        }

        private bool IsCoolingDown(string id, DateTimeOffset now)
        {
            DateTimeOffset until;
            if (!_cooldowns.TryGetValue(id, out until)) return false;
            if (now >= until)
            {
                _cooldowns.Remove(id);
                return false;
            }
            return true;
        }

        private void PruneCooldownsLocked(DateTimeOffset now)
        {
            List<string> stale = _cooldowns
                .Where(kv => now >= kv.Value)
                .Select(kv => kv.Key)
                .ToList();
            foreach (string id in stale) _cooldowns.Remove(id);
        }

        private static IslandEvent Rebase(IslandEvent evt, DateTimeOffset createdAt)
        {
            // 用反射之外的方式无法改 CreatedAt，这里借助 Ttl 语义：重建一个等价事件
            IslandEvent c = new IslandEvent(evt.Id, evt.Source, evt.Title)
                .WithSubtitle(evt.Subtitle)
                .WithProgress(evt.Progress)
                .WithActions(evt.Actions)
                .WithUrgency(evt.Urgency)
                .WithActionability(evt.Actionability)
                .WithTtl(evt.Ttl)
                .WithPinned(evt.Pinned)
                .WithHardPreempt(evt.HardPreempt);
            return c.WithCreatedAt(createdAt);
        }

        private void Raise(IslandEvent evt)
        {
            EventHandler<IslandSlotChangedEventArgs> h = CurrentChanged;
            if (h == null) return;
            h(this, new IslandSlotChangedEventArgs(evt));
        }
    }

    public sealed class IslandSlotChangedEventArgs : EventArgs
    {
        public IslandSlotChangedEventArgs(IslandEvent current)
        {
            Current = current;
        }

        /// <summary>null 表示槽位空。</summary>
        public IslandEvent Current { get; private set; }
    }
}
