using System;

namespace Islet.Core
{
    /// <summary>优先级评分。纯函数，可单测。</summary>
    public static class IslandScore
    {
        /// <summary>滞回带宽：新事件必须超出当前这么多分才软抢占。</summary>
        public const int Hysteresis = 15;

        /// <summary>固定标记的加成。</summary>
        public const int PinnedBonus = 200;

        /// <summary>新事件新鲜期（秒）内的加成。</summary>
        public const int FreshWindowSeconds = 60;

        public const int FreshBonus = 10;

        /// <summary>被抢占后的冷却期内扣分。</summary>
        public const int RepeatPenalty = 25;

        public static int BaseWeight(IslandSource source)
        {
            switch (source)
            {
                case IslandSource.Meeting: return 80;
                case IslandSource.Privacy: return 75;
                case IslandSource.Health: return 70;
                case IslandSource.Timer: return 55;
                case IslandSource.Progress: return 50;
                case IslandSource.Clipboard: return 45;
                case IslandSource.Media: return 10;
                default: return 0;
            }
        }

        /// <summary>
        /// score = BaseWeight + Urgency*0.40 + Actionability*0.30 + Pinned + Freshness - Repeat
        /// </summary>
        public static double Compute(IslandEvent evt, DateTimeOffset now, bool inRepeatCooldown)
        {
            if (evt == null) throw new ArgumentNullException("evt");

            double score = BaseWeight(evt.Source);
            score += evt.Urgency * 0.40;
            score += evt.Actionability * 0.30;

            if (evt.Pinned) score += PinnedBonus;

            double ageSeconds = (now - evt.CreatedAt).TotalSeconds;
            if (ageSeconds < FreshWindowSeconds)
            {
                double remain = 1.0 - (ageSeconds / FreshWindowSeconds);
                score += FreshBonus * remain;
            }

            if (inRepeatCooldown) score -= RepeatPenalty;

            return score;
        }

        /// <summary>新事件是否应替换当前事件。</summary>
        public static bool ShouldPreempt(IslandEvent incoming, double incomingScore,
                                         IslandEvent current, double currentScore)
        {
            if (incoming == null) return false;
            if (current == null) return true;
            if (incoming.HardPreempt) return true;
            if (current.Pinned) return false;
            return incomingScore > currentScore + Hysteresis;
        }
    }
}
