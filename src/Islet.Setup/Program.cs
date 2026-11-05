using System;
using System.Text;
using System.Windows;

namespace Islet.Setup
{
    /// <summary>
    /// 安装程序入口。
    ///
    /// 两个用途：
    ///   · 正常双击 → 图形安装界面；
    ///   · `--uninstall` → 直接跑卸载脚本（控制面板里的入口会带上它）。
    ///
    /// 用 /langversion:5，所以不用 interpolated string、nameof、元组。
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                if (args[0] == "--uninstall")
                    return RunUninstall(args);

                if (args[0] == "--selftest")
                {
                    string dir = args.Length >= 2 ? args[1] : ".";
                    return SetupSelfTest.Run(dir);
                }
            }

            try
            {
                Application app = new Application
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown
                };

                app.Startup += (s, e) =>
                {
                    try
                    {
                        // 先把载荷解到临时目录，失败就直接告诉用户，别等点了安装才报错
                        string problem = Installer.PreparePayload();

                        SetupWindow w = new SetupWindow();
                        if (problem != null) w.Invalidate(problem);

                        w.Closed += (s2, e2) =>
                        {
                            Installer.CleanupPayload();
                            app.Shutdown();
                        };

                        w.Show();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("安装程序启动失败：\n" + ex.Message,
                            "浮屿 Islet", MessageBoxButton.OK, MessageBoxImage.Error);
                        app.Shutdown();
                    }
                };

                app.Run();
                Installer.CleanupPayload();
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("安装程序异常退出：\n" + ex.Message,
                    "浮屿 Islet", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        /// <summary>控制面板「卸载」调用：跑安装目录里的 uninstall.cmd。</summary>
        private static int RunUninstall(string[] args)
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string cmd = System.IO.Path.Combine(dir, "uninstall.cmd");
                if (!System.IO.File.Exists(cmd))
                {
                    MessageBox.Show("找不到卸载脚本：\n" + cmd,
                        "浮屿 Islet", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return 1;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cmd,
                    WorkingDirectory = dir,
                    UseShellExecute = true
                });
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动卸载失败：\n" + ex.Message,
                    "浮屿 Islet", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
