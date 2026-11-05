// Cadence -- 量「窗口每秒能真正移动几次 / WPF 每秒真正合成几次帧」。
//
// 为什么单独写一个：上一版 DragCost 的端到端那部分不可信 ——
// CompositionTarget.Rendering 是**按需**触发的，窗口静止时它根本不开帧，
// 于是量出「静止 33fps、移动 57fps」这种自相矛盾的数字。
//
// 这次要回答的是一个具体问题：
//   「把拖动跟随从 MouseMove 收到 16ms DispatcherTimer，更新次数是不是明显变少了？」
// 如果是，那用户感觉「更掉帧」就说得通：位置更新变稀疏，动作变成一跳一跳。
//
// 量四个阶段，每段 2 秒，用真实消息循环（不手动泵），窗口是分层 + 投影（和岛同款）：
//   P1 Rendering 空转          → 纯静止时 WPF 合成多少帧/秒
//   P2 16ms 定时器 + SetWindowPos + 数 Rendering
//   P3 16ms 定时器 + Left/Top   + 数 Rendering
//   P4 16ms 定时器 + SetWindowPos，不数 Rendering → 定时器自己的节拍
//
// 用法：Cadence.exe            （每段 2000ms）
//      Cadence.exe 3000

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

internal static class Cadence
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, uint uFlags);

    private const uint SWP_FLAGS = 0x0001 | 0x0004 | 0x0010 | 0x0400;   // NOSIZE|NOZORDER|NOACTIVATE|NOSENDCHANGING

    private static StringBuilder _report = new StringBuilder();
    private static int _segMs = 2000;

    private static Window _win;
    private static IntPtr _hwnd;
    private static double _scale = 1.0;

    private static int _phase;
    private static int _renderTicks;
    private static int _moveTicks;
    private static double _maxGapMs;
    private static double _lastGapStamp;
    private static DispatcherTimer _mover;
    private static DispatcherTimer _segment;
    private static EventHandler _onRender;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args != null && args.Length > 0)
        {
            int v;
            if (int.TryParse(args[0], out v) && v >= 500 && v <= 20000) _segMs = v;
        }

        _scale = DpiScale();
        _report.AppendLine("CADENCE: segment " + _segMs + " ms, dpi=" + _scale);

        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (s, e) =>
        {
            try
            {
                BuildWindow();
                _phase = 0;
                EnterPhase();
                StartSegmentTimer();
            }
            catch (Exception ex)
            {
                _report.AppendLine("FATAL: " + ex);
                app.Shutdown();
            }
        };
        app.Run();

        string text = _report.ToString();
        Console.Write(text);
        try
        {
            System.IO.File.WriteAllText(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cadence.txt"),
                text, new UTF8Encoding(false));
        }
        catch (Exception) { }
        return 0;
    }

    private static double DpiScale()
    {
        try
        {
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
            {
                double s = g.DpiX / 96.0;
                return s > 0 ? s : 1.0;
            }
        }
        catch (Exception) { return 1.0; }
    }

    /// <summary>和岛同款的窗口：分层 + 圆角玻璃 + 同参数投影。</summary>
    private static void BuildWindow()
    {
        _win = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Width = 430,
            Height = 62,
            Left = 400,
            Top = 400,
            ShowActivated = false
        };

        Border shell = new Border
        {
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(Color.FromArgb(0x70, 0xFC, 0xFD, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)),
            Effect = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.55,
                Color = Colors.Black
            }
        };
        _win.Content = shell;
        _win.Show();
        _hwnd = new WindowInteropHelper(_win).Handle;
    }

    private static void StartSegmentTimer()
    {
        _segment = new DispatcherTimer(DispatcherPriority.Send);
        _segment.Interval = TimeSpan.FromMilliseconds(_segMs);
        _segment.Tick += (s, e) =>
        {
            _segment.Stop();
            LeavePhase();

            _phase++;
            if (_phase > 3)
            {
                Application.Current.Shutdown();
                return;
            }
            EnterPhase();
            StartSegmentTimer();
        };
        _segment.Start();
    }

    private static void EnterPhase()
    {
        _renderTicks = 0;
        _moveTicks = 0;
        _maxGapMs = 0;
        _lastGapStamp = 0;
        _report.AppendLine();
        _report.AppendLine("---- P" + (_phase + 1) + ": " + PhaseName(_phase) + " ----");
        _report.AppendLine("start=" + DateTime.Now.ToString("HH:mm:ss.fff"));

        // 所有阶段都数 Rendering —— 除了 P4（专门看定时器自己）
        if (_phase != 3)
        {
            _onRender = (s, e) =>
            {
                double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * 1000.0;
                if (_lastGapStamp > 0)
                {
                    double gap = now - _lastGapStamp;
                    if (gap > _maxGapMs) _maxGapMs = gap;
                }
                _lastGapStamp = now;
                _renderTicks++;
            };
            CompositionTarget.Rendering += _onRender;
        }

        if (_phase >= 1 && _phase <= 3)
        {
            _mover = new DispatcherTimer(DispatcherPriority.Render);
            _mover.Interval = TimeSpan.FromMilliseconds(16);
            _mover.Tick += (s, e) =>
            {
                _moveTicks++;
                double x = 400 + (_moveTicks % 240);
                double y = 400 + (_moveTicks % 40);

                if (_phase == 1 || _phase == 3)
                {
                    SetWindowPos(_hwnd, IntPtr.Zero,
                                 (int)Math.Round(x * _scale),
                                 (int)Math.Round(y * _scale),
                                 0, 0, SWP_FLAGS);
                }
                else if (_phase == 2)
                {
                    _win.BeginAnimation(Window.LeftProperty, null);
                    _win.BeginAnimation(Window.TopProperty, null);
                    _win.Left = x;
                    _win.Top = y;
                }
            };
            _mover.Start();
        }
    }

    private static void LeavePhase()
    {
        if (_mover != null) { _mover.Stop(); _mover = null; }
        if (_onRender != null) { CompositionTarget.Rendering -= _onRender; _onRender = null; }

        double secs = _segMs / 1000.0;
        _report.AppendLine("  render frames : " + _renderTicks
                           + "  (" + (_renderTicks / secs).ToString("F1") + "/s)"
                           + "  maxGap=" + _maxGapMs.ToString("F1") + "ms");
        _report.AppendLine("  window moves  : " + _moveTicks
                           + "  (" + (_moveTicks / secs).ToString("F1") + "/s)");
        if (_renderTicks > 0 && _moveTicks > 0)
        {
            _report.AppendLine("  moves per frame: "
                               + ((double)_moveTicks / _renderTicks).ToString("F2"));
        }
        _report.AppendLine("end=" + DateTime.Now.ToString("HH:mm:ss.fff"));
    }

    private static string PhaseName(int p)
    {
        switch (p)
        {
            case 0: return "render only (idle)";
            case 1: return "move by SetWindowPos @16ms";
            case 2: return "move by Left/Top @16ms";
            default: return "timer cadence only (no render count)";
        }
    }
}
