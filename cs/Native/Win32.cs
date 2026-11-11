using System;
using System.Runtime.InteropServices;
using System.Text;

namespace RunEverCs.Native
{
    /// <summary>
    /// Win32 API 声明 —— 对应 MenuNative.ahk 里的 DllCall。
    ///
    /// 踩过的坑全部保留为注释（来自 AHK 版实测）：
    ///   ① ExtractIconExW 必须写库名 → 在 .NET 里表现为必须声明 DllImport("shell32.dll")
    ///   ② 菜单位图要 32bpp 预乘 alpha 的 DIB，不能手工合成
    ///   ③ MIIM_BITMAP 单独用，加了 MIIM_FTYPE 会返回 0（ERROR_INVALID_PARAMETER）
    /// </summary>
    internal static class Win32
    {
        // ================= 菜单常量 =================
        public const uint MF_STRING = 0x0000;
        public const uint MF_POPUP = 0x0010;
        public const uint MF_SEPARATOR = 0x0800;
        public const uint MF_GRAYED = 0x0001;
        public const uint MF_DISABLED = 0x0002;
        /// <summary>GetMenuState / GetMenuString 的「按位置」口径（B4a-2 断言禁用占位项用）。</summary>
        public const uint MF_BYPOSITION = 0x00000400;

        public const uint TPM_LEFTALIGN = 0x0000;
        public const uint TPM_RIGHTALIGN = 0x0008;
        public const uint TPM_TOPALIGN = 0x0000;
        public const uint TPM_BOTTOMALIGN = 0x0020;
        public const uint TPM_RETURNCMD = 0x0100;
        public const uint TPM_NONOTIFY = 0x0080;

        public const uint MIIM_BITMAP = 0x0080;
        public const uint MIIM_ID = 0x0002;
        public const uint MIIM_SUBMENU = 0x0004;

        public const int SM_CXSMICON = 49;

        // ================= 菜单 =================
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

        /// <summary>把菜单项插到指定位置（2026-11-11：虚拟项「选中的文件」置顶用）。
        /// uPosition 配 <see cref="MF_BYPOSITION"/> 时是索引，0 = 最前。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool InsertMenuW(IntPtr hMenu, uint uPosition, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetMenuItemCount(IntPtr hMenu);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetMenuItemID(IntPtr hMenu, int nPos);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetSubMenu(IntPtr hMenu, int nPos);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMenuStringW(IntPtr hMenu, uint uIDItem, StringBuilder lpString, int cchMax, uint flags);

        /// <summary>
        /// 取菜单项状态/ID（B4a-2 自检断言「占位项真的灰了」用）。
        /// 失败返回 0xFFFFFFFF；按位置查时 nId 传索引 + <see cref="MF_BYPOSITION"/>。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetMenuState(IntPtr hMenu, uint nId, uint nFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetMenuItemInfoW(IntPtr hMenu, uint uItem, bool fByPosition, ref MENUITEMINFO lpmii);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetMenuItemInfoW(IntPtr hMenu, uint uItem, bool fByPosition, ref MENUITEMINFO lpmii);

        /// <summary>
        /// 带 TPM_RETURNCMD 时返回被点条目 ID；否则返回 BOOL。
        /// ★ 用 int 而非 uint：ID 语义是有符号的（-1 表示子菜单项）。
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);

        // ================= 窗口 =================
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>2026-11-10（全库搜索前置补丁）：置顶/取消置顶走 SetWindowPos。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_SHOWWINDOW = 0x0040;

        /// <summary>置前台的补强：AttachThreadInput 借前台线程输入队列，绕过前台锁定。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        /// <summary>GetWindowThreadProcessId 的第二参可传 IntPtr.Zero 只取线程 ID。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // ================= 图标 =================
        /// <summary>
        /// ★ 必须带库名 —— AHK 里 DllCall("ExtractIconExW") 会报 "nonexistent function"，
        ///   写成 DllCall("shell32\ExtractIconExW") 才行。C# 里由 DllImport 的库名保证。
        /// </summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint ExtractIconExW(string lpszFile, int nIconIndex,
            IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
            int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

        public const uint DI_NORMAL = 0x0003;

        // ================= GDI =================
        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);

        // ================= 结构体 =================
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        /// <summary>
        /// MENUITEMINFO（x64 尺寸 80，字段偏移见注释）。
        /// ★ SetIconById 只用 MIIM_BITMAP + hbmpItem(偏移 72)。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct MENUITEMINFO
        {
            public uint cbSize;         // 0
            public uint fMask;          // 4
            public uint fType;          // 8
            public uint fState;         // 12
            public uint wID;            // 16
            public IntPtr hSubMenu;     // 24
            public IntPtr hbmpChecked;  // 32
            public IntPtr hbmpUnchecked;// 40
            public IntPtr dwItemData;   // 48
            public IntPtr dwTypeData;   // 56
            public uint cch;            // 64
            public IntPtr hbmpItem;     // 72  ★ 关键偏移
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
            public uint bmiColors;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        // ══════════════ 菜单键盘：空格 = 回车（WH_MSGFILTER） ══════════════
        //  Win32 弹出菜单的原生键盘语义里 **空格不激活项**（回车才激活）；用户要求
        //  "选中条目时空格打开" ⇒ 用线程级消息钩子把菜单循环里的 VK_SPACE 改写成 VK_RETURN。
        //  用 WH_MSGFILTER（而不是 WH_KEYBOARD_LL）：它专为"菜单/滚动条/对话框循环"设计，
        //  线程本地、不注入全局、不改变其他程序的按键行为。
        public const int WH_MSGFILTER = -1;
        public const int MSGF_MENU = 2;
        public const uint WM_KEYDOWN = 0x0100;
        public const int VK_SPACE = 0x20;
        public const int VK_RETURN = 0x0D;

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string lpModuleName);

        public const uint SHGFI_ICON = 0x000000100;
        public const uint SHGFI_SMALLICON = 0x000000001;
        /// <summary>按文件属性取图标（路径可以不存在，只要给了属性）—— 取"文件夹"系统图标就用它。</summary>
        public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x000000010;
    }
}
