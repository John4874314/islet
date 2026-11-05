using System;

namespace Islet.Core
{
    /// <summary>胶囊停靠位置。</summary>
    public enum IslandPosition
    {
        TopCenter = 0,
        TopLeft = 1,
        TopRight = 2,

        /// <summary>
        /// 自由拖拽：位置由 FreeX / FreeY 决定，不再吸附到三个固定角落。
        /// 这是默认值——固定角落会挡住浏览器右上角的关闭按钮，用户反馈很强烈。
        /// </summary>
        Free = 3
    }

    /// <summary>
    /// 玻璃透明度档位。三档都是液态玻璃，只是基底的不透明度不同。
    /// 数值越大越透（越能看见背后的内容），文字对比度也越低。
    /// </summary>
    public enum GlassOpacity
    {
        /// <summary>深色玻璃（默认）。背后内容基本看不见，白字对比度最高。</summary>
        Deep = 0,

        /// <summary>中等。能隐约看到背后的色块。</summary>
        Medium = 1,

        /// <summary>清透。背后内容明显可辨，适合浅色壁纸。</summary>
        Clear = 2
    }

    /// <summary>
    /// 用户设置。纯数据 + 默认值，不含任何 IO 或 UI 依赖。
    /// 序列化格式见 <see cref="SettingsCodec"/>。
    /// </summary>
    public sealed class IsletSettings
    {
        // ---- 数据源开关 ----
        public bool MediaEnabled = true;
        public bool TimerEnabled = true;
        public bool ProgressEnabled = true;
        public bool HealthEnabled = true;
        public bool ClipboardEnabled = true;
        public bool PrivacyEnabled = true;

        /// <summary>
        /// 蓝牙耳机连接提示。
        ///
        /// 只做「连上/断开」的提示，**不做电量**：实测本机四副耳机全是
        /// 经典蓝牙（A2DP + AVRCP），没有 GATT Battery Service，
        /// Windows 层面读不到电量，硬做只能显示占位符。
        /// </summary>
        public bool BluetoothEnabled = true;

        // ---- 外观与行为 ----
        public IslandPosition Position = IslandPosition.Free;

        /// <summary>玻璃透明度档位。</summary>
        public GlassOpacity Opacity = GlassOpacity.Deep;

        /// <summary>
        /// 自由拖拽时的位置（屏幕工作区内的 WPF 逻辑坐标，左上角）。
        /// 仅在 Position == Free 时生效。NaN 表示「还没拖过，用默认位置」。
        /// </summary>
        public double FreeX = double.NaN;
        public double FreeY = double.NaN;

        /// <summary>游戏/全屏时是否自动隐身。</summary>
        public bool HideInFullscreen = true;

        // ---- 系统集成 ----
        /// <summary>
        /// 开机自启。按产品要求**默认开启**。
        ///
        /// 注意与「默认值」的区别：首次运行时宿主会尝试写入自启项；
        /// 若系统拒绝（组策略、受限账户、安全软件拦截），宿主会把实际结果回写到
        /// 这里并让设置页如实显示，而不是让开关变成一个谎话。
        /// </summary>
        public bool AutorunEnabled = true;

        /// <summary>
        /// 内存整理：空闲时把工作集换出，降低任务管理器里的占用。
        /// 默认开启，但只在实际空闲（隐藏点态）时触发。
        /// </summary>
        public bool TrimMemoryWhenIdle = true;

        /// <summary>剪贴板识别到的内容最长保留多少秒（0 表示用默认 12 秒）。</summary>
        public int ClipboardTtlSeconds = 12;

        public IsletSettings Clone()
        {
            IsletSettings c = new IsletSettings();
            c.MediaEnabled = MediaEnabled;
            c.TimerEnabled = TimerEnabled;
            c.ProgressEnabled = ProgressEnabled;
            c.HealthEnabled = HealthEnabled;
            c.ClipboardEnabled = ClipboardEnabled;
            c.PrivacyEnabled = PrivacyEnabled;
            c.BluetoothEnabled = BluetoothEnabled;
            c.Position = Position;
            c.Opacity = Opacity;
            c.FreeX = FreeX;
            c.FreeY = FreeY;
            c.HideInFullscreen = HideInFullscreen;
            c.AutorunEnabled = AutorunEnabled;
            c.TrimMemoryWhenIdle = TrimMemoryWhenIdle;
            c.ClipboardTtlSeconds = ClipboardTtlSeconds;
            return c;
        }

        /// <summary>当前启用的数据源数量，用于设置页摘要与自检。</summary>
        public int EnabledSourceCount
        {
            get
            {
                int n = 0;
                if (MediaEnabled) n++;
                if (TimerEnabled) n++;
                if (ProgressEnabled) n++;
                if (HealthEnabled) n++;
                if (ClipboardEnabled) n++;
                if (PrivacyEnabled) n++;
                if (BluetoothEnabled) n++;
                return n;
            }
        }

        public bool SourceEnabled(IslandSource source)
        {
            switch (source)
            {
                case IslandSource.Media: return MediaEnabled;
                case IslandSource.Timer: return TimerEnabled;
                case IslandSource.Progress: return ProgressEnabled;
                case IslandSource.Health: return HealthEnabled;
                case IslandSource.Clipboard: return ClipboardEnabled;
                case IslandSource.Privacy: return PrivacyEnabled;
                default: return true;
            }
        }
    }
}
