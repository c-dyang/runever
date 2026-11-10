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
            uint flags = Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY
                | (rightAlign ? Win32.TPM_RIGHTALIGN : Win32.TPM_LEFTALIGN)
                | (bottomAlign ? Win32.TPM_BOTTOMALIGN : Win32.TPM_TOPALIGN);

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
            int ret;
            IntPtr hook = IntPtr.Zero;
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
                if (hook != IntPtr.Zero) { try { Win32.UnhookWindowsHookEx(hook); } catch { } }
            }

            // ★ 还焦：菜单关闭后把前台还给弹出前的窗口（选中/取消/点外面三条路径都会走到这里）
            bool restored = false;
            if (prevFg != IntPtr.Zero && prevFg != ownerHwnd && Win32.IsWindow(prevFg))
                restored = ForceForeground(prevFg);

            FocusDiag = diag + " | 还焦="
                + (restored ? "成功" : (prevFg == IntPtr.Zero ? "无前窗口" : "失败/跳过"))
                + " 当前前台=0x" + Win32.GetForegroundWindow().ToInt64().ToString("X");

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
