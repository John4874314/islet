using System;
using System.Collections.Generic;

namespace Islet.Sources
{
    /// <summary>
    /// 「谁在出声」的判据（不依赖 SMTC）。
    ///
    /// 为什么需要它：
    ///   实测本机网易云音乐**完全不向 Windows 注册 SMTC** ——
    ///   正在播放时 GlobalSystemMediaTransportControlsSessionManager
    ///   里一个新会话都没有（GetCurrentSession() 返回 null）。
    ///   所以只靠 MediaProvider 的 SMTC 路径，网易云永远是「没反应」。
    ///
    /// 改用 WASAPI 音频会话的 State：
    ///   State == 1（Active）表示该会话此刻真的在出声。
    ///   实测网易云播放时 8 次采样全部稳定为 Active，暂停后立刻回到 Inactive。
    ///
    /// 这里只保留**判据**，不再实现 IIslandProvider：
    ///   岛上那行「正在播放」由 MediaProvider 的回落分支发布（同一个 IslandSource.Media
    ///   槽位），展开后的滑块由 IslandWindow 渲染。原先这里另起一个 Provider 是多余的一层，
    ///   而且从未被注册，属于死代码。
    /// </summary>
    internal static class NowPlayingProvider
    {
        /// <summary>
        /// 复用的枚举器实例。
        ///
        /// 为什么不能每次 new 一个：AudioSessionProvider 每次 Refresh 都会
        /// device.Activate 出一个新的 IAudioSessionManager2。虽然 Dispose 会
        /// ReleaseComObject，但**音频引擎侧的会话对象不会立刻回收**——
        /// 实测「每 2 秒 new+Dispose 一次」会让系统会话数每秒稳定 +1，
        /// 几分钟就攒出上千个僵尸会话，把音量面板本身也拖慢。
        /// 长期复用同一个实例（它内部复用 _manager）就没有这个问题。
        /// </summary>
        private static readonly AudioSessionProvider Shared = new AudioSessionProvider();
        private static readonly object SharedGate = new object();

        /// <summary>
        /// 诊断用：返回**全部**会话（不按 State 过滤）。
        ///
        /// 仅供 MediaProvider 写诊断日志，不要用在业务判断里 ——
        /// 它会把暂停中的会话也返回。
        /// </summary>
        internal static List<AudioSession> AllForDiagnostics()
        {
            List<AudioSession> result = new List<AudioSession>();
            lock (SharedGate)
            {
                if (!Shared.Refresh()) return result;
                foreach (AudioSession s in Shared.Sessions)
                {
                    if (string.Equals(s.DisplayName, "Islet",
                                      StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(s);
                }
            }
            return result;
        }

        /// <summary>当前真正在出声的应用（已排序、已排除本程序自己）。</summary>
        internal static List<AudioSession> FindPlaying()
        {
            List<AudioSession> result = new List<AudioSession>();

            lock (SharedGate)
            {
                if (!Shared.Refresh())
                {
                    // 只在失败时记录。成功路径一行不写 —— 正常轮询不该产生任何 I/O。
                    MediaDiag.Event("FindPlaying: Refresh 失败 err="
                        + (Shared.LastError ?? "(null)"));
                    return result;
                }

                foreach (AudioSession s in Shared.Sessions)
                {
                    // ---- 判据：峰值，不是 State ----
                    //
                    // ⚠️ 这里曾经是 `if (s.State != 1) continue;`，导致
                    // 「暂停后要等二十多秒岛才变回小球」。原因：
                    //
                    //   网易云暂停时**不关音频流**，只是不再往里写数据。
                    //   音频引擎认为流还活着，于是 State 继续报 1(Active)。
                    //   2026-11-02 实测（islet/tools/PeakProbe）：
                    //
                    //     18:35:27.018  State=1  Peak=0.000   ← 暂停，峰值立刻归零
                    //     18:35:46.837  State=0  Peak=0.000   ← 19.8 秒后 State 才跟上
                    //
                    //   峰值是引擎实时算的，没数据就是 0，所以它才是「是否在出声」
                    //   的可靠判据。
                    //
                    // 阈值取 0.0005：实测播放时峰值在 0.02~0.53 之间波动，
                    // 暂停后稳定为 0.000（float 精确 0）。取这个值既能盖住
                    // 极安静的段落（不会误判成停了），又不会被浮点噪声骗到。
                    //
                    // 留 State 作为**辅助**条件：State==2 表示会话已失效，
                    // 那种情况即使峰值残留也不该继续挂着。
                    if (s.State == 2) continue;        // 2 = Expired，会话已失效
                    if (s.Peak <= 0.0005f) continue;   // 没在出声

                    if (!s.CanControl) continue;

                    // 自己不出现在自己的列表里
                    if (string.Equals(s.DisplayName, "Islet",
                                      StringComparison.OrdinalIgnoreCase)) continue;

                    result.Add(s);
                }
            }

            // 排序：按名字，保证顺序稳定（不按音量/活跃度，避免抖动）
            result.Sort(delegate(AudioSession a, AudioSession b)
            {
                int c = string.Compare(a.DisplayName, b.DisplayName,
                                       StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
                return string.CompareOrdinal(a.Key ?? "", b.Key ?? "");
            });

            return result;
        }
    }
}
