using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Islet.App.Interop;
using Islet.Core;

namespace Islet.App.Views
{
    /// <summary>
    /// 设置窗口。纯代码构建（本机无 XAML 编译器）。
    ///
    /// 交互原则：改任何一项**立即生效并落盘**，没有「确定/取消」按钮。
    /// 常驻小工具不该让用户记得点保存。
    /// </summary>
    internal sealed class SettingsWindow : Window
    {
        private static readonly FontFamily UiFont =
            new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, SimSun");

        // ================= 液态玻璃（与岛同一套材料） =================
        //
        // 岛（IslandWindow）是浮在桌面上的分层窗口，白雾玻璃真的透出壁纸。
        // 设置窗是**普通窗口**（保留系统边框、拖动、滚动条，不去手搓 chrome），
        // 所以通透感靠两层叠出来：
        //   1. DWM 背板（Acrylic）：窗口背后的内容被系统实时模糊 —— 这是真透；
        //   2. 半透明白雾 + 亮边 + 顶部高光：岛的同一套涂层，负责「玻璃厚度」的观感。
        // 文字沿用岛的深色，浅玻璃上才读得清。

        private static readonly Brush TextPrimary =
            new SolidColorBrush(Color.FromRgb(0x11, 0x15, 0x1C));
        private static readonly Brush TextSecondary =
            new SolidColorBrush(Color.FromArgb(0xC0, 0x1B, 0x1F, 0x28));
        /// <summary>强调色：和岛的 Accent 一致（深蓝），浅底上不刺眼。</summary>
        private static readonly Color Accent = Color.FromRgb(0x2C, 0x5C, 0xB8);
        /// <summary>选中态药丸用的渐变两端，取自岛上的进度条。</summary>
        private static readonly Color AccentFrom = Color.FromRgb(0x3A, 0x62, 0xB0);
        private static readonly Color AccentTo = Color.FromRgb(0x5B, 0x8B, 0xE0);

        /// <summary>窗口底：135° 白雾渐变。alpha 不是 255 —— 下面垫着 DWM 背板。</summary>
        private static readonly Brush CardBg = WindowGlass();

        /// <summary>卡片：比窗口底再亮一层的白，读起来像玻璃上贴的一格。</summary>
        private static readonly Brush RowBg = CardGlass();

        /// <summary>卡片描边：上缘提亮、下缘压暗，模拟玻璃切边的受光。</summary>
        private static readonly Brush CardRim = RimBrush();

        /// <summary>DWM 背板类型：3 = Acrylic（实时模糊背后内容），2 = Mica（只采样壁纸）。</summary>
        private const int BackdropType = 3;


        private readonly IsletSettings _settings;
        private readonly Action<IsletSettings> _onChanged;
        private readonly Func<string> _autorunStatus;
        private readonly bool _autorunBlocked;
        private readonly TextBlock _autorunHint;

        /// <param name="onChanged">任一设置变更时回调，参数是完整的新设置。</param>
        /// <param name="autorunStatus">返回当前自启机制描述（注册表 / 启动文件夹 / 未启用）。</param>
        /// <param name="autorunBlocked">是否曾因系统限制导致开启失败。</param>
        public SettingsWindow(IsletSettings settings, Action<IsletSettings> onChanged,
                              Func<string> autorunStatus = null,
                              bool autorunBlocked = false)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            _settings = settings.Clone();
            _onChanged = onChanged;
            _autorunStatus = autorunStatus;
            _autorunBlocked = autorunBlocked;
            _autorunHint = new TextBlock
            {
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            Title = "浮屿设置";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = CardBg;
            FontFamily = UiFont;
            ShowInTaskbar = true;
            Topmost = true;

            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            Content = WrapWithGlass(BuildContent());
            SourceInitialized += OnSourceInitialized;
        }

        // ---------------------------------------------------------------
        // 液态玻璃：材料 + DWM 背板
        // ---------------------------------------------------------------

        /// <summary>
        /// 窗口底：135° 白雾渐变（和岛的 GlassBrush 同一套配色与方向）。
        /// alpha ≈ 0xD0：让下面 Acrylic 模糊出来的桌面还能隐约看见，
        /// 但不至于透到影响小字阅读。
        /// </summary>
        private static Brush WindowGlass()
        {
            LinearGradientBrush b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xBE, 0xFC, 0xFD, 0xFF), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xB2, 0xF4, 0xF7, 0xFC), 0.45));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xA8, 0xE8, 0xEE, 0xF8), 1.0));
            return b;
        }

        /// <summary>卡片：白色半透，上亮下暗 —— 叠在窗口底上就是「玻璃上再贴一格玻璃」。</summary>
        private static Brush CardGlass()
        {
            LinearGradientBrush b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x84, 0xFF, 0xFF, 0xFF), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x4E, 0xFF, 0xFF, 0xFF), 1.0));
            return b;
        }

        /// <summary>描边：上缘提亮、下缘压暗，模拟玻璃切边的受光（岛上的 RimBrush 同思路）。</summary>
        private static Brush RimBrush()
        {
            LinearGradientBrush b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x22, 0x6E, 0x7A, 0x90), 0.55));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0x5A, 0x66, 0x7A), 1.0));
            return b;
        }

        /// <summary>顶部高光：岛的 _spec 对应物，让人相信这是一块有厚度的玻璃。</summary>
        private static Brush SpecBrush()
        {
            RadialGradientBrush b = new RadialGradientBrush
            {
                Center = new Point(0.5, -0.15),
                GradientOrigin = new Point(0.5, -0.15),
                RadiusX = 0.85,
                RadiusY = 0.9
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x34, 0xFF, 0xFF, 0xFF), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.55));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1.0));
            return b;
        }

        /// <summary>开关关态：浅玻璃上「白=凸起」，所以关态用极淡的深色，读作凹陷。</summary>
        private static Brush ToggleOff()
        {
            return new SolidColorBrush(Color.FromArgb(0x38, 0x1B, 0x1F, 0x28));
        }

        /// <summary>选中态药丸的填充：岛的进度条同一组蓝。</summary>
        private static Brush AccentFill()
        {
            LinearGradientBrush b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            b.GradientStops.Add(new GradientStop(AccentFrom, 0.0));
            b.GradientStops.Add(new GradientStop(AccentTo, 1.0));
            return b;
        }

        /// <summary>未选中态药丸：白玻璃，带一点强调色的边。</summary>
        private static Brush PillIdle()
        {
            return new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        }

        /// <summary>给内容铺一层顶部高光。不参与命中测试，滚动手感不受影响。</summary>
        private static UIElement WrapWithGlass(UIElement content)
        {
            Grid host = new Grid();
            host.Children.Add(content);

            Border spec = new Border
            {
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Top,
                Height = 130,
                Background = SpecBrush()
            };
            host.Children.Add(spec);
            return host;
        }

        /// <summary>
        /// 让这个普通窗口（保留系统边框）也真的透出背后的桌面：
        /// DWM 的 Acrylic 背板 + 把边框扩到整个客户区。
        ///
        /// 为什么要这套而不是自己去画窗口：设置窗有滚动条、要能缩放/拖动、
        /// 用户还要点几十个开关，手搓 chrome 的收益远小于风险。
        /// 拿不到背板时（旧系统）只是变回不透明的白玻璃，不影响任何功能。
        /// </summary>
        private void OnSourceInitialized(object sender, EventArgs e)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                // 浅色玻璃必须配浅色标题栏，否则系统深色标题栏压在浅内容上很脏
                int light = 0;
                NativeMethods.DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode,
                                                    ref light, sizeof(int));

                int round = NativeMethods.DWMWCP_ROUND;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE,
                                                    ref round, sizeof(int));

                int backdrop = BackdropType;
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE,
                                                    ref backdrop, sizeof(int));

                NativeMethods.MARGINS m = new NativeMethods.MARGINS();
                m.Left = -1;
                m.Top = -1;
                m.Right = -1;
                m.Bottom = -1;
                NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref m);
            }
            catch (Exception ex)
            {
                Log.Info("设置窗玻璃背板不可用（降级为不透明白玻璃）: " + ex.Message);
            }
        }

        /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE。0 = 浅色标题栏。</summary>
        private const int DwmwaUseImmersiveDarkMode = 20;


        private UIElement BuildContent()
        {
            StackPanel root = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };

            // ---- 标题 ----
            root.Children.Add(new TextBlock
            {
                Text = "浮屿设置",
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary
            });
            root.Children.Add(new TextBlock
            {
                Text = "改动立即生效并保存",
                FontSize = 11.5,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 3, 0, 0)
            });

            // ---- 数据源 ----
            root.Children.Add(SectionHeader("数据源", "关掉后该来源不再上岛"));

            root.Children.Add(ToggleRow("媒体播放", "音乐、视频的播控与进度",
                _settings.MediaEnabled, v => { _settings.MediaEnabled = v; Commit(); }));

            root.Children.Add(ToggleRow("倒计时 / 番茄钟", "到点强提醒",
                _settings.TimerEnabled, v => { _settings.TimerEnabled = v; Commit(); }));

            root.Children.Add(ToggleRow("下载与文件进度", "监视下载目录里的正在增长的文件",
                _settings.ProgressEnabled, v => { _settings.ProgressEnabled = v; Commit(); }));

            root.Children.Add(ToggleRow("系统体征", "内存、CPU、电量、磁盘越界时提醒",
                _settings.HealthEnabled, v => { _settings.HealthEnabled = v; Commit(); }));

            root.Children.Add(ToggleRow("剪贴板识别", "验证码、快递单号、链接、手机号、金额",
                _settings.ClipboardEnabled, v => { _settings.ClipboardEnabled = v; Commit(); }));

            root.Children.Add(ToggleRow("隐私占用提醒", "麦克风或摄像头被打开时立刻告知",
                _settings.PrivacyEnabled, v => { _settings.PrivacyEnabled = v; Commit(); }));

            root.Children.Add(ToggleRow("蓝牙耳机连接", "耳机连上或断开时提示 · 不显示电量（设备不支持）",
                _settings.BluetoothEnabled, v => { _settings.BluetoothEnabled = v; Commit(); }));

            // ---- 外观 ----
            root.Children.Add(SectionHeader("外观与行为", null));

            root.Children.Add(PositionRow());
            root.Children.Add(OpacityRow());

            root.Children.Add(ToggleRow("全屏时自动隐身", "游戏、放映、视频全屏时不打扰",
                _settings.HideInFullscreen, v => { _settings.HideInFullscreen = v; Commit(); }));

            // ---- 工具 ----
            root.Children.Add(SectionHeader("工具", null));
            root.Children.Add(VolumeEntryRow());

            // ---- 系统 ----
            root.Children.Add(SectionHeader("系统", null));

            root.Children.Add(ToggleRow("开机自动启动", "登录 Windows 后自动运行浮屿",
                _settings.AutorunEnabled,
                v =>
                {
                    _settings.AutorunEnabled = v;
                    Commit();
                    RefreshAutorunHint();
                },
                _autorunHint));

            RefreshAutorunHint();

            root.Children.Add(ToggleRow("空闲时整理内存", "隐藏态时回收工作集，降低常驻占用",
                _settings.TrimMemoryWhenIdle, v => { _settings.TrimMemoryWhenIdle = v; Commit(); }));

            // ---- 页脚 ----
            Border footer = new Border
            {
                Background = RowBg,
                BorderBrush = CardRim,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 11, 13, 11),
                Margin = new Thickness(0, 18, 0, 0)
            };
            StackPanel footerStack = new StackPanel();
            footerStack.Children.Add(new TextBlock
            {
                Text = "配置文件",
                FontSize = 11.5,
                Foreground = TextSecondary
            });
            footerStack.Children.Add(new TextBlock
            {
                Text = @"%LOCALAPPDATA%\Islet\settings.ini",
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 2, 0, 0)
            });
            footerStack.Children.Add(new TextBlock
            {
                Text = "热键 Ctrl+Alt+I 显示/隐藏",
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 6, 0, 0)
            });
            footer.Child = footerStack;
            root.Children.Add(footer);

            ScrollViewer sv = new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 720
            };
            return sv;
        }

        /// <summary>
        /// 刷新自启状态提示。用户需要看到「真的写进去了没有」，
        /// 而不是一个可能骗人的开关颜色。
        /// </summary>
        private void RefreshAutorunHint()
        {
            if (_autorunHint == null) return;

            if (!_settings.AutorunEnabled)
            {
                _autorunHint.Text = "已关闭，登录后不会自动运行";
                _autorunHint.Foreground = TextSecondary;
                return;
            }

            if (_autorunBlocked)
            {
                _autorunHint.Text = "系统限制了写入自启项（组策略或安全软件），"
                                  + "可按住 Shift 右键以管理员身份运行后再试";
                _autorunHint.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x54, 0x09));
                return;
            }

            string mech = _autorunStatus == null ? null : _autorunStatus();
            _autorunHint.Text = string.IsNullOrEmpty(mech) || mech == "未启用"
                ? "已开启（尚未写入，重启后会再次尝试）"
                : "已开启 · 生效方式：" + mech;
            _autorunHint.Foreground = new SolidColorBrush(Color.FromRgb(130, 210, 150));
        }

        private void Commit()
        {
            if (_onChanged != null) _onChanged(_settings.Clone());
        }

        // ---------------------------------------------------------------
        // 控件工厂
        // ---------------------------------------------------------------

        private static UIElement SectionHeader(string title, string hint)
        {
            StackPanel sp = new StackPanel { Margin = new Thickness(0, 18, 0, 7) };
            sp.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                // 浅玻璃上的小标题用深蓝（岛的 Accent），不用浅蓝 —— 浅蓝在白底上糊掉了
                Foreground = new SolidColorBrush(Accent)
            });
            if (!string.IsNullOrEmpty(hint))
            {
                sp.Children.Add(new TextBlock
                {
                    Text = hint,
                    FontSize = 11,
                    Foreground = TextSecondary,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            return sp;
        }

        private static UIElement ToggleRow(string title, string hint,
                                           bool initial, Action<bool> onSet,
                                           TextBlock extraLine = null)
        {
            Border card = new Border
            {
                Background = RowBg,
                BorderBrush = CardRim,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 6)
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                Foreground = TextPrimary
            });
            text.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            if (extraLine != null) text.Children.Add(extraLine);
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            // 自绘开关：WPF 的 CheckBox 默认样式在玻璃上很难看。
            // 关态用「极淡的深色」而不是白色 —— 浅玻璃上白色是凸起，深色才是凹陷。
            Border track = new Border
            {
                Width = 38,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(1),
                BorderBrush = CardRim,
                Background = initial ? AccentFill() : ToggleOff(),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            Border knob = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = Brushes.White,
                HorizontalAlignment = initial ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 0, 3, 0)
            };
            track.Child = knob;

            bool value = initial;
            track.MouseLeftButtonUp += (s, e) =>
            {
                value = !value;
                track.Background = value ? AccentFill() : ToggleOff();
                knob.HorizontalAlignment = value
                    ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                onSet(value);
            };

            Grid.SetColumn(track, 1);
            grid.Children.Add(track);

            card.Child = grid;
            return card;
        }

        private UIElement PositionRow()
        {
            Border card = new Border
            {
                Background = RowBg,
                BorderBrush = CardRim,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 6)
            };

            StackPanel sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = "停靠位置",
                FontSize = 12.5,
                Foreground = TextPrimary
            });

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 7, 0, 0)
            };
            IslandPosition[] all = SettingsCodec.AllPositions();
            System.Collections.Generic.List<Button> btns =
                new System.Collections.Generic.List<Button>();

            foreach (IslandPosition p in all)
            {
                IslandPosition captured = p;
                Button b = new Button
                {
                    Content = SettingsCodec.Describe(p),
                    FontSize = 11.5,
                    Padding = new Thickness(14, 5, 14, 5),
                    Margin = new Thickness(0, 0, 7, 0),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Foreground = TextPrimary,
                    // 必须给足最小宽度：否则胶囊会挤在左侧互相压字
                    MinWidth = 74,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                b.Template = PillTemplate();
                StylePill(b, p == _settings.Position);

                b.Click += (s, e) =>
                {
                    _settings.Position = captured;
                    foreach (Button other in btns)
                    {
                        IslandPosition op = (IslandPosition)other.Tag;
                        StylePill(other, op == captured);
                    }
                    Commit();
                };
                b.Tag = p;
                btns.Add(b);
                buttons.Children.Add(b);
            }

            sp.Children.Add(buttons);

            sp.Children.Add(new TextBlock
            {
                Text = "选「自由拖动」后，直接按住胶囊拖到任意位置",
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            card.Child = sp;
            return card;
        }

        /// <summary>
        /// 透明度三档。三个都是液态玻璃，只有基底密度不同。
        /// 每颗药丸带一个小的玻璃预览色块，让用户不必反复试就能看出差别。
        /// </summary>
        private UIElement OpacityRow()
        {
            Border card = new Border
            {
                Background = RowBg,
                BorderBrush = CardRim,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 6)
            };

            StackPanel sp = new StackPanel();
            sp.Children.Add(new TextBlock
            {
                Text = "玻璃透明度",
                FontSize = 12.5,
                Foreground = TextPrimary
            });

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 7, 0, 0)
            };
            GlassOpacity[] all = SettingsCodec.AllOpacities();
            System.Collections.Generic.List<Button> btns =
                new System.Collections.Generic.List<Button>();

            foreach (GlassOpacity o in all)
            {
                GlassOpacity captured = o;
                Button b = new Button
                {
                    Content = SettingsCodec.Describe(o),
                    FontSize = 11.5,
                    Padding = new Thickness(13, 5, 13, 5),
                    Margin = new Thickness(0, 0, 7, 0),
                    BorderThickness = new Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Foreground = TextPrimary,
                    MinWidth = 96,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };
                b.Template = PillTemplate();
                StylePill(b, o == _settings.Opacity);

                b.Click += (s, e) =>
                {
                    _settings.Opacity = captured;
                    foreach (Button other in btns)
                    {
                        GlassOpacity oo = (GlassOpacity)other.Tag;
                        StylePill(other, oo == captured);
                    }
                    Commit();
                };
                b.Tag = o;
                btns.Add(b);
                buttons.Children.Add(b);
            }

            sp.Children.Add(buttons);
            sp.Children.Add(new TextBlock
            {
                Text = "越往右越透，背后的内容越明显；文字对比度也相应降低",
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            card.Child = sp;
            return card;
        }

        /// <summary>
        /// 「应用音量」入口行。
        ///
        /// 不做成开关，因为音量面板不是常驻功能 —— 它是个**按需打开的窗口**。
        /// 用一个按钮明确表达「点一下会弹东西出来」，比一个会误导人的开关好。
        /// </summary>
        private UIElement VolumeEntryRow()
        {
            Border card = new Border
            {
                Background = RowBg,
                BorderBrush = CardRim,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(13, 10, 13, 10),
                Margin = new Thickness(0, 0, 0, 6)
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = "应用音量",
                FontSize = 12.5,
                Foreground = TextPrimary
            });
            text.Children.Add(new TextBlock
            {
                Text = "单独调某个软件的响度，不动系统总音量 · 快捷键 Ctrl+Alt+V",
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            Button open = new Button
            {
                Content = "打开",
                FontSize = 11.5,
                Padding = new Thickness(16, 5, 16, 5),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                // 实心强调色按钮上必须用白字：深色字压在深蓝上读不出来
                Foreground = Brushes.White,
                Background = AccentFill(),
                VerticalAlignment = VerticalAlignment.Center
            };
            open.Template = PillTemplate();
            open.Click += (s, e) =>
            {
                VolumePanel p = new VolumePanel();
                // 以设置窗为所有者：设置窗在前时面板不会跑到后面去
                if (IsLoaded) p.Owner = this;
                p.Show();
            };
            Grid.SetColumn(open, 1);
            grid.Children.Add(open);

            card.Child = grid;
            return card;
        }

        /// <summary>
        /// 药丸按钮的选中 / 未选中配色。
        /// 选中态必须把文字转白：强调色是深蓝，深色字压上去完全读不出。
        /// </summary>
        private static void StylePill(Button b, bool selected)
        {
            b.Background = selected ? AccentFill() : PillIdle();
            b.Foreground = selected ? (Brush)Brushes.White : TextPrimary;
        }

        private static ControlTemplate PillTemplate()
        {
            ControlTemplate t = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            border.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Button.BackgroundProperty));
            // 模板必须自己转发 Padding，否则按钮的 Padding 设置不会生效，
            // 文字会顶到边缘、多个按钮看起来挤成一团。
            border.SetValue(Border.PaddingProperty,
                new TemplateBindingExtension(Button.PaddingProperty));

            FrameworkElementFactory presenter =
                new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            border.AppendChild(presenter);
            t.VisualTree = border;

            Trigger hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.OpacityProperty, 0.85, "Bd"));
            t.Triggers.Add(hover);

            return t;
        }
    }
}
