using System;
using System.Drawing;
using System.Windows.Forms;

namespace Islet.App.Services
{
    /// <summary>托盘图标与右键菜单。避免引入 WinForms 之外的依赖。</summary>
    internal sealed class TrayIcon : IDisposable
    {
        private NotifyIcon _icon;
        private ContextMenuStrip _menu;

        public event EventHandler ToggleRequested;
        public event EventHandler StartTimerRequested;
        public event EventHandler VolumeRequested;
        public event EventHandler SettingsRequested;
        public event EventHandler ExitRequested;

        public void Show()
        {
            _menu = new ContextMenuStrip();

            ToolStripMenuItem toggle = new ToolStripMenuItem("显示 / 隐藏");
            toggle.Click += (s, e) => Raise(ToggleRequested);
            _menu.Items.Add(toggle);

            ToolStripMenuItem timer = new ToolStripMenuItem("开始番茄钟 (25 分钟)");
            timer.Click += (s, e) => Raise(StartTimerRequested);
            _menu.Items.Add(timer);

            ToolStripMenuItem volume = new ToolStripMenuItem("应用音量…");
            volume.Click += (s, e) => Raise(VolumeRequested);
            _menu.Items.Add(volume);

            // 这里刻意**没有**「临时存放站…」菜单项：
            // 中转区现在长在岛的玻璃内部右侧，不是一个可以单独打开的窗口，
            // 留着入口只会让人以为还有第二个界面。要用它就把文件拖到岛上，
            // 或者点一下岛展开 —— 方框会自己出现在右侧。

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem settings = new ToolStripMenuItem("设置…");
            settings.Click += (s, e) => Raise(SettingsRequested);
            _menu.Items.Add(settings);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exit = new ToolStripMenuItem("退出");
            // 关键：这个 Click 是在 WinForms 的**菜单模态循环**里触发的。
            // 直接在循环内调 Application.Shutdown() 会让 WPF 的关闭流程
            // 和菜单的嵌套消息泵互相等待 —— 表现就是「点了退出，没反应」。
            // 所以这里只做一件事：把真正的退出**投递回 WPF 消息队列**，
            // 等菜单循环先结束，再执行关闭。
            exit.Click += (s, e) => Raise(ExitRequested);
            _menu.Items.Add(exit);

            _icon = new NotifyIcon
            {
                Icon = CreateIcon(),
                Text = "浮屿 Islet",
                Visible = true,
                ContextMenuStrip = _menu
            };

            // 双击托盘也打开设置：这是用户的第一直觉
            _icon.DoubleClick += (s, e) => Raise(SettingsRequested);
        }

        /// <summary>
        /// 取应用图标。
        ///
        /// 优先从**当前 exe 自身**里取（build.ps1 已用 /win32icon 嵌进去），
        /// 这样托盘、任务栏、资源管理器永远是同一张图，不会出现「托盘一个样、
        /// 文件一个样」。取不到时才退回到同目录的 islet.ico，最后兜底系统图标。
        ///
        /// 为什么要挑尺寸：托盘在 100% 缩放下只有 16px，而
        /// Icon.ExtractAssociatedIcon 给的是 32px 那一帧。直接塞进去会被
        /// 重采样糊掉（实测 new Icon(big,24,24) 也仍返回 32px，因为 ICO 里
        /// 没有 24px 帧，.NET 会回退到最接近的更大帧）。
        /// 所以这里显式从**文件**里按目标尺寸取帧 —— ICO 里有 16/20/24 各一帧。
        /// </summary>
        private static Icon CreateIcon()
        {
            int want = TrayPixelSize();

            // 首选：按目标尺寸从 ico 文件里取精确的那一帧
            Icon exact = LoadSizedFromIcoFile(want);
            if (exact != null) return exact;

            // 次选：从 exe 资源取（可能被缩放，但至少是同一张图）
            Icon fromExe = LoadFromOwnExe(want);
            if (fromExe != null) return fromExe;

            return SystemIcons.Application;
        }

        /// <summary>从 exe 同目录的 islet.ico 里按尺寸取帧。</summary>
        private static Icon LoadSizedFromIcoFile(int px)
        {
            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "islet.ico");
                if (!System.IO.File.Exists(path)) return null;
                return new Icon(path, px, px);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>从自身 exe 的资源里取图标。</summary>
        private static Icon LoadFromOwnExe(int px)
        {
            try
            {
                System.Reflection.Assembly asm =
                    System.Reflection.Assembly.GetEntryAssembly();
                if (asm == null) return null;
                string exe = asm.Location;
                if (string.IsNullOrEmpty(exe)) return null;

                Icon big = Icon.ExtractAssociatedIcon(exe);
                if (big == null) return null;

                Icon sized = new Icon(big, px, px);
                big.Dispose();
                return sized;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>托盘图标的物理像素尺寸（随 DPI 变化）。</summary>
        private static int TrayPixelSize()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    // 16 DIP 在 150% 缩放下就是 24px
                    int px = (int)Math.Round(16 * (g.DpiX / 96.0));
                    if (px < 16) px = 16;
                    if (px > 64) px = 64;
                    return px;
                }
            }
            catch (Exception)
            {
                return 16;
            }
        }

        private static void Raise(EventHandler h)
        {
            if (h != null) h(null, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
        }
    }
}
