using System;
using System.Runtime.InteropServices;

namespace Islet.App.Interop
{
    /// <summary>Win32 / DWM 互操作。集中放置，便于审查。</summary>
    internal static class NativeMethods
    {
        // ---- 窗口样式 ----
        public const int GWL_EXSTYLE = -20;

        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;

        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;
        public const int WM_HOTKEY = 0x0312;

        // ---- DWM ----
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        public const int DWMWCP_ROUND = 2;
        public const int DWMSBT_TRANSIENTWINDOW = 3;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_HIDE = 0;

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

        [StructLayout(LayoutKind.Sequential)]
        public struct MARGINS
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int MOD_ALT = 0x0001;
        public const int MOD_CONTROL = 0x0002;
        public const int MOD_SHIFT = 0x0004;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        // ---- 全屏检测 ----
        public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;

        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);

        // ---- 直接移动窗口（拖动跟手用）----
        //
        // 为什么不用 WPF 的 Window.Left / Top：那两个是**两个**依赖属性，
        // 各自会走一次 SetWindowPos，等于一帧搬两次窗；实测（tools\DragCost）
        // 一次 Left+Top 赋值 0.91ms，单次 SetWindowPos 只要 0.52ms。
        // 拖动是 16ms 一帧的硬要求，这一项就占了 5.5% 的帧预算，减半很值。
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                               int X, int Y, int cx, int cy, uint uFlags);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOSENDCHANGING = 0x0400;

        // ---- DPI ----
        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>Windows 11 才支持圆角与云母背景；Win10 静默跳过。</summary>
        public static bool IsWindows11OrGreater
        {
            get
            {
                Version v = Environment.OSVersion.Version;
                return v.Major > 10 || (v.Major == 10 && v.Build >= 22000);
            }
        }
    }
}
