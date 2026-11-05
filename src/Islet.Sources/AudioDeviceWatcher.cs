using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Islet.Sources
{
    /// <summary>
    /// 监听「默认播放设备变化」的推送式通知。
    ///
    /// 为什么需要它：插拔耳机时默认播放设备会切换，而媒体条靠 30 秒一次的轮询
    /// （频率是性能红线，不能调快 —— 每次枚举音频会话都会在音频引擎里留下会话对象）。
    /// 结果就是插上耳机后要等半分钟岛上才更新，用户感知就是「没响应」。
    ///
    /// `IMMNotificationClient` 是系统主动回调，**不枚举会话**，几乎没有成本。
    ///
    /// ------------------------------------------------------------------
    /// 重要教训（2026-10-30 踩过，岛完全不显示媒体条）：
    ///
    /// 这个类最初**自己又定义了一份** `MMDeviceEnumeratorComObject`
    /// （`[ComImport] [Guid("BCDE0395-...")]` coclass），
    /// 与 `AudioSessionProvider` 里那份**同名同 GUID**。
    ///
    /// 后果极其隐蔽：`(IMMDeviceEnumerator)(new MMDeviceEnumeratorComObject())`
    /// 在运行期抛
    ///     InvalidCastException: 无法将类型"MMDeviceEnumeratorComObject"
    ///         强制转换为类型"MMDeviceEnumeratorComObject"
    /// ——同一个类型名转同一个类型名失败。原因是两个程序集级 ComImport coclass
    /// 抢同一个 CLSID，RCW 的类型标识对不上。
    ///
    /// 这个异常发生在**静态共享实例** `NowPlayingProvider.Shared` 的 Refresh 里，
    /// 被 catch 后只写进 LastError，于是 MediaProvider 的回落分支永远拿到「0 个正在播放」，
    /// 岛一次都不会显示「正在播放」。
    ///
    /// 所以现在：**coclass 与 IMMDeviceEnumerator 只在 AudioSessionProvider 里定义一份**，
    /// 注册动作也放在那边（`AudioSessionProvider.TryRegisterDeviceNotifications`）。
    /// 本类只保留回调接口与事件，不再碰任何 coclass。
    /// ------------------------------------------------------------------
    /// </summary>
    public static class AudioDeviceWatcher
    {
        /// <summary>默认播放设备变化时触发。可能在任意线程上。</summary>
        public static event Action DefaultDeviceChanged;

        /// <summary>是否成功注册了系统回调。false 表示退回纯轮询。</summary>
        public static bool IsActive { get { return _impl != null; } }

        /// <summary>每次变化 +1。轮询方可以据此发现自己「错过」了变化。</summary>
        public static int Version { get { return _version; } }

        private static int _version;
        private static Impl _impl;          // 必须保住强引用，否则被 GC 后回调就断了
        private static readonly object _gate = new object();

        /// <summary>
        /// 注册设备变更通知。由 <see cref="AudioSessionProvider"/> 在创建枚举器后调用，
        /// 因为它持有唯一的 coclass 声明。
        /// 重复调用安全。
        /// </summary>
        internal static void Attach(NotifyRegistrar registrar)
        {
            if (_impl != null) return;

            lock (_gate)
            {
                if (_impl != null) return;

                try
                {
                    Impl impl = new Impl();
                    if (!registrar(impl)) return;
                    _impl = impl;
                }
                catch (Exception)
                {
                    _impl = null;
                }
            }
        }

        /// <summary>注册动作的委托形式，避免本类直接接触 coclass 类型。</summary>
        internal delegate bool NotifyRegistrar(IMMNotificationClient client);

        private static void RaiseChanged()
        {
            Interlocked.Increment(ref _version);
            Action h = DefaultDeviceChanged;
            if (h == null) return;
            try { h(); }
            catch (Exception) { }
        }

        // -------------------------------------------------------------
        // COM 声明（只有回调接口，没有 coclass）
        // -------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROPERTYKEY
        {
            public Guid fmtid;
            public int pid;
        }

        [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMNotificationClient
        {
            int OnDeviceStateChanged(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
            int OnDeviceAdded(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            int OnDeviceRemoved(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId);
            int OnDefaultDeviceChanged(
                int flow, int role,
                [MarshalAs(UnmanagedType.LPWStr)] string defaultDeviceId);
            int OnPropertyValueChanged(
                [MarshalAs(UnmanagedType.LPWStr)] string deviceId, PROPERTYKEY key);
        }

        /// <summary>
        /// 回调实现。实现 ComImport 接口的托管类会被 CLR 包成 CCW 传给 COM。
        /// 每个方法都必须返回 0（S_OK），否则系统可能不再回调。
        /// </summary>
        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class Impl : IMMNotificationClient
        {
            public int OnDeviceStateChanged(string deviceId, int newState)
            {
                if (newState == 1 || newState == 4 || newState == 8) RaiseChanged();
                return 0;
            }

            public int OnDeviceAdded(string deviceId) { return 0; }

            public int OnDeviceRemoved(string deviceId)
            {
                RaiseChanged();
                return 0;
            }

            public int OnDefaultDeviceChanged(int flow, int role, string defaultDeviceId)
            {
                // flow: 0=Render 1=Capture；role: 0=Console 1=Multimedia 2=Communications
                if (flow == 0 && (role == 0 || role == 1)) RaiseChanged();
                return 0;
            }

            public int OnPropertyValueChanged(string deviceId, PROPERTYKEY key)
            {
                return 0;
            }
        }
    }
}
