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
                Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            var hotkeyRow = SettingsKit.Row("弹出热键", "按一个键即可填入（Backspace 清空）；单字母需配 Ctrl/Alt，或改用 ` F1-F24", hotkeyBox);
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
                if (hotkeyBox.Text != Get("hotkey", "`")) Set("hotkey", hotkeyBox.Text.Trim());
            };

            sp.Children.Add(SettingsKit.Card(
                hotkeyRow,
                SettingsKit.Toggle("启用自动执行",
                    "选中文本 → 匹配插件 → 直接执行，不弹菜单",
                    GetBool("enabled", true), v => SetBool("enabled", v)),
                SettingsKit.Text("最小文本长度", "短于该长度的选中文本不触发自动执行",
                    Get("minLen", "4"), v => SetInt("minLen", PInt(v, 4)), 90)));

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
                SettingsKit.Info("生效方式", "改完立即写盘，下一次弹菜单就按新顺序（不用重启托盘）"),
                SettingsKit.Info("哪一档为空就没有那一块", "例如没选中东西且配置里没有兜底分组时，动态那格是空的，此时排它不会有可见变化"),
                SettingsKit.Info("分组内条目的先后", "仍去「菜单项」页用拖拽调整，这里只排三段内容块的先后")));

            sp.Children.Add(SettingsKit.SectionHeader("插件"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Text("插件元数据缓存（秒）",
                    "超时后重扫 config\\functions；频繁改插件时调小",
                    Get("cacheSeconds", "300"), v => SetInt("cacheSeconds", PInt(v, 300)), 90)));

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
                SettingsKit.ButtonRow("刷新路径缓存",
                    "立即重新解析全部程序名（托盘常驻时请从托盘菜单刷新，避免两个进程同时写）",
                    "立即刷新", RefreshCache)));

            return sp;
        }

        private void RefreshCache()
        {
            try
            {
                var r = PathCacheRefresh.Run(Root, null);
                MessageBox.Show(
                    r == null ? "已触发刷新。" : ("路径缓存已刷新：" + r.ToString()),
                    "RunEver", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("刷新失败：" + ex.Message, "RunEver",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>同步：WebDAV 端点与开关。</summary>
    internal class SyncPage : AutoConfPage
    {
        public SyncPage(string root) : base(root) { }

        protected override UIElement Build()
        {
            var sp = new StackPanel();

            sp.Children.Add(SettingsKit.SectionHeader("WebDAV"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Text("服务器地址", "如 https://nas.example.com:5006/menus",
                    Get("webdavUrl", ""), v => Set("webdavUrl", v), 380),
                SettingsKit.Text("用户名", null, Get("webdavUser", ""), v => Set("webdavUser", v), 220),
                SettingsKit.Text("密码", "写入日志时自动脱敏", Get("webdavPass", ""), v => Set("webdavPass", v), 220),
                SettingsKit.Text("远端目录", "留空 = 直接用地址本身",
                    Get("webdavDir", "RunEver"), v => Set("webdavDir", v), 220)));

            sp.Children.Add(SettingsKit.SectionHeader("行为"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Toggle("启动时自动同步", "托盘就绪后异步执行，不拖慢启动",
                    GetBool("syncOnStart", false), v => SetBool("syncOnStart", v))));

            return sp;
        }
    }

    /// <summary>日志与诊断。</summary>
    internal class DiagnosticsPage : AutoConfPage
    {
        public DiagnosticsPage(string root) : base(root) { }

        protected override UIElement Build()
        {
            var sp = new StackPanel();

            sp.Children.Add(SettingsKit.SectionHeader("日志"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Toggle("写运行日志", "state\\runever-cs.log；关掉可减少磁盘写入",
                    GetBool("log", true), v => SetBool("log", v)),
                SettingsKit.ButtonRow("打开日志文件", "定位到 state\\runever-cs.log",
                    "打开", OpenLog)));

            sp.Children.Add(SettingsKit.SectionHeader("诊断"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Info("状态目录", System.IO.Path.Combine(Root, "state")),
                SettingsKit.Info("自动配置", ConfPath),
                SettingsKit.ButtonRow("打开配置目录", "menus.conf / auto.conf 所在位置",
                    "打开", () => OpenDir(System.IO.Path.Combine(Root, "config")))));

            return sp;
        }

        private void OpenLog()
        {
            string p = System.IO.Path.Combine(Root, "state", "runever-cs.log");
            if (!System.IO.File.Exists(p))
            {
                MessageBox.Show("日志文件还不存在。", "RunEver",
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

    /// <summary>关于：版本、路径、依赖。</summary>
    internal class AboutPage : UserControl
    {
        public AboutPage(string root)
        {
            var sp = new StackPanel();

            sp.Children.Add(SettingsKit.SectionHeader("RunEver"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Info("版本", "C# 版（net48 / WPF 菜单 + Win32 兜底）"),
                SettingsKit.Info("根目录", root),
                SettingsKit.Info("配置文件", System.IO.Path.Combine(root, "config", "menus.conf")),
                SettingsKit.Info("插件目录", System.IO.Path.Combine(root, "config", "functions"))));

            sp.Children.Add(SettingsKit.SectionHeader("组件"));

            sp.Children.Add(SettingsKit.Card(
                SettingsKit.Info("菜单渲染", "WPF ContextMenu（MahApps 样式，仅作用于菜单）"),
                SettingsKit.Info("采集", "native（Win32 + UI Automation）／ahk（CaptureRunner）"),
                SettingsKit.Info("搜索后端", "Everything SDK（EV) / es.exe 兜底"),
                SettingsKit.Info("图标", "ExtractIconExW + 内存缓存，失败回落 assets\\runever.ico")));

            Content = SettingsKit.PageHost(sp);
        }
    }
}
