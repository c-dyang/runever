using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RunEverCs.Services
{
    /// <summary>
    /// 长按判定状态机 —— **纯逻辑、零 Win32、零 IO**（可被自检机械断言）。
    ///
    /// ★ 2026-11-11 语义修订（用户定案）：**按下达到阈值即触发**，不等抬起。
    ///   OnDown(tick,x,y)   → 记录起点，复位标志
    ///   OnMove(x,y)        → 位移 &gt; <see cref="DragThresholdPx"/>（严格大于）进入拖放，返回 true 通知补发 DOWN
    ///   TryFire(nowTick)   → 定时器到达：未拖放且未触发且 held &gt;= holdMs ⇒ true（并置已触发）
    ///   OnUp()             → 返回「是否已触发过」：true ⇒ 吞抬起；false ⇒ 短按/拖放，由钩子决定重放
    ///
    /// ★ 线程模型：钩子回调与 WM_TIMER 都在安装钩子的那个线程上，无竞态。
    /// </summary>
    public sealed class MouseHoldState
    {
        /// <summary>阈值下限（毫秒）：配置写小了夹到这里，不报错。</summary>
        public const int MinHoldMs = 200;

        /// <summary>阈值上限（毫秒）：配置写大了夹到这里，不报错。</summary>
        public const int MaxHoldMs = 3000;

        /// <summary>位移阈值（像素）：**严格大于** 才判拖放 ⇒ 4px 不算拖放、5px 算。</summary>
        public const int DragThresholdPx = 4;

        private readonly int _holdMs;

        private int _downTick;
        private int _downX;
        private int _downY;
        private bool _pressed;
        private bool _dragging;
        private bool _fired;

        public int HoldMs { get { return _holdMs; } }
        public bool IsPressed { get { return _pressed; } }
        public bool IsDragging { get { return _dragging; } }
        public bool HasFired { get { return _fired; } }

        /// <summary>阈值夹取：&lt;200 → 200，&gt;3000 → 3000，其余原样。**不抛、不报错。**</summary>
        public static int ClampHoldMs(int holdMs)
        {
            if (holdMs < MinHoldMs) return MinHoldMs;
            if (holdMs > MaxHoldMs) return MaxHoldMs;
            return holdMs;
        }

        public MouseHoldState(int holdMs)
        {
            _holdMs = ClampHoldMs(holdMs);
        }

        /// <summary>右键按下：记录时刻与坐标起点（钩子随后吞掉该 DOWN 并起定时器）。</summary>
        public void OnDown(int tick, int x, int y)
        {
            _downTick = tick;
            _downX = x;
            _downY = y;
            _pressed = true;
            _dragging = false;
            _fired = false;
        }

        /// <summary>
        /// 按下期间的移动。返回 <c>true</c> = **本次调用刚进入拖放**（调用方据此补发一个 DOWN 给系统）。
        /// 拖放是粘性的：一旦为真，抬起前不再复位。
        /// </summary>
        public bool OnMove(int x, int y)
        {
            if (!_pressed || _dragging) return false;

            if (Math.Abs(x - _downX) > DragThresholdPx
                || Math.Abs(y - _downY) > DragThresholdPx)
            {
                _dragging = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 定时器到达：**未拖放 + 未触发 + 按住已满 holdMs** ⇒ 触发（置已触发并返回 true）。
        /// 无符号差值处理 TickCount 的 49 天回绕。
        /// </summary>
        public bool TryFire(int nowTick)
        {
            if (!_pressed || _dragging || _fired) return false;

            long held = unchecked((uint)(nowTick - _downTick));
            if (held < _holdMs) return false;

            _fired = true;
            return true;
        }

        /// <summary>
        /// 右键抬起：返回「本次按下是否已经触发过」。
        /// true ⇒ 钩子吞掉抬起（系统视角从未按下过，干净）；false ⇒ 短按或拖放，由钩子重放/放行。
        /// </summary>
        public bool OnUp()
        {
            bool fired = _fired;
            _pressed = false;
            return fired;
        }

        /// <summary>卸载钩子时清状态（避免残留「按下中」导致下次抬起误判）。</summary>
        public void Reset()
        {
            _pressed = false;
            _dragging = false;
            _fired = false;
        }
    }

    /// <summary>
    /// 长按右键触发 —— WH_MOUSE_LL 低层鼠标钩子（2026-11-11 新增；同日按用户定案改为「按到时间即弹」）。
    ///
    /// ★ 为什么在 C# 而不是 AHK：
    ///   触发热键一直在 C# 托盘（TrayApp.RegisterHotKey，日志实证 2026-10-03 起），
    ///   AHK 的 HotkeyTrigger.ahk 已退役；且 RegisterHotKey 不支持鼠标键。
    ///
    /// ★ 时序（2026-11-11 修订版，**这是核心**）：
    ///   RBUTTONDOWN → **吞掉**（return 1）+ 起 SetTimer(holdMs)
    ///   MOUSEMOVE   → 位移 &gt;4px ⇒ 取消定时器 + **补发一个 DOWN**（让系统接管拖放）
    ///   WM_TIMER    → 取消定时器，由 TrayApp 弹菜单（此时手指仍按着）
    ///   RBUTTONUP   → 已触发过 ⇒ 吞掉（系统从没收到 DOWN，无状态残留）
    ///                 拖放中   ⇒ 放行（系统已收到补发的 DOWN）
    ///                 短按     ⇒ **补发 DOWN+UP**（系统收到完整点击 ⇒ 系统右键菜单照常弹）
    ///
    /// ★ 为什么 DOWN 也要吞（上一版的 bug 就在这）：
    ///   只吞 UP 会让目标窗口认为「右键一直按着」⇒ 菜单弹出后资源管理器处于按下模态，
    ///   表现为「菜单点不起」。DOWN 吞掉后系统视角干净，TrackPopupMenu 也不会撞上
    ///   「按钮已按下 ⇒ 菜单不显示/返回 0」这个已知 Win32 坑。
    ///
    /// ★ 补发事件的防自触发：SendInput 带 dwExtraInfo 魔数（<see cref="ReplayMagic"/>），
    ///   钩子见到魔数直接放行，不再进入判定。
    ///
    /// ★ 三条硬约束（违反会出诡异故障）：
    ///   ① 回调必须 &lt;300ms 返回 —— 超时后系统**静默卸载钩子**（症状：用着用着失灵，无报错）
    ///   ② 弹菜单不在回调里做 —— 定时器消息回 TrayApp 主线程后再弹
    ///   ③ 回调内不做 IO、不读配置、不弹窗
    /// </summary>
    public sealed class MouseTrigger : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;

        private const int INPUT_MOUSE = 0;
        private const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const int MOUSEEVENTF_RIGHTUP = 0x0010;

        /// <summary>补发事件的魔数（dwExtraInfo）—— 钩子见到就直接放行，防自触发。</summary>
        public static readonly IntPtr ReplayMagic = new IntPtr(unchecked((int)0x52564552));

        /// <summary>WM_TIMER 的定时器 id（TrayApp 在消息回调里按它分流）。</summary>
        public const int TimerId = 0x5257;

        /// <summary>钩子 → 主线程的「请你补发一个右键事件」请求（WM_APP+83）。</summary>
        public const int WM_REPLAY = 0x8000 + 0x53;

        /// <summary>WM_REPLAY 的 wParam：1 = 补发按下，0 = 补发抬起。</summary>
        public const int ReplayDown = 1;

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT Pt;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr DwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int Dx;
            public int Dy;
            public int MouseData;
            public int Flags;
            public int Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int Type;
            public MOUSEINPUT Mouse;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetTimer(IntPtr hWnd, int nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool KillTimer(IntPtr hWnd, int nIDEvent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        // ★ 字段持有委托，防 GC 回收（钩子回调被回收 = 进程直接崩）
        private readonly LowLevelMouseProc _proc;
        private IntPtr _hook = IntPtr.Zero;

        private readonly MouseHoldState _state;
        private readonly IntPtr _hwnd;
        private readonly Action<string> _log;

        public bool IsHooked { get { return _hook != IntPtr.Zero; } }

        /// <summary>实际生效的长按阈值（已被夹到 200-3000）。</summary>
        public int HoldMs { get { return _state.HoldMs; } }

        public MouseHoldState State { get { return _state; } }

        /// <param name="holdMs">长按阈值（毫秒），200-3000 外会被夹到边界。</param>
        /// <param name="hwnd">消息窗口句柄（SetTimer 的宿主；WM_TIMER 由 TrayApp 分流）。</param>
        /// <param name="log">日志出口（TrayApp 的 _app.Log）。</param>
        public MouseTrigger(int holdMs, IntPtr hwnd, Action<string> log)
        {
            _state = new MouseHoldState(holdMs);
            _hwnd = hwnd;
            _log = log ?? (s => { });
            _proc = HookProc;
        }

        public bool Install()
        {
            if (_hook != IntPtr.Zero) return true;
            try
            {
                using (var p = Process.GetCurrentProcess())
                using (var m = p.MainModule)
                {
                    _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc,
                        GetModuleHandleW(m.ModuleName), 0);
                }
            }
            catch (Exception ex)
            {
                _log("鼠标钩子安装异常: " + ex.Message);
            }

            if (_hook == IntPtr.Zero)
            {
                _log("⚠ 鼠标钩子安装失败 err=" + Marshal.GetLastWin32Error()
                    + "（可能被安全软件拦截；鼠标触发不可用，热键不受影响）");
                return false;
            }

            _log("鼠标触发 OK: 长按右键 " + _state.HoldMs + "ms（按到时间即弹）");
            return true;
        }

        public void Uninstall()
        {
            if (_hwnd != IntPtr.Zero) KillTimer(_hwnd, TimerId);
            if (_hook == IntPtr.Zero) return;
            try { UnhookWindowsHookEx(_hook); } catch { }
            _hook = IntPtr.Zero;
            _state.Reset();
        }

        public void Dispose()
        {
            Uninstall();
        }

        /// <summary>
        /// 定时器到（TrayApp 收到 WM_TIMER 时调）：更新状态，返回 <c>true</c> 表示该弹菜单。
        /// 若返回 false（期间进了拖放/已触发），静默丢弃。
        /// </summary>
        public bool OnTimerElapsed()
        {
            bool fire = _state.TryFire(Environment.TickCount);
            if (fire && _hwnd != IntPtr.Zero) KillTimer(_hwnd, TimerId);
            return fire;
        }

        /// <summary>
        /// 请求补发（**只在钩子回调里调用**）：只投递一条消息，立即返回。
        ///
        /// ★★ 2026-11-11 致命 bug 修复点：**钩子回调里绝不能调 SendInput**。
        ///   注入的事件会**立刻重新进入钩子链**（PowerToys #5040 原话：
        ///   "as soon as SendInput is called the next hook event gets processed"），
        ///   于是本进程在钩子回调里同步等待自己的下一个回调 ⇒ 消息泵卡死。
        ///   实测症状（用户机器，EnableLUA=0）：长按右键失效 + 热键无响应 + 进程杀不掉。
        ///   同款现象见 SO 62662606「程序停止响应」、SO 60236649「延迟 400ms」。
        ///   ⇒ 回调里只做内存操作 + SetTimer/KillTimer + PostMessage（全部非阻塞）。
        /// </summary>
        private void RequestReplay(bool down)
        {
            if (_hwnd == IntPtr.Zero) return;
            try { PostMessageW(_hwnd, WM_REPLAY, down ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        /// <summary>真正执行补发（**必须由主线程在收到 WM_REPLAY 时调用**）。</summary>
        public void DoReplay(bool down)
        {
            try
            {
                var inp = new INPUT[1];
                inp[0].Type = INPUT_MOUSE;
                inp[0].Mouse.Flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP;
                inp[0].Mouse.ExtraInfo = ReplayMagic;
                SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
            }
            catch (Exception ex)
            {
                try { _log("补发右键失败: " + ex.Message); } catch { }
            }
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
                return CallNextHookEx(_hook, nCode, wParam, lParam);

            try
            {
                // ★ 自己补发的事件：直接放行，绝不进入判定（否则自触发成环）
                var s = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if (s.DwExtraInfo == ReplayMagic)
                    return CallNextHookEx(_hook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();

                if (msg == WM_RBUTTONDOWN)
                {
                    _state.OnDown(Environment.TickCount, s.Pt.X, s.Pt.Y);
                    if (_hwnd != IntPtr.Zero)
                        SetTimer(_hwnd, TimerId, (uint)_state.HoldMs, IntPtr.Zero);
                    return (IntPtr)1;   // ★ 吞：系统不知道有右键按下
                }

                if (msg == WM_MOUSEMOVE && _state.IsPressed)
                {
                    if (_state.OnMove(s.Pt.X, s.Pt.Y))
                    {
                        // 进入拖放：取消长按判定，并把「按下」补还给系统（投递，不注入）
                        if (_hwnd != IntPtr.Zero) KillTimer(_hwnd, TimerId);
                        RequestReplay(true);
                    }
                    return CallNextHookEx(_hook, nCode, wParam, lParam);
                }

                if (msg == WM_RBUTTONUP)
                {
                    bool fired = _state.OnUp();
                    if (_hwnd != IntPtr.Zero) KillTimer(_hwnd, TimerId);

                    if (fired)
                        return (IntPtr)1;                 // 长按已弹 ⇒ 吞（系统无状态残留）

                    if (_state.IsDragging)
                        return CallNextHookEx(_hook, nCode, wParam, lParam);   // 拖放 ⇒ 放行

                    // 短按：系统从没见过按下 ⇒ 补一次完整点击，系统右键菜单照常弹
                    //   （★ 只投递，实际 SendInput 在主线程做 —— 回调里直接注入会卡死消息泵）
                    RequestReplay(true);
                    RequestReplay(false);
                    return (IntPtr)1;                     // 原始 UP 吞掉，避免重复
                }
            }
            catch (Exception ex)
            {
                // 回调里绝不抛：吞掉并留痕（超时 >300ms 会被系统卸钩子）
                try { _log("鼠标钩子回调异常: " + ex.Message); } catch { }
            }

            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }
    }
}
