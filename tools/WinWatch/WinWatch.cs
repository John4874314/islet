// 盯着岛上窗口的尺寸变化，用来验证「插拔耳机后岛多久更新」。
//
// 岛是 WS_EX_TOOLWINDOW 且不夺焦，Process.MainWindowHandle 拿不到，
// 必须 EnumWindows 按 PID 过滤。
//
// 用法：WinWatch.exe Islet 90 out.log
//   尺寸变化（条 <-> 圆点）就是岛上内容变了，配合时间戳就能量出反应延迟。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class WinWatch
{
    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    private static void Main(string[] args)
    {
        string name = args.Length > 0 ? args[0] : "Islet";
        int seconds = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 60;
        string outPath = args.Length > 2 ? args[2] : "winwatch.log";

        SetThreadDpiAwarenessContext(new IntPtr(-4));

        List<string> log = new List<string>();
        string prev = "";
        DateTime end = DateTime.Now.AddSeconds(seconds);

        while (DateTime.Now < end)
        {
            string sig = Snapshot(name);
            if (sig != prev)
            {
                log.Add("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + sig);
                prev = sig;
            }
            Thread.Sleep(150);
        }

        log.Add("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] 采样结束");
        File.WriteAllLines(outPath, log.ToArray(), new UTF8Encoding(false));
    }

    private static string Snapshot(string name)
    {
        uint[] pids;
        try
        {
            System.Diagnostics.Process[] ps = System.Diagnostics.Process.GetProcessesByName(name);
            pids = new uint[ps.Length];
            for (int i = 0; i < ps.Length; i++) pids[i] = (uint)ps[i].Id;
        }
        catch (Exception) { return "(enum failed)"; }

        if (pids.Length == 0) return "(not running)";

        StringBuilder sb = new StringBuilder();
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (Array.IndexOf(pids, pid) < 0) return true;
            if (!IsWindowVisible(h)) return true;

            RECT r;
            if (!GetWindowRect(h, out r)) return true;

            int w = r.R - r.L, ht = r.B - r.T;
            // 只记尺寸和位置，够判断「岛是圆点还是条/面板」
            if (sb.Length > 0) sb.Append(" | ");
            sb.Append(w).Append('x').Append(ht).Append('@').Append(r.L).Append(',').Append(r.T);
            return true;
        }, IntPtr.Zero);

        return sb.Length == 0 ? "(no visible window)" : sb.ToString();
    }
}
