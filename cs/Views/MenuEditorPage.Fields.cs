using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RunEverCs.Models;
using RunEverCs.Services;
using MItem = RunEverCs.Models.MenuItem;

namespace RunEverCs.Views
{
    public partial class MenuEditorPage
    {
        // ============================================================
        // 字段编辑区 + 预览
        // ============================================================
        /// <summary>
        /// 2026-11-07：右侧字段区改「两列网格」——标签右对齐固定 88px + 控件拉伸。
        ///
        /// 原来 StackPanel 里标签一行、控件一行、宽度还各不一样（见改前截图），
        /// 视觉上就是「一团散沙」，这是用户说「设置面太丑」最直观的一条。
        /// fullRow=true 用于占位符这种本来就该占满整行的块。
        /// </summary>
        /// <summary>
        /// 「这个值明确是目录吗」（B4b-12 选选择器用）：空值不算、带扩展名的文件不算。
        /// 判据只认三种硬信号：结尾是 \ 或 /、目录确实存在。
        /// </summary>
        private static bool LooksLikeDirectory(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim().Trim('"').Trim();
            if (t.Length == 0) return false;
            if (t.EndsWith("\\") || t.EndsWith("/")) return true;
            try { return System.IO.Directory.Exists(t); } catch { return false; }
        }

        private void AddField(string label, FrameworkElement control, bool fullRow = false)
        {
            if (fullRow)
            {
                if (!string.IsNullOrEmpty(label))
                    _fieldPanel.Children.Add(new TextBlock
                    {
                        Text = label,
                        Foreground = Brushes.Gray,
                        Margin = new Thickness(0, 8, 0, 4)
                    });
                _fieldPanel.Children.Add(control);
                return;
            }

            var g = new Grid { Margin = new Thickness(0, 0, 0, 9) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(88) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var lb = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(lb, 0);
            g.Children.Add(lb);

            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            _fieldPanel.Children.Add(g);
        }

        private void BuildFieldPanel(MItem it)
        {
            _fieldPanel.Children.Clear();
            _lastPanelItem = it;          // 见 _suppressFieldRebuild：面板内容 = 这个条目
            if (it == null) return;

            // 标题
            var tbTitle = MkEdit(it.Title);
            _titleBox = tbTitle;     // 双击条目时把焦点送到这里
            tbTitle.TextChanged += (s, e) =>
            {
                it.Title = tbTitle.Text;
                RefreshGrid(); MarkDirty();
            };
            AddField("标题", tbTitle);

            // 快捷键
            // ★ 2026-11-07：快捷键改成**按键捕获**（按下即填），仍可手填。
            //   Backspace/Delete 清空；只接受单字符键（menus.conf 的 hotkey 本就是单字符）。
            // ★ B4b-4：MaxLength 由 2 改 1 —— 这里的能力就是"一个字符"，留 2 只会让
            //   手输 "ab" 这种非法值进配置；F1-F24 / Space 属于**全局热键**的能力，
            //   本框按下时明确拒绝并给提示（提示文案与能力一致，不承诺做不到的事）。
            var tbKey = MkEdit(it.Hotkey);
            tbKey.MaxLength = 1;
            tbKey.ToolTip = "按一个键即可填入（单字符；Backspace 清空）；也可直接输入。F1-F24 / Space 请到设置里设为全局热键";
            tbKey.PreviewKeyDown += (s, e) =>
            {
                var key = e.Key == System.Windows.Input.Key.System
                    ? e.SystemKey : e.Key;

                if (key == System.Windows.Input.Key.Back
                    || key == System.Windows.Input.Key.Delete)
                {
                    tbKey.Text = "";
                    e.Handled = true;
                    return;
                }

                string ch = SettingsKit.KeyToHotkeyChar(key);
                if (ch != null)
                {
                    if (ch.Length == 1)
                    {
                        tbKey.Text = ch;
                    }
                    else
                    {
                        // F1-F24 / Space：条目快捷键用不上（配置里只存一个字符）
                        tbKey.ToolTip = ch + " 是功能键，条目快捷键只支持单字符；"
                            + "要改弹出热键请到「设置 → 触发与行为」。";
                    }
                    e.Handled = true;      // 吃掉原按键，避免同时输入原始字符
                }
            };
            tbKey.TextChanged += (s, e) =>
            {
                // ★ 只去首尾空白，且不把"纯空格"抹成空串（与 SettingsKit.TrimHotkeyText 同口径）
                it.Hotkey = SettingsKit.TrimHotkeyText(tbKey.Text);
                RefreshGrid(); MarkDirty();
            };
            AddField("快捷键", tbKey);

            // 动作类型
            // ★ L5：**显示中文、落盘英文**。ComboBoxItem.Content = 中文（只给人看），
            //   Tag = 英文键（**唯一落盘值**：menus.conf 里永远是 group/run/...）。
            //   取落盘值时只认 Tag —— 绝不能用 Content / SelectedItem.ToString() 写盘
            //   （那样配置里会出现中文动作类型，解析器不认 ⇒ 菜单项静默失效）。
            var cbType = new ComboBox { Margin = new Thickness(0) };
            foreach (var pair in ActionTypeMap)
                cbType.Items.Add(new ComboBoxItem { Content = pair.Value, Tag = pair.Key });
            _typeCombo = cbType;

            // 回显：按英文键找对应中文项；找不到 ⇒ **明确报错并保持未选中**
            //   （不回退成第一项：静默落成 group 会悄悄改掉条目的动作类型）
            string curKey = ActionTypeKey(it.Type);
            foreach (var o in cbType.Items)
            {
                var ci = o as ComboBoxItem;
                if (ci != null && Convert.ToString(ci.Tag) == curKey)
                {
                    cbType.SelectedItem = ci;
                    break;
                }
            }
            if (cbType.SelectedItem == null)
                SetStatus("动作类型「" + curKey + "」没有对应的中文显示项 ⇒ 下拉保持未选中，"
                    + "不会静默改成别的类型（请检查配置 / 模型）", true);

            cbType.SelectionChanged += (s, e) =>
            {
                var sel = cbType.SelectedItem as ComboBoxItem;
                if (sel == null) return;
                string bad = ApplyActionTypeKey(it, Convert.ToString(sel.Tag));
                if (bad.Length > 0) { SetStatus(bad, true); return; }
                RefreshGrid(); BuildFieldPanel(it); MarkDirty();
            };
            AddField("动作类型", cbType);

            // flag（仅 action 有意义）
            if (it.Type == ActionType.Action)
            {
                var cbFlag = new ComboBox { Margin = new Thickness(0) };
                cbFlag.Items.Add("默认（隐藏）");
                cbFlag.Items.Add("hide  显式隐藏");
                cbFlag.Items.Add("show  显示窗口");
                cbFlag.SelectedIndex = it.Flag == ActionFlag.Show ? 2 : (it.Flag == ActionFlag.Hide ? 1 : 0);
                cbFlag.SelectionChanged += (s, e) =>
                {
                    it.Flag = cbFlag.SelectedIndex == 2 ? ActionFlag.Show
                        : (cbFlag.SelectedIndex == 1 ? ActionFlag.Hide : ActionFlag.None);
                    RefreshGrid(); RefreshPreview(); MarkDirty();
                };
                AddField("执行窗口", cbFlag);
            }

            // ---- 子菜单展示（2026-11-10）----
            //   仅 open 有意义：勾选 ⇒ 落盘为 `open sub <目标>`（目录可枚举时渲染期展开）；
            //   目标不是文件夹时不显示（勾了也没意义）。
            //   ★ 全局总闸在「设置 → 触发与行为 → 文件夹条目展开为子菜单」。
            if (it.Type == ActionType.Open)
            {
                string dirTarget = (it.Target ?? "").Trim(' ', '\t', '"');
                bool isDir = dirTarget.Length > 0 && dirTarget.IndexOf('%') < 0
                    && System.IO.Directory.Exists(dirTarget);
                if (isDir)
                {
                    var cbSub = new CheckBox
                    {
                        Content = "以子菜单展示（文件夹）",
                        IsChecked = it.Sub
                    };
                    cbSub.ToolTip = "弹菜单时当场枚举该目录生成子菜单；目录空/无权限则退化为普通 open。"
                        + "全局总闸在「设置 → 触发与行为 → 文件夹条目展开为子菜单」。";
                    cbSub.Checked += (s, e) => { it.Sub = true; RefreshGrid(); RefreshPreview(); MarkDirty(); };
                    cbSub.Unchecked += (s, e) => { it.Sub = false; RefreshGrid(); RefreshPreview(); MarkDirty(); };
                    AddField("子菜单", cbSub);
                }
            }

            // ---- 目标（v8b AC-B5）----
            //   run / action / open ⇒ 拆「文件名」+「参数」两框（**只是视图**：
            //     磁盘仍是单串 ⇒ 配置文件格式零变更，见 TargetSplit 注释）；
            //   url / send / keys / group ⇒ 目标语义不是"程序 + 参数" ⇒ 保持单框。
            //   ★ 两个框的 TextChanged 只改 it.Target 并刷新，**不**调 BuildFieldPanel
            //     （否则会撞 _suppressFieldRebuild 的焦点保护，见 :48-57 注释）。
            bool twoBox = TargetSplit.AppliesTo(it.Type);
            PathBox fileBox = null;
            TextBox argsBox = null;
            TextBox tbTarget = null;
            // ★ D-4：占位符按钮插入「最后获得焦点的那个框」；无焦点历史时默认落在参数框
            TextBox lastFocusBox = null;

            if (twoBox)
            {
                string curFile, curArgs;
                TargetSplit.Split(it.Target, out curFile, out curArgs);

                // ★ B4b-12：按上下文选选择器 —— 空值 / 不像目录 ⇒ **文件**选择器（过滤 *.exe;*.lnk）；
                //   当前值确实是目录（存在，或以 \ / 结尾）⇒ 文件夹选择器。
                //   原来固定 PathAny，而 PathAny 的判据是"有扩展名且不含空格"，于是
                //   清空后点「浏览…」弹的是**选文件夹**，根本选不到 exe（用户实报）。
                PathBox.Mode pickMode = LooksLikeDirectory(curFile)
                    ? PathBox.Mode.Folder : PathBox.Mode.File;
                fileBox = new PathBox(pickMode,
                    "程序或文件 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
                    "选择程序或文件", ProgramCandidates());
                fileBox.Text = curFile;          // 先赋初值：此时尚未订阅 Changed
                lastFocusBox = fileBox.Box;
                fileBox.Box.GotKeyboardFocus += (s, e) => lastFocusBox = fileBox.Box;
                fileBox.Changed += () =>
                {
                    it.Target = TargetSplit.Join(fileBox.Text, argsBox.Text);
                    RefreshGrid(); RefreshPreview(); MarkDirty();
                };
                AddField("文件名", fileBox);

                argsBox = MkEdit(curArgs);
                argsBox.AcceptsReturn = true;
                argsBox.Height = 54;
                argsBox.TextWrapping = TextWrapping.Wrap;
                argsBox.GotKeyboardFocus += (s, e) => lastFocusBox = argsBox;
                argsBox.TextChanged += (s, e) =>
                {
                    it.Target = TargetSplit.Join(fileBox.Text, argsBox.Text);
                    RefreshGrid(); RefreshPreview(); MarkDirty();
                };
                AddField("参数", argsBox);
                lastFocusBox = argsBox;
            }
            else
            {
                tbTarget = MkEdit(it.Target);
                tbTarget.AcceptsReturn = true;
                tbTarget.Height = 54;
                tbTarget.TextWrapping = TextWrapping.Wrap;
                tbTarget.GotKeyboardFocus += (s, e) => lastFocusBox = tbTarget;
                tbTarget.TextChanged += (s, e) =>
                {
                    it.Target = tbTarget.Text;
                    RefreshGrid(); RefreshPreview(); MarkDirty();
                };
                AddField("目标", tbTarget);
                lastFocusBox = tbTarget;
            }

            // ★ 动态占位符面板（用户要求"全部有效的都显示出来"）
            //   点击即插入到最后编辑的那个目标框 —— 不用记也不用猜。
            var wrap = new WrapPanel { Margin = new Thickness(0, 3, 0, 4) };
            foreach (var ph in Placeholders)
            {
                var b = new Button
                {
                    Content = ph.Key,
                    Padding = new Thickness(6, 2, 6, 2),
                    Margin = new Thickness(0, 0, 4, 4),
                    ToolTip = ph.Value,
                    FontFamily = new FontFamily("Consolas")
                };
                string key = ph.Key;
                b.Click += (s, e) =>
                {
                    var box = lastFocusBox ?? argsBox ?? tbTarget;
                    if (box != null) InsertAtCursor(box, key);
                };
                wrap.Children.Add(b);
            }
            AddField("可用占位符", wrap, true);

            RefreshPreview();
        }

        /// <summary>
        /// 全部有效的动态占位符（与执行器 Executor.ReplaceVars 一一对应）。
        /// ★ 这份清单必须与 Services\Executor.cs 的 ReplaceVars 保持同步，
        ///   否则界面会承诺一个实际不生效的占位符。
        /// </summary>
        public static readonly List<KeyValuePair<string, string>> Placeholders =
            new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("%s",      "搜索词：优先选中文本，无文本时用剪贴板"),
            new KeyValuePair<string, string>("%text%",  "选中的文本"),
            new KeyValuePair<string, string>("%file%",  "选中的第一个文件/文件夹路径"),
            new KeyValuePair<string, string>("%clip%",  "当前剪贴板文本"),
            new KeyValuePair<string, string>("%win%",   "前台窗口标题"),
            new KeyValuePair<string, string>("%proc%",  "前台窗口的进程名（如 chrome.exe）"),
        };

        /// <summary>把焦点送到字段编辑区的"标题"框（双击条目时用）。</summary>
        private void FocusTitleBox()
        {
            if (_titleBox == null) return;
            try
            {
                _titleBox.Focus();
                _titleBox.SelectAll();
            }
            catch { }
        }

        /// <summary>把文本插入到 TextBox 光标处（无焦点时追加到末尾）。</summary>
        private void InsertAtCursor(TextBox tb, string text)
        {
            int pos = tb.CaretIndex;
            if (pos < 0 || pos > tb.Text.Length) pos = tb.Text.Length;
            tb.Text = tb.Text.Insert(pos, text);
            tb.CaretIndex = pos + text.Length;
            tb.Focus();
        }

        private void RefreshPreview()
        {
            if (_selectedItem == null) { _previewBox.Text = ""; return; }
            string line = MenuConfWriter.FormatItem(_selectedItem);
            _previewBox.Text = line;
        }

        /// <summary>
        /// 重建中栏（v9：原 DataGrid 重建 → **TreeView 重建**）。
        /// ★ 重建期间抑制「选中变更 → 重建字段面板」，并在重建后按 `_selectedItem` 还原选中；
        ///   否则字段编辑框每敲一个字符都会因控件重建而丢焦点（2026-10-04 的老 bug，别回归）。
        /// </summary>
        private void RefreshGrid()
        {
            bool prev = _suppressFieldRebuild;
            _suppressFieldRebuild = true;
            try
            {
                var keep = _selectedItem;
                _selNode = null;
                RebuildTree();
                if (keep != null) SelectNode(keep);
            }
            finally { _suppressFieldRebuild = prev; }
        }

        private TextBox MkEdit(string v)
        {
            return new TextBox
            {
                Text = v ?? "",
                Padding = new Thickness(4, 2, 4, 2),
                Margin = new Thickness(0, 2, 0, 4)
            };
        }

        /// <summary>
        /// 文件名框的候选（可选用）：`cache\paths.txt` 里已解析过的程序名（"名=全路径" 左侧）。
        /// 读不到（无缓存 / 读取失败）⇒ 返回 null ⇒ PathBox 不渲染候选下拉（不影响其它功能）。
        /// </summary>
        private string[] ProgramCandidates()
        {
            try
            {
                if (!System.IO.File.Exists(_cachePath)) return null;
                var names = new List<string>();
                foreach (var line in System.IO.File.ReadAllLines(_cachePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string n = line.Substring(0, eq).Trim();
                    if (n.Length == 0) continue;
                    if (names.Contains(n)) continue;
                    names.Add(n);
                    if (names.Count >= 40) break;
                }
                if (names.Count == 0) return null;
                names.Sort(StringComparer.OrdinalIgnoreCase);
                return names.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static string TypeStr(ActionType t)
        {
            switch (t)
            {
                case ActionType.Run: return "run";
                case ActionType.Open: return "open";
                case ActionType.Url: return "url";
                case ActionType.Action: return "action";
                case ActionType.Send: return "send";
                case ActionType.Keys: return "keys";
                // 分割线不是动作：menus.conf 里的原文就是一行 ---
                case ActionType.Separator: return "---";
                default: return "group";
            }
        }

        // ============================================================
        // ★ L5：动作类型的「显示 ⇄ 落盘」唯一映射表
        //   · 显示 = 中文（只活在下拉里，给人看）
        //   · 落盘 = 英文键（menus.conf 里永远是 group/run/open/url/action/send/keys）
        //   声明顺序 = 下拉顺序。合法值集合 = `Models.ActionType` 的 7 个，
        //   **不在这里扩**（新增类型要同时动模型 / 解析器 / 执行器）。
        // ============================================================
        internal static readonly KeyValuePair<string, string>[] ActionTypeMap =
        {
            new KeyValuePair<string, string>("group",  "分组"),
            new KeyValuePair<string, string>("run",    "运行程序"),
            new KeyValuePair<string, string>("open",   "打开路径"),
            new KeyValuePair<string, string>("url",    "网址"),
            new KeyValuePair<string, string>("action", "执行命令"),
            new KeyValuePair<string, string>("send",   "发送文本"),
            new KeyValuePair<string, string>("keys",   "发送按键"),
            // ★ 2026-11-12：分割线（配置里写作一行 `---`）。没有标题/目标/快捷键，
            //   只在菜单里产生一条横线；选它即把当前条目变成分割线条目。
            new KeyValuePair<string, string>("---",    "分隔符")
        };

        /// <summary>枚举 → **落盘用的英文键**（= `menus.conf` 里的值）。</summary>
        internal static string ActionTypeKey(ActionType t) { return TypeStr(t); }

        /// <summary>枚举 → 下拉显示的中文；不在映射表里返回 null（**不回退成第一项**）。</summary>
        internal static string ActionTypeDisplay(ActionType t)
        {
            string key = TypeStr(t);
            foreach (var pair in ActionTypeMap)
                if (pair.Key == key) return pair.Value;
            return null;
        }

        /// <summary>
        /// 英文键 → 枚举。**严格**：未知值返回 false。
        /// （旧 `ParseType` 的 `default ⇒ group` 正是"静默落成第一项"的源头，已废弃。）
        /// </summary>
        internal static bool TryParseActionTypeKey(string key, out ActionType t)
        {
            t = ActionType.Group;
            if (string.IsNullOrEmpty(key)) return false;
            switch (key.Trim().ToLowerInvariant())
            {
                case "group": t = ActionType.Group; return true;
                case "run": t = ActionType.Run; return true;
                case "open": t = ActionType.Open; return true;
                case "url": t = ActionType.Url; return true;
                case "action": t = ActionType.Action; return true;
                case "send": t = ActionType.Send; return true;
                case "keys": t = ActionType.Keys; return true;
                // 分隔符：落盘键就是配文里的 `---`，与 TypeStr(Separator) 对称
                case "---": t = ActionType.Separator; return true;
                default: return false;
            }
        }

        /// <summary>
        /// ★ L5：下拉「选中项 → 类型」的**唯一判定点**（产品 SelectionChanged 与自检共用）。
        /// 合法 ⇒ 写入 `it.Type` 并返回 ""；非法 ⇒ **不改 `it.Type`**，返回人话原因。
        /// </summary>
        internal static string ApplyActionTypeKey(MItem it, string key)
        {
            if (it == null) return "没有选中条目，动作类型未应用";
            ActionType t;
            if (!TryParseActionTypeKey(key, out t))
                return "动作类型「" + key + "」不是合法值，已忽略（只能是 "
                    + "group / run / open / url / action / send / keys / ---）";
            it.Type = t;
            // ★ 分割线没有标题/目标/快捷键/flag/sub：在这里一次性清干净。
            //   否则这些字段留在内存里，保存时被 FormatItem 丢掉 ⇒ 用户以为改了但没落盘。
            if (t == ActionType.Separator)
            {
                it.Title = "";
                it.Hotkey = "";
                it.Target = "";
                it.Flag = ActionFlag.None;
                it.Sub = false;
            }
            return "";
        }

    }
}
