using System;
using System.Runtime.InteropServices;
using Islet.App.Interop;

namespace Islet.App.Services
{
    /// <summary>
    /// 判断当前是否应隐身。全屏、演示、锁屏、屏幕保护时一律隐藏。
    /// 双重判定：系统通知状态 + 前台窗口矩形覆盖。
    /// </summary>
    internal sealed class FullscreenWatcher
    {
        /// <summary>true 表示现在应当隐藏胶囊。</summary>
        public bool ShouldHide()
        {
            // 第一重：系统通知状态。全屏应用/演示/锁屏都会返回非 ACCEPTS 值
            try
            {
                int state;
                if (NativeMethods.SHQueryUserNotificationState(out state) == 0)
                {
                    // 1 = 忙, 2 = 全屏运行, 3 = 演示模式, 4 = 锁屏, 5 = 正常
                    if (state == 2 || state == 3 || state == 4) return true;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            // 第二重：前台窗口是否铺满其所在显示器（兜住部分游戏/播放器）
            return IsForegroundFullscreen();
        }

        private static bool IsForegroundFullscreen()
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == GetShellWindow()) return false; // 桌面本身不算

            RECT win;
            if (!GetWindowRect(hwnd, out win)) return false;

            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;

            MONITORINFO mi = new MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(mon, ref mi)) return false;

            return win.Left <= mi.rcMonitor.Left
                && win.Top <= mi.rcMonitor.Top
                && win.Right >= mi.rcMonitor.Right
                && win.Bottom >= mi.rcMonitor.Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        private const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }
    }
}
