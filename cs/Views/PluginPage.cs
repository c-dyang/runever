using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    /// <summary>
    /// 插件管理页 —— 开关 + 模板新建 + 编辑 + 删除（进回收站）。
    ///
    /// 数据源：扫描 config\functions\*.ahk 的元数据（与运行期同一套语义）。
    /// 开关写回：改插件文件里的 RVE_FN_Enabled / RVE_FN_Show 元数据行。
    /// </summary>
    public class PluginPage : UserControl
    {
        private readonly string _root;
        private PluginScanner _scanner;

        private DataGrid _grid;
        private TextBlock _status;
        private TextBlock _badInfo;

        public PluginPage(string root)
        {
            _root = root;
            _scanner = new PluginScanner(root);
            Build();
            Reload();
        }

        private void Build()
        {
            var root = new DockPanel { Margin = new Thickness(10) };

            // ---- 顶部按钮 ----
            var top = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };
            DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(MkBtn("＋ 新建插件（模板向导）", OnNewPlugin, true));
            top.Children.Add(MkBtn("编辑（UI 字段）", OnEdit, true));
            top.Children.Add(MkBtn("编辑源码", OnEditSource, false));
            top.Children.Add(MkBtn("删除（进回收站）", OnDelete, false));
            top.Children.Add(MkBtn("刷新", (s, e) => Reload(), false));
            top.Children.Add(MkBtn("打开插件目录", (s, e) =>
                OpenPath(Path.Combine(_root, "config", "functions")), false));
            root.Children.Add(top);

            // ---- 状态栏 ----
            var statusBar = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(statusBar, Dock.Bottom);
            _badInfo = new TextBlock
            {
                Foreground = Brushes.Firebrick,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap
            };
            _status = new TextBlock { Foreground = Brushes.Gray };
            statusBar.Children.Add(_badInfo);
            statusBar.Children.Add(_status);
            root.Children.Add(statusBar);

            // ---- 列表 ----
            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };
            // ★ 「启用」列改用模板列 + 真实 CheckBox
            //   原因（2026-10-03 实测）：DataGridCheckBoxColumn 依赖 DataGrid 的
            //   单元格编辑管道（BeginEdit → EditRowItem → EditItem），而该管道
            //   在 Items.Add() 模式下会抛
            //     InvalidOperationException: 此视图不允许"EditItem"
            //   ⇒ 点一下勾选框就把整个设置窗口干掉。
            //   模板列自带 CheckBox 控件，点击即回调，完全不走编辑管道。
            {
                var col = new DataGridTemplateColumn { Header = "启用", Width = 50 };
                var f = new FrameworkElementFactory(typeof(CheckBox));
                f.SetBinding(CheckBox.IsCheckedProperty,
                    new System.Windows.Data.Binding("Enabled")
                    { Mode = System.Windows.Data.BindingMode.OneWay });
                f.SetValue(CheckBox.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                f.SetValue(CheckBox.VerticalAlignmentProperty, VerticalAlignment.Center);
                f.AddHandler(CheckBox.ClickEvent,
                    new RoutedEventHandler(OnEnableCheckClicked));
                col.CellTemplate = new DataTemplate { VisualTree = f };
                _grid.Columns.Add(col);
            }
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "显示窗口",
                Binding = new System.Windows.Data.Binding("ShowText"),
                Width = 75
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "插件名",
                Binding = new System.Windows.Data.Binding("Name"),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "匹配式",
                Binding = new System.Windows.Data.Binding("Match"),
                Width = new DataGridLength(2, DataGridLengthUnitType.Star)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "产出",
                Binding = new System.Windows.Data.Binding("Out"),
                Width = 60
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "形态",
                Binding = new System.Windows.Data.Binding("FormText"),
                Width = 50
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "优先级",
                Binding = new System.Windows.Data.Binding("Prio"),
                Width = 55
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "文件",
                Binding = new System.Windows.Data.Binding("File"),
                Width = 150
            });

            // ★★ 禁用编辑管道（与 MenuEditorPage 同源修复）
            //   崩溃堆栈：DataGridCell.OnAnyMouseLeftButtonDown → BeginEdit
            //            → OnExecutedBeginEdit → EditRowItem → EditItem
            //            → InvalidOperationException: 此视图不允许"EditItem"
            //   只设 IsReadOnly 挡不住（走的是路由命令，绕过属性检查）。
            _grid.IsReadOnly = true;
            _grid.BeginningEdit += (s, e) => { e.Cancel = true; };
            _grid.CommandBindings.Add(new System.Windows.Input.CommandBinding(
                DataGrid.BeginEditCommand,
                (s, e) => { e.Handled = true; },
                (s, e) => { e.CanExecute = false; e.Handled = true; }));

            _grid.MouseDoubleClick += (s, e) =>
            {
                var row = _grid.SelectedItem as PluginRow;
                if (row != null) OnEdit(s, e);   // 双击 = UI 字段编辑
            };

            root.Children.Add(_grid);
            Content = root;
        }

        /// <summary>
        /// 「启用」勾选框点击 —— 直接写回插件文件的 RVE_FN_Enabled 元数据。
        /// 不走 DataGrid 编辑管道（那条路会崩）。
        /// </summary>
        private void OnEnableCheckClicked(object sender, RoutedEventArgs e)
        {
            var cb = sender as CheckBox;
            if (cb == null) return;
            var row = cb.DataContext as PluginRow;
            if (row == null) return;

            bool want = cb.IsChecked == true;
            e.Handled = true;

            if (WriteMetaFlag(row.Info.Path, "Enabled", want ? "true" : "false"))
            {
                row.Info.Enabled = want;
                _status.Text = "已" + (want ? "启用" : "停用") + "：" + row.Info.File;
            }
            else
            {
                _status.Text = "写回失败：" + row.Info.File;
            }
        }

        private Button MkBtn(string text, RoutedEventHandler h, bool primary)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 0)
            };
            if (primary) b.FontWeight = FontWeights.Bold;
            b.Click += h;
            return b;
        }

        // ============================================================
        public void Reload()
        {
            try
            {
                _scanner.Scan();
                _grid.Items.Clear();
                foreach (var p in _scanner.Plugins)
                    _grid.Items.Add(new PluginRow(p));

                _status.Text = "已加载 " + _scanner.Plugins.Count + " 个插件"
                    + (string.IsNullOrEmpty(_scanner.Dir) ? "" : "（" + _scanner.Dir + "）");

                if (_scanner.BadPlugins.Count > 0)
                {
                    var names = new List<string>();
                    foreach (var b in _scanner.BadPlugins) names.Add(b.File);
                    _badInfo.Text = "⚠ " + _scanner.BadPlugins.Count
                        + " 个插件元数据非法（已跳过加载）：" + string.Join("、", names);
                }
                else
                {
                    _badInfo.Text = "";
                }
            }
            catch (Exception ex)
            {
                _status.Text = "扫描失败：" + ex.Message;
            }
        }

        // ---- 新建插件（模板向导）----
        private void OnNewPlugin(object sender, RoutedEventArgs e)
        {
            var dlg = new PluginWizardDialog(_root);
            if (dlg.ShowDialog() == true)
            {
                Reload();
                if (!string.IsNullOrEmpty(dlg.CreatedPath))
                    _status.Text = "已创建：" + Path.GetFileName(dlg.CreatedPath) + "（立即可用）";
            }
        }

        private void OnEdit(object sender, RoutedEventArgs e)
        {
            var row = _grid.SelectedItem as PluginRow;
            if (row == null) { _status.Text = "请先选一个插件"; return; }

            // ★ UI 字段编辑（不用切文本编辑器）—— 用户反馈的核心诉求
            var dlg = new PluginEditDialog(row.Info);
            if (dlg.ShowDialog() == true && dlg.Saved)
            {
                Reload();
                _status.Text = "已保存：" + row.Info.File;
            }
        }

        private void OnEditSource(object sender, RoutedEventArgs e)
        {
            var row = _grid.SelectedItem as PluginRow;
            if (row == null) { _status.Text = "请先选一个插件"; return; }
            EditPlugin(row.Info);
        }

        private void EditPlugin(PluginInfo p)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = p.Path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _status.Text = "打开失败：" + ex.Message;
            }
        }

        // ---- 删除（进回收站，不真删）----
        private void OnDelete(object sender, RoutedEventArgs e)
        {
            var row = _grid.SelectedItem as PluginRow;
            if (row == null) { _status.Text = "请先选一个插件"; return; }

            var r = MessageBox.Show(
                "删除插件「" + row.Info.Name + "」？\n\n"
                + "文件会移到 _trash 子目录（不会真删），需要时可恢复。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            try
            {
                string trash = Path.Combine(_scanner.Dir, "_trash");
                if (!Directory.Exists(trash)) Directory.CreateDirectory(trash);
                string dest = Path.Combine(trash, row.Info.File);
                if (File.Exists(dest))
                    dest = Path.Combine(trash,
                        Path.GetFileNameWithoutExtension(row.Info.File) + "_"
                        + DateTime.Now.ToString("HHmmss") + ".ahk");
                File.Move(row.Info.Path, dest);
                _status.Text = "已移到回收站：" + Path.GetFileName(dest);
                Reload();
            }
            catch (Exception ex)
            {
                _status.Text = "删除失败：" + ex.Message;
            }
        }

        private void OpenPath(string p)
        {
            try
            {
                if (!Directory.Exists(p)) Directory.CreateDirectory(p);
                Process.Start(new ProcessStartInfo { FileName = p, UseShellExecute = true });
            }
            catch { }
        }

        // ============================================================
        /// <summary>
        /// 元数据行原地替换（保留行尾注释 + 原子写）。
        ///
        /// ★ 格式规范（2026-10-03 用户定案：改为制表符）：
        ///      global RVE_FN_Name&lt;TAB&gt;:=&lt;TAB&gt;"值"&lt;TAB&gt;; 注释
        ///   用 Tab 而非空格对齐 —— 字段名长度不一（Name/Match/Whole/Prio）时，
        ///   空格对齐要手工数，改一个字段就会错位；Tab 天然对齐。
        ///
        /// ★ 踩坑记录：
        ///   ① 曾把行尾注释的分号前空格弄丢 ⇒ 生成 `:= 250; 注释`
        ///      ⇒ AHK 报 "Illegal character in expression. Specifically: ;"
        ///      （= 值直接粘上了分号）。现在统一 `\t; 注释`。
        ///   ② 曾用单空格覆盖原双空格 ⇒ 每次改字段就把整块对齐打乱。
        /// </summary>
        public static bool WriteMetaFlag(string path, string key, string valStr)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string text = File.ReadAllText(path, System.Text.Encoding.UTF8);
                string eol = text.Contains("\r\n") ? "\r\n" : "\n";
                string[] lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

                const string TAB = "\t";
                string pat = @"^(\s*global\s+RVE_FN_" + key + @"\s*:=)(.*)$";
                bool hit = false;
                int ins = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    var m = System.Text.RegularExpressions.Regex.Match(lines[i], pat,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    bool isMeta = m.Success || System.Text.RegularExpressions.Regex.IsMatch(lines[i],
                        @"^\s*global\s+RVE_FN_\w+\s*:=",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                    if (m.Success)
                    {
                        // 新格式：global RVE_FN_X<TAB>:=<TAB>值<TAB>; 注释
                        string cm = TailComment(m.Groups[2].Value);
                        lines[i] = "global RVE_FN_" + key + TAB + ":=" + TAB + valStr
                            + (cm.Length > 0 ? TAB + cm : "");
                        hit = true;
                    }
                    if (isMeta) ins = i;
                }

                if (!hit)
                {
                    if (ins == 0)
                    {
                        ins = 0;
                        for (int i = 0; i < lines.Length; i++)
                            if (System.Text.RegularExpressions.Regex.IsMatch(lines[i],
                                @"^\s*#(Requires|SingleInstance|Include)",
                                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                                ins = i;
                    }
                    var list = new List<string>(lines);
                    list.Insert(ins + 1, "global RVE_FN_" + key + TAB + ":=" + TAB + valStr);
                    lines = list.ToArray();
                }

                string outText = string.Join(eol, lines);
                string tmp = path + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                File.WriteAllText(tmp, outText, new System.Text.UTF8Encoding(false));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 取行尾注释（引号外的第一个 ; 起，【含分号】）。
        /// ★ 返回含 `;`，调用方负责在前面加分隔（Tab/空格）。
        ///   曾把分号前的空格弄丢 ⇒ 值与分号粘连 ⇒ AHK 报非法字符。
        /// </summary>
        private static string TailComment(string rest)
        {
            if (string.IsNullOrEmpty(rest)) return "";
            bool inQ = false;
            for (int i = 0; i < rest.Length; i++)
            {
                char c = rest[i];
                if (c == '"') inQ = !inQ;
                else if (!inQ && c == ';') return rest.Substring(i).TrimEnd();
            }
            return "";
        }

        // ============================================================
        public class PluginRow
        {
            public PluginInfo Info;
            public PluginRow(PluginInfo p) { Info = p; }

            public bool Enabled
            {
                get { return Info.Enabled; }
                set { Info.Enabled = value; }
            }
            public string ShowText { get { return Info.ShowText; } }
            public string Name { get { return Info.Name; } }
            public string Match { get { return Info.Match; } }
            public string Out { get { return Info.Out; } }
            public string FormText { get { return Info.IsFormB ? "逻辑" : "模板"; } }
            public int Prio { get { return Info.Prio; } }
            public string File { get { return Info.File; } }
        }
    }
}
