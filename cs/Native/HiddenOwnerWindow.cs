using System;
using System.Windows;
using System.Windows.Interop;

namespace RunEverCs.Native
{
    /// <summary>
    /// 进程内隐藏窗口 —— 专作 TrackPopupMenuEx 的 owner。
    ///
    /// ★★ 为什么必须有它：
    ///   TrackPopupMenuEx 要求 owner 窗口属于【调用进程】。
    ///   AHK 版曾直接用 WinExist("A")（别的进程的 hwnd）⇒ 失败，GetLastError=1400
    ///   （ERROR_INVALID_WINDOW_HANDLE）。改用自建隐藏窗口后，菜单正常阻塞等待。
    ///
    /// ★★ 关键纪律：owner 窗口必须【保持可见】（只是移到屏幕外），
    ///   绝不能 Hide()。
    ///   实测（2026-10-03，C# 版首轮）：构造里调了 _w.Hide() ⇒
    ///     · SetForegroundWindow 对不可见窗口无效
    ///     · 菜单收不到 WM_KILLFOCUS ⇒ 点外面不消失，一直挂着
    ///     · 模态循环抢键盘消息 ⇒ 热键与按键明显延迟
    ///     · 进程卡在 TrackPopupMenuEx 不退出（PID 残留）
    ///   对照 AHK 版 EnsureOwnerWindow：只 Show，从不 Hide ✓
    ///
    /// 实现要点：
    ///   · 1×1、屏外（-32000）、不进任务栏、不抢焦点（WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE）
    ///   · ShowActivated=false + WS_EX_NOACTIVATE 双保险：菜单弹出时不夺焦点
    /// </summary>
    public class HiddenOwnerWindow : IDisposable
    {
        private Window _w;
        public IntPtr Handle { get; private set; } = IntPtr.Zero;

        public HiddenOwnerWindow()
        {
            _w = new Window
            {
                Width = 1,
                Height = 1,
                Left = -32000,
                Top = -32000,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = false,
                Title = "RunEverHiddenOwner"
            };

            // ★ 先 Show（必须真正建窗，否则 hwnd 无效），且【绝不 Hide】
            _w.Show();

            var helper = new WindowInteropHelper(_w);
            Handle = helper.Handle;

            // 加上工具窗口 + 不激活扩展样式
            if (Handle != IntPtr.Zero)
            {
                int ex = GetWindowLong(Handle, GWL_EXSTYLE);
                SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            }
        }

        /// <summary>诊断用：owner 窗口当前是否可见（可见才能成为前台窗口）。</summary>
        public bool IsVisible()
        {
            if (Handle == IntPtr.Zero) return false;
            return IsWindowVisible(Handle);
        }

        /// <summary>诊断用：owner 窗口的矩形（应位于屏幕外）。</summary>
        public string Describe()
        {
            if (Handle == IntPtr.Zero) return "(无 hwnd)";
            var sb = new System.Text.StringBuilder();
            sb.Append("hwnd=0x").Append(Handle.ToInt64().ToString("X"));
            sb.Append(" visible=").Append(IsWindowVisible(Handle));
            sb.Append(" pos=(").Append(_w.Left).Append(",").Append(_w.Top).Append(")");
            sb.Append(" size=").Append(_w.Width).Append("x").Append(_w.Height);
            return sb.ToString();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        public void Dispose()
        {
            try
            {
                if (_w != null) _w.Close();
            }
            catch { }
            _w = null;
            Handle = IntPtr.Zero;
        }
    }
}
