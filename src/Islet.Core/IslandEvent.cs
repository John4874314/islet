using System;

namespace Islet.Core
{
    /// <summary>
    /// 上岛事件。不可变；更新事件时构造新实例并复用同一个 Id。
    /// </summary>
    public sealed class IslandEvent
    {
        public IslandEvent(string id, IslandSource source, string title)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("id 不能为空", "id");
            if (title == null) throw new ArgumentNullException("title");

            Id = id;
            Source = source;
            Title = title;
            Actions = new IslandAction[0];
            Urgency = 50;
            Actionability = 50;
            CreatedAt = DateTimeOffset.Now;
        }

        /// <summary>去重键。同 Id 视为同一事件的更新，不新增槽位。</summary>
        public string Id { get; private set; }

        public IslandSource Source { get; private set; }

        public string Title { get; private set; }

        public string Subtitle { get; private set; }

        /// <summary>0..1；null 表示无进度。</summary>
        public double? Progress { get; private set; }

        public IslandAction[] Actions { get; private set; }

        /// <summary>0..100，时间敏感度。</summary>
        public int Urgency { get; private set; }

        /// <summary>0..100，一键完成程度。</summary>
        public int Actionability { get; private set; }

        /// <summary>存活时长；null 表示持续到显式移除。</summary>
        public TimeSpan? Ttl { get; private set; }

        /// <summary>用户固定后不被软抢占。</summary>
        public bool Pinned { get; private set; }

        /// <summary>硬抢占：无视分差直接夺槽。</summary>
        public bool HardPreempt { get; private set; }

        public DateTimeOffset CreatedAt { get; private set; }

        // ---- 流式构造器 ----

        public IslandEvent WithSubtitle(string value)
        {
            IslandEvent c = Clone();
            c.Subtitle = value;
            return c;
        }

        public IslandEvent WithProgress(double? value)
        {
            IslandEvent c = Clone();
            c.Progress = value;
            return c;
        }

        public IslandEvent WithActions(params IslandAction[] value)
        {
            IslandEvent c = Clone();
            c.Actions = value ?? new IslandAction[0];
            return c;
        }

        public IslandEvent WithUrgency(int value)
        {
            IslandEvent c = Clone();
            c.Urgency = Clamp(value);
            return c;
        }

        public IslandEvent WithActionability(int value)
        {
            IslandEvent c = Clone();
            c.Actionability = Clamp(value);
            return c;
        }

        public IslandEvent WithTtl(TimeSpan? value)
        {
            IslandEvent c = Clone();
            c.Ttl = value;
            return c;
        }

        public IslandEvent WithPinned(bool value)
        {
            IslandEvent c = Clone();
            c.Pinned = value;
            return c;
        }

        public IslandEvent WithHardPreempt(bool value)
        {
            IslandEvent c = Clone();
            c.HardPreempt = value;
            return c;
        }

        /// <summary>仅供仲裁器在合并同 Id 更新时回填原始创建时间，业务代码不要调用。</summary>
        public IslandEvent WithCreatedAt(DateTimeOffset value)
        {
            IslandEvent c = Clone();
            c.CreatedAt = value;
            return c;
        }

        public bool IsExpired(DateTimeOffset now)
        {
            if (!Ttl.HasValue) return false;
            return now - CreatedAt >= Ttl.Value;
        }

        private IslandEvent Clone()
        {
            IslandEvent c = new IslandEvent(Id, Source, Title);
            c.Subtitle = Subtitle;
            c.Progress = Progress;
            c.Actions = Actions;
            c.Urgency = Urgency;
            c.Actionability = Actionability;
            c.Ttl = Ttl;
            c.Pinned = Pinned;
            c.HardPreempt = HardPreempt;
            c.CreatedAt = CreatedAt;
            return c;
        }

        private static int Clamp(int v)
        {
            if (v < 0) return 0;
            if (v > 100) return 100;
            return v;
        }
    }
}
