// DragCost -- 量「拖动窗口」到底贵在哪。
//
// 背景：IslandWindow 用 AllowsTransparency=true（分层窗口）画圆角玻璃 + 投影。
// 用户反馈「拖动时有点掉帧」。可能的账有三笔，必须分开量，不能猜：
//   1. WPF 的 Window.Left/Top 赋值 —— 会不会一帧里做两次 SetWindowPos；
//   2. 分层窗口（AllowsTransparency）本身的 SetWindowPos 成本；
//   3. DropShadowEffect（BlurRadius 26）对每帧的影响。
//
// 量法有两层：
//   A. 微基准：在紧循环里反复移动，量单次开销（这是**阻塞 UI 线程**的那部分）。
//   B. 端到端：用 16ms 定时器像 FollowCursor 那样移动窗口，同时用
//      CompositionTarget.Rendering 数真实帧，量「每秒几帧 / 最大帧间隔」。
//      B 才是用户能感觉到的东西 —— A 快不代表不掉帧，反之亦然。
//
// 用法：DragCost.exe            （默认每档 2 秒）
//      DragCost.exe 3000        （每档 3 秒）
//
// 全部输出 ASCII：这个受限会话里 Console.OutputEncoding 会抛异常。

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

internal static class DragCost
{
    // ---- Win32 ----
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSENDCHANGING = 0x0400;

    private const uint SWP_FLAGS =
        SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING;

    private sealed class Variant
    {
        public string Label;
        public bool Layered;
        public bool Shadow;
        public double W = 430;
        public double H = 62;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        int ms = 2000;
        if (args != null && args.Length > 0)
        {
            int v;
            if (int.TryParse(args[0], out v) && v >= 500 && v <= 20000) ms = v;
        }

        StringBuilder report = new StringBuilder();
        report.AppendLine("DRAGCOST: per-variant " + ms + " ms");
        report.AppendLine("dpi=" + DpiScale());

        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (s, e) =>
        {
            try
            {
                Variant[] variants = new Variant[]
                {
                    new Variant { Label = "layered+shadow", Layered = true,  Shadow = true  },
                    new Variant { Label = "layered",        Layered = true,  Shadow = false },
                    new Variant { Label = "plain",          Layered = false, Shadow = false }
                };

                foreach (Variant v in variants) RunVariant(v, ms, report);
            }
            catch (Exception ex)
            {
                report.AppendLine("FATAL: " + ex);
            }
            finally
            {
                app.Shutdown();
            }
        };
        app.Run();

        string text = report.ToString();
        Console.Write(text);
        try
        {
            string outPath = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "dragcost.txt");
            System.IO.File.WriteAllText(outPath, text, new UTF8Encoding(false));
        }
        catch (Exception) { }
        return 0;
    }

    private static double DpiScale()
    {
        try
        {
            using (System.Drawing.Graphics g =
                   System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            {
                return g.DpiX / 96.0;
            }
        }
        catch (Exception) { return 1.0; }
    }

    // ---------------------------------------------------------------
    // 一个变体：建窗 → 微基准 A/B/C → 端到端 60Hz 移动
    // ---------------------------------------------------------------
    private static void RunVariant(Variant v, int ms, StringBuilder report)
    {
        Window win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = v.Layered,
            Background = v.Layered ? Brushes.Transparent : Brushes.White,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Width = v.W,
            Height = v.H,
            Left = 150,
            Top = 150,
            ShowActivated = false
        };

        Border shell = new Border
        {
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(Color.FromArgb(0x70, 0xFC, 0xFD, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF))
        };
        if (v.Shadow)
        {
            shell.Effect = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.55,
                Color = Colors.Black
            };
        }
        win.Content = shell;

        win.Show();
        Pump(win.Dispatcher);   // 让首帧画出来

        IntPtr hwnd = new WindowInteropHelper(win).Handle;
        double scale = DpiScale();
        if (scale <= 0) scale = 1.0;

        report.AppendLine();
        report.AppendLine("==== " + v.Label + " ====");

        // ---- A: Left/Top 赋值（现在的 AnimateTo(animate:false) 走这条）----
        const int N = 200;
        double t0 = win.Left, t1 = win.Top;
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
        {
            win.Left = t0 + (i % 40);
            win.Top = t1 + (i % 8);
        }
        sw.Stop();
        double a = sw.Elapsed.TotalMilliseconds / N;
        Pump(win.Dispatcher);

        // ---- B: 单次 SetWindowPos ----
        sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
        {
            SetWindowPos(hwnd, IntPtr.Zero,
                         (int)Math.Round((150 + i % 40) * scale),
                         (int)Math.Round((150 + i % 8) * scale),
                         0, 0, SWP_FLAGS);
        }
        sw.Stop();
        double b = sw.Elapsed.TotalMilliseconds / N;

        // ---- C: BeginAnimation(null) + Left/Top（AnimateTo 现在就是这三步）----
        sw = Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
        {
            win.BeginAnimation(Window.LeftProperty, null);
            win.BeginAnimation(Window.TopProperty, null);
            win.Left = t0 + (i % 40);
            win.Top = t1 + (i % 8);
        }
        sw.Stop();
        double c = sw.Elapsed.TotalMilliseconds / N;
        Pump(win.Dispatcher);

        report.AppendLine("  A Left/Top assign      : " + a.ToString("F3") + " ms/move");
        report.AppendLine("  B SetWindowPos x1      : " + b.ToString("F3") + " ms/move");
        report.AppendLine("  C BeginAnimation+Left  : " + c.ToString("F3") + " ms/move");
        report.AppendLine("  A per 16.6ms frame     : " + (a / 16.6 * 100).ToString("F1") + " %");
        report.AppendLine("  B per 16.6ms frame     : " + (b / 16.6 * 100).ToString("F1") + " %");
        report.AppendLine("  C per 16.6ms frame     : " + (c / 16.6 * 100).ToString("F1") + " %");

        // ---- D: 端到端 60Hz 移动，数真实帧 ----
        FpsResult idle = MeasureFps(win, ms, false, MoveMode.None, scale, hwnd);
        FpsResult byDp = MeasureFps(win, ms, true, MoveMode.LeftTop, scale, hwnd);
        FpsResult bySwp = MeasureFps(win, ms, true, MoveMode.SetWindowPos, scale, hwnd);

        report.AppendLine("  idle    : fps=" + idle.Fps.ToString("F1")
                          + " maxGap=" + idle.MaxGapMs.ToString("F0") + "ms"
                          + " over>25ms=" + idle.LongFrames);
        report.AppendLine("  move DP : fps=" + byDp.Fps.ToString("F1")
                          + " maxGap=" + byDp.MaxGapMs.ToString("F0") + "ms"
                          + " over>25ms=" + byDp.LongFrames);
        report.AppendLine("  move SWP: fps=" + bySwp.Fps.ToString("F1")
                          + " maxGap=" + bySwp.MaxGapMs.ToString("F0") + "ms"
                          + " over>25ms=" + bySwp.LongFrames);

        win.Close();
        Pump(win.Dispatcher);
    }

    private enum MoveMode { None, LeftTop, SetWindowPos }

    private sealed class FpsResult
    {
        public double Fps;
        public double MaxGapMs;
        public int LongFrames;
    }

    /// <summary>
    /// 用 16ms 定时器像 FollowCursor 那样挪窗口，同时数 CompositionTarget.Rendering
    /// 的真实帧。Rendering 每帧一次，所以「2 秒里来了多少帧」就是真实帧率，
    /// 相邻两次的间隔最大值就是最卡的那一下。
    /// </summary>
    private static FpsResult MeasureFps(Window win, int ms, bool move,
                                        MoveMode mode, double scale, IntPtr hwnd)
    {
        FpsResult r = new FpsResult();

        Stopwatch clock = Stopwatch.StartNew();
        double last = 0;
        int frames = 0;
        double maxGap = 0;
        int longFrames = 0;

        EventHandler onFrame = null;
        onFrame = (s, e) =>
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (last > 0)
            {
                double gap = now - last;
                if (gap > maxGap) maxGap = gap;
                if (gap > 25) longFrames++;
            }
            last = now;
            frames++;
        };
        CompositionTarget.Rendering += onFrame;

        int tick = 0;
        DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Render);
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (s, e) =>
        {
            if (!move) return;
            tick++;
            // 在 240px 的区间里来回移动，模拟真实拖动
            double x = 150 + (tick % 240);
            double y = 150 + (tick % 40);
            if (mode == MoveMode.LeftTop)
            {
                win.BeginAnimation(Window.LeftProperty, null);
                win.BeginAnimation(Window.TopProperty, null);
                win.Left = x;
                win.Top = y;
            }
            else if (mode == MoveMode.SetWindowPos)
            {
                SetWindowPos(hwnd, IntPtr.Zero,
                             (int)Math.Round(x * scale),
                             (int)Math.Round(y * scale),
                             0, 0, SWP_FLAGS);
            }
        };

        Stopwatch run = Stopwatch.StartNew();
        timer.Start();

        // 用一个局部泵跑满 ms：不能关 dispatcher（后面还要用这个窗），
        // 所以每 1ms 抽一次 DoEvents，让 Rendering 与定时器都能正常触发。
        while (run.Elapsed.TotalMilliseconds < ms)
        {
            DoEvents(win.Dispatcher);
            System.Threading.Thread.Sleep(1);
        }

        timer.Stop();
        CompositionTarget.Rendering -= onFrame;

        double total = clock.Elapsed.TotalMilliseconds;
        r.Fps = frames / (total / 1000.0);
        r.MaxGapMs = maxGap;
        r.LongFrames = longFrames;
        return r;
    }

    private static void DoEvents(Dispatcher d)
    {
        DispatcherFrame frame = new DispatcherFrame();
        d.BeginInvoke(DispatcherPriority.Background,
                      new Action(() => { frame.Continue = false; }));
        Dispatcher.PushFrame(frame);
    }

    private static void Pump(Dispatcher d)
    {
        for (int i = 0; i < 3; i++)
        {
            DoEvents(d);
            System.Threading.Thread.Sleep(20);
        }
    }
}
