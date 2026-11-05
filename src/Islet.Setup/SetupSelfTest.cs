using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Islet.Setup
{
    /// <summary>
    /// 安装程序自检：无头跑一遍界面渲染 + 数据层，输出文本与截图后退出。
    ///
    /// 为什么需要：安装界面是给别人用的第一印象，而它平时只能靠人盯着看。
    /// 有了这个，改完一次就能确认「窗口画出来了、路径默认值对、卸载脚本生成得对」。
    ///
    /// 用法：IsletSetup.exe --selftest &lt;输出目录&gt;
    /// </summary>
    internal static class SetupSelfTest
    {
        public static int Run(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            string txtPath;

            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }
            txtPath = Path.Combine(outputDir, "setuptest.txt");

            report.AppendLine("SETUP_SELFTEST");
            report.AppendLine("source_dir=" + Installer.SourceDir);
            report.AppendLine("default_target=" + Installer.DefaultTargetDir);
            report.AppendLine("data_dir=" + Installer.DataDir);

            // ---- 数据层：不依赖 UI 的部分先验 ----
            try
            {
                string payload = Installer.PreparePayload();
                report.AppendLine("prepare_payload=" + (payload == null ? "OK" : payload));

                report.AppendLine("has_existing=" + Installer.HasExisting(Installer.DefaultTargetDir));
                report.AppendLine("writable_temp=" +
                    Installer.IsWritable(Path.Combine(Path.GetTempPath(), "islet_setup_probe")));

                // 卸载脚本：只检查关键语句都在
                string script = UninstallScript.Build();
                report.AppendLine("uninstall_len=" + script.Length);
                report.AppendLine("uninstall_has_regdelete=" +
                    script.Contains("Run\" /v \"Islet\" /f"));
                report.AppendLine("uninstall_has_uninstall_key=" +
                    script.Contains("Uninstall\\Islet"));
                report.AppendLine("uninstall_has_delfiles=" + script.Contains("rd /S /Q"));
                report.AppendLine("uninstall_asks_config=" + script.Contains("DELCFG"));

                // ---- 真的跑一遍安装 ----
                //
                // 只验证「界面画出来了」是不够的：装出来的文件是否齐全、
                // 卸载脚本有没有生成、快捷方式能不能创建（ShellLink 的 COM 调用）
                // 都必须真做一次才知道。
                // 目标放在输出目录下（自检本来就是往这里写文件），装完立即删掉，
                // 不碰真实安装位置。用输出目录而不是 %TEMP%：
                // 某些受限环境（安全软件、沙箱会话）连 %TEMP% 下新建目录都会拒绝，
                // 用 %TEMP% 会让这一整段验证被静默跳过 —— 那就等于没验。
                string probeDir = Path.Combine(outputDir,
                    "e2e_" + Guid.NewGuid().ToString("N"));

                try
                {
                    Installer.Install(probeDir,
                        desktopShortcut: false,      // 不自作主张往用户桌面丢东西
                        startMenuShortcut: false,
                        autorun: false,
                        log: null);

                    bool exeOk = File.Exists(Path.Combine(probeDir, "Islet.exe"));
                    bool coreOk = File.Exists(Path.Combine(probeDir, "Islet.Core.dll"));
                    bool cfgOk = File.Exists(Path.Combine(probeDir, "Islet.exe.config"));
                    bool icoOk = File.Exists(Path.Combine(probeDir, "islet.ico"));
                    bool uOk = File.Exists(Path.Combine(probeDir, "uninstall.cmd"));

                    report.AppendLine("e2e_target=" + probeDir);
                    report.AppendLine("e2e_Islet.exe=" + exeOk);
                    report.AppendLine("e2e_Islet.Core.dll=" + coreOk);
                    report.AppendLine("e2e_Islet.exe.config=" + cfgOk);
                    report.AppendLine("e2e_islet.ico=" + icoOk);
                    report.AppendLine("e2e_uninstall.cmd=" + uOk);
                    report.AppendLine("e2e_ALL_OK=" +
                        (exeOk && coreOk && cfgOk && icoOk && uOk));

                    // 装出来的 exe 大小要和载荷一致，避免「复制了 0 字节」这种蠢问题。
                    // 不能去比磁盘上的源文件：正式包里根本没有源文件，全在资源里。
                    try
                    {
                        long installed = new FileInfo(Path.Combine(probeDir, "Islet.exe")).Length;
                        long expected = Installer.PayloadSize("Islet.exe");
                        report.AppendLine("e2e_size_match=" + (installed == expected)
                                          + " (" + installed + " vs " + expected + ")");
                    }
                    catch (Exception ex) { report.AppendLine("e2e_size_err=" + ex.Message); }
                }
                finally
                {
                    try { Directory.Delete(probeDir, true); }
                    catch (Exception) { }
                    // 安装器注册了卸载信息与自启，测完要撤掉，别在真实系统里留垃圾
                    try { AddRemovePrograms.Unregister(); }
                    catch (Exception) { }
                }

                // ---- 快捷方式 COM 调用单独验一次（不落到桌面/开始菜单）----
                try
                {
                    string lnkDir = Path.Combine(outputDir,
                                                 "lnk_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(lnkDir);
                    string lnk = Path.Combine(lnkDir, "test.lnk");
                    ShellLink.Create(lnk,
                        Path.Combine(Installer.SourceDir, "Islet.exe"),
                        Installer.SourceDir, "浮屿 Islet", null);
                    report.AppendLine("shelllink_created=" + File.Exists(lnk));
                    try { Directory.Delete(lnkDir, true); } catch (Exception) { }
                }
                catch (Exception ex)
                {
                    report.AppendLine("shelllink_ERR=" + ex.Message);
                }
            }
            catch (Exception ex)
            {
                report.AppendLine("DATA_ERR: " + ex);
            }

            // ---- UI 层 ----
            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SetupWindow win = null;

                app.Startup += (s, e) =>
                {
                    try
                    {
                        win = new SetupWindow();
                        win.Show();
                        report.AppendLine("SETUP_SHOWN");

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(1000)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            try
                            {
                                report.AppendLine("SETUP_SIZE=" +
                                    (int)win.ActualWidth + "x" + (int)win.ActualHeight);

                                RenderTargetBitmap rtb = new RenderTargetBitmap(
                                    (int)Math.Ceiling(win.ActualWidth),
                                    (int)Math.Ceiling(win.ActualHeight),
                                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                rtb.Render(win);

                                PngBitmapEncoder enc = new PngBitmapEncoder();
                                enc.Frames.Add(BitmapFrame.Create(rtb));
                                using (FileStream fs = new FileStream(
                                    Path.Combine(outputDir, "setup.png"), FileMode.Create))
                                {
                                    enc.Save(fs);
                                }
                                report.AppendLine("SETUP_SHOT_OK");
                            }
                            catch (Exception ex)
                            {
                                report.AppendLine("SETUP_SHOT_ERR: " + ex);
                            }
                            app.Shutdown();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("STARTUP_ERR: " + ex);
                        app.Shutdown();
                    }
                };

                app.Run();
            }
            catch (Exception ex)
            {
                report.AppendLine("UI_ERR: " + ex);
            }

            try
            {
                File.WriteAllText(txtPath, report.ToString(), new UTF8Encoding(false));
                Console.Write(report.ToString());
            }
            catch (Exception) { }

            Installer.CleanupPayload();
            return 0;
        }
    }
}
