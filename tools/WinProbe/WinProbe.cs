// 打印指定进程所有「可见」顶层窗口的屏幕矩形（物理像素）。
// 用途：岛窗口是 WS_EX_TOOLWINDOW 且不夺焦，Process.MainWindowHandle 拿不到，
// 必须用 EnumWindows 按 PID 过滤。GUI 验证时用它精确定位，避免猜坐标。
using System;
using System.Runtime.InteropServices;
using System.Text;

internal static class WinProbe
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    private static void Main(string[] args)
    {
        string name = args.Length > 0 ? args[0] : "Islet";

        SetThreadDpiAwarenessContext(new IntPtr(-4));   // PER_MONITOR_AWARE_V2

        uint[] pids = Array.ConvertAll(
            System.Diagnostics.Process.GetProcessesByName(name),
            p => (uint)p.Id);

        if (pids.Length == 0) { Console.WriteLine("进程未运行: " + name); return; }

        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (Array.IndexOf(pids, pid) < 0) return true;
            if (!IsWindowVisible(h)) return true;

            RECT r;
            if (!GetWindowRect(h, out r)) return true;

            StringBuilder cls = new StringBuilder(256);
            GetClassName(h, cls, 256);

            Console.WriteLine(string.Format(
                "pid={0} hwnd={1} rect={2},{3},{4},{5} size={6}x{7} class={8}",
                pid, h, r.L, r.T, r.R, r.B, r.R - r.L, r.B - r.T, cls));
            return true;
        }, IntPtr.Zero);
    }
}
