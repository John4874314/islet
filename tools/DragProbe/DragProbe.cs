// DragProbe -- 量化「拖动跟手性」。
//
// 目的：拖动顿挫是主观感受，必须先变成数字，否则每次改完都只能靠「感觉好点了吗」。
//
// 做法：
//   1. 找到 Islet 的窗口（它是 WS_EX_TOOLWINDOW，Process.MainWindowHandle 恒为 0，
//      必须用 EnumWindows 按进程 id 筛）；
//   2. 把光标移到窗口**空白处**（玻璃中部，避开按钮/滑块），按下；
//   3. 以固定步长平移光标 N 步，每步之后采样窗口位置；
//   4. 期望位置 = 起始位置 + 光标累计位移；实测位置 - 期望位置 = 跟手误差。
//
// 判据：
//   · 误差全程 ≈ 0          → 严格跟手（拖动时不做位移动画）
//   · 误差随速度线性增大     → 每帧重启 EaseOut 动画的典型症状
//   · 误差不为 0 但恒定      → 只是坐标取整/DPI 换算，无感
//
// 用法：
//   DragProbe.exe [--steps 40] [--step 8] [--settle 20] [--dry]
//
// --dry 只打印窗口矩形和将要按下的点，不注入任何输入。默认先跑 dry 看一眼。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class DragProbe
{
    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    const int GWL_EXSTYLE = -20;
    const int WS_EX_LAYERED = 0x00080000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_NOACTIVATE = 0x08000000;

    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    // ---------------- 主流程 ----------------

    static int Main(string[] args)
    {
        int steps = GetInt(args, "--steps", 40);
        int stepPx = GetInt(args, "--step", 8);
        int settle = GetInt(args, "--settle", 20);
        bool dry = HasFlag(args, "--dry");

        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // ⚠️ 必须在**任何**窗口坐标查询之前声明 DPI 感知。
        //
        // 本机是 2560×1600 @150%。未声明时进程被 DWM 当成 96-DPI 应用，
        // GetWindowRect 返回的是**虚拟化后**的逻辑坐标（实测 731,50 287×62），
        // 而 SetCursorPos 收的是**物理像素**。两者混用会把抓取点算到岛外面，
        // 表现为「按下去了但窗口纹丝不动」—— 我第一版就栽在这。
        //
        // 优先用 per-monitor v2，退回系统级 DPI 感知，都不行就继续跑（坐标会偏）。
        if (SetThreadDpiAwarenessContext(new IntPtr(-4)) == IntPtr.Zero)
            SetProcessDPIAware();

        IntPtr hwnd = FindIslandWindow();
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine("找不到 Islet 窗口。先启动它。");
            return 2;
        }

        RECT r0;
        GetWindowRect(hwnd, out r0);
        int w = r0.Right - r0.Left;
        int h = r0.Bottom - r0.Top;
        Console.WriteLine("hwnd=0x" + hwnd.ToInt64().ToString("X"));
        Console.WriteLine("窗口矩形: " + r0.Left + "," + r0.Top + "  " + w + "x" + h);

        // 抓取点：玻璃中部偏右一点。
        // 为什么偏右：圆环恒在窗口最左端，偏右一定落在「空白玻璃」上，
        // 正好同时验证「任意空白区可拖」这个需求。
        int grabX = r0.Left + (int)(w * 0.62);
        int grabY = r0.Top + h / 2;
        Console.WriteLine("抓取点: " + grabX + "," + grabY +
                          "（窗口内偏移 " + (grabX - r0.Left) + "," + (grabY - r0.Top) + "）");

        if (dry)
        {
            Console.WriteLine("--dry：只探测，不注入输入。");
            return 0;
        }

        POINT before;
        GetCursorPos(out before);

        // ⚠️ 先验证本进程到底能不能动光标。
        //
        // 在受限的会话/桌面里（例如从某些宿主进程派生的非交互式环境），
        // SetCursorPos 会失败并返回 ERROR_INVALID_PARAMETER(87)，
        // SendInput 却仍然返回 1（它只是把事件塞进队列，不保证送达）。
        // 不先检查的话，探针会看到「窗口一动不动」并报 FAIL，
        // 让人以为是应用的问题 —— 我在这上面浪费过一轮。
        if (!SetCursorPos(grabX, grabY))
        {
            Console.WriteLine();
            Console.WriteLine("无法注入鼠标：SetCursorPos 失败 err=" +
                              Marshal.GetLastWin32Error());
            Console.WriteLine("本进程运行在不可交互的窗口站/桌面，探针测不了拖动。");
            Console.WriteLine("请改用宿主的桌面控制工具（desktop_control）手动拖动验证。");
            return 4;
        }
        GetCursorPos(out before);

        try
        {
            // ---- 按下 ----
            SetCursorPos(grabX, grabY);
            Thread.Sleep(60);
            SendInput(1, new[] { MouseInput(0, 0, MOUSEEVENTF_LEFTDOWN) },
                      Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(80);

            RECT rStart;
            GetWindowRect(hwnd, out rStart);

            Console.WriteLine();
            Console.WriteLine("步  光标dx  期望Left  实测Left   误差   耗时ms");
            Console.WriteLine("---------------------------------------------------");

            var lags = new List<int>();
            var costs = new List<double>();
            Stopwatch sw = new Stopwatch();
            int maxAbsLag = 0;

            for (int i = 1; i <= steps; i++)
            {
                int dx = i * stepPx;
                sw.Reset();
                sw.Start();
                SetCursorPos(grabX + dx, grabY);
                Thread.Sleep(settle);
                sw.Stop();

                RECT r;
                GetWindowRect(hwnd, out r);
                int expect = rStart.Left + dx;
                int lag = r.Left - expect;

                lags.Add(lag);
                costs.Add(sw.Elapsed.TotalMilliseconds);
                if (Math.Abs(lag) > maxAbsLag) maxAbsLag = Math.Abs(lag);

                // 只打前 12 步和后 3 步，中间省略，避免刷屏
                if (i <= 12 || i > steps - 3)
                    Console.WriteLine(string.Format(
                        "{0,3}  {1,6}  {2,8}  {3,8}  {4,5}  {5,7:0.0}",
                        i, dx, expect, r.Left, lag, sw.Elapsed.TotalMilliseconds));
            }

            // ---- 松开 ----
            SendInput(1, new[] { MouseInput(0, 0, MOUSEEVENTF_LEFTUP) },
                      Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(150);

            RECT rEnd;
            GetWindowRect(hwnd, out rEnd);

            int totalDx = steps * stepPx;
            int finalExpect = rStart.Left + totalDx;
            int finalLag = rEnd.Left - finalExpect;

            double sum = 0, worst = 0, total = 0;
            for (int i = 0; i < lags.Count; i++)
            {
                sum += Math.Abs(lags[i]);
                if (costs[i] > worst) worst = costs[i];
                total += costs[i];
            }

            Console.WriteLine();
            Console.WriteLine("-----------------------------------------------");
            Console.WriteLine("总位移      : " + totalDx + " px");
            Console.WriteLine("最大单帧误差: " + maxAbsLag + " px");
            Console.WriteLine("平均误差    : " + (sum / lags.Count).ToString("0.00") + " px");
            Console.WriteLine("落点误差    : " + finalLag + " px   (松手后)");
            Console.WriteLine("单步耗时    : 平均 " + (total / costs.Count).ToString("0.0") +
                              " ms / 最差 " + worst.ToString("0.0") + " ms");
            Console.WriteLine("采样间隔    : " + settle + " ms（注入 + 等待 + 读矩形）");
            Console.WriteLine();

            if (maxAbsLag <= 2 && Math.Abs(finalLag) <= 2)
                Console.WriteLine("判定: PASS —— 严格跟手，没有可见滞后。");
            else if (maxAbsLag <= 12)
                Console.WriteLine("判定: WARN —— 有 " + maxAbsLag + " px 滞后，肉眼可能勉强可感。");
            else
                Console.WriteLine("判定: FAIL —— " + maxAbsLag + " px 滞后，这就是顿挫。");
        }
        finally
        {
            // 无论成败都把光标还回去，别把用户的鼠标留在半路
            SetCursorPos(before.X, before.Y);
        }

        return 0;
    }

    // ---------------- 辅助 ----------------

    static INPUT MouseInput(int dx, int dy, uint flags)
    {
        INPUT inp = new INPUT();
        inp.type = INPUT_MOUSE;
        inp.mi.dx = dx;
        inp.mi.dy = dy;
        inp.mi.dwFlags = flags;
        return inp;
    }

    static Process FindIslet()
    {
        Process[] all = Process.GetProcessesByName("Islet");
        return all.Length > 0 ? all[0] : null;
    }

    /// <summary>
    /// 按「窗口类名 + 分层 + 工具窗口 + 不激活」找岛。
    ///
    /// ⚠️ 不能用 Process.GetProcessesByName("Islet")：实测在本机的某些执行上下文里
    /// 进程枚举会被裁剪（只返回 10 个进程，Islet 不在其中），
    /// 而 EnumWindows 是纯窗口层查询，不受影响。
    /// 岛的窗口类名形如 HwndWrapper[Islet.exe;;{guid}]，取前缀匹配即可。
    /// </summary>
    static IntPtr FindIslandWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;

            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return true;

            int ex = GetWindowLong(h, GWL_EXSTYLE);
            bool layered = (ex & WS_EX_LAYERED) != 0;
            bool tool = (ex & WS_EX_TOOLWINDOW) != 0;
            bool noact = (ex & WS_EX_NOACTIVATE) != 0;
            if (!layered || !tool || !noact) return true;

            StringBuilder cn = new StringBuilder(256);
            if (GetClassName(h, cn, 256) == 0) return true;
            string cls = cn.ToString();
            if (cls.IndexOf("HwndWrapper[Islet.exe", StringComparison.OrdinalIgnoreCase) < 0)
                return true;

            found = h;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    static int GetInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == name)
            {
                int v;
                if (int.TryParse(args[i + 1], out v)) return v;
            }
        return fallback;
    }

    static bool HasFlag(string[] args, string name)
    {
        foreach (string a in args) if (a == name) return true;
        return false;
    }
}
