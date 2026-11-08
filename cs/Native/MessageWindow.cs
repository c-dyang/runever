using System;
using System.Runtime.InteropServices;

namespace RunEverCs.Native
{
    /// <summary>
    /// 纯 Win32 消息窗口 —— 用于接收窗口消息（如 RegisterHotKey 的 WM_HOTKEY）。
    ///
    /// 与 OwnerWindow 的区别：
    ///   · OwnerWindow：只为 TrackPopupMenuEx 提供一个 owner 句柄，不处理消息
    ///   · MessageWindow：带实例级消息回调，用来接 WM_HOTKEY 等
    ///
    /// 为什么不用 WPF/WinForms 窗口：
    ///   常驻程序要尽量轻；而且 WPF 建窗实测 259ms（见 OwnerWindow 注释）。
    ///   纯 Win32 建窗接近 0 开销。
    /// </summary>
    public class MessageWindow : IDisposable
    {
        public IntPtr Handle { get; private set; } = IntPtr.Zero;

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint SW_SHOWNOACTIVATE = 4;

        private static readonly string ClassName =
            "RunEverMsgWnd_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private static bool _classRegistered = false;
        private static WndProcDelegate _wndProc;

        /// <summary>HWND → 实例 映射（静态 WndProc 需要路由到实例回调）。</summary>
        private static readonly System.Collections.Generic.Dictionary<IntPtr, MessageWindow> Instances
            = new System.Collections.Generic.Dictionary<IntPtr, MessageWindow>();

        private readonly Action<uint, IntPtr, IntPtr> _onMessage;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASSEX
        {
            public uint cbSize;
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
            public IntPtr hIconSm;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int dwExStyle, string lpClassName,
            string lpWindowName, int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, uint nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        /// <param name="onMessage">消息回调；返回 true 表示已处理。</param>
        public MessageWindow(Action<uint, IntPtr, IntPtr> onMessage)
        {
            _onMessage = onMessage;
            EnsureClass();

            Handle = CreateWindowExW(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                ClassName, "RunEverMsg", WS_POPUP,
                -32000, -32000, 1, 1,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException(
                    "CreateWindowExW(MessageWindow) 失败，GetLastError=" + Marshal.GetLastWin32Error());

            lock (Instances) { Instances[Handle] = this; }
            ShowWindow(Handle, SW_SHOWNOACTIVATE);   // 可见但不动焦点
        }

        private static void EnsureClass()
        {
            if (_classRegistered) return;
            lock (typeof(MessageWindow))
            {
                if (_classRegistered) return;

                _wndProc = (hWnd, msg, wParam, lParam) =>
                {
                    MessageWindow w = null;
                    lock (Instances)
                    {
                        Instances.TryGetValue(hWnd, out w);
                    }
                    if (w != null && w._onMessage != null)
                    {
                        try { w._onMessage(msg, wParam, lParam); }
                        catch { /* 回调异常不能打穿消息循环 */ }
                    }
                    return DefWindowProcW(hWnd, msg, wParam, lParam);
                };

                var wc = new WNDCLASSEX();
                wc.cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX));
                wc.style = 0;
                wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc);
                wc.hInstance = GetModuleHandleW(null);
                wc.lpszClassName = ClassName;

                ushort atom = RegisterClassExW(ref wc);
                if (atom == 0)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1410)   // 1410 = 类已注册
                        throw new InvalidOperationException("RegisterClassExW 失败，GetLastError=" + err);
                }
                _classRegistered = true;
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                lock (Instances) { Instances.Remove(Handle); }
                try { DestroyWindow(Handle); } catch { }
                Handle = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        ~MessageWindow() { Dispose(); }
    }
}
