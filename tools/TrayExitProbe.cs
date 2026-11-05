using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Islet.App;
using Islet.App.Services;

namespace Islet.Probe
{
    /// <summary>
    /// 诊断用（不参与正式构建）。回答「点托盘里的退出为什么没反应」。
    ///
    /// 做法：完全按 IsletHost.Start() 的方式装配一个真 TrayIcon，
    /// 再**用代码触发它的 ExitRequested**（与用户点菜单项走的是同一条路），
    /// 逐步落盘，看是事件没挂上、还是退出后进程没死。
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
            _report = Path.Combine(dir, "trayprobe.txt");
            try { File.Delete(_report); } catch (Exception) { }

            Log("PROBE_START");

            try
            {
                Core.MemoryTrimmer.TuneForBackgroundApp();

                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log("UI_EXCEPTION " + e.Exception.GetType().Name + ": " + e.Exception.Message);
                    e.Handled = true;
                };

                app.Startup += (s, e) =>
                {
                    try
                    {
                        Log("STARTUP_ENTER");

                        TrayIcon tray = new TrayIcon();
                        int exitHits = 0;
                        tray.ExitRequested += (s2, e2) =>
                        {
                            exitHits++;
                            Log("EXIT_REQUESTED_FIRED hits=" + exitHits);
                            Application.Current.Shutdown();
                            Log("EXIT_REQUESTED_SHUTDOWN_CALLED");
                        };
                        tray.Show();
                        Log("TRAY_SHOWN");

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(1500)
                        };
                        t.Tick += (s3, e3) =>
                        {
                            t.Stop();
                            Log("SIMULATE_CLICK_BEGIN");
                            // 反射调用 Raise(ExitRequested)：等价于用户点「退出」
                            try
                            {
                                System.Reflection.MethodInfo mi = typeof(TrayIcon).GetMethod(
                                    "Raise",
                                    System.Reflection.BindingFlags.NonPublic |
                                    System.Reflection.BindingFlags.Static);
                                Log("RAISE_FOUND=" + (mi != null));
                                System.Reflection.FieldInfo fi = typeof(TrayIcon).GetField(
                                    "ExitRequested",
                                    System.Reflection.BindingFlags.NonPublic |
                                    System.Reflection.BindingFlags.Instance);
                                Log("FIELD_FOUND=" + (fi != null));
                                if (mi != null && fi != null)
                                {
                                    object h = fi.GetValue(tray);
                                    Log("HANDLER_NULL=" + (h == null));
                                    mi.Invoke(null, new object[] { h });
                                    Log("RAISE_INVOKED");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log("RAISE_ERROR " + ex.GetType().Name + ": " + ex.Message
                                    + (ex.InnerException != null ? " / inner=" + ex.InnerException.Message : ""));
                            }
                        };
                        t.Start();
                        Log("TIMER_ARMED");
                    }
                    catch (Exception ex)
                    {
                        Log("STARTUP_ERROR " + ex);
                        app.Shutdown();
                    }
                };

                app.Exit += (s, e) => Log("APP_EXIT_EVENT");
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
