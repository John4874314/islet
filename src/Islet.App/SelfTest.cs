using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Islet.App.Interop;
using Islet.App.Services;
using Islet.App.Views;
using Islet.Core;

namespace Islet.App
{
    /// <summary>
    /// 无头自检：创建窗口、渲染事件、截图、输出诊断后退出。
    /// 在没有交互式调试器的环境里，这是唯一能验证渲染效果的手段。
    /// 用法：Islet.exe --selftest &lt;输出目录&gt;
    /// </summary>
    internal static class SelfTest
    {
        public static int Run(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            string pngPath = Path.Combine(outputDir, "selftest.png");
            string txtPath = Path.Combine(outputDir, "selftest.txt");

            try
            {
                Directory.CreateDirectory(outputDir);
                report.AppendLine("OUTPUT_DIR_OK=" + outputDir);
            }
            catch (Exception ex)
            {
                outputDir = AppDomain.CurrentDomain.BaseDirectory;
                pngPath = Path.Combine(outputDir, "selftest.png");
                txtPath = Path.Combine(outputDir, "selftest.txt");
                report.AppendLine("OUTPUT_DIR_FALLBACK: " + ex.Message + " -> " + outputDir);
            }

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                IslandWindow win = null;

                app.Startup += (s, e) =>
                {
                    try
                    {
                        win = new IslandWindow();
                        win.Show();
                        win.ShowNoActivate();

                        win.Render(new IslandEvent("islet.selftest", IslandSource.Progress,
                                                   "正在下载 视觉小说素材包")
                            .WithSubtitle("1.2 GB / 3.4 GB · 剩 4 分钟")
                            .WithProgress(0.36)
                            .WithActions(
                                new IslandAction("打开文件夹", () => { }),
                                new IslandAction("暂停", () => { })));
                        report.AppendLine("RENDER_OK");

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(1200)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            try
                            {
                                Dump(win, report, pngPath, "compact");
                                report.AppendLine(win.DiagnoseLayers());

                                // ---- 关键判定：这个圆到底在不在视觉树里？ ----
                                // CopyFromScreen 抓分层窗口（WS_EX_LAYERED）时经常拿到
                                // 上一帧的陈旧内容，所以「截图里有圆」不能证明「真的画了圆」。
                                // 走 WPF 自己的渲染管线再抓一次：如果离屏渲染是干净的，
                                // 那圆就只是抓屏假象，不是 UI bug。
                                try
                                {
                                    System.Windows.Media.Imaging.BitmapSource off =
                                        win.RenderToBitmap();
                                    if (off != null)
                                    {
                                        SaveBitmap(off, Path.Combine(outputDir,
                                                                     "offscreen-bar.png"));
                                        report.AppendLine("OFFSCREEN_BAR=" +
                                            off.PixelWidth + "x" + off.PixelHeight);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    report.AppendLine("OFFSCREEN_ERR: " + ex.Message);
                                }

                                win.SetExpanded(true);
                                win.UpdateLayout();
                                Dump(win, report, Path.Combine(outputDir, "selftest-card.png"), "card");
                                report.AppendLine(win.DiagnoseLayers());
                                try
                                {
                                    System.Windows.Media.Imaging.BitmapSource oc =
                                        win.RenderToBitmap();
                                    if (oc != null)
                                    {
                                        SaveBitmap(oc, Path.Combine(outputDir,
                                                                    "offscreen-card.png"));
                                    }
                                }
                                catch (Exception) { }

                                // ---- 中转区：长在玻璃内部右侧的方框 ----
                                //
                                // 抓三个形态 + 记两个宽度，用来证明：
                                //   1) 没有文件时窗口宽度与加这个功能之前完全一致；
                                //   2) 有文件时正好多出 方框宽(176) + 右边距(12) = 188。
                                // 宽度是这里最硬的证据 —— 「玻璃有没延伸」最终就是这个数。
                                double widthEmpty = win.ActualWidth;
                                try
                                {
                                    string work = Path.Combine(outputDir, "stash-work");
                                    Services.StashStore stash = new Services.StashStore(work);

                                    string srcDir = Path.Combine(outputDir, "stash-src");
                                    Directory.CreateDirectory(srcDir);
                                    string[] names = new string[]
                                    {
                                        "视觉小说素材包.zip", "会议纪要.md", "screenshot.png"
                                    };
                                    string[] srcs = new string[names.Length];
                                    for (int i = 0; i < names.Length; i++)
                                    {
                                        srcs[i] = Path.Combine(srcDir, names[i]);
                                        File.WriteAllText(srcs[i], "stash selftest " + i);
                                    }
                                    stash.Add(srcs, new System.Collections.Generic.List<string>());
                                    report.AppendLine("STASH_ADDED=" + stash.Count);

                                    win.AttachStashStore(stash);

                                    // 卡片态 + 中转区
                                    win.SetExpanded(true);
                                    win.UpdateLayout();
                                    Dump(win, report,
                                         Path.Combine(outputDir, "selftest-stash-card.png"),
                                         "stash-card");
                                    report.AppendLine("STASH_WIDTH_EMPTY=" + widthEmpty
                                                      + " STASH_WIDTH_FULL=" + win.ActualWidth
                                                      + " DELTA=" + (win.ActualWidth - widthEmpty));
                                    report.AppendLine(win.DiagnoseLayers());
                                    try
                                    {
                                        System.Windows.Media.Imaging.BitmapSource bs =
                                            win.RenderToBitmap();
                                        if (bs != null)
                                            SaveBitmap(bs, Path.Combine(
                                                outputDir, "offscreen-stash-card.png"));
                                    }
                                    catch (Exception ex) { report.AppendLine("OS_STASH_CARD: " + ex.Message); }

                                    // 条态（音乐播放等「有内容但没展开」）
                                    win.SetExpanded(false);
                                    win.UpdateLayout();
                                    Dump(win, report,
                                         Path.Combine(outputDir, "selftest-stash-bar.png"),
                                         "stash-bar");
                                    report.AppendLine(win.DiagnoseLayers());
                                    try
                                    {
                                        System.Windows.Media.Imaging.BitmapSource bb =
                                            win.RenderToBitmap();
                                        if (bb != null)
                                            SaveBitmap(bb, Path.Combine(
                                                outputDir, "offscreen-stash-bar.png"));
                                    }
                                    catch (Exception ex) { report.AppendLine("OS_STASH_BAR: " + ex.Message); }
                                }
                                catch (Exception ex)
                                {
                                    report.AppendLine("STASH_STAGE_ERROR: " + ex);
                                }

                                win.SetExpanded(false);
                                win.Render(null);
                                win.UpdateLayout();
                                Dump(win, report, Path.Combine(outputDir, "selftest-dot.png"), "dot");
                                report.AppendLine(win.DiagnoseLayers());

                                try
                                {
                                    System.Windows.Media.Imaging.BitmapSource od =
                                        win.RenderToBitmap();
                                    if (od != null)
                                    {
                                        SaveBitmap(od, Path.Combine(outputDir,
                                                                    "offscreen-dot.png"));
                                    }
                                }
                                catch (Exception) { }
                            }
                            catch (Exception ex) { report.AppendLine("DUMP_ERROR: " + ex); }
                            app.Shutdown();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("STARTUP_ERROR: " + ex);
                        app.Shutdown();
                    }
                };

                app.Run();
                report.AppendLine("APP_RUN_RETURNED");
            }
            catch (Exception ex)
            {
                report.AppendLine("FATAL: " + ex);
            }

            try { File.WriteAllText(txtPath, report.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
            return 0;
        }

        // ---------------------------------------------------------------
        // 宿主层自检：验证数据源装配 + 仲裁 + 渲染全链路
        // ---------------------------------------------------------------

        public static int RunHost(string outputDir, int seconds)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "hosttest.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                IsletHost host = null;

                app.Startup += (s, e) =>
                {
                    try
                    {
                        report.AppendLine("HOST_START_BEGIN");
                        host = new IsletHost();
                        report.AppendLine("HOST_CTOR_OK");

                        host.Start();
                        report.AppendLine("HOST_START_OK");

                        // 主动注入一个高优先级事件，验证仲裁链路
                        host.Slot.Publish(new IslandEvent("islet.hosttest", IslandSource.Timer,
                                                          "自检计时器")
                            .WithSubtitle("链路验证")
                            .WithProgress(0.5)
                            .WithUrgency(95)
                            .WithActionability(80)
                            .WithHardPreempt(true));
                        report.AppendLine("HOST_PUBLISH_OK");

                        report.AppendLine("SLOT_CURRENT=" +
                            (host.Slot.Current == null ? "null" : host.Slot.Current.Title));
                        report.AppendLine("QUEUE_DEPTH=" + host.Slot.QueueDepth);
                        report.AppendLine(host.WindowDiagnostics);

                        // 抓一张真实桌面的顶部区域，确认胶囊确实画在屏幕上
                        DispatcherTimer shot = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(900)
                        };
                        shot.Tick += (s3, e3) =>
                        {
                            shot.Stop();
                            try
                            {
                                using (Bitmap bmp = new Bitmap(1200, 120,
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                                using (Graphics g = Graphics.FromImage(bmp))
                                {
                                    g.CopyFromScreen(680, 0, 0, 0, new System.Drawing.Size(1200, 120));
                                    bmp.Save(Path.Combine(outputDir, "desktop-top.png"),
                                             ImageFormat.Png);
                                }
                                report.AppendLine("DESKTOP_SHOT_OK");
                            }
                            catch (Exception ex) { report.AppendLine("SHOT_ERR: " + ex.Message); }
                        };
                        shot.Start();
                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(seconds)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            report.AppendLine("AFTER_" + seconds + "S_CURRENT=" +
                                (host.Slot.Current == null ? "null" : host.Slot.Current.Title));
                            app.Shutdown();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("HOST_START_ERROR: " + ex);
                        app.Shutdown();
                    }
                };

                app.Run();
                report.AppendLine("HOST_RUN_RETURNED");
            }
            catch (Exception ex)
            {
                report.AppendLine("HOST_FATAL: " + ex);
            }

            try { File.WriteAllText(txtPath, report.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
            return 0;
        }

        // ---------------------------------------------------------------
        // 端到端：真的往剪贴板写内容，验证识别 + 上岛 + 渲染
        // ---------------------------------------------------------------

        public static int RunClipboard(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "clipboardtest.txt");

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
                        report.AppendLine("HOST_START_OK");

                        // 等监听线程就绪
                        DispatcherTimer delay = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(1500)
                        };
                        delay.Tick += (s2, e2) =>
                        {
                            delay.Stop();
                            try
                            {
                                // 写入一条看起来像真实验证码的短信
                                string sample = "831402";
                                System.Windows.Forms.Clipboard.SetText(sample);
                                report.AppendLine("CLIPBOARD_SET=" + sample);
                            }
                            catch (Exception ex)
                            {
                                report.AppendLine("CLIP_SET_ERR: " + ex.Message);
                            }

                            DispatcherTimer check = new DispatcherTimer
                            {
                                Interval = TimeSpan.FromMilliseconds(1800)
                            };
                            check.Tick += (s3, e3) =>
                            {
                                check.Stop();
                                IslandEvent cur = host.Slot.Current;
                                report.AppendLine("CURRENT=" +
                                    (cur == null ? "null" : cur.Title + " | " + cur.Subtitle));
                                report.AppendLine("MATCHED_CODE=" +
                                    (cur != null && cur.Title.IndexOf("验证码") >= 0));
                                report.AppendLine("DIAG_EVENTS=" +
                                    Sources.ClipboardProvider.DiagClipboardEvents);
                                report.AppendLine("DIAG_RECOGNIZED=" +
                                    Sources.ClipboardProvider.DiagRecognized);
                                report.AppendLine("DIAG_LAST_TEXT=" +
                                    (Sources.ClipboardProvider.DiagLastText ?? "(null)"));
                                report.AppendLine("DIAG_LAST_ERROR=" +
                                    (Sources.ClipboardProvider.DiagLastError ?? "(none)"));

                                // 截图留证：分层窗口抓不到屏幕内容，走 WPF 渲染管线
                                try
                                {
                                    System.Windows.Media.Imaging.BitmapSource bmp =
                                        host.RenderWindow();
                                    if (bmp == null)
                                    {
                                        report.AppendLine("CLIP_SHOT: 无位图");
                                    }
                                    else
                                    {
                                        report.AppendLine("CLIP_SHOT_SIZE=" +
                                            bmp.PixelWidth + "x" + bmp.PixelHeight);
                                        SaveBitmap(bmp,
                                            Path.Combine(outputDir, "clipboard-island.png"));
                                        report.AppendLine("CLIP_SHOT_OK");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    report.AppendLine("CLIP_SHOT_ERR: " + ex.Message);
                                }

                                app.Shutdown();
                            };
                            check.Start();
                        };
                        delay.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("HOST_START_ERROR: " + ex);
                        app.Shutdown();
                    }
                };

                app.Run();
                report.AppendLine("APP_RUN_RETURNED");
            }
            catch (Exception ex)
            {
                report.AppendLine("FATAL: " + ex);
            }

            try { File.WriteAllText(txtPath, report.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
            return 0;
        }

        // ---------------------------------------------------------------
        // 三档透明度对比：同一块玻璃铺在棋盘格上渲染三遍
        //
        // 棋盘格是判断「透不透」的唯一可靠底 —— 纯色底上三档只差一点灰度，
        // 肉眼几乎分不出来；棋盘格能直接看出背景格子在玻璃后面有多清晰。
        // ---------------------------------------------------------------

        public static int RunOpacity(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "opacity.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                app.Startup += (s, e) =>
                {
                    try
                    {
                        IslandWindow win = new IslandWindow();
                        win.Show();
                        win.ShowNoActivate();
                        win.Render(new IslandEvent("islet.opacity", IslandSource.Progress,
                                                   "正在下载 视觉小说素材包")
                            .WithSubtitle("1.2 GB / 3.4 GB · 剩 4 分钟")
                            .WithProgress(0.36)
                            .WithActions(new IslandAction("打开文件夹", () => { }),
                                         new IslandAction("暂停", () => { })));

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(800)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            try
                            {
                                GlassOpacity[] tiers = { GlassOpacity.Deep,
                                                         GlassOpacity.Medium,
                                                         GlassOpacity.Clear };
                                string[] names = { "深 Deep", "中 Medium", "浅 Clear" };

                                System.Collections.Generic.List<BitmapSource> shots =
                                    new System.Collections.Generic.List<BitmapSource>();
                                System.Collections.Generic.List<string> labels =
                                    new System.Collections.Generic.List<string>();

                                for (int i = 0; i < tiers.Length; i++)
                                {
                                    win.SetGlassOpacity(tiers[i]);
                                    win.UpdateLayout();
                                    BitmapSource bmp = win.RenderToBitmapWithBackdrop(true);
                                    shots.Add(bmp);
                                    labels.Add(names[i]);
                                    SaveBitmap(bmp,
                                        Path.Combine(outputDir, "opacity-" + i + ".png"));
                                    report.AppendLine("OPACITY_" + i + "=" + names[i]
                                        + " " + bmp.PixelWidth + "x" + bmp.PixelHeight);
                                }

                                SaveBitmap(ComposeOpacity(shots, labels),
                                           Path.Combine(outputDir, "opacity-compare.png"));
                                report.AppendLine("OPACITY_COMPARE_OK");
                            }
                            catch (Exception ex)
                            {
                                report.AppendLine("OPACITY_ERR: " + ex);
                            }
                            app.Shutdown();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("OPACITY_START_ERR: " + ex);
                        app.Shutdown();
                    }
                };

                app.Run();
            }
            catch (Exception ex) { report.AppendLine("FATAL: " + ex); }

            try { File.WriteAllText(txtPath, report.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
            return 0;
        }

        /// <summary>把三张图纵向拼起来，左侧留出档位标签。</summary>
        private static BitmapSource ComposeOpacity(
            System.Collections.Generic.List<BitmapSource> shots,
            System.Collections.Generic.List<string> labels)
        {
            const int LabelW = 100;
            const int Gap = 14;
            int w = 0, h = Gap;
            foreach (BitmapSource b in shots)
            {
                if (b.PixelWidth > w) w = b.PixelWidth;
                h += b.PixelHeight + Gap;
            }
            w += LabelW;

            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new SolidColorBrush(
                                     System.Windows.Media.Color.FromRgb(0x20, 0x24, 0x2C)), null,
                                 new Rect(0, 0, w, h));

                double y = Gap;
                for (int i = 0; i < shots.Count; i++)
                {
                    BitmapSource b = shots[i];
                    FormattedText ft = new FormattedText(
                        labels[i], System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Microsoft YaHei UI"), 13,
                        new SolidColorBrush(
                            System.Windows.Media.Color.FromRgb(0xE6, 0xEA, 0xF2)));
                    dc.DrawText(ft, new System.Windows.Point(
                        10, y + b.PixelHeight / 2.0 - ft.Height / 2.0));
                    dc.DrawImage(b, new Rect(LabelW, y, b.PixelWidth, b.PixelHeight));
                    y += b.PixelHeight + Gap;
                }
            }

            RenderTargetBitmap rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // ---------------------------------------------------------------
        // 音量面板渲染验证
        //
        // 面板依赖真实音频会话，而自检环境里往往没有应用在发声。
        // 所以这里只验证「能建起来、能渲染、行数与会话数一致」，
        // 具体音量读写在 AudioSessionProbe 里单独验证。
        // ---------------------------------------------------------------

        public static int RunVolume(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "volumetest.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                app.Startup += (s, e) =>
                {
                    try
                    {
                        Views.VolumePanel panel = new Views.VolumePanel();
                        panel.Show();
                        panel.UpdateLayout();
                        report.AppendLine("PANEL_SHOWN");

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(1400)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            try
                            {
                                panel.UpdateLayout();
                                report.AppendLine("PANEL_SIZE=" +
                                    (int)panel.ActualWidth + "x" + (int)panel.ActualHeight);

                                System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                                    new System.Windows.Media.Imaging.RenderTargetBitmap(
                                        (int)Math.Ceiling(panel.ActualWidth),
                                        (int)Math.Ceiling(panel.ActualHeight),
                                        96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                rtb.Render(panel);
                                SaveBitmap(rtb, Path.Combine(outputDir, "volume.png"));
                                report.AppendLine("PANEL_SHOT_OK");

                                // 直接问一次底层：面板里应该显示多少行
                                using (Islet.Sources.AudioSessionProvider probe =
                                       new Islet.Sources.AudioSessionProvider())
                                {
                                    bool ok = probe.Refresh();
                                    report.AppendLine("PROVIDER_AVAILABLE=" + ok);
                                    report.AppendLine("SESSION_COUNT=" + probe.Sessions.Count);
                                    report.AppendLine("PROVIDER_ERR=" +
                                        (probe.LastError ?? "(none)"));
                                }
                            }
                            catch (Exception ex)
                            {
                                report.AppendLine("PANEL_ERR: " + ex);
                            }
                            app.Shutdown();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("PANEL_START_ERR: " + ex);
                        app.Shutdown();
                    }
                };

                app.Run();
            }
            catch (Exception ex) { report.AppendLine("FATAL: " + ex); }

            try { File.WriteAllText(txtPath, report.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
            return 0;
        }

        // ---------------------------------------------------------------
        // 音量面板「写回链路」验证
        //
        // 只验证 UI 存在是不够的：面板能画出来、滑块能拖，不代表真的写进了 WASAPI。
        // 这里直接走「建面板 → 找到 Slider → 改 Value → 用独立 provider 回读」，
        // 把 UI→COM 这段链路单独测出来。
        // ---------------------------------------------------------------

        public static int RunVolumeWrite(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "volumewrite.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                app.Startup += (s, e) =>
                {
                    try
                    {
                        Views.VolumePanel panel = new Views.VolumePanel();
                        panel.Show();
                        panel.UpdateLayout();

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(1600)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            try
                            {
                                panel.UpdateLayout();

                                // 先把所有 Slider 找出来
                                System.Collections.Generic.List<System.Windows.Controls.Slider> sliders =
                                    new System.Collections.Generic.List<System.Windows.Controls.Slider>();
                                CollectSliders(panel, sliders);
                                report.AppendLine("SLIDER_COUNT=" + sliders.Count);

                                if (sliders.Count == 0)
                                {
                                    report.AppendLine("没有滑块，无法测试");
                                    app.Shutdown();
                                    return;
                                }

                                // 记录改动前：把**所有**会话的音量快照下来。
                                // 只看某一个会话不可靠 —— 面板显示的是筛选后的集合，
                                // 按「第一个可控会话」去取很容易取到面板里根本没有的那个。
                                string beforeSnap = VolumeSnapshot();
                                report.AppendLine("BEFORE_SNAPSHOT:");
                                report.AppendLine(beforeSnap);

                                // 直接改第一个滑块的值，模拟拖动
                                System.Windows.Controls.Slider sl = sliders[0];
                                report.AppendLine("SLIDER_VALUE_BEFORE=" + sl.Value);
                                sl.Value = 33;
                                panel.UpdateLayout();
                                report.AppendLine("SLIDER_VALUE_AFTER=" + sl.Value);

                                // 回读全部
                                string afterSnap = VolumeSnapshot();
                                report.AppendLine("AFTER_SNAPSHOT:");
                                report.AppendLine(afterSnap);
                                report.AppendLine("WRITE_WORKED="
                                    + (beforeSnap != afterSnap));
                                report.AppendLine(panel.Diagnose());

                                // ---- 关键：等一次自动刷新，再确认没被弹回去 ----
                                // 面板每秒 Refresh 一次，Refresh 会释放旧 COM 接口。
                                // 如果行里缓存了旧对象，这里就会看到滑块被「弹回」。
                                panel.UpdateLayout();
                                System.Threading.Thread.Sleep(1600);
                                panel.UpdateLayout();
                                report.AppendLine("SLIDER_AFTER_REFRESH=" + sl.Value);
                                report.AppendLine("STABLE_AFTER_REFRESH="
                                    + (Math.Abs(sl.Value - 33) < 1.5));

                                // 恢复：把 before 里的值写回去
                                RestoreSnapshot(beforeSnap);
                                report.AppendLine("RESTORED_SNAPSHOT:");
                                report.AppendLine(VolumeSnapshot());
                            }
                            catch (Exception ex)
                            {
                                report.AppendLine("WRITE_ERR: " + ex);
                            }
                            app.Shutdown();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("WRITE_START_ERR: " + ex);
                        app.Shutdown();
                    }
                };

                app.Run();
            }
            catch (Exception ex) { report.AppendLine("FATAL: " + ex); }

            try { File.WriteAllText(txtPath, report.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
            return 0;
        }

        private static void CollectSliders(DependencyObject root,
            System.Collections.Generic.List<System.Windows.Controls.Slider> outList)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                System.Windows.Controls.Slider s = c as System.Windows.Controls.Slider;
                if (s != null) outList.Add(s);
                CollectSliders(c, outList);
            }
        }

        /// <summary>把所有会话的「名字=音量」打成一行一行，用于前后对比。</summary>
        private static string VolumeSnapshot()
        {
            StringBuilder sb = new StringBuilder();
            using (Islet.Sources.AudioSessionProvider p = new Islet.Sources.AudioSessionProvider())
            {
                if (!p.Refresh()) return "  (refresh failed: " + p.LastError + ")";
                int i = 0;
                foreach (Islet.Sources.AudioSession s in p.Sessions)
                {
                    sb.AppendLine(string.Format(
                        "  [{0}] {1,-16} vol={2,4} muted={3} active={4} ctrl={5}",
                        i++, s.DisplayName,
                        ((int)Math.Round(s.Volume * 100)).ToString() + "%",
                        s.Muted, s.IsActive, s.CanControl));
                }
            }
            return sb.ToString();
        }

        /// <summary>把快照里的音量按索引写回去（尽力而为，失败不抛）。</summary>
        private static void RestoreSnapshot(string snap)
        {
            if (string.IsNullOrEmpty(snap)) return;

            // 解析出索引与百分比
            System.Collections.Generic.List<int> idx = new System.Collections.Generic.List<int>();
            System.Collections.Generic.List<float> val = new System.Collections.Generic.List<float>();
            foreach (string line in snap.Split('\n'))
            {
                int lb = line.IndexOf('[');
                int rb = line.IndexOf(']');
                int vp = line.IndexOf("vol=");
                if (lb < 0 || rb < 0 || vp < 0) continue;
                int i;
                if (!int.TryParse(line.Substring(lb + 1, rb - lb - 1).Trim(), out i)) continue;
                string rest = line.Substring(vp + 4).Trim();
                int pctSign = rest.IndexOf('%');
                if (pctSign < 0) continue;
                int pct;
                if (!int.TryParse(rest.Substring(0, pctSign).Trim(), out pct)) continue;
                idx.Add(i);
                val.Add(pct / 100f);
            }

            using (Islet.Sources.AudioSessionProvider p = new Islet.Sources.AudioSessionProvider())
            {
                if (!p.Refresh()) return;
                for (int k = 0; k < idx.Count && k < p.Sessions.Count; k++)
                {
                    if (idx[k] < 0 || idx[k] >= p.Sessions.Count) continue;
                    p.SetVolume(p.Sessions[idx[k]], val[k]);
                }
            }
        }

        /// <summary>当前进程工作集 MB。</summary>
        private static double MemMB()
        {
            using (System.Diagnostics.Process p = System.Diagnostics.Process.GetCurrentProcess())
            {
                p.Refresh();
                return Math.Round(p.WorkingSet64 / 1024.0 / 1024.0, 1);
            }
        }

        /// <summary>把 WPF 位图放大 2 倍存成 PNG，便于肉眼审查。</summary>
        private static void SaveBitmap(System.Windows.Media.Imaging.BitmapSource src, string path)
        {
            System.Windows.Media.Imaging.PngBitmapEncoder enc =
                new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(src));
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                enc.Save(fs);
            }
        }

        /// <summary>按窗口实际屏幕矩形截图，避免硬编码区域抓空。</summary>
        private static void CaptureWindow(IntPtr hwnd, string pngPath, StringBuilder report)
        {
            RECT r;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out r))
            {
                report.AppendLine("CAPTURE: 无有效窗口句柄");
                return;
            }

            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            report.AppendLine("CAPTURE_RECT=" + r.Left + "," + r.Top + "," + w + "x" + h);
            if (w <= 0 || h <= 0)
            {
                report.AppendLine("CAPTURE: 窗口尺寸为 0");
                return;
            }

            using (Bitmap bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
                using (Bitmap big = new Bitmap(w * 2, h * 2))
                using (Graphics g2 = Graphics.FromImage(big))
                {
                    g2.InterpolationMode =
                        System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g2.DrawImage(bmp, 0, 0, w * 2, h * 2);
                    big.Save(pngPath, ImageFormat.Png);
                }
            }
            report.AppendLine("CAPTURE_SAVED=" + pngPath);
        }

        // ---------------------------------------------------------------
        // 设置页渲染验证
        // ---------------------------------------------------------------

        public static int RunSettings(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "settingstest.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                app.Startup += (s, e) =>
                {
                    try
                    {
                        IsletSettings st = new IsletSettings();
                        st.MediaEnabled = false;
                        st.Position = IslandPosition.TopRight;
                        st.AutorunEnabled = true;

                        int changes = 0;
                        SettingsWindow win = new SettingsWindow(st,
                            settings => { changes++; });

                        win.Show();
                        win.UpdateLayout();
                        report.AppendLine("SETTINGS_SHOWN");

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(900)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            try
                            {
                                System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                                    new System.Windows.Media.Imaging.RenderTargetBitmap(
                                        (int)Math.Ceiling(win.ActualWidth),
                                        (int)Math.Ceiling(win.ActualHeight),
                                        96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                rtb.Render(win);
                                SaveBitmap(rtb,
                                    Path.Combine(outputDir, "settings.png"));
                                report.AppendLine("SETTINGS_SIZE=" +
                                    (int)win.ActualWidth + "x" + (int)win.ActualHeight);
                                report.AppendLine("SETTINGS_SHOT_OK");
                                report.AppendLine("CHANGES=" + changes);

                                // 再用 GDI 抓一次真实窗口。
                                // RenderTargetBitmap 只包含 WPF 自己画的东西：DWM 的 Acrylic 背板、
                                // 标题栏、圆角、阴影都是系统合成的，只有抓屏才看得见。
                                // 判断「玻璃到底长什么样」必须看这一张。
                                CaptureWindowRect(win, Path.Combine(outputDir, "settings-live.png"), report);
                            }
                            catch (Exception ex)
                            {
                                report.AppendLine("SETTINGS_SHOT_ERR: " + ex);
                            }
                            app.Shutdown();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("SETTINGS_START_ERROR: " + ex);
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

        // ---------------------------------------------------------------
        // 内存占用实测
        // ---------------------------------------------------------------

        public static int RunMemory(string outputDir, int seconds)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "memorytest.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                IsletHost host = null;

                app.Startup += (s, e) =>
                {
                    try
                    {
                        report.AppendLine("MEM_BASELINE=" + MemMB() + " MB");

                        host = new IsletHost();
                        report.AppendLine("MEM_AFTER_HOST_CTOR=" + MemMB() + " MB");

                        host.Start();
                        report.AppendLine("MEM_AFTER_START=" + MemMB() + " MB");

                        // 强制一次完整回收，看真实「稳态」而非初始化峰值
                        Core.MemoryTrimmer.Compact();
                        report.AppendLine("MEM_AFTER_FIRST_TRIM=" + MemMB() + " MB");

                        // 采样：每秒记一次，观察是否随时间攀升（泄漏征兆）
                        int ticks = 0;
                        System.Collections.Generic.List<double> samples =
                            new System.Collections.Generic.List<double>();

                        DispatcherTimer sample = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(1)
                        };
                        sample.Tick += (s2, e2) =>
                        {
                            ticks++;
                            samples.Add(host.WorkingSetMB);

                            if (ticks < seconds) return;

                            sample.Stop();

                            report.AppendLine("MEM_BEFORE_TRIM=" +
                                host.WorkingSetMB.ToString("F1") + " MB");
                            Core.MemoryTrimmer.Compact();
                            report.AppendLine("MEM_AFTER_TRIM=" +
                                host.WorkingSetMB.ToString("F1") + " MB");

                            double min = double.MaxValue, max = 0, sum = 0;
                            foreach (double v in samples)
                            {
                                if (v < min) min = v;
                                if (v > max) max = v;
                                sum += v;
                            }
                            report.AppendLine("SAMPLES=" + samples.Count);
                            report.AppendLine("MEM_MIN=" + min.ToString("F1") + " MB");
                            report.AppendLine("MEM_MAX=" + max.ToString("F1") + " MB");
                            report.AppendLine("MEM_AVG=" +
                                (sum / samples.Count).ToString("F1") + " MB");
                            report.AppendLine("GROWTH=" +
                                (max - min).ToString("F1") + " MB");

                            app.Shutdown();
                        };
                        sample.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("MEM_ERROR: " + ex);
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

        // ---------------------------------------------------------------
        // 设置端到端：读盘 → 装配 → 落盘 → 开机自启
        // ---------------------------------------------------------------

        public static int RunSettingsE2E(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "settingse2e.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                IsletHost host = null;

                app.Startup += (s, e) =>
                {
                    try
                    {
                        report.AppendLine("CONFIG_PATH=" + new SettingsStore().FilePath);

                        host = new IsletHost();
                        host.Start();

                        IsletSettings st = host.Settings;
                        report.AppendLine("LOADED_Media=" + st.MediaEnabled);
                        report.AppendLine("LOADED_Timer=" + st.TimerEnabled);
                        report.AppendLine("LOADED_Progress=" + st.ProgressEnabled);
                        report.AppendLine("LOADED_Health=" + st.HealthEnabled);
                        report.AppendLine("LOADED_Clipboard=" + st.ClipboardEnabled);
                        report.AppendLine("LOADED_Position=" + st.Position);
                        report.AppendLine("LOADED_HideFullscreen=" + st.HideInFullscreen);
                        report.AppendLine("LOADED_Ttl=" + st.ClipboardTtlSeconds);
                        report.AppendLine("LOADED_SourceCount=" + st.EnabledSourceCount);

                        // 校验：只有 timer 和 health 该是开的
                        bool expect = st.MediaEnabled == false
                                   && st.TimerEnabled == true
                                   && st.ProgressEnabled == false
                                   && st.HealthEnabled == true
                                   && st.ClipboardEnabled == false
                                   && st.Position == IslandPosition.TopRight
                                   && st.ClipboardTtlSeconds == 45;
                        report.AppendLine("CONFIG_APPLIED=" + expect);

                        // 开机自启：开 → 验证 → 关 → 验证（务必恢复原状）
                        bool wasEnabled = Services.AutoStart.IsEnabled();
                        report.AppendLine("AUTORUN_BEFORE=" + wasEnabled);
                        report.AppendLine("AUTORUN_MECHANISM_BEFORE=" +
                            Services.AutoStart.ActiveMechanism());

                        bool en = Services.AutoStart.Enable();
                        report.AppendLine("AUTORUN_ENABLE_RET=" + en);
                        report.AppendLine("AUTORUN_REG_AFTER=" +
                            Services.AutoStart.IsRegistryEntryPresent());
                        report.AppendLine("AUTORUN_LNK_AFTER=" +
                            Services.AutoStart.IsShortcutPresent());
                        report.AppendLine("AUTORUN_MECHANISM_AFTER=" +
                            Services.AutoStart.ActiveMechanism());

                        Services.AutoStart.Disable();
                        report.AppendLine("AUTORUN_AFTER_DISABLE=" +
                            Services.AutoStart.IsEnabled());

                        // 恢复用户原来的状态
                        if (wasEnabled) Services.AutoStart.Enable();
                        report.AppendLine("AUTORUN_RESTORED=" +
                            Services.AutoStart.IsEnabled());

                        // 内存
                        report.AppendLine("MEM=" + host.WorkingSetMB.ToString("F1") + " MB");

                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromSeconds(4)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            report.AppendLine("MEM_AFTER_WARMUP=" +
                                host.WorkingSetMB.ToString("F1") + " MB");
                            app.Shutdown();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("E2E_ERROR: " + ex);
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

        // ---------------------------------------------------------------
        // 内存分段诊断：逐层加载，看每一层各自吃多少
        // ---------------------------------------------------------------

        public static int RunMemProfile(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "memprofile.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

                app.Startup += (s, e) =>
                {
                    try
                    {
                        report.AppendLine("== 分段内存诊断 ==");
                        report.AppendLine("BASELINE(纯CLR)=" + Snapshot("初始"));

                        // --- 1. 只建窗口（构造过程就会加载 WPF 资源）---
                        Views.IslandWindow win = new Views.IslandWindow();
                        report.AppendLine("after_window_ctor=" + Snapshot("窗口构造"));

                        // --- 3. 显示窗口（触发 WPF 首次渲染管线）---
                        win.Show();
                        win.ShowNoActivate();
                        win.UpdateLayout();
                        report.AppendLine("after_window_show=" + Snapshot("窗口显示"));

                        // --- 4. 渲染一次内容 ---
                        win.Render(new IslandEvent("x", IslandSource.Progress, "测试")
                            .WithSubtitle("副标题").WithProgress(0.5)
                            .WithActions(new IslandAction("按钮", () => { })));
                        win.UpdateLayout();
                        report.AppendLine("after_first_render=" + Snapshot("首次渲染"));

                        // --- 5. 逐个数据源 ---
                        report.AppendLine("-- 数据源 --");

                        Sources.MediaProvider media = new Sources.MediaProvider();
                        report.AppendLine("after_media=" + Snapshot("媒体源"));

                        Sources.TimerProvider timer = new Sources.TimerProvider();
                        report.AppendLine("after_timer=" + Snapshot("计时源"));

                        Sources.ProgressProvider prog = new Sources.ProgressProvider();
                        report.AppendLine("after_progress=" + Snapshot("进度源"));

                        Sources.HealthProvider health = new Sources.HealthProvider();
                        report.AppendLine("after_health=" + Snapshot("体征源"));

                        Sources.ClipboardProvider clip = new Sources.ClipboardProvider();
                        report.AppendLine("after_clipboard=" + Snapshot("剪贴板源"));

                        // --- 6. 托盘 ---
                        Services.TrayIcon tray = new Services.TrayIcon();
                        tray.Show();
                        report.AppendLine("after_tray=" + Snapshot("托盘"));

                        report.AppendLine();
                        report.AppendLine("== 汇总 ==");
                        report.AppendLine("最终工作集=" + MemMB().ToString("F1") + " MB");

                        // 清理
                        try { clip.Dispose(); } catch (Exception) { }
                        try { health.Dispose(); } catch (Exception) { }
                        try { prog.Dispose(); } catch (Exception) { }
                        try { timer.Dispose(); } catch (Exception) { }
                        try { media.Dispose(); } catch (Exception) { }
                        try { tray.Dispose(); } catch (Exception) { }
                        try { win.Close(); } catch (Exception) { }

                        app.Shutdown();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("PROFILE_ERROR: " + ex);
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

        private static string Snapshot(string label)
        {
            return MemMB().ToString("F1") + " MB";
        }

        // ---------------------------------------------------------------
        // 隐私源验证：直接测「正在使用」判定逻辑
        // ---------------------------------------------------------------

        public static int RunPrivacy(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "privacytest.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                IsletHost host = null;

                app.Startup += (s, e) =>
                {
                    try
                    {
                        report.AppendLine("== 隐私源验证 ==");

                        // 先看系统里有没有历史记录（证明能读到该注册表）
                        report.AppendLine("注册表可读=" + CanReadConsentStore());
                        report.AppendLine("当前正在用麦克风=" +
                            (DescribeActive("microphone") ?? "(无)"));
                        report.AppendLine("当前正在用摄像头=" +
                            (DescribeActive("webcam") ?? "(无)"));

                        host = new IsletHost();
                        host.Start();

                        // 等隐私源第一次轮询（2 秒延迟 + 3 秒间隔）
                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(6000)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();
                            IslandEvent cur = host.Slot.Current;
                            report.AppendLine();
                            report.AppendLine("等待 6 秒后槽位内容=" +
                                (cur == null ? "null" : cur.Title + " | " + cur.Subtitle));
                            report.AppendLine("启用数据源数=" +
                                (host.Settings == null ? "?" :
                                 host.Settings.EnabledSourceCount.ToString()));
                            report.AppendLine("说明：若当前无人使用麦克风/摄像头，");
                            report.AppendLine("      槽位应显示其他内容或为空，不应出现隐私提示。");
                            app.Shutdown();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("PRIVACY_ERROR: " + ex);
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

        // ---------------------------------------------------------------
        // 隐私源端到端：本进程自己占用麦克风，验证能否检测到
        //
        // 为什么在同进程内做：跨进程启动会被沙箱的作业对象回收，
        // 而麦克风占用与检测逻辑本身不需要跨进程。
        // ---------------------------------------------------------------

        [DllImport("winmm.dll")]
        private static extern int waveInGetNumDevs();

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr hwi, uint device,
                                             ref WaveFormatEx fmt, IntPtr callback,
                                             IntPtr instance, uint flags);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct WaveFormatEx
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        public static int RunPrivacyE2E(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "privacye2e.txt");

            try
            {
                Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                IsletHost host = null;
                IntPtr micHandle = IntPtr.Zero;
                bool micOpen = false;

                app.Startup += (s, e) =>
                {
                    try
                    {
                        report.AppendLine("== 隐私源端到端 ==");
                        report.AppendLine("录音设备数=" + waveInGetNumDevs());
                        report.AppendLine("基线占用者=" + DescribeActive("microphone"));

                        host = new IsletHost();
                        host.Start();

                        // 1. 开启麦克风
                        WaveFormatEx fmt = new WaveFormatEx();
                        fmt.wFormatTag = 1;      // PCM
                        fmt.nChannels = 1;
                        fmt.nSamplesPerSec = 8000;
                        fmt.nAvgBytesPerSec = 8000;
                        fmt.nBlockAlign = 1;
                        fmt.wBitsPerSample = 8;

                        int rc = waveInOpen(out micHandle, 0, ref fmt,
                                            IntPtr.Zero, IntPtr.Zero, 0);
                        report.AppendLine("waveInOpen=" + rc);

                        if (rc == 0)
                        {
                            waveInStart(micHandle);
                            micOpen = true;
                            report.AppendLine("MIC_OPENED（已占用麦克风）");
                        }
                        else
                        {
                            report.AppendLine("MIC_OPEN_FAILED，跳过检测验证");
                        }

                        // 2. 等隐私源轮询（2 秒启动延迟 + 3 秒间隔，留足余量）
                        DispatcherTimer t = new DispatcherTimer
                        {
                            Interval = TimeSpan.FromMilliseconds(9000)
                        };
                        t.Tick += (s2, e2) =>
                        {
                            t.Stop();

                            report.AppendLine("系统侧占用者=" + DescribeActive("microphone"));
                            IslandEvent cur = host == null ? null : host.Slot.Current;
                            report.AppendLine("槽位=" +
                                (cur == null ? "null" : cur.Title + " | " + cur.Subtitle));

                            bool detected = cur != null &&
                                (cur.Title.IndexOf("麦克风") >= 0 ||
                                 cur.Title.IndexOf("隐私") >= 0);
                            report.AppendLine("PRIVACY_DETECTED=" + detected);

                            // 3. 释放麦克风，验证能否撤下
                            if (micOpen)
                            {
                                try
                                {
                                    waveInStop(micHandle);
                                    waveInClose(micHandle);
                                }
                                catch (Exception) { }
                                micOpen = false;
                                report.AppendLine("MIC_RELEASED");
                            }

                            DispatcherTimer t2 = new DispatcherTimer
                            {
                                Interval = TimeSpan.FromMilliseconds(7000)
                            };
                            t2.Tick += (s3, e3) =>
                            {
                                t2.Stop();
                                IslandEvent after = host == null ? null : host.Slot.Current;
                                report.AppendLine("释放后槽位=" +
                                    (after == null ? "null"
                                     : after.Title + " | " + after.Subtitle));
                                bool stillShown = after != null &&
                                    after.Title.IndexOf("麦克风") >= 0;
                                report.AppendLine("PRIVACY_CLEARED=" + !stillShown);
                                app.Shutdown();
                            };
                            t2.Start();
                        };
                        t.Start();
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine("PRIVACY_E2E_ERROR: " + ex);
                        if (micOpen && micHandle != IntPtr.Zero)
                        {
                            try { waveInStop(micHandle); waveInClose(micHandle); }
                            catch (Exception) { }
                        }
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

        // ---------------------------------------------------------------
        // 隐私源误报检验：本机有大量「僵尸记录」（stop==0 但进程已死）
        // ---------------------------------------------------------------

        public static int RunPrivacyFalsePositive(string outputDir)
        {
            StringBuilder report = new StringBuilder();
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception) { outputDir = AppDomain.CurrentDomain.BaseDirectory; }

            string txtPath = Path.Combine(outputDir, "privacyfp.txt");

            try
            {
                report.AppendLine("== 隐私源误报检验 ==");
                report.AppendLine();

                foreach (string cap in new[] { "microphone", "webcam" })
                {
                    report.AppendLine("--- " + cap + " ---");

                    string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + cap;

                    // 原始判定（只看 stop==0）
                    int rawCount = 0;
                    // 加进程校验后的判定
                    int verifiedCount = 0;

                    foreach (bool packaged in new[] { true, false })
                    {
                        string path = packaged ? root : root + "\\NonPackaged";
                        using (Microsoft.Win32.RegistryKey k =
                            Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path))
                        {
                            if (k == null) continue;

                            foreach (string sub in k.GetSubKeyNames())
                            {
                                using (Microsoft.Win32.RegistryKey a = k.OpenSubKey(sub))
                                {
                                    if (a == null) continue;
                                    object stopObj = a.GetValue("LastUsedTimeStop");
                                    if (stopObj == null || !(stopObj is long)) continue;
                                    if ((long)stopObj != 0) continue;

                                    rawCount++;

                                    bool alive = IsOwnerAlive(sub, packaged);
                                    report.AppendLine(string.Format("  {0,-52} 原始=占用 进程存活={1}",
                                        Truncate(sub, 50), alive));
                                    if (alive) verifiedCount++;
                                }
                            }
                        }
                    }

                    report.AppendLine("  原始判定数=" + rawCount);
                    report.AppendLine("  校验后数=" + verifiedCount);
                    report.AppendLine("  剔除的僵尸记录=" + (rawCount - verifiedCount));
                    report.AppendLine();
                }

                report.AppendLine("结论：若『剔除的僵尸记录』> 0，说明误报校验是必要的。");
            }
            catch (Exception ex)
            {
                report.AppendLine("FP_ERROR: " + ex);
            }

            try { File.WriteAllText(txtPath, report.ToString(), new UTF8Encoding(false)); }
            catch (Exception) { }
            return 0;
        }

        private static bool IsOwnerAlive(string keyName, bool packaged)
        {
            try
            {
                if (packaged) return true;   // UWP 无明显 exe 映射，放行

                string path = keyName.Replace('#', '\\');
                int slash = path.LastIndexOf('\\');
                string exe = slash >= 0 ? path.Substring(slash + 1) : path;
                if (string.IsNullOrEmpty(exe)) return false;

                string baseName = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? exe.Substring(0, exe.Length - 4) : exe;

                System.Diagnostics.Process[] found =
                    System.Diagnostics.Process.GetProcessesByName(baseName);
                try { return found.Length > 0; }
                finally { foreach (System.Diagnostics.Process p in found) p.Dispose(); }
            }
            catch (Exception) { return false; }
        }

        private static string Truncate(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        private static bool CanReadConsentStore()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone"))
                {
                    return k != null;
                }
            }
            catch (Exception) { return false; }
        }

        private static string DescribeActive(string cap)
        {
            try
            {
                string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\" + cap;
                foreach (string path in new[] { root, root + "\\NonPackaged" })
                {
                    using (Microsoft.Win32.RegistryKey k =
                        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path))
                    {
                        if (k == null) continue;
                        foreach (string sub in k.GetSubKeyNames())
                        {
                            using (Microsoft.Win32.RegistryKey a = k.OpenSubKey(sub))
                            {
                                if (a == null) continue;
                                object stop = a.GetValue("LastUsedTimeStop");
                                if (stop == null) continue;
                                if (!(stop is long)) continue;
                                if ((long)stop == 0) return sub;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        // ---------------------------------------------------------------
        // 截图与诊断
        // ---------------------------------------------------------------

        /// <summary>
        /// 抓一个窗口在屏幕上的真实样子（含 DWM 合成：Acrylic 背板、标题栏、圆角、阴影）。
        /// RenderTargetBitmap 拿不到这些 —— 它们是系统合成的，不在 WPF 的渲染树里。
        /// </summary>
        private static void CaptureWindowRect(Window win, string pngPath, StringBuilder sb)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(win).Handle;
                RECT r;
                if (!GetWindowRect(hwnd, out r))
                {
                    sb.AppendLine("LIVE_SHOT_ERR: GetWindowRect failed");
                    return;
                }

                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0)
                {
                    sb.AppendLine("LIVE_SHOT_ERR: empty rect " + w + "x" + h);
                    return;
                }

                using (Bitmap bmp = new Bitmap(w, h,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
                    bmp.Save(pngPath, ImageFormat.Png);
                }
                sb.AppendLine("LIVE_SHOT=" + pngPath + " " + w + "x" + h);
            }
            catch (Exception ex)
            {
                sb.AppendLine("LIVE_SHOT_ERR: " + ex.Message);
            }
        }

        private static void Dump(IslandWindow win, StringBuilder sb, string pngPath, string state)        {
            IntPtr hwnd = new WindowInteropHelper(win).Handle;
            sb.AppendLine();
            sb.AppendLine("===== STATE=" + state + " =====");
            sb.AppendLine("HWND=" + hwnd);
            sb.AppendLine("IsVisible=" + win.IsVisible);
            sb.AppendLine("ActualWidth=" + win.ActualWidth);
            sb.AppendLine("ActualHeight=" + win.ActualHeight);
            sb.AppendLine("Left=" + win.Left + " Top=" + win.Top);
            sb.AppendLine("Opacity=" + win.Opacity);

            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            sb.AppendLine("ExStyle=0x" + ex.ToString("X8"));
            sb.AppendLine("  WS_EX_NOACTIVATE=" + ((ex & NativeMethods.WS_EX_NOACTIVATE) != 0));
            sb.AppendLine("  WS_EX_TOOLWINDOW=" + ((ex & NativeMethods.WS_EX_TOOLWINDOW) != 0));
            sb.AppendLine("  WS_EX_LAYERED=" + ((ex & NativeMethods.WS_EX_LAYERED) != 0));

            RECT r;
            if (GetWindowRect(hwnd, out r))
            {
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                sb.AppendLine("ScreenRect=" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom);
                sb.AppendLine("ScreenSize=" + w + "x" + h);

                if (w > 0 && h > 0)
                {
                    using (Bitmap bmp = new Bitmap(w, h,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(r.Left, r.Top, 0, 0, new System.Drawing.Size(w, h));
                        using (Bitmap big = new Bitmap(w * 2, h * 2))
                        using (Graphics g2 = Graphics.FromImage(big))
                        {
                            g2.InterpolationMode =
                                System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                            g2.DrawImage(bmp, 0, 0, w * 2, h * 2);
                            big.Save(pngPath, ImageFormat.Png);
                        }
                    }
                    sb.AppendLine("SCREENSHOT=" + pngPath);
                }
            }

            sb.AppendLine("VisualChildCount=" + VisualTreeHelper.GetChildrenCount(win));
        }

        [System.Runtime.InteropServices.StructLayout(
            System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    }
}
