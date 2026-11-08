using System;
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

            // ---- 托盘 ----
            BuildTray();

            // ---- 暴露消息窗口句柄（供 --trigger 与 AHK 助手触发）----
            WriteHwndFile();

            // ★ 2026-11-08：菜单固定走 Win32 ⇒ 托盘进程**不再**预热 WPF/MahApps 主题栈
            //   （那是 uiBackend=wpf 时代为"首次弹菜单少付 230ms"准备的，同时也把 ~60MB
            //   主题字典焊死在常驻进程里）。WPF 只在设置窗口（独立进程）里出现。
            _app.Log("[diag] 菜单=Win32（WPF 仅设置窗口）| capture=ahk-resident");

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

            menu.Items.Add(new ToolStripSeparator());

            // ★ 对齐 AHK 托盘「刷新路径缓存」（src\RunEver.ahk:499 → :1019 TriggerCacheBuild）
            var miRefreshCache = new ToolStripMenuItem("刷新路径缓存");
            miRefreshCache.Click += (s, e) => RefreshPathCache();
            menu.Items.Add(miRefreshCache);

            // ★ 功能 B（用户 2026-10-03）：全盘自检 —— 配置驱动逐条查存在性
            //   结果窗口只给「未安装清单 + choco 命令清单」，**不做一键安装**（不弹 UAC、不跑 choco）
            var miSelfCheck = new ToolStripMenuItem("全盘自检…");
            miSelfCheck.Click += (s, e) => RunSelfCheck();
            menu.Items.Add(miSelfCheck);

            // ★ 对齐 AHK 托盘「刷新插件」（src\RunEver.ahk:500 → :535 RefreshPlugins）
            var miRefreshFn = new ToolStripMenuItem("刷新插件");
            miRefreshFn.Click += (s, e) => RefreshPlugins();
            menu.Items.Add(miRefreshFn);

            // ★ v7（WebDAV 配置同步）：把「菜单配置 + 插件」与远端双向对齐（新者胜 + 覆盖前备份）。
            //   webdavUrl 留空 ⇒ 安静跳过，**不动任何文件**（不是报错、更不是清空配置）。
            var miSync = new ToolStripMenuItem("同步配置");
            miSync.Click += (s, e) => SyncConfig();
            menu.Items.Add(miSync);

            // ★ 对齐 AHK 托盘（src\RunEver.ahk:501-505）：打开类入口
            var miConf = new ToolStripMenuItem("打开配置（菜单）");
            miConf.Click += (s, e) => OpenPath(Path.Combine(_root, "config", "menus.conf"));
            menu.Items.Add(miConf);

            var miAutoConf = new ToolStripMenuItem("打开自动执行配置");
            miAutoConf.Click += (s, e) => OpenPath(Path.Combine(_root, "config", "auto.conf"));
            menu.Items.Add(miAutoConf);

            var miPlugins = new ToolStripMenuItem("打开插件目录");
            miPlugins.Click += (s, e) => OpenPath(Path.Combine(_root, "config", "functions"));
            menu.Items.Add(miPlugins);

            var miFnReg = new ToolStripMenuItem("打开插件登记");
            miFnReg.Click += (s, e) => OpenPluginRegistry();
            menu.Items.Add(miFnReg);

            var miLog = new ToolStripMenuItem("打开日志");
            miLog.Click += (s, e) => OpenPath(Path.Combine(_root, "state", "runever-cs.log"));
            menu.Items.Add(miLog);

            menu.Items.Add(new ToolStripSeparator());

            // ★ 开机自启开关（用户点击才写注册表，程序不擅自改系统配置）
            var miAuto = new ToolStripMenuItem("开机自启")
            {
                CheckOnClick = true,
                Checked = IsAutoStartEnabled()
            };
            miAuto.Click += (s, e) =>
            {
                bool want = miAuto.Checked;
                if (SetAutoStart(want))
                {
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

            var miStatus = new ToolStripMenuItem("状态…");
            miStatus.Click += (s, e) => ShowStatus();
            menu.Items.Add(miStatus);

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
        // ============================================================
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValueName = "RunEver";

        private bool IsAutoStartEnabled()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath))
                {
                    if (k == null) return false;
                    var v = k.GetValue(RunValueName) as string;
                    return !string.IsNullOrEmpty(v);
                }
            }
            catch { return false; }
        }

        private bool SetAutoStart(bool on)
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (k == null) return false;

                    if (on)
                    {
                        string self = "";
                        using (var proc = System.Diagnostics.Process.GetCurrentProcess())
                            self = proc.MainModule.FileName;

                        // 注意：路径含空格必须加引号，否则注册表 Run 项会解析错
                        k.SetValue(RunValueName,
                            "\"" + self + "\" --root \"" + _root + "\"");
                    }
                    else
                    {
                        k.DeleteValue(RunValueName, false);
                    }
                    return true;
                }
            }
            catch { return false; }
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

        private void OpenSettings()
        {
            try
            {
                // ★ v8（AC-5②）：编辑器保存 menus.conf 后触发**后台增量解析**
                //   （否则「按缓存隐藏」会把刚加进来的条目直接隐藏 = 死角）。
                // ★ 段 1.6（B2）：常规页保存 auto.conf 后走**另一个**轻量回调：
                //   只重载 auto.conf 的显示类开关（hideMissing 等）—— 这正是
                //   「设置→常规→勾选隐藏本机没有的条目」改了不生效的三层原因之一
                //   （此前 GeneralPage 没有任何钩子，托盘永远是启动时那份内存态）。
                var w = new Views.SettingsWindow(_root,
                    () =>
                    {
                        if (_app != null) _app.OnConfigSaved();
                    },
                    () =>
                    {
                        if (_app != null) _app.OnAutoConfSaved();
                    });
                // ★ 设置里可能改了触发热键（auto.conf hotkey=）→ 关窗即重注册
                w.Closed += (s, e) => ApplyHotkey();
                w.Show();
                w.Activate();
            }
            catch (Exception ex)
            {
                _app.Log("打开设置失败: " + ex.Message);
                MessageBox.Show("打开设置失败：\n" + ex.Message,
                    "RunEver", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ReloadConfig()
        {
            try
            {
                bool ok;
                _app = new RunEverApp(_root);
                ok = _app.Init();
                // ★ v8：重载后同样接上失败气泡出口（与 Start 一致）
                _app.UserNotify = msg => TrayTipThreadSafe("RunEver 执行失败", msg, ToolTipIcon.Warning);
                _app.Log("手动重载配置: " + (ok ? "成功" : "失败"));
                ApplyHotkey();      // 重载也可能改了 hotkey=
                TrayTip("RunEver", ok ? "配置已重载" : "重载失败，见日志",
                    ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                _app.Log("重载异常: " + ex.Message);
            }
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

                UnregisterHotKey(_msgWnd.Handle, HOTKEY_ID);
                _hotkeyOk = RegisterHotKey(_msgWnd.Handle, HOTKEY_ID,
                    want.Modifiers | HotkeySpec.MOD_NOREPEAT, want.Vk);

                string from = _hotkey == null ? "(无)" : _hotkey.Text;
                _hotkey = want;

                if (_hotkeyOk)
                {
                    _app.Log("热键重设: " + from + " → " + want.Describe());
                    TrayTip("RunEver 热键已改", "新热键：" + want.Text, ToolTipIcon.Info);
                }
                else
                {
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    _app.Log("⚠ 热键重设失败: " + want.Text + " err=" + err + "（可能被占用）");
                    TrayTip("RunEver 热键注册失败",
                        want.Text + " 可能被其它程序占用，请在设置里换一个。", ToolTipIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                _app.Log("⚠ 应用热键异常: " + ex.Message);
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
            if (_cacheBusy)
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
                                "未配置 WebDAV，已跳过同步。\n到「设置 → 常规 → 同步」里填写地址即可。",
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
                    }
                    catch { }

                    TrayTipThreadSafe("RunEver 全盘自检完成", rep.SummaryLine(),
                        (rep.Missing > 0 || rep.Unknown > 0) ? ToolTipIcon.Warning : ToolTipIcon.Info);

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
                if (File.Exists(reg)) OpenPath(reg);
                else _app.Log("插件登记文件缺失，且刷新失败");
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
                + "开机自启：" + (IsAutoStartEnabled() ? "已开启" : "未开启") + "\n"
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

        private void OpenPath(string p)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = p,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                _app.Log("打开失败 " + p + ": " + ex.Message);
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

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
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
