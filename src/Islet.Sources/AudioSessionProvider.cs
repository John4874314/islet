// AudioSessionProvider -- 单应用音量控制的数据层。
//
// 为什么这是 Windows 的强需求：系统原生把「每个应用单独调音量」藏在
// 音量合成器里（右键音量图标 → 打开音量合成器），路径深、窗口小、还经常
// 因为应用重开而重置。而它偏偏是高频操作：游戏太吵、浏览器视频太轻、
// 语音软件要压低背景音 —— 每一样都要点四层。
//
// 实现走 WASAPI 的 IAudioSessionManager2 / ISimpleAudioVolume。
// 关键点（都是实测踩出来的，别照抄网上常见的错误写法）：
//
//   1) 取每会话的音量必须用**该会话自己的 GroupingParam**，
//      传 Guid.Empty 拿到的是「设备主音量」——所有会话都显示同一个值，
//      而且一调就把整机音量改了。这是最隐蔽的一个坑。
//
//   2) 进程名不要依赖 Process.GetProcessById。会话标识符里带着 exe 的
//      设备路径（\Device\HarddiskVolumeN\...\app.exe），音频服务直接给出，
//      不依赖进程枚举权限，更可靠。实测在受限环境下 PID 反查会全部失败，
//      而会话标识符仍然有效。
//
//   3) IAudioSessionControl2 必须把父接口 IAudioSessionControl 的方法
//      按 vtable 顺序**全部重声明**，不能只写自己新增的那几个。
//
//   4) 音量/静音调用后要**回读**确认。hr==0 不代表真的生效，
//      回读值才是唯一可信的判据。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Islet.Sources
{
    /// <summary>
    /// 单会话音量/静音控制接口。
    /// 声明在命名空间层级（而不是嵌在 provider 里），因为 AudioSession 也要引用它。
    /// </summary>
    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid eventContext);
        int GetMute(out bool mute);
    }

    /// <summary>一个可调音量的音频会话（通常对应一个应用）。</summary>
    public sealed class AudioSession
    {
        /// <summary>稳定标识：同一次播放期间不变，用于跨刷新定位同一会话。</summary>
        public string Key;

        /// <summary>会话索引（每次枚举可能变化，仅供调试）。</summary>
        public int Index;

        /// <summary>进程 ID。0 表示系统声音。</summary>
        public uint ProcessId;

        /// <summary>显示名（powershell / chrome / …），取不到时为「未知应用」。</summary>
        public string DisplayName;

        /// <summary>exe 完整设备路径，可能为空。</summary>
        public string ExePath;

        /// <summary>0=空闲 1=播放中 2=已失效。</summary>
        public int State;

        /// <summary>是否正在出声。</summary>
        public bool IsActive { get { return State == 1; } }

        /// <summary>是否系统声音会话。</summary>
        public bool IsSystemSounds;

        /// <summary>音量 0.0 - 1.0。</summary>
        public float Volume;

        /// <summary>是否静音。</summary>
        public bool Muted;

        /// <summary>是否可调节（取到了 COM 控制接口）。系统声音会话通常也可调。</summary>
        public bool CanControl { get { return Control != null; } }

        /// <summary>
        /// 会话分组标识。同一应用的多路音频共享同一个分组，
        /// 也是 GetSimpleAudioVolume 需要的 key。
        /// </summary>
        public Guid Grouping { get; internal set; }

        /// <summary>COM 控制接口。不对外暴露，仅本类内部使用。</summary>
        internal ISimpleAudioVolume Control;

        /// <summary>
        /// 会话音量峰值（0.0 - 1.0）。
        ///
        /// 为什么需要它：**`State` 不可靠。**
        /// 2026-11-02 实测：网易云暂停后，会话的 State 仍然保持 1(Active)
        /// 长达约 20 秒才变 0 —— 因为它暂停时没关流，只是不再往里写数据，
        /// 音频引擎认为流还活着。
        ///
        /// 而峰值是引擎实时算出来的，没出声就立刻掉到 ≈0。
        /// 所以「是否正在播放」要看 Peak，不能看 State。
        /// </summary>
        public float Peak { get; internal set; }
    }

    /// <summary>
    /// 枚举 / 控制默认输出设备上的各应用音量。
    ///
    /// 用法：每次要刷新列表就新建一个实例、调用 Refresh()、用完 Dispose()。
    /// 不要长期持有 COM 对象：设备切换（插拔耳机）会让它们全部失效。
    /// </summary>
    public sealed class AudioSessionProvider : IDisposable
    {
        // ---------------------------------------------------------------
        // COM 声明
        // ---------------------------------------------------------------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject { }

        private const int eRender = 0;
        private const int eMultimedia = 1;
        private const int CLSCTX_ALL = 23;

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int NotImpl1();
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
            int NotImpl2();

            // 第 4/5 槽：设备变更通知。槽位必须补齐，跳不过去 —— 签名对不上会调错虚表项。
            int RegisterEndpointNotificationCallback(
                AudioDeviceWatcher.IMMNotificationClient client);
            int UnregisterEndpointNotificationCallback(
                AudioDeviceWatcher.IMMNotificationClient client);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                         [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);

            /// <summary>设备 ID。用来判断「默认播放设备换没换」。</summary>
            int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

            int GetState(out int state);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(ref Guid audioSessionGuid, int streamFlags,
                                       out IntPtr sessionControl);
            int GetSimpleAudioVolume(ref Guid audioSessionGuid, int streamFlags,
                                     out ISimpleAudioVolume audioVolume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
            int NotImpl1();
            int NotImpl2();
            int NotImpl3();
            int NotImpl4();
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
            int NotImpl1();
            int NotImpl2();
        }

        /// <summary>
        /// 会话音量峰值。用来判断「此刻是否真的在出声」。
        ///
        /// 之所以需要它：`State` 在暂停后会保持 Active 约 20 秒（实测），
        /// 而峰值是引擎实时计算的，没数据就立刻归零。
        ///
        /// ⚠️ 只在 ReadOne 里 QI 一次、**读完立刻让局部变量出作用域**，
        /// 绝不保存到 AudioSession 上。理由见 2026-11-02 的闪退：
        /// 同一个 RCW 被两个字段同时持有、只 Release 一次，就会提前销毁底层对象，
        /// 导致音频引擎回调打到野指针上（AUDIOSES.DLL c0000005）。
        /// 峰值只在枚举时读一次，用完不持有，是安全的。
        /// </summary>
        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IAudioMeterInformation
        {
            int GetPeakValue(out float peak);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // 父接口方法必须全部按顺序重声明 —— 见文件头注释 (3)
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
            int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string ret);
            int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string ret);
            int GetProcessId(out uint ret);

            // 必须加 [PreserveSig]：这个方法靠**返回值**表达「是不是系统声音」
            // （S_OK=是 / S_FALSE=不是），没有 out 参数。
            // 不加的话 CLR 会把返回值当 HRESULT 吞掉，托管侧永远拿到 0，
            // 于是**所有会话都被误判成系统声音** —— 实测踩过。
            [PreserveSig] int IsSystemSoundsSession();

            int SetDuckingPreference(bool optOut);
        }

        /// <summary>
        /// 取单会话音量控制的正确方式说明（实现见命名空间层级的
        /// <see cref="Islet.Sources.ISimpleAudioVolume"/>）。
        ///
        /// 必须通过 **会话控件 QI** 拿到它（`ctl as ISimpleAudioVolume`），
        /// **不要**用 `IAudioSessionManager2::GetSimpleAudioVolume`。
        ///
        /// 这是本轮修掉的核心 bug：GetSimpleAudioVolume 的第一个参数是
        /// **会话 GUID**（创建会话时用的那个），不是 `GetGroupingParam()` 返回的分组参数。
        /// 把分组参数传进去，拿到的是一个「新造的、与会话无关」的音量对象。
        /// 实测证据：
        ///   QI 路径写 30%  → 回读 30.0%（真的改了应用音量）
        ///   同一时刻 GetSimpleAudioVolume(grouping) → 回读 100.0%（完全无关）
        /// 所以滑块看着在动、实际没作用。
        /// </summary>

        // ---------------------------------------------------------------
        // 状态
        // ---------------------------------------------------------------

        private object _managerObj;
        private IAudioSessionManager2 _manager;

        /// <summary>
        /// 当前 manager 绑定的**默认播放设备 ID**。
        ///
        /// 为什么必须有它：manager 是 Activate 自某个具体设备的，
        /// 插耳机/拔耳机/切输出时默认设备会变，旧 manager 仍绑定旧设备。
        /// 之前只判断 `_manager == null` 就复用，于是：
        ///   · 会话枚举的是旧设备 → 岛上「谁在出声」直接空白
        ///   · 音量写进旧设备的会话 → 应用音量纹丝不动
        /// 设备 ID 变了就重建 manager，这两个问题一起消失。
        /// </summary>
        private string _deviceId;

        /// <summary>
        /// 复用的设备枚举器。每次 Refresh 都新建一个枚举器会在音频引擎里
        /// 留下对象（实测每轮约 1 个），按轮询累积成百上千。
        /// 只在这里建一次，Dispose 时释放。
        /// </summary>
        private IMMDeviceEnumerator _enumerator;
        private bool _disposed;

        /// <summary>上一次枚举是否成功。失败时 Sessions 为空。</summary>
        public bool Available { get; private set; }

        /// <summary>上一次失败的原因（供日志用）。</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// 上一次 SetVolume / SetMute 失败的具体原因（HRESULT 或异常）。
        /// 滑块「拖了没反应」时，这是唯一能定位问题的线索。
        /// </summary>
        public string LastWriteError { get; private set; }

        /// <summary>当前绑定的默认播放设备 ID（可能为空）。</summary>
        public string DeviceId { get { return _deviceId; } }

        /// <summary>
        /// 最近一次 Refresh 是否因为「默认播放设备变了」而重建了 manager。
        /// 插拔耳机、切换输出时会为 true —— 调用方可以据此记日志。
        /// </summary>
        public bool DeviceChangedLastRefresh { get; private set; }

        private readonly List<AudioSession> _sessions = new List<AudioSession>();

        /// <summary>当前已知的会话列表（只读快照）。</summary>
        public IList<AudioSession> Sessions { get { return _sessions; } }

        // ---------------------------------------------------------------
        // 枚举
        // ---------------------------------------------------------------

        /// <summary>
        /// 重新枚举默认输出设备上的所有会话。
        /// 返回是否成功；失败不抛异常，原因写进 LastError。
        /// </summary>
        public bool Refresh()
        {
            // 先释放上一轮的 COM 对象，否则每次刷新都会漏一批引用。
            // 面板每秒刷新一次的话，几分钟就能攒出上百个悬挂引用。
            ReleaseSessionControls();
            _sessions.Clear();
            Available = false;
            LastError = null;

            IMMDeviceEnumerator enumerator = _enumerator;
            IMMDevice device = null;

            try
            {
                if (enumerator == null)
                {
                    try
                    {
                        enumerator = (IMMDeviceEnumerator)
                            (new MMDeviceEnumeratorComObject());
                        _enumerator = enumerator;
                    }
                    catch (COMException ex)
                    {
                        LastError = "创建设备枚举器失败 0x"
                            + ex.ErrorCode.ToString("X8");
                        return false;
                    }

                    // 设备变更通知必须在**持有 coclass 的这一侧**注册。
                    // 原因见 AudioDeviceWatcher 的类注释：两边各定义一份同名同 GUID 的
                    // coclass 会让 RCW 类型标识冲突，抛出「X 不能转 X」的怪异常。
                    IMMDeviceEnumerator en2 = enumerator;
                    AudioDeviceWatcher.Attach(delegate(AudioDeviceWatcher.IMMNotificationClient c)
                    {
                        try { return en2.RegisterEndpointNotificationCallback(c) == 0; }
                        catch (Exception) { return false; }
                    });
                }

                int hr = enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia,
                                                            out device);
                if (hr != 0 || device == null)
                {
                    LastError = "没有可用的播放设备 0x" + hr.ToString("X8");
                    return false;
                }

                // 取当前默认设备的 ID，用于判断「插耳机 / 拔耳机 / 切输出」。
                string currentDeviceId = null;
                try { device.GetId(out currentDeviceId); }
                catch (Exception) { currentDeviceId = null; }

                // 设备变了就必须重建 manager：旧 manager 仍绑定旧设备，
                // 用它枚举会得到空列表、写音量也打不到新设备上的会话。
                bool deviceChanged = _manager == null
                    || (!string.IsNullOrEmpty(currentDeviceId)
                        && !string.IsNullOrEmpty(_deviceId)
                        && !string.Equals(currentDeviceId, _deviceId,
                                          StringComparison.OrdinalIgnoreCase));

                // 首次绑定（_deviceId 为空）不算「切换」，避免每次启动都误报
                DeviceChangedLastRefresh = deviceChanged && !string.IsNullOrEmpty(_deviceId);

                if (deviceChanged)
                {
                    ReleaseManager();

                    Guid iid = typeof(IAudioSessionManager2).GUID;
                    object obj;
                    hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out obj);
                    if (hr != 0 || obj == null)
                    {
                        LastError = "激活会话管理器失败 0x" + hr.ToString("X8");
                        return false;
                    }
                    _managerObj = obj;
                    _manager = (IAudioSessionManager2)obj;
                    _deviceId = currentDeviceId;
                }

                IAudioSessionEnumerator sessionEnum = null;
                try
                {
                    hr = _manager.GetSessionEnumerator(out sessionEnum);
                    if (hr != 0 || sessionEnum == null)
                    {
                        LastError = "获取会话枚举器失败 0x" + hr.ToString("X8");
                        return false;
                    }

                    int count;
                    sessionEnum.GetCount(out count);
                    for (int i = 0; i < count; i++)
                    {
                        IAudioSessionControl ctl = null;
                        IAudioSessionControl2 ctl2 = null;

                        // 会话是否把「从 ctl QI 出来的音量接口」留在了 s.Control 上。
                        // 是的话这里**不能**释放 ctl —— 那是同一个 RCW，
                        // 释放会让它被「分离」，s.Control 立刻不可用
                        // （报错：COM 对象与其基础 RCW 分开后就不能再使用）。
                        // 那种情况交给 ReleaseSessionControls() 统一释放一次。
                        bool keepsCtl = false;

                        try
                        {
                            if (sessionEnum.GetSession(i, out ctl) != 0 || ctl == null) continue;

                            ctl2 = ctl as IAudioSessionControl2;
                            if (ctl2 == null) continue;

                            AudioSession s = ReadOne(i, ctl, ctl2, out keepsCtl);
                            if (s != null) _sessions.Add(s);
                        }
                        finally
                        {
                            // 每枚举一个会话都必须释放它自己的 COM 引用。
                            //
                            // 这里曾经是「漏得最狠」的一处：ctl / ctl2 全都没 Release。
                            // 本机音频会话数量很容易上千（每次播放器/浏览器/系统提示音
                            // 都会留下会话），一旦按秒轮询，引用就按「会话数 × 次数」
                            // 累积——实测几分钟就吃到 2.9 GB 内存、3 万多个线程，
                            // 属于会把机器拖死的量级。
                            //
                            // 注意：ctl2 与 ctl 是同一个 RCW，只释放一次；
                            // 重复释放会抛异常（虽被 catch 吞掉，但语义上是错的）。
                            if (!keepsCtl)
                            {
                                if (ctl2 != null && Marshal.IsComObject(ctl2))
                                {
                                    try { Marshal.ReleaseComObject(ctl2); }
                                    catch (Exception) { }
                                }
                                else if (ctl != null && Marshal.IsComObject(ctl))
                                {
                                    try { Marshal.ReleaseComObject(ctl); }
                                    catch (Exception) { }
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (sessionEnum != null && Marshal.IsComObject(sessionEnum))
                    {
                        try { Marshal.ReleaseComObject(sessionEnum); }
                        catch (Exception) { }
                    }
                }

                Available = true;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                // 注意：enumerator 是**缓存**的（_enumerator），这里绝不能释放，
                // 否则下一轮 Refresh 会拿到一个已释放的 RCW 直接抛异常。
                // 它由 Dispose 统一释放。
                if (device != null && Marshal.IsComObject(device))
                    Marshal.ReleaseComObject(device);
            }
        }

        private AudioSession ReadOne(int index, IAudioSessionControl ctl,
                                     IAudioSessionControl2 ctl2, out bool keepsCtl)
        {
            keepsCtl = false;
            AudioSession s = new AudioSession();
            s.Index = index;

            int state;
            ctl.GetState(out state);
            s.State = state;

            uint pid;
            ctl2.GetProcessId(out pid);
            s.ProcessId = pid;
            s.IsSystemSounds = (ctl2.IsSystemSoundsSession() == 0);

            // 标识符优先：不依赖进程枚举权限
            string ident = null;
            try { ctl2.GetSessionInstanceIdentifier(out ident); }
            catch (Exception) { ident = null; }
            if (string.IsNullOrEmpty(ident))
            {
                try { ctl2.GetSessionIdentifier(out ident); }
                catch (Exception) { ident = null; }
            }

            s.Key = ident;
            s.ExePath = ExeFromSessionId(ident);
            s.DisplayName = DisplayNameFor(s.ExePath, pid, s.IsSystemSounds);

            // 音量/静音：必须用 **会话控件 QI** 拿 ISimpleAudioVolume。
            //
            // 不能再用 IAudioSessionManager2::GetSimpleAudioVolume(grouping)：
            // 它的入参是「会话 GUID」而不是分组参数，传分组参数拿到的是无关对象，
            // 写进去应用音量纹丝不动（实测对比见 ISimpleAudioVolume 的注释）。
            Guid grouping = Guid.Empty;
            try { ctl.GetGroupingParam(out grouping); }
            catch (Exception) { grouping = Guid.Empty; }
            s.Grouping = grouping;

            ISimpleAudioVolume vol = ctl as ISimpleAudioVolume;
            if (vol != null)
            {
                // 这个接口与 ctl 是同一个 RCW，调用方不能再释放 ctl，
                // 否则 RCW 被分离，s.Control 立刻失效。
                keepsCtl = true;
            }
            else
            {
                // 极少数情况下会话控件不支持 QI，退回管理器接口。
                // 这时用 Guid.Empty 而不是 grouping —— 会话 GUID 通常就是 NULL，
                // 至少比传分组参数更接近正确语义。
                Guid nullGuid = Guid.Empty;
                try
                {
                    ISimpleAudioVolume viaMgr;
                    if (_manager.GetSimpleAudioVolume(ref nullGuid, 0, out viaMgr) == 0)
                        vol = viaMgr;
                }
                catch (Exception) { vol = null; }
            }

            if (vol != null)
            {
                float v;
                vol.GetMasterVolume(out v);
                s.Volume = v;
                bool m;
                vol.GetMute(out m);
                s.Muted = m;
                s.Control = vol;
            }

            // ---- 读峰值（瞬时，不持有）----
            //
            // 只在这里 QI 一次，读完就让局部变量出作用域。
            // **绝不把 meter 存到 AudioSession 上** —— 那样它就成了同一 RCW 的
            // 第二个持有者，释放时算错引用计数，会重演 2026-11-02 的
            // AUDIOSES.DLL 闪退。
            //
            // 峰值读不到不影响功能，保持 0 即可。
            try
            {
                IAudioMeterInformation meter = ctl as IAudioMeterInformation;
                if (meter != null)
                {
                    float peak;
                    if (meter.GetPeakValue(out peak) == 0) s.Peak = peak;
                }
            }
            catch (Exception) { /* 读不到就当 0 */ }

            return s;
        }

        /// <summary>
        /// 从会话标识符里抠出 exe 路径。
        /// 形如：{guid}|\Device\HarddiskVolume3\...\app.exe%b{guid}
        /// 「#」是路径不可用的占位符（进程已退出时常见），此时返回 null。
        /// </summary>
        internal static string ExeFromSessionId(string ident)
        {
            if (string.IsNullOrEmpty(ident)) return null;

            int bar = ident.IndexOf('|');
            string body = bar >= 0 ? ident.Substring(bar + 1) : ident;

            int pct = body.IndexOf("%b");
            if (pct >= 0) body = body.Substring(0, pct);

            body = body.Trim();
            if (body.Length == 0 || body == "#") return null;
            if (!body.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return body;
        }

        /// <summary>
        /// 设备路径 → 用户看得懂的名字。
        /// \Device\HarddiskVolume3\...\chrome.exe → chrome
        /// </summary>
        internal static string DisplayNameFor(string exePath, uint pid, bool sysSounds)
        {
            if (sysSounds) return "系统声音";

            if (!string.IsNullOrEmpty(exePath))
            {
                int slash = exePath.LastIndexOf('\\');
                string file = slash >= 0 ? exePath.Substring(slash + 1) : exePath;
                int dot = file.LastIndexOf('.');
                string name = dot > 0 ? file.Substring(0, dot) : file;
                if (name.Length > 0) return Friendly(name);
            }

            if (pid == 0) return "系统声音";
            return "未知应用";
        }

        /// <summary>把 chrome / msedge 这类内部名换成用户认得的说法。</summary>
        private static string Friendly(string exeName)
        {
            switch (exeName.ToLowerInvariant())
            {
                case "chrome": return "Chrome";
                case "msedge": return "Edge";
                case "firefox": return "Firefox";
                case "qqmusic": return "QQ音乐";
                case "cloudmusic": return "网易云音乐";
                case "potplayermini64":
                case "potplayermini": return "PotPlayer";
                case "vlc": return "VLC";
                case "wmplayer": return "媒体播放器";
                case "spotify": return "Spotify";
                case "wechat": return "微信";
                case "qq": return "QQ";
                case "discord": return "Discord";
                case "teams": return "Teams";
                case "zoom": return "Zoom";
                case "wemeetapp": return "腾讯会议";
                case "feishu": return "飞书";
                case "steam": return "Steam";
                case "steamwebhelper": return "Steam";
                default: return exeName;
            }
        }

        // ---------------------------------------------------------------
        // 控制
        // ---------------------------------------------------------------

        /// <summary>
        /// 设置某个会话的音量（0.0 - 1.0）。
        /// 返回**回读到的实际值**；失败返回 -1。
        /// 一定要看回读值：hr==0 不等于生效。
        /// </summary>
        public float SetVolume(AudioSession s, float level)
        {
            LastWriteError = null;
            if (s == null) { LastWriteError = "session=null"; return -1f; }
            if (s.Control == null) { LastWriteError = "control=null"; return -1f; }
            if (level < 0f) level = 0f;
            if (level > 1f) level = 1f;

            Guid ctx = Guid.Empty;
            try
            {
                int hr = s.Control.SetMasterVolume(level, ref ctx);
                if (hr != 0)
                {
                    // 把 HRESULT 留下来：滑块静默失败时这是唯一的线索。
                    LastWriteError = "SetMasterVolume hr=0x" + hr.ToString("X8");
                    return -1f;
                }

                float back;
                s.Control.GetMasterVolume(out back);
                s.Volume = back;
                return back;
            }
            catch (Exception ex)
            {
                LastWriteError = "SetMasterVolume 异常: " + ex.Message;
                return -1f;
            }
        }

        /// <summary>静音/取消静音。返回回读到的静音状态；失败返回 null。</summary>
        public bool? SetMute(AudioSession s, bool muted)
        {
            if (s == null || s.Control == null) return null;
            Guid ctx = Guid.Empty;
            try
            {
                int hr = s.Control.SetMute(muted, ref ctx);
                if (hr != 0) return null;

                bool back;
                s.Control.GetMute(out back);
                s.Muted = back;
                return back;
            }
            catch (Exception) { return null; }
        }

        // ---------------------------------------------------------------
        // 清理
        // ---------------------------------------------------------------

        /// <summary>释放当前持有的所有会话 COM 引用（刷新与 Dispose 共用）。</summary>
        private void ReleaseSessionControls()
        {
            foreach (AudioSession s in _sessions)
            {
                if (s.Control != null && Marshal.IsComObject(s.Control))
                {
                    try { Marshal.ReleaseComObject(s.Control); }
                    catch (Exception) { }
                }
                s.Control = null;
            }
        }

        /// <summary>
        /// 释放 manager 及其设备 ID 记录。
        /// 默认设备切换时必须先调用它，否则新设备会复用旧设备的 manager。
        /// </summary>
        private void ReleaseManager()
        {
            if (_managerObj != null && Marshal.IsComObject(_managerObj))
            {
                try { Marshal.ReleaseComObject(_managerObj); }
                catch (Exception) { }
            }
            _managerObj = null;
            _manager = null;
            _deviceId = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ReleaseSessionControls();
            _sessions.Clear();

            ReleaseManager();

            if (_enumerator != null && Marshal.IsComObject(_enumerator))
            {
                try { Marshal.ReleaseComObject(_enumerator); }
                catch (Exception) { }
            }
            _enumerator = null;
        }
    }
}
