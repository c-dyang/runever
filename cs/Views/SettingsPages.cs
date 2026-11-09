using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    // ============================================================
    // 2026-11-07 设置 UI 全面重建 —— 各主题页
    //
    // 旧结构：4 个顶部 Tab，其中「常规」一页塞了 6 个 GroupBox（触发/基本/日志/同步），
    //   又长又挤，用户原话「这个风格真的难用」。
    // 新结构：左导航 + 每页卡片式分组，一页只讲一个主题。
    // ============================================================

    /// <summary>触发与行为：热键、自动执行、菜单显示策略、引擎后端、插件缓存。</summary>
    internal class TriggerPage : AutoConfPage
    {
        public TriggerPage(string root) : base(root) { }

        protected override UIElement Build()
        {
            var sp = new StackPanel();

            sp.Children.Add(SettingsKit.SectionHeader("触发"));

            var hotkeyBox = new TextBox
            {
                Text = Get("hotkey", "`"),
                Width = 150,
                // ★ B4b-4：组合键最长形如 "#^+F24"（5 字符）⇒ 上限 6，防手滑粘一大段进来
                MaxLength = 6,
                Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = "按一个键即可填入；支持 F1–F24 与 Space（显示为 Space）；Backspace 清空"
            };
            var hotkeyRow = SettingsKit.Row("弹出热键",
                "按一个键即可填入（Backspace 清空）；支持 F1–F24 与 Space；单字母/数字需配 Ctrl/Alt",
                hotkeyBox);
            // 2026-11-07：按键捕获。
            // ★ 与菜单内快捷键不同，**全局热键不能是裸字母/数字** ——
            //   否则按下就该字符就会被吞掉去弹菜单（HotkeySpec 也会拒绝并回落默认）。
            //   这里直接拦下来，并明确提示；组合键（Ctrl/Alt/Shift/Win + 字符）照常接受。
            hotkeyBox.PreviewKeyDown += (s, e) =>
            {
                var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
                if (key == System.Windows.Input.Key.Back || key == System.Windows.Input.Key.Delete)
                {
                    hotkeyBox.Text = "";
                    e.Handled = true;
                    return;
                }

                string ch = SettingsKit.KeyToHotkeyChar(key);
                if (ch == null) return;

                bool isAlnum = (key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z)
                            || (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9);
                var mods = System.Windows.Input.Keyboard.Modifiers;

                if (isAlnum && mods == System.Windows.Input.ModifierKeys.None)
                {
                    hotkeyBox.ToolTip = "单字母/数字会吞掉正常输入，不可用作全局热键。"
                        + "请加 Ctrl/Alt/Shift/Win，或改用 ` - = [ ] ; ' , . / 或 F1-F24。";
                    e.Handled = true;
                    return;
                }

                var sb = new System.Text.StringBuilder();
                if ((mods & System.Windows.Input.ModifierKeys.Control) != 0) sb.Append('^');
                if ((mods & System.Windows.Input.ModifierKeys.Alt) != 0) sb.Append('!');
                if ((mods & System.Windows.Input.ModifierKeys.Shift) != 0) sb.Append('+');
                if ((mods & System.Windows.Input.ModifierKeys.Windows) != 0) sb.Append('#');
                sb.Append(ch);
                hotkeyBox.Text = sb.ToString();
                e.Handled = true;
            };
            hotkeyBox.LostFocus += (s, e) =>
            {
                // ★ B4b-4：不能用 `Text.Trim()` —— 纯空格会被抹成空串，热键静默回落默认值
                string v = SettingsKit.TrimHotkeyText(hotkeyBox.Text);
                if (v != Get("hotkey", "`")) Set("hotkey", v);
            };

            sp.Children.Add(SettingsKit.Card(
                hotkeyRow,
                SettingsKit.Toggle("启用自动执行",
                    "选中文本 → 匹配插件 → 直接执行，不弹菜单",
                    GetBool("enabled", true), v => SetBool("enabled", v)),
                SettingsKit.Text("最小文本长度", "短于该长度的选中文本不触发自动执行",
                    Get("minLen", "4"), v => SetInt("minLen", PInt(v, 4)), 90),
                // ★ 2026-11-09（U1）：键先立好，判定层实现在「一键直达」卡里接（L1 MenuDecision）
                SettingsKit.Toggle("一键直达（仅扩展名）",
                    "扩展名槽位只匹配到 1 个可执行条目时直接执行，不弹菜单；action / send / keys 不直达",
                    GetBool("oneShot", false), v => SetBool("oneShot", v))));

            sp.Children.Add(SettingsKit.SectionHeader("菜单显示"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Toggle("隐藏本机没有的条目",
                    "run / open 条目在本机解析不到时不显示（配置来自别的机器时有用）",
                    GetBool("hideMissing", false), v => SetBool("hideMissing", v)),
                SettingsKit.Toggle("默认显示执行窗口",
                    "命令行工具需要看输出时打开",
                    GetBool("showByDefault", false), v => SetBool("showByDefault", v)),
                SettingsKit.Toggle("显示窗口时等待命令结束",
                    "窗口留存，可看进度",
                    GetBool("waitWhenShown", true), v => SetBool("waitWhenShown", v))));

            // ★ 2026-11-09（U1）：四个文件夹子菜单键 + 批量执行上限，界面先立好，
            //   渲染/解析/执行三处的实现在各自的卡里接（open sub / 批量执行）。
            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Toggle("文件夹条目展开为子菜单",
                    "menus.conf 里用 open sub <目录> 时，弹菜单当场枚举该目录生成子菜单；目录空/无权限则不展开",
                    GetBool("submenuEnabled", false), v => SetBool("submenuEnabled", v)),
                SettingsKit.Text("子菜单最多几项",
                    "超出部分截断，防止大目录把菜单撑爆",
                    Get("submenuLimit", "20"), v => SetInt("submenuLimit", PInt(v, 20)), 90),
                SettingsKit.Combo("子菜单排序", "目录在前，或纯按名称",
                    new[] { "目录优先", "按名称" },
                    Get("submenuOrder", "folder-first") == "name" ? "按名称" : "目录优先",
                    v => Set("submenuOrder", v == "按名称" ? "name" : "folder-first")),
                SettingsKit.Toggle("子菜单递归展开",
                    "对子菜单里的目录继续展开（默认关：绝大多数场景两层就够）",
                    GetBool("submenuRecursive", false), v => SetBool("submenuRecursive", v)),
                SettingsKit.Text("递归层数",
                    "仅在开启递归时生效；上限 2（再深没人点）",
                    Get("submenuDepth", "2"), v => SetInt("submenuDepth", PInt(v, 2)), 90)));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Text("批量执行上限",
                    "多选文件时逐条循环执行同一条目；超过该数量只做前 N 个（防手滑选中整个盘）",
                    Get("batchMax", "10"), v => SetInt("batchMax", PInt(v, 10)), 90)));

            // ★ 2026-11-08：菜单三段顺序（auto.conf 的 menuOrder）
            //   为什么放这里：它决定"弹出来的菜单里谁在上面"，属于菜单显示策略，不属外观。
            //   为什么用上下移按钮而不是拖拽：设置页没有拖拽基础设施，原生 Button 零依赖、
            //   不会碰出焦点/命中测试类回归（菜单项那个树才值得上拖拽）。
            //   为什么只有三段：兜底与动态是**同一格**（I-1 互斥，都落在 Dynamic 槽位），
            //   给兜底单列一格等于宣称两者会并列出现 —— 那是错的模型。
            sp.Children.Add(SettingsKit.SectionHeader("菜单顺序"));

            _order = new List<string>(MenuOrderSpec.Parse(Get("menuOrder", "")));
            _orderHost = new StackPanel();
            RenderOrder();

            sp.Children.Add(SettingsKit.Card(
                _orderHost,
                // ★ 2026-11-09（U1）：三条解释性长句合成整段（原来各占一行 Info + 分隔线，
                //   看着像三个可配置项）。见 SettingsKit.Note 的注释。
                SettingsKit.Note(
                    "改完立即写盘，下一次弹菜单就按新顺序（不用重启托盘）。\n" +
                    "某一档为空时那一块不出现 —— 例如没选中东西、且配置里没有兜底分组时，动态那格是空的，此时排它不会有可见变化。\n" +
                    "分组内条目的先后仍去「菜单项」页用拖拽调整，这里只排三段内容块的先后。")));

            sp.Children.Add(SettingsKit.SectionHeader("插件"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Text("插件元数据缓存（秒）",
                    "超时后重扫 config\\functions；频繁改插件时调小",
                    Get("cacheSeconds", "300"), v => SetInt("cacheSeconds", PInt(v, 300)), 90)));

            // ★ B1-6：「插件」页原样并入本页末尾（不再单独占一格导航）。
            //   固定高度是必须的：PluginPage 内部是 DataGrid + DockPanel 的"填满父容器"布局，
            //   直接塞进外层可滚动 StackPanel（无限高）会让它按内容撑开、滚轮行为打架。
            sp.Children.Add(new PluginPage(Root) { Height = 430, Margin = new Thickness(0, 6, 0, 0) });

            return sp;
        }

        // ---- 菜单顺序（四段重排）----
        private List<string> _order;
        private StackPanel _orderHost;

        /// <summary>按当前顺序重画四行（每次移动后整块重建，行数固定 4，开销可忽略）。</summary>
        private void RenderOrder()
        {
            if (_orderHost == null || _order == null) return;
            _orderHost.Children.Clear();

            for (int i = 0; i < _order.Count; i++)
            {
                int idx = i;
                Button up = OrderButton("\u2191", "上移", i > 0);
                Button down = OrderButton("\u2193", "下移", i < _order.Count - 1);
                up.Click += (s, e) => MoveOrder(idx, -1);
                down.Click += (s, e) => MoveOrder(idx, 1);

                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(up);
                down.Margin = new Thickness(6, 0, 0, 0);
                row.Children.Add(down);

                _orderHost.Children.Add(SettingsKit.Row(
                    (i + 1) + ". " + MenuOrderSpec.Label(_order[idx]),
                    MenuOrderSpec.Desc(_order[idx]), row));
            }
        }

        /// <summary>交换相邻两段并立即落盘（AutoConfPage.Set 会触发托盘重载）。</summary>
        private void MoveOrder(int idx, int delta)
        {
            int j = idx + delta;
            if (j < 0 || j >= _order.Count) return;
            string t = _order[idx];
            _order[idx] = _order[j];
            _order[j] = t;
            RenderOrder();
            Set("menuOrder", MenuOrderSpec.ToConf(_order.ToArray()));
        }

        private static Button OrderButton(string text, string tip, bool enabled)
        {
            return new Button
            {
                Content = text,
                ToolTip = tip,
                Width = 34,
                Height = 26,
                IsEnabled = enabled,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private static int PInt(string s, int def)
        {
            int n;
            return int.TryParse((s ?? "").Trim(), out n) ? n : def;
        }
    }

    /// <summary>路径解析：搜索后端、缓存状态、立即刷新。</summary>
    internal class PathResolvePage : AutoConfPage
    {
        public PathResolvePage(string root) : base(root) { }

        protected override UIElement Build()
        {
            var sp = new StackPanel();

            sp.Children.Add(SettingsKit.SectionHeader("搜索后端"));
            _refreshBtn = RefreshButton();

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Text("Everything 命令行",
                    "es.exe 路径；EV 接口不可用时用它兜底",
                    Get("espath", ""), v => Set("espath", v), 380),
                SettingsKit.Text("Everything DLL",
                    "Everything64.dll 路径；留空 = 自动找 dist\\Everything64.dll",
                    Get("evdll", ""), v => Set("evdll", v), 380)));

            sp.Children.Add(SettingsKit.SectionHeader("缓存"));

            string cachePath = System.IO.Path.Combine(Root, "cache", "paths.txt");
            int count = 0;
            try
            {
                if (System.IO.File.Exists(cachePath))
                    count = System.IO.File.ReadAllLines(cachePath).Length;
            }
            catch { }

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Info("缓存文件", cachePath),
                SettingsKit.Info("当前条目数", count.ToString()),
                SettingsKit.Row("刷新路径缓存",
                    "重新解析全部程序名；后台执行，不会卡住窗口（同一时刻只允许一个刷新）",
                    _refreshBtn)));

            return sp;
        }

        /// <summary>「立即刷新」按钮（要能置灰/改字，所以不用 ButtonRow）。</summary>
        private Button _refreshBtn;

        private Button RefreshButton()
        {
            var b = new Button
            {
                Content = "立即刷新",
                Padding = new Thickness(14, 5, 14, 5),
                MinWidth = 90
            };
            b.Click += (s, e) => RefreshCache(b);
            return b;
        }

        /// <summary>
        /// 立即刷新（B4b-7）：① 后台线程跑（全量解析是秒级，跑 UI 线程会假死）；
        /// ② 结果说人话（`HumanSummary()`，绝不出现 .NET 类型名）；③ 单飞闸在
        /// `PathCacheRefresh.Run` 里，托盘那边同时刷新只会有一个真的在跑。
        /// </summary>
        private void RefreshCache(Button btn)
        {
            if (PathCacheRefresh.IsRunning)
            {
                MessageBox.Show("已经有一次刷新正在进行中，请等它结束。", "RunEver",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            btn.IsEnabled = false;
            btn.Content = "刷新中…";

            var worker = new System.Threading.Thread(() =>
            {
                PathCacheRefresh.Result r = null;
                Exception err = null;
                try { r = PathCacheRefresh.Run(Root, null); }
                catch (Exception ex) { err = ex; }

                Action done = () =>
                {
                    btn.IsEnabled = true;
                    btn.Content = "立即刷新";
                    string text;
                    if (err != null)
                        text = "没能刷新路径缓存。\n\n原因：" + err.Message + "\n\n（详情见 state\\runever-cs.log）";
                    else
                        text = r.HumanSummary();

                    MessageBox.Show(text, "RunEver", MessageBoxButton.OK,
                        (err != null || (r != null && !r.Ok)) ? MessageBoxImage.Warning : MessageBoxImage.Information);
                };

                try { Dispatcher.BeginInvoke(done); }
                catch { /* 窗口已关：结果没人看了，正常 */ }
            });
            worker.IsBackground = true;
            worker.Start();
        }
    }

    /// <summary>
    /// 同步（WebDAV）。
    /// ★ 2026-11-09（A4）：整块从「诊断」页搬出来独立成页 —— 「同步」是**要配的**，
    ///   和「诊断」里那些**只是看看的**（日志/目录/关于）不是一类事；塞在一起时
    ///   用户找同步得先知道它住在排障页里。
    ///   控件、键名（webdavUrl/User/Pass/Dir、syncOnStart）、`LostFocus` 即存、口令脱敏钩子
    ///   全部原样搬过来，只换了宿主页 ⇒ 「改一项存一项」和 `--sync` 链路零变更。
    /// </summary>
    internal class SyncPage : AutoConfPage
    {
        public SyncPage(string root) : base(root) { }

        protected override UIElement Build()
        {
            var sp = new StackPanel();

            sp.Children.Add(SettingsKit.SectionHeader("同步（WebDAV）"));

            // ★ 未配置时**不置灰**（置灰会让人永远填不进来 —— 自锁），改成一句人话说明：
            //   填了地址以后托盘才会出现「同步配置」（托盘那一侧是整项不生成，见 B1-5）。
            bool syncReady = (Get("webdavUrl", "") ?? "").Trim().Length > 0;

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Info("当前状态", syncReady
                    ? "已配置 —— 托盘右键菜单里会出现「同步配置」"
                    : "未配置 —— 填上服务器地址后，托盘右键菜单里才会出现「同步配置」"),
                SettingsKit.Text("服务器地址", "如 https://nas.example.com:5006/menus",
                    Get("webdavUrl", ""), v => Set("webdavUrl", v), 380),
                SettingsKit.Text("用户名", null, Get("webdavUser", ""), v => Set("webdavUser", v), 220),
                SettingsKit.Text("密码", "写入日志时自动脱敏", Get("webdavPass", ""), v => Set("webdavPass", v), 220),
                SettingsKit.Text("远端目录", "留空 = 直接用地址本身",
                    Get("webdavDir", "RunEver"), v => Set("webdavDir", v), 220),
                SettingsKit.Toggle("启动时自动同步", "托盘就绪后异步执行，不拖慢启动",
                    GetBool("syncOnStart", false), v => SetBool("syncOnStart", v))));

            return sp;
        }
    }

    /// <summary>
    /// 诊断：日志／状态目录／关于。
    /// ★ B1-6：原「同步」「日志与诊断」「关于」三页合并到这一页 —— 三页各自只有 2-4 行，
    ///   拆开只会让人来回点。
    /// ★ 2026-11-09（A4）：「同步（WebDAV）」又从这里拆出去独立成页（见 <see cref="SyncPage"/>）——
    ///   它是唯一"要填值"的块，留在排障页里既不好找、也让本页的"看"主题不纯。
    /// </summary>
    internal class DiagnosticsPage : AutoConfPage
    {
        public DiagnosticsPage(string root) : base(root) { }

        protected override UIElement Build()
        {
            var sp = new StackPanel();

            // ================= 日志 =================
            sp.Children.Add(SettingsKit.SectionHeader("日志"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Toggle("写运行日志", "state\\runever-cs.log；改完立即生效，不用重启托盘",
                    GetBool("log", true), v => SetBool("log", v)),
                SettingsKit.ButtonRow("打开日志文件", "用记事本打开 state\\runever-cs.log",
                    "打开", OpenLog)));

            // ================= 诊断 =================
            sp.Children.Add(SettingsKit.SectionHeader("诊断"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Info("状态目录", System.IO.Path.Combine(Root, "state")),
                SettingsKit.Info("自动配置", ConfPath),
                SettingsKit.Info("上次全盘自检", LastSelfCheckText()),
                SettingsKit.Info("缺件隐藏 / 空子菜单", LastPruneText()),
                SettingsKit.ButtonRow("打开配置目录", "menus.conf / auto.conf 所在位置",
                    "打开", () => OpenDir(System.IO.Path.Combine(Root, "config")))));

            // ================= 关于 =================
            sp.Children.Add(SettingsKit.SectionHeader("关于"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Info("版本", "C# 版（net48 / WPF 界面 + Win32 菜单）"),
                SettingsKit.Info("根目录", Root),
                SettingsKit.Info("配置文件", System.IO.Path.Combine(Root, "config", "menus.conf")),
                SettingsKit.Info("插件目录", System.IO.Path.Combine(Root, "config", "functions"))));

            sp.Children.Add(SettingsKit.Card(
                // ★ B4c-1：菜单早就不走 WPF 了（`NativeMenu` = CreatePopupMenu + AppendMenuW +
                //   TrackPopupMenuEx），写「WPF ContextMenu（MahApps）」是过期描述，会误导排障方向。
                // ★ 2026-11-09（U1）：四条能力说明合成整段（原各占一行 Info），同 Note 注释。
                SettingsKit.Note(
                    "· 菜单渲染：Win32 原生（CreatePopupMenu + TrackPopupMenu）\n" +
                    "· 采集：native（Win32 + UI Automation）／ ahk（CaptureRunner）\n" +
                    "· 搜索后端：Everything SDK（EV）/ es.exe 兜底\n" +
                    "· 图标：ExtractIconExW + 内存缓存，失败回落 assets\\runever.ico")));

            return sp;
        }

        /// <summary>「上次全盘自检」的留痕（B4b-9）：读 state\selfcheck-last.txt 的一行人话。</summary>
        private string LastSelfCheckText()
        {
            try
            {
                string f = System.IO.Path.Combine(Root, "state", "selfcheck-last.txt");
                if (System.IO.File.Exists(f))
                {
                    string s = System.IO.File.ReadAllText(f).Trim();
                    if (s.Length > 0) return s;
                }
            }
            catch { }
            return "还没跑过（托盘 → 维护 → 全盘自检…）";
        }

        /// <summary>
        /// 「缺件隐藏 / 剪掉空子菜单」的只读统计（F1-5）。
        /// 数据源 = 托盘进程弹菜单时写的 state\menu-prune-last.txt（与日志行同源），
        /// 本页只读文件、不重算 —— 设置窗口是另一进程，拿不到托盘的内存态。
        /// </summary>
        private string LastPruneText()
        {
            try
            {
                string f = System.IO.Path.Combine(Root, "state", "menu-prune-last.txt");
                if (System.IO.File.Exists(f))
                {
                    string s = System.IO.File.ReadAllText(f).Trim();
                    if (s.Length > 0) return s;
                }
            }
            catch { }
            return "还没有记录（弹一次菜单后显示）";
        }

        private void OpenLog()
        {
            string p = System.IO.Path.Combine(Root, "state", "runever-cs.log");
            if (!System.IO.File.Exists(p))
            {
                MessageBox.Show("日志文件还不存在：\n" + p
                    + "\n\n（启动托盘并让它跑一会儿就会生成）", "RunEver",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = "\"" + p + "\"",
                    UseShellExecute = false
                });
            }
            catch { }
        }

        private static void OpenDir(string dir)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dir,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }
}
