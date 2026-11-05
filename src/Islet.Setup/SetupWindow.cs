using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Islet.Setup
{
    /// <summary>
    /// 图形安装界面。
    ///
    /// 为什么要自己写一个而不是继续用 IExpress：
    /// IExpress 产物是「解压 + 弹一个黑框 cmd」，用户看到的是满屏英文命令刷过、
    /// 最后停在那里等 timeout；杀毒软件也常把自解压包判成可疑。
    /// 一个正经的 exe 有标题、有安装位置、有进度，信任感和可控感完全不一样。
    ///
    /// 界面沿用浮屿自己的观感：白玻璃 + 深蓝强调色，和设置窗是同一套视觉语言。
    /// </summary>
    internal sealed class SetupWindow : Window
    {
        private static readonly FontFamily UiFont =
            new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, SimSun");

        private static readonly Color AccentFrom = Color.FromRgb(0x3A, 0x62, 0xB0);
        private static readonly Color AccentTo = Color.FromRgb(0x5B, 0x8B, 0xE0);
        private static readonly Color TextMain = Color.FromRgb(0x11, 0x15, 0x1C);
        private static readonly Color TextDim = Color.FromArgb(0xC0, 0x1B, 0x1F, 0x28);

        private readonly TextBox _dirBox;
        private readonly CheckBox _desktop;
        private readonly CheckBox _startMenu;
        private readonly CheckBox _autorun;
        private readonly TextBlock _status;
        private readonly TextBlock _hint;
        private readonly Border _progressFill;
        private readonly Grid _progressTrack;
        private readonly Button _installBtn;
        private readonly Button _browseBtn;
        private readonly StackPanel _optionsPanel;

        private bool _installing;
        private bool _done;

        public SetupWindow()
        {
            Title = "浮屿 Islet 安装程序";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.White;
            FontFamily = UiFont;
            ShowInTaskbar = true;

            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            // ---- 顶部标题区 ----
            StackPanel head = new StackPanel();
            head.Children.Add(new TextBlock
            {
                Text = "浮屿 Islet",
                FontSize = 21,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextMain)
            });
            head.Children.Add(new TextBlock
            {
                Text = "常驻桌面的液态玻璃小岛 · 版本 " + Installer.Version,
                FontSize = 12,
                Foreground = new SolidColorBrush(TextDim),
                Margin = new Thickness(0, 4, 0, 0)
            });

            // ---- 安装位置 ----
            StackPanel dirBlock = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            dirBlock.Children.Add(Label("安装位置"));
            Grid dirRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            dirRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _dirBox = new TextBox
            {
                Text = Installer.DefaultTargetDir,
                FontSize = 12,
                Padding = new Thickness(9, 6, 9, 6),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x1B, 0x1F, 0x28)),
                Background = Brushes.White,
                Foreground = new SolidColorBrush(TextMain),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(_dirBox, 0);
            dirRow.Children.Add(_dirBox);

            _browseBtn = MakeButton("浏览…", false);
            _browseBtn.Margin = new Thickness(8, 0, 0, 0);
            _browseBtn.Click += (s, e) => BrowseForFolder();
            Grid.SetColumn(_browseBtn, 1);
            dirRow.Children.Add(_browseBtn);

            dirBlock.Children.Add(dirRow);
            dirBlock.Children.Add(new TextBlock
            {
                Text = "默认装在当前用户目录，不需要管理员权限。",
                FontSize = 11,
                Foreground = new SolidColorBrush(TextDim),
                Margin = new Thickness(0, 5, 0, 0)
            });

            // ---- 选项 ----
            StackPanel optBlock = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            optBlock.Children.Add(Label("选项"));

            _desktop = MakeCheck("创建桌面快捷方式", true);
            _startMenu = MakeCheck("创建开始菜单快捷方式", true);
            _autorun = MakeCheck("开机自动启动", false);

            _optionsPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            _optionsPanel.Children.Add(_desktop);
            _optionsPanel.Children.Add(_startMenu);
            _optionsPanel.Children.Add(_autorun);
            optBlock.Children.Add(_optionsPanel);

            // ---- 进度 ----
            _progressTrack = new Grid
            {
                Height = 4,
                Margin = new Thickness(0, 18, 0, 0),
                Visibility = Visibility.Collapsed
            };
            Border trackBg = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromArgb(0x24, 0x1B, 0x1F, 0x28))
            };
            _progressFill = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = AccentGradient(),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0
            };
            _progressTrack.Children.Add(trackBg);
            _progressTrack.Children.Add(_progressFill);
            _progressTrack.SizeChanged += (s, e) =>
            {
                // 进度条是「不确定进度」的来回扫，宽度取轨道的一个比例
                if (_progressTrack.ActualWidth > 0)
                    _progressFill.Width = _progressTrack.ActualWidth * 0.3;
            };

            // ---- 状态行 ----
            _status = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(TextDim),
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            _hint = new TextBlock
            {
                FontSize = 11.5,
                Foreground = new SolidColorBrush(TextDim),
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Visibility = Visibility.Collapsed
            };

            // ---- 按钮 ----
            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };

            Button cancel = MakeButton("取消", false);
            cancel.Click += (s, e) =>
            {
                if (_installing) return;      // 安装中不许关，避免装一半
                Close();
            };

            _installBtn = MakeButton("安装", true);
            _installBtn.MinWidth = 96;
            _installBtn.Margin = new Thickness(8, 0, 0, 0);
            _installBtn.Click += (s, e) => StartInstall();

            buttons.Children.Add(cancel);
            buttons.Children.Add(_installBtn);

            // ---- 组装 ----
            StackPanel root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(head);
            root.Children.Add(dirBlock);
            root.Children.Add(optBlock);
            root.Children.Add(_progressTrack);
            root.Children.Add(_status);
            root.Children.Add(_hint);
            root.Children.Add(buttons);

            Content = root;

            SourceInitialized += (s, e) => ApplyGlass();

            // 安装包完整性由 Program 在解包后用 Invalidate 传进来
            if (Installer.HasExisting(_dirBox.Text))
            {
                _hint.Text = "检测到已安装旧版本，安装会直接覆盖（配置与日志会保留）。";
                _hint.Visibility = Visibility.Visible;
            }

            Closing += (s, e) =>
            {
                if (_installing && !_done) e.Cancel = true;
            };
        }

        /// <summary>
        /// 让窗口进入「不可安装」状态。Program 在解包失败时调用，
        /// 避免用户点了安装才发现缺文件。
        /// </summary>
        public void Invalidate(string reason)
        {
            _status.Text = reason;
            _status.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x54, 0x09));
            _installBtn.IsEnabled = false;
            _installBtn.Opacity = 0.5;
            _installBtn.Content = "无法安装";
        }

        // ---------------------------------------------------------------
        // 安装流程
        // ---------------------------------------------------------------

        private void StartInstall()
        {
            if (_installing) return;

            string dir = _dirBox.Text == null ? "" : _dirBox.Text.Trim();
            if (dir.Length == 0)
            {
                _status.Text = "请先填写安装位置。";
                return;
            }

            // 路径里不能有非法字符，也不能是盘符根目录
            try
            {
                dir = System.IO.Path.GetFullPath(dir);
            }
            catch (Exception)
            {
                _status.Text = "安装位置不是合法路径。";
                return;
            }

            if (!Installer.IsWritable(dir))
            {
                _status.Text = "这个位置没有写入权限：" + dir
                             + "\n请换一个目录，或改用默认位置。";
                _status.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x54, 0x09));
                return;
            }

            _installing = true;
            _installBtn.IsEnabled = false;
            _installBtn.Content = "安装中…";
            _installBtn.Opacity = 0.6;
            _browseBtn.IsEnabled = false;
            _dirBox.IsEnabled = false;
            _optionsPanel.IsEnabled = false;
            _status.Foreground = new SolidColorBrush(TextDim);
            _hint.Visibility = Visibility.Collapsed;
            _progressTrack.Visibility = Visibility.Visible;
            StartIndeterminate();

            bool desktop = _desktop.IsChecked == true;
            bool startMenu = _startMenu.IsChecked == true;
            bool autorun = _autorun.IsChecked == true;

            // UI 线程不能阻塞在文件复制上，但这里工作量很小（几百 KB），
            // 用 Dispatcher 排队反而会让「安装中」三个字来不及画出来。
            // 折中：先把界面刷新一次，再同步装完。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    Installer.Install(dir, desktop, startMenu, autorun,
                        text => { _status.Text = text; PumpUi(); });

                    _done = true;
                    StopIndeterminate();
                    _progressTrack.Visibility = Visibility.Collapsed;

                    _status.Text = "安装完成，正在启动浮屿…";
                    _status.Foreground = new SolidColorBrush(Color.FromRgb(0x1F, 0x6F, 0x3C));

                    Installer.Launch(dir);

                    _installBtn.Content = "完成";
                    _installBtn.IsEnabled = true;
                    _installBtn.Opacity = 1.0;
                    _installing = false;
                    _installBtn.Click -= CompleteAndClose;
                    _installBtn.Click += CompleteAndClose;

                    Close();
                }
                catch (Exception ex)
                {
                    _done = false;
                    _installing = false;
                    StopIndeterminate();
                    _progressTrack.Visibility = Visibility.Collapsed;

                    _status.Text = "安装失败：" + ex.Message;
                    _status.Foreground = new SolidColorBrush(Color.FromRgb(0xA3, 0x2A, 0x2A));

                    _installBtn.Content = "重试";
                    _installBtn.IsEnabled = true;
                    _installBtn.Opacity = 1.0;
                    _browseBtn.IsEnabled = true;
                    _dirBox.IsEnabled = true;
                    _optionsPanel.IsEnabled = true;
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void CompleteAndClose(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>把挂起的布局/绘制跑掉，让状态文字能真的显示出来。</summary>
        private void PumpUi()
        {
            try
            {
                Dispatcher.Invoke(new Action(() => { }),
                    System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception) { }
        }

        private void StartIndeterminate()
        {
            if (_progressFill.Width <= 0) return;

            double track = _progressTrack.ActualWidth;
            if (track <= 0) track = 460;

            DoubleAnimation a = new DoubleAnimation
            {
                From = 0,
                To = Math.Max(0, track - _progressFill.Width),
                Duration = new Duration(TimeSpan.FromMilliseconds(1100)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            _progressFill.BeginAnimation(WidthProperty, a);
        }

        private void StopIndeterminate()
        {
            _progressFill.BeginAnimation(WidthProperty, null);
        }

        // ---------------------------------------------------------------
        // 浏览目录
        // ---------------------------------------------------------------

        private void BrowseForFolder()
        {
            // WPF 没有内置的「选文件夹」对话框，.NET Framework 4.8 里
            // FolderBrowserDialog 来自 System.Windows.Forms，直接用即可。
            using (System.Windows.Forms.FolderBrowserDialog dlg =
                   new System.Windows.Forms.FolderBrowserDialog())
            {
                dlg.Description = "选择浮屿的安装位置";
                dlg.ShowNewFolderButton = true;
                try
                {
                    string current = System.IO.Path.GetFullPath(_dirBox.Text);
                    if (System.IO.Directory.Exists(current)) dlg.SelectedPath = current;
                }
                catch (Exception) { }

                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    // 用户选的是父目录时，自动补一层 Islet，避免直接铺在用户目录里
                    string picked = dlg.SelectedPath;
                    if (string.Equals(picked.TrimEnd('\\'),
                                      Installer.DefaultTargetDir.TrimEnd('\\'),
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        _dirBox.Text = picked;
                    }
                    else
                    {
                        _dirBox.Text = System.IO.Path.Combine(picked, "Islet");
                    }
                }
            }
        }

        // ---------------------------------------------------------------
        // 视觉
        // ---------------------------------------------------------------

        /// <summary>
        /// 和设置窗同一套：浅玻璃 + 圆角 + 浅色标题栏。
        /// 安装程序的窗口比较小，背板用 Acrylic 会更通透。
        /// </summary>
        private void ApplyGlass()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int light = 0;
                DwmSetWindowAttribute(hwnd, 20, ref light, sizeof(int));

                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            }
            catch (Exception) { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr,
                                                        ref int value, int size);

        private static Brush AccentGradient()
        {
            LinearGradientBrush b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            b.GradientStops.Add(new GradientStop(AccentFrom, 0.0));
            b.GradientStops.Add(new GradientStop(AccentTo, 1.0));
            return b;
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0x2C, 0x5C, 0xB8))
            };
        }

        private static CheckBox MakeCheck(string text, bool initial)
        {
            return new CheckBox
            {
                Content = text,
                IsChecked = initial,
                FontSize = 12,
                Foreground = new SolidColorBrush(TextMain),
                Margin = new Thickness(0, 4, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static Button MakeButton(string text, bool primary)
        {
            Button b = new Button
            {
                Content = text,
                FontSize = 12,
                Padding = new Thickness(18, 7, 18, 7),
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Foreground = primary ? Brushes.White : new SolidColorBrush(TextMain),
                Background = primary
                    ? AccentGradient()
                    : new SolidColorBrush(Color.FromArgb(0x28, 0x1B, 0x1F, 0x28)),
                Template = PillTemplate()
            };
            return b;
        }

        private static ControlTemplate PillTemplate()
        {
            ControlTemplate t = new ControlTemplate(typeof(Button));
            FrameworkElementFactory border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Button.BackgroundProperty));
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

            Trigger disabled = new Trigger
            {
                Property = IsEnabledProperty,
                Value = false
            };
            disabled.Setters.Add(new Setter(Border.OpacityProperty, 0.5, "Bd"));
            t.Triggers.Add(disabled);

            return t;
        }
    }
}
