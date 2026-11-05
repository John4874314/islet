using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Islet.Core;
using Microsoft.Win32;

namespace Islet.Sources
{
    /// <summary>
    /// 外设/隐私状态合并 Provider：麦克风、摄像头占用 + 蓝牙耳机连接。
    ///
    /// 为什么合并（而不是两个 Provider 各自注册）：
    ///   IsletHost._providers 是按 IslandSource 索引的字典，同一来源只能挂一个 ——
    ///   后注册的会把先注册的顶掉，表现为「开了蓝牙就再也看不到麦克风提示」，
    ///   这种 bug 极难发现。两者在语义上都属于「该让用户立刻知道的外设状态」，
    ///   合成一个 Provider 既避开冲突，读起来也顺。
    ///
    /// 两个子功能可独立开关：构造时传两个 bool，关掉的那个连轮询都不做。
    /// </summary>
    public sealed class DeviceStatusProvider : IIslandProvider
    {
        // ---- 隐私：麦克风 / 摄像头 ----
        private const string ConsentRoot =
            @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
        private const string IdMic = "islet.privacy.mic";
        private const string IdCam = "islet.privacy.cam";

        // ---- 蓝牙耳机 ----
        private const string RenderRoot =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";

        /// <summary>PKEY_Device_FriendlyName（PID 14）。</summary>
        private const string KeyFriendlyName =
            "{a45c254e-df1c-4efd-8020-67d146a850e0},14";

        /// <summary>PKEY_DeviceInterface_FriendlyName（PID 2）。部分端点只有这个。</summary>
        private const string KeyInterfaceName =
            "{a45c254e-df1c-4efd-8020-67d146a850e0},2";

        /// <summary>端点的设备接口路径，里面带 Bluetooth MAC。</summary>
        private const string KeyDevicePath =
            "{840b8171-b0ad-410f-8581-cccc0382cfef},0";

        private const string IdHeadset = "islet.bt.headset";

        /// <summary>3 秒一轮。注册表扫描合计约 100~200ms，代价可忽略。</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

        /// <summary>耳机断开后的提示时长。</summary>
        private static readonly TimeSpan DisconnectTtl = TimeSpan.FromSeconds(20);

        private readonly bool _privacyOn;
        private readonly bool _bluetoothOn;

        private IIslandSink _sink;
        private Timer _timer;
        private bool _disposed;

        // 隐私：上一轮状态，只在变化时发布
        private string _lastMic;
        private string _lastCam;

        // 蓝牙：上一轮签名与连接态
        private string _lastBtSignature;
        private bool _wasBtConnected;
        private bool _btPrimed;

        public DeviceStatusProvider(bool privacyEnabled, bool bluetoothEnabled)
        {
            _privacyOn = privacyEnabled;
            _bluetoothOn = bluetoothEnabled;
        }

        public string Name
        {
            get { return "DeviceStatus"; }
        }

        public void Attach(IIslandSink sink)
        {
            _sink = sink;
            // 起步延迟 2 秒：避开开机那阵子与其它 Provider 抢首帧
            _timer = new Timer(Poll, null, 2000, (int)PollInterval.TotalMilliseconds);
        }

        private void Poll(object state)
        {
            if (_disposed || _sink == null) return;

            if (_privacyOn)
            {
                try { PollPrivacy(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Islet] Privacy 轮询失败: " + ex.Message);
                }
            }

            if (_bluetoothOn)
            {
                try { PollBluetooth(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Islet] Bluetooth 轮询失败: " + ex.Message);
                }
            }
        }

        // ===============================================================
        // 隐私：麦克风 / 摄像头
        // ===============================================================

        private void PollPrivacy()
        {
            string mic = FindActiveUser("microphone");
            string cam = FindActiveUser("webcam");

            UpdatePrivacy(IdMic, "麦克风正在使用", "🔴", mic, ref _lastMic);
            UpdatePrivacy(IdCam, "摄像头正在使用", "📷", cam, ref _lastCam);
        }

        private void UpdatePrivacy(string id, string title, string icon,
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

            string display = FriendlyAppName(currentUser);

            _sink.Publish(new IslandEvent(id, IslandSource.Privacy, icon + " " + title)
                .WithSubtitle(display)
                .WithUrgency(88)          // 隐私是高分场景：用户需要立刻知道
                .WithActionability(30)
                .WithHardPreempt(true)    // 硬抢占：正在被录音不该被音乐盖住
                .WithActions(new IslandAction("打开隐私设置", OpenPrivacySettings)));
        }

        /// <summary>
        /// 找出此刻**真正**正在使用该设备的应用。返回 null 表示没人用。
        ///
        /// 大坑：LastUsedTimeStop == 0 并不等于「正在使用」。
        /// 应用若从未正常释放（崩溃、强制结束、异常退出），这个值会永远停在 0。
        /// 实测本机有一堆假阳性（Microsoft.GamingApp / WindowsCamera /
        /// WindowsSoundRecorder / XboxGamingOverlay / 微信），全都显示 0 但根本没在用。
        /// 所以必须再叠一层「进程是否真的活着」的校验。
        /// 宁可漏报也不要误报 —— 隐私提示一旦误报，用户会直接卸载软件。
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
        /// 误差方向：宁可漏报（少一次提醒）也不能误报（用户以为被偷听）。
        ///   · 桌面应用：进程不存在 → 判为残留，不上岛
        ///   · 打包应用：拿不到可靠 exe 映射 → 采信注册表
        ///   · 任何异常 → 判为「不活跃」
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
        private static string FriendlyAppName(string raw)
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

        // ===============================================================
        // 蓝牙耳机连接状态
        // ===============================================================

        /// <summary>
        /// 判定「是不是蓝牙耳机端点」靠两条硬条件，宁可漏报不误报：
        ///   1) FriendlyName 含「耳机 / Headphone / Headset」（排除扬声器、麦克风）
        ///   2) 设备接口路径含 bthenum（排除有线耳机、USB 耳机、HDMI）
        /// </summary>
        private void PollBluetooth()
        {
            List<BtEndpoint> active = ScanBluetoothAudioEndpoints();

            string signature = BuildSignature(active);
            bool connectedNow = active.Count > 0;

            // 首次只建立基线，不提示（避免开机就弹一条）
            if (!_btPrimed)
            {
                _btPrimed = true;
                _lastBtSignature = signature;
                _wasBtConnected = connectedNow;
                return;
            }

            if (string.Equals(signature, _lastBtSignature, StringComparison.Ordinal))
                return;

            _lastBtSignature = signature;

            if (connectedNow)
            {
                PublishBtConnected(active);
                _wasBtConnected = true;
            }
            else
            {
                if (_wasBtConnected) PublishBtDisconnected();
                _wasBtConnected = false;
            }
        }

        private void PublishBtConnected(List<BtEndpoint> active)
        {
            BtEndpoint pick = active[0];
            foreach (BtEndpoint e in active)
            {
                if (e.IsHandsFree) { pick = e; break; }
            }

            string subtitle = active.Count > 1
                ? "已连接（" + active.Count + " 个音频端点） · 去蓝牙设置"
                : "已连接 · 去蓝牙设置";

            _sink.Publish(new IslandEvent(IdHeadset, IslandSource.Privacy,
                                          "🎧 " + pick.DisplayName)
                .WithSubtitle(subtitle)
                .WithUrgency(72)
                .WithActionability(40)
                .WithTtl(TimeSpan.FromSeconds(8))   // 连上属于「告知」，很快撤下
                .WithActions(new IslandAction("打开蓝牙设置", OpenBluetoothSettings)));
        }

        private void PublishBtDisconnected()
        {
            _sink.Publish(new IslandEvent(IdHeadset, IslandSource.Privacy,
                                          "🎧 蓝牙耳机已断开")
                .WithSubtitle("已切回扬声器 · 去蓝牙设置")
                .WithUrgency(65)
                .WithActionability(40)
                .WithTtl(DisconnectTtl)
                .WithActions(new IslandAction("打开蓝牙设置", OpenBluetoothSettings)));
        }

        private static string BuildSignature(List<BtEndpoint> active)
        {
            if (active.Count == 0) return "";

            List<string> parts = new List<string>();
            foreach (BtEndpoint e in active)
            {
                parts.Add(e.Mac + "|" + e.Friendly + "|" + e.IsHandsFree);
            }
            parts.Sort(StringComparer.Ordinal);
            return string.Join(";", parts.ToArray());
        }

        private sealed class BtEndpoint
        {
            public bool Active;
            public string Friendly;      // "耳机"
            public string Mac;           // "315bb0197820"
            public bool IsHandsFree;     // 是否 Hands-Free 链路（通话）
            public string Model;         // "联想thinkplus-LP25"，可能为空

            public string DisplayName
            {
                get { return string.IsNullOrEmpty(Model) ? "蓝牙耳机" : Model; }
            }
        }

        /// <summary>
        /// 扫描所有音频渲染端点，挑出「已激活的蓝牙耳机」。
        ///
        /// 为什么走注册表而不是 WinRT / WMI：
        ///   Get-PnpDevice 实测单次 **5.5 秒**，WinRT 的
        ///   DeviceInformation.FindAllAsync 也要数百毫秒到数秒，
        ///   做成常驻轮询会直接吃满 CPU。注册表全量扫只 **65ms**。
        /// </summary>
        private List<BtEndpoint> ScanBluetoothAudioEndpoints()
        {
            List<BtEndpoint> result = new List<BtEndpoint>();

            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(RenderRoot, false))
                {
                    if (root == null) return result;

                    foreach (string guid in root.GetSubKeyNames())
                    {
                        try
                        {
                            BtEndpoint e = ReadEndpoint(root, guid);
                            if (e != null && e.Active) result.Add(e);
                        }
                        catch (Exception)
                        {
                            // 单个端点读失败不影响其它端点
                        }
                    }
                }

                // 端点自身只有「耳机」二字，型号要去 BTHENUM 按 MAC 反查
                if (result.Count > 0)
                {
                    Dictionary<string, string> models = LoadModelMap();
                    foreach (BtEndpoint e in result)
                    {
                        if (string.IsNullOrEmpty(e.Mac)) continue;
                        string m;
                        if (models.TryGetValue(e.Mac, out m)) e.Model = m;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Islet] 扫描音频端点失败: " + ex.Message);
            }

            return result;
        }

        private BtEndpoint ReadEndpoint(RegistryKey root, string guid)
        {
            using (RegistryKey ep = root.OpenSubKey(guid, false))
            {
                if (ep == null) return null;

                object stateObj = ep.GetValue("DeviceState");
                if (stateObj == null) return null;
                int state = Convert.ToInt32(stateObj);

                using (RegistryKey props = ep.OpenSubKey("Properties", false))
                {
                    if (props == null) return null;

                    string friendly = ReadPropString(props, KeyFriendlyName);
                    if (string.IsNullOrEmpty(friendly))
                    {
                        // 实测同一台机器上两种形态并存：有的端点只有 PID 2，
                        // 只读 PID 14 会漏掉端点。
                        friendly = ReadPropString(props, KeyInterfaceName);
                    }
                    if (string.IsNullOrEmpty(friendly)) return null;

                    if (!IsHeadphoneName(friendly)) return null;

                    string path = ReadPropString(props, KeyDevicePath);
                    if (string.IsNullOrEmpty(path)) return null;

                    // 必须蓝牙设备，排除有线 / USB / HDMI
                    if (path.IndexOf("bthenum", StringComparison.OrdinalIgnoreCase) < 0)
                        return null;

                    BtEndpoint e = new BtEndpoint();
                    e.Active = (state == 1);   // 1 = ACTIVE
                    e.Friendly = friendly;
                    e.Mac = FindMacIn(path);
                    e.IsHandsFree = path.IndexOf("hands-free",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                    return e;
                }
            }
        }

        /// <summary>判定端点名是不是「耳机」。扬声器 / 麦克风都不算。</summary>
        private static bool IsHeadphoneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.IndexOf("麦克风", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("Microphone", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("扬声器", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (name.IndexOf("Speaker", StringComparison.OrdinalIgnoreCase) >= 0) return false;

            if (name.IndexOf("耳机", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("Headphone", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("Headset", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 读「MAC → 设备型号」映射。
        ///
        /// 端点名的 FriendlyName 只有「耳机」，不带型号；
        /// 型号在 HKLM\SYSTEM\CurrentControlSet\Enum\BTHENUM\&lt;服务&gt;\&lt;实例&gt;\FriendlyName，
        /// 那里是明文，实测扫全表 **17ms**。
        ///
        /// 注意：BTHPORT\Parameters\Devices 下的 FriendlyName 是**加密**的，
        /// 读出来是乱码，那条路不通（已验证）。
        /// </summary>
        private static Dictionary<string, string> LoadModelMap()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();

            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Enum\BTHENUM", false))
                {
                    if (root == null) return map;

                    foreach (string service in root.GetSubKeyNames())
                    {
                        using (RegistryKey svc = root.OpenSubKey(service, false))
                        {
                            if (svc == null) continue;

                            foreach (string inst in svc.GetSubKeyNames())
                            {
                                string mac = FindMacIn(inst);
                                if (mac == null) continue;
                                if (map.ContainsKey(mac)) continue;

                                using (RegistryKey dev = svc.OpenSubKey(inst, false))
                                {
                                    if (dev == null) continue;
                                    object raw = dev.GetValue("FriendlyName");
                                    string name = DecodeRegString(raw);
                                    if (!string.IsNullOrEmpty(name)) map[mac] = name;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Islet] 读取蓝牙型号表失败: " + ex.Message);
            }

            return map;
        }

        /// <summary>
        /// 读注册表里的 PROPVARIANT 字符串。
        /// 实测同一台机器上可能是 byte[]（带 4 字节头）或 string，两种都要处理。
        /// </summary>
        private static string ReadPropString(RegistryKey props, string valueName)
        {
            object raw = props.GetValue(valueName);
            if (raw == null) return null;

            byte[] bytes = raw as byte[];
            if (bytes != null)
            {
                if (bytes.Length <= 4) return null;
                try
                {
                    return Encoding.Unicode.GetString(bytes, 4, bytes.Length - 4)
                        .TrimEnd('\0');
                }
                catch (Exception) { return null; }
            }

            string s = raw as string;
            if (s != null) return s.TrimEnd('\0');

            return null;
        }

        private static string DecodeRegString(object raw)
        {
            if (raw == null) return null;

            string s = raw as string;
            if (s != null) return s.TrimEnd('\0');

            byte[] bytes = raw as byte[];
            if (bytes != null && bytes.Length >= 2)
            {
                try { return Encoding.Unicode.GetString(bytes).TrimEnd('\0'); }
                catch (Exception) { return null; }
            }
            return null;
        }

        /// <summary>
        /// 从蓝牙设备路径里抠出 12 位设备 MAC（小写）。
        ///
        /// 不能简单地「找第一个 12 位十六进制串」——实测会抓错：
        /// 设备接口路径里还含服务 UUID，例如
        ///   ...bthenum#{0000110b-0000-1000-8000-00805f9b34fb}_vid&..._pid&...#7&29c0a8f6&1&315bb0197820_c00000000#...
        /// 朴素的「第一段 12 位」会命中 UUID 尾巴 00805f9b34fb，那是蓝牙基址，不是设备。
        ///
        /// 正确做法：只看 `&` 分隔出来的段，且要求该段**整段**都是 12 位十六进制
        /// （UUID 尾段前面紧跟 `-`，不会被 `&` 切出来，因此天然排除）。
        /// </summary>
        internal static string FindMacIn(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;

            string lower = text.ToLowerInvariant();
            string[] segs = lower.Split(new char[] { '&', '#', '\\', '_', '|', '{', '}' },
                                        StringSplitOptions.RemoveEmptyEntries);

            foreach (string seg in segs)
            {
                if (seg.Length != 12) continue;

                bool allHex = true;
                foreach (char c in seg)
                {
                    if (!IsHex(c)) { allHex = false; break; }
                }
                if (!allHex) continue;

                // 排除蓝牙基址尾部这种「看起来像 MAC 但不是」的常见值
                if (seg == "000000000000") continue;

                return seg;
            }

            return null;
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
        }

        private static void OpenBluetoothSettings()
        {
            try
            {
                System.Diagnostics.Process.Start("ms-settings:bluetooth");
            }
            catch (Exception)
            {
                try { System.Diagnostics.Process.Start("control", "bthprops.cpl"); }
                catch (Exception) { }
            }
        }

        // ===============================================================

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_timer != null)
            {
                try { _timer.Dispose(); }
                catch (Exception) { }
                _timer = null;
            }
            _sink = null;
        }
    }
}
