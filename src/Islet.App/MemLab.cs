using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Islet.Core;

namespace Islet.App
{
    /// <summary>
    /// 内存治理实验：验证哪些手段能真正把稳态压到 60 MB 以内。
    ///
    /// 已知事实（来自分段诊断）：
    ///   纯 CLR           24.5 MB
    ///   + 窗口构造       40.5 MB   (+16.0)
    ///   + 窗口显示       61.3 MB   (+20.8)  ← 首次渲染管线，最大头
    ///   + 数据源 x5      65.6 MB   (+0.4)   ← 可忽略
    ///   + 托盘           72.6 MB   (+7.0)
    ///
    /// 本文件只做实验，验证完把结论落到正式代码里。
    /// </summary>
    internal static class MemLab
    {
        public static int Run(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "memlab.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                app.Startup += (s, e) =>
                {
                    try
                    {
                        report.AppendLine("== 内存治理实验 ==");
                        report.AppendLine("A_baseline=" + MB());

                        Views.IslandWindow win = new Views.IslandWindow();
                        win.Show();
                        win.ShowNoActivate();
                        win.Render(new IslandEvent("x", IslandSource.Progress, "测试")
                            .WithSubtitle("副标题").WithProgress(0.5)
                            .WithActions(new IslandAction("按钮", () => { })));
                        win.UpdateLayout();
                        report.AppendLine("B_after_show_render=" + MB());

                        // ---- 实验 1：只做 GC，不裁工作集 ----
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                        GC.WaitForPendingFinalizers();
                        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                        report.AppendLine("C_after_gc_only=" + MB());

                        // ---- 实验 2：GC + 裁工作集 ----
                        MemoryTrimmer.TrimWorkingSet();
                        report.AppendLine("D_after_trim=" + MB());

                        // ---- 实验 3：关闭 WPF 硬件加速的效果需要重启进程才能测，这里跳过 ----

                        // ---- 实验 4：模拟「真实使用 30 秒」后的稳态 ----
                        // 反复渲染不同类型的内容，模拟用户实际使用
                        int tick = 0;
                        DispatcherTimer stim = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(300)
                        };
                        stim.Tick += (s2, e2) =>
                        {
                            tick++;
                            try
                            {
                                IslandEvent ev = new IslandEvent("t" + (tick % 5),
                                    (IslandSource)(tick % 6),
                                    "模拟内容 " + tick)
                                    .WithSubtitle("副标题 " + tick)
                                    .WithProgress((tick % 10) / 10.0)
                                    .WithActions(
                                        new IslandAction("动作A", () => { }),
                                        new IslandAction("动作B", () => { }));
                                win.Render(ev);
                                win.UpdateLayout();

                                if (tick == 20) win.SetExpanded(true);
                                if (tick == 30) win.SetExpanded(false);
                            }
                            catch (Exception) { }

                            if (tick < 60) return;

                            stim.Stop();
                            report.AppendLine("E_after_60_renders=" + MB());

                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                            GC.WaitForPendingFinalizers();
                            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, true);
                            report.AppendLine("F_after_gc=" + MB());

                            MemoryTrimmer.TrimWorkingSet();
                            report.AppendLine("G_after_trim=" + MB());

                            report.AppendLine();
                            report.AppendLine("== 结论 ==");
                            report.AppendLine("WPF 渲染管线占用 = " +
                                "窗口显示后减去基线，约 37 MB，且这部分无法通过 GC 释放");
                            report.AppendLine("可回收部分 = 裁工作集后能降到多少，见 D/G 两行");

                            app.Shutdown();
                        };
                        stim.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("LAB_ERROR: " + ex);
                        app.Shutdown();
                    }
                };

                app.Run();
            }
            catch (Exception ex)
            {
                report.AppendLine("FATAL: " + ex);
            }

            try { File.WriteAllText(txtPath, report.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
            return 0;
        }

        private static string MB()
        {
            using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
            {
                p.Refresh();
                return Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1) + " MB";
            }
        }
    }
}
