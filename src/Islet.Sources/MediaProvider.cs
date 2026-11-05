using System;
using System.Collections.Generic;
using System.Threading;
using Islet.Core;

namespace Islet.Sources
{
    /// <summary>
    /// 媒体源。通过 SMTC 接管系统内任意播放器（Spotify / 浏览器 / 本地播放器）。
    /// 这是产品的「地板态」：无其他事件时它占槽，保证胶囊始终有用。
    /// </summary>
    public sealed class MediaProvider : IIslandProvider
    {
        private const string EventId = "islet.media";

        private IIslandSink _sink;
        private Timer _timer;
        private object _manager;
        private bool _disposed;

        /// <summary>Poll 的互斥标记：定时器与设备切换回调共用，只让一个进。</summary>
        private int _polling;

        // ---- 音频会话回落 ----
        // 网易云等播放器不向 Windows 注册 SMTC（实测播放中 SMTC 会话数为 0），
        // 只靠 SMTC 会导致「正在播放但岛上没反应」。SMTC 拿不到时改看 WASAPI
        // 音频会话的 State==1（Active，确实在出声），至少能显示「谁在出声」。
        // 拿不到歌名/歌手——那些只有 SMTC 才有，这是明确的取舍。
        // 停止播放后，媒体条还要挂多久才撤掉。
        //
        // 这个延迟是为了避免「拖动进度条 / 切歌」时音频会话短暂掉出 Active
        // 造成媒体条闪一下。原来给 3 秒，但加上轮询延迟后用户会觉得「停了半天还在」，
        // 所以压到 1.2 秒 —— 足够盖住瞬时抖动，又不会明显拖慢停止的反馈。
        // 停止播放后，媒体条还要挂多久才撤掉。
        //
        // 历史：3s → 1.2s → 0.45s。
        //
        // 这次能压到 0.45s，是因为判据换成了**峰值**（见 NowPlayingProvider）。
        // 峰值在暂停瞬间就归零，不再有「State 滞后 20 秒」的问题，
        // 所以这段延迟纯粹是防抖动，不需要长。
        //
        // 实测（islet/tools/PeakProbe）：暂停后峰值立刻从 ~0.09 掉到 0.000，
        // 切歌/拖进度条的瞬时静音通常不到 200ms，0.45s 足够盖住。
        private static readonly TimeSpan FallbackHold = TimeSpan.FromMilliseconds(450);
        private string _fallbackSignature;
        private DateTime _fallbackActiveAt = DateTime.MinValue;

        public string Name
        {
            get { return "Media"; }
        }

        public void Attach(IIslandSink sink)
        {
            _sink = sink;

            // SMTC 不可用时**不再直接放弃**：网易云这类不注册 SMTC 的播放器
            // 仍然可以从音频会话看到「谁在出声」。所以这里只跳过 SMTC 轮询，
            // 让 Poll 走回落分支。
            if (!SmtcBridge.IsAvailable)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Islet] SMTC 不可用，改用音频会话回落");
            }

            // 轮询间隔：500ms。
            //
            // 历史：30s → 2s → 500ms。
            //
            // 30s 时实测「2 秒轮询一小时攒近两千个僵尸音频会话」，但那条结论的前提
            // 是**每次 new 一个 AudioSessionProvider**（每次都 device.Activate
            // 出一个新 manager，引擎侧才会不断留下对象）。现在 provider 复用
            // _manager / _enumerator，前提已不成立。
            //
            // 2026-11-01 实测（islet/tools/PollCost）：
            //     同一个 provider 连续 Refresh 600 次
            //     单次平均 1.9ms
            //     系统会话总数 4 → 4，增量 0
            //
            // 500ms 一次 = 每秒 2 次 × 1.9ms ≈ 0.4% 单核占用，可以忽略。
            // 而它买到的是「按暂停后 0.5 秒内岛就收起来」。
            //
            // **改 provider 生命周期策略后必须重跑 PollCost。**
            _timer = new Timer(Poll, null, 300, 500);

            // 插拔耳机 / 切换输出时立刻刷一次，不用等下一个 30 秒。
            // 注册动作在 AudioSessionProvider 创建枚举器时完成
            // （coclass 只在那侧定义一份，见 AudioDeviceWatcher 的类注释），
            // 这里只订阅事件。
            AudioDeviceWatcher.DefaultDeviceChanged += OnAudioDeviceChanged;
        }

        /// <summary>
        /// 设备切换回调。跑在系统线程上，不能直接碰 UI；
        /// Poll 本身只调 sink.Offer，那条路径是线程安全的。
        /// </summary>
        private void OnAudioDeviceChanged()
        {
            if (_disposed) return;
            // 设备切换是低频事件，值得记一笔：以后「插耳机后岛没反应」可以直接
            // 从日志看出是根本没收到通知，还是收到了但下游没出东西。
            MediaDiag.Event("音频设备已切换，立即刷新媒体状态");
            Poll(null);
        }

        private void Poll(object state)
        {
            if (_disposed || _sink == null) return;

            // 定时器和设备切换回调可能同时进来（回调在系统线程上）。
            // 两个线程同时枚举会话会白白多造一批引擎侧会话，所以只让一个进。
            if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
            try
            {
                if (!SmtcBridge.IsAvailable)
                {
                    PollFallback();
                    return;
                }

                object mgr = _manager;
                if (mgr == null)
                {
                    mgr = SmtcBridge.RequestManager(3000);
                    if (mgr == null)
                    {
                        // SMTC 管理器拿不到（部分机器/部分播放器）→ 回落
                        PollFallback();
                        return;
                    }
                    _manager = mgr;
                }

                object session = Invoke(mgr, "GetCurrentSession");
                if (session == null)
                {
                    // 关键分支：SMTC 里没有会话。网易云正在播放时就是这种情况。
                    // 不要直接 Retract —— 先看音频会话里有没有人真的在出声。
                    PollFallback();
                    return;
                }

                // SMTC 有会话 → 说明走的是正常路径，清掉回落状态，
                // 免得后面 SMTC 断掉时还挂着一条过期的回落签名。
                _fallbackSignature = null;

                string appId = SmtcBridge.Str(session, "SourceAppUserModelId");
                object info = Invoke(session, "GetPlaybackInfo");
                string status = info == null ? "" : SmtcBridge.Str(info, "PlaybackStatus");

                // ---- 暂停/停止必须**立刻**撤条 ----
                //
                // 这是「暂停后要等二十多秒才变回小球」的根因：
                // 旧代码完全不看 PlaybackStatus，只要 SMTC 里还有会话就发布
                // 「正在播放」。而网易云暂停后会话还要存在二十多秒才被注销，
                // 于是岛就一直挂着 —— 用户看到的就是「停了半天还在」。
                //
                // PlaybackStatus 的取值（Windows.Media.Control）：
                //   Closed / Opened / Changing / Stopped / Playing / Paused
                // 只有 Playing 才算「在放」。Paused / Stopped / Closed 一律撤条。
                //
                // 注意这里**不做跳跃过滤**：用户按暂停就该马上看到岛收起来，
                // 不需要像 WASAPI 回落那样留 FallbackHold 防抖
                // —— SMTC 的状态是应用主动汇报的，比音频会话可靠得多。
                if (!IsPlaying(status))
                {
                    _fallbackSignature = null;
                    _sink.Retract(EventId);
                    return;
                }

                string title = null;
                string artist = null;
                try
                {
                    object propsOp = Invoke(session, "TryGetMediaPropertiesAsync");
                    if (propsOp != null)
                    {
                        Type opType = propsOp.GetType();
                        // 反射拿不到泛型形参时，用方法签名反查
                        System.Reflection.MethodInfo mi =
                            session.GetType().GetMethod("TryGetMediaPropertiesAsync");
                        object props = SmtcBridge.Await(mi.ReturnType, propsOp, 2000);
                        title = SmtcBridge.Str(props, "Title");
                        artist = SmtcBridge.Str(props, "Artist");
                    }
                }
                catch (Exception) { /* 封面元数据失败不影响主流程 */ }

                if (string.IsNullOrEmpty(title)) title = FriendlyApp(appId);
                if (string.IsNullOrEmpty(title))
                {
                    _sink.Retract(EventId);
                    return;
                }

                string subtitle = string.IsNullOrEmpty(artist) ? status : artist;

                IslandEvent evt = new IslandEvent(EventId, IslandSource.Media, title)
                    .WithSubtitle(subtitle)
                    .WithUrgency(5)
                    .WithActionability(90)
                    .WithActions(
                        new IslandAction("⏮", () => TrySend(session, "SkipPrevious")),
                        new IslandAction(IsPlaying(status) ? "⏸" : "▶",
                                         () => TryToggle(session)),
                        new IslandAction("⏭", () => TrySend(session, "SkipNext")));

                _sink.Publish(evt);
            }
            catch (Exception ex)
            {
                // 异常一律记录：这一层静默失败过太多次（2026-10-30 的 coclass 冲突
                // 就是被 catch 吞掉，导致媒体条三个小时不显示）。
                MediaDiag.Event("Poll 异常: " + ex.GetType().Name + " " + ex.Message);
                System.Diagnostics.Debug.WriteLine("[Islet] MediaProvider: " + ex.Message);
                _manager = null; // 管理器失效则重建
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
            }
        }

        /// <summary>
        /// SMTC 拿不到时的回落：直接看 WASAPI 音频会话里谁在出声。
        ///
        /// 取舍写在前面：这条路**只能**给出「哪个应用在出声」，
        /// 拿不到歌名、歌手、封面，也没有上一首/下一首/暂停这些动作
        /// —— 那些都只有 SMTC 才提供。所以这里故意不给动作按钮，
        /// 只给一句「点开调音量」，把用户导向岛内混音器。
        /// </summary>
        private void PollFallback()
        {
            List<AudioSession> playing = NowPlayingProvider.FindPlaying();

            // 诊断：把「这次看到的会话状态」落盘。
            //
            // 为什么需要它：用户反馈「暂停后要 20 多秒才变小球」，而按代码逻辑
            // FallbackHold 只有 1.2 秒，本该立刻撤。中间一定有某个假设是错的，
            // 与其继续猜，不如把每次决策的原始输入写下来。
            //
            // 只在「判定结果发生变化」时写，正常运行不会刷屏。
            string probe = DiagnoseSessions();

            if (playing.Count == 0)
            {
                // 刚停下的几秒内先不撤，避免拖动进度条/切歌时闪一下
                if (_fallbackSignature != null &&
                    (DateTime.UtcNow - _fallbackActiveAt) > FallbackHold)
                {
                    if (Diag && _lastDiag != "RETRACT")
                    {
                        MediaDiag.Event("【诊断】撤条 | " + probe);
                        _lastDiag = "RETRACT";
                    }
                    _sink.Retract(EventId);
                    _fallbackSignature = null;
                }
                return;
            }

            _fallbackActiveAt = DateTime.UtcNow;

            string sig = BuildFallbackSignature(playing);
            if (string.Equals(sig, _fallbackSignature, StringComparison.Ordinal)) return;
            _fallbackSignature = sig;

            string diag = "播放中 | " + probe;
            if (Diag && _lastDiag != diag)
            {
                MediaDiag.Event("【诊断】" + diag);
                _lastDiag = diag;
            }

            MediaDiag.Event("发布媒体事件: " + sig);

            AudioSession first = playing[0];
            string title = playing.Count == 1
                ? "🎵 " + first.DisplayName
                : "🎵 " + first.DisplayName + " 等 " + playing.Count + " 个应用";

            _sink.Publish(new IslandEvent(EventId, IslandSource.Media, title)
                .WithSubtitle("正在播放 · 点开调音量")
                .WithUrgency(5)
                .WithActionability(90));
        }

        /// <summary>
        /// 诊断开关。默认关闭，日志里不留噪音。
        ///
        /// 2026-11-02 排查「暂停后二十多秒才撤条」时打开过，结论已经写进
        /// NowPlayingProvider 的注释（判据要从 State 换成 Peak），
        /// 以后遇到同类问题把这里改成 true 就能复现那几行日志。
        ///
        /// ⚠️ 打开时每轮会额外跑一次完整 Refresh（见 DiagnoseSessions），
        /// 开销翻倍，**只适合临时排查**。
        /// </summary>
        private const bool Diag = false;

        /// <summary>
        /// 诊断：把此刻所有音频会话的 State 拼成一行。
        /// 只在状态变化时由调用方写日志。
        /// </summary>
        private static string DiagnoseSessions()
        {
            if (!Diag) return null;
            try
            {
                List<AudioSession> all = NowPlayingProvider.AllForDiagnostics();
                if (all.Count == 0) return "无关会话";
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                foreach (AudioSession s in all)
                {
                    if (sb.Length > 0) sb.Append(" , ");
                    sb.Append(s.DisplayName).Append("=").Append(s.State);
                }
                return all.Count + " 个: " + sb;
            }
            catch (Exception ex)
            {
                return "诊断失败 " + ex.GetType().Name;
            }
        }

        /// <summary>上一次写进日志的诊断结论，用来避免刷屏。</summary>
        private string _lastDiag;

        private static string BuildFallbackSignature(List<AudioSession> list)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (AudioSession s in list)
            {
                if (sb.Length > 0) sb.Append(';');
                sb.Append(s.DisplayName);
            }
            return sb.ToString();
        }

        private static bool IsPlaying(string status)
        {
            return string.Equals(status, "Playing", StringComparison.OrdinalIgnoreCase);
        }

        private static void TryToggle(object session)
        {
            try
            {
                object info = Invoke(session, "GetPlaybackInfo");
                string status = info == null ? "" : SmtcBridge.Str(info, "PlaybackStatus");
                Invoke(session, IsPlaying(status) ? "TryPauseAsync" : "TryPlayAsync");
            }
            catch (Exception) { }
        }

        private static void TrySend(object session, string method)
        {
            try { Invoke(session, method + "Async"); }
            catch (Exception) { }
        }

        private static object Invoke(object target, string method)
        {
            if (target == null) return null;
            System.Reflection.MethodInfo mi = target.GetType().GetMethod(method);
            if (mi == null) return null;
            return mi.Invoke(target, null);
        }

        private static string FriendlyApp(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return null;
            if (appId.IndexOf("chrome", StringComparison.OrdinalIgnoreCase) >= 0) return "Chrome";
            if (appId.IndexOf("msedge", StringComparison.OrdinalIgnoreCase) >= 0) return "Edge";
            if (appId.IndexOf("firefox", StringComparison.OrdinalIgnoreCase) >= 0) return "Firefox";
            if (appId.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0) return "Spotify";
            if (appId.IndexOf("cloudmusic", StringComparison.OrdinalIgnoreCase) >= 0) return "网易云音乐";
            if (appId.IndexOf("qqmusic", StringComparison.OrdinalIgnoreCase) >= 0) return "QQ音乐";
            return null;
        }

        public void Dispose()
        {
            _disposed = true;

            // 一定要退订：回调来自系统，订阅方被回收后还继续回调会很难查。
            try { AudioDeviceWatcher.DefaultDeviceChanged -= OnAudioDeviceChanged; }
            catch (Exception) { }

            if (_timer != null)
            {
                _timer.Dispose();
                _timer = null;
            }
            _manager = null;
            _sink = null;
        }
    }
}
