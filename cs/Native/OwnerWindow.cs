using System;
using System.Runtime.InteropServices;

namespace RunEverCs.Native
{
    /// <summary>
    /// 进程内 owner 窗口 —— 纯 Win32 实现（不用 WPF）。
    ///
    /// ★★ 为什么不用 WPF Window（实测性能数据）：
    ///   WPF 的 Window.Show() 要初始化整个 WPF 栈（DWM 合成、渲染线程、
    ///   依赖属性系统），实测耗时 259ms —— 是"菜单弹出慢"的最大头。
    ///   而 owner 窗口只需要一个有效 HWND，不需要任何渲染能力。
    ///   改用 CreateWindowExW 直接建窗，开销接近 0。
    ///
    /// ★★ 关键纪律（别改）：
    ///   1. owner 必须【保持可见】（只移到屏幕外），绝不能 ShowWindow(SW_HIDE)。
    ///      不可见的窗口无法成为前台窗口 ⇒
    ///        · SetForegroundWindow 无效
    ///        · 菜单收不到 WM_KILLFOCUS ⇒ 点外面不消失、卡住不退、按键延迟
    ///   2. 必须注册窗口类（用自定义类名，避免与系统类冲突）
    ///   3. 窗口消息循环由调用方的消息泵处理（TrackPopupMenuEx 自带模态循环）
    ///
    /// ★ 2026-10-06 变更（用户要求「菜单弹出抢焦点，保证快捷键有效」）：
    ///   本窗口**不再带 `WS_EX_NOACTIVATE`**。带该标志的窗口**不能被激活**，
    ///   即使 `SetForegroundWindow` 调了、`GetForegroundWindow` 也认了，
    ///   窗口自身仍拿不到键盘焦点 ⇒ `TrackPopupMenuEx` 的菜单模态循环收不到按键
    ///   ⇒ 菜单项助记键（`&X`）按了等于按给前台那个应用（"快捷键不生效"的成因之一）。
    ///   现在的分工：**建窗期仍不激活**（仍用 SW_SHOWNOACTIVATE），
    ///   **激活统一交给 `NativeMenu.Show` 的置前台序列**（抢焦点 → 菜单关闭后还焦）。
    /// </summary>
    public class OwnerWindow : IDisposable
    {
        public IntPtr Handle { get; private set; } = IntPtr.Zero;

        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const uint SW_SHOWNOACTIVATE = 4;

        private static readonly string ClassName = "RunEverOwnerWnd_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        private static bool _classRegistered = false;
        private static WndProcDelegate _wndProc;   // 必须持引用，否则被 GC 回收 → 回调崩溃

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

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        public OwnerWindow()
        {
            EnsureClass();

            // 屏幕外 1×1；WS_POPUP 无边框无标题
            // ★ 2026-10-06：去掉 WS_EX_NOACTIVATE（见类注释）—— 否则菜单抢不到键盘焦点。
            Handle = CreateWindowExW(
                WS_EX_TOOLWINDOW,
                ClassName, "RunEverOwner", WS_POPUP,
                -32000, -32000, 1, 1,
                IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowExW 失败，GetLastError=" + Marshal.GetLastWin32Error());

            // ★ 显示但【不激活】—— 绝不 SW_HIDE
            ShowWindow(Handle, SW_SHOWNOACTIVATE);
        }

        private static void EnsureClass()
        {
            if (_classRegistered) return;
            lock (typeof(OwnerWindow))
            {
                if (_classRegistered) return;

                _wndProc = (hWnd, msg, wParam, lParam) => DefWindowProcW(hWnd, msg, wParam, lParam);

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
                    // 1410 = 类已注册（重复注册是安全的）
                    if (err != 1410)
                        throw new InvalidOperationException("RegisterClassExW 失败，GetLastError=" + err);
                }
                _classRegistered = true;
            }
        }

        public bool IsVisible()
        {
            return Handle != IntPtr.Zero && IsWindowVisible(Handle);
        }

        public string Describe()
        {
            if (Handle == IntPtr.Zero) return "(无 hwnd)";
            return "hwnd=0x" + Handle.ToInt64().ToString("X")
                + " visible=" + IsVisible()
                + " pos=(-32000,-32000) size=1x1 [纯Win32]";
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                try { DestroyWindow(Handle); } catch { }
                Handle = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }

        ~OwnerWindow() { Dispose(); }
    }
}
