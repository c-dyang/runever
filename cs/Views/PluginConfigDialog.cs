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
    /// 插件「配置块」面板 —— 照 Listary「HTTP API 文档」的逻辑：只读展示 + 一键复制。
    ///
    /// 解决的问题（用户诉求）：
    ///   · 想「拿一段配置文本直接贴进来」⇒ 从剪贴板导入 → 解析 → 校验 → 显式落盘
    ///   · 想「把当前插件导出成一段话复制给 AI 或别的机器」⇒ 选中插件时的默认内容
    ///
    /// 两种默认态：
    ///   · 选中插件 ⇒ 该插件的配置块（元数据；形态 B 另附 Run() 摘要，均为注释行）
    ///   · 未选中   ⇒ 契约 + 两个成品样例（形态 A / 形态 B），**纯只读**
    ///
    /// ★ 安全边界：
    ///   ① 导入只回填文本区，不落盘；
    ///   ② 覆盖必须二次确认 + 先备份（.bak.时间戳），备份失败则不写；
    ///   ③ 只改写配置块里出现过的键，Run() 脚本体不碰；
    ///   ④ 写后做 AHK 语法校验 + 读回比对，任一失败回滚原件。
    /// </summary>
    public class PluginConfigDialog : Window
    {
        /// <summary>「另存为新插件」成功后写出的文件路径（供调用方刷新列表）。</summary>
        public string CreatedPath { get; private set; }

        /// <summary>落盘后给调用方状态栏用的一句话。</summary>
        public string ResultText { get; private set; }

        private readonly string _root;
        private readonly string _pluginDir;
        private readonly PluginInfo _selected;

        private TextBox _text;
        private TextBlock _validLine, _hintLine, _stateLine;
        private Button _saveBtn;

        /// <summary>true = 只读文档态（未选中插件且尚未导入）。</summary>
        private bool _docOnly;

        private bool _valid;
        private PluginInfo _parsed;

        public PluginConfigDialog(string root, PluginInfo selected)
        {
            _root = root;
            _pluginDir = Path.Combine(root, "config", "functions");
            _selected = selected;
            _docOnly = (selected == null);

            Title = selected == null
                ? "插件配置块 — 格式契约与样例"
                : "插件配置块 — " + selected.File;
            Width = 780;
            Height = 640;
            MinWidth = 660;
            MinHeight = 460;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- 第 1 行：说明 + 右上三个按钮 ----
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top
            };
            DockPanel.SetDock(actions, Dock.Right);
            actions.Children.Add(Btn("复制", OnCopy, false));
            actions.Children.Add(Btn("从剪贴板导入", OnImport, false));
            var closeBtn = Btn("关闭", (s, e) => Close(), false);
            closeBtn.IsCancel = true;      // Esc 也能关（IsCancel 挂在这个按钮上）
            actions.Children.Add(closeBtn);

            var desc = new TextBlock
            {
                Text = selected == null
                    ? "这是插件配置块的格式契约与两个成品样例（只读）。"
                      + "点「从剪贴板导入」把一段配置贴进来 → 校验 → 另存为新插件。"
                    : "下面是「" + selected.Name + "」的配置块，可整体复制给别人（或 AI）。"
                      + "改成别的值后点「覆盖当前插件…」写回（会二次确认 + 备份）。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 12, 0)
            };

            head.Children.Add(actions);
            head.Children.Add(desc);
            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            // ---- 第 2 行：等宽大文本区（可选中/可编辑/随窗口拉伸）----
            _text = new TextBox
            {
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                FontSize = 12,
                AcceptsReturn = true,
                // Tab 用于切换焦点（配置块每行都从行首开始，不需要缩进）
                AcceptsTab = false,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xD8)),
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
                Text = selected == null ? BuildDocText() : BuildPluginText(selected)
            };
            Grid.SetRow(_text, 1);
            grid.Children.Add(_text);

            // ---- 第 3 行：校验行（非法当场红字）----
            var checkPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            _validLine = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11.5,
                Foreground = Brushes.Gray
            };
            _hintLine = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x6A, 0x00)),
                Margin = new Thickness(0, 2, 0, 0)
            };
            checkPanel.Children.Add(_validLine);
            checkPanel.Children.Add(_hintLine);
            Grid.SetRow(checkPanel, 2);
            grid.Children.Add(checkPanel);

            // ---- 第 4 行：状态 + 落盘动作 ----
            var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            _saveBtn = new Button
            {
                Content = selected == null ? "另存为新插件…" : "覆盖当前插件…",
                MinWidth = 130,
                Padding = new Thickness(10, 5, 10, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsDefault = false
            };
            _saveBtn.Click += OnSaveClick;
            DockPanel.SetDock(_saveBtn, Dock.Right);

            _stateLine = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            foot.Children.Add(_saveBtn);
            foot.Children.Add(_stateLine);
            Grid.SetRow(foot, 3);
            grid.Children.Add(foot);

            Content = grid;

            _text.TextChanged += (s, e) => Revalidate();
            Revalidate();
        }

        // ============================================================
        // 默认内容
        // ============================================================

        /// <summary>未选中插件：契约 + 两个成品样例（只读展示）。</summary>
        private string BuildDocText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; ============================================================");
            sb.AppendLine("; RunEver 插件 · 配置块（格式契约 + 成品样例）");
            sb.AppendLine("; ============================================================");
            sb.AppendLine(";");
            sb.AppendLine("; 格式只有一种（不是 YAML / JSON）：每行一个键");
            sb.AppendLine(";     global RVE_FN_<键名> := 值");
            sb.AppendLine(";   字符串用双引号；布尔写 true/false；数字裸写");
            sb.AppendLine(";   行尾 ; 之后是注释（引号内的 ; 不算注释）");
            sb.AppendLine(";");
            sb.AppendLine("; （key = 键名 / req = 必需 / def = 默认值;  - = 无）");
            sb.AppendLine("; key      req   def      description");
            sb.AppendLine("; Name     yes   -        插件名（菜单 / 命中提示里显示）");
            sb.AppendLine("; Match    yes   -        匹配式（正则；Whole=true 时按整段匹配）");
            sb.AppendLine("; Out      yes   -        run / open / url / action / send / keys / intercept");
            sb.AppendLine("; Whole    no    true     整段匹配（false = 允许包含）");
            sb.AppendLine("; Prio     no    100      优先级（大者先匹配）");
            sb.AppendLine("; Tpl      no    -        目标模板，{0} = 选中文本（有它 = 形态 A）");
            sb.AppendLine("; Icon     no    -        图标源：chrome.exe / 全路径 / SHELL32.dll,71");
            sb.AppendLine("; Show     no    inherit  显示执行窗口（true / false；inherit = 跟随 auto.conf）");
            sb.AppendLine("; Enabled  no    true     是否启用");
            sb.AppendLine("; Arg      no    text     选中数据类型");
            sb.AppendLine("; Menu     no    false    是否进菜单（true = 追加到 always 段末尾，且不自动派发）");
            sb.AppendLine(";");
            sb.AppendLine("; ★ Out 不能写 \"cmd\" —— 要执行命令请写 \"action\"");
            sb.AppendLine(";   （历史炸点：写 cmd ⇒ 执行器报「未知动作类型: cmd」）");
            sb.AppendLine("; ★ 形态 A = 有 Tpl（零代码）；形态 B = 无 Tpl（插件自带 Run(text)）");
            sb.AppendLine(";");
            sb.AppendLine("; ★ 三层权限（声明即定档，核心不仲裁）");
            sb.AppendLine(";   L1 接管  Out=\"intercept\"：命中后核心不建菜单、不派发、不追加，");
            sb.AppendLine(";            插件在自己的 Run(text) 里弹自己的菜单；返回非空 = 接管成功，");
            sb.AppendLine(";            返回空串 / 异常 / 超时（8s）= 接管失败 ⇒ 回落核心菜单");
            sb.AppendLine(";   L2 派发  Out=run/open/url/action/send/keys：命中后直接执行，跳过弹菜单");
            sb.AppendLine(";   L3 菜单  RVE_FN_Menu := true：不自动派发，作为菜单项追加到 always 段末尾");
            sb.AppendLine(";            （标题 = 插件名，无快捷键，目标 = Tpl，{0} = 弹出时的选中文本）");
            sb.AppendLine(";   ★ 互斥：intercept 与 Menu 同时声明 ⇒ intercept 优先（Menu 忽略）");
            sb.AppendLine(";");
            sb.AppendLine("; ===== 样例 1 · 形态 A（纯模板，零代码）=====");
            sb.AppendLine("global RVE_FN_Name  := \"德邦快递查询\"");
            sb.AppendLine("global RVE_FN_Match := \"^DP[A-Za-z0-9]{8,}$\"");
            sb.AppendLine("global RVE_FN_Whole := true");
            sb.AppendLine("global RVE_FN_Prio  := 100");
            sb.AppendLine("global RVE_FN_Out   := \"url\"");
            sb.AppendLine("global RVE_FN_Tpl   := \"https://www.deppon.com/track/ordertrack?orderNumList=%22{0}%22\"");
            sb.AppendLine("global RVE_FN_Icon  := \"chrome.exe\"");
            sb.AppendLine(";");
            sb.AppendLine("; ===== 样例 2 · 形态 B（带逻辑：Run(text) 返回目标）=====");
            sb.AppendLine("global RVE_FN_Name  := \"执行 Chocolatey 命令\"");
            sb.AppendLine("global RVE_FN_Match := \"^choco\\s+(install|upgrade)\\b\"");
            sb.AppendLine("global RVE_FN_Whole := false");
            sb.AppendLine("global RVE_FN_Prio  := 200");
            sb.AppendLine("global RVE_FN_Out   := \"action\"");
            sb.AppendLine("global RVE_FN_Show  := true");
            sb.AppendLine(";");
            sb.AppendLine("; 下面是形态 B 的脚本体 —— 不属于配置块，导入时会被忽略：");
            sb.AppendLine("Run(text) {");
            sb.AppendLine("    vCmd := Trim(text, \" `t`r`n\")");
            sb.AppendLine("    if (vCmd = \"\")");
            sb.AppendLine("        return \"\"");
            sb.AppendLine("    return vCmd");
            sb.AppendLine("}");
            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>选中插件：该插件的配置块（+ 形态 B 的 Run() 摘要，全部是注释行）。</summary>
        private string BuildPluginText(PluginInfo p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("; ---- 插件配置块：" + p.File + " ----");
            sb.AppendLine("; 导入时只读 global RVE_FN_* 行；下面所有 ; 注释都会被忽略。");
            sb.AppendLine(PluginScanner.FormatMetaText(p));

            if (p.IsFormB)
            {
                sb.AppendLine();
                sb.AppendLine("; ---- Run() 摘要（仅供参考；本面板不导出脚本体）----");
                sb.AppendLine(RunSummary(p.Path, 12));
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine("; 形态 A（模板型）：目标由 RVE_FN_Tpl 代入 {0} 生成，不需要脚本体。");
            }
            return sb.ToString().TrimEnd('\r', '\n');
        }

        /// <summary>从插件源码里摘出 Run() 的头部若干行，逐行加 ; 前缀（保证导入时被忽略）。</summary>
        private static string RunSummary(string path, int maxLines)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return "; （源文件不可读）";

                string[] lines = File.ReadAllText(path, Encoding.UTF8)
                    .Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

                int start = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (Regex.IsMatch(lines[i], @"^\s*Run\s*\(")) { start = i; break; }
                }
                if (start < 0) return "; （未识别到 Run() 函数）";

                int end = lines.Length - 1;
                for (int i = start; i < lines.Length; i++)
                {
                    if (Regex.IsMatch(lines[i], @"^\}")) { end = i; break; }
                }

                var outLines = new List<string>();
                for (int i = start; i <= end; i++)
                {
                    if (outLines.Count >= maxLines)
                    {
                        outLines.Add("; …（共 " + (end - start + 1) + " 行，已截断）");
                        break;
                    }
                    outLines.Add("; " + lines[i].TrimEnd());
                }
                return string.Join("\r\n", outLines.ToArray());
            }
            catch (Exception ex)
            {
                return "; （读取失败：" + ex.Message + "）";
            }
        }

        // ============================================================
        // 校验
        // ============================================================

        private void Revalidate()
        {
            if (_docOnly)
            {
                _valid = false;
                _validLine.Foreground = Brushes.Gray;
                _validLine.Text = "只读展示：下面是配置块格式契约与两个成品样例。"
                    + "点「从剪贴板导入」后可编辑并校验。";
                _hintLine.Text = "";
                ApplyState();
                return;
            }

            _parsed = PluginScanner.ParseMetaText(_text.Text ?? "");

            var errs = new List<string>();
            var hints = new List<string>();

            // ① 名称与匹配式非空
            if (string.IsNullOrEmpty(_parsed.Name)) errs.Add("插件名为空（缺 RVE_FN_Name）");
            if (string.IsNullOrEmpty(_parsed.Match)) errs.Add("匹配式为空（缺 RVE_FN_Match）");

            // ② Out 合法（含 "cmd" 单独报错）
            string outErr = PluginScanner.CheckOutType(_parsed.Out);
            if (outErr.Length > 0) errs.Add(outErr);

            // ③ 正则能编译（与运行期同一份编译口径：AutoMatcher.Compile）
            string compiled = "";
            if (!string.IsNullOrEmpty(_parsed.Match))
            {
                compiled = AutoMatcher.Compile(_parsed.Match, _parsed.Whole);
                try
                {
                    new Regex(compiled);
                }
                catch (Exception ex)
                {
                    errs.Add("匹配式不是合法正则：" + ex.Message);
                }
            }

            // ④ 可疑但不致命：只提示，不拦
            int nameCount = PluginScanner.CountKeyOccurrences(_text.Text, "Name");
            if (nameCount > 1)
                hints.Add("文本里有 " + nameCount
                    + " 处 RVE_FN_Name（多段配置块）：解析取各键最后出现的值，建议只保留一段。");

            if (errs.Count == 0)
            {
                if (_parsed.Out == "url" && string.IsNullOrEmpty(_parsed.Tpl))
                    hints.Add("Out=url 但没有 RVE_FN_Tpl ⇒ 按形态 B 处理，插件必须自带 Run(text) 才有产出。");
                if (!string.IsNullOrEmpty(_parsed.Tpl) && _parsed.Tpl.IndexOf("{0}") < 0)
                    hints.Add("RVE_FN_Tpl 里没有 {0}：选中文本不会被代入。");
                if (_parsed.Out == "action" && !string.IsNullOrEmpty(_parsed.Tpl))
                    hints.Add("Out=action 且有 Tpl：形态 A 会把 Tpl 代入结果当命令执行 —— 确认这是你要的。");
                if (_parsed.Out == "intercept")
                {
                    if (!string.IsNullOrEmpty(_parsed.Tpl))
                        hints.Add("Out=intercept 不使用 Tpl（目标由插件自己决定），该行会被忽略。");
                    else
                        hints.Add("Out=intercept（L1 接管）：插件必须在 Run(text) 里自己弹菜单，"
                            + "并在接管成功时返回非空（如 \"ok\"）；返回空串/超时 ⇒ 回落核心菜单。");
                }
                if (_parsed.Menu && _parsed.Out == "intercept")
                    hints.Add("同时声明了 Menu 与 intercept：intercept 优先，Menu 被忽略。");
                if (_parsed.Prio < 0)
                    hints.Add("RVE_FN_Prio 是负数：会排在所有插件之后。");
            }

            _valid = errs.Count == 0;

            if (_valid)
            {
                _validLine.Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x7F, 0x3B));
                _validLine.Text = "✓ 校验通过：Out=" + _parsed.Out + " 合法 · 正则可编译"
                    + (compiled.Length > 0 ? "（" + compiled + "）" : "")
                    + " · 名称/匹配式非空";
            }
            else
            {
                _validLine.Foreground = Brushes.Firebrick;
                _validLine.Text = "✗ " + string.Join("\r\n✗ ", errs.ToArray());
            }

            _hintLine.Text = hints.Count > 0 ? "提示：" + string.Join(" ", hints.ToArray()) : "";
            ApplyState();
        }

        /// <summary>文档态隐藏落盘按钮；草稿态按校验结果启用。</summary>
        private void ApplyState()
        {
            _saveBtn.Visibility = _docOnly ? Visibility.Collapsed : Visibility.Visible;
            _saveBtn.IsEnabled = _valid;
            _text.IsReadOnly = _docOnly;
            _text.Background = _docOnly
                ? new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA))
                : Brushes.White;
        }

        // ============================================================
        // 复制 / 导入
        // ============================================================

        private void OnCopy(object sender, RoutedEventArgs e)
        {
            string t = _text.Text ?? "";
            if (t.Trim().Length == 0)
            {
                _stateLine.Text = "没有可复制的内容。";
                return;
            }
            try
            {
                Clipboard.SetText(t);
                _stateLine.Text = "已复制 " + t.Length + " 个字符到剪贴板。";
            }
            catch (Exception ex)
            {
                _stateLine.Text = "复制失败：" + ex.Message;
            }
        }

        /// <summary>
        /// 从剪贴板导入 —— 只回填文本区 + 校验，**绝不落盘**。
        /// 落盘必须再点「另存为」/「覆盖」，且覆盖还有二次确认 + 备份。
        /// </summary>
        private void OnImport(object sender, RoutedEventArgs e)
        {
            string clip = null;
            try
            {
                if (Clipboard.ContainsText()) clip = Clipboard.GetText();
            }
            catch (Exception ex)
            {
                _stateLine.Text = "读取剪贴板失败：" + ex.Message;
                return;
            }

            if (string.IsNullOrWhiteSpace(clip))
            {
                _stateLine.Text = "剪贴板里没有文本。";
                return;
            }

            _docOnly = false;
            _text.Text = clip.TrimEnd('\r', '\n');
            Revalidate();

            int n = _parsed == null ? 0 : _parsed.PresentKeys.Count;
            _stateLine.Text = "已从剪贴板导入（识别 " + n + " 个 RVE_FN_* 键），未写入磁盘"
                + (_valid ? "；校验通过，可落盘。" : "；校验未通过，见下方红字。");
        }

        // ============================================================
        // 落盘
        // ============================================================

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_selected == null) SaveAsNewPlugin();
            else OverwriteSelectedPlugin();
        }

        /// <summary>无选中插件：把当前配置块另存为新插件文件（不覆盖任何已有文件）。</summary>
        private void SaveAsNewPlugin()
        {
            Revalidate();
            if (!_valid)
            {
                _stateLine.Text = "校验未通过，未写入磁盘。";
                return;
            }

            string code = BuildNewPluginSource(_parsed);
            string syntax = PluginTemplate.ValidateAhkSyntax(code);
            if (!string.IsNullOrEmpty(syntax))
            {
                _validLine.Foreground = Brushes.Firebrick;
                _validLine.Text = "✗ 生成的脚本语法有误（未写入）：\r\n" + syntax;
                return;
            }

            string path;
            try
            {
                if (!Directory.Exists(_pluginDir)) Directory.CreateDirectory(_pluginDir);

                string baseName = PluginTemplate.SanitizeFileName(_parsed.Name);
                if (string.IsNullOrEmpty(baseName)) baseName = "plugin-" + DateTime.Now.ToString("HHmmss");

                path = Path.Combine(_pluginDir, baseName + ".ahk");
                int n = 2;
                while (File.Exists(path))
                {
                    path = Path.Combine(_pluginDir, baseName + "-" + n + ".ahk");
                    n++;
                }

                string tmp = path + ".tmp";
                File.WriteAllText(tmp, code, new UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                _stateLine.Text = "写入失败：" + ex.Message;
                return;
            }

            CreatedPath = path;
            ResultText = "已从配置块新建插件：" + Path.GetFileName(path) + "（立即可用）";
            MessageBox.Show(
                "已生成插件：\n" + Path.GetFileName(path)
                + "\n\n位置：" + _pluginDir
                + (_parsed.IsFormB
                    ? "\n\n⚠ 这是形态 B：脚本体里的 Run() 目前是空壳（返回空串 = 不生效），"
                      + "\n请用「编辑源码」补上逻辑。"
                    : "\n\n形态 A（纯模板）：无需写代码，立即可用。"),
                "新建成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }

        /// <summary>
        /// 有选中插件：把配置块写回该插件（二次确认 + 备份 + 语法/读回双校验）。
        /// 只改写块里出现过的键；未出现的键与 Run() 脚本体保持原样。
        /// </summary>
        private void OverwriteSelectedPlugin()
        {
            Revalidate();
            if (!_valid)
            {
                _stateLine.Text = "校验未通过，未写入磁盘。";
                return;
            }

            string path = _selected.Path;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _stateLine.Text = "插件文件不存在：" + path;
                return;
            }

            var keys = new List<string>();
            foreach (var k in _parsed.PresentKeys)
            {
                string ck = PluginScanner.CanonicalKey(k);
                if (ck != null && !keys.Contains(ck)) keys.Add(ck);
            }
            if (keys.Count == 0)
            {
                _stateLine.Text = "配置块里没有可写入的键。";
                return;
            }

            bool renamed = !string.Equals(_parsed.Name, _selected.Name, StringComparison.Ordinal);

            var r = MessageBox.Show(
                "用配置块覆盖插件「" + _selected.Name + "」的元数据？\n\n"
                + (renamed ? "⚠ 插件名将从「" + _selected.Name + "」改为「" + _parsed.Name + "」。\n\n" : "")
                + "文件：" + Path.GetFileName(path) + "\n"
                + "将写入 " + keys.Count + " 个键：" + string.Join("、", keys.ToArray()) + "\n"
                + "覆盖后：Name = " + _parsed.Name + " · Match = " + _parsed.Match
                + " · Out = " + _parsed.Out + "\n"
                + "块里没出现的键保持原样（不删除）；脚本体 Run() 不受影响。\n\n"
                + "覆盖前会自动备份为 .bak.<时间戳>，可随时还原。",
                "确认覆盖（二次确认）", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes)
            {
                _stateLine.Text = "已取消覆盖（未改动文件）。";
                return;
            }

            string backup;
            try
            {
                backup = path + ".bak." + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                File.Copy(path, backup, true);
            }
            catch (Exception ex)
            {
                // ★ 备份不成则不写 —— 绝不做"无备份覆盖"
                _stateLine.Text = "备份失败，已终止（未改动文件）：" + ex.Message;
                return;
            }

            int done = 0;
            foreach (var k in keys)
            {
                if (PluginPage.WriteMetaFlag(path, k, PluginScanner.MetaValueText(_parsed, k)))
                    done++;
            }

            // AHK 语法校验：失败回滚
            string syntax = PluginTemplate.ValidateAhkSyntax(File.ReadAllText(path, Encoding.UTF8));
            if (!string.IsNullOrEmpty(syntax))
            {
                Restore(backup, path);
                _validLine.Foreground = Brushes.Firebrick;
                _validLine.Text = "✗ AHK 语法校验失败，已回滚原件：\r\n" + syntax;
                _stateLine.Text = "未写入（已回滚）。";
                return;
            }

            // 读回比对：确认写进去的就是块里那份
            var back = new PluginScanner(_root).ReadMeta(path);
            string mismatch = CompareWritten(back, _parsed, keys);
            if (mismatch != null)
            {
                Restore(backup, path);
                _stateLine.Text = "写回校验不一致（" + mismatch + "），已回滚原件。";
                return;
            }

            ResultText = "已覆盖插件 " + _selected.File + "（" + done + " 个键），备份 "
                + Path.GetFileName(backup);
            MessageBox.Show(
                "已写入 " + done + " 个键：" + string.Join("、", keys.ToArray())
                + "\n\n备份：" + Path.GetFileName(backup),
                "覆盖成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }

        private static void Restore(string backup, string path)
        {
            try { File.Copy(backup, path, true); } catch { }
        }

        /// <summary>读回比对：逐键确认写进文件的值等于配置块解析出的值。</summary>
        private static string CompareWritten(PluginInfo back, PluginInfo want, List<string> keys)
        {
            if (back == null) return "文件已不是合法插件";
            foreach (var k in keys)
            {
                switch (k)
                {
                    case "Name": if (back.Name != want.Name) return "Name"; break;
                    case "Match": if (back.Match != want.Match) return "Match"; break;
                    case "Whole": if (back.Whole != want.Whole) return "Whole"; break;
                    case "Prio": if (back.Prio != want.Prio) return "Prio"; break;
                    case "Out": if (back.Out != want.Out) return "Out"; break;
                    case "Tpl": if (back.Tpl != want.Tpl) return "Tpl"; break;
                    case "Icon": if (back.Icon != want.Icon) return "Icon"; break;
                    case "Arg": if (back.Arg != want.Arg) return "Arg"; break;
                    case "Menu": if (back.Menu != want.Menu) return "Menu"; break;
                    case "Show": if (back.Show != want.Show) return "Show"; break;
                    case "Enabled": if (back.Enabled != want.Enabled) return "Enabled"; break;
                }
            }
            return null;
        }

        /// <summary>新插件的源码文本：元数据块 +（形态 B 才有）Run() 空壳。</summary>
        private string BuildNewPluginSource(PluginInfo p)
        {
            var sb = new StringBuilder();
            sb.AppendLine("#Requires AutoHotkey v2.0");
            sb.AppendLine("; ============================================================");
            sb.AppendLine("; RunEver 插件 · " + p.Name);
            sb.AppendLine(";");
            sb.AppendLine("; 由「插件 → 配置块…」导入生成（"
                + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "）");
            sb.AppendLine("; ============================================================");
            sb.AppendLine();
            sb.AppendLine("; ===== 元数据 =====");
            sb.AppendLine(PluginScanner.FormatMetaText(p));
            sb.AppendLine();

            if (!p.IsFormB)
            {
                sb.AppendLine("; ===== 形态 A：纯模板，零代码 =====");
                sb.AppendLine("; 目标 = RVE_FN_Tpl 代入 {0}（选中文本）生成。");
            }
            else
            {
                sb.AppendLine("; ===== 脚本体（形态 B）=====");
                sb.AppendLine("; 契约：Run(text) 返回目标；返回空串 = 放弃（回落菜单）");
                sb.AppendLine("Run(text) {");
                sb.AppendLine("    vText := Trim(text, \" `t`r`n\")");
                sb.AppendLine("    if (vText = \"\")");
                sb.AppendLine("        return \"\"");
                sb.AppendLine();
                sb.AppendLine("    ; TODO: 在这里写你的逻辑（当前返回空串 = 插件不生效）");
                sb.AppendLine("    return \"\"");
                sb.AppendLine("}");
            }
            return sb.ToString();
        }

        // ============================================================
        private static Button Btn(string text, RoutedEventHandler h, bool bold)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(6, 0, 0, 0)
            };
            if (bold) b.FontWeight = FontWeights.Bold;
            b.Click += h;
            return b;
        }
    }
}
