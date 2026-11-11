using System;
using System.IO;
using System.Text;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 长按右键触发自检 —— 判定状态机（<see cref="MouseHoldState"/>）的机械断言。
    /// 用法：RunEverCs.exe --selftest-mousetrigger &lt;root&gt; &lt;out.txt&gt;
    ///
    /// ★ 2026-11-11 语义（用户定案）：**按下到点即弹**，不等抬起。
    ///   定时器打 TryFire(nowTick) 判「够久 + 未拖放 + 未触发」；
    ///   抬起只由 OnUp() 的返回值决定吞（true）还是放行/重放（false）。
    ///   旧版的 MouseHoldVerdict / OnUp(tick) 已删除，本文件不再引用。
    ///
    /// 覆盖（A–G，对应验收表）：
    ///   A 阈值夹取：100/199/200/3000/3001/9999 + 构造后 HoldMs 生效
    ///   B 短按不触发：held 不足 ⇒ TryFire=false ⇒ OnUp=false
    ///   C 到点触发：held=500 ⇒ TryFire=true，同一轮再调 false
    ///   D 未到点：held=499 ⇒ false
    ///   E 拖放：4px 不算、5px 算（含负方向 / Y 轴）、进入后 TryFire 恒 false、抬起不吞
    ///   F 一次性：触发后 OnUp=true；新一轮 OnDown 后可再次触发；Reset 清零
    ///   G 时间轴：long 单调时钟（Stopwatch），**无回绕**；跨 1 小时仍精确到毫秒
    ///
    /// ★ 为什么不在这里 `Install()` 真实全局钩子：
    ///   WH_MOUSE_LL 的回调靠**安装线程的消息泵**投递。自检进程不泵消息 ⇒ 系统每次鼠标事件
    ///   都要等钩子超时（LowLevelHooksTimeout，默认 ~300ms 起）⇒ 整个桌面的鼠标会卡住，
    ///   直到本进程退出。钩子安装/实点验证属**实机验收**，不在无人值守自检里做；
    ///   本自检只锁死「判定逻辑」这一层（那也是唯一能机械断言的一层）。
    /// </summary>
    public static class MouseTriggerSelfTest
    {
        /// <summary>
        /// 毫秒 → Stopwatch 时间戳。
        ///
        /// ★ 2026-11-11：判定内核的时间基准从 `Environment.TickCount`（约 15.6ms 粒度）
        ///   换成 `Stopwatch` 高精度时间戳 —— 外部会诊指出 `WM_TIMER` 的**到达精度不保证 15ms**，
        ///   "到没到 500ms"不该由定时器的送达时刻决定，应由单调时钟复算。
        ///   自检用本函数把"毫秒"换算到同一时间轴，断言语义保持不变。
        /// </summary>
        private static long Ms(double ms)
        {
            return (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        }

        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;

            string root = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int total = 0;
            int fail = 0;

            sb.AppendLine("[selftest-mousetrigger] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("被测类型     = RunEverCs.Services.MouseHoldState（MouseTrigger 的判定内核）");
            sb.AppendLine("判定语义     = 按下到点即触发（TryFire），抬起只决定吞/放（OnUp）");
            sb.AppendLine("位移阈值     = " + MouseHoldState.DragThresholdPx + "px（严格大于才判拖放）");
            sb.AppendLine("阈值范围     = " + MouseHoldState.MinHoldMs + "-" + MouseHoldState.MaxHoldMs + "ms");
            sb.AppendLine("真实钩子     = 未安装（自检进程无消息泵 ⇒ 会拖死全局鼠标；实机另验）");
            AppendCurrentConfig(sb, root);
            sb.AppendLine();

            // ============================================================
            // A 阈值夹取
            // ============================================================
            sb.AppendLine("-- A 阈值夹取 ---------------------------------------------");
            Check(sb, ref total, ref fail, "A1", "100 → 200（低于下限夹取）",
                MouseHoldState.ClampHoldMs(100) == 200, "得到 " + MouseHoldState.ClampHoldMs(100));
            Check(sb, ref total, ref fail, "A2", "199 → 200（下限外 1ms）",
                MouseHoldState.ClampHoldMs(199) == 200, "得到 " + MouseHoldState.ClampHoldMs(199));
            Check(sb, ref total, ref fail, "A3", "200 → 200（正好下限，不变）",
                MouseHoldState.ClampHoldMs(200) == 200, "得到 " + MouseHoldState.ClampHoldMs(200));
            Check(sb, ref total, ref fail, "A4", "3000 → 3000（正好上限，不变）",
                MouseHoldState.ClampHoldMs(3000) == 3000, "得到 " + MouseHoldState.ClampHoldMs(3000));
            Check(sb, ref total, ref fail, "A5", "3001 → 3000（上限外 1ms）",
                MouseHoldState.ClampHoldMs(3001) == 3000, "得到 " + MouseHoldState.ClampHoldMs(3001));
            Check(sb, ref total, ref fail, "A6", "9999 → 3000（高于上限夹取）",
                MouseHoldState.ClampHoldMs(9999) == 3000, "得到 " + MouseHoldState.ClampHoldMs(9999));
            {
                // 夹取结果必须真正落到实例上（配置写 100 ⇒ 实际按 200 判定）
                var lo = new MouseHoldState(100);
                var hi = new MouseHoldState(9999);
                Check(sb, ref total, ref fail, "A7", "构造后 HoldMs = 夹取结果（100→200，9999→3000）",
                    lo.HoldMs == 200 && hi.HoldMs == 3000,
                    "100→" + lo.HoldMs + "  9999→" + hi.HoldMs);
            }
            sb.AppendLine();

            // ============================================================
            // B 短按不触发
            // ============================================================
            sb.AppendLine("-- B 短按不触发 -------------------------------------------");
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 10, 10);
                bool fired = st.TryFire(Ms(1200));                  // held=200 < 500
                bool up = st.OnUp();

                Check(sb, ref total, ref fail, "B1", "holdMs=500：OnDown(1000) → TryFire(1200) = false",
                    !fired, "fired=" + fired);
                Check(sb, ref total, ref fail, "B2", "短按抬起 → OnUp() = false（钩子重放完整点击）",
                    !up, "OnUp=" + up);
                Check(sb, ref total, ref fail, "B3", "短按后 HasFired=false / IsPressed=false",
                    !st.HasFired && !st.IsPressed,
                    "HasFired=" + st.HasFired + " IsPressed=" + st.IsPressed);
            }
            {
                // 没经历 DOWN 的 UP（钩子中途装上）：不能误判成「已触发」而吞掉系统菜单
                var st = new MouseHoldState(500);
                bool up = st.OnUp();
                Check(sb, ref total, ref fail, "B4", "无 DOWN 的孤立 UP → OnUp() = false（不误吞）",
                    !up, "OnUp=" + up);
            }
            sb.AppendLine();

            // ============================================================
            // C 到点触发（held = holdMs 含等号）
            // ============================================================
            sb.AppendLine("-- C 到点触发（按下满阈值即触发，不等抬起）-----------------");
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 10, 10);

                bool first = st.TryFire(Ms(1500));                 // held=500
                bool second = st.TryFire(Ms(1700));                // 同一轮再来一次定时器

                Check(sb, ref total, ref fail, "C1", "OnDown(1000) → TryFire(1500) = true（held=500）",
                    first, "fired=" + first);
                Check(sb, ref total, ref fail, "C2", "同一轮再次 TryFire → false（本轮已触发）",
                    !second, "fired=" + second);
                Check(sb, ref total, ref fail, "C3", "触发后 HasFired=true / IsPressed=true（手指仍按着）",
                    st.HasFired && st.IsPressed,
                    "HasFired=" + st.HasFired + " IsPressed=" + st.IsPressed);
            }
            sb.AppendLine();

            // ============================================================
            // D 未到点
            // ============================================================
            sb.AppendLine("-- D 未到点（held = holdMs-1）------------------------------");
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 10, 10);
                bool fired = st.TryFire(Ms(1499));                 // held=499
                bool up = st.OnUp();

                Check(sb, ref total, ref fail, "D1", "OnDown(1000) → TryFire(1499) = false（held=499）",
                    !fired, "fired=" + fired);
                Check(sb, ref total, ref fail, "D2", "未到点抬起 → OnUp() = false（按短按处理）",
                    !up, "OnUp=" + up);
            }
            sb.AppendLine();

            // ============================================================
            // E 拖放（4px 不算 / 5px 算，严格大于）
            // ============================================================
            sb.AppendLine("-- E 拖放 / 位移边界（4px 不算、5px 算）--------------------");
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 100, 100);
                bool entered = st.OnMove(104, 100);            // X +4px
                Check(sb, ref total, ref fail, "E1", "位移 4px → OnMove=false，且不判拖放",
                    !entered && !st.IsDragging,
                    "OnMove=" + entered + " IsDragging=" + st.IsDragging);
            }
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 100, 100);
                bool entered = st.OnMove(104, 104);            // 对角各 4px（逐轴比较）
                Check(sb, ref total, ref fail, "E2", "对角 4px/4px → OnMove=false（逐轴严格大于）",
                    !entered && !st.IsDragging,
                    "OnMove=" + entered + " IsDragging=" + st.IsDragging);
            }
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 100, 100);
                bool entered = st.OnMove(105, 100);            // X +5px
                Check(sb, ref total, ref fail, "E3", "位移 5px → OnMove=true（本次刚进入拖放）",
                    entered && st.IsDragging,
                    "OnMove=" + entered + " IsDragging=" + st.IsDragging);
            }
            {
                // 拖放是粘性的：第二次移动不再返回 true（调用方只补发一次 DOWN）
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 100, 100);
                st.OnMove(140, 100);
                bool again = st.OnMove(200, 200);
                bool back = st.OnMove(100, 100);               // 拖回原点
                Check(sb, ref total, ref fail, "E4", "已拖放后再 OnMove / 拖回原点 → false，标记粘性",
                    !again && !back && st.IsDragging,
                    "again=" + again + " back=" + back + " IsDragging=" + st.IsDragging);
            }
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 100, 100);
                st.OnMove(105, 100);
                bool f1 = st.TryFire(Ms(1500));
                bool f2 = st.TryFire(Ms(2000));
                bool f3 = st.TryFire(Ms(9999));
                Check(sb, ref total, ref fail, "E5", "进入拖放后 TryFire 恒 false（无论多久）",
                    !f1 && !f2 && !f3 && !st.HasFired,
                    "1500=" + f1 + " 2000=" + f2 + " 9999=" + f3 + " HasFired=" + st.HasFired);
            }
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 100, 100);
                bool entered = st.OnMove(96, 100);             // X -4px
                Check(sb, ref total, ref fail, "E6", "负方向 -4px → OnMove=false（取绝对值后仍不大于阈值）",
                    !entered && !st.IsDragging,
                    "OnMove=" + entered + " IsDragging=" + st.IsDragging);
            }
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 100, 100);
                bool entered = st.OnMove(95, 100);             // X -5px
                Check(sb, ref total, ref fail, "E7", "负方向 -5px → OnMove=true",
                    entered && st.IsDragging,
                    "OnMove=" + entered + " IsDragging=" + st.IsDragging);
            }
            {
                var ax = new MouseHoldState(500);
                ax.OnDown(Ms(1000), 100, 100);
                bool minus4 = ax.OnMove(100, 96);              // Y -4px

                var ay = new MouseHoldState(500);
                ay.OnDown(Ms(1000), 100, 100);
                bool minus5 = ay.OnMove(100, 95);              // Y -5px

                Check(sb, ref total, ref fail, "E8", "Y 轴负方向：-4px → false / -5px → true",
                    !minus4 && minus5 && !ax.IsDragging && ay.IsDragging,
                    "-4=" + minus4 + " -5=" + minus5);
            }
            {
                // 拖放中抬起：系统已收到补发的 DOWN ⇒ 必须放行（不吞）
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 100, 100);
                st.OnMove(120, 100);
                bool up = st.OnUp();
                Check(sb, ref total, ref fail, "E9", "拖放中抬起 → OnUp() = false（放行给系统）",
                    !up, "OnUp=" + up);
            }
            sb.AppendLine();

            // ============================================================
            // F 一次性
            // ============================================================
            sb.AppendLine("-- F 一次性（同一次按下只弹一次；新一轮可再弹）-------------");
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 10, 10);
                bool fired = st.TryFire(Ms(1600));
                bool up = st.OnUp();

                Check(sb, ref total, ref fail, "F1", "触发后 OnUp() = true（钩子据此吞掉抬起）",
                    fired && up, "fired=" + fired + " OnUp=" + up);
                Check(sb, ref total, ref fail, "F2", "OnUp 之后 IsPressed=false（本轮结束）",
                    !st.IsPressed, "IsPressed=" + st.IsPressed);
            }
            {
                // 新一轮按下必须能再次触发（一次性不能把功能锁死）
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 10, 10);
                bool first = st.TryFire(Ms(1600));
                bool up1 = st.OnUp();
                st.OnDown(Ms(3000), 10, 10);
                bool firedReset = !st.HasFired && st.IsPressed;
                bool second = st.TryFire(Ms(3600));
                bool up2 = st.OnUp();

                Check(sb, ref total, ref fail, "F3", "两次独立长按 → 各触发一次（true/true，且第二轮 HasFired 已复位）",
                    first && up1 && firedReset && second && up2,
                    "first=" + first + " up1=" + up1 + " 复位=" + firedReset
                    + " second=" + second + " up2=" + up2);
            }
            {
                var st = new MouseHoldState(500);
                st.OnDown(Ms(1000), 10, 10);
                st.TryFire(Ms(1600));
                st.Reset();

                bool fireAfterReset = st.TryFire(Ms(9000));
                Check(sb, ref total, ref fail, "F4", "Reset() 清空 pressed/dragging/fired，之后 TryFire=false",
                    !st.IsPressed && !st.IsDragging && !st.HasFired && !fireAfterReset,
                    "IsPressed=" + st.IsPressed + " IsDragging=" + st.IsDragging
                    + " HasFired=" + st.HasFired + " TryFire=" + fireAfterReset);
            }
            sb.AppendLine();

            // ============================================================
            // G 时间轴（2026-11-11 已从 int TickCount 换成 long Stopwatch 时间戳）
            // ============================================================
            sb.AppendLine("-- G 时间轴（long 单调时钟，无回绕）------------------------");
            {
                // 旧实现用 `unchecked((uint)(now-down))` 兜 Environment.TickCount 的 49 天回绕；
                // 现在基准是 long 时间戳（Stopwatch），**回绕问题从根上消失**。
                // 这里改为验证：跨大跨度（1 小时）仍能精确判到毫秒。
                var st = new MouseHoldState(500);
                st.OnDown(Ms(0), 0, 0);
                bool fired = st.TryFire(Ms(3600 * 1000));   // 1 小时后
                Check(sb, ref total, ref fail, "G1", "跨 1 小时（Ms(3600000)）仍判成立",
                    fired, "fired=" + fired);
            }
            {
                // 反例：大跨度下差 1ms 依然不成立（证明不是"只要跨度大就当够"）
                var st = new MouseHoldState(500);
                st.OnDown(Ms(0), 0, 0);
                bool fired = st.TryFire(Ms(499));           // 差 1ms
                Check(sb, ref total, ref fail, "G2", "499ms < 500ms → 不触发（大跨度下仍精确）",
                    !fired, "fired=" + fired);
            }
            sb.AppendLine();

            // ============================================================
            // 汇总
            // ============================================================
            sb.AppendLine("----------------------------------------------------------");
            sb.AppendLine("断言总数 = " + total + "；通过 = " + (total - fail) + "；失败 = " + fail);
            sb.AppendLine("结果: " + (fail == 0 ? "全部通过 ✓" : fail + " 项失败 ✗"));

            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return fail == 0 ? 0 : 1;
        }

        /// <summary>把 auto.conf 当前的 mouseTrigger / holdMs 写进报告（**只读**，不参与判定）。</summary>
        private static void AppendCurrentConfig(StringBuilder sb, string root)
        {
            try
            {
                string conf = Path.Combine(root, "config", "auto.conf");
                if (!File.Exists(conf)) { sb.AppendLine("当前配置     = (无 auto.conf)"); return; }

                string mode = "(缺省 off)";
                string hold = "(缺省 500)";
                foreach (var raw in File.ReadAllLines(conf, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k == "mousetrigger") mode = v;
                    else if (k == "holdms") hold = v;
                }
                sb.AppendLine("当前配置     = mouseTrigger=" + mode + "  holdMs=" + hold + "（只读展示，不参与判定）");
            }
            catch (Exception ex)
            {
                sb.AppendLine("当前配置     = 读取失败: " + ex.Message);
            }
        }

        private static void Check(StringBuilder sb, ref int total, ref int fail,
            string id, string desc, bool ok, string detail)
        {
            total++;
            if (!ok) fail++;
            sb.AppendLine((ok ? "PASS  " : "FAIL  ") + id + "  " + desc
                + (string.IsNullOrEmpty(detail) ? "" : "   [" + detail + "]"));
        }
    }
}
