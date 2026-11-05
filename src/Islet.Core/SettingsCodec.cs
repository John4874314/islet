using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Islet.Core
{
    /// <summary>
    /// 设置的文本编解码。格式刻意做得极简（key = value 每行一条）：
    ///   · 无第三方依赖
    ///   · 人手可改、可读、可 diff
    ///   · 解析容错：未知键忽略、坏值回退默认，绝不让一个坏配置把程序卡死
    ///
    /// 纯函数，无 IO，可直接单测。
    /// </summary>
    public static class SettingsCodec
    {
        public const string Header = "# Islet settings v1";

        public static string Serialize(IsletSettings s)
        {
            if (s == null) throw new ArgumentNullException("s");

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(Header);
            sb.AppendLine();

            sb.AppendLine("# 数据源（false = 关闭该来源，不再上岛）");
            sb.AppendLine("media = " + Bool(s.MediaEnabled));
            sb.AppendLine("timer = " + Bool(s.TimerEnabled));
            sb.AppendLine("progress = " + Bool(s.ProgressEnabled));
            sb.AppendLine("health = " + Bool(s.HealthEnabled));
            sb.AppendLine("clipboard = " + Bool(s.ClipboardEnabled));
            sb.AppendLine("privacy = " + Bool(s.PrivacyEnabled));
        sb.AppendLine("bluetooth = " + Bool(s.BluetoothEnabled));
            sb.AppendLine();

            sb.AppendLine("# 外观与行为");
            sb.AppendLine("position = " + PositionText(s.Position));
            sb.AppendLine("opacity = " + OpacityText(s.Opacity));

            // 自由拖拽坐标。NaN 表示「没拖过」，不写进文件，让程序用默认位置。
            if (!double.IsNaN(s.FreeX) && !double.IsNaN(s.FreeY))
            {
                sb.AppendLine("freeX = " + s.FreeX.ToString("F1", CultureInfo.InvariantCulture));
                sb.AppendLine("freeY = " + s.FreeY.ToString("F1", CultureInfo.InvariantCulture));
            }

            sb.AppendLine("hideInFullscreen = " + Bool(s.HideInFullscreen));
            sb.AppendLine();

            sb.AppendLine("# 系统集成");
            sb.AppendLine("autorun = " + Bool(s.AutorunEnabled));
            sb.AppendLine("trimMemoryWhenIdle = " + Bool(s.TrimMemoryWhenIdle));
            sb.AppendLine();

            sb.AppendLine("# 剪贴板提示保留秒数（4 - 120）");
            sb.AppendLine("clipboardTtlSeconds = " +
                          s.ClipboardTtlSeconds.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>
        /// 解析配置文本。任何非法值都退回默认，不抛异常。
        /// </summary>
        public static IsletSettings Parse(string text)
        {
            IsletSettings s = new IsletSettings();
            if (string.IsNullOrEmpty(text)) return s;

            string[] lines = text.Split(new[] { '\r', '\n' },
                                        StringSplitOptions.RemoveEmptyEntries);

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (key.Length == 0) continue;

                Apply(s, key, value);
            }

            return s;
        }

        private static void Apply(IsletSettings s, string key, string value)
        {
            switch (key.ToLowerInvariant())
            {
                case "media": s.MediaEnabled = ParseBool(value, s.MediaEnabled); break;
                case "timer": s.TimerEnabled = ParseBool(value, s.TimerEnabled); break;
                case "progress": s.ProgressEnabled = ParseBool(value, s.ProgressEnabled); break;
                case "health": s.HealthEnabled = ParseBool(value, s.HealthEnabled); break;
                case "clipboard": s.ClipboardEnabled = ParseBool(value, s.ClipboardEnabled); break;
                case "privacy": s.PrivacyEnabled = ParseBool(value, s.PrivacyEnabled); break;
                case "bluetooth": s.BluetoothEnabled = ParseBool(value, s.BluetoothEnabled); break;

                case "position": s.Position = ParsePosition(value, s.Position); break;
                case "opacity": s.Opacity = ParseOpacity(value, s.Opacity); break;
                case "freex": s.FreeX = ParseDouble(value, s.FreeX); break;
                case "freey": s.FreeY = ParseDouble(value, s.FreeY); break;
                case "hideinfullscreen":
                    s.HideInFullscreen = ParseBool(value, s.HideInFullscreen); break;

                case "autorun": s.AutorunEnabled = ParseBool(value, s.AutorunEnabled); break;
                case "trimmemorywhenidle":
                    s.TrimMemoryWhenIdle = ParseBool(value, s.TrimMemoryWhenIdle); break;

                case "clipboardttlseconds":
                    s.ClipboardTtlSeconds = ParseInt(value, s.ClipboardTtlSeconds, 4, 120); break;

                // 未知键静默忽略：向前兼容旧/新版本配置文件
            }
        }

        private static bool ParseBool(string v, bool fallback)
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            switch (v.Trim().ToLowerInvariant())
            {
                case "true": case "1": case "yes": case "on": return true;
                case "false": case "0": case "no": case "off": return false;
                default: return fallback;
            }
        }

        private static int ParseInt(string v, int fallback, int min, int max)
        {
            int n;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                return fallback;
            if (n < min) return min;
            if (n > max) return max;
            return n;
        }

        private static IslandPosition ParsePosition(string v, IslandPosition fallback)
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            switch (v.Trim().ToLowerInvariant())
            {
                case "topcenter": case "center": return IslandPosition.TopCenter;
                case "topleft": case "left": return IslandPosition.TopLeft;
                case "topright": case "right": return IslandPosition.TopRight;
                case "free": case "custom": return IslandPosition.Free;
                default: return fallback;
            }
        }

        private static GlassOpacity ParseOpacity(string v, GlassOpacity fallback)
        {
            if (string.IsNullOrEmpty(v)) return fallback;
            switch (v.Trim().ToLowerInvariant())
            {
                case "deep": case "0": return GlassOpacity.Deep;
                case "medium": case "1": return GlassOpacity.Medium;
                case "clear": case "2": return GlassOpacity.Clear;
                default: return fallback;
            }
        }

        private static double ParseDouble(string v, double fallback)
        {
            double d;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return fallback;
            // 拒绝 NaN / Infinity / 离谱坐标，避免一条坏配置把窗口甩到屏幕外
            if (double.IsNaN(d) || double.IsInfinity(d)) return fallback;
            if (d < -20000 || d > 20000) return fallback;
            return d;
        }

        private static string Bool(bool b)
        {
            return b ? "true" : "false";
        }

        private static string PositionText(IslandPosition p)
        {
            switch (p)
            {
                case IslandPosition.TopLeft: return "TopLeft";
                case IslandPosition.TopRight: return "TopRight";
                case IslandPosition.Free: return "Free";
                default: return "TopCenter";
            }
        }

        private static string OpacityText(GlassOpacity o)
        {
            switch (o)
            {
                case GlassOpacity.Medium: return "Medium";
                case GlassOpacity.Clear: return "Clear";
                default: return "Deep";
            }
        }

        /// <summary>给设置页用的人类可读文案。</summary>
        public static string Describe(IslandPosition p)
        {
            switch (p)
            {
                case IslandPosition.TopLeft: return "左上角";
                case IslandPosition.TopRight: return "右上角";
                case IslandPosition.Free: return "自由拖动";
                default: return "顶部居中";
            }
        }

        /// <summary>给设置页用的人类可读文案。</summary>
        public static string Describe(GlassOpacity o)
        {
            switch (o)
            {
                case GlassOpacity.Medium: return "中 · 半透";
                case GlassOpacity.Clear: return "浅 · 清透";
                default: return "深 · 沉稳";
            }
        }

        public static IslandPosition[] AllPositions()
        {
            return new[]
            {
                IslandPosition.Free,
                IslandPosition.TopCenter,
                IslandPosition.TopLeft,
                IslandPosition.TopRight
            };
        }

        public static GlassOpacity[] AllOpacities()
        {
            return new[]
            {
                GlassOpacity.Deep,
                GlassOpacity.Medium,
                GlassOpacity.Clear
            };
        }
    }
}
