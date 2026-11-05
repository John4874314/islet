using System;
using System.Windows;
using System.Windows.Interop;
using Islet.App.Interop;
using Islet.App.Services;

namespace Islet.App
{
    /// <summary>应用入口。单实例、全局热键、异常兜底。</summary>
    internal static class Program
    {
        private const int HotkeyId = 0x15E7;
        private const int HotkeyVolumeId = 0x15E8;

        private static IsletHost _host;
        private static System.Threading.Mutex _mutex;
        private static HwndSource _hotkeySource;

        [STAThread]
        private static void Main(string[] args)
        {
            // 必须在任何托管堆分配之前调：关并发 GC、选 Workstation GC
            Core.MemoryTrimmer.TuneForBackgroundApp();

            if (args != null && args.Length >= 1 && args[0] == "--selftest")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.Run(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--hosttest")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                int secs = args.Length >= 3 ? int.Parse(args[2]) : 5;
                Environment.Exit(SelfTest.RunHost(dir, secs));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--clipboardtest")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunClipboard(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--settingstest")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunSettings(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--memorytest")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                int secs = args.Length >= 3 ? int.Parse(args[2]) : 20;
                Environment.Exit(SelfTest.RunMemory(dir, secs));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--settingse2e")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunSettingsE2E(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--memprofile")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunMemProfile(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--memlab")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(MemLab.Run(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--cpubench")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                int secs = args.Length >= 3 ? int.Parse(args[2]) : 20;
                Environment.Exit(CpuBench.Run(dir, secs));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--privacytest")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunPrivacy(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--privacye2e")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunPrivacyE2E(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--privacyfp")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunPrivacyFalsePositive(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--opacitytest")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunOpacity(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--volumetest")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunVolume(dir));
                return;
            }

            if (args != null && args.Length >= 1 && args[0] == "--volumewrite")
            {
                string dir = args.Length >= 2 ? args[1] : ".";
                Environment.Exit(SelfTest.RunVolumeWrite(dir));
                return;
            }

            bool createdNew;
            _mutex = new System.Threading.Mutex(true, "Islet.SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("浮屿已在运行。", "浮屿 Islet",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Error("AppDomain 未捕获异常", e.ExceptionObject as Exception);
            };

            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Error("UI 线程异常", e.Exception);
                e.Handled = true; // 单点故障不得拖垮外壳
            };

            app.Startup += (s, e) =>
            {
                Log.Info("应用启动");
                try
                {
                    _host = new IsletHost();
                    _host.Start();
                    Log.Info("宿主已启动");
                }
                catch (Exception ex)
                {
                    Log.Error("宿主启动失败", ex);
                    throw;
                }

                try
                {
                    RegisterHotkey();
                }
                catch (Exception ex)
                {
                    Log.Error("热键注册失败", ex);
                }
            };

            app.Exit += (s, e) =>
            {
                Log.Info("应用退出");
                UnregisterHotkey();
                try { if (_host != null) _host.Dispose(); }
                catch (Exception ex) { Log.Error("释放宿主失败", ex); }
                if (_mutex != null)
                {
                    try { _mutex.ReleaseMutex(); } catch (Exception) { }
                    _mutex.Dispose();
                }
            };

            app.Run();
            Log.Info("消息循环结束");
        }

        // ---- 全局热键：需要一个不可见消息窗口 ----

        private static void RegisterHotkey()
        {
            HwndSourceParameters p = new HwndSourceParameters("IsletHotkeySink")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0
            };
            _hotkeySource = new HwndSource(p);
            _hotkeySource.AddHook(HotkeyWndProc);

            bool ok = NativeMethods.RegisterHotKey(
                _hotkeySource.Handle, HotkeyId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
                0x49); // I

            if (!ok)
                System.Diagnostics.Debug.WriteLine("[Islet] 热键注册失败（可能被占用）");

            // Ctrl+Alt+V：打开应用音量面板。
            // 注册失败不影响主功能，只是少一个快捷入口。
            bool okVol = NativeMethods.RegisterHotKey(
                _hotkeySource.Handle, HotkeyVolumeId,
                NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
                0x56); // V

            if (!okVol)
                System.Diagnostics.Debug.WriteLine("[Islet] 音量热键注册失败（可能被占用）");
        }

        private static void UnregisterHotkey()
        {
            if (_hotkeySource != null)
            {
                NativeMethods.UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
                NativeMethods.UnregisterHotKey(_hotkeySource.Handle, HotkeyVolumeId);
                _hotkeySource.Dispose();
                _hotkeySource = null;
            }
        }

        private static IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HotkeyId)
                {
                    handled = true;
                    if (_host != null) _host.ToggleVisible();
                }
                else if (id == HotkeyVolumeId)
                {
                    handled = true;
                    if (_host != null) _host.ShowVolumePanel();
                }
            }
            return IntPtr.Zero;
        }
    }
}
