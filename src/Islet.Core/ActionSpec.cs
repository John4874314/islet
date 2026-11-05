using System;
using System.Collections.Generic;

namespace Islet.Core
{
    /// <summary>
    /// 动作类型。Core 层只描述「要做什么」，不负责执行——
    /// 执行留给 App 层，这样规则可以脱离桌面单测。
    /// </summary>
    public enum ActionKind
    {
        /// <summary>把 Argument 写进剪贴板。</summary>
        Copy = 0,

        /// <summary>用默认浏览器打开 Argument（必须是 http/https）。</summary>
        OpenUrl = 1,

        /// <summary>交给 shell 打开 Argument（如 mailto:、tel:、bingmaps:）。</summary>
        OpenShell = 2
    }

    /// <summary>
    /// 一条可执行的动作。纯数据，可单测。
    /// </summary>
    public sealed class ActionSpec
    {
        public ActionSpec(string label, ActionKind kind, string argument)
        {
            if (label == null) throw new ArgumentNullException("label");
            Label = label;
            Kind = kind;
            Argument = argument ?? "";
        }

        public string Label { get; private set; }

        public ActionKind Kind { get; private set; }

        public string Argument { get; private set; }

        public override string ToString()
        {
            return Label + "(" + Kind + ")";
        }
    }

    /// <summary>
    /// 危险动作的白名单校验。
    ///
    /// 剪贴板内容来自外部，**绝不能**直接当成命令执行。
    /// 所有 Open* 动作在真正执行前都必须过这一关：
    ///   · 只允许明确列出的协议
    ///   · 拒绝 file:、javascript:、data: 等可执行/注入类协议
    ///   · 拒绝命令行参数拼接（不能让内容逃逸成 shell 命令）
    /// </summary>
    public static class ActionGuard
    {
        private static readonly string[] AllowedSchemes =
        {
            "http", "https", "mailto", "tel", "bingmaps", "ms-settings"
        };

        /// <summary>这个 URI 是否允许交给系统打开。</summary>
        public static bool IsSafeUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return false;

            // 长度限制：防止超长字符串撑爆 shell
            if (uri.Length > 2048) return false;

            // 不允许换行/控制字符：这些可以用来注入额外的 shell 参数
            foreach (char c in uri)
            {
                if (c < 0x20 || c == 0x7F) return false;
            }

            int colon = uri.IndexOf(':');
            if (colon <= 0) return false;

            string scheme = uri.Substring(0, colon).ToLowerInvariant();

            foreach (string allowed in AllowedSchemes)
            {
                if (scheme == allowed) return true;
            }
            return false;
        }

        /// <summary>
        /// 把任意文本塞进 mailto:/tel: 这类模板时，做最小化转义。
        /// 只编码会破坏 URI 结构的字符。
        /// </summary>
        public static string EscapeForUri(string raw)
        {
            if (raw == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(raw.Length + 16);
            foreach (char c in raw)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '@')
                    sb.Append(c);
                else
                    sb.Append(Uri.EscapeDataString(c.ToString()));
            }
            return sb.ToString();
        }

        /// <summary>从一段文本里挑出最像「地址」的片段，供地图搜索。</summary>
        public static bool LooksLikeAddress(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Length < 6 || text.Length > 120) return false;

            // 必须含行政区划关键词，且不能是纯 URL
            if (text.IndexOf("://") >= 0) return false;

            string[] markers = { "省", "市", "区", "县", "镇", "路", "街", "道",
                                 "号", "弄", "巷", "楼", "室", "村", "大道" };
            int hits = 0;
            foreach (string m in markers)
            {
                if (text.IndexOf(m) >= 0) hits++;
            }
            return hits >= 2;
        }
    }
}
