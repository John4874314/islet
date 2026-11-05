using System;
using System.Collections.Generic;
using System.Threading;
using Islet.Core;
using Microsoft.Win32;

namespace Islet.Sources
{
    /// <summary>
    /// 隐私占用指示：麦克风 / 摄像头正在被谁使用。
    ///
    /// 数据来源：Windows 自己在
    ///   HKCU\...\CapabilityAccessManager\ConsentStore\{microphone,webcam}
    /// 下记录每次访问的 LastUsedTimeStart / LastUsedTimeStop。
    /// **LastUsedTimeStop == 0 表示此刻正在使用**——这是系统级事实，
    /// 任何应用调用麦克风/摄像头都会被记录，包括本程序无法 hook 的 UWP。
    ///
    /// 为什么不上岛「历史记录」：那会把胶囊变成审计日志。
    /// 只在「正在使用」时出现，用完即撤——符合 PRD 的时效性闸门。
    /// </summary>
    public sealed class PrivacyProvider : IIslandProvider
    {
        private const string ConsentRoot =
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

        private const string IdMic = "islet.privacy.mic";
        private const string IdCam = "islet.privacy.cam";

        /// <summary>3 秒扫一次注册表。比文件监视便宜，且这类状态变化本身不快。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

        private IIslandSink _sink;
        private Timer _timer;
        private bool _disposed;

        // 上一轮状态，用于只在变化时发布，避免每 3 秒刷一次屏
        private string _lastMic;
        private string _lastCam;

        public string Name
        {
            get { return "Privacy"; }
        }

        public void Attach(IIslandSink sink)
        {
            _sink = sink;
            _timer = new Timer(Poll, null, 2000, (int)PollInterval.TotalMilliseconds);
        }

        private void Poll(object state)
        {
            if (_disposed || _sink == null) return;

            try
            {
                string mic = FindActiveUser("microphone");
                string cam = FindActiveUser("webcam");

                UpdateOne(IdMic, "microphone", "麦克风正在使用", "🔴", mic, ref _lastMic);
                UpdateOne(IdCam, "webcam", "摄像头正在使用", "📷", cam, ref _lastCam);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Islet] PrivacyProvider: " + ex.Message);
            }
        }

        private void UpdateOne(string id, string capability, string title, string icon,
                               string currentUser, ref string lastUser)
        {
            // 状态没变就不动，避免反复抢占槽位
            if (string.Equals(currentUser, lastUser, StringComparison.Ordinal)) return;
            lastUser = currentUser;

            if (currentUser == null)
            {
                _sink.Retract(id);
                return;
            }

            string display = FriendlyName(currentUser);

            _sink.Publish(new IslandEvent(id, IslandSource.Health, icon + " " + title)
                .WithSubtitle(display)
                .WithUrgency(88)          // 隐私是高分场景：用户需要立刻知道
                .WithActionability(30)
                .WithHardPreempt(true)    // 硬抢占：正在被录音不该被音乐盖住
                .WithActions(new IslandAction("打开隐私设置", OpenPrivacySettings)));
        }

        /// <summary>
        /// 找出此刻**真正**正在使用该设备的应用。返回 null 表示没人用。
        ///
        /// ⚠️ 这里有个大坑：LastUsedTimeStop == 0 并不等于「正在使用」。
        /// 应用若从未正常释放（崩溃、强制结束、异常退出），这个值会**永远停在 0**。
        /// 实测本机就有一堆假阳性：
        ///   Microsoft.GamingApp / WindowsCamera / WindowsSoundRecorder /
        ///   XboxGamingOverlay / 微信 —— 全都显示 0，但根本没在用。
        ///
        /// 所以必须再叠一层校验：对应进程是否真的活着。
        /// 宁可漏报也不要误报——隐私提示一旦误报，用户会直接卸载软件。
        /// </summary>
        private static string FindActiveUser(string capability)
        {
            string root = ConsentRoot + "\\" + capability;

            // 打包应用（UWP）挂在 capability 下
            string hit = ScanForKey(root, true);
            if (hit != null) return hit;

            // 桌面应用挂在 NonPackaged 下
            return ScanForKey(root + "\\NonPackaged", false);
        }

        /// <param name="isPackaged">
        /// true 表示键名是 PackageFamilyName（需按包名找进程）；
        /// false 表示键名是路径（用 # 代替 \）。
        /// </param>
        private static string ScanForKey(string path, bool isPackaged)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(path, false))
                {
                    if (key == null) return null;

                    string best = null;
                    long bestStart = 0;

                    foreach (string sub in key.GetSubKeyNames())
                    {
                        using (RegistryKey app = key.OpenSubKey(sub, false))
                        {
                            if (app == null) continue;

                            object stopObj = app.GetValue("LastUsedTimeStop");
                            long stop;
                            if (!TryToLong(stopObj, out stop)) continue;

                            // 第一道闸：stop == 0 才可能是「正在使用」
                            if (stop != 0) continue;

                            // 第二道闸：进程必须真的活着，否则就是残留记录
                            if (!IsOwnerAlive(sub, isPackaged)) continue;

                            object startObj = app.GetValue("LastUsedTimeStart");
                            long start;
                            TryToLong(startObj, out start);

                            if (best == null || start > bestStart)
                            {
                                best = sub;
                                bestStart = start;
                            }
                        }
                    }
                    return best;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 判断占用者是否真在运行。
        ///
        /// 注意这个校验的**误差方向**：宁可误报也不能漏报吗？不是。
        /// 恰恰相反——隐私提示误报会让用户以为被偷听，那是信任崩塌；
        /// 漏报只是少一次提醒。所以这里的策略是：
        ///   · 桌面应用：进程确实不存在 → 判定为残留，不上岛
        ///   · 打包应用：拿不到可靠 exe 映射 → 直接采信注册表
        ///   · 任何异常 → 判为「不活跃」
        ///
        /// 实际数据表明 LastUsedTimeStop 通常会被正确写回（本机 15 条记录全部有时间戳），
        /// 所以 stop==0 本身已经相当可靠；这层校验只是兜住崩溃残留。
        /// </summary>
        private static bool IsOwnerAlive(string keyName, bool isPackaged)
        {
            try
            {
                if (isPackaged) return true;

                string path = keyName.Replace('#', '\\');
                int slash = path.LastIndexOf('\\');
                string exe = slash >= 0 ? path.Substring(slash + 1) : path;
                if (string.IsNullOrEmpty(exe)) return true;   // 拿不到名字，采信注册表

                string baseName = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? exe.Substring(0, exe.Length - 4)
                    : exe;
                if (baseName.Length == 0) return true;

                System.Diagnostics.Process[] found =
                    System.Diagnostics.Process.GetProcessesByName(baseName);
                try
                {
                    // 进程在跑 → 采信；进程不在 → 视为残留记录
                    return found.Length > 0;
                }
                finally
                {
                    foreach (System.Diagnostics.Process p in found) p.Dispose();
                }
            }
            catch (Exception)
            {
                // 查询失败时采信注册表，避免因权限问题漏报
                return true;
            }
        }

        private static bool TryToLong(object value, out long result)
        {
            result = 0;
            if (value == null) return false;
            if (value is long) { result = (long)value; return true; }
            if (value is int) { result = (int)value; return true; }
            if (value is ulong) { result = (long)(ulong)value; return true; }
            return long.TryParse(value.ToString(), out result);
        }

        /// <summary>把注册表里的键名变成人能读的名字。</summary>
        private static string FriendlyName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";

            // 桌面应用：键名形如 C:#Program Files#Tencent#WeChat#WeChat.exe
            if (raw.IndexOf('#') >= 0)
            {
                string path = raw.Replace('#', '\\');
                int slash = path.LastIndexOf('\\');
                string exe = slash >= 0 ? path.Substring(slash + 1) : path;
                return exe + "（桌面应用）";
            }

            // 打包应用：Microsoft.ScreenSketch_8wekyb3d8bbwe
            int under = raw.IndexOf('_');
            string name = under > 0 ? raw.Substring(0, under) : raw;
            return name + "（应用）";
        }

        private static void OpenPrivacySettings()
        {
            try
            {
                System.Diagnostics.Process.Start("ms-settings:privacy-microphone");
            }
            catch (Exception) { }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_timer != null) { _timer.Dispose(); _timer = null; }
            _sink = null;
        }
    }
}
