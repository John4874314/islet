using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Islet.Sources;

namespace Islet.App.Views
{
    /// <summary>
    /// 单应用音量面板（独立小窗，不占用岛的槽位）。
    ///
    /// 为什么不做成「岛内展开一列滑块」：
    /// 岛的核心是**单槽仲裁**——同一时刻只有一件事占据主位。音量调节是
    /// 「偶尔调一次」的动作，把它常驻在岛里会长期占着主位，破坏这个干净模型。
    /// 独立面板让岛保持原样，音量只在需要时出现。
    ///
    /// 交互要点：
    ///   - 每行一个应用：图标位（首字）＋名称＋静音键＋音量滑块
    ///   - 拖动滑块**立刻生效**，不做防抖（WASAPI 调用很轻，实测无卡顿）
    ///   - 面板打开期间定时刷新列表，但**拖动中的行不刷新**，
    ///     否则用户拖到一半会被外部读数拽回去
    ///   - 关闭即释放 COM，不留悬挂引用
    /// </summary>
    internal sealed class VolumePanel : Window
    {
        private static readonly FontFamily UiFont =
            new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, SimSun");

        private static readonly Brush TextPrimary = Brushes.White;
        private static readonly Brush TextSecondary =
            new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
        private static readonly Brush CardBg =
            new SolidColorBrush(Color.FromArgb(255, 30, 32, 40));
        private static readonly Brush RowBg =
            new SolidColorBrush(Color.FromArgb(255, 42, 45, 55));
        private static readonly Brush RowHover =
            new SolidColorBrush(Color.FromArgb(255, 50, 54, 66));
        private static readonly Brush AccentBrush =
            new SolidColorBrush(Color.FromRgb(76, 140, 245));

        private readonly AudioSessionProvider _provider = new AudioSessionProvider();
        private readonly StackPanel _list;
        private readonly TextBlock _status;
        private readonly DispatcherTimer _refresh;

        /// <summary>正在被用户拖动的会话 Key。刷新时跳过它，避免读数把滑块拽回去。</summary>
        private string _draggingKey;

        public VolumePanel()
        {
            Title = "应用音量";
            Width = 420;
            SizeToContent = SizeToContent.Height;
            MaxHeight = 560;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = CardBg;
            FontFamily = UiFont;
            ShowInTaskbar = false;
            Topmost = true;

            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            _list = new StackPanel();
            _status = new TextBlock
            {
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            Content = BuildContent();

            // 每秒刷新一次列表。比这更快没有意义（用户看不清变化），
            // 更慢则应用关掉后行会赖着不走。
            _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _refresh.Tick += (s, e) => Reload();

            Loaded += (s, e) => { Reload(); _refresh.Start(); };
            Closed += (s, e) =>
            {
                _refresh.Stop();
                _provider.Dispose();
            };
        }

        private UIElement BuildContent()
        {
            StackPanel root = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };

            root.Children.Add(new TextBlock
            {
                Text = "应用音量",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary
            });
            root.Children.Add(new TextBlock
            {
                Text = "拖动滑块即时生效，只影响该应用，不动系统总音量",
                FontSize = 11,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            ScrollViewer scroll = new ScrollViewer
            {
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Margin = new Thickness(0, 12, 0, 0),
                Content = _list
            };
            root.Children.Add(scroll);

            root.Children.Add(_status);

            // Esc 关闭：小工具不该逼人去点右上角
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            return root;
        }

        // ---------------------------------------------------------------
        // 列表渲染
        // ---------------------------------------------------------------

        private void Reload()
        {
            if (!_provider.Refresh())
            {
                _status.Text = "读取音频会话失败：" + (_provider.LastError ?? "未知原因");
                _status.Foreground = new SolidColorBrush(Color.FromRgb(240, 160, 100));
                return;
            }

            List<AudioSession> shown = new List<AudioSession>();
            foreach (AudioSession s in _provider.Sessions)
            {
                // 跳过已失效的会话
                if (s.State == 2) continue;
                if (!s.CanControl) continue;

                // 过滤「幽灵会话」：Windows 在进程退出后不会立刻删掉它的音频会话，
                // 而是把它留在 Inactive 状态，exe 路径变成「#」、PID 已不存在。
                // 实测本机上就有一个这样的残留，会以「未知应用」占着一行，
                // 用户既认不出也用不上，还容易误调。
                //
                // 保留规则（满足其一即可）：
                //   - 正在出声 → 一定要显示
                //   - 系统声音 → 有身份，允许保留
                //   - 能解析出 exe 路径 → 说明进程还在，只是暂停了
                if (!s.IsActive && !s.IsSystemSounds
                    && string.IsNullOrEmpty(s.ExePath)) continue;

                // 也不要把浮屿自己列进去 —— 自己调自己的音量没有意义，
                // 而且首次打开面板时 WPF 会建一个会话，用户会看到一个「Islet」行。
                if (!s.IsSystemSounds
                    && string.Equals(s.DisplayName, "Islet",
                                     StringComparison.OrdinalIgnoreCase)) continue;

                shown.Add(s);
            }

            // 排序：**只按名字**，绝不按「是否在播放」。
            //
            // 这里踩过一个真坑：最初把「正在发声的排前面」当作贴心设计，
            // 结果某个应用一停一播，整列顺序就变，用户正拖着的滑块底下换了个人。
            // 实测表现是「拖 powershell 那一行，改的却是系统声音」。
            // 顺序必须稳定，正在发声改用左侧高亮条表达（见 ApplyRowValues）。
            shown.Sort(delegate(AudioSession a, AudioSession b)
            {
                int byName = string.Compare(a.DisplayName, b.DisplayName,
                                            StringComparison.CurrentCultureIgnoreCase);
                if (byName != 0) return byName;
                // 同名的多个会话再按 Key 兜底，保证顺序完全确定
                return string.CompareOrdinal(a.Key ?? "", b.Key ?? "");
            });

            // 行数没变且身份一致时走「就地更新」，避免每秒重建控件
            // （重建会让正在拖动的滑块丢失鼠标捕获）
            if (CanUpdateInPlace(shown))
            {
                UpdateInPlace(shown);
            }
            else
            {
                RebuildRows(shown);
            }

            int active = 0;
            foreach (AudioSession s in shown) { if (s.IsActive) active++; }
            _status.Foreground = TextSecondary;
            _status.Text = shown.Count == 0
                ? "当前没有正在发声的应用。播放音频后这里会出现对应的行。"
                : "共 " + shown.Count + " 个会话，其中 " + active + " 个正在发声";
        }

        /// <summary>已渲染的行数/顺序是否与目标一致。</summary>
        private bool CanUpdateInPlace(List<AudioSession> shown)
        {
            if (_list.Children.Count != shown.Count) return false;
            for (int i = 0; i < shown.Count; i++)
            {
                Border row = _list.Children[i] as Border;
                if (row == null) return false;
                if (!object.Equals(row.Tag, shown[i].Key)) return false;
            }
            return true;
        }

        private void UpdateInPlace(List<AudioSession> shown)
        {
            for (int i = 0; i < shown.Count; i++)
            {
                Border row = (Border)_list.Children[i];
                RowRefs refs = row.Resources["refs"] as RowRefs;
                if (refs == null) continue;
                ApplyRowValues(refs, shown[i]);
            }
        }

        private void RebuildRows(List<AudioSession> shown)
        {
            _list.Children.Clear();
            foreach (AudioSession s in shown)
            {
                _list.Children.Add(BuildRow(s));
            }
        }

        private void ApplyRowValues(RowRefs refs, AudioSession s)
        {
            // 注意：这里**不缓存 Session 对象**，只缓存 Key。
            // provider 每次 Refresh 都会释放上一轮的 COM 控制接口，
            // 如果行里还攥着旧 AudioSession，拖动时写的就是已经释放的接口 ——
            // 表现为「拖了没反应，一秒后又弹回原值」。
            refs.Key = s.Key;

            // 正在拖动的行不刷新音量，否则会把用户的手感打断
            if (refs.Key != _draggingKey)
            {
                refs.Suppress = true;
                refs.Slider.Value = Math.Round(s.Volume * 100.0);
                refs.Suppress = false;
                refs.Percent.Text = ((int)Math.Round(s.Volume * 100)).ToString() + "%";
            }

            refs.MuteBtn.Content = s.Muted ? "🔇" : "🔊";
            refs.MuteBtn.Foreground = s.Muted
                ? new SolidColorBrush(Color.FromRgb(240, 120, 120))
                : TextPrimary;

            // 出声中的行左侧加一条高亮，替代「正在播放」文字，省空间
            refs.Accent.Background = s.IsActive
                ? AccentBrush
                : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        /// <summary>
        /// 按 Key 到**当前这轮**会话里找对应项。
        /// 每次写操作都必须走这里重新解析，绝不能复用上一轮缓存的 AudioSession。
        /// </summary>
        private AudioSession Resolve(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            foreach (AudioSession s in _provider.Sessions)
            {
                if (s.Key == key) return s;
            }
            return null;
        }

        // ---------------------------------------------------------------
        // 单行
        // ---------------------------------------------------------------

        private sealed class RowRefs
        {
            public string Key;
            public Slider Slider;
            public TextBlock Percent;
            public Button MuteBtn;
            public Border Accent;
            public bool Suppress;   // 程序化改值时抑制回调
        }

        private Border BuildRow(AudioSession s)
        {
            Border card = new Border
            {
                Background = RowBg,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6),
                Tag = s.Key
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 左侧「正在发声」高亮条
            Border accent = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(0, 2, 8, 2),
                Background = s.IsActive ? AccentBrush
                                        : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0))
            };
            Grid.SetColumn(accent, 0);
            grid.Children.Add(accent);

            // 主体：名称 + 滑块
            StackPanel body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock name = new TextBlock
            {
                Text = s.DisplayName,
                FontSize = 12.5,
                Foreground = TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            head.Children.Add(name);

            TextBlock percent = new TextBlock
            {
                Text = ((int)Math.Round(s.Volume * 100)).ToString() + "%",
                FontSize = 11,
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(percent, 1);
            head.Children.Add(percent);

            body.Children.Add(head);

            Slider slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = Math.Round(s.Volume * 100.0),
                Margin = new Thickness(0, 5, 0, 0),
                IsMoveToPointEnabled = true,
                // 细一点更像系统原生音量条，也更省高度
                Height = 18
            };
            // 默认 WPF 滑块是浅色系统样式，放在深色卡片上像贴了块白胶布。
            // 换成自定义模板：深色底轨 + 蓝色已填充部分 + 小圆点拇指。
            slider.Template = SliderTemplate();
            body.Children.Add(slider);

            Grid.SetColumn(body, 1);
            grid.Children.Add(body);

            // 右侧静音键
            Button mute = new Button
            {
                Content = s.Muted ? "🔇" : "🔊",
                Width = 30,
                Height = 26,
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)),
                Foreground = TextPrimary,
                Cursor = Cursors.Hand,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            mute.Template = PillTemplate();
            Grid.SetColumn(mute, 2);
            grid.Children.Add(mute);

            card.Child = grid;

            RowRefs refs = new RowRefs
            {
                Key = s.Key,
                Slider = slider,
                Percent = percent,
                MuteBtn = mute,
                Accent = accent
            };
            card.Resources["refs"] = refs;

            // ---- 交互 ----

            // 开始拖动：锁定该行，暂停外部刷新覆盖
            slider.PreviewMouseLeftButtonDown += (o, e) => { _draggingKey = refs.Key; };
            slider.PreviewMouseLeftButtonUp += (o, e) => { _draggingKey = null; };

            slider.ValueChanged += (o, e) =>
            {
                if (refs.Suppress) return;

                int pct = (int)Math.Round(e.NewValue);
                refs.Percent.Text = pct + "%";

                // 每次都按 Key 重新解析到当前这轮的会话对象
                AudioSession target = Resolve(refs.Key);
                if (target == null) return;

                float applied = _provider.SetVolume(target, (float)(e.NewValue / 100.0));
                if (applied < 0)
                {
                    // 失败就退回真实值，不要骗用户「已经调好了」
                    refs.Suppress = true;
                    slider.Value = Math.Round(target.Volume * 100.0);
                    refs.Suppress = false;
                }
            };

            mute.Click += (o, e) =>
            {
                AudioSession target = Resolve(refs.Key);
                if (target == null) return;
                bool? now = _provider.SetMute(target, !target.Muted);
                if (now.HasValue) ApplyRowValues(refs, target);
            };

            card.MouseEnter += (o, e) => card.Background = RowHover;
            card.MouseLeave += (o, e) => card.Background = RowBg;

            ApplyRowValues(refs, s);
            return card;
        }

        /// <summary>
        /// 深色音量滑块模板。
        ///
        /// 为什么这里用运行时 XAML 字符串，而不是 FrameworkElementFactory：
        /// Track 的 DecreaseRepeatButton / Thumb / IncreaseRepeatButton 是
        /// **普通 CLR 属性**（不是依赖属性），FrameworkElementFactory.SetValue
        /// 只接受 DependencyProperty，根本设不进去；
        /// 而 Track 又不是 IAddChild，也不能 AppendChild。
        /// XAML 解析器能直接给 CLR 属性赋值，所以这条路是唯一干净的。
        ///
        /// 注意：这不是编译期 XAML（本机没有 XAML 编译器），
        /// 是运行时 XamlReader.Parse，构建流程不受影响。
        /// </summary>
        internal static ControlTemplate SliderTemplate()
        {
            return SliderTemplateLazy.Value;
        }

        private static readonly Lazy<ControlTemplate> SliderTemplateLazy =
            new Lazy<ControlTemplate>(delegate
            {
                const string Xaml =
                    "<ControlTemplate " +
                    "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                    "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' " +
                    "TargetType='{x:Type Slider}'>" +
                    "  <Track Name='PART_Track'>" +
                    "    <Track.DecreaseRepeatButton>" +
                    "      <RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>" +
                    "        <RepeatButton.Template>" +
                    "          <ControlTemplate TargetType='{x:Type RepeatButton}'>" +
                    "            <Border Background='#FF4C8CF5' CornerRadius='2' Height='4'" +
                    "                    VerticalAlignment='Center'/>" +
                    "          </ControlTemplate>" +
                    "        </RepeatButton.Template>" +
                    "      </RepeatButton>" +
                    "    </Track.DecreaseRepeatButton>" +
                    "    <Track.Thumb>" +
                    "      <Thumb Width='12' Height='12'>" +
                    "        <Thumb.Template>" +
                    "          <ControlTemplate TargetType='{x:Type Thumb}'>" +
                    "            <Ellipse Fill='White' Width='12' Height='12'/>" +
                    "          </ControlTemplate>" +
                    "        </Thumb.Template>" +
                    "      </Thumb>" +
                    "    </Track.Thumb>" +
                    "    <Track.IncreaseRepeatButton>" +
                    "      <RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>" +
                    "        <RepeatButton.Template>" +
                    "          <ControlTemplate TargetType='{x:Type RepeatButton}'>" +
                    "            <Border Background='#2EFFFFFF' CornerRadius='2' Height='4'" +
                    "                    VerticalAlignment='Center'/>" +
                    "          </ControlTemplate>" +
                    "        </RepeatButton.Template>" +
                    "      </RepeatButton>" +
                    "    </Track.IncreaseRepeatButton>" +
                    "  </Track>" +
                    "</ControlTemplate>";

                return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(Xaml);
            });

        /// <summary>
        /// 诊断：把每一行的 Key、滑块值、以及「能不能按 Key 解析回会话」都吐出来。
        /// 用于定位「界面能拖、底层没变」这类只靠看截图判断不了的问题。
        /// </summary>
        public string Diagnose()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("VOLUME_PANEL_PROBE:");
            sb.AppendLine("  provider.Available=" + _provider.Available
                          + " err=" + (_provider.LastError ?? "(none)"));
            sb.AppendLine("  sessions=" + _provider.Sessions.Count);
            sb.AppendLine("  rows=" + _list.Children.Count);
            sb.AppendLine("  draggingKey=" + (_draggingKey ?? "(null)"));

            for (int i = 0; i < _list.Children.Count; i++)
            {
                Border row = _list.Children[i] as Border;
                if (row == null) continue;
                RowRefs refs = row.Resources["refs"] as RowRefs;
                if (refs == null) { sb.AppendLine("  [" + i + "] refs=null"); continue; }

                AudioSession cur = Resolve(refs.Key);
                sb.AppendLine(string.Format(
                    "  [{0}] key={1} slider={2} resolve={3} vol={4}",
                    i,
                    Shorten(refs.Key),
                    refs.Slider.Value,
                    cur == null ? "NULL" : cur.DisplayName,
                    cur == null ? "?" : ((int)Math.Round(cur.Volume * 100)) + "%"));
            }
            return sb.ToString();
        }

        private static string Shorten(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            int bar = s.LastIndexOf('|');
            string tail = bar >= 0 ? s.Substring(bar) : s;
            return tail.Length <= 40 ? tail : tail.Substring(0, 40);
        }

        /// <summary>与设置页一致的药丸模板（无边框、圆角、悬停微亮）。</summary>
        internal static ControlTemplate PillTemplate()
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
            hover.Setters.Add(new Setter(Border.OpacityProperty, 0.8, "Bd"));
            t.Triggers.Add(hover);

            return t;
        }
    }
}
