using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace RunEverCs.Native
{
    /// <summary>
    /// Win32 原生菜单封装 —— 对应 AHK 版 MenuNative.ahk 的 NativeMenu 类。
    ///
    /// 为什么不用 WinForms/WPF 的菜单：
    ///   与 AHK 的 Menu 对象同理，托管菜单把「唯一键」和「显示文本」绑定，
    ///   同名项会冲突。Win32 的 AppendMenuW(hMenu, uFlags, uIDNewItem, lpNewItem)
    ///   把「命令 ID」（整数）与「显示文本」分开 ⇒ 同名项天然共存。
    ///
    /// 踩坑记录（全部从 AHK 版实测继承）：
    ///   ① SetIconById 的 fMask 只能是 MIIM_BITMAP，加 MIIM_FTYPE 返回 0
    ///   ② hbmpItem 在 MENUITEMINFO 的偏移 72（x64）
    ///   ③ 菜单位图必须是 32bpp 预乘 alpha 的 DIB
    ///   ④ TrackPopupMenuEx 的 owner 窗口必须是本进程的（用别的进程 hwnd 会 1400 错误）
    ///   ⑤ IconToDib 用 DrawIconEx 路线（GetDIBits 路线会卡死）
    /// </summary>
    public partial class NativeMenu
    {
        public NativeMenu()
        {
            Handle = Win32.CreatePopupMenu();
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("CreatePopupMenu 失败");
        }

        // ============================================================
        // 添加条目
        // ============================================================

        /// <summary>添加叶条目，返回命令 ID（全局唯一；0 保留给「未选」）。</summary>
        public int AddItem(string text)
        {
            int id = AllocId();
            bool ok = Win32.AppendMenuW(Handle, Win32.MF_STRING, new IntPtr(id), text);
            if (!ok) throw new InvalidOperationException("AppendMenuW 失败: " + text);
            return id;
        }

        /// <summary>添加禁用/灰显条目。</summary>
        public int AddItemGrayed(string text)
        {
            int id = AllocId();
            Win32.AppendMenuW(Handle, Win32.MF_STRING | Win32.MF_GRAYED | Win32.MF_DISABLED,
                new IntPtr(id), text);
            return id;
        }

        /// <summary>
        /// 添加**禁用（灰显）**条目 —— B4a-2「菜单全空」时的占位项用（用户能看见字，但点不动）。
        ///
        /// ★ 只用 `MF_GRAYED`：MS 文档写明 `MF_GRAYED` 本身就"禁用 + 变灰"，
        ///   且 `MF_DISABLED / MF_ENABLED / MF_GRAYED` 是**互斥组**，不能叠加
        ///   （https://learn.microsoft.com/zh-cn/windows/desktop/api/winuser/nf-winuser-appendmenuw）。
        /// 与 `AddItem` 一样返回命令 ID（进不了 idmap ⇒ 万一点到也不会误触发任何条目）。
        /// </summary>
        public int AddItemDisabled(string text)
        {
            int id = AllocId();
            bool ok = Win32.AppendMenuW(Handle, Win32.MF_STRING | Win32.MF_GRAYED,
                new IntPtr(id), text);
            if (!ok) throw new InvalidOperationException("AppendMenuW(GRAYED) 失败: " + text);
            return id;
        }

        public void AddSeparator()
        {
            Win32.AppendMenuW(Handle, Win32.MF_SEPARATOR, IntPtr.Zero, null);
        }

        /// <summary>
        /// 添加子菜单（返回后 sub 的生命周期由父菜单持有）。
        /// ★ 返回值 = 该子菜单项在父菜单里的**位置索引**（2026-11-08）——
        ///   popup 项拿不到命令 ID（uIDNewItem 被 sub.Handle 占着），要给它设图标
        ///   只能按位置 `SetMenuItemInfo(fByPosition=true)`，所以这里把索引交出去。
        /// </summary>
        public int AddSubMenu(string text, NativeMenu sub)
        {
            _subMenus.Add(sub);     // 持引用，防 GC
            bool ok = Win32.AppendMenuW(Handle, Win32.MF_POPUP, sub.Handle, text);
            if (!ok) throw new InvalidOperationException("AppendMenuW(POPUP) 失败: " + text);
            return Win32.GetMenuItemCount(Handle) - 1;
        }

        // ============================================================
        // 图标
        // ============================================================

        /// <summary>
        /// 给指定命令 ID 设置图标。
        /// ★ fMask 只写 MIIM_BITMAP（实测加 MIIM_FTYPE 会返回 0 + ERROR_INVALID_PARAMETER=87）。
        /// </summary>
        public bool SetIconById(int cmdId, string source)
        {
            IntPtr hbm = GetIconBitmap(source);
            if (hbm == IntPtr.Zero) return false;

            var mii = new Win32.MENUITEMINFO();
            mii.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.MENUITEMINFO));
            mii.fMask = Win32.MIIM_BITMAP;
            mii.hbmpItem = hbm;

            return Win32.SetMenuItemInfoW(Handle, (uint)cmdId, false, ref mii);
        }

        /// <summary>
        /// 给**子菜单项**（popup）设置图标：popup 项没有可用命令 ID，只能按位置设。
        /// 与 SetIconById 同一套位图管线（MIIM_BITMAP + 缓存）。
        /// </summary>
        public bool SetIconByPosition(int index, string source)
        {
            if (index < 0) return false;
            IntPtr hbm = GetIconBitmap(source);
            if (hbm == IntPtr.Zero) return false;

            var mii = new Win32.MENUITEMINFO();
            mii.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.MENUITEMINFO));
            mii.fMask = Win32.MIIM_BITMAP;
            mii.hbmpItem = hbm;

            return Win32.SetMenuItemInfoW(Handle, (uint)index, true, ref mii);
        }

        private static IntPtr _folderBitmap = IntPtr.Zero;

        /// <summary>
        /// **系统文件夹图标**（2026-11-08）—— 子菜单（`标题 | group`）的默认图标。
        /// 用 `SHGetFileInfo(SHGFI_ICON|SHGFI_SMALLICON|SHGFI_USEFILEATTRIBUTES, FILE_ATTRIBUTE_DIRECTORY)`
        /// 拿系统认定的文件夹图标，再走 IconToDib 转菜单认的 32bpp DIB；拿不到就用包内兜底图标。
        /// ★ 2026-11-10 三次踩坑（用户实机截图暴露）：
        ///   · 传 "C:\\" ⇒ SHGetFileInfo 按**驱动器**返回**硬盘图标**；
        ///   · 传空串 ⇒ SHGetFileInfo **失败** ⇒ 落回 MakeFallbackIcon（放大镜兜底）；
        ///   ⇒ 必须传一个**非空目录路径**（USEFILEATTRIBUTES 不会访问磁盘，路径只需非空）；
        ///   路径不硬编码 —— 用 <see cref="Environment.GetFolderPath(Environment.SpecialFolder.Windows)"/>
        ///   取系统目录（%SystemRoot%，盘符变化/精简系统都稳），取不到再退 SystemRoot 环境变量。
        /// </summary>
        public static IntPtr GetFolderIconBitmap()
        {
            if (_folderBitmap != IntPtr.Zero) return _folderBitmap;
            try
            {
                string folderProbe = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (string.IsNullOrEmpty(folderProbe))
                    folderProbe = Environment.GetEnvironmentVariable("SystemRoot");
                if (string.IsNullOrEmpty(folderProbe))
                    folderProbe = "C:\\Windows";   // 最后兜底（只为非空，USEFILEATTRIBUTES 不访问磁盘）

                var sfi = new Win32.SHFILEINFO();
                IntPtr r = Win32.SHGetFileInfoW(folderProbe, Win32.FILE_ATTRIBUTE_DIRECTORY, ref sfi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.SHFILEINFO)),
                    Win32.SHGFI_ICON | Win32.SHGFI_SMALLICON | Win32.SHGFI_USEFILEATTRIBUTES);
                if (r != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
                {
                    _folderBitmap = IconToDib(sfi.hIcon);
                    Win32.DestroyIcon(sfi.hIcon);
                }
            }
            catch { }
            if (_folderBitmap == IntPtr.Zero) _folderBitmap = MakeFallbackIcon();
            return _folderBitmap;
        }

        /// <summary>给子菜单项换成**系统文件夹图标**（不看图标源；子菜单没有动作，本就没有图标源）。</summary>
        public bool SetFolderIconByPosition(int index)
        {
            if (index < 0) return false;
            IntPtr hbm = GetFolderIconBitmap();
            if (hbm == IntPtr.Zero) return false;

            var mii = new Win32.MENUITEMINFO();
            mii.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.MENUITEMINFO));
            mii.fMask = Win32.MIIM_BITMAP;
            mii.hbmpItem = hbm;
            return Win32.SetMenuItemInfoW(Handle, (uint)index, true, ref mii);
        }

        /// <summary>
        /// 给指定**命令 ID** 的叶条目换成系统文件夹图标（2026-11-10）——
        /// 文件夹条目（open 指向目录 / open sub 退化）本身有命令 ID，不走 popup 位置。
        /// 与 <see cref="SetFolderIconByPosition"/> 同一套图标（SHGetFileInfo 系统文件夹图标）。
        /// </summary>
        public bool SetFolderIconById(int cmdId)
        {
            if (cmdId <= 0) return false;
            IntPtr hbm = GetFolderIconBitmap();
            if (hbm == IntPtr.Zero) return false;

            var mii = new Win32.MENUITEMINFO();
            mii.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.MENUITEMINFO));
            mii.fMask = Win32.MIIM_BITMAP;
            mii.hbmpItem = hbm;
            return Win32.SetMenuItemInfoW(Handle, (uint)cmdId, false, ref mii);
        }

        private static IntPtr _fileBitmap = IntPtr.Zero;

        /// <summary>
        /// **系统通用文件图标**（2026-11-09 / U-B）—— 目录子菜单里的**文件**子项用。
        /// 与 <see cref="GetFolderIconBitmap"/> 同一条路：`SHGetFileInfo` + `SHGFI_USEFILEATTRIBUTES`
        /// ⇒ 纯内存取图标、不落盘抽取，进程内只做一次（位图缓存）。
        /// </summary>
        public static IntPtr GetFileIconBitmap()
        {
            if (_fileBitmap != IntPtr.Zero) return _fileBitmap;
            try
            {
                const uint FILE_ATTRIBUTE_NORMAL = 0x80;   // Win32.cs 没收录这个常量，就地声明
                var sfi = new Win32.SHFILEINFO();
                IntPtr r = Win32.SHGetFileInfoW("file", FILE_ATTRIBUTE_NORMAL, ref sfi,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.SHFILEINFO)),
                    Win32.SHGFI_ICON | Win32.SHGFI_SMALLICON | Win32.SHGFI_USEFILEATTRIBUTES);
                if (r != IntPtr.Zero && sfi.hIcon != IntPtr.Zero)
                {
                    _fileBitmap = IconToDib(sfi.hIcon);
                    Win32.DestroyIcon(sfi.hIcon);
                }
            }
            catch { }
            if (_fileBitmap == IntPtr.Zero) _fileBitmap = MakeFallbackIcon();
            return _fileBitmap;
        }

        /// <summary>给条目换成**系统通用文件图标**（按位置；目录子菜单里的文件子项用）。</summary>
        public bool SetFileIconByPosition(int index)
        {
            if (index < 0) return false;
            IntPtr hbm = GetFileIconBitmap();
            if (hbm == IntPtr.Zero) return false;

            var mii = new Win32.MENUITEMINFO();
            mii.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.MENUITEMINFO));
            mii.fMask = Win32.MIIM_BITMAP;
            mii.hbmpItem = hbm;
            return Win32.SetMenuItemInfoW(Handle, (uint)index, true, ref mii);
        }

        /// <summary>取图标位图（带缓存）。空源 = 明确要兜底图标。</summary>
        public static IntPtr GetIconBitmap(string src)
        {
            if (string.IsNullOrEmpty(src) || src.Trim().Length == 0)
                return MakeFallbackIcon();

            string key = src.ToLowerInvariant();
            IntPtr cached;
            if (IconCache.TryGetValue(key, out cached))
                return cached;

            IntPtr hbm = ExtractHBitmap(src);
            if (hbm == IntPtr.Zero)
                hbm = MakeFallbackIcon();

            if (hbm != IntPtr.Zero)
                IconCache[key] = hbm;
            return hbm;
        }

        /// <summary>从文件抽图标 → DIB。支持 "文件,索引" 形式。</summary>
        private static IntPtr ExtractHBitmap(string src)
        {
            string file = src;
            int idx = 0;

            var m = System.Text.RegularExpressions.Regex.Match(src, @"^(.+?),\s*(-?\d+)$");
            if (m.Success)
            {
                file = m.Groups[1].Value.Trim(' ', '\t', '"');
                int.TryParse(m.Groups[2].Value, out idx);
            }

            var hLarge = new IntPtr[1];
            var hSmall = new IntPtr[1];
            IntPtr n = IntPtr.Zero;
            try
            {
                n = new IntPtr(unchecked((int)Win32.ExtractIconExW(file, idx, hLarge, hSmall, 1)));
            }
            catch
            {
                n = IntPtr.Zero;
            }

            IntPtr hIcon = IntPtr.Zero;
            if (n != IntPtr.Zero && hSmall[0] != IntPtr.Zero) hIcon = hSmall[0];
            else if (n != IntPtr.Zero && hLarge[0] != IntPtr.Zero) hIcon = hLarge[0];

            // ★ 用不上的句柄必须销毁，否则泄漏 HICON（AHK 版实测 GDI 句柄增长）
            if (hIcon != hLarge[0] && hLarge[0] != IntPtr.Zero) Win32.DestroyIcon(hLarge[0]);
            if (hIcon != hSmall[0] && hSmall[0] != IntPtr.Zero) Win32.DestroyIcon(hSmall[0]);

            // 主源失败 → SHGetFileInfo 兜底拿系统图标
            if (hIcon == IntPtr.Zero)
            {
                var sfi = new Win32.SHFILEINFO();
                IntPtr r = Win32.SHGetFileInfoW(file, 0, ref sfi, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.SHFILEINFO)),
                    Win32.SHGFI_ICON | Win32.SHGFI_SMALLICON);
                if (r != IntPtr.Zero) hIcon = sfi.hIcon;
            }

            if (hIcon == IntPtr.Zero) return IntPtr.Zero;

            IntPtr hbm = IconToDib(hIcon);
            Win32.DestroyIcon(hIcon);
            return hbm;
        }

        /// <summary>
        /// HICON → 32bpp DIB（菜单唯一认的形态）。
        ///
        /// ★ 路线选择（AHK 版实测定案，别换）：
        ///   ❌ GetDIBits 像素级合成：累积到第 3~4 个图标时必卡死
        ///   ✅ DrawIconEx 铺图：4 个图标全部 0ms 完成
        ///   原理：建 32bpp DIB → SelectObject 进内存 DC → DrawIconEx(DI_NORMAL)
        ///         图标自带的遮罩与 alpha 由系统处理。
        /// </summary>
        private static IntPtr IconToDib(IntPtr hIcon)
        {
            int w = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
            if (w <= 0 || w > 256) w = 16;
            int h = w;

            IntPtr hdcScreen = Win32.GetDC(IntPtr.Zero);
            IntPtr hDib = IntPtr.Zero;
            IntPtr hdcMem = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;

            try
            {
                var bmi = new Win32.BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER));
                bmi.bmiHeader.biWidth = w;
                bmi.bmiHeader.biHeight = -h;    // ★ 负高度 = 自上而下
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;  // ★ 32bpp 预乘 alpha
                bmi.bmiHeader.biCompression = 0; // BI_RGB

                IntPtr bits;
                hDib = Win32.CreateDIBSection(hdcScreen, ref bmi, 0, out bits, IntPtr.Zero, 0);
                if (hDib == IntPtr.Zero) return IntPtr.Zero;

                hdcMem = Win32.CreateCompatibleDC(hdcScreen);
                hOld = Win32.SelectObject(hdcMem, hDib);
                Win32.DrawIconEx(hdcMem, 0, 0, hIcon, w, h, 0, IntPtr.Zero, Win32.DI_NORMAL);
                Win32.SelectObject(hdcMem, hOld);
                hOld = IntPtr.Zero;

                return hDib;
            }
            finally
            {
                if (hOld != IntPtr.Zero && hdcMem != IntPtr.Zero) Win32.SelectObject(hdcMem, hOld);
                if (hdcMem != IntPtr.Zero) Win32.DeleteDC(hdcMem);
                if (hdcScreen != IntPtr.Zero) Win32.ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        /// <summary>
        /// 兜底图标（无程序图标可解析时用）。
        /// ★ 2026-10-05：优先用**随包图标** `assets\runever.ico`（用户提供），
        ///   取不到才退回 shell32.dll 资源 2 —— exe 被单独拷走或 ico 缺失时不至于没图标。
        /// </summary>
        private static IntPtr MakeFallbackIcon()
        {
            if (_fallbackBitmap != IntPtr.Zero) return _fallbackBitmap;

            IntPtr packaged = IntPtr.Zero;

            // ① 随包图标（ExtractIconExW 对 .ico 文件同样有效 ⇒ 复用既有抽取路径）
            try
            {
                string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "runever.ico");
                if (File.Exists(ico)) packaged = ExtractHBitmap(ico);
            }
            catch { packaged = IntPtr.Zero; }

            if (packaged != IntPtr.Zero)
            {
                _fallbackBitmap = packaged;
                return packaged;
            }

            // ② 兜底之兜底：shell32.dll 资源 2
            var hLarge = new IntPtr[1];
            var hSmall = new IntPtr[1];
            IntPtr n = IntPtr.Zero;
            try
            {
                n = new IntPtr(unchecked((int)Win32.ExtractIconExW("shell32.dll", 2, hLarge, hSmall, 1)));
            }
            catch { n = IntPtr.Zero; }

            IntPtr hIcon = IntPtr.Zero;
            if (n != IntPtr.Zero && hSmall[0] != IntPtr.Zero) hIcon = hSmall[0];
            else if (n != IntPtr.Zero && hLarge[0] != IntPtr.Zero) hIcon = hLarge[0];

            if (hIcon != hLarge[0] && hLarge[0] != IntPtr.Zero) Win32.DestroyIcon(hLarge[0]);
            if (hIcon != hSmall[0] && hSmall[0] != IntPtr.Zero) Win32.DestroyIcon(hSmall[0]);

            if (hIcon == IntPtr.Zero) return IntPtr.Zero;

            IntPtr hbm = IconToDib(hIcon);
            Win32.DestroyIcon(hIcon);
            _fallbackBitmap = hbm;
            return hbm;
        }

        // ============================================================
        // 显示（阻塞直到用户选择或取消）
        // ============================================================

        /// <summary>
        /// 弹出菜单，返回被点条目 ID（0 = 未选/取消）。
        ///
        /// ★★ owner 窗口必须是【本进程】创建的窗口。
        ///    AHK 版曾用 WinExist("A")（别的进程的 hwnd）⇒ TrackPopupMenuEx
        ///    失败 GetLastError=1400（ERROR_INVALID_WINDOW_HANDLE）。
        ///    解法：自建一个进程内隐藏窗口当 owner（见 HiddenOwnerWindow）。
        ///
        /// ★★ 前置条件是 owner 必须是【可见】且能成为前台窗口。
        ///    实测（2026-10-03 C# 版首轮）owner 被 Hide() 后：
        ///      SetForegroundWindow 无效 ⇒ 菜单点外面不消失、卡住不退、按键延迟。
        ///
        /// ★★ 2026-10-06（用户要求「菜单弹出时抢焦点，保证快捷键有效」）：
        ///    ① 弹出前**抢焦点**：置前台序列（AttachThreadInput → SetForegroundWindow →
        ///       分离）作用在 owner 上。⚠️ 前提是 owner **不带 WS_EX_NOACTIVATE**
        ///       （见 OwnerWindow 类注释）—— 带它就只是"自称前台"、拿不到键盘。
        ///    ② 菜单关闭后**还焦**：把前台还给弹出前的窗口。顺序关键 —— 必须在
        ///       `TrackPopupMenuEx` 返回后、调用方 `Dispatch` **之前**，
        ///       否则选中「显示窗口」类条目时会把刚上前的窗口压下去。
        ///    诊断信息落 <see cref="FocusDiag"/>，由调用方写进日志（实机取证用）。
        /// </summary>
        public int Show(IntPtr ownerHwnd, int x, int y, bool rightAlign = false, bool bottomAlign = false)
        {
            // ★ 卡 D（2026-11-17b）：flags 收进纯函数 BuildPopupFlags —— 自检可离线断言
            //   「TPM_RECURSE 已置入、TPM_RETURNCMD | TPM_NONOTIFY 与对齐位一位没改」。
            uint flags = BuildPopupFlags(rightAlign, bottomAlign);

            IntPtr prevFg = IntPtr.Zero;
            string diag;

            if (ownerHwnd != IntPtr.Zero && Win32.IsWindow(ownerHwnd))
            {
                // ★ 置前台的可靠序列（单靠 SetForegroundWindow 常被系统拒绝）：
                //   ① AttachThreadInput：借前台线程的输入队列，绕过前台锁定
                //   ② SetForegroundWindow
                //   ③ 分离输入队列
                //   AHK 版只调了 SetForegroundWindow（它没有跨进程 hwnd 的困扰，
                //   因为 owner 始终可见），C# 里补强这一步更稳。
                prevFg = Win32.GetForegroundWindow();
                bool got = ForceForeground(ownerHwnd);
                diag = "owner=0x" + ownerHwnd.ToInt64().ToString("X")
                    + " prevFg=0x" + prevFg.ToInt64().ToString("X")
                    + " 抢焦点=" + (got ? "成功" : "失败");
            }
            else
            {
                diag = "⚠ owner 无效（未抢焦点，快捷键会失效）";
            }

            // ★ 空格 = 回车：菜单循环里把 VK_SPACE 改写成 VK_RETURN（线程级 WH_MSGFILTER，
            //   只在本次弹出期间挂着，退出即摘，不影响任何其他程序）
            //
            // ★ 卡 D：WM_MENURBUTTONUP 是 **sent** 消息（不进队列）⇒ WH_MSGFILTER 抓不到，
            //   只能挂在 owner 的窗口过程上。这里在弹出期间**临时子类化** owner（弹出后原样还回），
            //   于是 owner 是 OwnerWindow / HiddenOwnerWindow 都同样生效，且不改它们任何一行。
            int ret;
            IntPtr hook = IntPtr.Zero;
            // ★ 没挂上下文菜单（MenuButtonUp == null）就完全不子类化 ⇒ 现役路径一个系统调用都不多
            bool subclassed = MenuButtonUp != null && InstallSubclass(ownerHwnd);
            // ★ 菜单显示期间让鼠标钩子放行右键（否则右键事件被吞，WM_MENURBUTTONUP 不来）
            System.Threading.Interlocked.Increment(ref PopupDepth);
            ActiveOwner = ownerHwnd;
            ActiveHandler = MenuButtonUp;
            try
            {
                // ⚠ hMod 必须给模块句柄（托管回调 + IntPtr.Zero 在部分环境直接失败，
                //   失败会静默导致"空格没反应"）；失败显式进日志，不吞。
                hook = Win32.SetWindowsHookExW(Win32.WH_MSGFILTER, _spaceAsEnter,
                    Win32.GetModuleHandleW(null), Win32.GetCurrentThreadId());
                if (hook == IntPtr.Zero)
                {
                    var klog = KeyLog;
                    if (klog != null) { try { klog("⚠ 空格钩子安装失败 err=" + Marshal.GetLastWin32Error()); } catch { } }
                }
                ret = Win32.TrackPopupMenuEx(Handle, flags, x, y, ownerHwnd, IntPtr.Zero);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref PopupDepth);
                ActiveHandler = null;
                ActiveOwner = IntPtr.Zero;
                if (hook != IntPtr.Zero) { try { Win32.UnhookWindowsHookEx(hook); } catch { } }
                if (subclassed) RemoveSubclass();
            }

            // ★ 还焦：菜单关闭后把前台还给弹出前的窗口（选中/取消/点外面三条路径都会走到这里）
            bool restored = false;
            if (prevFg != IntPtr.Zero && prevFg != ownerHwnd && Win32.IsWindow(prevFg))
                restored = ForceForeground(prevFg);

            FocusDiag = diag + " | 还焦="
                + (restored ? "成功" : (prevFg == IntPtr.Zero ? "无前窗口" : "失败/跳过"))
                + " 当前前台=0x" + Win32.GetForegroundWindow().ToInt64().ToString("X");

            // ★ 卡 D：菜单**已经关掉**（TrackPopupMenuEx 已返回、窗口过程已还原）才跑延后动作 ——
            //   输入框 / 写盘都绝不在菜单消息泵里同步做（那会卡死菜单）。
            var closed = PopupClosed;
            if (closed != null) { try { closed(); } catch { } }

            return ret;
        }

        // ★ 静态字段持有委托，防 GC 回收（钩子回调被回收 = 进程直接崩）
        private static readonly Win32.HookProc _spaceAsEnter = SpaceAsEnterProc;

        /// <summary>按键改写时的日志出口（RunEverApp 注入主日志；自检里也用它取证）。</summary>
        public static Action<string> KeyLog;

        /// <summary>
        /// WH_MSGFILTER 回调：菜单循环里把「空格」改写成「回车」。
        /// 只在 nCode == MSGF_MENU（菜单消息）时动手；其他一律原样放行。
        /// 失败一律吞掉 —— 钩子绝不能把菜单弄崩。
        /// </summary>
        private static IntPtr SpaceAsEnterProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (lParam != IntPtr.Zero)
                {
                    var msg = (Win32.MSG)Marshal.PtrToStructure(lParam, typeof(Win32.MSG));
                    if (TryConvertSpace(nCode, wParam.ToInt32(), ref msg))
                    {
                        Marshal.StructureToPtr(msg, lParam, false);
                        var cb = KeyLog;
                        if (cb != null) { try { cb("键: 空格 → 回车（激活当前项）"); } catch { } }
                    }
                }
            }
            catch { }
            return Win32.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        /// <summary>
        /// 菜单循环里把「空格」改写成「回车」的**纯判定核心**（钩子回调与自检共用）。
        /// 只在「菜单消息 + 按键按下 + 空格」时改写，其余一律原样返回 false。
        /// </summary>
        internal static bool TryConvertSpace(int nCode, int keyMsg, ref Win32.MSG msg)
        {
            if (nCode != Win32.MSGF_MENU) return false;
            if (keyMsg != (int)Win32.WM_KEYDOWN) return false;
            if (msg.wParam.ToInt32() != Win32.VK_SPACE) return false;
            msg.wParam = (IntPtr)Win32.VK_RETURN;
            return true;
        }

        /// <summary>当前前台窗口句柄（internal：自检只读观测用）。</summary>
        internal static IntPtr CurrentForeground()
        {
            return Win32.GetForegroundWindow();
        }

        /// <summary>句柄是否仍有效（internal：自检用）。</summary>
        internal static bool IsWindowAlive(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && Win32.IsWindow(hwnd);
        }

        /// <summary>
        /// 把前台/键盘焦点强制交给 <paramref name="hwnd"/>。
        /// 用 AttachThreadInput 借当前前台线程的输入队列绕过前台锁定；
        /// 返回 true = 调完后 `GetForegroundWindow()` 确实是它。
        /// ★ internal：`--selftest-focus` 直接测这一条（产品路径与自检同一份实现）。
        /// </summary>
        internal static bool ForceForeground(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;

            IntPtr fg = Win32.GetForegroundWindow();
            uint fgThread = Win32.GetWindowThreadProcessId(fg, IntPtr.Zero);
            uint myThread = Win32.GetCurrentThreadId();

            bool attached = false;
            if (fgThread != 0 && fgThread != myThread)
                attached = Win32.AttachThreadInput(myThread, fgThread, true);

            try
            {
                Win32.SetForegroundWindow(hwnd);
                Win32.BringWindowToTop(hwnd);
            }
            finally
            {
                if (attached)
                    Win32.AttachThreadInput(myThread, fgThread, false);
            }

            return Win32.GetForegroundWindow() == hwnd;
        }

        /// <summary>用当前光标位置弹出。</summary>
        public int ShowAtCursor(IntPtr ownerHwnd)
        {
            Win32.POINT pt;
            Win32.GetCursorPos(out pt);
            return Show(ownerHwnd, pt.X, pt.Y);
        }

        // ============================================================
        // ★ 卡 D（2026-11-17b）：菜单项右键上下文菜单 —— 原生侧支撑
        //   官方机制（已核实，别自己发明）：
        //     · WM_MENURBUTTONUP (0x0122)：用户在**某个菜单项上松开右键**时发给菜单 **owner**；
        //       wParam = 该项的零基索引，lParam = hMenu。它是 **sent** 消息（不进消息队列）
        //       ⇒ 既有 WH_MSGFILTER（空格改回车）抓不到，必须挂在 owner 的窗口过程上。
        //     · 文档 Remarks：显示该项的上下文菜单要调 TrackPopupMenuEx **并带 TPM_RECURSE**。
        //     · TPM_RECURSE (0x0001)：允许"另一个菜单已显示时"再弹一个菜单 ——
        //       主菜单弹出时必须带上它，否则二级菜单弹不出来/异常。
        //
        //   本层只做「原生事实」（标志位 / 消息 / 索引→命令 ID / 裸弹菜单 / 关菜单），
        //   **策略**（该弹什么、改哪个字段、怎么写盘）在 Services.MenuItemContextMenu ——
        //   Native 不反向依赖 Services（分层方向见 --selftest-arch）。
        // ============================================================

        /// <summary>用户在菜单项上松开右键（发给菜单 owner 窗口；sent 消息，不进队列）。</summary>
        public const uint WM_MENURBUTTONUP = 0x0122;

        /// <summary>允许"另一个菜单已显示时"再弹一个菜单（菜单里的上下文菜单必须带它）。</summary>
        public const uint TPM_RECURSE = 0x0001;

        private const uint MIIM_FTYPE = 0x00000100;
        private const uint MFT_SEPARATOR = 0x0800;

        /// <summary>
        /// 一级（主）菜单的弹出 flags —— **纯函数**（自检可离线断言，不用弹菜单）。
        /// ★ 既有语义逐位不变（TPM_RETURNCMD | TPM_NONOTIFY + 对齐位），
        ///   只多置了 <see cref="TPM_RECURSE"/>（右键二级菜单的前提）。
        /// </summary>
        public static uint BuildPopupFlags(bool rightAlign, bool bottomAlign)
        {
            return Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY | TPM_RECURSE
                | (rightAlign ? Win32.TPM_RIGHTALIGN : Win32.TPM_LEFTALIGN)
                | (bottomAlign ? Win32.TPM_BOTTOMALIGN : Win32.TPM_TOPALIGN);
        }

        /// <summary>二级（右键）菜单的 flags：外层菜单还开着 ⇒ 同样必须 TPM_RECURSE。</summary>
        public const uint ContextPopupFlags = TPM_RECURSE | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY
            | Win32.TPM_LEFTALIGN | Win32.TPM_TOPALIGN;

        /// <summary>
        /// owner 窗口收到 <see cref="WM_MENURBUTTONUP"/> 时的回调：(owner, hMenu, index)。
        /// null ⇒ 消息原样放行（零回归：没有挂上下文菜单时行为与改造前完全一致）。
        /// ★ 处理器内部必须自己吞异常 —— 见 <see cref="SubclassProcImpl"/>。
        /// </summary>
        public Action<IntPtr, IntPtr, int> MenuButtonUp;

        /// <summary>
        /// 我们自己的弹出菜单嵌套深度（0 = 没有菜单在显示）。
        ///
        /// ★ 2026-11-17 真机事故换来的：`MouseTrigger` 的低级鼠标钩子会**吞掉所有
        ///   RBUTTONDOWN**（长按触发的前提），于是菜单显示期间用户点右键时，菜单收不到
        ///   配对事件 ⇒ Windows 不发 `WM_MENURBUTTONUP` ⇒ 右键上下文菜单永远不弹。
        ///   钩子据此标志在菜单显示期间把右键原样放行。
        /// </summary>
        public static int PopupDepth;

        /// <summary>当前是否有我们自己的弹出菜单正在显示。</summary>
        /// <summary>当前这次弹出的右键处理器（Show 期间有效；钩子路径据此回调）。</summary>
        public static Action<IntPtr, IntPtr, int> ActiveHandler;

        /// <summary>当前这次弹出的 owner 窗口（右键二级菜单的 TrackPopupMenuEx owner）。</summary>
        public static IntPtr ActiveOwner;

        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Win32.POINT pt);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref Win32.POINT pt);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int MenuItemFromPoint(IntPtr hWnd, IntPtr hMenu, Win32.POINT pt);

        /// <summary>菜单弹窗窗口类名（系统内置）。</summary>
        private const string PopupMenuClass = "#32768";

        /// <summary>
        /// ★ 2026-11-17 兜底路径：**不依赖 WM_MENURBUTTONUP**。
        /// 实测那条消息在本机（Win32 TrackPopupMenuEx + 纯 Win32 owner + 低级鼠标钩子）根本不来；
        /// 改由鼠标钩子把"菜单弹窗期间的右键抬起"转成坐标投递到主线程，这里自己算命中项。
        /// 返回 true = 已交给右键处理器。
        /// </summary>
        public static bool HandlePopupRightClick(int screenX, int screenY)
        {
            try
            {
                var pt = new Win32.POINT { X = screenX, Y = screenY };
                IntPtr wnd = WindowFromPoint(pt);
                if (wnd == IntPtr.Zero) return false;
                var sb = new StringBuilder(32);
                if (Win32.GetClassNameW(wnd, sb, 32) <= 0) return false;
                if (sb.ToString() != PopupMenuClass) return false;

                IntPtr hMenu = SendMessageW(wnd, 0x01E1 /*MN_GETHMENU*/, IntPtr.Zero, IntPtr.Zero);
                if (hMenu == IntPtr.Zero) return false;

                int idx = MenuItemFromPoint(IntPtr.Zero, hMenu, pt);
                if (idx < 0)
                {
                    var cli = pt;
                    if (ScreenToClient(wnd, ref cli)) idx = MenuItemFromPoint(IntPtr.Zero, hMenu, cli);
                }
                if (idx < 0) return false;

                var h = ActiveHandler;
                if (h == null) return false;
                h(ActiveOwner, hMenu, idx);
                return true;
            }
            catch { return false; }
        }

        public static bool IsPopupActive
        {
            get { return System.Threading.Volatile.Read(ref PopupDepth) > 0; }
        }

        /// <summary>
        /// 主菜单**已关闭之后**（TrackPopupMenuEx 已返回、窗口过程已还原）的回调。
        /// 延后动作（输入框 / 写盘 / 重建菜单）只能挂这里 —— 绝不进菜单消息泵。
        /// </summary>
        public Action PopupClosed;

        /// <summary>右键链路的日志出口（Services 注入；为空则不写）。</summary>
        public Action<string> ContextLog;

        private IntPtr _subclassHwnd = IntPtr.Zero;
        private int _diagBtnUp;      // 诊断：WM_MENURBUTTONUP 到达次数

        private IntPtr _oldWndProc = IntPtr.Zero;
        private WndProcDelegate _subclassProc;      // ★ 必须持引用：被 GC 回收 = 进程直接崩

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private const int GWLP_WNDPROC = -4;

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd,
            uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EndMenu();

        /// <summary>SetWindowLongPtr 的 32/64 位统一入口（x86 上 user32 只导出 SetWindowLongW）。</summary>
        private static IntPtr SetWndProc(IntPtr hWnd, IntPtr newProc)
        {
            if (IntPtr.Size == 8) return SetWindowLongPtrW(hWnd, GWLP_WNDPROC, newProc);
            return new IntPtr(SetWindowLongW(hWnd, GWLP_WNDPROC, newProc.ToInt32()));
        }

        /// <summary>
        /// 弹出期间临时子类化 owner。失败 ⇒ 返回 false（右键菜单弹不出来，主菜单照常）。
        /// </summary>
        private bool InstallSubclass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
            try
            {
                _subclassProc = SubclassProcImpl;
                IntPtr fn = Marshal.GetFunctionPointerForDelegate(_subclassProc);
                IntPtr old = SetWndProc(hwnd, fn);
                if (old == IntPtr.Zero)
                {
                    _subclassProc = null;
                    Log("⚠ 右键：owner 窗口过程替换失败 err=" + Marshal.GetLastWin32Error()
                        + "（主菜单不受影响，右键菜单不可用）");
                    return false;
                }
                _oldWndProc = old;
                _subclassHwnd = hwnd;
                Log("右键：owner 子类化成功 hwnd=0x" + hwnd.ToInt64().ToString("X")
                    + "（PopupDepth=" + System.Threading.Volatile.Read(ref PopupDepth) + "）");
                return true;
            }
            catch (Exception ex)
            {
                _subclassProc = null;
                Log("⚠ 右键：子类化 owner 异常（主菜单不受影响）: " + ex.Message);
                return false;
            }
        }

        private void RemoveSubclass()
        {
            try
            {
                if (_subclassHwnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
                    SetWndProc(_subclassHwnd, _oldWndProc);
            }
            catch { }
            _subclassHwnd = IntPtr.Zero;
            _oldWndProc = IntPtr.Zero;
            _subclassProc = null;
        }

        /// <summary>
        /// 子类化的窗口过程：只截 <see cref="WM_MENURBUTTONUP"/>，其余一律照原过程走。
        /// ★ 任何异常都在这里吞掉 —— 右键菜单失败绝不许把主菜单打掉。
        /// </summary>
        private IntPtr SubclassProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // ★ 2026-11-17 实测结论：本机 WM_MENURBUTTONUP **从不发出**（诊断 0 命中；
            //   该项为 MF_POPUP 时系统本就不发，见 SO 76769328），所以实际生效的是
            //   鼠标钩子那条兜底路径（NativeMenu.HandlePopupRightClick）。
            //   这里只保留"万一它来了"的计数，不再逐条刷日志。
            if (msg == WM_MENURBUTTONUP)
            {
                if (++_diagBtnUp <= 3)
                    Log("右键：收到 WM_MENURBUTTONUP index=" + wParam.ToInt64() + "（系统这条消息一般不发）");
            }

            if (msg == WM_MENURBUTTONUP)
            {
                try
                {
                    var handler = MenuButtonUp;
                    if (handler != null)
                    {
                        handler(hWnd, lParam, wParam.ToInt32());
                        return IntPtr.Zero;      // 已处理：默认过程对这条消息本来也无动作
                    }
                }
                catch (Exception ex)
                {
                    Log("⚠ 右键菜单处理失败（主菜单不受影响）: " + ex.Message);
                }
            }

            if (_oldWndProc == IntPtr.Zero) return IntPtr.Zero;
            return CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private void Log(string line)
        {
            var log = ContextLog;
            if (log != null) { try { log(line); } catch { } }
        }

        /// <summary>
        /// 索引 → 命令 ID（**官方口径**：GetMenuItemInfoW + MF_BYPOSITION + MIIM_ID）。
        /// 返回 false 时 <paramref name="why"/> 说明为什么不可编辑（越界 / 分隔线 / 子菜单项）。
        /// ★ 纯查表，不弹任何菜单；自检直接喂 hMenu + index 即可。
        /// </summary>
        public static bool TryGetMenuItemInfo(IntPtr hMenu, int index,
            out int cmdId, out bool isSeparator, out bool isSubmenu, out string why)
        {
            cmdId = 0;
            isSeparator = false;
            isSubmenu = false;
            why = "";

            if (hMenu == IntPtr.Zero) { why = "菜单句柄为空"; return false; }

            int count = Win32.GetMenuItemCount(hMenu);
            if (count < 0) { why = "菜单句柄无效（GetMenuItemCount=" + count + "）"; return false; }
            if (index < 0 || index >= count)
            {
                why = "索引越界（index=" + index + "，菜单项数=" + count + "）";
                return false;
            }

            var mii = new Win32.MENUITEMINFO();
            mii.cbSize = (uint)Marshal.SizeOf(typeof(Win32.MENUITEMINFO));
            mii.fMask = Win32.MIIM_ID | Win32.MIIM_SUBMENU | MIIM_FTYPE;
            if (!Win32.GetMenuItemInfoW(hMenu, (uint)index, true, ref mii))
            {
                why = "GetMenuItemInfoW 失败 err=" + Marshal.GetLastWin32Error();
                return false;
            }

            if ((mii.fType & MFT_SEPARATOR) != 0 || mii.wID == 0)
            {
                isSeparator = true;
                why = "分隔线（不可编辑）";
                return false;
            }
            if (mii.hSubMenu != IntPtr.Zero || mii.wID == 0xFFFFFFFF)
            {
                isSubmenu = true;
                why = "子菜单项（容器没有动作，不可编辑）";
                return false;
            }

            cmdId = unchecked((int)mii.wID);
            if (cmdId <= 0) { why = "命令 ID 无效（" + cmdId + "）"; return false; }
            why = "";
            return true;
        }

        /// <summary>
        /// 裸 TrackPopupMenuEx（二级菜单用）：不装空格钩子、不抢/还焦点、不子类化 ——
        /// 二级菜单本来就在主菜单的模态循环里，抢焦点会打断外层。
        /// </summary>
        public static int TrackPopupRaw(IntPtr hMenu, uint flags, int x, int y, IntPtr ownerHwnd)
        {
            // ★ 二级菜单同样计入"菜单显示中"（嵌套深度），右键才不会在二级菜单里又被吞
            System.Threading.Interlocked.Increment(ref PopupDepth);
            try
            {
                return Win32.TrackPopupMenuEx(hMenu, flags, x, y, ownerHwnd, IntPtr.Zero);
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref PopupDepth);
            }
        }

        /// <summary>
        /// 结束当前线程的**活动菜单**（右键选定动作后关主菜单）。
        /// ★ 关掉而不是"就地改"：当前 HMENU 句柄是固定的，改完配置只能重建一张新菜单，
        ///   复用旧句柄会点到已失效的 IdMap（历史"点任何项都开同一个程序"同源）。
        /// </summary>
        public static bool EndActiveMenu()
        {
            try { return EndMenu(); }
            catch { return false; }
        }

        // ============================================================
        // 断言辅助（自检用）
        // ============================================================

        public int Count() { return Win32.GetMenuItemCount(Handle); }

        public int GetItemIdAt(int pos) { return Win32.GetMenuItemID(Handle, pos); }

        public IntPtr GetSubMenuHandle(int pos) { return Win32.GetSubMenu(Handle, pos); }

        public int CountSubMenus()
        {
            int n = 0;
            int c = Count();
            for (int i = 0; i < c; i++)
            {
                if (Win32.GetMenuItemID(Handle, i) == -1) n++;
            }
            return n;
        }

        public string GetItemText(int pos)
        {
            var sb = new StringBuilder(512);
            Win32.GetMenuStringW(Handle, (uint)pos, sb, sb.Capacity, 0x00000400); // MF_BYPOSITION
            return sb.ToString();
        }

        // ============================================================

        public void Dispose()
        {
            // ★ 只销毁自己创建的句柄（FromHandle 造的包装实例不拥有句柄）
            if (_ownsHandle && Handle != IntPtr.Zero)
            {
                Win32.DestroyMenu(Handle);
            }
            Handle = IntPtr.Zero;
            GC.SuppressFinalize(this);
        }

        ~NativeMenu()
        {
            // 终结器走同一路径：只有拥有句柄的实例才销毁
            try
            {
                if (_ownsHandle && Handle != IntPtr.Zero)
                    Win32.DestroyMenu(Handle);
            }
            catch { }
        }
    }
}
