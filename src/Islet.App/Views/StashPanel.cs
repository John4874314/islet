using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Islet.App.Services;

namespace Islet.App.Views
{
    /// <summary>
    /// 临时存放站（独立小窗）—— **已废弃，不要把宿主接回来**。
    ///
    /// 用户明确否掉了这个形态：中转区必须长在岛的玻璃内部右侧，和音乐播放等形态
    /// 共用同一块玻璃。现在的实现是 IslandWindow._stashBox（_content 第二列），
    /// 数据层仍是 StashStore。本文件只为「万一要回退」保留，IsletHost 已不再引用。
    /// 回退快照：E:\dsh\backups\20261105-144423-islet-source-before-stash-inline
    ///
    /// 下面是这个已废弃形态的历史说明：
    /// 临时存放站（独立小窗）。
    ///
    /// 为什么是独立窗口而不是岛的新形态：VolumePanel 已经证明这个模式在本项目
    /// 成熟可靠，而 IslandWindow 的形态系统（SizeToContent / 拖动 / 位移动画）
    /// 历史坑很多，往里再加一种形态风险太高。岛只作为「拖入入口 + 数量提示」。
    ///
    /// 交互：
    ///   · 拖文件到本窗口或岛上 → 复制副本进暂存站
    ///   · 从某一行拖出去 → 标准 FileDrop，可放进资源管理器 / 聊天框 / agent 输入框
    ///   · 每行一个 × 按钮 → 移除该条目（只删暂存副本）
    ///   · 「清空」→ 清空全部
    ///
    /// 拖出用 DragDropEffects.Copy | Move：由**接收方**决定用哪种。
    /// 资源管理器默认 Copy（按 Shift 才是 Move），聊天框一般只收 Copy。
    /// 只有返回 Move 才把条目从列表移除 —— 移走的始终是暂存副本，不是用户原文件。
    /// </summary>
    internal sealed class StashPanel : Window
    {
        private static readonly FontFamily UiFont =
            new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI, SimSun");

        private static readonly Brush CardBg =
            new SolidColorBrush(Color.FromRgb(0x1E, 0x20, 0x28));
        private static readonly Brush RowBg =
            new SolidColorBrush(Color.FromRgb(0x2A, 0x2D, 0x37));
        private static readonly Brush RowHover =
            new SolidColorBrush(Color.FromRgb(0x32, 0x36, 0x42));
        private static readonly Brush TextMain = Brushes.White;
        private static readonly Brush TextSub =
            new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
        private static readonly Brush AccentBrush =
            new SolidColorBrush(Color.FromRgb(0x4C, 0x8C, 0xF5));

        private readonly StashStore _store;
        private readonly StackPanel _list;
        private readonly TextBlock _status;

        /// <summary>条目数量变化时通知宿主，用于更新岛上的角标。</summary>
        public event EventHandler CountChanged;

        /// <summary>本次拖出涉及的文件 id，拖放结束后据此决定是否移除。</summary>
        private string _draggingId;
        private bool _forceClose;

        /// <summary>宿主退出前调用，让 Close() 真的关闭而不是被隐藏。</summary>
        public void ForceClose() { _forceClose = true; Close(); }

        public StashPanel(StashStore store)
        {
            _store = store;

            Title = "临时存放站";
            Width = 380;
            MinHeight = 180;
            MaxHeight = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = CardBg;
            FontFamily = UiFont;
            ShowInTaskbar = false;
            Topmost = true;

            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

            // ---- 标题行 ----
            Grid head = new Grid { Margin = new Thickness(16, 14, 16, 10) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel titles = new StackPanel();
            titles.Children.Add(new TextBlock
            {
                Text = "临时存放站",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextMain
            });
            titles.Children.Add(new TextBlock
            {
                Text = "拖文件进来 · 从列表拖出去发给好友或放进文件夹",
                FontSize = 11,
                Foreground = TextSub,
                Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetColumn(titles, 0);
            head.Children.Add(titles);

            Button clear = MakeSmallButton("清空");
            clear.Click += (s, e) => { _store.Clear(); Refresh(); };
            Grid.SetColumn(clear, 1);
            head.Children.Add(clear);

            // ---- 列表 ----
            _list = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };

            // ---- 底部状态 ----
            _status = new TextBlock
            {
                FontSize = 11,
                Foreground = TextSub,
                Margin = new Thickness(16, 8, 16, 14),
                TextWrapping = TextWrapping.Wrap
            };

            StackPanel root = new StackPanel();
            root.Children.Add(head);
            root.Children.Add(_list);
            root.Children.Add(_status);
            Content = root;

            // ---- 拖入：整个窗口都是放置目标 ----
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragOver += OnDragOver;
            Drop += OnDrop;

            Closing += (s, e) =>
            {
                // 用户点关闭 → 只隐藏，数据层保留到退出。
                // 但宿主 Dispose 时会先 Show() 再 Close()，那种情况必须真的关掉，
                // 所以用一个标志区分：_forceClose 为真时不取消。
                if (_forceClose) return;
                e.Cancel = true;
                Hide();
            };

            Refresh();
        }

        // ---------------------------------------------------------------
        // 拖入
        // ---------------------------------------------------------------

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            SetEffect(e);
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            SetEffect(e);
        }

        private void SetEffect(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
            }
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (paths == null || paths.Length == 0) return;

            AddFiles(paths);
            e.Handled = true;
        }

        /// <summary>供岛（或其它入口）在收到拖入后调用。</summary>
        public void AddFiles(string[] paths)
        {
            List<string> errors = new List<string>();
            IList<StashItem> added = _store.Add(paths, errors);

            Refresh();

            if (added.Count == 0 && errors.Count > 0)
                _status.Text = string.Join("；", errors.ToArray());
            else if (errors.Count > 0)
                _status.Text = "已加入 " + added.Count + " 个；" + string.Join("；", errors.ToArray());

            // 把窗口带到前面，让用户立刻看到结果
            if (!IsVisible) Show();
            Activate();
        }

        // ---------------------------------------------------------------
        // 列表渲染
        // ---------------------------------------------------------------

        private void Refresh()
        {
            IList<StashItem> items = _store.Snapshot();
            _list.Children.Clear();

            foreach (StashItem it in items)
            {
                _list.Children.Add(MakeRow(it));
            }

            if (items.Count == 0)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = "（空）把文件拖到这里即可暂存",
                    FontSize = 12,
                    Foreground = TextSub,
                    Margin = new Thickness(6, 18, 6, 18),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                _status.Text = "重启浮屿后自动清空 · 原文件不会被改动";
            }
            else
            {
                _status.Text = items.Count + " 个文件 · 拖出去即可复制/移动 · 重启后清空";
            }

            EventHandler h = CountChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        private Border MakeRow(StashItem item)
        {
            Border row = new Border
            {
                Background = RowBg,
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 8, 8, 8),
                Cursor = Cursors.Hand,
                AllowDrop = false
            };

            Grid g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 12.5,
                Foreground = TextMain,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = FormatSize(item.Size) + "  ·  " + item.AddedAt.ToString("HH:mm"),
                FontSize = 10.5,
                Foreground = TextSub,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(info, 0);
            g.Children.Add(info);

            Button del = MakeSmallButton("×");
            del.ToolTip = "移除（只删暂存副本）";
            del.Click += (s, e) => { _store.Remove(item.Id); Refresh(); };
            Grid.SetColumn(del, 1);
            g.Children.Add(del);

            row.Child = g;

            row.MouseEnter += (s, e) => row.Background = RowHover;
            row.MouseLeave += (s, e) => row.Background = RowBg;

            // ---- 拖出：标准 FileDrop ----
            //
            // 用 PreviewMouseMove + 左键判定，而不是 DoDragDrop 的 MouseDown 里直接调：
            // 前者能避开「点 × 按钮时误触发拖出」，也符合资源管理器里
            // 「按住拖动」的直觉。
            row.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // 点在 × 按钮上不启动拖出
                if (IsDescendantOf(e.OriginalSource as DependencyObject, del)) return;
                _dragCandidateId = item.Id;
                _dragStart = e.GetPosition(row);
            };
            row.PreviewMouseMove += (s, e) =>
            {
                if (_dragCandidateId != item.Id) return;
                if (e.LeftButton != MouseButtonState.Pressed) return;

                Point now = e.GetPosition(row);
                if (Math.Abs(now.X - _dragStart.X) < 4 && Math.Abs(now.Y - _dragStart.Y) < 4)
                    return;

                _dragCandidateId = null;
                StartDragOut(item);
            };
            row.PreviewMouseLeftButtonUp += (s, e) => { _dragCandidateId = null; };

            return row;
        }

        private string _dragCandidateId;
        private Point _dragStart;

        private void StartDragOut(StashItem item)
        {
            if (!File.Exists(item.Path)) return;

            _draggingId = item.Id;

            // FileDrop 是 Windows 通用格式：资源管理器、微信/QQ 聊天框、
            // 浏览器上传框、agent 输入框都认它，不需要逐个适配。
            DataObject data = new DataObject();
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { item.Path });

            try
            {
                DragDropEffects result = DragDrop.DoDragDrop(
                    this, data, DragDropEffects.Copy | DragDropEffects.Move);

                bool moved = (result & DragDropEffects.Move) != 0;
                _store.OnDragCompleted(item.Id, moved);
            }
            catch (Exception ex)
            {
                Log.Info("收纳站拖出失败: " + ex.Message);
            }
            finally
            {
                _draggingId = null;
            }

            Refresh();
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

        private static Button MakeSmallButton(string text)
        {
            Button b = new Button
            {
                Content = text,
                FontSize = 12,
                Foreground = TextMain,
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            return b;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return (bytes / (1024.0 * 1024.0)).ToString("F1") + " MB";
        }

        /// <summary>宿主用来更新岛上的暂存数量。</summary>
        public int Count { get { return _store.Count; } }
    }
}
