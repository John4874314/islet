using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Islet.Sources
{
    /// <summary>
    /// 原生剪贴板监听器。用 AddClipboardFormatListener + 消息专用窗口，
    /// 比轮询 Clipboard.GetText 便宜得多（PRD P4：禁止轮询）。
    ///
    /// 必须在 STA 线程上创建，自带消息循环。
    /// </summary>
    internal sealed class ClipboardWatcher : IDisposable
    {
        private const uint WM_CLIPBOARDUPDATE = 0x031D;

        private SinkWindow _window;
        private bool _registered;

        /// <summary>剪贴板内容变化。在消息线程上同步触发。</summary>
        public event Action Changed;

        /// <summary>进入消息循环，直到 shouldContinue 返回 false。</summary>
        public void Run(Func<bool> shouldContinue)
        {
            _window = new SinkWindow();
            CreateParams cp = new CreateParams();
            cp.Caption = "IsletClipboardSink";
            cp.Width = 0;
            cp.Height = 0;
            cp.Style = 0;
            cp.ExStyle = 0;
            cp.Parent = new IntPtr(-3); // HWND_MESSAGE：消息专用窗口，不参与渲染
            _window.CreateHandle(cp);
            _window.OnClipboardUpdate = RaiseChanged;

            _registered = AddClipboardFormatListener(_window.Handle);

            // 每 80ms 泵一次消息；循环本身不做任何剪贴板读取
            while (shouldContinue != null && shouldContinue())
            {
                if (!Pump()) break;
                System.Threading.Thread.Sleep(80);
            }

            Detach();
        }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null) h();
        }

        /// <summary>派发待处理消息。返回 false 表示窗口已销毁。</summary>
        private bool Pump()
        {
            if (_window == null || _window.Handle == IntPtr.Zero) return false;

            MSG msg;
            while (PeekMessage(out msg, _window.Handle, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            return true;
        }

        private void Detach()
        {
            if (_registered && _window != null && _window.Handle != IntPtr.Zero)
            {
                RemoveClipboardFormatListener(_window.Handle);
            }
            _registered = false;

            if (_window != null)
            {
                _window.DestroyHandle();
                _window = null;
            }
        }

        public void Dispose()
        {
            Detach();
        }

        /// <summary>只用来承载一个消息回调的最小窗口。</summary>
        private sealed class SinkWindow : NativeWindow
        {
            public Action OnClipboardUpdate;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == (int)WM_CLIPBOARDUPDATE)
                {
                    Action h = OnClipboardUpdate;
                    if (h != null) h();
                }
                base.WndProc(ref m);
            }
        }

        // ---------------------------------------------------------------
        // P/Invoke
        // ---------------------------------------------------------------

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd,
            uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpmsg);

        private const uint PM_REMOVE = 0x0001;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }
    }
}
