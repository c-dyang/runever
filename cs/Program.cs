using System;
using System.IO;
using System.Text;
using System.Windows;

namespace RunEverCs
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            // ============================================================
            // 参数解析
            //   --show-menu [root]    热键触发：采集→匹配→弹菜单→执行
            //   --selftest  [root]    自检
            //   --settings  [root]    打开设置窗口
            //   --dump-parser ...     解析器 dump（一致性验证）
            //   --dump-autostart [root] 开机自启项只读诊断（B3-6）
            //   --selftest-menu ...   菜单渲染自检
            //   --selftest-capture .. 采集链自检
            // ============================================================
            if (args == null) args = new string[0];

            string root = FindRoot(args);

            if (args.Length >= 1)
            {
                switch (args[0])
                {
                    case "--dump-parser":
                        Environment.ExitCode = ParserDump.Run(args);
                        return;
                    case "--bench-build":
                        Environment.ExitCode = BenchMenuBuild(root, args);
                        return;
                    case "--bench-capture":
                        Environment.ExitCode = BenchCapture(root, args);
                        return;
                    case "--selftest-menu":
                        Environment.ExitCode = MenuSelfTest.Run(args);
                        return;
                    case "--selftest-capture":
                        Environment.ExitCode = CaptureSelfTest.Run(args);
                        return;
                    case "--selftest-writer":
                        Environment.ExitCode = WriterSelfTest.Run(args);
                        return;
                    case "--dump-menutext":
                        Environment.ExitCode = MenuTextDump.Run(args);
                        return;
                    case "--dump-order":
                        Environment.ExitCode = RunEverCs.Services.MenuOrderProbe.Run(args);
                        return;
                    // ★ B3-6（2026-11-08）：开机自启项只读诊断（读注册表值 + 解析 + 判定）
                    case "--dump-autostart":
                        Environment.ExitCode = TrayApp.DumpAutoStart(root, args);
                        return;
                    case "--selftest-template":
                        Environment.ExitCode = TemplateSelfTest.Run(args);
                        return;
                    case "--selftest-pluginedit":
                        Environment.ExitCode = PluginEditSelfTest.Run(args);
                        return;
                    case "--selftest-bootstrap":
                        Environment.ExitCode = BootstrapSelfTest.Run(args);
                        return;
                    case "--selftest-pathcache":
                        Environment.ExitCode = PathCacheBuilderSelfTest.Run(args);
                        return;
                    // ★ 功能 B（全盘自检 + EV 后端）：三条新 CLI
                    case "--ev-probe":
                        Environment.ExitCode = EverythingSdkProbe.Run(args);
                        return;
                    case "--selftest-selfcheck":
                        Environment.ExitCode = SelfCheckSelfTest.Run(args);
                        return;
                    case "--selfcheck":
                        Environment.ExitCode = SelfCheckCli.Run(args);
                        return;
                    case "--refresh-plugins":
                        Environment.ExitCode = RefreshPluginsCli(root);
                        return;
                    case "--refresh-cache":
                        Environment.ExitCode = RefreshCacheCli(root);
                        return;
                    case "--selftest-hide":
                        Environment.ExitCode = MissingFilterSelfTest.Run(args);
                        return;
                    case "--selftest-automatch":
                        Environment.ExitCode = AutoMatchSelfTest.Run(args);
                        return;
                    // ★ v8 第 1 段（可用性门）：三条新自检
                    //   --selftest-matcher  判定层 R1–R9 + 同名共存（AC-1/AC-4）
                    //   --selftest-resolve  解析三时机 + 三态 + 超时（AC-5）
                    //   --selftest-exec     执行能力矩阵 + 占位符（AC-9）
                    case "--selftest-matcher":
                        Environment.ExitCode = MatchEngineSelfTest.Run(args);
                        return;
                    case "--selftest-resolve":
                        Environment.ExitCode = ResolvePolicySelfTest.Run(args);
                        return;
                    case "--selftest-exec":
                        Environment.ExitCode = ExecutorSelfTest.Run(args);
                        return;
                    // ★ 段 1.6 热修（B1 写盘留痕 / B2 显示开关即时生效）
                    case "--selftest-confwrite":
                        Environment.ExitCode = ConfWriteSelfTest.Run(args);
                        return;
                    // ★ 段 1.7（配置级热加载：menus.conf 换代 + auto.conf 全键实时 + waitWhenShown）
                    case "--selftest-hotreload":
                        Environment.ExitCode = HotReloadSelfTest.Run(args);
                        return;
                    // ★ v9（2026-10-06）：系统默认文件夹选择窗口（IFileOpenDialog）互操作自检
                    //   （不弹窗：Show/GetResult 由实机点「浏览…」验收）
                    // ★ v9（2026-10-06）：菜单 owner 能否成为前台（① 抢焦点修复的无人值守判据）
                    case "--selftest-focus":
                        Environment.ExitCode = FocusSelfTest.Run(args);
                        return;
                    // ★ v9（2026-10-06）：菜单编辑页树形中栏自检
                    //   （结构操作 + 不变式 + 集合不丢 + 保存往返；沙箱副本，绝不动真实配置）
                    case "--selftest-menueditor":
                        Environment.ExitCode = MenuEditorSelfTest.Run(args);
                        return;
                    case "--selftest-folderdlg":
                        Environment.ExitCode = FolderPickerSelfTest.Run(args);
                        return;
                    // ★ 拆-0（2026-11-09）：架构自检 —— 分层引用方向（Models/Services/Native/Views）
                    //   只扫 using 行，不改任何业务代码；已知违规走白名单（按文件+类型）。
                    //   口径：--selftest-arch <root> <out.txt>，**禁带 --root**。
                    case "--selftest-arch":
                        Environment.ExitCode = ArchSelfTest.Run(args);
                        return;
                    // ★ U-C（2026-11-09）：图标源支持网址 —— 协议白名单 / 缓存命中零网络 /
                    //   未命中先兜底后后台补齐 / 超时 ≤3s / 缓存键 = sha256(url) 前 16 位。
                    //   口径：--selftest-iconurl <root> <out.txt>，**禁带 --root**，离线可跑。
                    case "--selftest-iconurl":
                        Environment.ExitCode = IconUrlSelfTest.Run(args);
                        return;
                    case "--selftest":
                        RunSelfTest(root);
                        return;
                    // ★ v7（WebDAV 配置同步）：CLI 等价物 + 自检。两者与托盘**共用同一份**
                    //   ConfigSync.Run（PathCacheRefresh 的教训：同一件事两套实现必然漂移）。
                    case "--sync":
                        Environment.ExitCode = Services.SyncCli.Run(root, args);
                        return;
                    case "--selftest-webdav":
                        Environment.ExitCode = Services.WebDavSyncSelfTest.Run(args);
                        return;
                    case "--show-menu":
                        RunShowMenu(root);
                        return;
                    case "--settings":
                        RunSettings(root);
                        return;
                    case "--tray":
                        RunTray(root);
                        return;
                    case "--trigger":
                        Environment.ExitCode = TriggerRunningInstance(root);
                        return;
                }
            }

            // ★★ 无参数 = 常驻托盘模式（用户反馈 2026-10-03）
            //   原来无参数走 --show-menu（弹一次菜单就退出）⇒ 双击后托盘里什么都没有，
            //   用户描述"开不起来 / 挂不住后台"。launcher 类程序双击就该挂后台。
            RunTray(root);
        }

        /// <summary>常驻托盘模式（双击 exe 的默认行为）。</summary>
        private static void RunTray(string root)
        {
            var wpfApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            InstallCrashHandler(wpfApp, root);

            // 托盘图标需要 WinForms 消息泵；WPF 的 Application.Run 也在泵消息，
            // 两者共用同一个 Win32 消息队列，可共存。
            System.Windows.Forms.Application.EnableVisualStyles();

            var tray = new TrayApp(root);
            if (!tray.Start())
            {
                Environment.Exit(0);
                return;
            }

            wpfApp.Run();
            tray.Dispose();
        }

        /// <summary>
        /// 全局异常兜底：任何未捕获异常都记录到 settings-crash.log 并保持进程存活。
        /// 设置窗口曾因 DataGrid 编辑管道异常被整个关掉（用户修改全丢）。
        /// </summary>
        private static void InstallCrashHandler(Application app, string root)
        {
            app.DispatcherUnhandledException += (s, e) =>
            {
                try
                {
                    string logPath = Path.Combine(root, "state", "settings-crash.log");
                    File.AppendAllText(logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " "
                        + e.Exception.GetType().Name + ": " + e.Exception.Message
                        + Environment.NewLine + e.Exception.StackTrace
                        + Environment.NewLine + new string('-', 60) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch { }

                MessageBox.Show(
                    "操作出错（程序会继续运行）：\n\n"
                    + e.Exception.Message + "\n\n"
                    + "详细信息已记录到 state\\settings-crash.log",
                    "RunEver", MessageBoxButton.OK, MessageBoxImage.Warning);

                e.Handled = true;   // ★ 阻止进程终止
            };

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    string logPath = Path.Combine(root, "state", "settings-crash.log");
                    File.AppendAllText(logPath,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [Domain] " + e.ExceptionObject
                        + Environment.NewLine + new string('-', 60) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch { }
            };
        }

        /// <summary>
        /// 触发已运行的常驻实例弹菜单（不新起进程）。
        ///
        /// 用途：
        ///   · 命令行/脚本触发（等价于按热键）
        ///   · AHK 热键助手集成：AHK 负责注册热键，按下后跑 `RunEverCs.exe --trigger`
        ///   · 自我诊断（验证常驻实例的菜单链路是否通）
        ///
        /// 返回 0 = 已投递；2 = 常驻实例未运行；3 = 句柄失效
        /// </summary>
        private static int TriggerRunningInstance(string root)
        {
            const uint WM_HOTKEY = 0x0312;
            const int HOTKEY_ID = 0x5256;

            try
            {
                string f = TrayApp.HwndFilePath(root);
                if (!File.Exists(f))
                {
                    Console.Error.WriteLine("常驻实例未运行（无 " + f + "）");
                    return 2;
                }

                string[] lines = File.ReadAllLines(f);
                if (lines.Length < 1) return 3;

                long h = 0;
                if (!long.TryParse(lines[0].Trim(), out h) || h == 0) return 3;
                IntPtr hwnd = new IntPtr(h);

                if (!IsWindow(hwnd))
                {
                    Console.Error.WriteLine("句柄已失效（常驻实例可能已退出）");
                    return 3;
                }

                if (!PostMessage(hwnd, WM_HOTKEY, new IntPtr(HOTKEY_ID), IntPtr.Zero))
                {
                    Console.Error.WriteLine("PostMessage 失败");
                    return 3;
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("触发失败: " + ex.Message);
                return 3;
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        /// <summary>定位 RunEver 根目录：--root 参数优先，否则从 exe 位置向上找 config。</summary>
        private static string FindRoot(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--root")
                    return args[i + 1].TrimEnd('\\', '/');
            }

            // 从 exe 目录向上找含 config\menus.conf 的目录
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 5 && !string.IsNullOrEmpty(dir); i++)
            {
                if (File.Exists(Path.Combine(dir, "config", "menus.conf")))
                    return dir;
                if (File.Exists(Path.Combine(dir, "..", "config", "menus.conf")))
                    return Path.GetFullPath(Path.Combine(dir, ".."));
                var parent = Path.GetDirectoryName(dir.TrimEnd('\\', '/'));
                if (parent == dir) break;
                dir = parent;
            }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        private static RunEverApp MakeApp(string root, out bool ok)
        {
            var app = new RunEverApp(root);
            ok = app.Init();
            return app;
        }

        private static void RunSelfTest(string root)
        {
            bool ok;
            var app = MakeApp(root, out ok);
            string report = app.SelfTest();
            string outFile = Path.Combine(root, "state", "selftest-cs.txt");
            try
            {
                File.WriteAllText(outFile, report, new UTF8Encoding(false));
            }
            catch { }
            app.Log("SELFTEST 报告 → " + outFile);
            Environment.ExitCode = ReportHasFailure(report) ? 1 : 0;
        }

        /// <summary>
        /// 总自检报告的**失败判据**（守门 Z-2）。
        ///
        /// ① `[FAIL]`：本文件与判定段（`RunEverApp.SelfTest`）自己的失败行；
        /// ② `FAIL` / `有失败`：被汇总段 `MenuBuilder.SelfTest` 的失败行**不带方括号**
        ///    （`RunEverApp.cs` 菜单渲染段），旧判据只看 `[FAIL]` ⇒ 菜单段失败时总自检仍绿。
        /// ③ `[OK]` / `[SKIP]` 行里的计数字样（如 "FAIL=0"）**不算失败** —— 避免把统计文字
        ///    误判为失败（放宽范围仅限"非 [OK]/[SKIP] 行内的 FAIL 字样"，其余断言一律不放宽）。
        /// ④ **行尾裸 `FAIL`**：`MenuBuilder.SelfTest` 的两条断言（`cs\Services\MenuBuilder.cs:214`
        ///    `… → FAIL`、`:238` 同名）失败行以 `FAIL` **结尾、无任何后缀分隔符**，前 4 个模式
        ///    全匹配不到。`:238` 尚被汇总行 `结果: 有失败` 兜住；`:214`（idmap 项数 ≠ 可点条目数，
        ///    即"菜单项没进 IdMap"这类映射缺口）**汇总仍打"全部通过 ✓"** ⇒ 旧判据下总自检假绿。
        ///    该模式不误判：统计行以 `FAIL=0` 结尾（尾字符是 `0`）。
        /// </summary>
        private static bool ReportHasFailure(string report)
        {
            if (string.IsNullOrEmpty(report)) return false;
            foreach (string raw in report.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("[OK]", StringComparison.Ordinal)
                    || line.StartsWith("[SKIP]", StringComparison.Ordinal)) continue;

                if (line.IndexOf("[FAIL]", StringComparison.Ordinal) >= 0) return true;
                if (line.StartsWith("FAIL", StringComparison.Ordinal)) return true;
                if (line.IndexOf("FAIL ", StringComparison.Ordinal) >= 0) return true;
                if (line.IndexOf("FAIL:", StringComparison.Ordinal) >= 0) return true;
                if (line.IndexOf("FAIL(", StringComparison.Ordinal) >= 0) return true;
                if (line.IndexOf("有失败", StringComparison.Ordinal) >= 0) return true;
                if (line.EndsWith("FAIL", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 重扫插件目录并写出 `state\plugins.json`（与 AHK `AutoMatcher.WriteRegistry` 同格式）。
        ///
        /// 用途：① 托盘「刷新插件」的程序化等价物（脚本/自动化可调）
        ///       ② 无 GUI 环境下验证登记契约（守门 AC-1/AC-2 的取证入口）
        /// 用法：--refresh-plugins [--root &lt;root&gt;]
        /// </summary>
        private static int RefreshPluginsCli(string root)
        {
            try
            {
                var am = new Services.AutoMatcher(root, s => Console.Error.WriteLine(s));
                am.Load(true);      // 强制重扫

                string path = Services.PluginRegistry.DefaultPath(root);
                bool ok = Services.PluginRegistry.Write(am, path, s => Console.Error.WriteLine(s));

                Console.Error.WriteLine("插件扫描：正常=" + am.Plugins.Count
                    + " 异常=" + am.BadPlugins.Count + " → " + path);
                return ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("刷新插件失败: " + ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// 刷新路径缓存 —— 托盘「刷新路径缓存」的命令行等价物（**同一份实现**：
        /// `Services.PathCacheRefresh.Run`），搜索后端 EV → es → PATH + App Paths。
        /// 用途：无 GUI 环境验证 / 脚本与自动化调用。
        /// 用法：--refresh-cache [--root &lt;root&gt;]
        /// </summary>
        private static int RefreshCacheCli(string root)
        {
            var r = Services.PathCacheRefresh.Run(root, s => Console.Error.WriteLine(s));
            if (!r.Ok) Console.Error.WriteLine("刷新失败: " + r.Message);
            return r.Ok ? 0 : 1;
        }

        private static void RunShowMenu(string root)
        {
            bool ok;
            var app = MakeApp(root, out ok);
            if (!ok)
            {
                app.Log("初始化失败，退出");
                Environment.ExitCode = 2;
                return;
            }

            // ★ 不再需要 WPF Application / 消息泵：
            //   owner 窗口改用纯 Win32 CreateWindowExW（见 OwnerWindow），
            //   TrackPopupMenuEx 自带模态消息循环，不需要外部泵。
            //   （曾用 WPF Window 当 owner：Show() 要 259ms 初始化 WPF 栈；
            //     且 OnExplicitShutdown 下进程可能残留不退出。）
            int sel = app.ShowMenuFlow();
            app.Log("菜单流程结束，退出进程");

            // ★ I-5：现查分支的「现查 + 启动」已移交 worker 线程
            //   （RunEverApp.DispatchOnWorkerIfNeeded）⇒ 硬退出前必须等在途派发收尾：
            //   Environment.Exit 会立刻杀掉所有线程（含 worker），不等就退化成
            //   用户侧"点了条目、程序没启动"。
            if (!app.WaitPendingDispatch(3000))
                app.Log("⚠ 在途现查派发未在 3s 内收尾，直接退出");

            // ★ 硬退出：这是"用进程生命周期消灭阻塞"架构的落地点
            Environment.Exit(0);
        }

        private static void RunSettings(string root)
        {
            var wpfApp = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
            InstallCrashHandler(wpfApp, root);

            var win = new Views.SettingsWindow(root);
            wpfApp.Run(win);
        }

        /// <summary>
        /// 2026-11-07 Win32 菜单构建分段测速（`--bench-build`）：跑 10 次 Build，看
        /// 「条目创建」与「图标转 DIB」各占多少 ⇒ 决定菜单树缓存该缓存什么。
        /// </summary>
        private static int BenchMenuBuild(string root, string[] args)
        {
            var outPath = System.IO.Path.Combine(root, "state", "bench-build.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath));
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                var confPath = System.IO.Path.Combine(root, "config", "menus.conf");
                var conf = new RunEverCs.Services.ConfParser(confPath);
                conf.Parse();
                var groups = conf.Groups;

                // ★ 关键：必须复现真实路径 —— 先解析图标源（走 PathCache），否则全是兜底图标，
                //   测不到「真图标抽出 + DIB 转换」这段，结论会假绿。
                var cache = new RunEverCs.Services.PathCache(System.IO.Path.Combine(root, "cache", "paths.txt"));
                var icons = new RunEverCs.Services.IconResolver(cache);
                icons.ResolveAll(groups);

                lines.Add("[bench-build] 分组=" + groups.Count + "  配置=" + confPath
                    + "  缓存条目=" + cache.Count);

                for (int i = 0; i < 10; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var built = RunEverCs.Services.MenuBuilder.Build(groups);
                    long total = sw.ElapsedMilliseconds;
                    lines.Add(string.Format(
                        "  #{0,-2}: total={1,3}ms  item={2,3}ms  icon={3,3}ms | 顶层={4} 条目={5} hit={6} 兜底={7}",
                        i + 1, total,
                        RunEverCs.Services.MenuBuilder.LastItemMs,
                        RunEverCs.Services.MenuBuilder.LastIconMs,
                        built.Menu.Count(), built.ItemCount,
                        built.IconHit, built.IconFallback));
                    built.Menu.Dispose();
                }
                System.IO.File.WriteAllLines(outPath, lines, System.Text.Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                lines.Add("[bench-build] FAIL: " + ex.GetType().Name + ": " + ex.Message);
                System.IO.File.WriteAllLines(outPath, lines, System.Text.Encoding.UTF8);
                return 1;
            }
        }

        /// <summary>
        /// 采集测速（`--bench-capture`）：跑 5 次**常驻**采集器，打印每轮耗时与采到的内容；
        /// 再跑一次一次性进程作对照。原生采集后端已于 2026-11-07 删除（见 RunEverApp 注释）。
        /// 注意：选中文本/文件依赖当前前台有选区 —— 无选区时该项为空属正常。
        /// </summary>
        private static int BenchCapture(string root, string[] args)
        {
            var outPath = System.IO.Path.Combine(root, "state", "bench-capture.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outPath));
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                var cc0 = new RunEverCs.Services.CaptureClient(root);
                var nc = new RunEverCs.Services.ResidentCaptureClient(cc0.RunnerPath, cc0.AhkExe);
                lines.Add("[bench-capture] " + DateTime.Now.ToString("HH:mm:ss"));

                for (int i = 0; i < 5; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var ctx = nc.Capture(s => lines.Add("   " + s));
                    long total = sw.ElapsedMilliseconds;
                    lines.Add(string.Format(
                        "  #{0}: total={1,3}ms | proc=[{2}] class=[{3}] text={4}B files={5} clip={6}B",
                        i + 1, total, ctx.Proc, ctx.WinClass,
                        ctx.Text == null ? 0 : ctx.Text.Length,
                        ctx.Files.Count,
                        ctx.Clipboard == null ? 0 : ctx.Clipboard.Length));
                }

                // 对照：AHK 后端一次
                var cc = new RunEverCs.Services.CaptureClient(root);
                if (cc.IsAvailable())
                {
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    var c2 = cc.Capture(s => lines.Add("   " + s));
                    lines.Add(string.Format(
                        "  [ahk 对照] total={0}ms | proc=[{1}] text={2}B files={3}",
                        sw2.ElapsedMilliseconds, c2.Proc,
                        c2.Text == null ? 0 : c2.Text.Length, c2.Files.Count));
                }
                else
                {
                    lines.Add("  [ahk 对照] 采集器不可用，跳过");
                }

                System.IO.File.WriteAllLines(outPath, lines, System.Text.Encoding.UTF8);
                return 0;
            }
            catch (Exception ex)
            {
                lines.Add("[bench-capture] FAIL: " + ex.GetType().Name + ": " + ex.Message);
                lines.Add(ex.StackTrace);
                System.IO.File.WriteAllLines(outPath, lines, System.Text.Encoding.UTF8);
                return 1;
            }
        }
    }
}
