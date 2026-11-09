using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using RunEverCs.Models;
using RunEverCs.Native;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 主程序 —— 对应 AHK RunEver.ahk 的主流程。
    ///
    /// ★ 分工（用户 2026-10-03 定案）：
    ///   · 热键触发 → 留 AHK（单键 ` 用 AHK 可靠，C# 需键盘钩子有杀软风险）
    ///   · 采集     → 留 AHK（编译 exe，C# 进程调用）
    ///   · 菜单渲染 / 判定 / 执行 / 设置 UI → 全在 C#
    ///
    /// 触发协议（AHK → C#）：
    ///   AHK 热键脚本启动本 exe 并带 --show-menu 参数；
    ///   本 exe 采集 → 匹配 → 弹菜单 → 执行 → 退出。
    ///   （与 AHK 版"进程生命周期消灭阻塞"的架构一致）
    /// </summary>
    public class RunEverApp
    {
        public string Root { get; private set; }

        /// <summary>
        /// menus.conf 解析产物。★ 段 1.7：**只能整体替换**（`_parser = fresh`），
        /// 禁止任何运行期 `_parser.Groups.Add/Clear`（就地改会让持旧引用的链看到"半成品列表"）。
        /// `volatile`：写入方 = 设置保存线程（`OnConfigSaved`/`ReloadMenus`）/ 热键线程，
        /// 读取方 = 托盘任意线程 ⇒ 需要可见性保证（C# 7.3 / net48 下引用类型 volatile 合法）。
        /// </summary>
        private volatile ConfParser _parser;

        /// <summary>段 1.7：menus.conf 的版本戳检查点（热键路径 1 次 stat，未变 0 解析 / 0 读盘）。</summary>
        private MenuConfWatcher _menuWatcher;

        private PathCache _cache;
        private IconResolver _icons;
        private CaptureClient _capture;

        /// <summary>
        /// 常驻采集（2026-11-07）：AHK 采集器作为本进程的子进程常驻，
        /// 管道往返 ~3ms（一次性模式 ~396ms）。采集逻辑仍在 AHK
        /// （用户 2026-10-03 定案），这里只做管道与解析。
        ///
        /// ★ 2026-11-07 晚：进程内原生采集后端（NativeCapture / Win32+UIA）**已删除**。
        ///   原因：它取「选中文件」时只读剪贴板里现成的 CF_HDROP、从不发 Ctrl+C，
        ///   而 Explorer 选中文件不会自动进剪贴板 ⇒ files 恒为 0，
        ///   PDF/图片/文本/压缩包/视频 五组动态菜单永不命中（两台机器现象一致）。
        ///   补它等于把 AHK 里已踩平的 Ctrl+C/clipboard 还原坑在 C# 重踩一遍，
        ///   且 Ctrl+C 一旦发出无法撤回 ⇒ 采集统一收敛到 AHK 一条路。
        /// </summary>
        private ResidentCaptureClient _captureResident;
        private Executor _executor;
        private AutoMatcher _autoMatcher;
        private PluginRunner _pluginRunner;
        private string _logPath;

        /// <summary>auto.conf 的当前内存快照（托盘预热等场景只读；null = 尚未初始化）。</summary>
        public AutoConf Conf
        {
            get { return _autoMatcher == null ? null : _autoMatcher.Conf; }
        }

        public RunEverApp(string root)
        {
            Root = root;
            _logPath = Path.Combine(root, "state", "runever-cs.log");
        }

        /// <summary>后台解析互斥（0 = 空闲）：开机扫 / 托盘刷新 / 配置保存后增量共用一条入口。</summary>
        private int _refreshBusy;

        /// <summary>
        /// 用户可见通知出口（执行期失败 ⇒ 托盘气泡）。
        /// 由 TrayApp 注入；未注入时只写 `state\` 日志（不静默）。
        /// </summary>
        public Action<string> UserNotify;

        /// <summary>
        /// 带自定义标题的用户通知出口（托盘气泡）。
        /// ★ B4a-2：空菜单解释**不是**"执行失败"，不能套「RunEver 执行失败」的标题。
        /// 未注入时退化为 <see cref="UserNotify"/>（老调用方零改动）。
        /// </summary>
        public Action<string, string> UserNotifyTitled;

        private void NotifyUser(string msg)
        {
            Log("通知用户: " + msg);
            if (UserNotify != null)
            {
                try { UserNotify(msg); } catch { }
            }
        }

        /// <summary>带标题的通知（先走 <see cref="UserNotifyTitled"/>，没注入就退回 <see cref="UserNotify"/>）。</summary>
        private void NotifyUser(string title, string msg)
        {
            Log("通知用户[" + title + "]: " + msg);
            if (UserNotifyTitled != null)
            {
                try { UserNotifyTitled(title, msg); return; } catch { }
            }
            if (UserNotify != null)
            {
                try { UserNotify(msg); } catch { }
            }
        }

        // ============================================================
        // 初始化
        // ============================================================
        public bool Init()
        {
            try
            {
                EnsureLayout();
                ReadLogSettings();     // ★ 先读日志开关，后面的记录才受控
                Log("=== RunEver C# 启动 ===");
                Log("root = " + Root);

                string confPath = Path.Combine(Root, "config", "menus.conf");
                Log("conf = " + confPath + " (存在=" + File.Exists(confPath) + ")");

                _cache = new PathCache(Path.Combine(Root, "cache", "paths.txt"));
                // 内存整理（2026-11-08）：菜单关闭后空闲若干秒把工作集还给系统
                Services.MemTrim.AttachLog(Log);
                Native.NativeMenu.KeyLog = Log;
                _capture = new CaptureClient(Root);
                // ★ 常驻采集：复用 CaptureClient 的路径解析（dist\CaptureRunner.exe 优先，
                //   其次根目录，最后 src\CaptureRunner.ahk + 解释器）。
                //   起进程是惰性的（第一次 WaitCapture 才起），启动不额外付进程创建时间。
                _captureResident = new ResidentCaptureClient(_capture.RunnerPath, _capture.AhkExe);
                // 图标落盘缓存（2026-11-07）：state\icons\<sha1(exe路径)>_<mtime>.png
                Native.IconDiskCache.Init(Root);

                // ★★ 并行预热采集：采集要起 AHK 进程（~250ms 主要是运行时加载），
                //    与后面的配置解析/图标准备重叠，能省下大半等待。
                //    实测（2026-10-03）：采集 279ms 是"菜单弹出慢"的最大头，
                //    串行时完全暴露在用户等待里。
                if (_capture.IsAvailable())
                {
                    _captureTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    _captureTask = System.Threading.Tasks.Task.Run(() => CaptureOnce());
                }

                _parser = new ConfParser(confPath);
                _parser.Parse();
                Log("配置加载 OK，分组数 = " + _parser.Groups.Count);

                // ★ 段 1.7：menus.conf 热加载检查点（登记启动解析的版本戳基线 ⇒ 首次热键不白解析）
                _menuWatcher = new MenuConfWatcher(confPath, s => Log("  " + s));
                _menuWatcher.AcceptBaseline(_parser);

                Log("路径缓存 OK，条目数 = " + _cache.Count);

                _icons = new IconResolver(_cache);
                _executor = new Executor(_cache);

                // ★ 「禁静默失败」（AC-5③ / 契约 §4-3）：执行期失败必须有用户可见出口
                _executor.ErrorNotify = s => NotifyUser(s);
                // ★ 时机③（软件移位兜底）：现查走解析层 —— 含硬超时 + 命中回写缓存
                _executor.OnDemandResolve = ResolveOnDemandForRun;

                // ★ 自动执行（匹配块）—— 热键路径的 1.5 步早退
                //   （对照 AHK `RunEver.ahk:564-577`）
                //   · 这里只构造，不扫插件目录：实际读取推迟到 TryAutoExec 的
                //     Load(false)，由 auto.conf 的 cacheSeconds 决定是否真读盘
                //     ⇒ 菜单弹出前零多余 IO。
                //   · 日志接主日志（带 "  " 缩进，与既有分阶段日志同级）。
                _autoMatcher = new AutoMatcher(Root, s => Log("  " + s));
                // ★ 2026-11-07：构造只给默认值，Conf 要等 Load() 才填 —— 而 Load() 只在
                //   TryAutoExec 里调（无选中文本时可能提前 return）。结果托盘启动后
                //   `Conf.HideMissing` / `Conf.MenuOrder` / `Conf.MemTrimIdle` 一直是默认值，
                //   改配置永远不生效。这里补一次只读配置（不扫插件目录，无额外 IO 压力）。
                _autoMatcher.ReadConf();
                _pluginRunner = new PluginRunner(Root, null, 3000, s => Log("  " + s));

                // ★ waitWhenShown 接线（auto.conf:29「显示窗口时是否等待命令结束」）
                //   执行器只看 t.Show 决定窗口显不显示；"显示之后等不等它跑完"由这里给。
                //   默认 true = 与既有行为完全一致（显示即等待，cmd 窗口留存可看输出）。
                //   （此前该键被读入却无消费者 = 死配置，守门 AC-6 打回项）
                _executor.WaitWhenShown = (_autoMatcher.Conf.WaitWhenShown != 0);

                Log("采集器可用 = " + _capture.IsAvailable());

                // ★ 时机①（AC-5①）：开机后台扫一遍 —— **后台线程**，绝不进热键回调（I-5）。
                //   语义按契约 §4-1 = 「新增条目名 + 失效条目重解析」（**增量**，不是全量重查）：
                //   已缓存且文件仍存在的条目走 skipped，只有新增名与"缓存指向的文件已不在"的名
                //   （失效 = 软件移位/升级换目录）才真去解析。热键弹出时缓存可能是"半熟的"，
                //   这与 D4（缺件隐藏按缓存判定）一致，可接受。
                StartBackgroundRefresh(false);
                return true;
            }
            catch (Exception ex)
            {
                Log("初始化失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 并行采集任务（Init 时预热）—— ★ 一次性，消费即失效。
        ///
        /// 背景（2026-10-03 实机 bug）：常驻模式下热键是"同一个进程反复触发"，
        /// 而预热只代表"进程启动那一刻的窗口/选中内容"。原实现成功返回后未置空，
        /// 于是每次热键都复用启动时的陈旧上下文（日志实证：同一常驻进程 13 秒后
        /// 两次触发的 proc 完全相同、text 恒为 0B）⇒ 选中文本永远失效、动态菜单
        /// 永远走 fallback。修正：① 消费即置空 ② 超过 PrewarmValidMs 视为过期。
        /// </summary>
        private System.Threading.Tasks.Task<Context> _captureTask;

        /// <summary>预热采集的启动时刻（Stopwatch 时间戳）。</summary>
        private long _captureTicks;

        /// <summary>预热结果的有效窗口（ms）：常驻模式下超过它就重采。</summary>
        private const int PrewarmValidMs = 2000;

        /// <summary>
        /// 目录准备（幂等）+ 首次启动自举配置模板。
        ///
        /// ★ 2026-10-03 补齐（对照 AHK `RunEver.ahk:1088-1112`）：原来只建空目录，
        ///   干净机器上 config\menus.conf 不存在 ⇒ 解析出 0 个分组、菜单空壳；
        ///   AHK 版用 FileInstall 把模板编译进 exe，C# 版漏了这一环。
        ///   现在交给 ConfigBootstrap（模板随产物发布在 templates\，缺失即释放）。
        /// </summary>
        private void EnsureLayout()
        {
            ConfigBootstrap.Ensure(Root, Log);
        }

        /// <summary>
        /// 取采集结果：优先用预热结果（仅当新鲜且未被消费），否则实时采一次。
        ///
        /// ★ 一次性语义（2026-10-03 修 bug）：无论走哪条路，预热槽都会被清空——
        ///   常驻模式下"采一次用一辈子"是错的（选中文本必须按键那一刻采）。
        ///   一次性模式（--show-menu）不受影响：进程刚起，预热必然新鲜。
        /// </summary>
        public Context WaitCapture()
        {
            var task = _captureTask;
            if (task != null)
            {
                _captureTask = null;          // ★ 消费即失效（防常驻模式复用陈旧采集）

                long ageMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _captureTicks)
                             * 1000L / System.Diagnostics.Stopwatch.Frequency;

                if (ageMs > PrewarmValidMs)
                {
                    Log("  ⚠ 预热采集已过期（" + ageMs + "ms > " + PrewarmValidMs
                        + "ms）→ 改为实时采集");
                }
                else
                {
                    try
                    {
                        if (task.Wait(2000))
                        {
                            Log("  · 用预热采集结果（预热后 " + ageMs + "ms）");
                            return task.Result;
                        }
                        Log("  ⚠ 采集等待超时，用空上下文继续");
                    }
                    catch (Exception ex)
                    {
                        Log("  ⚠ 并行采集异常: " + ex.Message);
                    }
                }
            }
            // ★ 2026-11-07：采集唯一入口 —— 常驻 AHK 管道优先，失败退回一次性进程。
            return CaptureOnce();
        }

        /// <summary>
        /// 采一次（**唯一**采集入口，预热与实时都走这里）。
        /// 常驻管道优先（~3ms）；常驻起不来/刚崩 ⇒ 退回一次性进程（~396ms），
        /// 不因为常驻故障就把菜单打空。
        /// </summary>
        private Context CaptureOnce()
        {
            if (_captureResident != null && _captureResident.IsAvailable())
            {
                var c = _captureResident.Capture(s => Log("  " + s));
                if (_captureResident.IsRunning) return c;
                Log("  ⚠ 常驻采集不可用 → 退回一次性采集进程");
            }
            return _capture.Capture(s => Log("  " + s));
        }

        // ============================================================
        // ★ 段 1.7：配置级热加载（热键路径的**唯一**配置检查点）
        //
        // 覆盖两条路径（目标卡 §背景）：
        //   ① 设置界面保存（走 OnConfigSaved / OnAutoConfSaved 的快路径，零 stat）；
        //   ② 外部编辑 / WebDAV 覆盖（**只能**靠这里的 stat 发现）。
        //
        // 成本（I-5）：2 次 `FileInfo` stat；两个文件都未变 ⇒ **0 解析、0 读盘**。
        //   禁止：插件重扫 / PathCache 重建 / 重建 AutoMatcher·IconResolver·Executor。
        // ============================================================

        /// <summary>本轮热键流程取到的 hidemissing（判定层用）。</summary>
        private bool _liveHideMissing;

        /// <summary>本轮热键流程取到的 waitwhenshown（执行器镜像字段的目标值，D-2v）。</summary>
        private bool _liveWaitWhenShown = true;

        /// <summary>
        /// 本轮热键流程取到的 menuOrder（三段顺序，永远非空）。
        /// 与 hideMissing 同一份 stat 快照取走 ⇒ 设置页改完，下一次弹菜单即按新顺序。
        /// </summary>
        private string[] _liveMenuOrder = MenuOrderSpec.Default;

        /// <summary>
        /// 热键路径的配置检查：menus.conf 变了 ⇒ 整体换代；auto.conf 变了 ⇒ 刷新开关。
        /// ★ 必须在判定层之前调用（`ShowMenuFlow` 流程入口），且**只在此处**允许替换 `_parser`
        ///   （难点① A4：A1→A4 之间替换会造成"判定用新表、图标/派发用旧对象"）。
        ///
        /// ★ `hotkey` 是**注册期键**，本检查**不处理**它：重注册只在 `TrayApp.ApplyHotkey()`
        ///   两条调用链上发生（设置窗口关闭 `TrayApp.cs:428` / 托盘「重载配置」`:450`）⇒
        ///   「改触发键需关设置窗或走托盘『重载配置』」，外部编辑 `auto.conf` 不会自动重注册。
        ///   `hotkey` 也**不进** `AutoConf`（避免"文件 vs 内存"两条读取路径）。
        /// </summary>
        public void HotReloadCheck()
        {
            // ---- ① menus.conf：整体换代（解析失败 / 文件暂缺 ⇒ 保留旧表，不清空）----
            if (_menuWatcher != null)
            {
                try
                {
                    ConfParser fresh;
                    if (_menuWatcher.TryReloadIfChanged(out fresh))
                    {
                        _parser = fresh;          // ★ 只能整体替换（禁止部分替换 / 就地改 Groups）
                        Log("menus.conf 热加载: 分组数=" + fresh.Groups.Count
                            + "（全新实例，旧实例交 GC；下次判定即用新表）");
                    }
                }
                catch (Exception ex)
                {
                    // 兜底：绝不能因配置检查炸掉热键
                    Log("menus.conf 热加载检查异常（沿用旧表）: " + ex.Message);
                }
            }

            // ---- ② auto.conf：一次 stat 取显示类开关快照 ----
            _liveHideMissing = false;
            _liveWaitWhenShown = true;
            _liveMenuOrder = MenuOrderSpec.Default;
            if (_autoMatcher != null)
            {
                try
                {
                    var s = _autoMatcher.LiveShowConf();
                    _liveHideMissing = (s.HideMissing != 0);
                    _liveWaitWhenShown = (s.WaitWhenShown != 0);
                    _liveMenuOrder = s.MenuOrder ?? MenuOrderSpec.Default;
                }
                catch (Exception ex)
                {
                    Log("auto.conf 热加载检查异常（沿用旧值）: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// menus.conf **重读的单点收口**（难点① A6 的既有范式抽出来）：
        /// `new ConfParser` → `Parse()` → 整体换引用；并同步版本戳（下一次热键不白解析）。
        /// 解析失败 ⇒ 保留旧表（不清空）。返回是否成功。
        /// </summary>
        public bool ReloadMenus()
        {
            try
            {
                var p = new ConfParser(Path.Combine(Root, "config", "menus.conf"));
                p.Parse();
                _parser = p;                                  // ★ 整体换代
                if (_menuWatcher != null) _menuWatcher.AcceptBaseline(p);
                Log("配置重读 OK，分组数 = " + p.Groups.Count);
                return true;
            }
            catch (Exception ex)
            {
                Log("配置重读失败（保留旧分组表）: " + ex.Message);
                return false;
            }
        }

        // ============================================================
        // 核心流程：采集 → 匹配 → 渲染 → 显示 → 执行
        // 返回值：被点条目 ID（0 = 未选）
        // ============================================================
        public int ShowMenuFlow()
        {
            // ★ 分阶段计时：定位"弹出慢"到底在哪一段
            //   （总耗时 = 进程启动 + 配置解析 + 采集 + 匹配 + 图标 + 渲染 + 显示）
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            // ★ 段 1.7：配置热加载检查（流程**入口**前 —— 难点① A4 纪律：替换只允许发生在
            //   A1→A4 之间之外）。成本 = 2 次 stat（menus.conf + auto.conf），未变 ⇒ 0 解析 / 0 读盘。
            //   ⚠ 边界（如实标注）：本检查在 WaitCapture 之前 ⇒ 采集期间（实测 ~250ms，
            //     见 :295-300 的耗时分段）发生的改动落到**下一轮**热键，不在本轮。
            HotReloadCheck();

            var ctx = WaitCapture();
            Log("采集: text=" + ctx.Text.Length + "B files=" + ctx.Files.Count
                + " proc=[" + ctx.Proc + "]");
            long tCapture = swTotal.ElapsedMilliseconds;

            // ★ 1.5 步 自动执行（匹配块）：选中文本命中插件 → 直接派发，跳过弹菜单
            //   对照 AHK `RunEver.ahk:564-577`。
            //   ★★ 必须复用【同一次采集结果】ctx —— WaitCapture 是一次性语义，
            //      再调一次会实时重采 ⇒ 菜单与自动执行看到不同上下文。
            //   三层兜底（AHK :624-627）：无命中 / 产出空 target / 派发异常
            //      → 一律回落到下面的正常链路（零回归）。
            try
            {
                if (TryAutoExec(ctx))
                {
                    Log("自动执行命中 → 已派发，跳过弹菜单");
                    Log("菜单流程总耗时(不含用户等待) = "
                        + swTotal.ElapsedMilliseconds + "ms（自动执行）");
                    return 0;             // 0 = 未弹菜单（与"用户没选"同语义）
                }
            }
            catch (Exception ex)
            {
                // ★ 异常必须吞掉并记日志：自动执行绝不能炸掉热键
                //   （否则用户表现为"按了热键没反应"，排查成本极高）
                Log("自动执行异常（回落弹菜单）: " + ex.Message);
            }

            // ============================================================
            // 判定层（契约 §5）：分类 → 缺件位注入 → MatchEngine.Decide
            //   ★ 零 IO 边界：判定层（MatchEngine/MatchContext/MenuDecision）不读盘；
            //     分类与缺件判定都在这两行之前、由 IO 允许的层算成**值**注入。
            // ============================================================
            var mctx = SelectionClassifier.FromCaptured(ctx);

            // 缺件位（D4：按缓存判定，不查 EV）——一次性注入
            // ★ 段 1.6 B2：这里**不再读 `_autoMatcher.Conf.HideMissing` 这份可能很旧的内存态**
            //   （托盘进程常驻，Conf 只在 Load 里刷新，而 Load 的唯一非强制调用点在
            //   TryAutoExec 内、没选中文本时提前 return）。改成热键期实时取值：
            //   stat 未变 ⇒ 零读盘；变了 ⇒ 重读一次 auto.conf（不扫插件、不重建匹配器）。
            // ★ 段 1.7：改成一次 stat 取"显示类开关快照"（hideMissing + waitWhenShown）——
            //   取值在流程入口的 HotReloadCheck()（见上），这里只消费。
            bool hideMissing = _liveHideMissing;

            // ★ 段 1.7 / D-2v：`waitWhenShown` 的消费点是执行器镜像字段（**不在**判定链上）。
            //   只有**值真的变了**才写（避免每轮热键都动共享字段）。
            if (_executor != null && _executor.WaitWhenShown != _liveWaitWhenShown)
            {
                _executor.WaitWhenShown = _liveWaitWhenShown;
                Log("waitWhenShown 实时同步（热键期）: " + (_liveWaitWhenShown ? "1" : "0"));
            }

            // ★ 难点① A1/A2/A3：**同一份快照**（本流程内不再直读 `_parser.Groups` 多次）
            var confSnap = _parser.Groups;

            // ★ 唯一收口：A1（缺件注入）+ A3（判定）共用 confSnap —— 下标口径必然一致
            var hot = MenuDecisionPipeline.InjectAndDecide(mctx, confSnap, _cache, hideMissing,
                s => Log("  " + s));
            if (hot.HideMissing)
                Log("缺件判定: 条目=" + hot.ItemTotal
                    + " 隐藏=" + hot.HiddenCount + "（hideMissing=1）");

            var decision = hot.Decision;
            // ★ 2026-11-08：显示顺序由 auto.conf 的 menuOrder 决定（三段：dynamic/process/always；兜底在 dynamic 格内），
            //   取值走热键期快照（与 hideMissing 同一次 stat）⇒ 设置页改完下一次弹菜单就生效。
            var groups = decision.Flat(_liveMenuOrder);
            Log("判定: " + decision.Rule
                + " 命中分组=" + groups.Count
                + " 顺序=" + MenuOrderSpec.ToConf(_liveMenuOrder)
                + " always=" + decision.AlwaysCount
                + " dynamic=" + decision.DynamicCount
                + " process=" + decision.ProcessCount
                + " fallback=" + (decision.FallbackShown ? 1 : 0)
                + " 违规=" + decision.Violations.Count);
            long tMatch = swTotal.ElapsedMilliseconds;

            // ★ U-A 一键直达（仅扩展名槽位，2026-11-09）：
            //   结论位在 L1 算好（`MenuDecision.OneShot`），这里只做两件事 ——
            //   ① 读 auto.conf 的 oneShot 开关（关 ⇒ null ⇒ 完全走原链路）
            //   ② 命中就直接派发并 return（**不建菜单**：MenuBuilder / NativeMenu 零感知）。
            var oneShot = decision.OneShotFor(_autoMatcher != null ? _autoMatcher.Conf : null);
            if (oneShot != null)
            {
                Log("一键直达: " + oneShot.Title);
                Dispatch(oneShot, ctx);
                Log("菜单流程总耗时(不含用户等待) = "
                    + swTotal.ElapsedMilliseconds + "ms（一键直达）");
                return 0;
            }

            // 图标解析
            _icons.ResolveAll(groups);
            long tIcon = swTotal.ElapsedMilliseconds;

            // ★ 2026-11-08：菜单**固定走 Win32**（TrackPopupMenu）。
            //   WPF 菜单后端整套撤掉：① 条目快捷键只有系统助记（`&X`）才跟手，WPF 那边
            //   `InputGestureText` 只是显示、真按键得靠 `_` 助记 + 自写 PreviewKeyDown 才追平；
            //   ② 托盘进程不再加载 WPF 菜单栈（Application + MahApps 主题字典 ≈ 60MB 首解析）；
            //   ③ WPF 只留给设置窗口（独立进程 `--settings`，用完即退）。
            var built = MenuBuilder.Build(groups);
            Log("菜单渲染: 顶层项数=" + built.Menu.Count()
                + " 可点条目=" + built.ItemCount
                + " 图标命中=" + built.IconHit + " 兜底=" + built.IconFallback
                + " | 构建=" + MenuBuilder.LastBuildMs + "ms"
                + "（条目=" + MenuBuilder.LastItemMs + " 图标=" + MenuBuilder.LastIconMs + "）");

            // ★ F1：缺件隐藏后的空子菜单收尾 —— 一次弹菜单输出一行统计（无剪也输出 =0），
            //   被剪标题单独一行便于排障。两个来源不同层：判定层剪枝（数据）+ 渲染护栏（最后一道闸）。
            int prunedTrees = SubtreePruner.LastPrunedTotal + MenuBuilder.LastPrunedSubtrees;
            Log("剪空子树=" + prunedTrees);
            var prunedTitles = new List<string>(SubtreePruner.LastPrunedTitles);
            prunedTitles.AddRange(MenuBuilder.LastPrunedTitles);
            if (prunedTitles.Count > 0)
                Log("空子菜单已剪: " + string.Join(",", prunedTitles.ToArray()));
            SavePruneStat(hot.HiddenCount, prunedTrees);

            long tBuild = swTotal.ElapsedMilliseconds;

            // ★ B4a-2（P0 体验）：**全空菜单必须解释**。
            //   原来 `ItemCount==0` 时照样弹一个空菜单 ⇒ 用户按热键"什么都没出现"（最坏体验）。
            //   这里两步兜底：① 托盘气泡说清最可能的原因与关掉它的路径 ② 菜单里塞一条
            //   **禁用占位项**再弹（看得见为什么空，但点不动）。
            //   ⚠️ 不动 MenuBuilder「跳过空分组」的既有语义（那是对的），只在"全空"这一层兜底。
            if (built.ItemCount == 0)
            {
                int emptyGroups = 0;
                if (groups != null)
                    foreach (var g in groups)
                        if (g == null || g.Items == null || g.Items.Count == 0) emptyGroups++;

                Log("菜单为空：可点条目=0 ⇒ 气泡解释 + 禁用占位项"
                    + "（分组=" + (groups == null ? 0 : groups.Count)
                    + " 空分组=" + emptyGroups
                    + " hideMissing=" + (hideMissing ? 1 : 0)
                    + " 隐藏=" + hot.HiddenCount + " 候选条目=" + hot.ItemTotal + "）");

                NotifyUser("菜单是空的",
                    "菜单里没有可显示的条目 —— 可能被『隐藏本机没有的条目』全部隐藏了，"
                    + "可在 设置 → 触发与行为 关掉它");

                built.Menu.AddItemDisabled("（没有可显示的条目）");
            }

            int selId = 0;
            // ★ 用纯 Win32 owner 窗口（不用 WPF）—— 实测 WPF Window.Show() 要 259ms
            //   初始化整个 WPF 栈，是"菜单弹出慢"的最大头；纯 Win32 建窗接近 0 开销。
            using (var owner = new OwnerWindow())
            {
                Log("owner: " + owner.Describe());
                if (!owner.IsVisible())
                    Log("⚠ owner 不可见！菜单会失焦不消失，请检查 OwnerWindow 是否误调 SW_HIDE");
                long tOwner = swTotal.ElapsedMilliseconds;

                LogVerbose("★ 耗时明细(ms): 采集=" + tCapture
                    + " 匹配=" + (tMatch - tCapture)
                    + " 图标=" + (tIcon - tMatch)
                    + " 渲染=" + (tBuild - tIcon)
                    + " owner=" + (tOwner - tBuild)
                    + " | 菜单弹出前累计=" + tOwner);

                RunEverCs.Services.MemTrim.MarkBusy();
                selId = built.Menu.ShowAtCursor(owner.Handle);
                // ★ 2026-10-06：焦点诊断（抢焦点 / 还焦）—— 实机验证快捷键问题就看这行
                Log("焦点: " + built.Menu.FocusDiag);
            }
            Log("用户选择 ID = " + selId + "（等待 " + (swTotal.ElapsedMilliseconds - tBuild) + "ms）");

            if (selId > 0 && built.IdMap.ContainsKey(selId))
            {
                var item = built.IdMap[selId];
                Log("点击: " + item.Title + " | " + item.Type.ToString().ToLowerInvariant()
                    + " " + item.Target);
                Dispatch(item, ctx);
            }
            else if (selId > 0)
            {
                // ★ ID 找不到 = 分配冲突（历史 bug：点任何项都弹计算器）
                Log("⚠ 选择 ID=" + selId + " 在 idmap 中不存在！"
                    + " idmap 键=" + string.Join(",", built.IdMap.Keys));
            }

            built.Menu.Dispose();
            ScheduleMemTrim();
            Log("菜单流程总耗时(不含用户等待) = " + tBuild + "ms");
            return selId;
        }

        /// <summary>
        /// 菜单关闭后按 `auto.conf` 的 `memTrimIdle` 安排一次内存整理（2026-11-08）。
        /// 0 / 未配置 ⇒ 不做（回到"一直常驻"的旧行为）。异常一律吞掉：绝不因整理炸掉热键链。
        /// </summary>
        private void ScheduleMemTrim()
        {
            try
            {
                int idle = (_autoMatcher != null && _autoMatcher.Conf != null) ? _autoMatcher.Conf.MemTrimIdle : 0;
                RunEverCs.Services.MemTrim.Schedule(idle);
            }
            catch { }
        }

        // ============================================================
        // 解析三时机的**单一入口**（AC-5 与 AC-8 的交界）
        //   时机① 开机扫 / 时机② 配置保存后增量 / 托盘「刷新路径缓存」全部经此，
        //   且**就地回灌运行实例**（旧缺口：刷新新建 PathCache 实例落盘 ⇒ 运行期看不见）。
        //   全部在后台线程；热键回调内绝不调用（I-5）。
        // ============================================================
        /// <summary>后台解析（force = 全量 / 增量）；跑完就地回灌并通知依赖方。</summary>
        public void StartBackgroundRefresh(bool force)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _refreshBusy, 1, 0) != 0)
            {
                Log("后台解析已在运行，跳过本次请求（force=" + force + "）");
                return;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Log("后台解析开始（" + (force ? "全量 / 开机扫" : "增量 / 保存后") + "）");
                    var st = PathResolvePolicy.BuildInto(Root, _cache, force,
                        s => Log("  " + s), (ISearchBackend)null);
                    Log("后台解析完成（就地回灌同一 PathCache 实例）：" + st);
                    PublishCacheReloaded();
                }
                catch (Exception ex)
                {
                    Log("后台解析失败: " + ex.Message);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _refreshBusy, 0);
                }
            });
        }

        /// <summary>
        /// 缓存回灌的**唯一通知点**：解析写盘后重读一次做对账，再放行依赖方。
        /// ★ 为什么不用"替换引用"：解析是**就地**写进运行实例那份 PathCache
        ///   （PathCacheBuilder 的 Set + 原子 Save 都在同一实例上），
        ///   于是图标解析器与执行器（持有同一引用）零改造即可见 —— 这就是
        ///   "就地重载 + 通知依赖"的落地形态。
        /// </summary>
        private void PublishCacheReloaded()
        {
            try
            {
                _cache.Reload();
                Log("缓存回灌完成：条目数=" + _cache.Count + "（图标/执行器共用同一实例，无需重建）");
            }
            catch (Exception ex)
            {
                Log("缓存回灌失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 托盘「刷新路径缓存」的入口（TrayApp 调用）。内部走 `PathCacheRefresh.Run`
        /// —— 与 CLI `--refresh-cache` **同一份实现**，并把结果就地回灌运行实例。
        /// ★ 调用方负责放在后台线程（托盘现有实现已经是后台线程）。
        /// </summary>
        public PathCacheRefresh.Result RefreshPathCache()
        {
            var r = PathCacheRefresh.Run(Root, s => Log("  " + s), _cache);
            PublishCacheReloaded();
            return r;
        }

        /// <summary>
        /// 时机②：配置保存后的**增量解析**（编辑器保存回调触发；不阻塞保存 UI）。
        /// 契约 §4-2：没有这一步，「按缓存隐藏」（D4）会把刚加进来的条目直接隐藏 ⇒ 死角。
        /// </summary>
        public void OnConfigSaved()
        {
            Log("配置已保存 ⇒ 触发时机② 增量解析");
            // ★ 段 1.7：重读收口到 ReloadMenus()（与 HotReloadCheck 共用同一份实现，
            //   并同步 MenuConfWatcher 的版本戳 ⇒ 下一次热键不会对同一份文件再解析一次）
            ReloadMenus();
            StartBackgroundRefresh(false);
        }

        /// <summary>
        /// 段 1.6 B2：托盘收到「常规设置（auto.conf）保存成功」通知后的动作。
        ///
        /// 只做两件轻事（不重扫插件目录、不碰 menus.conf）：
        ///   ① 重读 auto.conf ⇒ 显示类开关（hideMissing / showByDefault / waitWhenShown …）
        ///      立即变成保存后的值；
        ///   ② 把 waitWhenShown 这类"开了就别等第二轮"的消费点同步过来。
        /// 与 `OnConfigSaved`（菜单页/配置保存后的增量解析）分开：常规页是"改一项存一项"，
        /// 每动一下控件都拖着跑一遍 menus.conf 解析是无谓开销。
        ///
        /// ★ 段 1.7：它现在**只是一条快路径腿**（设置界面保存时零 stat 立即同步），
        ///   **不再是唯一腿** —— 外部编辑 / WebDAV 覆盖由热键期的 `HotReloadCheck()` 兜住。
        ///   两条腿最终都落到同一份内存态（`AutoMatcher.Conf` + `Executor.WaitWhenShown`）。
        /// </summary>
        public void OnAutoConfSaved()
        {
            try
            {
                if (_autoMatcher == null) return;
                _autoMatcher.ReloadConf();
                if (_executor != null)
                    _executor.WaitWhenShown = (_autoMatcher.Conf.WaitWhenShown != 0);

                Log("常规设置已保存 ⇒ auto.conf 重载: hideMissing=" + _autoMatcher.Conf.HideMissing
                    + " showByDefault=" + _autoMatcher.Conf.ShowByDefault
                    + " waitWhenShown=" + _autoMatcher.Conf.WaitWhenShown
                    + " enabled=" + (_autoMatcher.Conf.Enabled ? 1 : 0)
                    + " minLen=" + _autoMatcher.Conf.MinLen
                    + " cache=" + _autoMatcher.Conf.CacheSec + "s");
            }
            catch (Exception ex)
            {
                Log("auto.conf 重载失败: " + ex.Message);
            }
        }

        /// <summary>时机③ 的现查钩子（执行期，**点击之后**；含硬超时与缓存回写）。</summary>
        private string ResolveOnDemandForRun(string progName)
        {
            var o = PathResolvePolicy.ResolveOnDemand(Root, progName, _cache, s => Log("  " + s));
            Log("现查结果: " + o);
            return o.State == ResolveState.Ok ? o.Path : "";
        }

        /// <summary>在途现查派发计数（>0 ⇒ 有执行仍在 worker 线程上跑）。</summary>
        private int _pendingWorkers;

        /// <summary>在途派发清零时置位（一次性进程的硬退出点等它）。</summary>
        private readonly System.Threading.ManualResetEvent _noPendingWorkers =
            new System.Threading.ManualResetEvent(true);

        /// <summary>
        /// 等"在途现查派发"收尾 —— **只给一次性进程的硬退出点用**
        /// （`Program.cs:387 Environment.Exit(0)` 会立刻杀掉所有线程，含 worker）。
        /// 不等就会退化成用户侧"点了条目，程序没启动"。
        /// ★ 常驻托盘路径**不调用**它（那里正是不能阻塞消息线程的地方）。
        /// 返回 true = 已无在途派发。
        /// </summary>
        public bool WaitPendingDispatch(int timeoutMs)
        {
            if (System.Threading.Volatile.Read(ref _pendingWorkers) <= 0) return true;
            Log("退出前等待在途现查派发收尾（上限 " + timeoutMs + "ms）");
            return _noPendingWorkers.WaitOne(timeoutMs);
        }

        /// <summary>
        /// ★ I-5（热键路径禁阻塞）：可能走「时机③ 现查」的执行，把 **现查 + 启动**
        /// 整体移交 worker 线程 —— 消息线程（托盘 `WM_HOTKEY` → `TrayApp.InvokeMenu`
        /// → `ShowMenuFlow`：① 点击分支 `Dispatch` ② 自动执行分支 `DispatchAuto`）
        /// 只做一次只读预判（`Executor.NeedsOnDemandResolve`）后**立即返回**。
        ///
        ///   · 现查硬超时（`PathResolvePolicy.OnDemandTimeoutMs=1200ms`）留在 worker 内部；
        ///   · 命中 ⇒ worker 内启动进程；未命中/超时 ⇒ worker 内 `Fail` ⇒ 托盘气泡
        ///     （`TrayApp.TrayTipThreadSafe` 已做跨线程派发，worker 上可安全弹）；
        ///   · 消息线程**不再** `task.Wait` ⇒ `_menuBusy` 不因现查被占用、第二次热键不丢。
        ///
        /// 返回 true = 已受理（执行在 worker 上继续，调用方不要再同步执行一遍）。
        /// </summary>
        private bool DispatchOnWorkerIfNeeded(Executor.Task t)
        {
            if (!_executor.NeedsOnDemandResolve(t)) return false;

            System.Threading.Interlocked.Increment(ref _pendingWorkers);
            _noPendingWorkers.Reset();
            var worker = new System.Threading.Thread(delegate ()
            {
                try { _executor.Execute(t); }
                catch (Exception ex) { Log("现查 worker 执行异常: " + ex.Message); }
                finally
                {
                    if (System.Threading.Interlocked.Decrement(ref _pendingWorkers) <= 0)
                        _noPendingWorkers.Set();
                }
            });
            worker.Name = "rve-ondemand-dispatch";
            // ★ 前台线程（非后台）只是二道保险：真正的保证是 `Program.cs:383-388`
            //   的 `--show-menu` 硬退出点会先 `WaitPendingDispatch` 再 `Environment.Exit`。
            worker.IsBackground = false;
            Log("现查分支：现查 + 启动移交 worker 线程（消息线程不等待）");
            worker.Start();
            return true;
        }

        // ============================================================
        // 派发到执行器
        // ============================================================
        public void Dispatch(MenuItem item, Context ctx)
        {
            var t = new Executor.Task
            {
                Type = item.Type.ToString().ToLowerInvariant(),
                Target = item.Target,
                Flag = item.Flag,
                Text = ctx.Text ?? "",
                Files = ctx.Files ?? new List<string>(),
                Proc = ctx.Proc ?? "",
                WinTitle = ctx.WinTitle ?? "",
                Clipboard = ctx.Clipboard ?? "",
                Log = s => Log("  " + s)
            };
            try
            {
                // ★ I-5：现查分支整体移交 worker（消息线程不 task.Wait）—— 见 DispatchOnWorkerIfNeeded
                if (DispatchOnWorkerIfNeeded(t)) return;
                _executor.Execute(t);
            }
            catch (Exception ex)
            {
                Log("派发失败: " + ex.Message);
            }
        }

        // ============================================================
        // ★ 自动执行（匹配块）· 2026-10-03 定案，对照 AHK `RunEver.ahk:618-673`
        //
        // 流程：选中文本 → 匹配插件 → 产出目标 → 复用执行器派发
        // 返回：true = 已派发（调用方应跳过弹菜单）；false = 未命中/放弃（走正常链路）
        //
        // ★ 三层兜底（AHK `:624-627`，一条都不能少）：
        //   ① 无规则命中      → false → 弹菜单
        //   ② 脚本产出空 target → false → 弹菜单
        //   ③ 派发失败/异常    → false → 弹菜单（异常吞掉并记日志）
        // ============================================================
        public bool TryAutoExec(Context ctx)
        {
            if (_autoMatcher == null) return false;

            string text = (ctx != null && ctx.Text != null)
                ? ctx.Text.Trim(' ', '\t', '\r', '\n') : "";
            if (text.Length == 0) return false;

            // 缓存过期才真扫插件目录（热键路径零多余 IO —— 见 AutoMatcher.Load）
            _autoMatcher.Load(false);

            // ---- 匹配 ----
            var r = _autoMatcher.Match(text);
            if (!r.Hit) return false;                       // 兜底①

            string target = r.Target;

            // ---- 形态 B：无 Tpl ⇒ 由插件脚本产出目标 ----
            if (string.IsNullOrEmpty(target))
            {
                target = (_pluginRunner != null) ? _pluginRunner.Run(r.Plugin, text) : "";
                if (string.IsNullOrEmpty(target))
                {
                    Log("  自动执行: 插件 " + r.Plugin.Name + " 未产出目标 → 回落弹菜单");
                    return false;                           // 兜底②
                }
            }

            Log("  自动执行命中: " + r.Plugin.Name + " (prio=" + r.Plugin.Prio + ") out=" + r.Out
                + " target=" + (target.Length > 60 ? target.Substring(0, 60) + "..." : target));

            // ---- 派发：复用执行器任务契约（对照 AHK 里的 item Map）----
            try
            {
                if (!DispatchAuto(r, target, ctx))
                {
                    Log("  自动执行派发失败（回落弹菜单）");
                    return false;                           // 兜底③
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("  自动执行派发失败（回落弹菜单）: " + ex.Message);
                return false;                               // 兜底③
            }
        }

        /// <summary>
        /// 自动执行派发 —— 构造 Executor.Task 并执行。
        ///
        /// ★ show 语义：AutoMatcher 已把 RVE_FN_Show 三态（-1 继承 / 0 隐藏 / 1 显示）
        ///   解析成 0/1，这里映射到 `Executor.Task.Show`（null = 未指定 → 隐藏）。
        ///   显示时执行器用 RunWait（对照 `src\RunEverExec.ahk:248-277`），
        ///   等待只阻塞执行器自己，不违反主程序零阻塞。
        /// ★ 不设 Flag：条目级 flag 只存在于 menus.conf 条目上，插件路径没有它。
        ///
        /// ★ I-5（热键路径禁阻塞）：本次若可能走时机③ 现查（缓存命中但文件不存在），
        ///   则**现查 + 启动**移交 worker 并立即返回 true（已受理）—— 消息线程不再
        ///   `task.Wait(≤1200ms)`，弹菜单之前也不阻塞。此分支下的失败出口由
        ///   `Executor.Fail → ErrorNotify → 托盘气泡` 承担（不再回落弹菜单：
        ///   失败在 worker 上异步到达时，菜单早已按"已受理"语义跳过）。
        /// </summary>
        public bool DispatchAuto(AutoMatchResult r, string target, Context ctx)
        {
            var t = new Executor.Task
            {
                Type = (r.Out ?? "").ToLowerInvariant(),
                Target = target,
                Flag = ActionFlag.None,
                Show = (r.Show == 1),
                Text = ctx.Text ?? "",
                Files = ctx.Files ?? new List<string>(),
                Proc = ctx.Proc ?? "",
                WinTitle = ctx.WinTitle ?? "",
                Clipboard = ctx.Clipboard ?? "",
                Log = s => Log("  " + s)
            };
            if (DispatchOnWorkerIfNeeded(t)) return true;   // 已受理（现查 + 启动在 worker 上）
            return _executor.Execute(t);
        }

        // ============================================================
        // 自检（与 AHK 版 --selftest 对齐）
        // ============================================================
        public string SelfTest()
        {
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine("=== RunEver C# SELFTEST ===");
                sb.AppendLine("root = " + Root);

                if (_parser == null || _parser.Groups.Count == 0)
                {
                    sb.AppendLine("[FAIL] 配置未加载");
                    return sb.ToString();
                }
                sb.AppendLine("[OK]   配置解析  分组=" + _parser.Groups.Count);

                int itemCount = 0, groupHeaders = 0;
                foreach (var g in _parser.Groups)
                    foreach (var it in g.Items)
                    {
                        itemCount++;
                        if (it.IsGroupHeader) groupHeaders++;
                    }
                sb.AppendLine("[OK]   条目统计  总数=" + itemCount + " 分组标题=" + groupHeaders);

                sb.AppendLine("[OK]   路径缓存  条目=" + _cache.Count);

                // 四种上下文各测一次判定（判定层 = MatchEngine，纯函数零 IO）
                // ★ 守门 Z-2：**真比对**（不是"只打印就 [OK]"）。期望值从配置**独立推导**
                //   （不把实际值抄进期望）：判定层错乱 / 配置错 ⇒ 输出 [FAIL] ⇒ 总自检 exit≠0。
                bool hasFallback = false, hasText = false;
                var extGrps = new List<MenuGroup>();
                var procGrps = new List<MenuGroup>();
                foreach (var g in _parser.Groups)
                {
                    if (g.Type == GroupType.Fallback) hasFallback = true;
                    else if (g.Type == GroupType.Text) hasText = true;
                    else if (g.Type == GroupType.Ext) extGrps.Add(g);
                    else if (g.Type == GroupType.Proc) procGrps.Add(g);
                }
                // 期望规则由配置独立推导（契约 §5 R1–R6 口径）
                int pdfHits = MatchEngine.MatchExtGroups(extGrps, new List<string> { "pdf" }).Count;
                string pdfRule = pdfHits == 1 ? "R2" : (pdfHits >= 2 ? "R3" : "R2-0命中");
                int chromeHits = MatchEngine.MatchProcGroups(procGrps, "chrome.exe").Count;
                int fallbackDyn = hasFallback ? 1 : 0;

                var cases = new[]
                {
                    new { Name="空上下文",   Text="",      Files=new List<string>(),                      Proc="",
                          Rule="R6", Dyn=fallbackDyn, Fallback=true,  ProcCount=0,
                          DynType=(GroupType?)GroupType.Fallback },
                    new { Name="选中文本",   Text="hello", Files=new List<string>(),                      Proc="",
                          Rule="R1", Dyn=(hasText ? 1 : 0), Fallback=!hasText, ProcCount=0,
                          DynType=(GroupType?)GroupType.Text },
                    new { Name="选中PDF",    Text="",      Files=new List<string>{@"C:\a\b.pdf"},          Proc="",
                          Rule=pdfRule, Dyn=(pdfHits == 1 ? 1 : fallbackDyn), Fallback=(pdfHits != 1), ProcCount=0,
                          DynType=(GroupType?)(pdfHits == 1 ? GroupType.Ext : GroupType.Fallback) },
                    new { Name="浏览器进程", Text="",      Files=new List<string>(),                      Proc="chrome.exe",
                          Rule="R6", Dyn=fallbackDyn, Fallback=true,  ProcCount=(chromeHits > 0 ? 1 : 0),
                          DynType=(GroupType?)GroupType.Fallback },
                };
                foreach (var c in cases)
                {
                    var mctx = SelectionClassifier.Build(c.Text, c.Files, c.Proc, "", "");
                    var d = MatchEngine.Decide(mctx, _parser.Groups);
                    var flat = d.Flat();
                    var names = new List<string>();
                    foreach (var g in flat) names.Add(g.Name);

                    var mismatch = new List<string>();
                    if (d.Rule != c.Rule) mismatch.Add("规则=" + d.Rule + "(期望 " + c.Rule + ")");
                    if (d.DynamicCount != c.Dyn) mismatch.Add("动态组数=" + d.DynamicCount + "(期望 " + c.Dyn + ")");
                    if (d.ProcessCount != c.ProcCount) mismatch.Add("进程组数=" + d.ProcessCount + "(期望 " + c.ProcCount + ")");
                    if (d.FallbackShown != c.Fallback) mismatch.Add("兜底=" + d.FallbackShown + "(期望 " + c.Fallback + ")");
                    // I-1：动态槽位**互斥**（至多一组）
                    if (d.DynamicCount > 1) mismatch.Add("动态槽位不互斥（" + d.DynamicCount + " 组）");
                    // 命中的动态组必须是该形态对应的槽位（R1⇒text / R2⇒ext / R3·R6⇒fallback）
                    if (d.DynamicCount == 1 && c.DynType.HasValue && d.Dynamic[0].Type != c.DynType.Value)
                        mismatch.Add("动态组类型=" + d.Dynamic[0].Type + "(期望 " + c.DynType.Value + ")");

                    string line = "判定 " + c.Name.PadRight(6)
                        + " 规则=" + d.Rule + " 分组=" + flat.Count
                        + " [" + string.Join(",", names) + "]";
                    if (mismatch.Count == 0)
                        sb.AppendLine("[OK]   " + line);
                    else
                        sb.AppendLine("[FAIL] " + line + "  <<< " + string.Join("；", mismatch.ToArray()));
                }

                // 菜单渲染断言
                var sample = new List<MenuGroup>();
                foreach (var g in _parser.Groups)
                    if (g.Type == GroupType.Always) sample.Add(g);
                if (sample.Count > 0)
                {
                    _icons.ResolveAll(sample);
                    sb.AppendLine(MenuBuilder.SelfTest(sample));
                }

                sb.AppendLine("[OK]   采集器    " + (_capture.IsAvailable() ? "可用" : "缺失"));
                sb.AppendLine(SelfTestCaptureFreshness());
                sb.AppendLine(SelfTestAutoExec());     // ★ 自动执行（匹配块）段
                sb.AppendLine("=== SELFTEST 通过 ===");
            }
            catch (Exception ex)
            {
                sb.AppendLine("[FAIL] 异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 回归防线（2026-10-03 常驻采集陈旧 bug）：连续两次取上下文 ⇒ 必须真采两次。
        /// 只断言"每次都去采了"，不断言内容（自检环境没有真实选中文本）。
        /// </summary>
        /// <summary>
        /// 当前生效后端累计的采集次数。
        /// ★ 2026-11-07：原来是直接读 `_capture.CaptureCount`（AHK 客户端）。
        ///   改成常驻后，常驻管道与一次性回退各记一份，这里取合计，
        ///   断言语义保持不变（第二次必须重采）。
        /// </summary>
        private int ActiveCaptureCount
        {
            get
            {
                if (_captureResident != null && _captureResident.IsAvailable())
                    return _captureResident.CaptureCount + (_capture == null ? 0 : _capture.CaptureCount);
                return _capture == null ? 0 : _capture.CaptureCount;
            }
        }

        private string SelfTestCaptureFreshness()
        {
            try
            {
                if (_captureResident == null || !_captureResident.IsAvailable())
                    return "[SKIP] 采集一次性语义  (采集器缺失)";

                int before = ActiveCaptureCount;
                WaitCapture();                 // ① 允许消费预热
                int after1 = ActiveCaptureCount;
                WaitCapture();                 // ② 必须重新采集（不许复用陈旧上下文）
                int after2 = ActiveCaptureCount;

                string backend = "ahk-常驻";
                if (after2 > after1)
                    return "[OK]   采集一次性语义  第二次调用确实重采（后端=" + backend
                        + "，本进程累计采集 " + (after2 - before) + " 次）";

                return "[FAIL] 采集一次性语义  第二次调用未重采（后端=" + backend
                    + "，计数仍为 " + after2 + "）⇒ 常驻模式会复用启动时的陈旧上下文，选中文本必失效";
            }
            catch (Exception ex)
            {
                return "[FAIL] 采集一次性语义 异常: " + ex.Message;
            }
        }

        /// <summary>
        /// 自动执行（匹配块）段断言 —— 对照 AHK `RunEver.ahk:403-441`（6d）。
        ///
        /// 用例表逐字照抄 AHK（`:411-416`），期望值不猜：
        ///   SF1234567890 → 顺丰快递查询 / https://a.com → 打开网址 /
        ///   choco install 7zip → 执行 Chocolatey 命令 / 普通文字啊啊啊 → 不命中
        ///
        /// ★ 与 AHK 的差异（有意，防误判）：AHK 那段还断言"启动登记文件"
        ///   （AHK 每次 Scan 会写 state 下的登记 JSON）。C# 侧没有该文件的消费者
        ///   （设置页直接读 PluginScanner.Plugins），故不生成也不断言。
        /// </summary>
        private string SelfTestAutoExec()
        {
            try
            {
                if (_autoMatcher == null)
                    return "[FAIL] 自动执行  未初始化";

                int n = _autoMatcher.Load(true);      // 强制重扫，拿到当前真实插件集
                var conf = _autoMatcher.Conf;
                var sb = new StringBuilder();
                sb.AppendLine("[OK]   自动执行  插件数=" + n
                    + " enabled=" + (conf.Enabled ? "1" : "0")
                    + " minLen=" + conf.MinLen
                    + " cache=" + conf.CacheSec + "s"
                    + " showByDefault=" + conf.ShowByDefault);

                if (n == 0)
                {
                    sb.AppendLine("[WARN] 自动执行  插件目录为空（config\\functions\\*.ahk）");
                    return sb.ToString().TrimEnd();
                }
                if (_autoMatcher.BadPlugins.Count > 0)
                    sb.AppendLine("[WARN] 自动执行  非法插件=" + _autoMatcher.BadPlugins.Count
                        + "（已跳过，见日志）");

                if (!conf.Enabled)
                {
                    sb.AppendLine("[SKIP] 自动执行用例  (auto.conf enabled=0，总开关已关)");
                    return sb.ToString().TrimEnd();
                }

                string[][] cases = {
                    new[] { "SF1234567890",       "顺丰快递查询" },
                    new[] { "https://a.com",      "打开网址" },
                    new[] { "choco install 7zip", "执行 Chocolatey 命令" },
                    new[] { "普通文字啊啊啊",       "" }
                };
                int caseFail = 0;
                foreach (var c in cases)
                {
                    var r = _autoMatcher.Match(c[0]);
                    string got = r.Hit ? r.Plugin.Name : "";
                    if (got != c[1])
                    {
                        sb.AppendLine("[FAIL] 自动执行  用例 [" + c[0] + "] 期望="
                            + (c[1] == "" ? "(不命中)" : c[1])
                            + " 实得=" + (got == "" ? "(不命中)" : got));
                        caseFail++;
                    }
                }
                sb.AppendLine(caseFail == 0
                    ? "[OK]   自动执行  " + cases.Length + " 个用例全通过 ✓"
                    : "[FAIL] 自动执行  用例失败 " + caseFail + " 个");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return "[FAIL] 自动执行: " + ex.Message;
            }
        }

        /// <summary>统计分组里的条目总数（缺件过滤前后对比用）。</summary>
        private static int CountItems(List<MenuGroup> groups)
        {
            int n = 0;
            if (groups == null) return 0;
            foreach (var g in groups)
                if (g.Items != null) n += g.Items.Count;
            return n;
        }

        /// <summary>
        /// 把最近一次弹菜单的「缺件隐藏 / 剪空子树」落到 state\menu-prune-last.txt，
        /// 供诊断页只读展示（与 F1-4 的日志行**同源**：同一次计算的同一组数字）。
        /// </summary>
        private void SavePruneStat(int hidden, int pruned)
        {
            try
            {
                string f = Path.Combine(Root, "state", "menu-prune-last.txt");
                string dir = Path.GetDirectoryName(f);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(f,
                    "隐藏=" + hidden + " 剪空=" + pruned
                    + " 时间=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        // ============================================================
        // 日志（受 auto.conf 的 log 开关控制）
        // ============================================================
        /// <summary>是否写运行日志（auto.conf: log=0/1）。</summary>
        public bool LogEnabled = true;

        /// <summary>日志级别：0=关 1=简 2=详细（含耗时明细）。</summary>
        public int LogLevel = 1;

        public void Log(string s)
        {
            if (LogLevel <= 0) return;
            try
            {
                string dir = Path.GetDirectoryName(_logPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("HH:mm:ss") + " " + s + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>详细日志（仅 LogLevel >= 2 时写）。</summary>
        public void LogVerbose(string s)
        {
            if (LogLevel < 2) return;
            Log(s);
        }

        /// <summary>读 auto.conf 里的日志设置。</summary>
        private void ReadLogSettings()
        {
            try
            {
                string conf = Path.Combine(Root, "config", "auto.conf");
                if (!File.Exists(conf)) return;

                foreach (var raw in File.ReadAllLines(conf, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();

                    if (k == "log" || k == "loglevel")
                    {
                        int n;
                        if (int.TryParse(v, out n)) LogLevel = n;
                        else LogLevel = (v == "true" || v == "1") ? 1 : 0;
                    }
                }
            }
            catch { }
        }
    }
}
