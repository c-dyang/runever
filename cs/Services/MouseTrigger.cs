using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RunEverCs.Services
{
    /// <summary>
    /// 长按判定状态机 —— **纯逻辑、零 Win32、零 IO**（可被自检机械断言）。
    ///
    /// 时序语义（与钩子回调一一对应）：
    ///   OnDown(stamp,x,y) → 记录起点，复位标志
    ///   OnMove(x,y)       → 位移 &gt; <see cref="DragThresholdPx"/>（严格大于）进入拖放；
    ///                       **已触发（fired）后不再进拖放**
    ///   TryFire(nowStamp) → 未拖放 + 未触发 + 真实经过时间 &gt;= holdMs ⇒ true（并置 fired）
    ///   OnUp()            → 返回「本次按下是否已触发过」
    ///
    /// ★ 2026-11-11 修订（外部会诊后）：
    ///   ① 时间基准从 `Environment.TickCount`（约 15.6ms 粒度）换成 `Stopwatch` 高精度时间戳
    ///      —— `WM_TIMER` 的触发精度并不保证 15ms，只靠它算"到没到 500ms"不严谨。
    ///   ② `fired` 之后禁止再进拖放：否则「菜单已经弹出，又被拖放逻辑截走」。
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

        private long _downStamp;
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

        /// <summary>右键按下：记录高精度时间戳与坐标起点（钩子随后吞掉该 DOWN 并起定时器）。</summary>
        public void OnDown(long stamp, int x, int y)
        {
            _downStamp = stamp;
            _downX = x;
            _downY = y;
            _pressed = true;
            _dragging = false;
            _fired = false;
        }

        /// <summary>
        /// 按下期间的移动。返回 <c>true</c> = **本次调用刚进入拖放**。
        /// ★ 已触发后恒返回 false：菜单已经弹出，再让拖放逻辑接管会造成"菜单还在、系统却开始拖路径"。
        /// </summary>
        public bool OnMove(int x, int y)
        {
            if (!_pressed || _dragging || _fired) return false;

            if (Math.Abs(x - _downX) > DragThresholdPx
                || Math.Abs(y - _downY) > DragThresholdPx)
            {
                _dragging = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 定时器到达：**未拖放 + 未触发 + 真实经过时间已满 holdMs** ⇒ 触发（置 fired 并返回 true）。
        /// 时间用 Stopwatch 时间戳复算，不信任 WM_TIMER 的到达时刻。
        /// </summary>
        public bool TryFire(long nowStamp)
        {
            if (!_pressed || _dragging || _fired) return false;

            double elapsedMs = (nowStamp - _downStamp) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs < _holdMs) return false;

            _fired = true;
            return true;
        }

        /// <summary>
        /// 右键抬起：返回「本次按下是否已经触发过」。
        /// true ⇒ 钩子吞掉抬起（系统视角从未按下过，干净）；false ⇒ 短按或拖放，由钩子决定。
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
    /// 长按右键触发 —— WH_MOUSE_LL 低层鼠标钩子。
    ///
    /// ★★ 2026-11-11 三次修订史（每次都是真机事故换来的，不要回退）：
    ///   ① 只吞 UP → 目标窗口认为「右键一直按着」，菜单弹出后点不起 ⇒ 改为 DOWN 也吞。
    ///   ② **在钩子回调里直接 SendInput → 注入事件立刻重入钩子链 ⇒ 消息泵自锁**
    ///      （症状：长按右键失效 + 热键无响应 + 进程卡在退出流程）。
    ///      ⇒ 回调里只 `PostMessage` 投递，注入放主线程。
    ///   ③ 投递与物理事件之间存在**时序竞态**（外部会诊指出）：
    ///      拖放请求还在队列里、物理 UP 已经到了 ⇒ 系统收到一个没有配对的 UP。
    ///      ⇒ 引入 pending / injected / released 三态（见 <see cref="OnReplayRequest"/>）。
    ///
    /// ★ 当前时序：
    ///   RBUTTONDOWN → 吞掉（return 1）+ SetTimer(holdMs)
    ///   MOUSEMOVE   → 位移 &gt;4px ⇒ KillTimer + 请求补发 DOWN（**只投递**）
    ///   WM_TIMER    → 主线程复算真实耗时，够了就弹菜单（手指仍按着）
    ///   RBUTTONUP   → 已触发        ⇒ 吞
    ///                 DOWN 已注入   ⇒ 放行（系统有完整配对）
    ///                 DOWN 待注入   ⇒ **吞掉并标记已释放**，等主线程发 DOWN+UP 一次补齐
    ///                 短按          ⇒ 请求「一次原子 tap」，吞原始 UP
    ///
    /// ★ 三条硬约束（违反会出诡异故障）：
    ///   ① 回调必须 &lt;300ms 返回 —— 超时后系统**静默卸载钩子**
    ///   ② **回调内禁止 SendInput / SendMessage / 任何同步等待 / IO**
    ///   ③ 弹菜单不在回调里做 —— 定时器消息回主线程后再弹
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

        /// <summary>钩子 → 主线程的「请补发」请求（WM_APP+83）。</summary>
        public const int WM_REPLAY = 0x8000 + 0x53;

        // ---- 补发请求类型 ----
        private const int ReqNone = 0;
        private const int ReqDown = 1;   // 拖放：只要补 DOWN
        private const int ReqTap = 2;    // 短按：原子 DOWN+UP

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

        // 钩子线程写、主线程读 —— 都是 int/bool 原子读写
        private int _pendingReplay = ReqNone;
        private volatile bool _downInjected;      // 补发的 DOWN 已真正注入
        private volatile bool _physicalReleased;  // 物理 UP 已到，但 DOWN 还没注入
        private int _postFailCount;               // PostMessage 失败次数（主线程定期落日志）

        private readonly LowLevelMouseProc _proc;
        private IntPtr _hook = IntPtr.Zero;

        private readonly MouseHoldState _state;
        private readonly IntPtr _hwnd;
        private readonly Action<string> _log;

        public bool IsHooked { get { return _hook != IntPtr.Zero; } }
        public int HoldMs { get { return _state.HoldMs; } }
        public MouseHoldState State { get { return _state; } }

        /// <param name="holdMs">长按阈值（毫秒），200-3000 外会被夹到边界。</param>
        /// <param name="hwnd">消息窗口句柄（SetTimer 宿主 + WM_REPLAY 目的地）。</param>
        /// <param name="log">日志出口（**只在主线程路径上调用**）。</param>
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
            _pendingReplay = ReqNone;
            _downInjected = false;
            _physicalReleased = false;
        }

        public void Dispose()
        {
            Uninstall();
        }

        /// <summary>
        /// 定时器到（TrayApp 收到 WM_TIMER 时**在主线程**调）：用高精度时间戳复算，够久才返回 true。
        /// </summary>
        public bool OnTimerElapsed()
        {
            bool fire = _state.TryFire(Stopwatch.GetTimestamp());
            if (fire && _hwnd != IntPtr.Zero) KillTimer(_hwnd, TimerId);
            return fire;
        }

        /// <summary>
        /// 主线程收到 WM_REPLAY 时调用：真正执行注入。
        ///
        /// ★ 三态处理（外部会诊指出的时序竞态）：
        ///   拖放请求到达时，若物理 UP 已经先到（`_physicalReleased`），
        ///   那就不能只补 DOWN（会留下没有抬起配对的按下），而是**一次性补完整 tap**。
        /// </summary>
        public void OnReplayRequest()
        {
            int req = System.Threading.Interlocked.Exchange(ref _pendingReplay, ReqNone);
            if (req == ReqNone) return;

            if (req == ReqTap)
            {
                DoTap();
                return;
            }

            // req == ReqDown（拖放场景）
            if (_physicalReleased)
            {
                // 请求还在路上，用户已经松手 → 补一次完整点击，不留悬空按下
                DoTap();
            }
            else
            {
                DoDown();
                _downInjected = true;
            }
        }

        /// <summary>主线程调用：把 PostMessage 失败次数落日志（回调里不能 IO，只能攒着）。</summary>
        public void FlushDiagnostics()
        {
            int n = System.Threading.Interlocked.Exchange(ref _postFailCount, 0);
            if (n > 0) _log("⚠ 鼠标钩子 PostMessage 失败 " + n + " 次（消息队列满？补发可能丢失）");
        }

        /// <summary>补发一次右键按下。</summary>
        private void DoDown()
        {
            Send(1, MOUSEEVENTF_RIGHTDOWN);
        }

        /// <summary>
        /// 补发一次完整点击 —— **单次 SendInput 提交 DOWN+UP**。
        /// ★ 依据：MSDN 说单次 SendInput 内的输入按顺序插入且不会被其它输入打断 ⇒ 这才是原子事务；
        ///   分两次调用或两条 WM_COPY 消息都可能被物理事件插进来。
        /// </summary>
        private void DoTap()
        {
            try
            {
                var inp = new INPUT[2];
                inp[0].Type = INPUT_MOUSE;
                inp[0].Mouse.Flags = MOUSEEVENTF_RIGHTDOWN;
                inp[0].Mouse.ExtraInfo = ReplayMagic;
                inp[1].Type = INPUT_MOUSE;
                inp[1].Mouse.Flags = MOUSEEVENTF_RIGHTUP;
                inp[1].Mouse.ExtraInfo = ReplayMagic;

                uint sent = SendInput(2, inp, Marshal.SizeOf(typeof(INPUT)));
                if (sent != 2)
                    _log("⚠ 补发点击未完整注入（SendInput 返回 " + sent
                        + "/2，err=" + Marshal.GetLastWin32Error() + "）");
            }
            catch (Exception ex)
            {
                _log("补发点击异常: " + ex.Message);
            }
        }

        private void Send(uint count, int flag)
        {
            try
            {
                var inp = new INPUT[1];
                inp[0].Type = INPUT_MOUSE;
                inp[0].Mouse.Flags = flag;
                inp[0].Mouse.ExtraInfo = ReplayMagic;

                uint sent = SendInput(1, inp, Marshal.SizeOf(typeof(INPUT)));
                if (sent != count)
                    _log("⚠ 补发右键未注入（SendInput 返回 " + sent + "/" + count
                        + "，err=" + Marshal.GetLastWin32Error() + "）");
            }
            catch (Exception ex)
            {
                _log("补发右键异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 请求补发（**只在钩子回调里调用**）：只投递一条消息，立即返回。
        ///
        /// ★★ 绝不能在回调里调 SendInput：注入的事件会立刻重入钩子链
        ///   （PowerToys #5040：“as soon as SendInput is called the next hook event gets processed”），
        ///   本进程就在回调里等自己的下一个回调 ⇒ 消息泵自锁。
        ///   实测症状：长按右键失效 + 热键无响应 + 进程卡在退出流程。
        /// </summary>
        private void RequestReplay(int kind)
        {
            if (_hwnd == IntPtr.Zero) return;
            System.Threading.Interlocked.Exchange(ref _pendingReplay, kind);
            if (!PostMessageW(_hwnd, WM_REPLAY, IntPtr.Zero, IntPtr.Zero))
                System.Threading.Interlocked.Increment(ref _postFailCount);
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
                    _state.OnDown(Stopwatch.GetTimestamp(), s.Pt.X, s.Pt.Y);
                    _downInjected = false;
                    _physicalReleased = false;
                    if (_hwnd != IntPtr.Zero)
                        SetTimer(_hwnd, TimerId, (uint)_state.HoldMs, IntPtr.Zero);
                    return (IntPtr)1;   // ★ 吞：系统不知道有右键按下
                }

                if (msg == WM_MOUSEMOVE && _state.IsPressed)
                {
                    if (_state.OnMove(s.Pt.X, s.Pt.Y))
                    {
                        // 进入拖放：取消长按判定，把「按下」补还给系统（只投递）
                        if (_hwnd != IntPtr.Zero) KillTimer(_hwnd, TimerId);
                        RequestReplay(ReqDown);
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
                    {
                        if (_downInjected)
                            return CallNextHookEx(_hook, nCode, wParam, lParam);   // 有配对 ⇒ 放行

                        // ★ DOWN 还在队列里没注入：吞掉这次 UP 并打标记，
                        //   主线程稍后看到标记会改发「原子 tap」，不留悬空按下。
                        _physicalReleased = true;
                        return (IntPtr)1;
                    }

                    // 短按：系统从没见过按下 ⇒ 请求一次原子 tap，原始 UP 吞掉
                    RequestReplay(ReqTap);
                    return (IntPtr)1;
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
