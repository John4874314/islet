using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Islet.App.Interop;
using Islet.Core;

namespace Islet.App.Views
{
    /// <summary>
    /// 胶囊窗口。纯代码构建，不用 XAML（本机无 XAML 编译器）。
    ///
    /// 视觉规范借鉴 Desktop Dock 的 Liquid Glass，按同一套层级复刻：
    ///   1. 材质层：135° 对角渐变模拟玻璃本体（WPF 无 backdrop-filter，
    ///      用深色基底 + 白色渐变叠加达到同样的通透观感，同时保证白字对比度）
    ///   2. 厚度层：2.5px 暗色外圈，模拟玻璃切边
    ///   3. 高光层：顶部径向弧光，对应 CSS 的 layer-spec
    ///   4. 内发光：1px 白色内描边，对应 inset 0 1px 0 rgba(255,255,255,.45)
    ///   5. 投影：DropShadowEffect，对应 0 14px 40px rgba(0,0,0,.32)
    ///
    /// 关键约束（PRD §5.4）：
    ///   WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED
    ///   显示走 SW_SHOWNOACTIVATE，绝不 SetForegroundWindow
    ///   WM_MOUSEACTIVATE 返回 MA_NOACTIVATE
    /// </summary>
    internal sealed class IslandWindow : Window
    {
        // 尺寸（@96 DPI）
        private const double DotSize = 26;          // 隐藏点的可点击直径
        private const double BarHeight = 38;
        private const double CardWidth = 400;       // 展开态玻璃宽度（**不含**中转区）
        private const double TopMargin = 8;

        // ---- 中转区（同一块玻璃内部右侧的方框）----
        //
        // 玻璃宽度 = 主内容宽度 + 方框宽度 + 右边距。
        // 没有条目时方框整列 Collapsed，这部分宽度为 0 —— 这就是
        // 「无文件时 UI 和之前一模一样」的实现方式，不靠任何开关。
        private const double StashBoxWidth = 176;
        private const double StashGapRight = 12;
        private const double StashBoxMinHeight = 92;   // 至少能放下标题 + 一行
        private const double StashListMaxHeight = 148; // 再多就滚动，不让玻璃无限变高

        /// <summary>
        /// 阴影留白：给 DropShadowEffect 留出绘制空间，否则会被窗口边缘裁掉。
        /// 左右各 18，顶部 6，底部 18（投影向下）。
        /// </summary>
        private const double PadSide = 18;
        private const double PadTop = 6;
        private const double PadBottom = 18;

        private readonly Grid _root;
        private readonly Border _ring;          // 玻璃厚度（暗色外圈）
        private readonly Border _shell;         // 玻璃本体
        private readonly Grid _shellInner;
        private readonly Border _spec;          // 顶部镜面弧光
        private readonly Border _innerRim;      // 1px 内发光
        private readonly Grid _content;
        private readonly Ellipse _dot;
        private readonly Ellipse _dotHalo;
        private readonly DropShadowEffect _shellShadow;
        private readonly Grid _bar;
        private readonly StackPanel _card;

        /// <summary>岛内音量面板（展开态专用，替代卡片显示各应用滑块）。</summary>
        private readonly StackPanel _mixer;
        private readonly StackPanel _mixerRows;
        private readonly TextBlock _mixerHint;
        private readonly Dictionary<string, MixerRow> _mixerRowsByKey =
            new Dictionary<string, MixerRow>();
        private Islet.Sources.AudioSessionProvider _audioProvider;
        private System.Windows.Threading.DispatcherTimer _mixerTimer;
        private string _mixerDragging;

        private readonly TextBlock _barTitle;
        private readonly TextBlock _barSubtitle;
        private readonly Grid _barProgressHost;
        private readonly Border _barProgressTrack;
        private readonly Border _barProgressFill;
        private readonly TextBlock _cardTitle;
        private readonly TextBlock _cardSubtitle;
        private readonly Grid _progressHost;
        private readonly Border _progressTrack;
        private readonly Border _progressFill;
        private readonly StackPanel _cardActions;
        private readonly StackPanel _barActions;

        private double _progressValue;
        private double _barProgressValue;

        private IslandEvent _current;
        private bool _expanded;

        // ---- 临时存放站 ----
        //
        // 形态：**不做独立窗口**。中转区是同一块玻璃内部右侧的一个方框：
        // 主内容（圆环 / 条 / 卡片 / 混音器）占左列，中转区占右列。
        // 没有文件时右列整列 Collapsed，玻璃宽度与之前完全一致；
        // 有文件时玻璃向右（必要时也向下）延伸，把方框一起包进同一块玻璃。
        // 这样音乐播放等形态与中转区天然共存，不存在「两个窗口」的割裂感。
        private Border _stashBadge;          // 圆环态的小角标（中转区可见时不显示）
        private TextBlock _stashBadgeText;
        private int _stashCount;

        private Grid _mainHost;              // 左列：原有的四种形态
        private Border _stashBox;            // 右列：中转区方框
        private StackPanel _stashRows;       // 方框里的文件行
        private TextBlock _stashCaption;     // 「中转 · N」
        private Services.StashStore _stashStore;

        /// <summary>当前形态。玻璃尺寸与中转区可见性都由它决定。</summary>
        private IslandForm _form;

        /// <summary>正在从中转区拖出文件。为真时禁止自动收起，否则行一消失拖影就断。</summary>
        private bool _stashDragging;
        private string _stashDragId;
        private Point _stashDragStart;

        /// <summary>玻璃形态。尺寸规则按它分支，见 ApplyMetrics。</summary>
        private enum IslandForm
        {
            Dot,        // 小圆环（收起且没有事件）
            Bar,        // 紧凑条
            Card,       // 展开卡片
            Mixer       // 展开后的应用音量
        }

        /// <summary>用户把文件拖到岛上时触发，参数为源文件路径数组。</summary>
        public event Action<string[]> FilesDropped;

        // ---- 调色板 ----
        //
        // 整体向 Desktop Dock 靠：更浅、更透、白雾感更重。
        // 之前用近乎不透明的深蓝黑（0xE0），看起来像「深色塑料块」而不是玻璃。
        // 现在基底改用**浅色半透明**（白雾），配深色文字 —— 这是 Dock 那套
        // 「白玻璃 + 深字」的真实做法，也是「再浅再透明」最直接的解法：
        // 继续降低深色底只会变成灰雾 + 白字更难读，必须连文字一起翻转。
        private static readonly Color TextPrimary = Color.FromRgb(0x11, 0x15, 0x1C);
        private static readonly Color TextSecondary = Color.FromArgb(0xC4, 0x1B, 0x1F, 0x28);
        private static readonly Color Accent = Color.FromRgb(0x2C, 0x5C, 0xB8);

        /// <summary>中转区文件行的常态 / 悬停底色（免去每次进出都新建画刷）。</summary>
        private static readonly Brush StashRowBg =
            new SolidColorBrush(Color.FromArgb(0x1C, 0x1B, 0x1F, 0x28));
        private static readonly Brush StashRowHover =
            new SolidColorBrush(Color.FromArgb(0x34, 0x1B, 0x1F, 0x28));

        /// <summary>当前透明度档位（影响基底 alpha 与高光强度）。</summary>
        private GlassOpacity _opacity = GlassOpacity.Deep;

        /// <summary>
        /// 三档基底的 alpha（白雾的不透明度）。数值越高越**实**：
        ///   Deep   0x70 → 白雾中等，深色文字最稳（比旧版 0x8E 又透了一档）
        ///   Medium 0x50 → 能明显看到背后的色块
        ///   Clear  0x2C → 几乎只是一层薄雾，背景内容清晰可辨
        /// 这一轮整体再降：用户要「再浅再透明一些」，靠的是把白雾调薄，
        /// 同时把文字加粗一级、颜色再压深一点来补对比度 —— 只降 alpha 会让字发灰。
        /// </summary>
        private static byte BaseAlpha(GlassOpacity o)
        {
            switch (o)
            {
                case GlassOpacity.Medium: return 0x50;
                case GlassOpacity.Clear: return 0x2C;
                default: return 0x70;
            }
        }

        /// <summary>越透的档位，顶部高光要略收，否则浅底上会过曝。</summary>
        private static byte SpecAlpha(GlassOpacity o)
        {
            switch (o)
            {
                case GlassOpacity.Medium: return 0x48;
                case GlassOpacity.Clear: return 0x3C;
                default: return 0x54;
            }
        }

        /// <summary>
        /// 显式中文字体链。WPF 默认 Segoe UI 对中文的按字形回退不稳定，
        /// 实测会导致部分按钮文字渲染成方框，因此在窗口级固定字体族。
        /// </summary>
        private static readonly FontFamily UiFont =
            new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, SimSun");

        public IslandWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            ShowActivated = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = UiFont;
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            // ================= 视觉树 =================

            // ---- 隐藏点：外圈光晕 + 实心点 ----
            // 浅玻璃上的小球用**深色**实心，才能和浅底拉开对比；
            // 旧版是白色球，在浅玻璃上几乎看不见。
            _dotHalo = new Ellipse
            {
                Width = DotSize - 2,
                Height = DotSize - 2,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Fill = new RadialGradientBrush
                {
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x38, 0x2C, 0x5C, 0xB8), 0.0),
                        new GradientStop(Color.FromArgb(0x00, 0x2C, 0x5C, 0xB8), 1.0)
                    }
                }
            };

            _dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Fill = new LinearGradientBrush
                {
                    StartPoint = new Point(0.3, 0),
                    EndPoint = new Point(0.7, 1),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0xFF, 0x5B, 0x8B, 0xE0), 0.0),
                        new GradientStop(Color.FromArgb(0xFF, 0x2C, 0x5C, 0xB8), 1.0)
                    }
                },
                Effect = new DropShadowEffect
                {
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.55,
                    Color = Color.FromRgb(0x2C, 0x5C, 0xB8)
                }
            };

            // ---- 紧凑条 ----
            _barTitle = new TextBlock
            {
                Foreground = new SolidColorBrush(TextPrimary),
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 180
            };

            _barSubtitle = new TextBlock
            {
                Foreground = new SolidColorBrush(TextSecondary),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 130
            };

            _barActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };

            // 底部进度线：3px（2px 在 96 DPI 下会被抗锯齿吃掉一半）
            _barProgressTrack = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = new SolidColorBrush(Color.FromArgb(0x2A, 0x1B, 0x1F, 0x28)),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _barProgressFill = new Border
            {
                Height = 3,
                CornerRadius = new CornerRadius(1.5),
                Background = ProgressBrush(),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _barProgressHost = new Grid
            {
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Bottom,
                Height = 3
            };
            _barProgressHost.Children.Add(_barProgressTrack);
            _barProgressHost.Children.Add(_barProgressFill);
            _barProgressHost.SizeChanged += (s, e) => ApplyBarProgressWidth();

            StackPanel barRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            barRow.Children.Add(_barTitle);
            barRow.Children.Add(_barSubtitle);
            barRow.Children.Add(_barActions);

            // Grid 会重叠子元素：进度线必须在最底层，内容要置顶且不能被进度线穿透
            Grid.SetZIndex(_barProgressHost, 0);
            Grid.SetZIndex(barRow, 1);

            _bar = new Grid
            {
                Margin = new Thickness(16, 0, 14, 0),
                Height = BarHeight,
                // 中转区可见时玻璃会变高（38 → 约 110），条必须继续贴顶，
                // 否则 Stretch 会把它垂直居中，看起来像浮在玻璃中间。
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };
            _bar.Children.Add(barRow);
            _bar.Children.Add(_barProgressHost);

            // ---- 展开卡 ----
            _cardTitle = new TextBlock
            {
                Foreground = new SolidColorBrush(TextPrimary),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            _cardSubtitle = new TextBlock
            {
                Foreground = new SolidColorBrush(TextSecondary),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            // 进度条：不用 ProgressBar（自定义模板需 PART_Track 才能算宽度，容易失效），
            // 改为一个 Grid 里放底轨 + 填充条，手动按比例设宽度。
            _progressTrack = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(Color.FromArgb(0x2A, 0x1B, 0x1F, 0x28)),
                Height = 4
            };
            _progressFill = new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = ProgressBrush(),
                Height = 4,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _progressHost = new Grid
            {
                Height = 4,
                Margin = new Thickness(0, 12, 0, 0),
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            _progressHost.Children.Add(_progressTrack);
            _progressHost.Children.Add(_progressFill);
            _progressHost.SizeChanged += (s, e) => ApplyProgressWidth();

            _cardActions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            StackPanel cardStack = new StackPanel();
            cardStack.Children.Add(_cardTitle);
            cardStack.Children.Add(_cardSubtitle);
            cardStack.Children.Add(_progressHost);
            cardStack.Children.Add(_cardActions);

            // 卡片内部可用宽度 = 玻璃宽 - 左右内边距。
            // 直接给 Border 设 Width 会被 SizeToContent 撑开（实测 400 → 436），
            // 所以在内容层夹死：文本按此宽度换行，按钮也不会把卡片顶宽。
            const double CardInner = CardWidth - 18 * 2;

            _card = new StackPanel
            {
                Margin = new Thickness(18, 16, 18, 16),
                Width = CardInner,
                // 同上：中转区更高时卡片贴顶，多余的空白留在下方。
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };
            _card.Children.Add(cardStack);

            // ---- 岛内音量面板 ----
            // 和卡片共用同一个玻璃壳，只是内容层不同。
            // 之所以不并入卡片：卡片是「事件详情」，这里是「控制面板」，
            // 两者同时存在的语义完全不同，混在一起会让点击意图变得含糊。
            _mixerRows = new StackPanel();
            _mixerHint = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(TextSecondary),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };

            _mixer = new StackPanel
            {
                Margin = new Thickness(18, 14, 18, 14),
                Width = CardInner,
                // 同上：中转区更高时贴顶。
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed
            };
            _mixer.Children.Add(new TextBlock
            {
                Text = "应用音量",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextPrimary)
            });
            _mixer.Children.Add(_mixerRows);
            _mixer.Children.Add(_mixerHint);

            // 球和条/卡必须分层，不能平铺在同一个 Grid 里：
            // 平铺时球是居中定位的，条展开后球虽然 Collapsed，
            // 但 Grid 的对齐约束会把球的位置算进布局，实测出现「球透在条中间」。
            Grid dotLayer = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            dotLayer.Children.Add(_dotHalo);
            dotLayer.Children.Add(_dot);

            // ---- 内容层：左列主内容 + 右列中转区 ----
            //
            // 为什么是两列而不是叠在一起：
            //   中转区必须和主内容**共用同一块玻璃**（用户明确要求：不要独立窗口，
            //   音乐播放等形态与中转区都在同一个壳里）。两列布局的天然好处是
            //   「没有文件时右列塌成 0」，玻璃宽度与加这个功能之前逐像素一致。
            _mainHost = new Grid();
            _mainHost.Children.Add(dotLayer);
            _mainHost.Children.Add(_bar);
            _mainHost.Children.Add(_card);
            _mainHost.Children.Add(_mixer);

            _content = new Grid();
            _content.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(_mainHost, 0);
            _content.Children.Add(_mainHost);

            _stashBox = BuildStashBox();
            Grid.SetColumn(_stashBox, 1);
            _content.Children.Add(_stashBox);

            // 让「按在玻璃上任何地方」都能拖动窗口。
            //
            // ⚠️ 这里必须是**窗口级** Preview，不能挂在 _content 上。
            //
            // 之前写的是 _content.PreviewMouseLeftButtonDown，实测「只有边缘能拖」。
            // 原因在事件路由：玻璃的 Background 画在 _shell（Border）上，空白处
            // 命中的就是 _shell 本身，路由是 _shell → shellHost → _root → Window。
            // _content 是 _shellInner 的子级、_shell 的**后代**，根本不在那条路由上，
            // 所以按在空白玻璃上时它一次都不会触发 —— 那个订阅形同虚设。
            // 只有按在圆角边缘（落在窗口透明底上）时才走另一条路，看起来就像
            // 「只有边缘能拖」。
            //
            // 窗口级 Preview 是整条路由的起点，无论命中的是 _shell、_bar 还是
            // TextBlock，都一定先经过这里，因此「任意空白区可拖」才成立。
            //
            // 代价：按钮/滑块也会先经过它，所以 OnDragStart 里必须放行可交互控件，
            // 否则按按钮会变成拖窗口（CaptureMouse 会把点击从按钮手里抢走）。
            PreviewMouseLeftButtonDown += OnDragStart;

            // ---- 顶部镜面弧光 ----
            _spec = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(20),
                Background = SpecBrush()
            };

            // ---- 1px 内发光 ----
            _innerRim = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new CornerRadius(19),
                Margin = new Thickness(1),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF))
            };

            _shellInner = new Grid();
            _shellInner.Children.Add(_spec);
            _shellInner.Children.Add(_content);
            _shellInner.Children.Add(_innerRim);

            // ---- 玻璃本体 ----
            _shell = new Border
            {
                CornerRadius = new CornerRadius(20),
                Background = GlassBrush(),
                BorderThickness = new Thickness(1),
                BorderBrush = RimBrush(),
                Child = _shellInner,
                SnapsToDevicePixels = true
            };

            // 投影：对应 CSS 的 0 14px 40px rgba(0,0,0,.32)
            _shellShadow = new DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.55,
                Color = Colors.Black
            };
            _shell.Effect = _shellShadow;

            // ---- 玻璃厚度：2.5px 外圈 ----
            // 浅色玻璃的外圈必须**极淡**。之前用 0x30 的灰蓝，在浅底上叠出来
            // 是一圈脏灰边，像没擦干净的玻璃。改成几乎透明的白，只留一丝边界感。
            _ring = new Border
            {
                CornerRadius = new CornerRadius(22.5),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0x5A, 0x66, 0x7A)),
                Margin = new Thickness(-2.5),
                IsHitTestVisible = false
            };

            Grid shellHost = new Grid();
            shellHost.Children.Add(_ring);
            shellHost.Children.Add(_shell);

            _root = new Grid { Margin = new Thickness(PadSide, PadTop, PadSide, PadBottom) };
            _root.Children.Add(shellHost);

            // ---- 暂存数量角标 ----
            // 放在 _root 里、右上角，不参与命中测试：不会挡住窗口拖动，
            // 也不会改变 SizeToContent 的测量结果（Collapsed 时完全不占位）。
            _stashBadgeText = new TextBlock
            {
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _stashBadge = new Border
            {
                Width = 15,
                Height = 15,
                CornerRadius = new CornerRadius(7.5),
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x53, 0x4A)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                // 贴在圆环的右上角切线上：再大就会盖住那个 26px 的球本身
                Margin = new Thickness(0, -1, -3, 0),
                IsHitTestVisible = false,
                Child = _stashBadgeText,
                Visibility = Visibility.Collapsed
            };
            Grid.SetZIndex(_stashBadge, 10);
            _root.Children.Add(_stashBadge);

            Content = _root;

            // ---- 拖入文件：整个窗口都是放置目标 ----
            // 只接收 FileDrop；其它格式（文本、图片）不处理，避免误收纳。
            AllowDrop = true;
            DragEnter += OnStashDragEnter;
            DragOver += OnStashDragOver;
            Drop += OnStashDrop;

            _shell.SizeChanged += (s, e) => UpdateSpecClip();

            // ---- 交互：悬停展开 ----
            //
            // 圆环态**不**悬停展开：它是常驻小挂件，用户经常只是划过、或想按住拖走，
            // 一碰就弹成大矩形既挡视线又拖不动。
            //
            // 条 / 卡片态也不再「一碰就展开」，改成**停在上面 500ms 才展开**。
            // 这是用户实测踩到的：岛贴在右边缘时，鼠标刚碰到条就弹成 400 宽的大框，
            // 而大框不能越过屏幕右边 —— 于是「想把它推到最右」永远做不到，
            // 手一碰它就先变大了。加延迟之后三条行为分开：
            //   · 路过（不到 500ms）→ 不展开；
            //   · 想拖它 → 按下鼠标立刻取消倒计时，半秒内按住就能拖；
            //   · 真的停在上面不动 → 半秒后展开。
            MouseEnter += (s, e) => StartHoverExpand();
            MouseLeave += (s, e) =>
            {
                CancelHoverExpand();
                // 正在从中转区往外拖文件：鼠标会离开岛（拖到资源管理器/聊天框），
                // 这时候绝不能收起 —— 行一消失，拖影和后续 Drop 都会乱掉。
                if (_stashDragging) return;
                ScheduleCollapse();
            };

            // ---- 拖动 ----
            // 不能用 DragMove()：它要求窗口被激活（会夺焦），而这个窗口是
            // WS_EX_NOACTIVATE + ShowActivated=false，调用 DragMove 会抛
            // InvalidOperationException。所以自己实现：记录按下点与窗口原位的差值，
            // 跟着光标走（MouseMove + 定时器兜底）。全程不激活窗口，符合 PRD §5.4。
            //
            // 按下入口**只有 Preview 一条**（在构造函数上方已挂）。
            // 不要再补一个冒泡阶段的 MouseLeftButtonDown += OnDragStart：
            // 那会在按到空白玻璃时触发两次（Preview 一次 + 冒泡一次），
            // 虽然有 _dragging 兜底，但两个订阅并存只会让「谁先谁后」变得难推理。
            //
            // ⚠️ MouseMove 这条路**不能删**。2026-11-05 曾为了省 CPU 删掉它、只留
            // 16ms 定时器，用户反馈「更掉帧了」。tools\Cadence 量出原因：
            // 这台机器上 DispatcherTimer(16ms, Render) 实际只有 **39.5 次/秒**
            //（≈25ms，还带抖动），而屏幕是 60Hz —— 位置更新比屏幕还慢，动作自然一跳一跳。
            // 结论：不是「搬得太勤」而是「每次太贵 + 没有上限」。
            // 现在的做法是保留 MouseMove 的密度，用 FollowCursor 里的
            // MoveGateMs 给搬运次数封顶，两头都占住。
            MouseMove += OnDragMove;
            MouseLeftButtonUp += OnDragEnd;

            // 丢捕获兜底：正常松手会先走 OnDragEnd（那里已经把 _dragging 置 false），
            // 所以这个处理器只在「异常中断」时生效 —— 比如拖动过程中窗口被隐藏、
            // 或者捕获被别的窗口抢走，此时永远收不到 MouseLeftButtonUp，
            // _dragging 会一直停在 true，之后每次按下都被它挡掉，岛就再也拖不动了。
            LostMouseCapture += (s, e) =>
            {
                if (!_dragging) return;
                _dragging = false;
                _dragMoved = false;
                _dragCacheValid = false;
                if (_dragTimer != null) _dragTimer.Stop();
            };
            // 拖动跟随另配一个定时器兜底：分层窗口在快速拖动时 MouseMove 会丢事件，
            // 表现为窗口跟不上鼠标、越拖越落后。
            //
            // 16ms ≈ 60fps。优先级必须给 Render：默认的 Background 会和布局、
            // 数据绑定抢时间片，拖动时表现为一顿一顿的。
            _dragTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _dragTimer.Tick += (s, e) => FollowCursor();

            // 注意：点击展开不在这里单独挂 MouseLeftButtonUp ——
            // 它和拖动共用同一个按键，两个处理器会互相打架（拖完还会顺带展开）。
            // 统一由 OnDragEnd 判断：位移小于阈值算点击 → 展开；否则算拖动 → 记住位置。

            SourceInitialized += OnSourceInitialized;
        }

        // ---------------------------------------------------------------
        // 画刷
        // ---------------------------------------------------------------

        /// <summary>
        /// 玻璃本体：135° 对角渐变。WPF 没有 backdrop-filter，
        /// 用「深色基底 + 白色渐变叠加」模拟同一观感，并保证白字对比度。
        /// 基底 alpha 由当前透明度档位决定。
        /// </summary>
        private Brush GlassBrush()
        {
            byte a = BaseAlpha(_opacity);
            LinearGradientBrush b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            // 白雾玻璃：三段都是浅色，靠 alpha 控制通透度。
            // 左上略亮、右下略暗 —— 模拟光线从左上穿过玻璃的厚度差。
            b.GradientStops.Add(new GradientStop(Color.FromArgb(a, 0xFC, 0xFD, 0xFF), 0.0));
            b.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(a * 0.92), 0xF4, 0xF7, 0xFC), 0.45));
            b.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)Math.Min(255, a * 1.06), 0xE8, 0xEE, 0xF8), 1.0));
            return b;
        }

        /// <summary>顶部镜面弧光，强度随透明度档位变化。</summary>
        private Brush SpecBrush()
        {
            byte a = SpecAlpha(_opacity);
            RadialGradientBrush b = new RadialGradientBrush
            {
                Center = new Point(0.5, -0.12),
                GradientOrigin = new Point(0.5, -0.12),
                RadiusX = 0.78,
                RadiusY = 0.72
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(a, 0xFF, 0xFF, 0xFF), 0.0));
            b.GradientStops.Add(new GradientStop(
                Color.FromArgb((byte)(a / 4), 0xFF, 0xFF, 0xFF), 0.45));
            b.GradientStops.Add(new GradientStop(
                Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.72));
            return b;
        }

        /// <summary>
        /// 应用透明度档位。设置页改动后由宿主调用。
        /// 只重建两个画刷，不动布局，因此可以随时切换、立即生效。
        /// </summary>
        public void SetGlassOpacity(GlassOpacity o)
        {
            _opacity = o;
            _shell.Background = GlassBrush();
            _spec.Background = SpecBrush();
        }

        /// <summary>
        /// 边框：浅色玻璃用「外圈略暗、上缘提亮」的细描边。
        /// 不能再用纯白 —— 浅玻璃 + 白边会糊在一起看不出边界。
        /// </summary>
        private static Brush RimBrush()
        {
            LinearGradientBrush b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xA8, 0xFF, 0xFF, 0xFF), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0x8A, 0x96, 0xAA), 0.5));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0x50, 0x6E, 0x7A, 0x90), 1.0));
            return b;
        }

        private static Brush ProgressBrush()
        {
            LinearGradientBrush b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            // 浅玻璃上进度条要够深才看得见，改用深蓝渐变
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x3A, 0x62, 0xB0), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xFF, 0x5B, 0x8B, 0xE0), 1.0));
            return b;
        }

        /// <summary>把镜面层裁成圆角，避免高光溢出到圆角外面。</summary>
        private void UpdateSpecClip()
        {
            double w = _shell.ActualWidth;
            double h = _shell.ActualHeight;
            if (w <= 0 || h <= 0)
            {
                _spec.Clip = null;
                return;
            }
            double r = _shell.CornerRadius.TopLeft;
            _spec.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        }

        /// <summary>展开状态变化（供宿主暂停抢占）。</summary>
        public event EventHandler ExpandedChanged;

        public bool IsExpanded
        {
            get { return _expanded; }
        }

        // ---------------------------------------------------------------
        // 临时存放站：拖入 + 玻璃内部右侧的中转区
        // ---------------------------------------------------------------

        private void OnStashDragEnter(object sender, System.Windows.DragEventArgs e)
        {
            SetStashEffect(e);

            // 拖进来的第一反应：把岛张开。
            // 圆环态只有 26px，接不住任何东西；用户把文件拖到那个小点上时
            // 「岛自己张开」才是他期待的响应。拖过去又不放下也没关系 ——
            // 鼠标离开后照常自动收起。
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
                && !_expanded && !_dragging)
            {
                try { SetExpanded(true); }
                catch (Exception ex) { Log.Info("拖入时展开失败: " + ex.Message); }
            }
        }

        private void OnStashDragOver(object sender, System.Windows.DragEventArgs e)
        {
            SetStashEffect(e);
        }

        private static void SetStashEffect(System.Windows.DragEventArgs e)
        {
            if (e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.Copy;
                e.Handled = true;
            }
        }

        private void OnStashDrop(object sender, System.Windows.DragEventArgs e)
        {
            string[] paths = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;

            Action<string[]> h = FilesDropped;
            if (h != null) h(paths);

            e.Handled = true;
        }

        /// <summary>宿主创建窗口后注入数据层。中转区的渲染与拖出都直接读它。</summary>
        public void AttachStashStore(Services.StashStore store)
        {
            _stashStore = store;
            RefreshStash(false);
        }

        /// <summary>
        /// 重建中转区内容并同步玻璃尺寸。宿主在条目增删后调用。
        /// </summary>
        /// <param name="reveal">
        /// 条目**增加**时是否自动把岛展开。
        /// 拖文件进来必须立刻看得见结果，所以走 true；宿主初始化时传 false，
        /// 否则一启动岛就自己弹开。
        /// </param>
        public void RefreshStash(bool reveal)
        {
            int count = _stashStore == null ? 0 : _stashStore.Count;
            bool grew = count > _stashCount;
            _stashCount = count;

            RebuildStashRows();
            ApplyMetrics();

            if (reveal && grew && !_expanded)
            {
                // 圆环态只有一个 26px 的球，中转区在里面根本放不下。
                // 拖进来的文件必须立刻看得见，所以这里主动展开一次
                // （展开后如果是音乐播放，会落在混音器形态，中转区照样在右侧）。
                SetExpanded(true);
                return;
            }

            // 玻璃尺寸变了就必须重算位置：展开态是按「圆环中心」锚定的，
            // 宽度变了不重算的话，右边新增的中转区会跑到屏幕外。
            if (IsVisible) Reposition();
        }

        private void RemoveStashItem(string id)
        {
            if (_stashStore == null) return;
            _stashStore.Remove(id);
            RefreshStash(false);
        }

        private void ClearStash()
        {
            if (_stashStore == null) return;
            _stashStore.Clear();
            RefreshStash(false);
        }

        // ---------------------------------------------------------------
        // 中转区的构建与渲染
        // ---------------------------------------------------------------

        /// <summary>
        /// 构建右侧中转区方框。
        ///
        /// 它是 _content 第二列的子元素 —— 也就是说，它**就在同一块玻璃里**。
        /// 「液态玻璃延伸出去」不是另开一块玻璃，而是这块玻璃因为多了一列而变宽。
        /// </summary>
        private Border BuildStashBox()
        {
            _stashCaption = new TextBlock
            {
                Text = "中转",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(TextSecondary),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            Button clear = new Button
            {
                Content = "清空",
                FontSize = 10,
                Foreground = new SolidColorBrush(TextSecondary),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(5, 1, 5, 1),
                Margin = new Thickness(4, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            clear.Template = VolumePanel.PillTemplate();
            clear.Click += (s, e) => ClearStash();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_stashCaption, 0);
            Grid.SetColumn(clear, 1);
            head.Children.Add(_stashCaption);
            head.Children.Add(clear);

            _stashRows = new StackPanel();

            // 列表用 ScrollViewer 兜住：文件多的时候只让这一小块滚动，
            // 不让玻璃一直变高（岛是贴边挂件，长太高会挡住内容）。
            ScrollViewer list = new ScrollViewer
            {
                Content = _stashRows,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = StashListMaxHeight,
                Margin = new Thickness(0, 4, 0, 0),
                Focusable = false
            };

            Grid body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1, GridUnitType.Star)
            });
            Grid.SetRow(head, 0);
            Grid.SetRow(list, 1);
            body.Children.Add(head);
            body.Children.Add(list);

            Border box = new Border
            {
                Width = StashBoxWidth,
                MinHeight = StashBoxMinHeight,
                Margin = new Thickness(0, 8, StashGapRight, 8),
                Padding = new Thickness(7, 6, 4, 6),
                CornerRadius = new CornerRadius(10),
                // 比玻璃本体重一档的淡色 + 1px 亮边：读起来像玻璃内壁的一个格子，
                // 而不是又一块贴在旁边的独立面板。
                Background = new SolidColorBrush(Color.FromArgb(0x1E, 0x1B, 0x1F, 0x28)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF)),
                Child = body,
                VerticalAlignment = VerticalAlignment.Stretch,
                // 没有文件时整列塌成 0 —— 这就是「无文件时 UI 和之前一样」。
                Visibility = Visibility.Collapsed
            };

            return box;
        }

        private void RebuildStashRows()
        {
            if (_stashRows == null) return;
            _stashRows.Children.Clear();

            IList<Services.StashItem> items = _stashStore == null
                ? (IList<Services.StashItem>)new List<Services.StashItem>()
                : _stashStore.Snapshot();

            _stashCaption.Text = items.Count > 0
                ? "中转 · " + items.Count
                : "中转";

            for (int i = 0; i < items.Count; i++)
                _stashRows.Children.Add(MakeStashRow(items[i]));
        }

        private Border MakeStashRow(Services.StashItem item)
        {
            Border row = new Border
            {
                Background = StashRowBg,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(6, 4, 3, 4),
                Cursor = System.Windows.Input.Cursors.Hand
            };

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 11,
                Foreground = new SolidColorBrush(TextPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = FormatSize(item.Size) + " · " + item.AddedAt.ToString("HH:mm"),
                FontSize = 9.5,
                Foreground = new SolidColorBrush(TextSecondary),
                Margin = new Thickness(0, 1, 0, 0)
            });
            Grid.SetColumn(info, 0);
            g.Children.Add(info);

            Button del = new Button
            {
                Content = "×",
                FontSize = 10,
                Width = 18,
                Height = 18,
                Foreground = new SolidColorBrush(TextSecondary),
                Background = new SolidColorBrush(Color.FromArgb(0x1E, 0x1B, 0x1F, 0x28)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false,
                ToolTip = "移除（只删暂存副本，不动原文件）"
            };
            del.Template = VolumePanel.PillTemplate();
            del.Click += (s, e) => RemoveStashItem(item.Id);
            Grid.SetColumn(del, 1);
            g.Children.Add(del);

            row.Child = g;

            row.MouseEnter += (s, e) => { row.Background = StashRowHover; };
            row.MouseLeave += (s, e) => { row.Background = StashRowBg; };

            // ---- 拖出：标准 FileDrop ----
            //
            // 用「按下记录候选 + 移动超阈值才启动」而不是在 MouseDown 里直接
            // DoDragDrop：这样点 × 不会被误当成拖出，也符合资源管理器的手感。
            row.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (IsDescendantOf(e.OriginalSource as DependencyObject, del)) return;
                _stashDragId = item.Id;
                _stashDragStart = e.GetPosition(row);
            };
            row.PreviewMouseMove += (s, e) =>
            {
                if (_stashDragId != item.Id) return;
                if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;

                Point now = e.GetPosition(row);
                if (Math.Abs(now.X - _stashDragStart.X) < 4
                    && Math.Abs(now.Y - _stashDragStart.Y) < 4) return;

                _stashDragId = null;
                StartStashDragOut(item);
            };
            row.PreviewMouseLeftButtonUp += (s, e) => { _stashDragId = null; };

            return row;
        }

        /// <summary>
        /// 把一条暂存文件拖出去。
        ///
        /// FileDrop 是 Windows 通用格式：资源管理器、微信 / QQ 聊天框、浏览器上传框、
        /// agent 输入框都直接认它，不需要逐个适配。
        /// 效果位给 Copy | Move，**由接收方决定**：资源管理器默认 Copy（按 Shift 是 Move），
        /// 聊天框一般只收 Copy。只有真的返回 Move 才把条目从列表移除 ——
        /// 而且移走的始终是暂存副本，用户的原文件从头到尾没动过。
        /// </summary>
        private void StartStashDragOut(Services.StashItem item)
        {
            if (_stashStore == null) return;
            if (!System.IO.File.Exists(item.Path)) return;

            // 拖出期间禁止自动收起：鼠标一离开岛，MouseLeave 会排一个 400ms 的
            // 收起任务，收到一半行就没了，拖影和后续 Drop 都会变得莫名其妙。
            _stashDragging = true;

            DataObject data = new DataObject();
            data.SetFileDropList(
                new System.Collections.Specialized.StringCollection { item.Path });

            try
            {
                DragDropEffects result = DragDrop.DoDragDrop(
                    this, data, DragDropEffects.Copy | DragDropEffects.Move);

                bool moved = (result & DragDropEffects.Move) != 0;
                _stashStore.OnDragCompleted(item.Id, moved);
            }
            catch (Exception ex)
            {
                Log.Info("中转区拖出失败: " + ex.Message);
            }
            finally
            {
                _stashDragging = false;
            }

            // 到这里拖放已经结束，清掉旧行再重建是安全的
            // （当前这个 PreviewMouseMove 的处理器马上就要返回了）。
            RefreshStash(false);
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }

        private static bool IsDescendantOf(DependencyObject node, DependencyObject ancestor)
        {
            while (node != null)
            {
                if (ReferenceEquals(node, ancestor)) return true;
                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        // ---------------------------------------------------------------
        // 玻璃尺寸：唯一入口
        // ---------------------------------------------------------------

        /// <summary>
        /// 统一决定玻璃尺寸。**全类只有这里设置 _shell 的 Width/Height/Min/MaxWidth。**
        ///
        /// 为什么集中：中转区可见时玻璃要变宽（必要时也变高），而每个形态的
        /// 约束方式并不相同 —— 圆环是固定 Width，条是自动宽度 + 固定高，
        /// 卡片/混音器是夹死在 [Min, Max] 区间。分散在四个 Show* 里改，
        /// 每加一条规则就得改四处，漏一处就是「内容被裁掉」。
        /// </summary>
        private void ApplyMetrics()
        {
            // 先定中转区可见性，后面的宽度计算才有正确的前提
            ApplyStashVisibility();

            switch (_form)
            {
                case IslandForm.Dot:
                    _shell.Width = DotSize;
                    _shell.Height = DotSize;
                    _shell.MinWidth = 0;
                    _shell.MaxWidth = double.PositiveInfinity;
                    break;

                case IslandForm.Bar:
                    _shell.Width = double.NaN;
                    // 中转区可见时必须放开高度：条本身只有 38，装不下右侧方框。
                    // 放开后高度由内容自然决定（38 与 92+上下 8 取大者）。
                    _shell.Height = StashVisible() ? double.NaN : BarHeight;
                    _shell.MinWidth = 0;
                    _shell.MaxWidth = double.PositiveInfinity;
                    break;

                default:
                    // 卡片与混音器：宽度夹死在 [w, w]，中转区可见时 w 加上它那一列。
                    // 不能直接给 Width —— SizeToContent 只把 Width 当建议值
                    // （实测设 400 却量到 436，右上角因此溢出屏幕）。
                    double w = CardWidth + StashExtra();
                    _shell.Width = double.NaN;
                    _shell.Height = double.NaN;
                    _shell.MinWidth = w;
                    _shell.MaxWidth = w;
                    break;
            }
        }

        /// <summary>中转区占用的额外玻璃宽度（不可见时为 0）。</summary>
        private double StashExtra()
        {
            return StashVisible() ? StashBoxWidth + StashGapRight : 0;
        }

        private bool StashVisible()
        {
            return _stashBox != null && _stashBox.Visibility == Visibility.Visible;
        }

        /// <summary>
        /// 中转区只在「有条目」且「不是小圆环态」时出现。
        ///
        /// 圆环态为什么不出：它只有 26px，硬塞一个 176 宽的方框会把
        /// 常驻小挂件变成一根长条，违背它「安静待在角落」的定位。
        /// 那个形态改用右上角数字角标提示；点一下（或把文件拖进来，会自动展开）
        /// 就能看到完整的列表。展开态、条态、混音器态则一律显示在右侧。
        /// </summary>
        private void ApplyStashVisibility()
        {
            if (_stashBox == null) return;

            bool show = _stashCount > 0 && _form != IslandForm.Dot;
            Visibility v = show ? Visibility.Visible : Visibility.Collapsed;
            if (_stashBox.Visibility != v) _stashBox.Visibility = v;

            UpdateStashBadge();
        }

        /// <summary>角标只在「有文件但中转区看不见」时出现，避免和方框重复提示。</summary>
        private void UpdateStashBadge()
        {
            if (_stashBadge == null) return;

            bool show = _stashCount > 0 && !StashVisible();
            if (!show)
            {
                _stashBadge.Visibility = Visibility.Collapsed;
                return;
            }

            _stashBadgeText.Text = _stashCount > 99 ? "99+" : _stashCount.ToString();
            _stashBadge.Visibility = Visibility.Visible;
        }

        // ---------------------------------------------------------------
        // 窗口初始化：无焦点、圆角、定位
        // ---------------------------------------------------------------

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;

            int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            ex |= NativeMethods.WS_EX_NOACTIVATE
                | NativeMethods.WS_EX_TOOLWINDOW
                | NativeMethods.WS_EX_LAYERED;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);

            // ⚠️ 这里**绝对不能**设置 DWMWA_SYSTEMBACKDROP_TYPE。
            // 分层窗口（AllowsTransparency=true）一旦设了 Mica/亚克力，
            // DWM 会把整个窗口矩形涂成云母底色 —— 实测表现为胶囊背后
            // 多出一块灰白色方块，把圆角和透明边缘全毁了。
            // 玻璃质感完全由 WPF 自己画的几层负责，不依赖 DWM 背景。

            HwndSource src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(WndProc);

            Reposition();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }

        /// <summary>停靠位置。设置页改动后由宿主写入并调用 Reposition。</summary>
        public Core.IslandPosition Position { get; set; }

        /// <summary>
        /// 自由拖拽锚点：**小圆环的中心**（WPF 逻辑像素，屏幕坐标）。
        /// 仅在 Position == Free 时生效；NaN 表示还没拖过，回落到右上角默认位。
        ///
        /// 为什么存「圆环中心」而不是「窗口左上角」：
        /// 窗口尺寸随形态变化（圆环 62 → 条 ~430 → 卡片 654），如果锚在左上角，
        /// 那么每次展开都会向右长，撞到屏幕边被夹紧后整体被推左，
        /// 收起时圆环就跑到矩形左侧去了 —— 用户会觉得「球自己跑了」。
        /// 锚在圆环中心则圆环永远不动，展开只改变它周围矩形的生长方向。
        /// </summary>
        public double FreeX = double.NaN;
        public double FreeY = double.NaN;

        /// <summary>拖动结束时触发，参数为新的自由坐标。宿主据此落盘。</summary>
        public event EventHandler PositionChanged;

        /// <summary>把窗口摆到光标所在显示器的指定位置。</summary>
        public void Reposition()
        {
            NativeMethods.POINT pt;
            if (!NativeMethods.GetCursorPos(out pt)) return;

            PresentationSource src = PresentationSource.FromVisual(this);
            double scale = 1.0;
            if (src != null && src.CompositionTarget != null)
                scale = src.CompositionTarget.TransformToDevice.M11;

            double screenW = SystemParameters.PrimaryScreenWidth;

            // 用光标判定目标屏：以 WPF 的虚拟屏幕坐标近似
            double vLeft = SystemParameters.VirtualScreenLeft;
            double vTop = SystemParameters.VirtualScreenTop;

            double cursorX = pt.X / scale;
            double cursorY = pt.Y / scale;

            // 找到包含光标的屏幕矩形
            double targetLeft = vLeft;
            double targetWidth = screenW;
            foreach (System.Windows.Forms.Screen sc in System.Windows.Forms.Screen.AllScreens)
            {
                double l = sc.Bounds.Left / scale;
                double t = sc.Bounds.Top / scale;
                double w = sc.Bounds.Width / scale;
                double h = sc.Bounds.Height / scale;
                if (cursorX >= l && cursorX < l + w && cursorY >= t && cursorY < t + h)
                {
                    targetLeft = l;
                    targetWidth = w;
                    break;
                }
            }

            UpdateLayout();
            double w2 = ActualWidth > 0 ? ActualWidth : 160;
            double h2 = ActualHeight > 0 ? ActualHeight : 60;

            // 自由拖拽：锚点是**小圆环中心**，不是窗口左上角。
            if (Position == Core.IslandPosition.Free
                && !double.IsNaN(FreeX) && !double.IsNaN(FreeY))
            {
                PlaceAroundAnchor(FreeX, FreeY, w2, h2);
                return;
            }

            const double SideMargin = 16;

            // 窗口本身含左右各 PadSide 的透明留白（给投影用），
            // 所以「玻璃右边缘距屏幕 16px」= 窗口右边缘距屏幕 16 - PadSide。
            // 之前把符号写反了，导致玻璃实际贴死在屏幕边缘（右余量=0）。
            double left;
            switch (Position)
            {
                case Core.IslandPosition.TopLeft:
                    left = targetLeft + SideMargin - PadSide;
                    break;

                // ⚠️ 这里必须显式列出 TopCenter 与 TopRight。
                // 上一版把 TopCenter 漏掉了，它落到 default（右对齐），
                // 于是设置里选「顶部居中」却跑到右上角 —— 用户直接踩到。
                case Core.IslandPosition.TopCenter:
                    left = targetLeft + (targetWidth - w2) / 2.0;
                    break;

                case Core.IslandPosition.TopRight:
                    left = targetLeft + targetWidth - SideMargin - w2 + PadSide;
                    break;

                // Free 但还没有保存过坐标（首次启动）：默认落右上角
                default:
                    left = targetLeft + targetWidth - SideMargin - w2 + PadSide;
                    break;
            }

            // 兜底：不允许窗口越出该屏幕。
            double minLeft = targetLeft;
            double maxLeft = targetLeft + targetWidth - w2;
            if (maxLeft < minLeft) maxLeft = minLeft;
            if (left < minLeft) left = minLeft;
            if (left > maxLeft) left = maxLeft;
            Left = left;

            Top = vTop + TopMargin - PadTop;
        }

        /// <summary>
        /// 把窗口摆在「以小圆环中心为锚点」的位置。
        ///
        /// 规则（这是本次修复的核心）：
        ///   1. 圆环中心尽量固定在 (anchorX, anchorY)；
        ///   2. 展开时优先向右侧生长（视觉上从球往右拉出面板）；
        ///   3. 右边放不下时**不整体左移**，而是让窗口贴住右边缘，
        ///      圆环随之移动到窗口内的正确位置 —— 这是 2026-11-01 修掉的问题；
        ///   4. 垂直方向同理，默认向下生长；
        ///   5. 只有「窗口比可用空间还宽」时才做最小必要夹紧。
        ///
        /// 尺寸 w2/h2 是**整个窗口**（含投影留白），锚点指的是玻璃圆环中心，
        /// 所以窗口左上角 = 锚点 - 半个圆环 - 留白。
        ///
        /// ------------------------------------------------------------------
        /// 为什么之前会「瞬移」
        ///
        /// 旧代码在右边放不下时走「向左生长」：让**窗口右边缘**对齐锚点。
        /// 但圆环恒在窗口**左端**，于是圆环被推到 anchorX - w2 + PadSide + 13，
        /// 实测在 2560 宽的屏上整整左跳 368px —— 用户看到的就是整个岛突然飞走。
        ///
        /// 那个分支的意图是「保住圆环位置」，结果反而把它推得最远。
        /// 现在改成：贴住右边缘，圆环跟着走。圆环移动量等于窗口被夹紧的量，
        /// 最多是 (w2 - dotW) 的一部分，视觉上是「岛从圆点向右长出去」，
        /// 而不是「整个岛跳一下」。
        /// </summary>
        private void PlaceAroundAnchor(double anchorX, double anchorY, double w2, double h2)
        {
            PlaceAroundAnchor(anchorX, anchorY, w2, h2, true);
        }

        /// <param name="animate">
        /// 是否用过渡动画落到目标位。拖动时必须传 <c>false</c> —— 见 AnimateTo 的注释。
        /// </param>
        private void PlaceAroundAnchor(double anchorX, double anchorY, double w2, double h2,
                                       bool animate)
        {
            // 屏幕边界：拖动期间直接用 OnDragStart 缓存的那份。
            // SystemParameters.VirtualScreen* 是系统级查询，拖动时每 16ms 查四次纯属浪费，
            // 而这些值在一次拖动里根本不会变。
            double vLeft, vTop, vRight, vBottom;
            if (_dragCacheValid)
            {
                vLeft = _dragVLeft;
                vTop = _dragVTop;
                vRight = _dragVRight;
                vBottom = _dragVBottom;
            }
            else
            {
                vLeft = SystemParameters.VirtualScreenLeft;
                vTop = SystemParameters.VirtualScreenTop;
                vRight = vLeft + SystemParameters.VirtualScreenWidth;
                vBottom = vTop + SystemParameters.VirtualScreenHeight;
            }

            // 玻璃圆环本身的尺寸（窗口里除留白外的部分）
            double glassW = DotSize;
            double glassH = DotSize;

            // 圆环贴在窗口左边时的窗口左边缘
            double winLeftIfRight = anchorX - PadSide - glassW / 2.0;

            // 首选：向右生长，且不越出右边界
            double winLeft;
            if (winLeftIfRight + w2 <= vRight - 2)
            {
                winLeft = winLeftIfRight;
            }
            else
            {
                // 右边放不下 → 贴住右边缘。**不再整体左移到锚点右侧**，
                // 那会让圆环被推到窗口最左端，产生大跨度瞬移。
                winLeft = vRight - w2;
            }

            // 左侧也不能越界（窗口比屏幕还宽时只能贴左）
            if (winLeft < vLeft) winLeft = vLeft;

            // 垂直：默认圆环贴窗口顶部（岛习惯贴顶），下方放不下再向上生长
            double winTopIfDown = anchorY - PadTop - glassH / 2.0;
            double winBottomEdge = winTopIfDown + h2;

            double winTop;
            if (winBottomEdge <= vBottom - 2)
            {
                winTop = winTopIfDown;
            }
            else
            {
                // 同样不做整体跳：贴住底边
                winTop = vBottom - h2;
                if (winTop < vTop) winTop = vTop;
            }

            AnimateTo(winLeft, winTop, animate);
        }

        /// <summary>
        /// 把窗口移到目标位置，短距离瞬移、长距离做过渡。
        ///
        /// 为什么需要它：岛贴在右边缘时，条要贴住右边界就只能向左长，
        /// 于是玻璃左边缘会在展开瞬间左移最多 (条宽 - 圆点宽) ≈ 359px。
        /// 这是几何必然（不左移就会超出屏幕），但直接跳过去很难看 ——
        /// 用户看到的是「整个岛突然飞走」。
        ///
        /// 动画的是 Left/Top（窗口位置），**不是宽高**。
        /// 上一版试过动画宽高，结果 SizeToContent 会忽略它、并让测量失效，
        /// 窗口宽度一度算成 1914px。位置没有这个问题：它和布局系统完全解耦。
        /// </summary>
        private void AnimateTo(double targetLeft, double targetTop)
        {
            AnimateTo(targetLeft, targetTop, true);
        }

        /// <param name="animate">
        /// 是否做过渡动画。**拖动时必须 false。**
        ///
        /// 为什么：FollowCursor 每 16ms 调一次本方法，而动画时长是 140~260ms。
        /// 每帧都重启一个 EaseOut 动画，等于每帧都把「已经走了多远」清零，
        /// 窗口永远只走到目标的一小段就又被新目标重置 —— 实测表现为
        /// 「拖得越快落得越远，最后像拖着一根橡皮筋」，这就是顿挫感的真正来源，
        /// 和渲染、阴影、分层窗口都无关。
        ///
        /// 拖动要的是**严格跟手**：光标在哪，窗口就立刻在哪。
        /// 过渡动画只用于「形态切换导致的被动位移」（展开/收起贴边），
        /// 那种场景用户没有在拖动，看到平滑过渡是加分项。
        /// </param>
        private void AnimateTo(double targetLeft, double targetTop, bool animate)
        {
            // 当前 Left/Top 必须是**已固化的值**才能当动画起点。
            //
            // ⚠️ 这里不能直接读 Left/Top 判断 NaN 了事。
            // 只要上一次动画还挂着（BeginAnimation 未摘除），读到的就是
            // 动画当前值，而且 Window.Left 在窗口首次显示前的默认值是 NaN，
            // DoubleAnimation 拿不到 From 就会去问属性的默认 origin，
            // 遇到 NaN 直接抛 AnimationException —— 这就是日志里每次启动
            // 都出现两条「无法使用 DoubleAnimation 动态 Left/Top 属性」的原因。
            double curLeft = double.IsNaN(Left) ? targetLeft : Left;
            double curTop = double.IsNaN(Top) ? targetTop : Top;

            double dx = targetLeft - curLeft;
            double dy = targetTop - curTop;

            // 位移很小（例如收起回原位）就直接设，没必要为几个像素做动画
            if (!animate || (Math.Abs(dx) < 1.0 && Math.Abs(dy) < 1.0))
            {
                // 直接落位前必须先摘掉可能还在跑的动画，
                // 否则动画的最终值会在下一帧把 Left/Top 又覆盖回去。
                BeginAnimation(LeftProperty, null);
                BeginAnimation(TopProperty, null);
                Left = targetLeft;
                Top = targetTop;
                return;
            }

            // 距离越长给的时间稍长，但封顶，避免拖尾。
            double dist = Math.Sqrt(dx * dx + dy * dy);
            int ms = (int)Math.Min(260, 140 + dist * 0.22);

            Duration dur = new Duration(TimeSpan.FromMilliseconds(ms));
            IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            // **必须显式给 From**。只给 To 时 WPF 会去取属性的默认 origin，
            // 而 Window.Left/Top 在首次显示前是 NaN，于是抛 AnimationException。
            // 显式 From 同时还有一个好处：动画起点是此刻的真实位置，
            // 不会因为上一段动画没播完而跳一下。
            DoubleAnimation ax = new DoubleAnimation(curLeft, targetLeft, dur)
            {
                EasingFunction = ease
            };
            DoubleAnimation ay = new DoubleAnimation(curTop, targetTop, dur)
            {
                EasingFunction = ease
            };

            // 动画结束后把值固化，避免长期挂着动画影响后续读取
            ax.Completed += delegate
            {
                BeginAnimation(LeftProperty, null);
                Left = targetLeft;
            };
            ay.Completed += delegate
            {
                BeginAnimation(TopProperty, null);
                Top = targetTop;
            };

            BeginAnimation(LeftProperty, ax);
            BeginAnimation(TopProperty, ay);
        }

        /// <summary>
        /// 当前窗口状态下，小圆环中心在屏幕上的位置（逻辑像素）。
        /// 拖动落盘、状态切换保位都用它，保证「球不动」是唯一真相。
        /// </summary>
        private Point CurrentAnchor()
        {
            // 圆环恒在玻璃内左上角区域（点态时玻璃就是圆环本身）。
            // 展开态下圆环不可见，但它的逻辑位置仍是玻璃左上角 + 半个圆环。
            return new Point(Left + PadSide + DotSize / 2.0,
                             Top + PadTop + DotSize / 2.0);
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (hi < lo) return lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        // ---------------------------------------------------------------
        // 拖动
        // ---------------------------------------------------------------

        private bool _dragging;
        private NativeMethods.POINT _dragOrigin;   // 按下时的光标屏幕坐标
        private double _dragWinLeft;               // 按下时的窗口 Left
        private double _dragWinTop;                // 按下时的窗口 Top
        private double _dragAnchorX;               // 按下时「小圆环中心」的 X（逻辑像素）
        private double _dragAnchorY;               // 按下时「小圆环中心」的 Y
        private bool _dragMoved;                   // 是否真的移动过（用于区分点击）
        private System.Windows.Threading.DispatcherTimer _dragTimer;

        /// <summary>
        /// 两次「真正搬窗」之间的最小间隔（毫秒）。
        ///
        /// 上限 = 1000/MoveGateMs 次/秒。8ms → 125 次/秒，比 60Hz 屏幕快一倍：
        /// 每帧都能拿到最新位置，又不会像「每个 MouseMove 都搬」那样被
        /// 1000Hz 鼠标把 UI 线程吃光（实测一次搬窗 0.9ms）。
        ///
        /// 想更省就给 12~16，想更顺就给 4~6；给 0 等于关掉节流（回到最费的那版）。
        /// </summary>
        private const int MoveGateMs = 8;

        /// <summary>上一次真正搬窗的 Stopwatch 时间戳（0 = 本次拖动还没搬过）。</summary>
        private long _lastMoveStamp;

        // ---- 拖动遥测：松手时往 islet.log 写一行统计 ----
        //
        // 为什么要有：拖动手感没法靠猜。这些数能直接回答「延迟高还是帧率低」：
        //   calls  每次 FollowCursor 进入次数（MouseMove + 定时器的总和）
        //   moves  真正搬窗的次数
        //   gated  被 MoveGateMs 节流掉的次数
        //   cost   单次搬窗耗时（平均 / 最大），这就是吃掉帧预算的那部分
        //   gap    相邻两次搬窗的间隔（平均 / 最大），≈ 位置更新周期
        private int _statsCalls, _statsMoves, _statsGated;
        private double _statsCostSum, _statsCostMax;
        private double _statsGapSum, _statsGapMax;

        // ---- 拖动期间缓存的屏幕/窗口参数 ----
        //
        // 为什么必须缓存：FollowCursor 每 16ms 跑一次，而
        //   · SystemParameters.VirtualScreen* 是系统级查询（每次都要跨进程问窗口管理器）
        //   · ActualWidth/Height 在布局脏的时候会触发一次布局
        // 这些值在拖动过程中根本不会变，每帧重算纯属浪费 —— 这是拖顿的主要来源。
        private double _dragVLeft, _dragVTop, _dragVRight, _dragVBottom;
        private double _dragWinW, _dragWinH;
        private double _dragScale = 1.0;           // DPI 缩放比，拖动期间不变
        private int _lastCursorX, _lastCursorY;    // 上一帧光标物理坐标，用于跳过空转
        private bool _dragCacheValid;

        /// <summary>
        /// 拖动时的锚点：**小圆环的中心**。
        ///
        /// 为什么不再用「玻璃左上角」：
        /// 窗口宽度随形态变化（圆环 62 → 条 ~430 → 卡片 654）。
        /// 按左上角跟手时，用户把球拖到靠右位置、展开一次、再收起，
        /// 因为展开时窗口被屏幕右边缘夹紧、整体被推左，球就永久停在矩形左侧了
        /// —— 这正是用户反馈的「球自己跑掉、不能自由移动」。
        /// 锚定圆环中心后，形态怎么变，球都待在原处。
        /// </summary>
        /// 这是修「球总在长条左侧、拖不到屏幕右边」的关键。
        /// 窗口宽度跟着内容走（卡片 400 / 条 300+），而球只占最左 26px。
        /// 如果按「窗口左边缘跟随鼠标」来拖，鼠标其实一直停在球的左边，
        /// 于是球永远贴着窗口左边、窗口右边那一大片空白把屏幕右侧吃掉了。
        /// 正确做法：记住鼠标抓在**玻璃内部**的哪个相对位置，拖动时让玻璃跟手。
        /// </summary>
        private void OnDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // 按下 = 用户在操作它，悬停展开的倒计时立刻作废。
            // 这是「半秒内长按可以拖动」的关键：鼠标一按住，岛就不会在下一拍
            // 突然变形成大框，拖动全程保持当前形态（条还是条），才能贴到屏幕最右。
            CancelHoverExpand();

            // 中转区自己处理鼠标：按在方框上是要把文件拖出去，不是拖岛。
            // 不排除的话 CaptureMouse 会把行上的鼠标事件抢走，拖出彻底失效，
            // 而且用户想拖文件时岛会先跟着跑一下，非常难受。
            if (_stashBox != null
                && IsDescendantOf(e.OriginalSource as DependencyObject, _stashBox)) return;

            // 拖动入口挂在窗口级 Preview 上（见构造函数里的说明），
            // 它比所有子控件都早，因此必须在这里放行「本来该由控件自己处理」的点击。
            //
            // 判据用 **IsInteractiveUnder**：只要按下点下面（含自身）存在
            // Button / Slider / TextBox 之类，就整条链让路。
            // 之前只排除了混音器，结果卡片上的「暂停 / 上一首 / 下一首」按钮
            // 一按就被 CaptureMouse 抢走、点不动 —— 这是把入口提到窗口级后
            // 必然要补的一步。
            if (IsInteractiveUnder(e.OriginalSource as DependencyObject)) return;

            // 同一个按下会先走 Preview（窗口级）再冒泡到窗口的 MouseLeftButtonDown，
            // 没有这个判断的话 OnDragStart 会执行两次，CaptureMouse 也会调两次。
            if (_dragging) return;

            _dragging = true;
            _dragMoved = false;

            // 屏幕边界与窗口尺寸在本次拖动期间不会变，量一次就够
            FillDragCache();

            NativeMethods.POINT pt;
            if (NativeMethods.GetCursorPos(out pt)) _dragOrigin = pt;
            _dragWinLeft = Left;
            _dragWinTop = Top;

            // 拖动以**小圆环中心**为锚点。
            // 之前是按「玻璃左上角」跟手的，而玻璃宽度会随形态变化，
            // 于是「拖到某处 → 展开 → 收起」之后球会漂到矩形左侧。
            // 锚定圆环中心后，无论中间展开成多大，球都会回到原处。
            Point anchor = CurrentAnchor();
            _dragAnchorX = anchor.X;
            _dragAnchorY = anchor.Y;

            CaptureMouse();
            if (_dragTimer != null) _dragTimer.Start();
        }

        /// <summary>
        /// 判断按下点所在的元素链上是否有「自己处理鼠标」的控件。
        ///
        /// 拖动入口是窗口级 Preview，会先于一切子控件触发；
        /// 若不主动放行，按钮点不动、滑块拖不动（CaptureMouse 会把输入抢走）。
        /// 混音器滑块和卡片上的播放按钮都靠这个判断保命。
        /// </summary>
        private static bool IsInteractiveUnder(DependencyObject node)
        {
            while (node != null)
            {
                if (node is System.Windows.Controls.Primitives.ButtonBase) return true;
                if (node is System.Windows.Controls.Primitives.RangeBase) return true;   // Slider / ScrollBar
                if (node is System.Windows.Controls.Primitives.Thumb) return true;
                if (node is System.Windows.Controls.Primitives.TextBoxBase) return true;
                if (node is System.Windows.Controls.Primitives.Selector) return true;    // 下拉框
                if (node is System.Windows.Controls.PasswordBox) return true;

                node = VisualTreeHelper.GetParent(node);
            }
            return false;
        }

        /// <summary>
        /// 让「玻璃左上角」跟着光标走，而不是「窗口左上角」。
        ///
        /// 由 MouseMove 和 16ms 定时器**两条**路调用（缺一不可，见构造函数里的说明）。
        /// </summary>
        /// <summary>
        /// 填充拖动期间要反复用到的屏幕/窗口参数。
        /// 只在 OnDragStart 调一次 —— 这些值在拖动过程中不变。
        /// </summary>
        private void FillDragCache()
        {
            _dragVLeft = SystemParameters.VirtualScreenLeft;
            _dragVTop = SystemParameters.VirtualScreenTop;
            _dragVRight = _dragVLeft + SystemParameters.VirtualScreenWidth;
            _dragVBottom = _dragVTop + SystemParameters.VirtualScreenHeight;

            _dragWinW = ActualWidth > 0 ? ActualWidth : 160;
            _dragWinH = ActualHeight > 0 ? ActualHeight : 60;

            // DPI 也缓存：PresentationSource.FromVisual 本身不便宜，
            // 而缩放比在一次拖动里是常量（跨不同 DPI 的显示器拖动属于边角情况，
            // 那种情况下松手重拖即可，不值得为它每帧付一次查询成本）。
            PresentationSource src = PresentationSource.FromVisual(this);
            _dragScale = 1.0;
            if (src != null && src.CompositionTarget != null)
                _dragScale = src.CompositionTarget.TransformToDevice.M11;
            if (_dragScale <= 0) _dragScale = 1.0;

            _lastCursorX = int.MinValue;   // 保证第一帧一定被处理
            _lastCursorY = int.MinValue;
            _lastMoveStamp = 0;            // 节流从零开始：按下后的第一次搬运不受限

            _statsCalls = 0;
            _statsMoves = 0;
            _statsGated = 0;
            _statsCostSum = 0;
            _statsCostMax = 0;
            _statsGapSum = 0;
            _statsGapMax = 0;

            _dragCacheValid = true;
        }

        private void OnDragMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragging) return;
            FollowCursor();
        }

        /// <summary>松手时把本次拖动的统计写进 islet.log，用来判断「延迟高」还是「帧率低」。</summary>
        private void LogDragStats()
        {
            if (_statsMoves <= 0) return;

            double avgCost = _statsCostSum / _statsMoves;
            double avgGap = _statsMoves > 1 ? _statsGapSum / (_statsMoves - 1) : 0;

            Log.Info(string.Format(
                "拖动统计: calls={0} moves={1} gated={2} | 单次搬窗 平均{3:F2}ms 最大{4:F2}ms"
                + " | 更新间隔 平均{5:F1}ms 最大{6:F1}ms | 一帧预算16.6ms",
                _statsCalls, _statsMoves, _statsGated,
                avgCost, _statsCostMax, avgGap, _statsGapMax));
        }

        private void FollowCursor()
        {
            if (!_dragging) return;
            _statsCalls++;

            NativeMethods.POINT pt;
            if (!NativeMethods.GetCursorPos(out pt)) return;

            // 这个位置已经搬过了就什么都不做。用户手停下来时，
            // 后面几十上百次调用（MouseMove + 定时器）全是无用功，白白占着 UI 线程。
            if (pt.X == _lastCursorX && pt.Y == _lastCursorY) return;

            // DPI 比例在拖动期间不会变，已由 FillDragCache 缓存。
            double scale = _dragScale > 0 ? _dragScale : 1.0;

            double dx = (pt.X - _dragOrigin.X) / scale;
            double dy = (pt.Y - _dragOrigin.Y) / scale;

            // 小于 3 逻辑像素视为手抖，不算拖动，避免「点一下就把球挪走」
            if (!_dragMoved && Math.Abs(dx) < 3 && Math.Abs(dy) < 3) return;
            _dragMoved = true;

            // ---- 节流：两次真正搬窗之间至少隔 MoveGateMs ----
            //
            // 为什么需要它：鼠标回报率 1000Hz 时，一秒能来一千个 MouseMove，
            // 每个都搬一次窗（实测 0.9ms）就是 90% 的 UI 线程，渲染被挤掉就是「掉帧」。
            //
            // 为什么是 8ms 而不是 16ms：屏幕 60Hz（16.7ms 一帧）。
            // 如果按 16ms 搬，我们的节拍会和屏幕节拍**打架**，时不时有两帧看到同一个位置
            // 或者一帧跨过两格，肉眼就是抖。8ms ≈ 125 次/秒，比刷新率快一倍，
            // 每帧拿到的都是最新位置，也就不存在两个时钟互相拍的问题。
            //
            // 注意：被节流掉的位置**不能**写进 _lastCursorX/Y，否则那一格就被吞了，
            // 窗口会永远停在上一格（手停下时尤其明显）。
            if (_lastMoveStamp != 0)
            {
                double sinceMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _lastMoveStamp)
                                 * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (sinceMs < MoveGateMs)
                {
                    _statsGated++;
                    return;
                }

                _statsGapSum += sinceMs;
                if (sinceMs > _statsGapMax) _statsGapMax = sinceMs;
            }
            _lastMoveStamp = System.Diagnostics.Stopwatch.GetTimestamp();

            _lastCursorX = pt.X;
            _lastCursorY = pt.Y;

            // 锚点（圆环中心）跟着光标位移走，而不是让窗口左上角跟手。
            // 这样拖动过程中即使形态切换，球的位置也始终等于锚点。
            double newAnchorX = _dragAnchorX + dx;
            double newAnchorY = _dragAnchorY + dy;

            // 夹紧：保证**整个窗口**不越出虚拟屏幕，而不是只保证锚点在屏内。
            // 屏幕边界和窗口尺寸都用按下时缓存的值 —— 拖动过程中它们不会变，
            // 每帧去查 SystemParameters / ActualWidth 是拖顿的来源之一。
            if (!_dragCacheValid) FillDragCache();

            double w = _dragWinW;
            double h = _dragWinH;
            double vLeft = _dragVLeft;
            double vTop = _dragVTop;
            double vRight = _dragVRight;
            double vBottom = _dragVBottom;

            // 圆环中心可活动范围：窗口四边都不出屏
            double minAnchorX = vLeft + PadSide + DotSize / 2.0;
            double maxAnchorX = vRight - (w - PadSide - DotSize / 2.0);
            double minAnchorY = vTop + PadTop + DotSize / 2.0;
            double maxAnchorY = vBottom - (h - PadTop - DotSize / 2.0);

            newAnchorX = Clamp(newAnchorX, minAnchorX, Math.Max(minAnchorX, maxAnchorX));
            newAnchorY = Clamp(newAnchorY, minAnchorY, Math.Max(minAnchorY, maxAnchorY));

            FreeX = newAnchorX;
            FreeY = newAnchorY;
            Position = Core.IslandPosition.Free;

            // ⚠️ 这里必须是 false（不做过渡动画）。
            // 每帧重启一次 EaseOut 动画会让窗口永远追不上光标，
            // 表现为「拖得越快落得越远」—— 这是之前顿挫的根因。
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            PlaceAroundAnchor(newAnchorX, newAnchorY, w, h, false);

            // 遥测：单次搬窗耗时（见 _statsCostSum 的注释）
            double costMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0)
                            * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _statsMoves++;
            _statsCostSum += costMs;
            if (costMs > _statsCostMax) _statsCostMax = costMs;
        }

        private void OnDragEnd(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            bool wasDragging = _dragging;
            bool moved = _dragMoved;

            _dragging = false;
            _dragMoved = false;
            _dragCacheValid = false;      // 下次拖动重新量
            if (_dragTimer != null) _dragTimer.Stop();
            ReleaseMouseCapture();

            if (!wasDragging) return;

            // 拖动期间攒下的渲染请求，现在补上。
            // 必须在「记住位置」之前处理，否则展开态的内容会晚一拍。
            if (_hasPendingRender)
            {
                _hasPendingRender = false;
                IslandEvent pending = _pendingRender;
                _pendingRender = null;
                RenderNow(pending);
            }

            // 真的拖动过 → 记住新位置并通知宿主落盘。
            // 落盘的是「圆环中心」锚点（FollowCursor 已写进 FreeX/FreeY），
            // 不是窗口左上角 —— 否则下次启动形态一变，球又会偏。
            if (moved)
            {
                LogDragStats();

                Point anchor = CurrentAnchor();
                FreeX = anchor.X;
                FreeY = anchor.Y;
                Position = Core.IslandPosition.Free;

                EventHandler h = PositionChanged;
                if (h != null) h(this, EventArgs.Empty);
                return;
            }

            // 没移动 → 当作点击：展开成卡片
            if (!_expanded) SetExpanded(true);
        }

        // ---------------------------------------------------------------
        // 状态切换
        // ---------------------------------------------------------------

        /// <summary>渲染一个事件；null 表示隐藏为呼吸点。</summary>
        public void Render(IslandEvent evt)
        {
            // 拖动期间**绝不重建 UI**。
            //
            // 为什么：数据源每 2 秒发布一次事件，Render 会走 ShowBar/ShowCard，
            // 而那两个方法里有 UpdateLayout() + ApplyBarProgressWidth() + UpdateSpecClip()。
            // 拖动时每 16ms 就要跟着光标走，中间被强制布局打断一次，手感就是一顿一顿的。
            //
            // 这里只把最新事件记下来，松手后（OnDragEnd）再补渲染一次 ——
            // 拖动过程本来也不需要看到内容更新。
            if (_dragging)
            {
                _pendingRender = evt;
                _hasPendingRender = true;
                return;
            }

            RenderNow(evt);
        }

        /// <summary>拖动期间攒下的待渲染事件。松手后补上。</summary>
        private IslandEvent _pendingRender;
        private bool _hasPendingRender;

        private void RenderNow(IslandEvent evt)
        {
            _current = evt;

            if (evt == null)
            {
                if (_expanded) ShowIdleCard();
                else ShowDot();
                return;
            }

            ApplyContent(evt);

            if (_expanded) ShowCard();
            else ShowBar();
        }

        private void ApplyContent(IslandEvent evt)
        {
            _barTitle.Text = evt.Title;
            _barSubtitle.Text = evt.Subtitle ?? "";
            _barSubtitle.Visibility = string.IsNullOrEmpty(evt.Subtitle)
                ? Visibility.Collapsed : Visibility.Visible;

            if (evt.Progress.HasValue)
            {
                _barProgressHost.Visibility = Visibility.Visible;
                _barProgressValue = Clamp01(evt.Progress.Value);
                ApplyBarProgressWidth();
            }
            else
            {
                _barProgressHost.Visibility = Visibility.Collapsed;
            }

            _cardTitle.Text = evt.Title;
            _cardSubtitle.Text = evt.Subtitle ?? "";
            _cardSubtitle.Visibility = string.IsNullOrEmpty(evt.Subtitle)
                ? Visibility.Collapsed : Visibility.Visible;

            if (evt.Progress.HasValue)
            {
                _progressHost.Visibility = Visibility.Visible;
                _progressValue = Clamp01(evt.Progress.Value);
                ApplyProgressWidth();
            }
            else
            {
                _progressHost.Visibility = Visibility.Collapsed;
            }

            BuildActions(_barActions, evt, compact: true);
            BuildActions(_cardActions, evt, compact: false);
        }

        /// <summary>
        /// 把当前视觉树渲染成位图。分层窗口（WS_EX_LAYERED）无法用 CopyFromScreen 抓取，
        /// 这条路径直接走 WPF 渲染管线，结果与屏幕上看到的一致。
        /// </summary>
        public System.Windows.Media.Imaging.BitmapSource RenderToBitmap()
        {
            return RenderToBitmapWithBackdrop(false);
        }

        /// <summary>
        /// 把当前视觉树渲染成位图。
        /// checker = true 时铺一层灰白棋盘格 —— 只有棋盘格才能真正看出「透」：
        /// 纯色底上再透也只是一块颜色，肉眼分不出三档差别。
        /// </summary>
        public System.Windows.Media.Imaging.BitmapSource RenderToBitmapWithBackdrop(bool checker)
        {
            UpdateLayout();

            // 必须渲染内容元素而不是 Window：Window 的视觉根是窗口 chrome，
            // 直接 Render(this) 只会得到背景，内容不会进入位图。
            FrameworkElement visual = _root as FrameworkElement;
            if (visual == null) return null;

            double w = visual.ActualWidth > 0 ? visual.ActualWidth : 200;
            double h = visual.ActualHeight > 0 ? visual.ActualHeight : 40;

            System.Windows.Media.Imaging.RenderTargetBitmap rtb =
                new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96,
                    System.Windows.Media.PixelFormats.Pbgra32);

            DrawingVisual bg = new DrawingVisual();
            using (DrawingContext dc = bg.RenderOpen())
            {
                if (checker)
                {
                    const double Cell = 10;
                    SolidColorBrush a = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                    SolidColorBrush b = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8));
                    a.Freeze(); b.Freeze();
                    dc.DrawRectangle(b, null, new Rect(0, 0, w, h));
                    for (int row = 0; row * Cell < h; row++)
                    {
                        for (int col = 0; col * Cell < w; col++)
                        {
                            if (((row + col) & 1) == 0) continue;
                            dc.DrawRectangle(a, null,
                                new Rect(col * Cell, row * Cell, Cell, Cell));
                        }
                    }
                }
                else
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x6E, 0xA8)), null,
                                     new Rect(0, 0, w, h));
                }
            }
            rtb.Render(bg);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>诊断用：把紧凑条各层的实际几何吐出来，便于定位「算得出但看不见」。</summary>
        public string DiagnoseBar()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("BAR_PROBE:");
            sb.AppendLine("  bar vis=" + _bar.Visibility + " act=" + _bar.ActualWidth
                          + "x" + _bar.ActualHeight);
            sb.AppendLine("  progHost vis=" + _barProgressHost.Visibility
                          + " act=" + _barProgressHost.ActualWidth
                          + "x" + _barProgressHost.ActualHeight
                          + " z=" + Grid.GetZIndex(_barProgressHost));
            sb.AppendLine("  track act=" + _barProgressTrack.ActualWidth
                          + "x" + _barProgressTrack.ActualHeight);
            sb.AppendLine("  fill  w=" + _barProgressFill.Width
                          + " act=" + _barProgressFill.ActualWidth
                          + "x" + _barProgressFill.ActualHeight
                          + " vis=" + _barProgressFill.Visibility);
            sb.AppendLine("  barProgressValue=" + _barProgressValue);
            sb.AppendLine("  shell act=" + _shell.ActualWidth + "x" + _shell.ActualHeight);
            return sb.ToString();
        }

        /// <summary>
        /// 诊断用：把内容层每个子元素的实际可见性与几何吐出来。
        /// 截图里「条中间浮着一个圆」这种问题，光看代码判断不了是哪个元素画的，
        /// 必须让运行时自己报出「谁可见、多大、在哪个坐标」。
        /// </summary>
        public string DiagnoseLayers()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("LAYER_PROBE:");
            sb.AppendLine("  _content act=" + _content.ActualWidth + "x" + _content.ActualHeight);
            // 形态层都在 _mainHost（_content 第 0 列）里，这里直接报它们，
            // 免得再加一层容器后诊断输出只剩「一个 Grid」看不出问题。
            for (int i = 0; i < _mainHost.Children.Count; i++)
            {
                UIElement el = _mainHost.Children[i];
                FrameworkElement fe = el as FrameworkElement;
                string line = "  main[" + i + "] " + el.GetType().Name;
                if (fe != null)
                {
                    line += " vis=" + fe.Visibility
                          + " act=" + fe.ActualWidth + "x" + fe.ActualHeight
                          + " ha=" + fe.HorizontalAlignment
                          + " va=" + fe.VerticalAlignment;
                    try
                    {
                        GeneralTransform t = fe.TransformToAncestor(_content);
                        Point p = t.Transform(new Point(0, 0));
                        line += " at=(" + Math.Round(p.X, 1) + "," + Math.Round(p.Y, 1) + ")";
                    }
                    catch (Exception) { line += " at=?"; }
                }
                sb.AppendLine(line);
            }

            sb.AppendLine("  _stashBox vis=" + _stashBox.Visibility
                          + " act=" + _stashBox.ActualWidth + "x" + _stashBox.ActualHeight
                          + " rows=" + (_stashRows == null ? 0 : _stashRows.Children.Count)
                          + " extra=" + StashExtra());
            sb.AppendLine("  _form=" + _form + " stashCount=" + _stashCount
                          + " stashDragging=" + _stashDragging);
            sb.AppendLine("  shell min/max=" + _shell.MinWidth + "/" + _shell.MaxWidth
                          + " w/h=" + _shell.Width + "/" + _shell.Height);

            sb.AppendLine("  _dot vis=" + _dot.Visibility + " " + _dot.Width + "x" + _dot.Height);
            sb.AppendLine("  _dotHalo vis=" + _dotHalo.Visibility + " "
                          + _dotHalo.Width + "x" + _dotHalo.Height);
            sb.AppendLine("  _spec vis=" + _spec.Visibility + " act="
                          + _spec.ActualWidth + "x" + _spec.ActualHeight);
            sb.AppendLine("  _innerRim vis=" + _innerRim.Visibility);
            return sb.ToString();
        }

        /// <summary>按比例设置填充条宽度。宽度取自宿主 Grid 的实际宽度。</summary>
        private void ApplyProgressWidth()
        {
            double available = _progressHost.ActualWidth;
            if (available <= 0) return;
            _progressFill.Width = Math.Max(0, Math.Min(available, available * _progressValue));
        }

        private void ApplyBarProgressWidth()
        {
            double available = _barProgressHost.ActualWidth;
            if (available <= 0) return;
            _barProgressFill.Width = Math.Max(0, Math.Min(available,
                                                          available * _barProgressValue));
        }

        private static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        /// <summary>
        /// 按钮模板：圆角胶囊、细边框、悬停提亮。
        /// WPF 默认 Button 模板在 AllowsTransparency 窗口里会带灰边和白底，必须替换。
        /// </summary>
        private static ControlTemplate BuildButtonTemplate()
        {
            return ButtonTemplate.Value;
        }

        private static readonly Lazy<ControlTemplate> ButtonTemplate =
            new Lazy<ControlTemplate>(CreateButtonTemplate);

        private static ControlTemplate CreateButtonTemplate()
        {
            ControlTemplate t = new ControlTemplate(typeof(Button));

            FrameworkElementFactory border =
                new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
            border.SetValue(Border.BackgroundProperty,
                new TemplateBindingExtension(Button.BackgroundProperty));
            border.SetValue(Border.BorderBrushProperty,
                new TemplateBindingExtension(Button.BorderBrushProperty));
            border.SetValue(Border.BorderThicknessProperty,
                new TemplateBindingExtension(Button.BorderThicknessProperty));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);

            FrameworkElementFactory presenter =
                new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty,
                VerticalAlignment.Center);
            border.AppendChild(presenter);

            t.VisualTree = border;

            Trigger hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), "Bd"));
            hover.Setters.Add(new Setter(Border.BorderBrushProperty,
                new SolidColorBrush(Color.FromArgb(0x46, 0xFF, 0xFF, 0xFF)), "Bd"));
            t.Triggers.Add(hover);

            Trigger down = new Trigger
            {
                Property = Button.IsPressedProperty,
                Value = true
            };
            down.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromArgb(0x4A, 0xFF, 0xFF, 0xFF)), "Bd"));
            t.Triggers.Add(down);

            return t;
        }

        private void BuildActions(StackPanel host, IslandEvent evt, bool compact)
        {
            host.Children.Clear();
            if (evt.Actions == null) return;

            int max = compact ? 2 : 3;
            for (int i = 0; i < evt.Actions.Length && i < max; i++)
            {
                IslandAction act = evt.Actions[i];
                Button b = new Button
                {
                    Content = act.Label,
                    FontSize = compact ? 12 : 12.5,
                    Foreground = new SolidColorBrush(TextPrimary),
                    // 浅玻璃上的按钮用「淡蓝底 + 深字」，比纯白半透更清楚
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x3A, 0x62, 0xB0)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x3C, 0x3A, 0x62, 0xB0)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(compact ? 9 : 15, compact ? 3 : 7,
                                            compact ? 9 : 15, compact ? 3 : 7),
                    Margin = new Thickness(compact ? 5 : 0, 0, compact ? 0 : 8, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    // 不随父容器拉伸：否则按钮会撑满整行高度并变形
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };
                if (!compact) b.MinHeight = 32;

                // 用 ControlTemplate 去掉系统默认的边框与按下态填充
                b.Template = BuildButtonTemplate();
                b.Resources.Add(SystemColors.ControlTextBrushKey,
                                new SolidColorBrush(TextPrimary));

                b.Click += (s, e) =>
                {
                    try { act.Invoke(); }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Islet] 动作失败: " + ex.Message);
                    }
                };
                host.Children.Add(b);
            }
        }

        private void ShowDot()
        {
            _form = IslandForm.Dot;

            _dot.Visibility = Visibility.Visible;
            _dotHalo.Visibility = Visibility.Visible;
            _bar.Visibility = Visibility.Collapsed;
            _card.Visibility = Visibility.Collapsed;

            _shell.CornerRadius = new CornerRadius(DotSize / 2.0);
            _innerRim.CornerRadius = new CornerRadius(DotSize / 2.0 - 1);
            _spec.CornerRadius = new CornerRadius(DotSize / 2.0);
            _ring.CornerRadius = new CornerRadius(DotSize / 2.0 + 2.5);

            ApplyMetrics();
            _shell.BorderThickness = new Thickness(1);

            _spec.Visibility = Visibility.Collapsed;
            _innerRim.Visibility = Visibility.Visible;
            Cursor = System.Windows.Input.Cursors.Hand;

            Fade(0.92, 400);
            Reposition();
            UpdateSpecClip();
        }

        private void ShowBar()
        {
            _form = IslandForm.Bar;

            _dot.Visibility = Visibility.Collapsed;
            _dotHalo.Visibility = Visibility.Collapsed;
            _bar.Visibility = Visibility.Visible;
            _card.Visibility = Visibility.Collapsed;

            _shell.CornerRadius = new CornerRadius(BarHeight / 2.0);
            _innerRim.CornerRadius = new CornerRadius(BarHeight / 2.0 - 1);
            _spec.CornerRadius = new CornerRadius(BarHeight / 2.0);
            _ring.CornerRadius = new CornerRadius(BarHeight / 2.0 + 2.5);

            ApplyMetrics();
            _shell.BorderThickness = new Thickness(1);

            _spec.Visibility = Visibility.Visible;
            _innerRim.Visibility = Visibility.Visible;
            Cursor = System.Windows.Input.Cursors.Arrow;

            Fade(1.0, 180);
            Reposition();

            // 布局完成后才拿得到真实宽度。同步 UpdateLayout 比 Dispatcher 回调更可靠，
            // 否则首帧进度线宽度为 0，用户要等到下一次进度更新才看得见。
            UpdateLayout();
            ApplyBarProgressWidth();
            ApplyProgressWidth();
            UpdateSpecClip();
        }

        private void ShowCard()
        {
            _form = IslandForm.Card;

            _dot.Visibility = Visibility.Collapsed;
            _dotHalo.Visibility = Visibility.Collapsed;
            _bar.Visibility = Visibility.Collapsed;
            _card.Visibility = Visibility.Visible;

            _shell.CornerRadius = new CornerRadius(20);
            _innerRim.CornerRadius = new CornerRadius(19);
            _spec.CornerRadius = new CornerRadius(20);
            _ring.CornerRadius = new CornerRadius(22.5);

            ApplyMetrics();
            _shell.BorderThickness = new Thickness(1);

            _spec.Visibility = Visibility.Visible;
            _innerRim.Visibility = Visibility.Visible;
            Cursor = System.Windows.Input.Cursors.Arrow;

            Fade(1.0, 180);
            Reposition();
            UpdateSpecClip();
        }

        public void SetExpanded(bool expand)
        {
            if (_expanded == expand) return;
            _expanded = expand;

            // 注意：这里曾经写成「_current == null 一律 ShowDot()」，
            // 结果槽位空时无论点多少次都只是把隐藏点重画一遍——用户看到的就是
            // 「点了没反应」。PRD §5.3 要求点击隐藏点必须强制展开为卡片，
            // 所以展开方向优先，空槽位也要给出一张可读的卡片。
            if (expand)
            {
                // 优先级：有应用在出声 → 直接给音量滑块。
                // 用户点开岛时最可能想做的是「把刚才那个声音调小/静音」，
                // 这比看事件详情更即时。没有声音时才回落到原有卡片逻辑。
                if (TryShowMixer()) { }
                else if (_current == null) ShowIdleCard();
                else ShowCard();
            }
            else
            {
                StopMixerRefresh();
                _mixer.Visibility = Visibility.Collapsed;
                if (_current == null) ShowDot();
                else ShowBar();
            }

            EventHandler h = ExpandedChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        // ---------------------------------------------------------------
        // 岛内音量面板
        // ---------------------------------------------------------------

        /// <summary>一行音量控件持有的引用。</summary>
        private sealed class MixerRow
        {
            public string Key;
            public Slider Slider;
            public TextBlock Percent;
            public Border FillTrack;
            public bool Suppress;
        }

        /// <summary>
        /// 展开态下，如果有应用正在出声，就把卡片换成音量滑块。
        ///
        /// 为什么要替换而不是并列：
        ///   岛的空间很小，并列会把卡片撑得很高；而且用户点开时的心智
        ///   「我现在想调刚才那个声音」远强于「我想看事件详情」。
        ///   没有应用出声时才回落到普通卡片。
        /// </summary>
        private bool TryShowMixer()
        {
            List<Islet.Sources.AudioSession> playing =
                Islet.Sources.NowPlayingProvider.FindPlaying();

            if (playing.Count == 0)
            {
                StopMixerRefresh();
                return false;
            }

            ShowMixer(playing);
            return true;
        }

        private void ShowMixer(List<Islet.Sources.AudioSession> playing)
        {
            _form = IslandForm.Mixer;

            _dot.Visibility = Visibility.Collapsed;
            _dotHalo.Visibility = Visibility.Collapsed;
            _bar.Visibility = Visibility.Collapsed;
            _card.Visibility = Visibility.Collapsed;
            _mixer.Visibility = Visibility.Visible;

            _shell.CornerRadius = new CornerRadius(16);
            _innerRim.CornerRadius = new CornerRadius(15);
            _spec.CornerRadius = new CornerRadius(16);
            _ring.CornerRadius = new CornerRadius(18.5);

            ApplyMetrics();
            _shell.BorderThickness = new Thickness(1);

            _spec.Visibility = Visibility.Visible;
            _innerRim.Visibility = Visibility.Visible;
            Cursor = System.Windows.Input.Cursors.Arrow;

            RebuildMixer(playing);
            StartMixerRefresh();

            Fade(1.0, 180);
            Reposition();
            UpdateSpecLayoutSafe();
        }

        private void RebuildMixer(List<Islet.Sources.AudioSession> playing)
        {
            _mixerRows.Children.Clear();
            _mixerRowsByKey.Clear();

            // 只显示「正在出声」的，最多 4 个 —— 再多岛就太长了。
            int shown = 0;
            foreach (Islet.Sources.AudioSession s in playing)
            {
                if (shown >= 4) break;
                Border row = BuildMixerRow(s);
                _mixerRows.Children.Add(row);
                shown++;
            }

            _mixerHint.Text = playing.Count > 4
                ? "只列出正在出声的 " + shown + " 个（共 " + playing.Count + " 个）"
                : "拖动滑块即时生效 · 只影响该应用，不动系统总音量";
        }

        private Border BuildMixerRow(Islet.Sources.AudioSession s)
        {
            Border card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0x1B, 0x1F, 0x28)),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 8, 0, 0)
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel body = new StackPanel();

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock name = new TextBlock
            {
                Text = s.DisplayName,
                FontSize = 12,
                Foreground = new SolidColorBrush(TextPrimary),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 0);
            head.Children.Add(name);

            TextBlock pct = new TextBlock
            {
                Text = ((int)Math.Round(s.Volume * 100)).ToString() + "%",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(TextSecondary),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(pct, 1);
            head.Children.Add(pct);

            body.Children.Add(head);

            Slider slider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = Math.Round(s.Volume * 100.0),
                Margin = new Thickness(0, 2, 0, 0),
                IsMoveToPointEnabled = true,
                Height = 16
            };
            slider.Template = VolumePanel.SliderTemplate();
            body.Children.Add(slider);

            Grid.SetColumn(body, 0);
            grid.Children.Add(body);

            Button mute = new Button
            {
                Content = s.Muted ? "🔇" : "🔊",
                Width = 26,
                Height = 24,
                FontSize = 11.5,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0x1B, 0x1F, 0x28)),
                Foreground = new SolidColorBrush(TextPrimary),
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Focusable = false
            };
            mute.Template = VolumePanel.PillTemplate();
            Grid.SetColumn(mute, 1);
            grid.Children.Add(mute);

            card.Child = grid;

            MixerRow row = new MixerRow
            {
                Key = s.Key,
                Slider = slider,
                Percent = pct,
                FillTrack = null
            };
            card.Resources["row"] = row;
            _mixerRowsByKey[s.Key] = row;

            // 拖动期间锁定该行，避免定时刷新把滑块拽回去
            slider.PreviewMouseLeftButtonDown += (o, e) => { _mixerDragging = s.Key; };
            slider.PreviewMouseLeftButtonUp += (o, e) =>
            {
                if (_mixerDragging == s.Key) _mixerDragging = null;
            };

            slider.ValueChanged += (o, e) =>
            {
                if (row.Suppress) return;

                int p = (int)Math.Round(e.NewValue);
                pct.Text = p + "%";

                Islet.Sources.AudioSession target = ResolveAudio(s.Key);
                if (target == null) return;

                float applied = GetAudioProvider().SetVolume(
                    target, (float)(e.NewValue / 100.0));
                if (applied < 0)
                {
                    // 写失败就退回真实值，不骗用户「已经调好」
                    row.Suppress = true;
                    slider.Value = Math.Round(target.Volume * 100.0);
                    row.Suppress = false;
                }
            };

            mute.Click += (o, e) =>
            {
                Islet.Sources.AudioSession target = ResolveAudio(s.Key);
                if (target == null) return;
                bool? now = GetAudioProvider().SetMute(target, !target.Muted);
                if (now.HasValue) mute.Content = now.Value ? "🔇" : "🔊";
            };

            return card;
        }

        private Islet.Sources.AudioSessionProvider GetAudioProvider()
        {
            if (_audioProvider == null) _audioProvider =
                new Islet.Sources.AudioSessionProvider();
            return _audioProvider;
        }

        /// <summary>
        /// 刷新一次音频会话；默认播放设备变了就记一条日志。
        ///
        /// 「插耳机后岛上没反应」这类问题，光看界面现象无从下手。
        /// 有这条日志就能直接确认：是设备没切过来，还是切过来了但枚举不到会话。
        /// </summary>
        private bool RefreshAudio(bool logDeviceChange)
        {
            Islet.Sources.AudioSessionProvider p = GetAudioProvider();
            bool ok = p.Refresh();
            if (logDeviceChange && p.DeviceChangedLastRefresh)
            {
                Log.Info("默认播放设备已切换: " + (p.DeviceId ?? "(未知)")
                    + "，已重建会话管理器，会话数=" + p.Sessions.Count);
            }
            return ok;
        }

        /// <summary>
        /// 按 Key 到**当前这轮**会话里重新解析。
        /// 绝不能缓存 AudioSession 对象：provider 每次 Refresh 都会释放旧 COM 接口，
        /// 拿旧对象去写会静默失败（音量面板那边踩过这个坑）。
        /// </summary>
        private Islet.Sources.AudioSession ResolveAudio(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            Islet.Sources.AudioSessionProvider p = GetAudioProvider();
            foreach (Islet.Sources.AudioSession s in p.Sessions)
            {
                if (s.Key == key) return s;
            }
            return null;
        }

        private void StartMixerRefresh()
        {
            if (_mixerTimer == null)
            {
                _mixerTimer = new System.Windows.Threading.DispatcherTimer
                {
                    // 只在面板展开时跑。3 秒一次足够跟上「别处改了音量」这种变化，
                    // 再快没有意义——用户自己拖动时是即时写入的，不靠这个定时器。
                    // 每次 Tick 都要枚举系统全部音频会话，而枚举本身会让音频引擎
                    // 留下会话对象，所以这个频率是性能红线，不要调快。
                    Interval = TimeSpan.FromMilliseconds(3000)
                };
                _mixerTimer.Tick += (s, e) => RefreshMixer();
            }
            _mixerTimer.Stop();
            _mixerTimer.Start();
        }

        private void StopMixerRefresh()
        {
            if (_mixerTimer != null) _mixerTimer.Stop();
        }

        /// <summary>刷新各行的音量读数；正在拖动的那一行跳过。</summary>
        private void RefreshMixer()
        {
            if (_mixer.Visibility != Visibility.Visible) { StopMixerRefresh(); return; }

            Islet.Sources.AudioSessionProvider p = GetAudioProvider();
            if (!RefreshAudio(true)) return;

            foreach (Islet.Sources.AudioSession s in p.Sessions)
            {
                MixerRow row;
                if (!_mixerRowsByKey.TryGetValue(s.Key, out row)) continue;
                if (row.Key == _mixerDragging) continue;

                row.Suppress = true;
                row.Slider.Value = Math.Round(s.Volume * 100.0);
                row.Suppress = false;
                row.Percent.Text = ((int)Math.Round(s.Volume * 100)).ToString() + "%";
            }
        }

        /// <summary>UpdateSpecClip 的安全版：布局未就绪时跳过，避免拿到 0 尺寸。</summary>
        private void UpdateSpecLayoutSafe()
        {
            UpdateLayout();
            UpdateSpecClip();
        }

        /// <summary>
        /// 槽位为空时的卡片。它不占用仲裁槽位，只是把「当前没有事情」如实说出来，
        /// 顺带给出下一步能做什么——否则用户点开一片空白，会以为程序坏了。
        /// </summary>
        private void ShowIdleCard()        {
            _cardTitle.Text = "浮屿待命中";
            _cardSubtitle.Text = "当前没有需要关注的事";
            _cardSubtitle.Visibility = Visibility.Visible;
            _progressHost.Visibility = Visibility.Collapsed;
            _cardActions.Children.Clear();
            _barActions.Children.Clear();

            ShowCard();
        }

        private System.Windows.Threading.DispatcherTimer _collapseTimer;

        /// <summary>悬停多久才自动展开（用户要求：停在上面半秒没有操作再展开）。</summary>
        private const int HoverExpandDelayMs = 500;

        /// <summary>悬停展开的倒计时。按下鼠标 / 鼠标离开都会取消它。</summary>
        private System.Windows.Threading.DispatcherTimer _hoverTimer;

        /// <summary>
        /// 鼠标进入岛 → 起一个 500ms 倒计时，到点才真正展开。
        ///
        /// 为什么不再「一碰就展开」：条形态贴在右边缘时，鼠标刚碰到它就会弹成
        /// 400 宽的大框，而大框不能越过屏幕右边 —— 用户想「把它推到最右」时，
        /// 手一碰它就先变大了，于是永远推不到边。延迟把三种意图分开了：
        ///   路过 → 不到半秒就走了，不展开；
        ///   想拖 → 按下鼠标立刻取消倒计时（见 OnDragStart），拖的时候它还是个条；
        ///   想看 → 停在上面不动，半秒后展开。
        /// </summary>
        private void StartHoverExpand()
        {
            // 圆环态不悬停展开：它是常驻小挂件，用户经常只是划过或想按住拖走。
            // 圆环态仍然「点击才展开」。
            if (_form == IslandForm.Dot) return;
            if (_expanded) return;

            if (_hoverTimer == null)
            {
                // 用 Background 优先级：这是「没人操作时才会发生的事」，
                // 不能跟渲染、拖动跟随抢时间片。
                _hoverTimer = new System.Windows.Threading.DispatcherTimer(
                    System.Windows.Threading.DispatcherPriority.Background);
                _hoverTimer.Interval = TimeSpan.FromMilliseconds(HoverExpandDelayMs);
                _hoverTimer.Tick += (s, e) =>
                {
                    _hoverTimer.Stop();

                    // 到点时再确认一次：这半秒里用户可能已经按下鼠标在拖，
                    // 也可能已经把鼠标移走了 —— 两种情况都不该展开。
                    if (_dragging || _stashDragging) return;
                    if (!IsMouseOver) return;

                    SetExpanded(true);
                };
            }

            _hoverTimer.Stop();
            _hoverTimer.Start();
        }

        private void CancelHoverExpand()
        {
            if (_hoverTimer != null) _hoverTimer.Stop();
        }

        private void ScheduleCollapse()
        {
            if (_collapseTimer == null)
            {
                _collapseTimer = new System.Windows.Threading.DispatcherTimer();
                _collapseTimer.Interval = TimeSpan.FromMilliseconds(400);
                _collapseTimer.Tick += (s, e) =>
                {
                    _collapseTimer.Stop();
                    if (_stashDragging) return;      // 拖出文件期间保持展开
                    // 正在拖岛时也绝不收起：Show* 里有 UpdateLayout()，
                    // 每 16ms 一次的跟随被强制布局打断就是一顿一顿的。
                    if (_dragging) return;
                    if (!IsMouseOver) SetExpanded(false);
                };
            }
            _collapseTimer.Stop();
            _collapseTimer.Start();
        }

        private void Fade(double to, int ms)
        {
            DoubleAnimation a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms));
            a.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, a);
        }

        /// <summary>显示而不夺焦。</summary>
        public void ShowNoActivate()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        }
    }
}
