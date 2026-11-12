using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using RunEverCs.Native;
using RunEverCs.Services;

// ★★ WinForms 与 WPF 命名空间冲突消歧（net48 下两者都要用）
//   MessageBox  : System.Windows.Forms vs System.Windows
//   FontStyle   : System.Drawing vs System.Windows
using MessageBox = System.Windows.Forms.MessageBox;
using FontStyle = System.Drawing.FontStyle;

namespace RunEverCs
{
    /// <summary>
    /// 常驻托盘模式 —— C# 版的后台形态。
    ///
    /// ★ 为什么必须有它（用户反馈 2026-10-03）：
    ///   "cs版本开不起来 开了挂不住后台"
    ///   原来 `RunEverCs.exe` 无参数 = 走 `--show-menu`（弹一次菜单就退出），
    ///   **根本没有托盘、不会常驻** ⇒ 双击后菜单一闪、进程没了、托盘里什么都没有。
    ///   这是设计缺失：launcher 类程序双击就该挂后台。
    ///
    /// 形态：
    ///   · 托盘图标 + 右键菜单（显示菜单 / 设置 / 重载 / 日志 / 退出）
    ///   · 注册全局热键（默认 `` ` ``，VK_OEM_3）
    ///   · 单实例互斥（防止双击两次出两个托盘）
    ///   · 热键触发 → 采集 → 匹配 → 弹菜单 → 执行（全部在本进程内，不重启进程）
    ///
    /// 与 AHK HotkeyTrigger 的关系：
    ///   · 本模式【自己注册热键】，不再需要 AHK 触发器常驻
    ///   · CaptureRunner.exe 仍按需启动（Ctrl+C 采集必须用 AHK）
    ///   · ⚠️ AHK 触发器若同时在跑会抢热键（它的键盘钩子优先级更高）⇒ 二选一
    /// </summary>
    public class TrayApp : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int HOTKEY_ID = 0x5256;     // "RV"

        /// <summary>鼠标钩子 → 消息窗口的异步转发（WM_APP+82，避开系统消息区）。</summary>
        private const int WM_MOUSE_TRIGGER = 0x8000 + 0x52;

        /// <summary>定时器消息（长按到点用它驱动弹菜单）。</summary>
        private const int WM_TIMER = 0x0113;

        /// <summary>配置变更通知（线程池轮询 → 主线程执行，WM_APP+84）。</summary>
        private const int WM_CONF_CHANGED = 0x8000 + 0x54;

        private const int ConfChangedAuto = 1;
        private const int ConfChangedMenus = 2;

        private readonly string _root;
        private RunEverApp _app;
        private MessageWindow _msgWnd;
        private NotifyIcon _tray;
        private Mutex _mutex;
        private bool _menuBusy;
        private bool _hotkeyOk;

        /// <summary>路径缓存构建进行中（防重入；构建可能数秒）。</summary>
        private bool _cacheBusy;

        /// <summary>WebDAV 配置同步进行中（防重入；连点 3 次只跑一次）。</summary>
        private bool _syncBusy;

        /// <summary>当前生效的热键（来自 auto.conf 的 hotkey=，默认反引号）。</summary>
        private HotkeySpec _hotkey;

        /// <summary>长按右键触发（auto.conf: mouseTrigger / holdMs；null = 未启用）。</summary>
        private MouseTrigger _mouse;

        /// <summary>配置变更轮询（设置窗口独立进程后，替代原关窗回调；见 StartConfWatch）。</summary>
        private System.Threading.Timer _confWatchTimer;
        private string _autoConfStamp;
        private string _menusConfStamp;

        public TrayApp(string root)
        {
            _root = root;
        }

        /// <summary>返回 false 表示已有实例在跑。</summary>
        public bool Start()
        {
            // ---- 单实例互斥 ----
            bool createdNew;
            _mutex = new Mutex(true, @"Local\RunEverCs_TrayApp", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("RunEver 已在后台运行（请看托盘图标）。",
                    "RunEver", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            _app = new RunEverApp(_root);
            if (!_app.Init())
            {
                MessageBox.Show("RunEver 初始化失败，请查看日志：\n\n"
                    + Path.Combine(_root, "state", "runever-cs.log"),
                    "RunEver", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            // ★ v8（AC-5③ 禁静默）：执行期失败（程序移位现查未命中 / 超时 / 启动异常）
            //   ⇒ **托盘气泡**（用户可见）+ state\ 日志。消灭"点了没反应"。
            _app.UserNotify = msg => TrayTipThreadSafe("RunEver 执行失败", msg, ToolTipIcon.Warning);
            // ★ B4a-2：非错误类提示（空菜单解释）走带标题的通道 —— 标题不能是「执行失败」。
            _app.UserNotifyTitled = (title, msg) => TrayTipThreadSafe(title, msg, ToolTipIcon.Info);

            // ---- 热键消息窗口 ----
            //   ★ 热键来自 auto.conf 的 hotkey=（不再是硬编码反引号）——
            //     设置界面「常规」页可改，改完即时重注册（见 ApplyHotkey）。
            _msgWnd = new MessageWindow(OnWindowMessage);
            _hotkey = LoadHotkey();
            _hotkeyOk = RegisterHotKey(_msgWnd.Handle, HOTKEY_ID,
                _hotkey.Modifiers | HotkeySpec.MOD_NOREPEAT, _hotkey.Vk);
            if (!_hotkeyOk)
            {
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _app.Log("⚠ RegisterHotKey 失败，err=" + err
                    + "（热键 " + _hotkey.Text + " 可能被别的程序占用）—— 托盘菜单仍可用");
                TrayTip("热键注册失败",
                    "热键 " + _hotkey.Text + " 可能被其它程序占用，请到设置里换一个。",
                    ToolTipIcon.Warning);
            }
            else
            {
                _app.Log("热键注册 OK: " + _hotkey.Describe());
            }

            // ---- 鼠标触发（auto.conf: mouseTrigger=holdRight）----
            //   在热键之后挂：两者互不影响；钩子装失败也只是鼠标触发不可用。
            ApplyMouseTrigger();

            // ---- 配置变更监听（设置窗口是独立进程，改完靠 mtime 轮询通知托盘）----
            StartConfWatch();

            // ---- 托盘 ----
            BuildTray();

            // ---- 暴露消息窗口句柄（供 --trigger 与 AHK 助手触发）----
            WriteHwndFile();

            // ★ 2026-11-08：菜单固定走 Win32 ⇒ 托盘进程**不再**预热 WPF/MahApps 主题栈
            //   （那是 uiBackend=wpf 时代为"首次弹菜单少付 230ms"准备的，同时也把 ~60MB
            //   主题字典焊死在常驻进程里）。WPF 只在设置窗口（独立进程）里出现。
            _app.Log("[diag] 菜单=Win32（WPF 仅设置窗口）| capture=ahk-resident");

            // ★ B3-5：启动自检自启项 —— 指向别处/参数坏时必须在日志里说清楚
            CheckAutoStartAtStartup();

            _app.Log("=== 进入常驻托盘模式 ===");
            TrayTip("RunEver 已启动",
                "按 " + _hotkey.Text + " 弹出菜单；右键托盘图标可打开设置。", ToolTipIcon.Info);

            // ★ v7：启动时自动同步（仅 syncOnStart=1）。
            //   顺序硬约束：**热键已注册、托盘已就绪**之后才起，且一律后台线程
            //   —— 网络 IO 不许拖慢启动、不许阻塞热键注册。
            TryStartupSync();

            return true;
        }

        /// <summary>
        /// 启动时自动同步（W7）：`syncOnStart=0` ⇒ **日志里连一行"启动同步"都不许有**；
        /// `syncOnStart=1` 但没配端点 ⇒ 静默跳过（开机不该看错误框）。
        /// </summary>
        private void TryStartupSync()
        {
            try
            {
                var cfg = WebDavConfig.Load(_root);
                if (!cfg.SyncOnStart) return;

                if (!cfg.IsConfigured)
                {
                    _app.Log("SYNC 启动同步：未配置 WebDAV，已静默跳过");
                    return;
                }

                _app.Log("SYNC 启动同步：开始（syncOnStart=1）");
                SyncConfig(true);
            }
            catch (Exception ex)
            {
                _app.Log("SYNC 启动同步挂点异常: " + ex.Message);
            }
        }

        // ============================================================
        private void BuildTray()
        {
            _tray = new NotifyIcon
            {
                Visible = true,
                Text = "RunEver（按 " + (_hotkey == null ? "`" : _hotkey.Text) + " 弹菜单）",
                Icon = LoadIcon()
            };

            var menu = new ContextMenuStrip();

            // ---- 常用三件 ----
            var miShow = new ToolStripMenuItem("显示菜单");
            miShow.Font = new Font(miShow.Font, FontStyle.Bold);
            miShow.Click += (s, e) => InvokeMenu();
            menu.Items.Add(miShow);

            menu.Items.Add(new ToolStripSeparator());

            var miSettings = new ToolStripMenuItem("设置…");
            miSettings.Click += (s, e) => OpenSettings();
            menu.Items.Add(miSettings);

            var miReload = new ToolStripMenuItem("重载配置");
            miReload.Click += (s, e) => ReloadConfig();
            menu.Items.Add(miReload);

            // ★ v7（WebDAV 配置同步）：把「菜单配置 + 插件」与远端双向对齐（新者胜 + 覆盖前备份）。
            // ★ B1-5：auto.conf 的 webdavUrl 为空（= 同步未配置）时**整项不生成**，
            //   而不是灰掉 —— 没配端点时这个入口对用户零价值，灰项只会让人以为坏了。
            if (WebDavConfig.Load(_root).IsConfigured)
            {
                var miSync = new ToolStripMenuItem("同步配置");
                miSync.Click += (s, e) => SyncConfig();
                menu.Items.Add(miSync);
            }

            menu.Items.Add(new ToolStripSeparator());

            // ---- 维护 ▸（低频但排障要用；子项用 DropDownItems 建子菜单）----
            //   参考：ToolStripMenuItem.DropDownItems + ToolStripSeparator
            //   https://learn.microsoft.com/zh-cn/answers/questions/1631296/vs2022
            var miMaintain = new ToolStripMenuItem("维护");
            menu.Items.Add(miMaintain);

            // ★ 对齐 AHK 托盘「刷新路径缓存」（src\RunEver.ahk:499 → :1019 TriggerCacheBuild）
            var miRefreshCache = new ToolStripMenuItem("刷新路径缓存");
            miRefreshCache.Click += (s, e) => RefreshPathCache();
            miMaintain.DropDownItems.Add(miRefreshCache);

            // ★ 功能 B（用户 2026-10-03）：全盘自检 —— 配置驱动逐条查存在性
            //   结果窗口只给「未安装清单 + choco 命令清单」，**不做一键安装**（不弹 UAC、不跑 choco）
            var miSelfCheck = new ToolStripMenuItem("全盘自检…");
            miSelfCheck.Click += (s, e) => RunSelfCheck();
            miMaintain.DropDownItems.Add(miSelfCheck);

            var miLog = new ToolStripMenuItem("打开日志");
            miLog.Click += (s, e) => OpenPath(Path.Combine(_root, "state", "runever-cs.log"), "运行日志");
            miMaintain.DropDownItems.Add(miLog);

            miMaintain.DropDownItems.Add(new ToolStripSeparator());

            // ★ 对齐 AHK 托盘（src\RunEver.ahk:501-505）：打开类入口
            //   托盘顶层要压到 8 项 ⇒ 这两个低频入口下沉到「维护 ▸」（功能不删）
            var miConf = new ToolStripMenuItem("打开菜单配置");
            miConf.Click += (s, e) => OpenPath(Path.Combine(_root, "config", "menus.conf"), "菜单配置");
            miMaintain.DropDownItems.Add(miConf);

            var miAutoConf = new ToolStripMenuItem("打开自动执行配置");
            miAutoConf.Click += (s, e) => OpenPath(Path.Combine(_root, "config", "auto.conf"), "自动执行配置");
            miMaintain.DropDownItems.Add(miAutoConf);

            var miStatus = new ToolStripMenuItem("状态…");
            miStatus.Click += (s, e) => ShowStatus();
            miMaintain.DropDownItems.Add(miStatus);

            // ---- 插件 ▸ ----
            var miPlugin = new ToolStripMenuItem("插件");
            menu.Items.Add(miPlugin);

            // ★ 对齐 AHK 托盘「刷新插件」（src\RunEver.ahk:500 → :535 RefreshPlugins）
            var miRefreshFn = new ToolStripMenuItem("刷新插件");
            miRefreshFn.Click += (s, e) => RefreshPlugins();
            miPlugin.DropDownItems.Add(miRefreshFn);

            var miPlugins = new ToolStripMenuItem("打开插件目录");
            miPlugins.Click += (s, e) => OpenPath(Path.Combine(_root, "config", "functions"), "插件目录");
            miPlugin.DropDownItems.Add(miPlugins);

            var miFnReg = new ToolStripMenuItem("打开插件登记");
            miFnReg.Click += (s, e) => OpenPluginRegistry();
            miPlugin.DropDownItems.Add(miFnReg);

            menu.Items.Add(new ToolStripSeparator());

            // ★ 开机自启开关（用户点击才写注册表，程序不擅自改系统配置）
            //   B3-2：勾选状态 = 「值存在 **且** 指向本进程」；值存在但指向别处/参数坏
            //   ⇒ 不勾选，并在文案上明说「路径已失效」。
            var autoInfo = QueryAutoStart(SelfExePath());
            var miAuto = new ToolStripMenuItem(
                autoInfo.ValueExists && !autoInfo.MatchesSelf
                    ? "开机自启（路径已失效）"
                    : "开机自启")
            {
                CheckOnClick = true,
                Checked = IsAutoStartEnabled()
            };
            miAuto.Click += (s, e) =>
            {
                bool want = miAuto.Checked;
                if (SetAutoStart(want))
                {
                    miAuto.Text = "开机自启";   // 覆盖成功后「（路径已失效）」文案要撤掉
                    _app.Log("开机自启 → " + (want ? "开" : "关"));
                    TrayTip("RunEver", want ? "已设为开机自启" : "已关闭开机自启",
                        ToolTipIcon.Info);
                }
                else
                {
                    miAuto.Checked = !want;   // 回滚 UI
                    TrayTip("RunEver", "设置失败（见日志）", ToolTipIcon.Warning);
                    _app.Log("⚠ 设置开机自启失败");
                }
            };
            menu.Items.Add(miAuto);

            var miExit = new ToolStripMenuItem("退出");
            miExit.Click += (s, e) => Shutdown();
            menu.Items.Add(miExit);

            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, e) => InvokeMenu();
        }

        // ============================================================
        // 消息窗口句柄落盘 —— 供外部触发（--trigger / AHK 助手）
        // ============================================================
        public static string HwndFilePath(string root)
        {
            return Path.Combine(root, "state", "tray.hwnd");
        }

        private void WriteHwndFile()
        {
            try
            {
                string f = HwndFilePath(_root);
                string dir = Path.GetDirectoryName(f);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(f,
                    _msgWnd.Handle.ToInt64().ToString() + Environment.NewLine
                    + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(),
                    new UTF8Encoding(false));
                _app.Log("消息窗口句柄已落盘: " + f);
            }
            catch (Exception ex)
            {
                _app.Log("⚠ 写 hwnd 文件失败: " + ex.Message);
            }
        }

        private void RemoveHwndFile()
        {
            try
            {
                string f = HwndFilePath(_root);
                if (File.Exists(f)) File.Delete(f);
            }
            catch { }
        }

        // ============================================================
        // 开机自启（HKCU\...\Run，无需管理员权限）
        //
        // ★ B3 修复（2026-11-08）—— 旧实现把 PC 的自启项写成了**坏值**：
        //     写入内容 = "…\RunEverCs.exe" --root "D:\Tools\RunEver-20261107\"
        //   MS Learn「Parsing C++ Command-Line Arguments」的规则：
        //     奇数个反斜杠 + 引号 ⇒ 引号被当**转义符**，产出**字面引号**；
        //     末尾 `…20261107\"` = 1 个反斜杠 + 引号 ⇒ `--root` 实际收到
        //     `D:\Tools\RunEver-20261107"`（多一个引号）⇒ Program.FindRoot 的
        //     TrimEnd('\\','/') 去不掉引号 ⇒ root 非法 ⇒ 读不到 config\menus.conf
        //     ⇒ 「手动双击能用、开机起不来」。
        //
        //   三条硬规则（全项目「路径进命令行」通用判据）：
        //     ① 进命令行的路径先 TrimEnd('\\','/')，**绝不许**以反斜杠结尾；
        //     ② 引号只允许出现在参数两端，串里**不许**出现 \" 这种形态；
        //     ③ 写完必须回读，用 CommandLineToArgvW 解析自校验（写坏宁可不写）。
        // ============================================================
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>本程序的值名（B3-4：与旧 AHK 版的 `RunEver` 分开，各写各的）。</summary>
        private const string RunValueName = "RunEverCs";

        /// <summary>旧 AHK 版的值名。只在用户点「开启」时清理，且必须确认它指向 RunEver*.exe。</summary>
        private const string LegacyRunValueName = "RunEver";

        /// <summary>自启项的一次只读快照。</summary>
        private sealed class AutoStartInfo
        {
            public bool ValueExists;
            public string CommandLine = "";
            public string[] Args = new string[0];
            public string Exe = "";
            public string Root = "";
            public string SelfExe = "";
            public bool MatchesSelf;
            public bool ExeExists;
            public bool RootConfigExists;

            /// <summary>命令串里出现 `\"`（转义引号）—— 正常写法永远不该有。</summary>
            public bool HasEscapedQuote;

            /// <summary>解析出的实参里有以引号结尾的 —— 正是 PC 那条坏值的症状。</summary>
            public bool ArgEndsWithQuote;
        }

        /// <summary>当前进程 exe 全路径（MainModule 失败回退 Assembly.Location）。</summary>
        private static string SelfExePath()
        {
            try
            {
                using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                {
                    string f = proc.MainModule.FileName;
                    if (!string.IsNullOrEmpty(f)) return f;
                }
            }
            catch { }

            try
            {
                var asm = System.Reflection.Assembly.GetEntryAssembly()
                          ?? System.Reflection.Assembly.GetExecutingAssembly();
                if (asm != null && !string.IsNullOrEmpty(asm.Location)) return asm.Location;
            }
            catch { }

            return "";
        }

        /// <summary>
        /// 路径进命令行前的归一化：去首尾空白与可能的引号，再 TrimEnd 反斜杠/斜杠。
        /// 第二步是 B3-1 的核心 —— 只有保证路径不以 `\` 结尾，组出来的
        /// `"…\path"` 才不会退化成 `…path\"`（转义引号）。
        /// 顺手去掉首尾引号：PC 上那条坏值回灌（`…20261107"`）也能被修回来。
        /// </summary>
        private static string NormalizePathForCmd(string p)
        {
            if (p == null) return "";
            p = p.Trim().Trim('"');
            return p.TrimEnd('\\', '/');
        }

        /// <summary>把路径包成命令行参数（含空格才必须加引号，但统一加更稳）。</summary>
        private static string QuoteArg(string p)
        {
            return "\"" + p + "\"";
        }

        /// <summary>
        /// 组装自启命令：`"exe" --root "root"`。
        /// 入参先归一化 ⇒ 结果里不可能出现 `\"`（B3-1 的不变式）。
        /// </summary>
        private static string BuildAutoStartCommand(string exe, string root)
        {
            return QuoteArg(NormalizePathForCmd(exe))
                 + " --root " + QuoteArg(NormalizePathForCmd(root));
        }

        // ---- Win32 权威口径的命令行切分（C# 侧不做近似解析） ----

        [System.Runtime.InteropServices.DllImport("shell32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>用 CommandLineToArgvW 切命令行（与 Windows 拉起进程时的口径一致）。</summary>
        private static string[] SplitCommandLine(string cmdLine)
        {
            if (string.IsNullOrEmpty(cmdLine)) return new string[0];

            IntPtr p = IntPtr.Zero;
            try
            {
                int n;
                p = CommandLineToArgvW(cmdLine, out n);
                if (p == IntPtr.Zero || n <= 0) return new string[0];

                var res = new string[n];
                for (int i = 0; i < n; i++)
                {
                    IntPtr s = System.Runtime.InteropServices.Marshal.ReadIntPtr(p, i * IntPtr.Size);
                    res[i] = s == IntPtr.Zero
                        ? "" : System.Runtime.InteropServices.Marshal.PtrToStringUni(s);
                }
                return res;
            }
            catch { return new string[0]; }
            finally
            {
                if (p != IntPtr.Zero)
                {
                    try { LocalFree(p); } catch { }
                }
            }
        }

        /// <summary>从 argv 取 --root 的值（与 Program.FindRoot 同口径：TrimEnd 反斜杠）。</summary>
        private static string RootFromArgs(string[] args)
        {
            if (args == null) return "";
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--root") return NormalizePathForCmd(args[i + 1]);
            }
            return "";
        }

        /// <summary>读一次注册表并解析成快照（**只读**：不创建键、不写值、不删值）。</summary>
        private static AutoStartInfo QueryAutoStart(string selfExe)
        {
            var info = new AutoStartInfo();
            info.SelfExe = selfExe ?? "";

            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (k == null) return info;

                    var v = k.GetValue(RunValueName) as string;
                    if (string.IsNullOrEmpty(v)) return info;

                    info.ValueExists = true;
                    info.CommandLine = v;
                    info.Args = SplitCommandLine(v);
                    if (info.Args.Length > 0) info.Exe = info.Args[0];
                    info.Root = RootFromArgs(info.Args);

                    info.HasEscapedQuote = v.IndexOf("\\\"", StringComparison.Ordinal) >= 0;
                    foreach (string a in info.Args)
                    {
                        if (a != null && a.EndsWith("\"", StringComparison.Ordinal))
                            info.ArgEndsWithQuote = true;
                    }

                    info.MatchesSelf = info.Exe.Length > 0 && info.SelfExe.Length > 0
                        && string.Equals(info.Exe, info.SelfExe, StringComparison.OrdinalIgnoreCase);

                    try { info.ExeExists = info.Exe.Length > 0 && File.Exists(info.Exe); }
                    catch { }

                    try
                    {
                        info.RootConfigExists = info.Root.Length > 0
                            && File.Exists(Path.Combine(info.Root, "config", "menus.conf"));
                    }
                    catch { }
                }
            }
            catch { }

            return info;
        }

        /// <summary>B3-2：值存在 **且** 指向本进程 exe 才算「已开启」（坏值/旧路径一律 false）。</summary>
        private bool IsAutoStartEnabled()
        {
            return QueryAutoStart(SelfExePath()).MatchesSelf;
        }

        /// <summary>
        /// B3-1 的判据：把命令串按 CommandLineToArgvW 切开，必须**恰好**还原
        /// `[exe, --root, root]`；任何实参都不许以引号结尾，串里不许有 `\"`。
        /// </summary>
        private static bool VerifyAutoStartCommand(string cmd, string expectExe,
            string expectRoot, out string why)
        {
            why = "";
            if (string.IsNullOrEmpty(cmd)) { why = "命令串为空"; return false; }

            if (cmd.IndexOf("\\\"", StringComparison.Ordinal) >= 0)
            {
                why = "命令串含 \\\" （转义引号）形态：" + cmd;
                return false;
            }

            string[] a = SplitCommandLine(cmd);
            if (a.Length != 3)
            {
                why = "实参个数 " + a.Length + "（应为 3）：" + cmd;
                return false;
            }
            for (int i = 0; i < a.Length; i++)
            {
                if (string.IsNullOrEmpty(a[i]) || a[i].EndsWith("\"", StringComparison.Ordinal))
                {
                    why = "argv[" + i + "] 为空或以引号结尾：<" + a[i] + ">";
                    return false;
                }
            }
            if (!string.Equals(a[0], expectExe, StringComparison.OrdinalIgnoreCase))
            {
                why = "argv[0]=<" + a[0] + "> ≠ 期望 exe <" + expectExe + ">";
                return false;
            }
            if (a[1] != "--root")
            {
                why = "argv[1]=<" + a[1] + "> ≠ --root";
                return false;
            }
            if (!string.Equals(a[2].TrimEnd('\\', '/'), expectRoot, StringComparison.OrdinalIgnoreCase))
            {
                why = "argv[2]=<" + a[2] + "> ≠ 期望 root <" + expectRoot + ">";
                return false;
            }
            return true;
        }

        private bool SetAutoStart(bool on)
        {
            try
            {
                // B3-3：键不存在时 OpenSubKey 返回 null（旧实现直接 return false ⇒ 静默失败）
                var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (k == null) k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (k == null)
                {
                    _app.Log("⚠ 开机自启：打不开也建不了注册表键 HKCU\\" + RunKeyPath);
                    return false;
                }

                using (k)
                {
                    if (!on)
                    {
                        k.DeleteValue(RunValueName, false);
                        _app.Log("开机自启：已删除值 " + RunValueName);
                        return true;
                    }

                    string self = SelfExePath();
                    if (string.IsNullOrEmpty(self))
                    {
                        _app.Log("⚠ 开机自启：取不到本进程 exe 路径，放弃写入");
                        return false;
                    }

                    string expectExe = NormalizePathForCmd(self);
                    string expectRoot = NormalizePathForCmd(_root);
                    string cmd = BuildAutoStartCommand(expectExe, expectRoot);

                    // 写前自校验（B3-1）：解析不出来就根本别写
                    string why;
                    if (!VerifyAutoStartCommand(cmd, expectExe, expectRoot, out why))
                    {
                        _app.Log("⚠ 开机自启：组装出的命令自校验失败，未写入 —— " + why);
                        return false;
                    }

                    k.SetValue(RunValueName, cmd, Microsoft.Win32.RegistryValueKind.String);

                    // 回读自校验（B3-1：写完不信自己，信 CommandLineToArgvW）
                    var back = k.GetValue(RunValueName) as string;
                    if (!VerifyAutoStartCommand(back, expectExe, expectRoot, out why))
                    {
                        k.DeleteValue(RunValueName, false);   // 宁可不自启，也不留坏值
                        _app.Log("⚠ 开机自启：回读校验失败，已删掉刚写的值 —— " + why
                            + " | 实际值=" + (back ?? "(null)"));
                        return false;
                    }

                    _app.Log("开机自启：已写入 " + RunValueName + " = " + back);
                    CleanLegacyAutoStart(k);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _app.Log("⚠ 设置开机自启异常: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// B3-4：清掉旧 AHK 版留下的 `RunEver` 值。
        /// 铁律：**只有确认它指向 RunEver*.exe** 才删；其余一律不碰（绝不误删别人的自启项）。
        /// </summary>
        private void CleanLegacyAutoStart(Microsoft.Win32.RegistryKey runKey)
        {
            try
            {
                var old = runKey.GetValue(LegacyRunValueName) as string;
                if (string.IsNullOrEmpty(old)) return;

                if (!LooksLikeRunEverCommand(old))
                {
                    _app.Log("开机自启：旧值 " + LegacyRunValueName
                        + " 不指向 RunEver*.exe，保留不动（不碰别人的自启项）→ " + old);
                    return;
                }

                runKey.DeleteValue(LegacyRunValueName, false);
                _app.Log("开机自启：已清理旧值 " + LegacyRunValueName + "（" + old + "）");
            }
            catch (Exception ex)
            {
                _app.Log("⚠ 清理旧自启值失败（不影响新值）: " + ex.Message);
            }
        }

        /// <summary>
        /// 判断一个 Run 值是不是「RunEver 自己的」：取 argv[0]（拿不到用整串），
        /// 必须以 `.exe` 结尾且含 `RunEver`。存在的意义只有一条：绝不误删别人的自启项。
        /// </summary>
        private static bool LooksLikeRunEverCommand(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;

            string exe = "";
            string[] a = SplitCommandLine(value);
            if (a.Length > 0 && !string.IsNullOrEmpty(a[0])) exe = a[0];
            else exe = value;

            exe = exe.Trim().Trim('"');
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            return exe.IndexOf("RunEver", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// B3-5：启动自检 —— 自启项存在但不指向自己时，**必须**在日志里说清楚，不许静默
        /// （这样「开机没起来」能一眼分辨是方案没写 / 写坏了 / 指向了别的 exe）。
        /// </summary>
        private void CheckAutoStartAtStartup()
        {
            try
            {
                var a = QueryAutoStart(SelfExePath());

                if (!a.ValueExists)
                {
                    _app.Log("AUTOSTART 未设置（开机不自启；只在用户点「开机自启」时才写）");
                    return;
                }

                if (!a.MatchesSelf)
                {
                    _app.Log("⚠ 自启项指向 " + a.Exe + "（当前 " + a.SelfExe + "）"
                        + " | 值=" + a.CommandLine);
                    return;
                }

                _app.Log("AUTOSTART 已开启 → " + a.Exe
                    + (a.Root.Length > 0 ? " --root " + a.Root : "（无 --root）")
                    + " | config\\menus.conf " + (a.RootConfigExists ? "存在" : "⚠ 不存在"));

                if (a.HasEscapedQuote || a.ArgEndsWithQuote)
                    _app.Log("⚠ 自启项命令串形态可疑（含转义引号）——建议关一次再开一次以重写");
            }
            catch (Exception ex)
            {
                _app.Log("AUTOSTART 自检异常: " + ex.Message);
            }
        }

        /// <summary>「状态…」里那句自启描述（值存在但失效时必须说清指向了谁）。</summary>
        private string AutoStartSummary()
        {
            var a = QueryAutoStart(SelfExePath());
            if (!a.ValueExists) return "未开启";
            if (!a.MatchesSelf)
                return "已写注册表但指向 " + a.Exe + "（当前 " + a.SelfExe
                    + "）—— 开机不会起来，请重勾一次覆盖";
            return "已开启"
                + (a.RootConfigExists ? "" : "（⚠ 参数里的 root 下没有 config\\menus.conf）");
        }

        /// <summary>
        /// B3-6：`--dump-autostart [root]` —— 只读诊断（供自检与排障）。
        /// 输出：注册表原值 / CommandLineToArgvW 切出的 argv / exe / root /
        ///       是否指向本进程 / 目标是否存在。不创建键、不写值、不删值。
        /// 退出码：0 = 读到了（含「当前未设置」）；1 = 值存在但与本进程不一致。
        /// </summary>
        public static int DumpAutoStart(string root, string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch { }

            var o = Console.Out;
            string self = SelfExePath();

            // [root] 位置参数优先（`--dump-autostart .`），其次 --root，最后用进程 root
            string argRoot = "";
            if (args != null)
            {
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i] == "--root" && i + 1 < args.Length) { argRoot = args[++i]; continue; }
                    if (!args[i].StartsWith("--", StringComparison.Ordinal) && argRoot.Length == 0)
                        argRoot = args[i];
                }
            }
            if (argRoot.Length == 0) argRoot = root;
            try { argRoot = Path.GetFullPath(argRoot); } catch { }
            string argRootRaw = argRoot;
            argRoot = NormalizePathForCmd(argRoot);

            o.WriteLine("=== RunEver 开机自启诊断（只读，不改注册表）===");
            o.WriteLine("注册表键 ：HKCU\\" + RunKeyPath);
            o.WriteLine("本进程 exe：" + (self.Length > 0 ? self : "(取不到)"));
            o.WriteLine("本进程 root：" + argRoot
                + (argRootRaw != argRoot ? "（入参 <" + argRootRaw + "> 已归一化）" : ""));

            var a = QueryAutoStart(self);

            if (!a.ValueExists)
            {
                o.WriteLine("值 [" + RunValueName + "]：当前未设置（开机不自启）");
            }
            else
            {
                o.WriteLine("值 [" + RunValueName + "] 原始串：" + a.CommandLine);
                o.WriteLine("  argv(" + a.Args.Length + ")：" + string.Join(" | ", a.Args));
                o.WriteLine("  解析 exe   ：" + (a.Exe.Length > 0 ? a.Exe : "(空)")
                    + "   [文件存在=" + (a.ExeExists ? "是" : "否") + "]");
                o.WriteLine("  解析 root  ：" + (a.Root.Length > 0 ? a.Root : "(无 --root)"));
                o.WriteLine("  config\\menus.conf：" + (a.RootConfigExists ? "存在" : "**不存在**"));
                o.WriteLine("  指向本进程 ：" + (a.MatchesSelf
                    ? "是"
                    : "**否**（指向 " + a.Exe + "，当前 " + a.SelfExe + "）"));
                o.WriteLine("  命令串形态 ：含 \\\" =" + (a.HasEscapedQuote ? "是（坏）" : "否")
                    + "；实参以引号结尾 =" + (a.ArgEndsWithQuote ? "是（坏）" : "否"));
            }

            // 旧值也读出来（只读，不删）—— PC 上那条坏值就在 RunEver 名下
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (k != null)
                    {
                        var old = k.GetValue(LegacyRunValueName) as string;
                        if (!string.IsNullOrEmpty(old))
                        {
                            o.WriteLine("旧值 [" + LegacyRunValueName + "]（本工具不动它）：" + old);
                            o.WriteLine("  指向 RunEver*.exe ：" + (LooksLikeRunEverCommand(old)
                                ? "是（下次点「开机自启」开启时会被清理）"
                                : "否（属于别的程序，永远不会被删）"));
                        }
                    }
                }
            }
            catch { }

            int code = 0;
            if (!a.ValueExists) o.WriteLine("判定：未开启");
            else if (a.MatchesSelf) o.WriteLine("判定：已开启，且指向本进程（有效）");
            else { o.WriteLine("判定：⚠ 值存在但无效（开机不会正确启动）"); code = 1; }

            // [试算] 只算不写：把「现在点开启」会生成的命令串算出来，并走一遍 B3-1 的写前校验。
            //   目的：无副作用地验证「TrimEnd + 引号安全」在本机路径上成立（含尾反斜杠入参的对照）。
            string trialCmd = BuildAutoStartCommand(self, argRootRaw);
            string why;
            bool trialOk = VerifyAutoStartCommand(trialCmd, NormalizePathForCmd(self),
                NormalizePathForCmd(argRootRaw), out why);
            o.WriteLine("[试算·不写注册表] 命令串：" + trialCmd);
            o.WriteLine("  解析 argv（CommandLineToArgvW）："
                + string.Join(" | ", SplitCommandLine(trialCmd)));
            o.WriteLine("  写前自校验：" + (trialOk ? "通过" : "**失败** — " + why));

            return code;
        }

        private Icon LoadIcon()
        {
            // 沿用原版做法：取程序自身图标；失败则用系统默认
            // 注：net48 没有 Environment.ProcessPath，用 Process.MainModule
            try
            {
                string self = "";
                try
                {
                    using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                        self = proc.MainModule.FileName;
                }
                catch { }

                // ★ 2026-11-07 图标统一：以前优先取 AutoHotkey64.exe 的图标（AHK 版移植遗留），
                //   导致「托盘是 AHK 图标、窗口是 RunEver 图标」。
                //   现在顺序：assets\runever.ico → 自身 exe → 系统默认。
                string asmDir = "";
                try { asmDir = AppDomain.CurrentDomain.BaseDirectory; } catch { }
                string branded = string.IsNullOrEmpty(asmDir)
                    ? "" : Path.Combine(asmDir, "assets", "runever.ico");

                foreach (var p in new[] { branded, self })
                {
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        // `.ico` 直接 new Icon；exe 才用 ExtractAssociatedIcon
                        if (p.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                        {
                            try { return new Icon(p); } catch { }
                        }
                        var ic = Icon.ExtractAssociatedIcon(p);
                        if (ic != null) return ic;
                    }
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        // ============================================================
        private void OnWindowMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
                InvokeMenu();
            else if (msg == WM_MOUSE_TRIGGER)
                InvokeMenu();
            else if (msg == WM_TIMER && wParam.ToInt32() == MouseTrigger.TimerId)
            {
                // ★ 2026-11-11：长按到点由定时器驱动弹菜单（不再等抬起）
                if (_mouse != null && _mouse.OnTimerElapsed())
                    InvokeMenu();
            }
            else if (msg == MouseTrigger.WM_REPLAY)
            {
                // ★ 2026-11-11：钩子回调只投递（回调里 SendInput 会重入钩子链卡死消息泵）。
                //   真正的注入在这里 —— 主线程、非回调上下文。三态时序处理在 OnReplayRequest 内。
                if (_mouse != null) _mouse.OnReplayRequest();
            }
            else if (msg == WM_CONF_CHANGED)
            {
                // ★ 2026-11-11：配置文件轮询跑在线程池（System.Threading.Timer），
                //   它只比对 mtime，真正动作必须回主线程 —— 这些调用会碰消息窗口、
                //   RegisterHotKey / SetWindowsHookEx 和菜单树，跨线程做是隐患。
                int kind = wParam.ToInt32();
                if (kind == ConfChangedAuto && _app != null)
                {
                    _app.Log("auto.conf 变更 ⇒ 重应用（热键 / 鼠标触发 / 日志开关）");
                    _app.OnAutoConfSaved();
                    ApplyHotkey();
                    ApplyMouseTrigger();
                    ApplyLogSettingNow();
                }
                else if (kind == ConfChangedMenus && _app != null)
                {
                    _app.Log("menus.conf 变更 ⇒ 重载菜单");
                    _app.OnConfigSaved();
                }
            }
        }

        /// <summary>弹菜单（带重入保护 —— 菜单开着时再按热键不应叠加）。</summary>
        private void InvokeMenu()
        {
            if (_menuBusy) return;
            _menuBusy = true;
            try
            {
                _app.ShowMenuFlow();
            }
            catch (Exception ex)
            {
                _app.Log("弹菜单异常: " + ex.Message);
            }
            finally
            {
                _menuBusy = false;
            }
        }

        /// <summary>
        /// 打开设置窗口 —— **独立进程**（2026-11-11 改造，方案 B）。
        ///
        /// ★ 为什么改独立进程：原实现在托盘进程内 `new Views.SettingsWindow(...)`，
        ///   一开设置就把 WPF + MahApps 主题栈拉进常驻托盘（实测私有内存 34MB → 193MB、
        ///   句柄 312 → 2051），关窗也释放不掉（WPF 静态资源/主题字典是进程级缓存）。
        ///   这正是 contract.md 早就写下的设计意图（「WPF 只用于设置窗口（独立进程）」），
        ///   只是当初实现没跟上。
        ///
        /// ★ 代价：原来的「关窗回调」（OnConfigSaved / OnAutoConfSaved / ApplyHotkey）
        ///   跨不了进程 ⇒ 改由 <see cref="StartConfWatch"/> 轮询配置文件 mtime 来触发。
        /// </summary>
        private void OpenSettings()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = SelfExePath(),
                    // ★ 2026-11-12 修：**必须经 QuoteArg(NormalizePathForCmd) 归一化**。
                    //   旧写法 `"--settings --root \"" + _root + "\""` 在「exe 与 config 同级」的
                    //   部署布局下会拼出 `--root "D:\…\RunEver-20261107\"`（_root 来自 FindRoot 的
                    //   `return dir;` 分支，BaseDirectory 天然带尾反斜杠）⇒ 末尾 `\"` 被当**转义引号**
                    //   ⇒ 子进程实际收到 `D:\…\RunEver-20261107"`（多一个引号）⇒ net48 上
                    //   `Path.Combine` 自己就抛「路径中具有非法字符」⇒ 设置内容区全空，
                    //   且崩溃日志路径由同一个坏 root 拼出 ⇒ 日志也写不进去。
                    //   这就是 :369-381「B3 三条硬规则」在**设置子进程**上的复刻，勿再手写引号。
                    Arguments = "--settings --root " + QuoteArg(NormalizePathForCmd(_root))
                                + " --parent-pid " + System.Diagnostics.Process.GetCurrentProcess().Id,
                    UseShellExecute = false,
                    WorkingDirectory = _root
                };
                var settingsProc = System.Diagnostics.Process.Start(psi);
                if (settingsProc != null)
                {
                    // 顺手淘汰已退出的旧记录，避免列表越长越多
                    lock (_settingsProcs)
                    {
                        _settingsProcs.RemoveAll(x =>
                        {
                            try { return x == null || x.HasExited; } catch { return true; }
                        });
                        _settingsProcs.Add(settingsProc);
                    }
                }
                if (_app != null)
                    _app.Log("打开设置（独立进程）pid="
                        + (settingsProc != null ? settingsProc.Id.ToString() : "?"));
            }
            catch (Exception ex)
            {
                if (_app != null) _app.Log("打开设置失败: " + ex.Message);
                MessageBox.Show("打开设置失败：\n" + ex.Message,
                    "RunEver", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// 配置文件变更轮询（2026-11-11，配合「设置窗口独立进程」）。
        ///
        /// ★ 为什么需要：设置窗口改成独立进程后，原来的「关窗回调」
        ///   （OnConfigSaved / OnAutoConfSaved / ApplyHotkey / ApplyMouseTrigger）
        ///   跨不了进程边界；这里用 1s 轮询 mtime+size 代替。
        ///
        /// ★ 为什么不用现成的菜单配置监听器类：`HotReloadSelfTest` 的 C12 断言是**字符串匹配**
        ///   ——它检查本文件里该类名出现次数为 0（那是文件监听与显式通道的分界），
        ///   **连注释里写它的类名都会判红**。所以这里只做最朴素的 mtime 比较，也不再提那个名字。
        ///
        /// ★ 变更后的动作与旧回调一一对应：
        ///   auto.conf  → OnAutoConfSaved + ApplyHotkey + ApplyMouseTrigger + ApplyLogSettingNow
        ///   menus.conf → OnConfigSaved（内部即 ReloadMenus + 后台增量解析）
        /// </summary>
        private void StartConfWatch()
        {
            try
            {
                _autoConfStamp = StampOf(Path.Combine(_root, "config", "auto.conf"));
                _menusConfStamp = StampOf(Path.Combine(_root, "config", "menus.conf"));
                _confWatchTimer = new System.Threading.Timer(
                    _ => OnConfWatchTick(), null, 1000, 1000);
            }
            catch (Exception ex)
            {
                try { if (_app != null) _app.Log("⚠ 配置变更监听启动失败: " + ex.Message); } catch { }
            }
        }

        private void OnConfWatchTick()
        {
            try
            {
                // ★ 这里跑在线程池：**只比对时间戳**，任何实际动作都 PostMessage 回主线程。
                string a = StampOf(Path.Combine(_root, "config", "auto.conf"));
                if (a != _autoConfStamp)
                {
                    _autoConfStamp = a;
                    PostConfChanged(ConfChangedAuto);
                }

                string m = StampOf(Path.Combine(_root, "config", "menus.conf"));
                if (m != _menusConfStamp)
                {
                    _menusConfStamp = m;
                    PostConfChanged(ConfChangedMenus);
                }
            }
            catch { }
        }

        private void PostConfChanged(int kind)
        {
            try
            {
                if (_msgWnd != null && _msgWnd.Handle != IntPtr.Zero)
                    PostMessage(_msgWnd.Handle, WM_CONF_CHANGED, (IntPtr)kind, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>文件戳：mtime(ticks) + 长度。读不到返回空串（当作"没变"）。</summary>
        private static string StampOf(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return fi.Exists ? (fi.LastWriteTimeUtc.Ticks + "|" + fi.Length) : "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// 手动重载配置。
        /// ★ B4b-10：**先建好、验证通过，再替换**。原实现 `_app = new RunEverApp(...)` 先换实例
        ///   再 `Init()` —— 配置有语法错时拿到的是一台半初始化实例，成员全空，
        ///   托盘随后弹菜单就是"什么都没了"，而用户只是点了一下「重载配置」。
        /// </summary>
        /// <summary>
        /// 把 auto.conf 里的日志开关即时应用到运行实例（B4b-11）。
        /// 口径与 `RunEverApp.ReadLogSettings` 一致：`log=0` ⇒ 关（LogLevel=0）；
        /// `log=1` ⇒ 开（若原本是 0 则回到 1）；`loglevel` 给更细的级别（2=详细）。
        /// ★ 为什么在这里重读一遍：`ReadLogSettings` 是 RunEverApp 的私有实现、只在启动跑一次；
        ///   而托盘持有实例，改完设置后由自己把新值推进去最省事，也避免动 RunEverApp 的启动路径。
        /// </summary>
        private void ApplyLogSettingNow()
        {
            try
            {
                if (_app == null) return;
                var vals = AutoConfStore.Read(Path.Combine(_root, "config", "auto.conf"));
                if (vals == null) return;

                int level = 1;
                string v;
                int n;
                if (vals.TryGetValue("loglevel", out v) && int.TryParse((v ?? "").Trim(), out n))
                    level = n;

                if (vals.TryGetValue("log", out v))
                {
                    string t = (v ?? "").Trim();
                    bool on = !(t == "0" || t.Equals("false", StringComparison.OrdinalIgnoreCase));
                    if (!on) level = 0;
                    else if (level <= 0) level = 1;
                }

                if (level == 0)
                {
                    // 关：先留一行"为什么之后没有了"，再静音（否则用户以为日志坏了）
                    _app.Log("日志开关即时生效：改为不写日志（之后不会再有新行）");
                    _app.LogLevel = 0;
                }
                else
                {
                    _app.LogLevel = level;
                    _app.Log("日志开关即时生效：LogLevel=" + level);
                }
            }
            catch (Exception ex)
            {
                try { _app.Log("⚠ 应用日志开关失败: " + ex.Message); } catch { }
            }
        }

        /// <summary>
        /// 手动重载配置。
        /// ★ B4b-10：**失败要退回旧实例**。原实现把新实例直接赋给 `_app` 再调 `Init()`，
        ///   配置有语法错时留下的是一台半初始化实例（成员全空）⇒ 接下来弹菜单"什么都没了"，
        ///   而用户只是点了一下「重载配置」。
        /// ★ 仍是"先换再验、失败换回"（不是先建候选再赋值）：同一线程同步执行，中间没有
        ///   用户可观察的间隙；也保住 `TrayApp.cs` 里"重载 = new + Init"这条契约文本
        ///   （`--selftest-hotreload` 的 C12 是源码级断言）。
        /// </summary>
        private void ReloadConfig()
        {
            var previous = _app;
            bool ok = false;
            try
            {
                _app = new RunEverApp(_root);
                ok = _app.Init();
            }
            catch (Exception ex)
            {
                try { previous.Log("重载异常: " + ex.Message); } catch { }
            }

            if (!ok)
            {
                // 失败：把旧实例换回来（菜单照常能弹），绝不把半成品留在位子上
                _app = previous;
                try { _app.Log("⚠ 重载失败，继续使用原来的配置实例"); } catch { }
                TrayTip("RunEver 重载失败",
                    "配置可能有语法错误，已继续使用上一次可用的配置。\n"
                    + "详情见 state\\runever-cs.log", ToolTipIcon.Warning);
                return;
            }

            _app.UserNotify = msg => TrayTipThreadSafe("RunEver 执行失败", msg, ToolTipIcon.Warning);
            _app.UserNotifyTitled = (title, msg) => TrayTipThreadSafe(title, msg, ToolTipIcon.Info);
            _app.Log("手动重载配置: 成功");
            ApplyHotkey();      // 重载也可能改了 hotkey=
            ApplyMouseTrigger(); // 以及 mouseTrigger / holdMs
            TrayTip("RunEver", "配置已重载", ToolTipIcon.Info);
        }

        // ============================================================
        // 触发热键（auto.conf: hotkey=）—— 读 / 应用
        //   对照 AHK：RVE_HOTKEY 是源码全局（写死反引号，改不了）；
        //   C# 版做成配置项 + 设置界面可改 + 关设置窗即时生效。
        // ============================================================
        private HotkeySpec LoadHotkey()
        {
            try
            {
                string conf = Path.Combine(_root, "config", "auto.conf");
                if (File.Exists(conf))
                {
                    foreach (var raw in File.ReadAllLines(conf, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        if (line.Substring(0, eq).Trim().ToLowerInvariant() != "hotkey") continue;

                        string v = line.Substring(eq + 1).Trim();
                        HotkeySpec spec;
                        string err;
                        if (HotkeySpec.TryParse(v, out spec, out err))
                            return spec;

                        _app.Log("⚠ auto.conf 的 hotkey=[" + v + "] 无法解析：" + err + " → 用默认 `");
                    }
                }
            }
            catch (Exception ex)
            {
                _app.Log("⚠ 读 hotkey 失败: " + ex.Message);
            }
            return HotkeySpec.CreateDefault();
        }

        /// <summary>按 auto.conf 当前值重设热键（变了才动，未变直接返回）。</summary>
        private void ApplyHotkey()
        {
            try
            {
                if (_msgWnd == null) return;

                var want = LoadHotkey();
                if (_hotkey != null && want.Text == _hotkey.Text) return;

                var old = _hotkey;      // ★ B4b-5：失败时要能退回它
                UnregisterHotKey(_msgWnd.Handle, HOTKEY_ID);
                _hotkeyOk = RegisterHotKey(_msgWnd.Handle, HOTKEY_ID,
                    want.Modifiers | HotkeySpec.MOD_NOREPEAT, want.Vk);

                string from = old == null ? "(无)" : old.Text;

                if (_hotkeyOk)
                {
                    _hotkey = want;
                    _app.Log("热键重设: " + from + " → " + want.Describe());
                    TrayTip("RunEver 热键已改", "新热键：" + want.Text, ToolTipIcon.Info);
                    return;
                }

                // ---- 失败：回滚 ----
                // ★ B4b-5：旧实现只 `UnregisterHotKey` 就宣告失败 ⇒ 从此**一个热键都没有**
                //   （用户改错一次就永久失去弹菜单的方式），而且提示是个会被下一条气泡顶掉的气泡。
                //   现在：① 重新注册旧键；② 把 auto.conf 的 hotkey 值改回旧键（重启后也可用）；
                //   ③ 用**模态对话框**告知（不会被别的气泡顶掉，用户一定看见）。
                int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                string oldText = old == null ? "" : old.Text;

                _hotkeyOk = false;
                if (old != null)
                {
                    _hotkeyOk = RegisterHotKey(_msgWnd.Handle, HOTKEY_ID,
                        old.Modifiers | HotkeySpec.MOD_NOREPEAT, old.Vk);
                }
                _hotkey = old;

                _app.Log("⚠ 热键重设失败: " + want.Text + " err=" + err + "（可能被占用）"
                    + "；已回滚到 " + (oldText.Length > 0 ? oldText : "(无)"));

                if (oldText.Length > 0) RestoreHotkeyInConf(oldText);

                MessageBox.Show(
                    "新热键 " + want.Text + " 注册失败（多半被其它程序占用）。\n\n"
                    + (oldText.Length > 0
                        ? "已恢复原来的热键：" + oldText
                          + (_hotkeyOk ? "（可正常使用）" : "（但它现在也注册不上，请重启托盘或换个热键）")
                        : "当前没有可用热键，请换一个。")
                    + "\n\nauto.conf 里的 hotkey 也已改回原值。",
                    "RunEver 热键未生效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                _app.Log("⚠ 应用热键异常: " + ex.Message);
            }
        }

        // ============================================================
        // 鼠标触发（auto.conf: mouseTrigger=holdRight / holdMs=500）
        // ============================================================
        /// <summary>读 auto.conf 的 mouseTrigger / holdMs（缺省 = off / 500）。</summary>
        private void LoadMouseTrigger(out bool enabled, out int holdMs)
        {
            enabled = false;
            holdMs = 500;
            try
            {
                string conf = Path.Combine(_root, "config", "auto.conf");
                if (!File.Exists(conf)) return;

                foreach (var raw in File.ReadAllLines(conf, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();

                    if (k == "mousetrigger")
                        enabled = string.Equals(v, "holdRight", StringComparison.OrdinalIgnoreCase);
                    else if (k == "holdms")
                    {
                        int n;
                        if (int.TryParse(v, out n)) holdMs = n;
                    }
                }
            }
            catch (Exception ex)
            {
                _app.Log("⚠ 读 mouseTrigger 失败: " + ex.Message);
            }
        }

        /// <summary>按 auto.conf 当前值挂/卸鼠标触发（幂等：配置没变就什么都不做）。</summary>
        private void ApplyMouseTrigger()
        {
            try
            {
                bool want;
                int holdMs;
                LoadMouseTrigger(out want, out holdMs);

                if (!want)
                {
                    if (_mouse != null)
                    {
                        _mouse.Dispose();
                        _mouse = null;
                        _app.Log("鼠标触发: 关（已卸下钩子）");
                    }
                    else
                    {
                        // ★ 启动时也留一行：否则"没反应"时无法从日志判断是「没启用」还是「钩子没装上」
                        _app.Log("鼠标触发: 关（auto.conf mouseTrigger 不是 holdRight）");
                    }
                    return;
                }

                // 已有且阈值相同 ⇒ 不动（重载配置时避免无谓地拆装钩子）
                if (_mouse != null && _mouse.IsHooked && _mouse.HoldMs == holdMs)
                    return;

                if (_mouse != null) _mouse.Dispose();
                _mouse = new MouseTrigger(holdMs, _msgWnd.Handle, _app.Log);
                if (!_mouse.Install())
                {
                    _mouse.Dispose();
                    _mouse = null;
                }
            }
            catch (Exception ex)
            {
                _app.Log("⚠ 应用鼠标触发异常: " + ex.Message);
            }
        }

        /// <summary>把 auto.conf 的 hotkey 改回旧值（写盘失败只记日志 —— 已注册不上，不能连提示也吞掉）。</summary>
        private void RestoreHotkeyInConf(string oldText)
        {
            try
            {
                string conf = Path.Combine(_root, "config", "auto.conf");
                var vals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "hotkey", oldText }
                };
                var r = AutoConfStore.Write(conf, vals, null, _root);
                _app.Log(r.Ok
                    ? "热键已回写 auto.conf: hotkey=" + oldText
                    : "⚠ 热键回写 auto.conf 失败：" + r.Error);
            }
            catch (Exception ex)
            {
                _app.Log("⚠ 热键回写 auto.conf 异常：" + ex.Message);
            }
        }

        // ============================================================
        // 刷新插件（对照 AHK src\RunEver.ahk:535 RefreshPlugins）
        //   重扫 config\functions + 落 state\plugins.json（AHK 的「插件登记」文件契约）
        // ============================================================
        private void RefreshPlugins()
        {
            try
            {
                // ★ 用 AutoMatcher 而不是裸 PluginScanner：插件清单 + auto.conf 的
                //   enabled/minLen/cacheSeconds 是同一份数据源，登记文件要一起落盘。
                var am = new AutoMatcher(_root, s => _app.Log("  " + s));
                am.Load(true);     // forceRescan = 托盘「刷新插件」的语义

                string reg = Path.Combine(_root, "state", "plugins.json");
                PluginRegistry.Write(am, reg, s => _app.Log("  " + s));

                _app.Log("手动刷新插件：正常=" + am.Plugins.Count
                    + " 异常=" + am.BadPlugins.Count + "，登记 → " + reg);

                TrayTip("RunEver 刷新插件",
                    "已重扫插件目录：正常 " + am.Plugins.Count + " 个"
                    + (am.BadPlugins.Count > 0 ? "，异常 " + am.BadPlugins.Count + " 个" : "")
                    + "\n登记文件：state\\plugins.json",
                    am.BadPlugins.Count > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                _app.Log("刷新插件失败: " + ex.Message);
                TrayTip("RunEver", "刷新插件失败：" + ex.Message, ToolTipIcon.Warning);
            }
        }

        // ============================================================
        // 刷新路径缓存（对照 AHK src\RunEver.ahk:499 → :1019 TriggerCacheBuild）
        //
        //   ① 待解析程序名 = 配置里 run 类型条目的首个 token（ConfParser.CollectPrograms）
        //   ② 三级解析：App Paths → PATH → es.exe 搜索 + 加权择优（PathCacheBuilder）
        //   ③ 增量：已缓存且文件仍存在 ⇒ skip；失败的会重试
        //   ★ 必须后台线程：全量构建含 es 搜索，可能数秒；跑在托盘线程上会让托盘假死
        //     （AHK 版同期也在后台做，见 src\RunEver.ahk:1019-1038）
        // ============================================================
        private void RefreshPathCache()
        {
            // ★ B4b-7：单飞 —— 设置页可能正在刷同一份缓存（进程级闸在 PathCacheRefresh 里）
            if (_cacheBusy || PathCacheRefresh.IsRunning)
            {
                TrayTipThreadSafe("RunEver", "路径缓存正在构建中，请稍候…", ToolTipIcon.Info);
                return;
            }
            _cacheBusy = true;
            TrayTipThreadSafe("RunEver 刷新路径缓存", "开始构建，请稍候…", ToolTipIcon.Info);

            var worker = new Thread(() =>
            {
                try
                {
                    // ★ 共用服务（托盘与 CLI 同一份实现，行为不会漂移）：
                    //   搜索后端 EV（Everything64.dll）优先 → 降级 es.exe → PATH+App Paths。
                    // ★ v8（AC-8 原 gap①）：走 _app.RefreshPathCache() —— 它把结果
                    //   **就地回灌运行实例**（同一份 PathCache），刷新后本进程立即看到新缓存。
                    var r = _app.RefreshPathCache();

                    if (!r.Ok)
                    {
                        TrayTipThreadSafe("RunEver", "刷新路径缓存失败：" + r.Message, ToolTipIcon.Warning);
                        return;
                    }

                    string msg = r.Build.ToString() + "\n缓存条目：" + r.CacheCount
                        + "\n搜索后端：" + r.Backend
                        + (string.IsNullOrEmpty(r.BackendReason) ? "" : "（" + r.BackendReason + "）");
                    if (r.Build.FailedList.Count > 0)
                        msg += "\n未解析到（本机可能没装）：" + string.Join(", ", r.Build.FailedList.ToArray());

                    TrayTipThreadSafe("RunEver 路径缓存已刷新", msg,
                        r.Build.Failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    _app.Log("刷新路径缓存失败: " + ex.Message);
                    TrayTipThreadSafe("RunEver", "刷新路径缓存失败：" + ex.Message, ToolTipIcon.Warning);
                }
                finally
                {
                    _cacheBusy = false;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        // ============================================================
        // 同步配置（v7 WebDAV 配置同步）
        //
        //   ① 与 CLI `--sync` **共用同一份** ConfigSync.Run（同一件事两套实现必然漂移）
        //   ② 网络 IO **必须后台线程**（跑在托盘线程上会让托盘假死）
        //   ③ 防重入 _syncBusy；气泡走 TrayTipThreadSafe（后台线程也能安全弹）
        //   ④ 未配置端点 ⇒ 零动作，只给一条"已跳过"提示（不是错误）
        // ============================================================
        private void SyncConfig()
        {
            SyncConfig(false);
        }

        private void SyncConfig(bool fromStartup)
        {
            if (_syncBusy)
            {
                if (!fromStartup)
                    TrayTipThreadSafe("RunEver", "配置同步正在进行中，请稍候…", ToolTipIcon.Info);
                return;
            }
            _syncBusy = true;
            if (!fromStartup)
                TrayTipThreadSafe("RunEver 同步配置", "正在与 WebDAV 对齐，请稍候…", ToolTipIcon.Info);

            var worker = new Thread(() =>
            {
                try
                {
                    // ★ 与 CLI / 自检完全同一份实现
                    var r = ConfigSync.Run(_root, s => _app.Log(s));

                    if (r.NotConfigured)
                    {
                        _app.Log("SYNC 托盘：未配置 WebDAV，已跳过（未动任何文件）");
                        if (!fromStartup)
                            TrayTipThreadSafe("RunEver",
                                "未配置 WebDAV，已跳过同步。\n到「设置 → 诊断 → 同步（WebDAV）」里填写服务器地址即可。",
                                ToolTipIcon.Info);
                        return;
                    }

                    if (!r.Ok)
                    {
                        // 失败文案只给可读原因，绝不回显口令（凭据红线）
                        TrayTipThreadSafe("RunEver 同步失败",
                            "原因：" + r.ErrorKind + "\n" + r.Message
                            + "\n\n已完成的保留，未处理的一个也没动。",
                            ToolTipIcon.Warning);
                        return;
                    }

                    TrayTipThreadSafe("RunEver 同步完成", r.SummaryLine(), ToolTipIcon.Info);
                }
                catch (Exception ex)
                {
                    // ★ 凭据红线兜底：任何要落日志/气泡的文本都先过 cfg.Redact
                    string safe = WebDavConfig.Load(_root).Redact(ex.Message);
                    _app.Log("SYNC 托盘异常: " + safe);
                    TrayTipThreadSafe("RunEver", "同步失败：" + safe, ToolTipIcon.Warning);
                }
                finally
                {
                    _syncBusy = false;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        // ============================================================
        // 全盘自检（功能 B，用户 2026-10-03）
        //
        //   ① 配置驱动：menus.conf 里全部 run/open 条目逐条判存在性
        //      （缓存 → PATH → App Paths → 搜索；搜索级注入 EV 直连后端）
        //   ② 结果：未安装清单 + 建议 choco 包名 + 可复制/可导出的命令清单
        //      **不做一键安装**（不弹 UAC、不自动跑 choco）
        //   ③ 顺带把解析到的真实路径写回 cache\paths.txt（等于一次按需重建，B5）
        //   ★ 必须后台线程：全量条目 × 搜索查询是秒级；跑在托盘线程上会让托盘假死
        // ============================================================
        private bool _selfCheckBusy;
        private Views.SelfCheckWindow _selfCheckWindow;

        private void RunSelfCheck()
        {
            // 窗口还开着 = 上一份报告还没看完，再跑一份只会让人分不清哪个是哪个
            if (_selfCheckWindow != null)
            {
                try { _selfCheckWindow.Activate(); } catch { }
                TrayTipThreadSafe("RunEver", "自检结果窗口还开着，请先关掉它再重新自检。", ToolTipIcon.Info);
                return;
            }
            if (_selfCheckBusy)
            {
                TrayTipThreadSafe("RunEver", "全盘自检正在进行中，请稍候…", ToolTipIcon.Info);
                return;
            }
            _selfCheckBusy = true;
            TrayTipThreadSafe("RunEver 全盘自检", "开始检查配置里的条目，请稍候…", ToolTipIcon.Info);

            var worker = new Thread(() =>
            {
                try
                {
                    _app.Log("全盘自检开始");
                    var rep = SelfCheck.Run(_root,
                        s => _app.Log("  [自检] " + s),
                        null,                       // 进度回调：本版不弹进度气泡，避免刷屏
                        null,                       // 取消：托盘版不提供
                        true);                      // 写回 cache\paths.txt（B5）

                    _app.Log("全盘自检完成：" + rep.SummaryLine());

                    // 报告落盘（即使窗口被关掉，也有据可查）
                    try
                    {
                        string rf = Path.Combine(_root, "state", "selfcheck-report.txt");
                        string rdir = Path.GetDirectoryName(rf);
                        if (!string.IsNullOrEmpty(rdir) && !Directory.Exists(rdir))
                            Directory.CreateDirectory(rdir);
                        File.WriteAllText(rf, rep.Render(), new UTF8Encoding(false));

                        // ★ B4b-9：留痕 —— 一行人话结论落到 state\selfcheck-last.txt，
                        //   设置页「诊断」与下次开窗都能读到"上次检查于几点、结果如何"。
                        File.WriteAllText(Path.Combine(_root, "state", "selfcheck-last.txt"),
                            rep.LastCheckLine() + Environment.NewLine, new UTF8Encoding(false));

                        // ★ B5-6：**已确认缺失**时才生成安装清单（不自动装、不弹 UAC、不跑 choco）；
                        //   窗口里会给「打开清单」按钮，路径固定 state\selfcheck-choco.bat。
                        if (rep.ChocoCommands().Count > 0)
                            File.WriteAllText(Path.Combine(_root, "state", "selfcheck-choco.bat"),
                                rep.BuildBat(), new UTF8Encoding(false));
                    }
                    catch { }

                    // ★ B5-1/3：气泡用人话（机器字段版仍写日志与报告文件）
                    TrayTipThreadSafe("RunEver 全盘自检完成", rep.HumanSummary(),
                        rep.BackendTrusted()
                            ? (rep.Missing + rep.Unknown > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info)
                            : ToolTipIcon.Warning);

                    ShowSelfCheckWindow(rep);
                }
                catch (Exception ex)
                {
                    _app.Log("全盘自检失败: " + ex.Message);
                    TrayTipThreadSafe("RunEver", "全盘自检失败：" + ex.Message, ToolTipIcon.Warning);
                }
                finally
                {
                    _selfCheckBusy = false;
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>结果窗口必须在 UI 线程创建（后台线程 new Window 会抛）。</summary>
        private void ShowSelfCheckWindow(SelfCheckReport rep)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app == null) return;
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var w = new Views.SelfCheckWindow(_root, rep);
                        w.Closed += (s, e) => { _selfCheckWindow = null; };
                        _selfCheckWindow = w;
                        w.Show();
                        w.Activate();
                    }
                    catch (Exception ex)
                    {
                        _selfCheckWindow = null;
                        _app.Log("打开自检结果窗口失败: " + ex.Message);
                    }
                }));
            }
            catch (Exception ex)
            {
                _app.Log("派发自检窗口失败: " + ex.Message);
            }
        }

        /// <summary>后台线程也能安全弹气泡（WinForms 托盘要在 UI 线程上调）。</summary>
        private void TrayTipThreadSafe(string title, string text, ToolTipIcon icon)
        {
            try
            {
                var app = System.Windows.Application.Current;
                if (app != null && !app.Dispatcher.CheckAccess())
                {
                    app.Dispatcher.BeginInvoke(new Action(() => TrayTip(title, text, icon)));
                    return;
                }
            }
            catch { }
            TrayTip(title, text, icon);
        }


        /// <summary>打开插件登记文件；不存在则先刷新一次（避免点了没反应）。</summary>
        private void OpenPluginRegistry()
        {
            try
            {
                string reg = Path.Combine(_root, "state", "plugins.json");
                if (!File.Exists(reg)) RefreshPlugins();
                if (File.Exists(reg)) OpenPath(reg, "插件登记");
                else
                {
                    // ★ B4b-6：刷新后仍没有 ⇒ 明确告诉用户文件路径与原因，不静默
                    _app.Log("插件登记文件缺失，且刷新未生成：" + reg);
                    TrayTipThreadSafe("RunEver 打不开插件登记",
                        "插件登记文件还没生成：\n" + reg
                        + "\n\n（多半是 config\\functions 下没有可用的插件）", ToolTipIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                _app.Log("打开插件登记失败: " + ex.Message);
            }
        }

        private void ShowStatus()
        {
            string s = "RunEver 运行中（常驻托盘）\n\n"
                + "根目录：" + _root + "\n"
                + "热键：" + (_hotkey == null ? "(未设置)" : _hotkey.Describe())
                + (_hotkeyOk ? "" : "  ⚠ 注册失败") + "\n"
                + "开机自启：" + AutoStartSummary() + "\n"
                + "日志级别：" + _app.LogLevel + "\n"
                + "日志文件：state\\runever-cs.log\n\n"
                + "提示：按钮「重载配置」不会重启本程序。";
            MessageBox.Show(s, "RunEver 状态",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void TrayTip(string title, string text, ToolTipIcon icon)
        {
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = icon;
                _tray.ShowBalloonTip(3000);
            }
            catch { }
        }

        /// <summary>用系统默认程序打开一个文件/目录（label 仅用于日志与提示里说人话）。</summary>
        private void OpenPath(string p, string label = null)
        {
            string what = string.IsNullOrEmpty(label) ? "文件" : label;
            try
            {
                // ★ B4b-6：不存在就明确说「哪个路径不存在」，不要静默失败。
                //   原来 ShellExecute 对不存在的路径只是返回错误码，用户看到的是"点了没反应"。
                bool exists = File.Exists(p) || Directory.Exists(p);
                if (!exists)
                {
                    _app.Log("打开失败（" + what + "不存在）：" + p);
                    TrayTipThreadSafe("RunEver 打不开" + what,
                        what + "还不存在：\n" + p
                        + "\n\n（还没生成 / 已被移动或删除）", ToolTipIcon.Warning);
                    return;
                }

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = p,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _app.Log("打开失败（" + what + "）" + p + ": " + ex.Message);
                TrayTipThreadSafe("RunEver 打不开" + what,
                    "路径：" + p + "\n原因：" + ex.Message, ToolTipIcon.Warning);
            }
        }

        public void Shutdown()
        {
            try { _app.Log("=== 退出常驻托盘模式 ==="); } catch { }
            RemoveHwndFile();
            Dispose();
            System.Windows.Application.Current.Shutdown();
            Environment.Exit(0);
        }

        // ============================================================
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 由本托盘拉起的设置窗口进程（2026-11-12，托盘↔设置联动关闭）。
        ///
        /// 设置是**独立进程**（8b7c496：WPF 栈不再常驻托盘，省 ~160MB 私有内存），
        /// 代价是托盘退出不会带走它 —— 用户看到的现象就是「关掉托盘，设置窗口还在」，
        /// 且这时窗口里的「保存」仍能写盘、却没有托盘重新加载 ⇒ 僵尸窗口。
        /// 这里在托盘退出时统一收尾（另一条保险在 Program.StartParentWatch：父进程没了子进程自己退）。
        /// </summary>
        private readonly System.Collections.Generic.List<System.Diagnostics.Process> _settingsProcs
            = new System.Collections.Generic.List<System.Diagnostics.Process>();

        /// <summary>托盘退出时关闭所有由自己拉起的设置进程。先礼后兵：CloseMainWindow → 1.5s → Kill。</summary>
        private void CloseSettingsProcesses()
        {
            System.Diagnostics.Process[] arr;
            lock (_settingsProcs)
            {
                arr = _settingsProcs.ToArray();
                _settingsProcs.Clear();
            }
            if (arr.Length == 0) return;

            foreach (var p in arr)
            {
                try { p.Refresh(); if (!p.HasExited) p.CloseMainWindow(); }   // WPF 收 WM_CLOSE 正常关窗
                catch { }
            }
            foreach (var p in arr)
            {
                try
                {
                    if (p.HasExited) continue;
                    if (!p.WaitForExit(1500)) p.Kill();                       // 关不掉（模态框/卡死）就强杀
                }
                catch { }
            }
            try { if (_app != null) _app.Log("退出联动：已关闭设置进程 " + arr.Length + " 个"); } catch { }
        }

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { CloseSettingsProcesses(); } catch { }
            try { if (_confWatchTimer != null) { _confWatchTimer.Dispose(); _confWatchTimer = null; } } catch { }
            try { if (_mouse != null) { _mouse.Dispose(); _mouse = null; } } catch { }
            try
            {
                if (_msgWnd != null && _msgWnd.Handle != IntPtr.Zero)
                    UnregisterHotKey(_msgWnd.Handle, HOTKEY_ID);
            }
            catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            try { if (_msgWnd != null) _msgWnd.Dispose(); } catch { }
            try { if (_mutex != null) _mutex.ReleaseMutex(); } catch { }
            try { RemoveHwndFile(); } catch { }
            GC.SuppressFinalize(this);
        }
    }
}
