using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RunEverCs.Services
{
    /// <summary>
    /// 菜单关闭后的内存整理（2026-11-08，auto.conf 的 `memTrimIdle`）。
    ///
    /// 为什么需要：WPF + MahApps 主题字典在**第一次弹菜单**时才会解析（实测 66MB → 127MB），
    /// 之后常驻。这部分是设计上就留下的（预解析换来后续开菜单 60ms），不能丢；
    /// 但**页面可以还给操作系统** —— 空闲时把工作集清出去，任务管理器的读数立刻降下来，
    /// 代价只是下次开菜单的第一帧要从页面文件取回（预热已有几百 ms 预算）。
    ///
    /// 只做两件事，都在**后台线程**上跑，不占热键路径：
    ///   ① `GC.Collect` 收掉关闭菜单后残留的托管对象（菜单树 / 图片句柄）；
    ///   ② `EmptyWorkingSet` 把工作集还给系统（提交内存不变，只是缺页回读）。
    ///
    /// `memTrimIdle` 语义：菜单关闭后空闲多少秒执行整理；`0` = 关闭（回到"一直常驻 130MB"的行为）。
    /// </summary>
    public static class MemTrim
    {
        [DllImport("psapi.dll")]
        private static extern int EmptyWorkingSet(IntPtr hProcess);

        private static readonly object Gate = new object();
        private static System.Threading.Timer _timer;
        private static int _idleSeconds = 20;
        private static int _busy;              // 1 = 菜单开着，别整理
        private static Action<string> _log;

        /// <summary>由 RunEverApp 注入日志（与主日志同一条流）。</summary>
        public static void AttachLog(Action<string> log) { _log = log; }

        private static void L(string s)
        {
            var cb = _log;
            if (cb != null) { try { cb(s); } catch { } }
        }

        /// <summary>菜单要弹出了：抑制整理（避免整理与首次渲染抢页）。</summary>
        public static void MarkBusy()
        {
            System.Threading.Interlocked.Exchange(ref _busy, 1);
            lock (Gate)
            {
                if (_timer != null) { try { _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { } }
            }
        }

        /// <summary>菜单关闭：按 `memTrimIdle` 秒后跑一次整理。idleSeconds &lt;= 0 ⇒ 不做。</summary>
        public static void Schedule(int idleSeconds)
        {
            System.Threading.Interlocked.Exchange(ref _busy, 0);
            if (idleSeconds <= 0)
            {
                lock (Gate)
                {
                    if (_timer != null) { try { _timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); } catch { } }
                }
                return;
            }

            _idleSeconds = idleSeconds;
            lock (Gate)
            {
                if (_timer == null)
                    _timer = new System.Threading.Timer(OnTick, null, idleSeconds * 1000, System.Threading.Timeout.Infinite);
                else
                    _timer.Change(idleSeconds * 1000, System.Threading.Timeout.Infinite);
            }
        }

        private static void OnTick(object state)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _busy, 0, 0) != 0) return;
            try
            {
                var p = Process.GetCurrentProcess();
                long wsBefore = p.WorkingSet64;

                GC.Collect(2, GCCollectionMode.Forced, true, false);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, false);
                try { EmptyWorkingSet(p.Handle); } catch { }

                p.Refresh();
                long wsAfter = p.WorkingSet64;
                if (wsBefore / 1048576 != wsAfter / 1048576 || wsAfter < wsBefore)
                {
                    L("内存整理: 工作集 " + (wsBefore / 1048576) + "MB → " + (wsAfter / 1048576)
                      + "MB（空闲 " + _idleSeconds + "s 触发；提交内存不变）");
                }
            }
            catch (Exception ex)
            {
                L("内存整理失败（忽略）: " + ex.Message);
            }
        }
    }
}
