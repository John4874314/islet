using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;
using Islet.Core;

namespace Islet.Sources
{
    /// <summary>
    /// 剪贴板内容识别。对应移动端「取餐码上岛」在桌面侧最接近的形态。
    ///
    /// 设计要点（PRD §8 隐私约束）：
    ///   · 全部本地正则匹配，不上传、不落盘、不留历史
    ///   · 只识别「可操作」的内容（验证码、快递单号、链接、手机号、金额）
    ///   · 识别规则在 Islet.Core.ClipboardRecognizer，可脱离桌面单测
    /// </summary>
    public sealed class ClipboardProvider : IIslandProvider
    {
        private const string IdPrefix = "islet.clip.";

        /// <summary>同一类内容重复出现时不重复上岛。</summary>
        private static readonly TimeSpan SameKindCooldown = TimeSpan.FromSeconds(30);

        private readonly Dictionary<string, DateTimeOffset> _recent =
            new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        private IIslandSink _sink;
        private Thread _listener;
        private volatile bool _running;
        private bool _disposed;
        private string _lastFingerprint;

        // ---- 诊断计数（供自检读取，产品态无副作用）----
        public static int DiagClipboardEvents;
        public static int DiagRecognized;
        public static string DiagLastError;
        public static string DiagLastText;

        public string Name
        {
            get { return "Clipboard"; }
        }

        public void Attach(IIslandSink sink)
        {
            _sink = sink;
            _running = true;

            // 独立 STA 线程做监听：AddClipboardFormatListener 需要消息循环，
            // 放在 UI 线程会干扰胶囊的动画帧率。
            _listener = new Thread(Listen)
            {
                IsBackground = true,
                Name = "Islet.ClipboardListener"
            };
            _listener.SetApartmentState(ApartmentState.STA);
            _listener.Start();
        }

        private void Listen()
        {
            ClipboardWatcher watcher = new ClipboardWatcher();
            watcher.Changed += () =>
            {
                try
                {
                    System.Threading.Interlocked.Increment(ref DiagClipboardEvents);
                    OnClipboardChanged();
                }
                catch (Exception ex)
                {
                    DiagLastError = ex.GetType().Name + ": " + ex.Message;
                    System.Diagnostics.Debug.WriteLine("[Islet] Clipboard: " + ex.Message);
                }
            };

            try { watcher.Run(() => _running); }
            catch (Exception ex)
            {
                DiagLastError = "listener: " + ex.GetType().Name + ": " + ex.Message;
                System.Diagnostics.Debug.WriteLine("[Islet] Clipboard listener: " + ex.Message);
            }
            finally { watcher.Dispose(); }
        }

        private void OnClipboardChanged()
        {
            if (_disposed || _sink == null) return;

            string text = SafeGetText();
            if (string.IsNullOrEmpty(text))
            {
                DiagLastText = "(null)";
                return;
            }
            DiagLastText = text;

            text = text.Trim();

            // 注意：这里不能再按 MinLength 过滤——验证码只有 4-6 位，
            // 长度约束已由 ClipboardRecognizer 内部按类别处理。
            string fingerprint = text.GetHashCode().ToString("X8") + ":" + text.Length;
            if (string.Equals(fingerprint, _lastFingerprint, StringComparison.Ordinal))
            {
                DiagLastError = "duplicate";
                return;
            }
            _lastFingerprint = fingerprint;

            DateTimeOffset now = DateTimeOffset.Now;
            PruneRecent(now);

            ClipboardRecognizer.Result match = ClipboardRecognizer.Recognize(text);
            if (match == null)
            {
                DiagLastError = "no-match";
                return;
            }
            DiagLastError = null;
            System.Threading.Interlocked.Increment(ref DiagRecognized);

            if (_recent.ContainsKey(match.Kind))
            {
                DiagLastError = "kind-cooldown:" + match.Kind;
                return;
            }
            _recent[match.Kind] = now;

            string id = IdPrefix + match.Kind;
            string captured = match.Payload;

            IslandEvent evt = new IslandEvent(id, IslandSource.Clipboard, match.Title)
                .WithSubtitle(match.Subtitle)
                .WithUrgency(match.Urgency)
                .WithActionability(95)
                .WithTtl(TimeSpan.FromSeconds(12));

            IslandAction action;
            if (match.IsUrl)
            {
                string link = captured;
                action = new IslandAction("打开链接", () =>
                {
                    try { System.Diagnostics.Process.Start(link); }
                    catch (Exception) { }
                });
            }
            else
            {
                string copyValue = captured;
                action = new IslandAction("复制", () =>
                {
                    try { Clipboard.SetText(copyValue); }
                    catch (Exception) { }
                });
            }

            evt = evt.WithActions(action);
            _sink.Publish(evt);
        }

        private void PruneRecent(DateTimeOffset now)
        {
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, DateTimeOffset> kv in _recent)
            {
                if (now - kv.Value > SameKindCooldown) stale.Add(kv.Key);
            }
            foreach (string k in stale) _recent.Remove(k);
        }

        private static string SafeGetText()
        {
            // 剪贴板可能被其他进程占用，重试三次
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (Clipboard.ContainsText()) return Clipboard.GetText();
                    return null;
                }
                catch (Exception)
                {
                    Thread.Sleep(60);
                }
            }
            return null;
        }

        public void Dispose()
        {
            _disposed = true;
            _running = false;
            _sink = null;
            _recent.Clear();
        }
    }
}
