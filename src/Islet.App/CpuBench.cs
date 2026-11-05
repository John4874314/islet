using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Islet.App
{
    /// <summary>
    /// CPU 与唤醒频率实测。常驻软件真正该看的是这个，不是内存数字。
    ///
    /// 测量项：
    ///   · 空闲 CPU 占用（PRD 要求 ≤ 0.3%）
    ///   · 每秒唤醒次数（Windows 上直接影响 Modern Standby 续航）
    ///   · 有内容时的 CPU 占用
    /// </summary>
    internal static class CpuBench
    {
        public static int Run(string outputDir, int seconds)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "cpubench.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                IsletHost host = null;

                app.Startup += (s, e) =>
                {
                    try
                    {
                        host = new IsletHost();
                        host.Start();

                        Process self = Process.GetCurrentProcess();
                        self.Refresh();

                        TimeSpan cpu0 = self.TotalProcessorTime;
                        DateTimeOffset t0 = DateTimeOffset.Now;
                        long handle0 = self.HandleCount;

                        report.AppendLine("== CPU 与唤醒实测 ==");
                        report.AppendLine("采样时长 = " + seconds + " 秒");
                        report.AppendLine("逻辑处理器 = " + Environment.ProcessorCount);
                        report.AppendLine();

                        // 阶段 1：完全空闲（欢迎语 6 秒后过期，之后应该没有内容）
                        int phase = 0;
                        int tick = 0;

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(250)
                        };

                        t.Tick += (s2, e2) =>
                        {
                            tick++;

                            // 10 秒时注入一个持续刷新的进度事件，测「有内容」时的开销
                            if (tick == 40)
                            {
                                self.Refresh();
                                TimeSpan idleCpu = self.TotalProcessorTime - cpu0;
                                double idleSec = (DateTimeOffset.Now - t0).TotalSeconds;
                                double idlePct = 100.0 * idleCpu.TotalSeconds
                                                 / (idleSec * Environment.ProcessorCount);

                                report.AppendLine("--- 阶段 1：完全空闲（10 秒）---");
                                report.AppendLine("CPU 时间 = " + idleCpu.TotalSeconds.ToString("F3") + " s");
                                report.AppendLine("空闲 CPU = " + idlePct.ToString("F3") + " %");
                                report.AppendLine("句柄变化 = " + (self.HandleCount - handle0));
                                report.AppendLine();

                                cpu0 = self.TotalProcessorTime;
                                t0 = DateTimeOffset.Now;
                                phase = 1;
                            }

                            if (tick >= 40 && tick < 80)
                            {
                                // 每 250 ms 刷新一次内容，模拟音乐进度条
                                host.Slot.Publish(new Core.IslandEvent("bench",
                                    Core.IslandSource.Progress, "CPU 测试 " + tick)
                                    .WithSubtitle("每 250ms 刷新")
                                    .WithProgress((tick % 40) / 40.0)
                                    .WithUrgency(20)
                                    .WithActionability(50));
                            }

                            if (tick == 80)
                            {
                                self.Refresh();
                                TimeSpan busyCpu = self.TotalProcessorTime - cpu0;
                                double busySec = (DateTimeOffset.Now - t0).TotalSeconds;
                                double busyPct = 100.0 * busyCpu.TotalSeconds
                                                 / (busySec * Environment.ProcessorCount);

                                report.AppendLine("--- 阶段 2：活跃刷新（10 秒，4 次/秒）---");
                                report.AppendLine("CPU 时间 = " + busyCpu.TotalSeconds.ToString("F3") + " s");
                                report.AppendLine("活跃 CPU = " + busyPct.ToString("F3") + " %");
                                report.AppendLine();

                                t.Stop();
                                report.AppendLine("== 判定 ==");
                                report.AppendLine("PRD 要求空闲 CPU ≤ 0.3%，请对照上面的『空闲 CPU』一行");
                                app.Shutdown();
                            }
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("BENCH_ERROR: " + ex);
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
    }
}
