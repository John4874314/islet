using System;
using System.Text.RegularExpressions;

namespace Islet.Core
{
    /// <summary>
    /// 剪贴板内容识别的纯逻辑：输入一段文本，输出「该不该上岛、上岛显示什么」。
    ///
    /// 抽到 Core 里是为了可单测。**不涉及任何 Win32 / 剪贴板 API**，
    /// 因此可以在没有桌面会话的环境下验证全部规则。
    /// </summary>
    public static class ClipboardRecognizer
    {
        /// <summary>非验证码类内容的最小长度。验证码单独放宽到 4-8 位。</summary>
        public const int MinLength = 8;
        public const int MaxLength = 500;

        /// <summary>验证码长度区间（含端点）。</summary>
        public const int CodeMinLength = 4;
        public const int CodeMaxLength = 8;

        /// <summary>
        /// 识别结果。null 表示这段文本不值得上岛。
        /// </summary>
        public sealed class Result
        {
            public string Kind;        // code / tracking / url / phone / amount
            public string Title;
            public string Subtitle;
            public string Payload;     // 真正要复制/打开的值
            public int Urgency;
            public bool IsUrl;
        }

        public static Result Recognize(string rawText)
        {
            if (rawText == null) return null;

            string text = rawText.Trim();
            if (text.Length > MaxLength) return null;

            // 验证码必须优先于长度门槛：它通常只有 4-6 位，
            // 若统一按 MinLength 过滤就永远上不了岛。
            Result code = RecognizeCode(text);
            if (code != null) return code;

            if (text.Length < MinLength) return null;

            return RecognizeTracking(text)
                ?? RecognizeUrl(text)
                ?? RecognizePhone(text)
                ?? RecognizeAmount(text);
        }

        // 1. 纯数字验证码：整段就是 4-8 位数字
        private static Result RecognizeCode(string text)
        {
            if (text.Length < CodeMinLength || text.Length > CodeMaxLength) return null;
            if (!Regex.IsMatch(text, @"^\d+$")) return null;
            return new Result
            {
                Kind = "code",
                Title = "识别到验证码",
                Subtitle = text + " · 点击复制",
                Payload = text,
                Urgency = 60
            };
        }

        // 2. 快递单号：常见前缀 + 数字，或 12-15 位纯数字
        private static Result RecognizeTracking(string text)
        {
            Match m = Regex.Match(text,
                @"\b((?:SF|YT|ZTO|JD|STO|YTO|EMS|DBL|JT)[0-9]{8,16}|[0-9]{12,15})\b",
                RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            string no = m.Groups[1].Value.ToUpperInvariant();
            return new Result
            {
                Kind = "tracking",
                Title = "识别到快递单号",
                Subtitle = no + " · 点击复制",
                Payload = no,
                Urgency = 45
            };
        }

        // 3. URL：必须以它开头，避免把正文里的链接误当剪贴板主内容
        private static Result RecognizeUrl(string text)
        {
            Match m = Regex.Match(text, @"https?://[^\s""'<>]{6,300}");
            if (!m.Success || m.Index != 0) return null;

            return new Result
            {
                Kind = "url",
                Title = "识别到链接",
                Subtitle = SafeHost(m.Value),
                Payload = m.Value,
                Urgency = 40,
                IsUrl = true
            };
        }

        // 4. 中国手机号，且整段很短（避免长文本里的号码误报）
        private static Result RecognizePhone(string text)
        {
            if (text.Length > 40) return null;
            Match m = Regex.Match(text, @"\b1[3-9]\d{9}\b");
            if (!m.Success) return null;

            return new Result
            {
                Kind = "phone",
                Title = "识别到手机号",
                Subtitle = m.Value + " · 点击复制",
                Payload = m.Value,
                Urgency = 50
            };
        }

        // 5. 金额
        private static Result RecognizeAmount(string text)
        {
            if (text.Length > 60) return null;
            Match m = Regex.Match(text,
                @"([¥￥$]\s?\d+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?\s?元)");
            if (!m.Success) return null;

            string amount = m.Value.Trim();
            return new Result
            {
                Kind = "amount",
                Title = "识别到金额",
                Subtitle = amount,
                Payload = amount,
                Urgency = 35
            };
        }

        private static string SafeHost(string url)
        {
            try { return new Uri(url).Host; }
            catch (Exception) { return "外部链接"; }
        }
    }
}
