// AudioSessionProbe -- 验证 WASAPI 能否枚举「每个应用」的音量会话。
//
// 为什么先写探针而不是直接改主程序：
// 单应用音量能不能做，取决于三件事能否在真机上稳定拿到：
//   1) 会话枚举（IAudioSessionEnumerator）是否返回全部有声音的进程
//   2) 从会话反查 PID、再反查进程名是否可靠
//   3) GetMasterVolume / SetMasterVolume / GetMute 是否真的生效
// 这三点任何一点不成立，整个功能就是空中楼阁。先用一个不依赖 WPF 的
// 控制台程序跑通，再考虑接进岛上。
//
// 编译（不进 build.ps1，手动编）：
//   csc /nologo /target:exe /out:build\AudioSessionProbe.exe islet\tools\AudioSessionProbe.cs
// 运行：
//   build\AudioSessionProbe.exe            列出所有会话
//   build\AudioSessionProbe.exe --set 0 0.3  把第 0 个会话音量设为 30%
//   build\AudioSessionProbe.exe --mute 0    切换第 0 个会话静音
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Islet.Tools
{
    internal static class AudioSessionProbe
    {
        // ---------------------------------------------------------------
        // WASAPI COM 声明
        // ---------------------------------------------------------------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        private const int eRender = 0;
        private const int eMultimedia = 1;

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int NotImpl1();   // EnumAudioEndpoints（本探针不用）
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
            int NotImpl2();   // GetDevice
            int NotImpl3();   // RegisterEndpointNotificationCallback
            int NotImpl4();   // UnregisterEndpointNotificationCallback
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx,
                         IntPtr pActivationParams,
                         [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int NotImpl1();   // OpenPropertyStore
            int NotImpl2();   // GetId
            int NotImpl3();   // GetState
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            // --- IAudioSessionManager ---
            int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags,
                                       out IntPtr sessionControl);
            int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags,
                                     out ISimpleAudioVolume audioVolume);
            // --- IAudioSessionManager2 ---
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            int NotImpl1();   // RegisterSessionNotification
            int NotImpl2();   // UnregisterSessionNotification
            int NotImpl3();   // RegisterDuckNotification
            int NotImpl4();   // UnregisterDuckNotification
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int sessionCount);
            int GetSession(int sessionIndex, out IAudioSessionControl session);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            int GetState(out int state);
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name,
                               ref Guid eventContext);
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path,
                            ref Guid eventContext);
            int GetGroupingParam(out Guid groupingParam);
            int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
            int NotImpl1();   // RegisterAudioSessionNotification
            int NotImpl2();   // UnregisterAudioSessionNotification
        }

        // IAudioSessionControl2 必须完整重声明父接口方法（COM vtable 顺序），
        // 不能只写自己新增的那几个 —— 这是 interop 最常见的踩坑点。
        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // --- IAudioSessionControl ---
            int GetState(out int state);
            int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name,
                               ref Guid eventContext);
            int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path,
                            ref Guid eventContext);
            int GetGroupingParam(out Guid groupingParam);
            int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
            int NotImpl1();
            int NotImpl2();
            // --- IAudioSessionControl2 ---
            int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string ret);
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string ret);
            int GetProcessId(out uint ret);
            int IsSystemSoundsSession();
            int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            int SetMasterVolume(float level, ref Guid eventContext);
            int GetMasterVolume(out float level);
            int SetMute(bool mute, ref Guid eventContext);
            int GetMute(out bool mute);
        }

        // ---------------------------------------------------------------
        // 数据
        // ---------------------------------------------------------------

        private sealed class SessionInfo
        {
            public int Index;
            public uint Pid;
            public string ProcessName;
            public string DisplayName;
            public float Volume;
            public bool Muted;
            public int State;          // 0=Inactive 1=Active 2=Expired
            public bool IsSystemSounds;
            public Guid Grouping;
            public string SessionId;
            public string InstanceId;
            public string ExePath;
            public ISimpleAudioVolume VolumeControl;

            public string StateText
            {
                get
                {
                    if (State == 1) return "播放中";
                    if (State == 0) return "空闲";
                    return "已失效";
                }
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            List<SessionInfo> sessions = Enumerate();
            if (sessions == null) return 1;

            if (args.Length >= 1 && args[0] == "--set")
            {
                if (args.Length < 3)
                {
                    Console.WriteLine("用法: --set <索引> <0.0-1.0>");
                    return 2;
                }
                int idx = int.Parse(args[1]);
                float vol = float.Parse(args[2],
                    System.Globalization.CultureInfo.InvariantCulture);
                SessionInfo s = Find(sessions, idx);
                if (s == null) { Console.WriteLine("没有该索引"); return 2; }

                Guid ctx = Guid.Empty;
                float before;
                s.VolumeControl.GetMasterVolume(out before);
                int hr = s.VolumeControl.SetMasterVolume(vol, ref ctx);
                float after;
                s.VolumeControl.GetMasterVolume(out after);
                Console.WriteLine(string.Format(
                    "SET  [{0}] {1}: {2:P0} -> {3:P0}  hr=0x{4:X8}  回读={5:P0}",
                    idx, s.ProcessName, before, vol, hr, after));
                return 0;
            }

            if (args.Length >= 2 && args[0] == "--mute")
            {
                int idx = int.Parse(args[1]);
                SessionInfo s = Find(sessions, idx);
                if (s == null) { Console.WriteLine("没有该索引"); return 2; }

                Guid ctx = Guid.Empty;
                bool before;
                s.VolumeControl.GetMute(out before);
                int hr = s.VolumeControl.SetMute(!before, ref ctx);
                bool after;
                s.VolumeControl.GetMute(out after);
                Console.WriteLine(string.Format(
                    "MUTE [{0}] {1}: {2} -> {3}  hr=0x{4:X8}  回读={5}",
                    idx, s.ProcessName, before, !before, hr, after));
                return 0;
            }

            Dump(sessions);
            return 0;
        }

        private static SessionInfo Find(List<SessionInfo> list, int index)
        {
            foreach (SessionInfo s in list)
            {
                if (s.Index == index) return s;
            }
            return null;
        }

        private static void Dump(List<SessionInfo> sessions)
        {
            Console.WriteLine("===== 音频会话（默认输出设备）=====");
            Console.WriteLine("索引  进程                 状态     音量   静音  来源");
            Console.WriteLine(new string('-', 78));

            foreach (SessionInfo s in sessions)
            {
                string vol = s.Volume.ToString("P0");
                string src = string.IsNullOrEmpty(s.ExePath) ? "PID" : "会话ID";
                Console.WriteLine(string.Format(
                    "{0,-4}  {1,-20} {2,-8} {3,-5} {4,-5} {5}",
                    s.Index,
                    Cut(s.ProcessName, 20),
                    s.StateText,
                    vol,
                    s.Muted ? "是" : "",
                    src));
            }

            Console.WriteLine(new string('-', 78));
            Console.WriteLine("会话总数=" + sessions.Count);

            int active = 0;
            foreach (SessionInfo s in sessions) { if (s.State == 1) active++; }
            Console.WriteLine("其中正在播放=" + active);

            int viaId = 0;
            foreach (SessionInfo s in sessions)
            {
                if (!string.IsNullOrEmpty(s.ExePath)) viaId++;
            }
            Console.WriteLine("从会话ID解析出 exe 路径=" + viaId + " / " + sessions.Count);

            Console.WriteLine();
            Console.WriteLine("--- 会话标识符明细 ---");
            foreach (SessionInfo s in sessions)
            {
                Console.WriteLine(string.Format("[{0}] pid={1}", s.Index, s.Pid));
                Console.WriteLine("    会话ID = " +
                    (string.IsNullOrEmpty(s.SessionId) ? "(空)" : s.SessionId));
                Console.WriteLine("    实例ID = " +
                    (string.IsNullOrEmpty(s.InstanceId) ? "(空)" : s.InstanceId));
                Console.WriteLine("    exe    = " +
                    (string.IsNullOrEmpty(s.ExePath) ? "(无)" : s.ExePath));
            }

            if (sessions.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("提示：用 --set <索引> <0-1> 或 --mute <索引> 实测控制是否生效");
            }
        }

        private static string Cut(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        // ---------------------------------------------------------------
        // 枚举
        // ---------------------------------------------------------------

        private static List<SessionInfo> Enumerate()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            object managerObj = null;

            try
            {
                try
                {
                    enumerator = (IMMDeviceEnumerator)
                        (new MMDeviceEnumeratorComObject());
                }
                catch (COMException ex)
                {
                    Console.WriteLine("创建 MMDeviceEnumerator 失败: 0x"
                        + ex.ErrorCode.ToString("X8") + " " + ex.Message);
                    return null;
                }

                int hr = enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia,
                                                            out device);
                if (hr != 0 || device == null)
                {
                    Console.WriteLine("GetDefaultAudioEndpoint 失败: 0x"
                        + hr.ToString("X8") + "（可能没有播放设备）");
                    return null;
                }

                Guid iidManager = typeof(IAudioSessionManager2).GUID;
                hr = device.Activate(ref iidManager, 23 /*CLSCTX_ALL*/,
                                     IntPtr.Zero, out managerObj);
                if (hr != 0 || managerObj == null)
                {
                    Console.WriteLine("Activate(IAudioSessionManager2) 失败: 0x"
                        + hr.ToString("X8"));
                    return null;
                }

                IAudioSessionManager2 manager = (IAudioSessionManager2)managerObj;
                IAudioSessionEnumerator sessionEnum;
                hr = manager.GetSessionEnumerator(out sessionEnum);
                if (hr != 0 || sessionEnum == null)
                {
                    Console.WriteLine("GetSessionEnumerator 失败: 0x"
                        + hr.ToString("X8"));
                    return null;
                }

                int count;
                sessionEnum.GetCount(out count);
                Console.WriteLine("原始会话数=" + count);
                Console.WriteLine();

                List<SessionInfo> result = new List<SessionInfo>();
                int index = 0;

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctl;
                    if (sessionEnum.GetSession(i, out ctl) != 0 || ctl == null) continue;

                    IAudioSessionControl2 ctl2 = ctl as IAudioSessionControl2;
                    if (ctl2 == null) continue;

                    SessionInfo info = new SessionInfo();
                    info.Index = index++;

                    uint pid;
                    ctl2.GetProcessId(out pid);
                    info.Pid = pid;
                    info.ProcessName = ResolveName(pid);
                    info.IsSystemSounds = (ctl2.IsSystemSoundsSession() == 0);

                    // 会话标识符里带着 exe 的完整路径，形如：
                    //   {...}|\Device\HarddiskVolume3\Program Files\Foo\foo.exe%b{...}
                    // 这比「PID → Process.GetProcessById」可靠得多：后者需要进程枚举权限，
                    // 在受限环境下会全部失败（本机实测过），而这里由音频服务直接给出。
                    string ident;
                    try
                    {
                        ctl2.GetSessionIdentifier(out ident);
                        info.SessionId = ident;
                        string fromPath = ExeFromSessionId(ident);
                        if (!string.IsNullOrEmpty(fromPath))
                        {
                            info.ExePath = fromPath;
                            if (string.IsNullOrEmpty(info.ProcessName)
                                || info.ProcessName.StartsWith("(未知"))
                            {
                                info.ProcessName = System.IO.Path.GetFileNameWithoutExtension(
                                    fromPath);
                            }
                        }
                    }
                    catch (Exception) { info.SessionId = ""; }

                    try
                    {
                        ctl2.GetSessionInstanceIdentifier(out ident);
                        info.InstanceId = ident;
                        if (string.IsNullOrEmpty(info.ExePath))
                        {
                            string fromInst = ExeFromSessionId(ident);
                            if (!string.IsNullOrEmpty(fromInst))
                            {
                                info.ExePath = fromInst;
                                if (string.IsNullOrEmpty(info.ProcessName)
                                    || info.ProcessName.StartsWith("(未知"))
                                {
                                    info.ProcessName =
                                        System.IO.Path.GetFileNameWithoutExtension(fromInst);
                                }
                            }
                        }
                    }
                    catch (Exception) { info.InstanceId = ""; }

                    int state;
                    ctl.GetState(out state);
                    info.State = state;

                    string disp;
                    try { ctl.GetDisplayName(out disp); info.DisplayName = disp; }
                    catch (Exception) { info.DisplayName = ""; }

                    // 音量/静音：**必须用该会话自己的 GroupingParam**。
                    // 用 Guid.Empty 取到的是「设备主音量」，对所有会话都一样 ——
                    // 那样显示出来每个应用都是 100%，调节也会把整个设备音量改掉。
                    // 这是 WASAPI interop 最容易踩的坑，实测确认过。
                    ISimpleAudioVolume vol;
                    Guid grouping = Guid.Empty;
                    try { ctl.GetGroupingParam(out grouping); }
                    catch (Exception) { grouping = Guid.Empty; }
                    info.Grouping = grouping;

                    if (manager.GetSimpleAudioVolume(ref grouping, 0, out vol) == 0
                        && vol != null)
                    {
                        float v;
                        vol.GetMasterVolume(out v);
                        info.Volume = v;
                        bool m;
                        vol.GetMute(out m);
                        info.Muted = m;
                        info.VolumeControl = vol;
                    }

                    result.Add(info);
                }

                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine("枚举异常: " + ex.GetType().Name + " " + ex.Message);
                return null;
            }
            finally
            {
                if (managerObj != null && Marshal.IsComObject(managerObj))
                    Marshal.ReleaseComObject(managerObj);
                if (device != null && Marshal.IsComObject(device))
                    Marshal.ReleaseComObject(device);
                if (enumerator != null && Marshal.IsComObject(enumerator))
                    Marshal.ReleaseComObject(enumerator);
            }
        }

        /// <summary>
        /// 从会话标识符里抠出 exe 路径。
        /// 格式：{guid}|\Device\HarddiskVolumeN\path\to\app.exe%b{guid}
        /// 前半段是「设备路径」，后半段（%b 之后）是该进程内具体实例的 GUID。
        /// </summary>
        private static string ExeFromSessionId(string ident)
        {
            if (string.IsNullOrEmpty(ident)) return null;

            // 只保留第一个 | 之后的部分
            int bar = ident.IndexOf('|');
            string body = bar >= 0 ? ident.Substring(bar + 1) : ident;

            // 去掉 %b{...} 尾巴
            int pct = body.IndexOf("%b");
            if (pct >= 0) body = body.Substring(0, pct);

            body = body.Trim();
            if (body.Length == 0) return null;

            // 必须以 .exe 结尾（大小写不敏感）才算「一个可执行文件」
            if (!body.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return body;
        }

        /// <summary>PID → 进程名。取不到就退回「(未知)」并带上 PID，便于排查。</summary>
        private static string ResolveName(uint pid)
        {
            if (pid == 0) return "(系统声音)";
            try
            {
                Process p = Process.GetProcessById((int)pid);
                try { return p.ProcessName; }
                finally { p.Dispose(); }
            }
            catch (Exception)
            {
                return "(未知:" + pid + ")";
            }
        }
    }
}
