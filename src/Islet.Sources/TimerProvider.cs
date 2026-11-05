using System;
using System.Threading;
using Islet.Core;

namespace Islet.Sources
{
    /// <summary>
    /// 倒计时 / 番茄钟。用户主动启动，意图明确，因此权重高于进度类。
    /// 单实例：同一时刻只有一个计时器。
    /// </summary>
    public sealed class TimerProvider : IIslandProvider
    {
        private const string EventId = "islet.timer";

        private IIslandSink _sink;
        private Timer _ticker;
        private DateTimeOffset _deadline;
        private TimeSpan _total;
        private bool _running;
        private bool _disposed;

        public string Name
        {
            get { return "Timer"; }
        }

        public void Attach(IIslandSink sink)
        {
            _sink = sink;
            _ticker = new Timer(Tick, null, 500, 500);
        }

        /// <summary>启动一个倒计时。重复调用会替换现有计时器。</summary>
        public void Start(string label, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException("duration");

            _total = duration;
            _deadline = DateTimeOffset.Now + duration;
            _running = true;
            Publish(label ?? "倒计时");
        }

        public void Pause()
        {
            _running = false;
            if (_sink != null) _sink.Retract(EventId);
        }

        public void Stop()
        {
            _running = false;
            if (_sink != null) _sink.Retract(EventId);
        }

        private void Tick(object state)
        {
            if (_disposed || !_running || _sink == null) return;

            TimeSpan remain = _deadline - DateTimeOffset.Now;

            if (remain <= TimeSpan.Zero)
            {
                _running = false;
                // 到点：硬抢占 + 短 TTL，确保用户注意到后自动退场
                _sink.Publish(new IslandEvent(EventId, IslandSource.Timer, "时间到")
                    .WithSubtitle("计时结束")
                    .WithUrgency(100)
                    .WithActionability(60)
                    .WithHardPreempt(true)
                    .WithTtl(TimeSpan.FromSeconds(8))
                    .WithActions(new IslandAction("知道了", () => _sink.Retract(EventId))));
                return;
            }

            Publish(null);
        }

        private void Publish(string overrideLabel)
        {
            if (_sink == null || !_running) return;

            TimeSpan remain = _deadline - DateTimeOffset.Now;
            if (remain < TimeSpan.Zero) remain = TimeSpan.Zero;

            double progress = _total.TotalSeconds <= 0
                ? 0
                : 1.0 - (remain.TotalSeconds / _total.TotalSeconds);

            string label = overrideLabel ?? "倒计时";
            string text = FormatRemain(remain);

            _sink.Publish(new IslandEvent(EventId, IslandSource.Timer, label)
                .WithSubtitle(text)
                .WithProgress(progress)
                .WithUrgency(70)
                .WithActionability(55)
                .WithActions(
                    new IslandAction("结束", Stop)));
        }

        private static string FormatRemain(TimeSpan t)
        {
            if (t.TotalHours >= 1)
                return string.Format("{0}:{1:00}:{2:00}",
                    (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }

        public void Dispose()
        {
            _disposed = true;
            _running = false;
            if (_ticker != null)
            {
                _ticker.Dispose();
                _ticker = null;
            }
            _sink = null;
        }
    }
}
