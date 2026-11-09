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
        /// <summary>
        /// **纯模型层**：命中 标题 / 快捷键 / 目标，返回 `MItem`（不碰任何控件 ⇒ 可无人值守自检）。
        /// 匹配判据在 <see cref="MenuTreeOps.Search"/>（唯一实现）。
        /// </summary>
        internal List<MItem> T_Search(string q)
        {
            var g = SelectedGroup();
            return MenuTreeOps.Search(g == null ? null : g.Items, q);
        }

        // ============================================================
        // ★ 测试缝（`--selftest-menueditor` 用）
        //   把「结构操作」暴露给同程序集的**无人值守**自检 —— 不经 UI 事件、不弹对话框；
        //   产品路径与自检路径调用的**是同一份实现**（DoTreeAction / DeleteNodeCore /
        //   ApplyDropCore / DropFiles），不存在"测试缝里另有一套逻辑"。
        //
        //   ★ B2 起：所有结构操作都从 `DoTreeAction` 走（`T_AddItem`/`T_Move`/`T_Indent`/
        //   `T_Delete` 也改成转调它）—— 三条产品入口与自检入口共用同一个分发点。
        // ============================================================
        internal List<NodeRow> T_Roots() { return CurrentRoots(); }
        internal MenuGroup T_Group() { return SelectedGroup(); }
        internal List<MenuGroup> T_Groups() { return _groups; }
        internal string T_SaveConf()
        {
            string m;
            bool ok = _writer.Save(_groups, out m);
            return (ok ? "OK " : "FAIL ") + m.Replace("\n", " | ");
        }

        /// <summary>自检缝（C1）：带出逐条问题列表（弹窗里显示的就是这些）。</summary>
        internal string T_SaveConfEx(out List<MenuStructureIssue> issues)
        {
            string m;
            bool ok = _writer.Save(_groups, out m, out issues);
            return (ok ? "OK " : "FAIL ") + m.Replace("\n", " | ");
        }

        /// <summary>自检缝（C1）：只跑校验、不写盘（判"闸拦没拦住"用）。</summary>
        internal List<MenuStructureIssue> T_Validate() { return MenuConfWriter.Validate(_groups); }

        /// <summary>
        /// 自检缝（C1）：**真的把验证窗建出来**（不 Show），回报标题 / 逐条文案 / 按钮，
        /// 用来在无窗口环境断言"行号 + 条目名 + 原因"和两个按钮都真的存在。
        /// </summary>
        internal static string T_DialogProbe(List<MenuStructureIssue> issues)
        {
            var dlg = new MenuValidationDialog(issues);
            var sb = new StringBuilder();
            sb.Append("标题=").Append(dlg.Title);
            sb.Append(" | 提示=").Append(dlg.HintText);
            sb.Append(" | 行=[").Append(string.Join(" ‖ ", dlg.IssueTexts().ToArray())).Append("]");
            sb.Append(" | 按钮=[").Append(string.Join("/", dlg.ButtonTexts().ToArray())).Append("]");
            sb.Append(" | 按钮数=").Append(dlg.ButtonTexts().Count);
            return sb.ToString();
        }

        /// <summary>自检缝（C1）：双击验证窗某行后的跳转（与双击事件调同一个方法）。返回是否真的选中了该条目。</summary>
        internal bool T_JumpToIssue(MenuStructureIssue issue)
        {
            JumpToIssue(issue);
            return issue != null && issue.Item != null && ReferenceEquals(_selectedItem, issue.Item);
        }
        internal MItem T_Selected() { return _selectedItem; }
        internal NodeRow T_Find(MItem it) { return FindNode(CurrentRoots(), it); }
        internal void T_Select(MItem it) { SelectNode(it); }
        internal void T_AddItem() { DoTreeAction(TreeAction.AddChild, false); }
        internal void T_Delete() { DoTreeAction(TreeAction.Delete, false); }
        internal void T_Move(int dir) { DoTreeAction(dir < 0 ? TreeAction.MoveUp : TreeAction.MoveDown, false); }
        internal void T_Indent(int delta) { DoTreeAction(delta > 0 ? TreeAction.Indent : TreeAction.Outdent, false); }

        /// <summary>
        /// ★ B2 自检缝：**代表全部入口**驱动一个结构动作（按钮 / 右键 / 键盘调的是同一个
        /// `DoTreeAction`；这里只跳过确认框）。
        /// </summary>
        internal void T_TreeAction(TreeAction a) { DoTreeAction(a, false); }

        /// <summary>
        /// ★ B2 自检缝：**真的去点右键菜单里的那一项**（走 MenuItem.Click 路由，不是直调动作）
        /// ⇒ 证明「菜单项 → 动作」这根线是通的。返回是否找到该项。
        /// </summary>
        internal bool T_ClickMenu(string header)
        {
            if (_contextMenu == null) return false;
            foreach (var o in _contextMenu.Items)
            {
                var mi = o as System.Windows.Controls.MenuItem;
                if (mi == null) continue;
                if (Convert.ToString(mi.Header) == header)
                {
                    mi.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    return true;
                }
            }
            return false;
        }

        /// <summary>★ B2 自检缝：右键菜单里**动作项**的表头（跳过分隔符），用于断言「八项齐全且顺序对」。</summary>
        internal List<string> T_MenuHeaders()
        {
            var list = new List<string>();
            if (_contextMenu == null) return list;
            foreach (var o in _contextMenu.Items)
            {
                var mi = o as System.Windows.Controls.MenuItem;
                if (mi != null) list.Add(Convert.ToString(mi.Header));
            }
            return list;
        }

        /// <summary>
        /// ★ B2 自检缝：键盘入口的无 UI 版 —— 与 `ItemTree_PreviewKeyDown` 走**同一个**
        /// `KeyToAction` 映射 + 同一个 `DoTreeAction`。返回实际执行的动作名（"none" = 未处理）。
        /// </summary>
        internal string T_Key(string keyName, string mods, bool editorFocused)
        {
            var key = (Key)Enum.Parse(typeof(Key), keyName);
            var m = ModifierKeys.None;
            if (!string.IsNullOrEmpty(mods))
                foreach (var part in mods.Split(new[] { '+', '|' }, StringSplitOptions.RemoveEmptyEntries))
                    m |= (ModifierKeys)Enum.Parse(typeof(ModifierKeys), part.Trim());

            var a = KeyToAction(key, m, editorFocused);
            if (!a.HasValue) return "none";
            DoTreeAction(a.Value, false);
            return a.Value.ToString();
        }

        /// <summary>★ B2 自检缝：字段面板当前展示的条目（重命名输入点绑定在谁身上的判据）。</summary>
        internal MItem T_PanelItem() { return _lastPanelItem; }

        /// <summary>★ B2 自检缝：字段面板「标题」框的当前文本。</summary>
        internal string T_TitleBoxText() { return _titleBox == null ? null : _titleBox.Text; }

        internal void T_Drop(NodeRow src, NodeRow target, DropWhere where) { ApplyDropCore(src, target, where); }
        /// <summary>自检缝：外部拖文件入树（与产品路径同一个 `DropFiles`；只按后缀识别，路径不必存在）。</summary>
        internal void T_DropFiles(string[] paths, NodeRow anchor) { DropFiles(paths, anchor); }
        internal bool T_WellFormed(out string why) { return MenuTreeOps.LevelsWellFormed(SelectedGroup().Items, out why); }
        internal int T_GroupCount() { return _groupList.Items.Count; }
        internal void T_SelectGroup(int i) { if (i >= 0 && i < _groupList.Items.Count) _groupList.SelectedIndex = i; }
        internal int T_BaseLevel() { return _baseLevel; }
        internal string T_Status() { return _statusText.Text; }

        // ---- ★ C2 自检缝：跨分组移动 / 复制（与产品路径同一份实现）----

        /// <summary>自检缝：模拟一次树上单击（普通 / Ctrl / Shift），走产品同一个 HandleNodeClick。</summary>
        internal void T_ClickNode(MItem it, ModifierKeys mods)
        {
            var n = FindNode(CurrentRoots(), it);
            if (n != null) HandleNodeClick(n, mods);
        }

        /// <summary>自检缝：当前多选条数。</summary>
        internal int T_MarkedCount() { return _marked.Count; }

        /// <summary>自检缝：当前多选条目（DFS 顺序）。</summary>
        internal List<MItem> T_Marked()
        {
            var all = new List<NodeRow>();
            CollectNodes(CurrentRoots(), all);
            var res = new List<MItem>();
            foreach (var n in all) if (_marked.Contains(n.Item)) res.Add(n.Item);
            return res;
        }

        /// <summary>自检缝：注入目标分组选择器替身（null = 恢复真实弹窗）。</summary>
        internal void T_SetGroupPicker(Func<bool, int> pick) { _pickGroupOverride = pick; }

        /// <summary>自检缝：搬运核心（与右键路径同一个 MoveSelectionToGroup）。</summary>
        internal string T_MoveSelectionToGroup(int gi, bool copy) { return MoveSelectionToGroup(gi, copy); }

        /// <summary>自检缝：某分组的最外层 Level（判"Level 重算"的基准）。</summary>
        internal int T_GroupBaseLevel(int gi)
        {
            return (gi >= 0 && gi < _groups.Count) ? MenuTreeOps.GroupBaseLevel(_groups[gi]) : -1;
        }

        /// <summary>自检缝：某分组条目数。</summary>
        internal int T_GroupItemCount(int gi)
        {
            return (gi >= 0 && gi < _groups.Count && _groups[gi] != null) ? _groups[gi].Items.Count : -1;
        }

        /// <summary>自检缝：某分组的层级不变式（跳级检查）。</summary>
        internal bool T_WellFormedGroup(int gi, out string why)
        {
            why = "分组下标越界";
            if (gi < 0 || gi >= _groups.Count || _groups[gi] == null) return false;
            return MenuTreeOps.LevelsWellFormed(_groups[gi].Items, out why);
        }

        /// <summary>自检缝：目标选择窗的候选分组名（不含当前分组）。</summary>
        internal List<string> T_PickerGroupNames()
        {
            List<int> idx;
            var labels = PickerLabels(false, out idx);
            var names = new List<string>();
            foreach (var i in idx) names.Add((_groups[i].Name ?? "").Trim());
            return names;
        }

        /// <summary>
        /// 自检缝：**真的把目标选择窗建出来**（不 Show），回报标题 / 提示 / 候选行 / 按钮。
        /// 用来在无窗口环境断言"候选清单 + 两个按钮 + 排除当前分组"。
        /// </summary>
        internal string T_PickerProbe(bool copy)
        {
            List<int> idx;
            var labels = PickerLabels(copy, out idx);
            var dlg = new GroupPickDialog(copy, labels, idx);

            var idxText = new List<string>();
            foreach (var i in idx) idxText.Add(i.ToString());

            return "标题=" + dlg.Title
                + " | 提示=" + dlg.HintText
                + " | 分组=[" + string.Join(" ‖ ", dlg.ItemTexts().ToArray()) + "]"
                + " | 索引=[" + string.Join(",", idxText.ToArray()) + "]"
                + " | 按钮=[" + string.Join("/", dlg.ButtonTexts().ToArray()) + "]"
                + " | 默认=" + dlg.SelectedGroupIndex;
        }

        /// <summary>自检缝（B4a-1）：配置是否真的读进来了。</summary>
        internal bool T_LoadOk() { return _loadOk; }

        /// <summary>自检缝（B4a-1）：「保存」按钮当前是否可用（应恒等于 T_LoadOk）。</summary>
        internal bool T_SaveEnabled() { return _saveBtn != null && _saveBtn.IsEnabled; }

        /// <summary>
        /// 自检缝（B4a-1）：走**产品同一份**保存主流程（含二次防御 + C1 校验闸），返回状态行文本。
        /// ★ 弹窗开关传 false（无窗口自检不能挂死）；弹窗本身由 `T_DialogProbe` 单独验证。
        /// </summary>
        internal string T_OnSave() { return SaveCore(false); }

        // ---- 自检缝（`--selftest-menueditor` 用；与产品路径同一份实现）----

        /// <summary>自检缝（L5）：实时预览框当前文本（应是**原文法**，与汉化无关）。</summary>
        internal string T_PreviewText() { return _previewBox == null ? null : _previewBox.Text; }

        /// <summary>自检缝（L5）：构建字段面板 —— 断言下拉内容需要控件实例。</summary>
        internal void T_ShowFields(MItem it) { BuildFieldPanel(it); }

        /// <summary>自检缝（L5）：下拉当前的（英文键, 中文显示）清单，顺序 = 下拉顺序。</summary>
        internal List<KeyValuePair<string, string>> T_TypeComboPairs()
        {
            var list = new List<KeyValuePair<string, string>>();
            if (_typeCombo == null) return list;
            foreach (var o in _typeCombo.Items)
            {
                var ci = o as ComboBoxItem;
                if (ci != null)
                    list.Add(new KeyValuePair<string, string>(
                        Convert.ToString(ci.Tag), Convert.ToString(ci.Content)));
            }
            return list;
        }

        /// <summary>自检缝（L5）：下拉选中项的**落盘键**（Tag）；未选中 ⇒ null。</summary>
        internal string T_TypeComboSelectedKey()
        {
            var ci = _typeCombo == null ? null : _typeCombo.SelectedItem as ComboBoxItem;
            return ci == null ? null : Convert.ToString(ci.Tag);
        }

        /// <summary>自检缝（L5）：下拉选中项的**显示文本**（Content）；未选中 ⇒ null。</summary>
        internal string T_TypeComboSelectedDisplay()
        {
            var ci = _typeCombo == null ? null : _typeCombo.SelectedItem as ComboBoxItem;
            return ci == null ? null : Convert.ToString(ci.Content);
        }

        /// <summary>自检缝（L5）：按显示文本选中（走产品同一个 SelectionChanged）。返回是否找到。</summary>
        internal bool T_SelectTypeByDisplay(string display)
        {
            if (_typeCombo == null) return false;
            foreach (var o in _typeCombo.Items)
            {
                var ci = o as ComboBoxItem;
                if (ci != null && Convert.ToString(ci.Content) == display)
                {
                    _typeCombo.SelectedItem = ci;
                    return true;
                }
            }
            return false;
        }

    }
}
