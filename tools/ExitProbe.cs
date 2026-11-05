using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Islet.App;
using Islet.App.Views;
using Islet.Core;

namespace Islet.Probe
{
    /// <summary>
    /// 诊断用（不参与正式构建）。回答两个问题：
    ///
    ///   A. 「点隐藏点无反应」：槽位为空时 Render(null) 会走 ShowDot()，此时
    ///      SetExpanded(true) 因为 _current == null 又调回 ShowDot() —— 展开被吞掉。
    ///      本探针直接量窗口尺寸来证明这一点。
    ///
    ///   B. 「退出退不掉」：复刻 Program.Main 的 Exit 处理器（含 _host.Dispose()），
    ///      逐步落盘，看卡在哪一行。
    ///
    /// 每条日志立即 append，进程若卡死，最后一行就是卡住的位置。
    /// </summary>
    internal static class ExitProbe
    {
        private static string _report;
        private static readonly object Gate = new object();

        private static void Log(string msg)
        {
            lock (Gate)
            {
                try
                {
                    File.AppendAllText(_report,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch (Exception) { }
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            string dir = args.Length >= 1 ? args[0] : ".";
            Directory.CreateDirectory(dir);
            _report = Path.Combine(dir, "exitprobe.txt");
            try { File.Delete(_report); } catch (Exception) { }

            Log("PROBE_START");

            try
            {
                MemoryTrimmer.TuneForBackgroundApp();

                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                IsletHost host = null;
                IslandWindow probeWindow = null;

                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log("UI_EXCEPTION " + e.Exception.GetType().Name + ": " + e.Exception.Message);
                    e.Handled = true;
                };

                app.Startup += (s, e) =>
                {
                    try
                    {
                        // ---- A. 隐藏点态下「展开」是否被吞 ----
                        Log("A_BEGIN");
                        probeWindow = new IslandWindow();
                        probeWindow.Show();
                        probeWindow.ShowNoActivate();
                        Log("A_WINDOW_SHOWN");

                        probeWindow.Render(null);   // 槽位空 -> 隐藏点
                        Log("A_RENDER_NULL done, IsExpanded=" + probeWindow.IsExpanded);
                        Log("A_SIZE_BEFORE=" + probeWindow.ActualWidth + "x" + probeWindow.ActualHeight);

                        // 模拟「鼠标进入」——这正是用户点击/悬停时发生的事
                        probeWindow.SetExpanded(true);
                        Log("A_AFTER_SETEXPANDED IsExpanded=" + probeWindow.IsExpanded);
                        Log("A_SIZE_AFTER=" + probeWindow.ActualWidth + "x" + probeWindow.ActualHeight);

                        // 再模拟一次带内容的展开，作为对照
                        probeWindow.Render(new IslandEvent("probe.card", IslandSource.Timer, "对照卡")
                            .WithSubtitle("应当展开为卡片"));
                        Log("A_WITH_EVENT_SIZE=" + probeWindow.ActualWidth + "x" + probeWindow.ActualHeight);

                        probeWindow.Close();
                        probeWindow = null;
                        Log("A_END");

                        // ---- B. 真实宿主 + Program 同款 Exit 处理器 ----
                        Log("B_BEGIN");
                        host = new IsletHost();
                        Log("B_HOST_CTOR");
                        host.Start();
                        Log("B_HOST_STARTED");

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(2000)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            Log("B_BEFORE_SHUTDOWN threads=" +
                                Process.GetCurrentProcess().Threads.Count);
                            Log("B_SHUTDOWN_CALLED");
                            app.Shutdown();
                            Log("B_SHUTDOWN_RETURNED");
                        };
                        t.Start();
                        Log("B_TIMER_ARMED");
                    }
                    catch (Exception ex)
                    {
                        Log("STARTUP_ERROR " + ex);
                        app.Shutdown();
                    }
                };

                // 与 Program.Main 完全一致的退出处理器
                app.Exit += (s, e) =>
                {
                    Log("EXIT_ENTER");
                    Log("EXIT_DISPOSE_BEGIN");
                    try
                    {
                        if (host != null) host.Dispose();
                        Log("EXIT_DISPOSE_OK");
                    }
                    catch (Exception ex)
                    {
                        Log("EXIT_DISPOSE_ERROR " + ex.GetType().Name + ": " + ex.Message);
                    }
                    Log("EXIT_LEAVE");
                };

                Log("RUN_ENTER");
                app.Run();
                Log("RUN_RETURNED");
            }
            catch (Exception ex)
            {
                Log("FATAL " + ex);
            }

            Log("MAIN_EXITING");
        }
    }
}
