using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    /// <summary>
    /// 插件字段编辑器 —— 在 UI 里直接改插件元数据，不用切文本编辑器。
    ///
    /// ★ 用户反馈："插件编辑 进不了UI编辑 只能走文本 那意义何在"
    ///   这是设计缺陷：之前只做了"模板生成"，没做"改已有插件"，
    ///   而后者恰恰是最常做的事（调匹配式、换命令、开关窗口）。
    ///
    /// 编辑范围：全部元数据字段（Name/Match/Whole/Prio/Out/Tpl/Icon/Show/Enabled）
    /// 写回方式：逐行原地替换元数据行（保留注释 + 原子写）
    /// 脚本体（Run 函数）不在 UI 编辑 —— 那是逻辑代码，给「编辑源码」按钮。
    /// </summary>
    public class PluginEditDialog : Window
    {
        public bool Saved { get; private set; }

        private readonly PluginInfo _info;
        private readonly string _file;

        private TextBox _name, _match, _prio;
        private CheckBox _whole, _show, _enabled;
        private ComboBox _out;
        private TextBox _tpl, _command;
        private PathBox _icon;
        private TextBlock _error, _formHint;
        private StackPanel _tplPanel, _cmdPanel;
        private TextBox _preview;

        /// <summary>形态 B 的原始命令模板（从源码 Run() 里抠出来的）。</summary>
        private string _origCommand = "";

        public PluginEditDialog(PluginInfo info)
        {
            _info = info;
            _file = info.Path;

            Title = "编辑插件 — " + info.File;
            Width = 720;
            Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MinWidth = 600;

            var sp = new StackPanel { Margin = new Thickness(16) };

            // ---- 基本 ----
            sp.Children.Add(new TextBlock
            {
                Text = info.File,
                Foreground = Brushes.Gray,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 8)
            });

            sp.Children.Add(Lbl("插件名（菜单/命中提示里显示）"));
            _name = Edit(info.Name);
            sp.Children.Add(_name);

            sp.Children.Add(Lbl("匹配式（正则表达式）"));
            _match = Edit(info.Match);
            _match.FontFamily = new FontFamily("Consolas");
            _match.TextChanged += (s, e) => UpdatePreview();
            sp.Children.Add(_match);

            var matchHints = new TextBlock
            {
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 2),
                Text = "示例：^SF\\d{10,}$（顺丰单号）  ^https?://\\S+$（网址）  ^choco\\s+install\\b"
            };
            sp.Children.Add(matchHints);

            _whole = new CheckBox
            {
                Content = "整段匹配（选中文本必须完全符合；取消则允许包含）",
                IsChecked = info.Whole,
                Margin = new Thickness(0, 6, 0, 4)
            };
            _whole.Checked += (s, e) => UpdatePreview();
            _whole.Unchecked += (s, e) => UpdatePreview();
            sp.Children.Add(_whole);

            // ---- 产出 ----
            var row2 = new Grid();
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });

            var c1 = new StackPanel();
            c1.Children.Add(Lbl("产出类型（决定执行方式）"));
            _out = new ComboBox { Margin = new Thickness(0, 3, 0, 0) };
            foreach (var t in PluginScanner.OutTypes) _out.Items.Add(t);
            _out.SelectedItem = info.Out;
            _out.SelectionChanged += (s, e) => { UpdatePanels(); UpdatePreview(); };
            c1.Children.Add(_out);
            Grid.SetColumn(c1, 0);

            var c2 = new StackPanel();
            c2.Children.Add(Lbl("优先级（大者先匹配）"));
            _prio = Edit(info.Prio.ToString());
            _prio.TextChanged += (s, e) => UpdatePreview();
            c2.Children.Add(_prio);
            Grid.SetColumn(c2, 2);

            row2.Children.Add(c1);
            row2.Children.Add(c2);
            sp.Children.Add(row2);

            // ---- 形态相关字段 ----
            _formHint = new TextBlock
            {
                Foreground = Brushes.Gray,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            sp.Children.Add(_formHint);

            // 模板型：Tpl
            _tplPanel = new StackPanel();
            _tplPanel.Children.Add(Lbl("目标模板（{0} = 选中的文本）"));
            _tpl = Edit(info.Tpl);
            _tpl.FontFamily = new FontFamily("Consolas");
            _tpl.TextChanged += (s, e) => UpdatePreview();
            _tplPanel.Children.Add(_tpl);
            sp.Children.Add(_tplPanel);

            // 逻辑型：命令（从 Run() 里解析）
            _cmdPanel = new StackPanel();
            _cmdPanel.Children.Add(Lbl("执行命令（{0} = 选中的文本；仅形态 B 且能识别时可用）"));
            _command = Edit("");
            _command.FontFamily = new FontFamily("Consolas");
            _command.TextChanged += (s, e) => UpdatePreview();
            _cmdPanel.Children.Add(_command);
            sp.Children.Add(_cmdPanel);

            // ---- 开关 ----
            var sw = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            _enabled = new CheckBox
            {
                Content = "启用",
                IsChecked = info.Enabled,
                Margin = new Thickness(0, 0, 20, 0)
            };
            _show = new CheckBox
            {
                Content = "显示执行窗口（命令行工具看输出）",
                IsChecked = info.Show == 1
            };
            sw.Children.Add(_enabled);
            sw.Children.Add(_show);
            sp.Children.Add(sw);

            if (info.Show < 0)
            {
                sp.Children.Add(new TextBlock
                {
                    Text = "※ 当前未声明 RVE_FN_Show（继承 auto.conf 的默认值）。"
                         + "勾/取消勾会写入显式值。",
                    FontSize = 11,
                    Foreground = Brushes.Gray,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            // ---- 图标 ----
            sp.Children.Add(Lbl("图标源（可空；支持 程序名 / 全路径 / SHELL32.dll,71 形式）"));
            _icon = new PathBox(PathBox.Mode.File,
                "程序 (*.exe)|*.exe|图标库 (*.dll)|*.dll|所有文件 (*.*)|*.*",
                "选择图标来源",
                new[] { "chrome.exe", "explorer.exe", "notepad.exe", "SHELL32.dll,71", "SHELL32.dll,21" });
            _icon.Text = info.Icon;
            _icon.Changed += () => UpdatePreview();
            sp.Children.Add(_icon);

            // ---- 预览 ----
            sp.Children.Add(Lbl("元数据预览（保存后写入插件文件；与「配置块」面板同源）"));
            _preview = new TextBox
            {
                IsReadOnly = true,
                Height = 165,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                FontSize = 11.5,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8),
                // ★ 只读代码块外观（用户 2026-11-09 要求）：无边框 + 等宽 + 淡底；
                //   只读但保留可选中复制（IsReadOnly 不影响选择）。
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(0xF6, 0xF6, 0xF6))
            };
            sp.Children.Add(_preview);

            _error = new TextBlock
            {
                Foreground = Brushes.Firebrick,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            sp.Children.Add(_error);

            // ---- 按钮 ----
            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var save = new Button
            {
                Content = "保存",
                Width = 100,
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                FontWeight = FontWeights.Bold
            };
            save.Click += OnSave;

            var openSrc = new Button
            {
                Content = "编辑源码",
                Width = 100,
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "脚本体（Run 函数）是逻辑代码，在文本编辑器里改"
            };
            openSrc.Click += (s, e) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _file,
                        UseShellExecute = true
                    });
                }
                catch { }
            };

            var cancel = new Button { Content = "取消", Width = 90, IsCancel = true };
            btns.Children.Add(save);
            btns.Children.Add(openSrc);
            btns.Children.Add(cancel);
            sp.Children.Add(btns);

            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            // 读出现有命令模板（形态 B）
            _origCommand = TryExtractCommand(_file);
            _command.Text = _origCommand;

            UpdatePanels();
            UpdatePreview();
        }

        // ============================================================
        private void UpdatePanels()
        {
            bool isTpl = (_out.SelectedItem != null
                && string.Equals(_out.SelectedItem.ToString(), "url", StringComparison.OrdinalIgnoreCase));

            // url 用模板；其他类型隐藏模板框
            _tplPanel.Visibility = isTpl ? Visibility.Visible : Visibility.Collapsed;

            bool hasCmd = !string.IsNullOrEmpty(_origCommand);
            _cmdPanel.Visibility = hasCmd ? Visibility.Visible : Visibility.Collapsed;

            _formHint.Text = isTpl
                ? "形态 A（模板型）：目标由「目标模板」代入 {0} 生成，不需要写代码。"
                : (hasCmd
                    ? "形态 B（逻辑型）：检测到 Run() 中的命令模板，可直接在此修改。"
                    : "形态 B（逻辑型）：脚本体里有自定义逻辑，请用「编辑源码」修改。");
        }

        /// <summary>
        /// 元数据预览 —— ★ 与「配置块」面板共用 `PluginScanner.FormatMetaText`：
        /// 对话框里看到的、复制出去的、导入后再生成的都是同一份规范文本
        /// （不再另写第二套拼接代码，避免格式漂移）。
        /// </summary>
        private void UpdatePreview()
        {
            var p = new PluginInfo
            {
                Name = _name.Text,
                Match = _match.Text,
                Whole = _whole.IsChecked == true,
                Prio = ParsePrio(_prio.Text),
                Out = (_out.SelectedItem ?? "").ToString(),
                // 与保存一致：只有 Out=url 时才用 Tpl（其他类型面板隐藏、不写）
                Tpl = (_tplPanel.Visibility == Visibility.Visible) ? _tpl.Text : "",
                Icon = _icon.Text.Trim(),
                // 选框只有 true/false；未声明 Show 的插件呈"未勾"⇒ 预览 false（与原行为一致）
                Show = _show.IsChecked == true ? 1 : 0,
                Enabled = _enabled.IsChecked == true
            };
            _preview.Text = PluginScanner.FormatMetaText(p);
        }

        private static int ParsePrio(string s)
        {
            int pr;
            return int.TryParse((s ?? "").Trim(), out pr) ? pr : 100;
        }

        // ============================================================
        private void OnSave(object sender, RoutedEventArgs e)
        {
            _error.Text = "";

            if (string.IsNullOrWhiteSpace(_name.Text)) { _error.Text = "插件名不能为空"; return; }
            if (string.IsNullOrWhiteSpace(_match.Text)) { _error.Text = "匹配式不能为空"; return; }

            // 正则合法性
            try
            {
                string pat = _match.Text.Trim();
                if (_whole.IsChecked == true)
                {
                    if (!pat.StartsWith("^")) pat = "^" + pat;
                    if (!pat.EndsWith("$")) pat = pat + "$";
                }
                new Regex(pat);
            }
            catch (Exception ex)
            {
                _error.Text = "匹配式不是合法正则：" + ex.Message;
                return;
            }

            int pr;
            if (!int.TryParse(_prio.Text.Trim(), out pr)) pr = 100;

            // 逐字段写回
            var ops = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string,string>("Name",  "\"" + _name.Text.Trim() + "\""),
                new KeyValuePair<string,string>("Match", "\"" + _match.Text.Trim() + "\""),
                new KeyValuePair<string,string>("Whole", _whole.IsChecked == true ? "true" : "false"),
                new KeyValuePair<string,string>("Prio",  pr.ToString()),
                new KeyValuePair<string,string>("Out",   "\"" + (_out.SelectedItem ?? "") + "\""),
                new KeyValuePair<string,string>("Show",  _show.IsChecked == true ? "true" : "false"),
                new KeyValuePair<string,string>("Enabled", _enabled.IsChecked == true ? "true" : "false"),
            };
            if (_tplPanel.Visibility == Visibility.Visible)
                ops.Add(new KeyValuePair<string, string>("Tpl", "\"" + _tpl.Text.Trim() + "\""));
            if (!string.IsNullOrWhiteSpace(_icon.Text))
                ops.Add(new KeyValuePair<string, string>("Icon", "\"" + _icon.Text.Trim() + "\""));

            // 先备份
            string backup = null;
            try
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                backup = _file + ".bak." + stamp;
                File.Copy(_file, backup, true);
            }
            catch { backup = null; }

            int done = 0;
            foreach (var op in ops)
            {
                if (PluginPage.WriteMetaFlag(_file, op.Key, op.Value)) done++;
            }

            // 命令模板替换（形态 B）
            bool cmdChanged = false;
            if (_cmdPanel.Visibility == Visibility.Visible
                && _command.Text.Trim() != _origCommand
                && !string.IsNullOrWhiteSpace(_command.Text))
            {
                cmdChanged = TryReplaceCommand(_file, _origCommand, _command.Text.Trim());
            }

            // 语法校验（用 AHK 运行时）
            string syntaxErr = PluginTemplate.ValidateAhkSyntax(File.ReadAllText(_file, Encoding.UTF8));
            if (!string.IsNullOrEmpty(syntaxErr))
            {
                // 回滚
                try { if (backup != null) File.Copy(backup, _file, true); } catch { }
                _error.Text = "语法校验失败，已回滚：\n" + syntaxErr;
                return;
            }

            Saved = true;
            MessageBox.Show(
                "已保存 " + done + " 个字段"
                + (cmdChanged ? " + 命令模板" : "")
                + (backup != null ? "\n\n备份：" + Path.GetFileName(backup) : ""),
                "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }

        // ============================================================
        /// <summary>从插件源码里抠出 Run() 的 return 命令模板（识别 {0}/text 拼接）。</summary>
        private static string TryExtractCommand(string path)
        {
            try
            {
                string code = File.ReadAllText(path, Encoding.UTF8);
                var m = Regex.Match(code, @"return\s+(.+?)\s*$", RegexOptions.Multiline);
                if (!m.Success) return "";

                string expr = m.Groups[1].Value.Trim();

                // `"choco install " text` → choco install {0}
                var parts = new List<string>();
                foreach (Match sm in Regex.Matches(expr, "\"((?:[^\"]|\"\")*)\""))
                    parts.Add(sm.Groups[1].Value.Replace("\"\"", "\""));

                if (parts.Count == 0) return "";
                if (expr.Contains(" text") || expr.Contains(" vText") || expr.Contains(" vCmd"))
                {
                    // 有变量拼接 → 把变量位还原成 {0}
                    var sb = new StringBuilder();
                    sb.Append(parts[0]);
                    for (int i = 1; i < parts.Count; i++) sb.Append("{0}").Append(parts[i]);
                    // 变量在末尾的情况
                    if (Regex.IsMatch(expr, @"\b(text|vText|vCmd)\s*$")) sb.Append("{0}");
                    return sb.ToString();
                }
                return parts[0];
            }
            catch { return ""; }
        }

        /// <summary>在源码里把旧命令模板替换成新的。</summary>
        private static bool TryReplaceCommand(string path, string oldTpl, string newTpl)
        {
            try
            {
                string code = File.ReadAllText(path, Encoding.UTF8);
                string eol = code.Contains("\r\n") ? "\r\n" : "\n";

                // 构造新的 AHK 表达式
                var parts = newTpl.Split(new[] { "{0}" }, StringSplitOptions.None);
                var sb = new StringBuilder();
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) sb.Append(" text ");
                    if (parts[i].Length > 0)
                        sb.Append("\"").Append(parts[i].Replace("\"", "\"\"")).Append("\"");
                }
                string newExpr = sb.ToString().Trim();

                var m = Regex.Match(code, @"^(\s*)return\s+.+?(\s*)$", RegexOptions.Multiline);
                if (!m.Success) return false;

                string indent = m.Groups[1].Value;
                string replaced = code.Substring(0, m.Index)
                    + indent + "return " + newExpr
                    + code.Substring(m.Index + m.Length);

                string tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                File.WriteAllText(tmp, replaced, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch { return false; }
        }

        // ============================================================
        private TextBlock Lbl(string t)
        {
            return new TextBlock
            {
                Text = t,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 6, 0, 0)
            };
        }

        private TextBox Edit(string v)
        {
            return new TextBox
            {
                Text = v ?? "",
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(0, 3, 0, 2)
            };
        }
    }
}
