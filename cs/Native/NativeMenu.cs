using System;
using System.Collections.Generic;

namespace RunEverCs.Native
{
    /// <summary>
    /// Win32 原生菜单封装（分部类·<b>状态与契约</b>侧）。
    ///
    /// 本文件只放不碰 P/Invoke 的那一半：句柄/焦点诊断属性、进程级命令 ID 分配器、
    /// 子菜单引用（防 GC）、图标位图缓存账本、以及 FromHandle 的句柄包装入口。
    /// 建菜单句柄、填菜单项结构体、装图标、弹出与模态循环、Dispose 的句柄销毁
    /// 全在同目录的另一半分部文件里 —— 模型侧改状态、Win32 侧改调用，互不踩。
    /// </summary>
    public partial class NativeMenu : IDisposable
    {
        public IntPtr Handle { get; private set; }

        /// <summary>
        /// 最近一次 <see cref="Show"/> 的焦点诊断（抢焦点 / 还焦结果 + 当前前台 hwnd）。
        /// 由调用方写进日志 —— 实机验证"快捷键为什么无效"时就看这一行。
        /// </summary>
        public string FocusDiag { get; private set; }

        // ★★ 命令 ID 必须是【进程级全局唯一】，不能每菜单实例各自从 1 开始。
        //
        //    踩坑实录（2026-10-03，用户实测）：
        //      根菜单与各子菜单都用实例内 _nextId（都从 1 起）⇒ ID 撞车 ⇒
        //      idmap[1] 被后写入的子菜单项覆盖 ⇒
        //      点「控制面板」「任务管理器」等任何 ID=1 的项，结果全弹出「计算器」。
        //    症状是"除了计算器，其他不是打不开就是打开计算器"。
        //
        //    AHK 版同样每实例从 1 起（MenuNative.ahk L62 NextId := NM_ID_FIRST），
        //    隐患相同；只是它的 always 组层级较浅没暴露。这里彻底根治。
        private static int _globalNextId = 1;

        /// <summary>子菜单引用（防被 GC 回收 —— 句柄挂在父菜单上，但托管对象不能被回收）。</summary>
        private readonly List<NativeMenu> _subMenus = new List<NativeMenu>();

        /// <summary>分配一个进程级唯一的命令 ID（0 保留给「未选」）。</summary>
        private static int AllocId()
        {
            return System.Threading.Interlocked.Increment(ref _globalNextId) - 1;
        }

        /// <summary>重置 ID 分配器（每次构建新菜单树前调用，避免长期运行后 ID 溢出）。</summary>
        public static void ResetIdAllocator()
        {
            System.Threading.Interlocked.Exchange(ref _globalNextId, 1);
        }

        /// <summary>图标位图缓存（按源字符串去重，跨菜单实例共享）。</summary>
        private static readonly Dictionary<string, IntPtr> IconCache = new Dictionary<string, IntPtr>();
        private static IntPtr _fallbackBitmap = IntPtr.Zero;

        /// <summary>
        /// 用已有 HMENU 包装（只读遍历用，如自检递归子菜单）。
        /// ★ 这样造出来的实例【不拥有】句柄，Dispose 不能销毁它。
        /// </summary>
        public static NativeMenu FromHandle(IntPtr hMenu)
        {
            var m = new NativeMenu(true);
            m.Handle = hMenu;
            m._ownsHandle = false;
            return m;
        }

        private bool _ownsHandle = true;

        private NativeMenu(bool skipCreate)
        {
            // 供 FromHandle 用的无句柄构造
        }
    }
}
