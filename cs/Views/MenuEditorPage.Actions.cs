using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RunEverCs.Models;
using RunEverCs.Services;
using MItem = RunEverCs.Models.MenuItem;

namespace RunEverCs.Views
{
    public partial class MenuEditorPage
    {
        // ============================================================
        // 条目操作
        // ============================================================
        // ============================================================
        // ★ B2 结构操作抽象层（按钮 / 右键 / 键盘 **三入口归一**）
        //
        //   唯一分发点 = `DoTreeAction`。上面六个按钮、`BuildTreeContextMenu()` 生成的
        //   右键项、`ItemTree_PreviewKeyDown` 的按键映射，**全都只调它**；
        //   下面每个 `*Core` 只被 `DoTreeAction` 调用（grep 可核）。
        //
        //   为什么必须归一：结构操作有"Flatten → RefreshGrid → 重新 SelectNode"这套
        //   收尾契约（顺序错了就丢选中 / 丢焦点 / 视图与模型不一致）。三个入口各写一遍
        //   就是三份收尾逻辑，今天已经因为"文件拖入那条路少写了选中"出过 P0（见 DropFiles）。
        // ============================================================

        /// <summary>
        /// 执行一个结构动作（**唯一实现**）。产品路径（按钮 / 右键 / 键盘）走这个重载 ⇒ 破坏性操作先问。
        /// </summary>
        internal void DoTreeAction(TreeAction a)
        {
            DoTreeAction(a, true);
        }

        /// <summary>
        /// 唯一实现本体。<paramref name="ask"/> = 破坏性操作是否弹确认框
        /// （自检缝传 false：无人值守不能弹窗；产品路径恒为 true）。
        /// </summary>
        private void DoTreeAction(TreeAction a, bool ask)
        {
            switch (a)
            {
                case TreeAction.AddChild: AddChildCore(); break;
                case TreeAction.Rename: BeginRenameItem(); break;
                case TreeAction.Delete: DeleteItemCore(ask); break;
                case TreeAction.MoveUp: MoveItemCore(-1); break;
                case TreeAction.MoveDown: MoveItemCore(1); break;
                case TreeAction.Indent: IndentItemCore(1); break;
                case TreeAction.Outdent: IndentItemCore(-1); break;
                case TreeAction.Duplicate: DuplicateItemCore(); break;
                case TreeAction.MoveToGroup:
                case TreeAction.CopyToGroup:
                    // 交互层会弹「选目标分组」模态框；自检（ask=false）必须已经注入替身，
                    // 否则宁可跳过也不许把无人值守的运行挂死在弹窗上。
                    if (ask || _pickGroupOverride != null)
                        ToGroupInteractive(a == TreeAction.CopyToGroup);
                    else
                        SetStatus("自检模式下未注入目标分组选择器，已跳过", true);
                    break;
            }
        }

        private void AddChildCore()
        {
            var g = SelectedGroup();
            if (g == null) { SetStatus("请先选一个分组", true); return; }

            var sel = SelectedNode();
            var it = MenuTreeOps.AddChild(g.Items, FlatIndexOf(g, sel), _baseLevel);
            if (sel != null && sel.Item.Type == ActionType.Group) _collapsed.Remove(sel.Item);   // 组头 ⇒ 展开

            AfterStructOp(it, null);
        }

        /// <summary>删除（<paramref name="ask"/>=false 时跳过确认框，自检用）。</summary>
        private void DeleteItemCore(bool ask)
        {
            var g = SelectedGroup();
            var sel = SelectedNode();
            if (g == null || sel == null) { SetStatus("先选中要删除的条目", true); return; }

            if (ask)
            {
                int total = MenuTreeOps.CountSubtree(g.Items, FlatIndexOf(g, sel));
                string msg = total > 1
                    ? "删除「" + sel.Item.Title + "」及其 " + (total - 1) + " 个子项？"
                    : "删除条目「" + sel.Item.Title + "」？";
                if (MessageBox.Show(msg, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes) return;
            }

            DeleteNodeCore(sel);
        }

        /// <summary>
        /// 复制条目（含整棵子树）⇒ 插到被复制条目的**后面**（同层）。
        /// 副本的规则（标题加「副本」/ 不带快捷键 / 子项深拷贝）在
        /// <see cref="MenuTreeOps.CloneSubtree"/>（唯一实现），这里只管状态行。
        /// </summary>
        private void DuplicateItemCore()
        {
            var g = SelectedGroup();
            var sel = SelectedNode();
            if (g == null || sel == null) { SetStatus("先选中要复制的条目", true); return; }

            int idx = FlatIndexOf(g, sel);
            if (idx < 0) return;
            string srcTitle = sel.Item.Title;
            int children;
            var copy = CloneSubtree(g.Items, idx, out children);

            AfterStructOp(copy, "已复制「" + srcTitle + "」⇒「" + copy.Title + "」"
                + (children > 0 ? "（含 " + children + " 个直接子项）" : ""));
        }

        /// <summary>深拷贝一棵子树（薄壳：算法与判据在 <see cref="MenuTreeOps.CloneSubtree"/>）。</summary>
        private static MItem CloneSubtree(List<MItem> items, int index, out int directChildren)
        {
            return MenuTreeOps.CloneSubtree(items, index, out directChildren);
        }

        /// <summary>
        /// 重命名 = 把输入点定位到字段编辑区的「标题」框并全选（**不改模型**）。
        /// 为什么不做输入对话框：标题的唯一输入口就是这个 TextBox（TextChanged 已写回模型 +
        /// MarkDirty），再开一个对话框等于第二份改名实现；双击条目本来也是跳这里，行为一致。
        /// </summary>
        private void BeginRenameItem()
        {
            var g = SelectedGroup();
            var sel = SelectedNode();
            if (g == null || sel == null)
            {
                SetStatus("先选中要重命名的条目", true);
                return;
            }

            SelectNode(sel.Item);                              // 同步 _selNode / _selectedItem
            if (!ReferenceEquals(_lastPanelItem, sel.Item))    // 面板已经显示它就不必重建（防丢焦点）
                BuildFieldPanel(sel.Item);
            FocusTitleBox();
            SetStatus("在右侧「标题」框里改名（改完自动写回，记着点保存）", false);
        }

        /// <summary>
        /// 删除节点（含子树）。★ 与确认对话框分离 ⇒ 自检可无人值守驱动同一份实现。
        /// 删完选谁（同层后继 → 否则父项）由 <see cref="MenuTreeOps.DeleteSubtree"/> 一并给出。
        /// </summary>
        private void DeleteNodeCore(NodeRow sel)
        {
            var g = SelectedGroup();
            int idx = FlatIndexOf(g, sel);
            if (idx < 0) return;

            int total = MenuTreeOps.CountSubtree(g.Items, idx);
            ForgetCollapsed(sel);
            _selectedItem = null;
            _lastPanelItem = null;      // 面板必须重建

            MItem next;
            MenuTreeOps.DeleteSubtree(g.Items, idx, out next);
            AfterStructOp(next, "已删除 " + total + " 个条目");
        }

        private void MoveItemCore(int dir)
        {
            var g = SelectedGroup();
            var sel = SelectedNode();
            if (g == null || sel == null) return;

            int pos, children;
            if (!MenuTreeOps.Move(g.Items, FlatIndexOf(g, sel), dir, out pos, out children)) return;

            AfterStructOp(sel.Item, (dir < 0 ? "上移" : "下移") + "到同层第 " + pos + " 位"
                + (children > 0 ? "（连同 " + children + " 个子项）" : ""));
        }

        /// <summary>
        /// ⇥ = 成为**上一条（同层前驱）的子项**；⇤ = 升到**父项的同层后继位置**。
        /// 算法在 <see cref="MenuTreeOps.Indent"/>（整棵子树相对位移 + 拒绝判据），这里只管文案与折叠。
        /// </summary>
        private void IndentItemCore(int delta)
        {
            var g = SelectedGroup();
            var sel = SelectedNode();
            if (g == null || sel == null) return;

            MItem anchor;
            var outcome = MenuTreeOps.Indent(g.Items, FlatIndexOf(g, sel), delta, out anchor);
            if (outcome == MenuTreeOps.IndentOutcome.NoPrevSibling)
            {
                SetStatus("缩进被拒绝：它是同级第一条，没有可挂靠的上一条（先用 ↑↓ 挪次序）", true);
                return;
            }
            if (outcome == MenuTreeOps.IndentOutcome.AtTopLevel)
            {
                SetStatus("已在最外层，无法反缩进", true);
                return;
            }

            if (delta > 0 && anchor != null) _collapsed.Remove(anchor);   // 挂靠后要能看见新子项
            AfterStructOp(sel.Item, delta > 0
                ? "已成为「" + anchor.Title + "」的子项"
                : "已升到「" + anchor.Title + "」的同层");
        }

        /// <summary>
        /// 结构操作收尾（**唯一一份**）：重建树 → 重选 → 刷新预览 → 标脏 → 写状态行。
        /// 顺序错了就丢选中 / 丢焦点 / 视图与模型不一致，别在别处再抄一遍。
        /// </summary>
        private void AfterStructOp(MItem select, string status)
        {
            RefreshGrid();
            if (select != null) SelectNode(select);
            RefreshPreview();
            MarkDirty();
            if (status != null) SetStatus(status, false);
        }

        /// <summary>选中节点在当前分组扁平列表里的下标（没有选中 / 不在列表里 = -1）。</summary>
        private static int FlatIndexOf(MenuGroup g, NodeRow sel)
        {
            return (g == null || sel == null) ? -1 : g.Items.IndexOf(sel.Item);
        }

        // ============================================================
        // ★ C2：跨分组移动 / 复制
        //   右键「移动到分组… / 复制到分组…」是主路径；整棵子树一起走，
        //   落到目标后按该组最外层 Level 重算缩进，搬完立刻过 C1 的校验闸。
        //   模型算法（块搬移 / Level 重算 / 整体回滚）在 MenuTreeOps.MoveToGroup。
        // ============================================================

        /// <summary>
        /// 把当前选中项（含**整棵子树**）移动或复制到 <paramref name="targetGi"/> 分组。
        /// —— 右键路径与自检调的是这一份实现（不许第二套）。
        ///
        /// 算法与回滚在 <see cref="MenuTreeOps.MoveToGroup"/>（快照 → 搬 → C1 校验闸 → 整体还原）；
        /// 这里只管：前置 SKIP 判定、折叠账本清理、视图刷新与状态行。
        ///
        /// 返回："OK ..." / "REJECT ..." / "SKIP ..."（自检据此断言；调用方不解析）。
        /// </summary>
        internal string MoveSelectionToGroup(int targetGi, bool copy)
        {
            var src = SelectedGroup();
            if (src == null) { SetStatus("请先在左侧选一个分组", true); return "SKIP 没有选中分组"; }
            if (targetGi < 0 || targetGi >= _groups.Count || _groups[targetGi] == null)
            { SetStatus("目标分组不存在", true); return "SKIP 目标分组不存在"; }

            var dst = _groups[targetGi];
            if (ReferenceEquals(dst, src))
            {
                SetStatus("目标就是当前分组 —— 组内复制请用右键「复制条目」，组内调序请用 ↑↓ / ⇥", true);
                return "SKIP 目标分组 = 当前分组";
            }

            var roots = SelectionRoots();
            if (roots.Count == 0)
            {
                SetStatus("先选中要" + (copy ? "复制" : "移动") + "的条目", true);
                return "SKIP 没有选中条目";
            }

            // 搬走的条目整棵子树退出折叠账本（否则账本里留一堆再也点不到的 MItem）
            var rootIdx = new List<int>(roots.Count);
            foreach (var r in roots)
            {
                rootIdx.Add(src.Items.IndexOf(r));
                if (!copy) ForgetCollapsed(FindNode(CurrentRoots(), r));
            }

            var r2 = MenuTreeOps.MoveToGroup(_groups, src, dst, rootIdx, copy);

            if (r2.Code == "REJECT")
            {
                RebuildTree();
                if (_selectedItem != null) SelectNode(_selectedItem);
                else ApplyMarks();

                string why = r2.Detail.Replace("\n", "  |  ");
                SetStatus("已拒绝：" + (copy ? "复制" : "移动") + "会让结构不合法（" + r2.Issues
                    + " 处），已还原、未写盘  |  " + why, true);
                return "REJECT " + r2.Issues + " 处：" + why;
            }

            // ---- 收尾 ----
            if (!copy)
            {
                // 搬走的是"当前分组里的东西" ⇒ 整栏重刷（树 / 选中 / 字段面板 / 标题里的条目数）。
                // FillItems 对空分组也安全（清空字段面板 + 预览），不会留下指向别组条目的编辑框。
                _selectedItem = null;
                _lastPanelItem = null;
                _selNode = null;
                _marked.Clear();
                FillItems();
            }
            else
            {
                // 复制不改当前分组 ⇒ 保留当前选中，只重建一次树（折叠态/标记按 MItem 记账）
                RefreshGrid();
            }
            RefreshPreview();
            MarkDirty();

            string srcName = (src.Name ?? "").Trim();
            string dstName = (dst.Name ?? "").Trim();
            SetStatus((copy ? "已复制 " : "已移动 ") + r2.Count + " 个条目（" + r2.Blocks + " 棵子树）"
                + "：「" + srcName + "」→「" + dstName + "」"
                + (copy ? "（复制件是新条目，原条目未动）" : "（顶层缩进已按目标分组重算）"), false);
            return "OK " + (copy ? "copy " : "move ") + r2.Count + " 项 → " + dstName;
        }

        /// <summary>
        /// 右键「移动到分组… / 复制到分组…」的**交互层**：选目标 → 交给
        /// <see cref="MoveSelectionToGroup"/>。这里不碰模型结构。
        /// </summary>
        private void ToGroupInteractive(bool copy)
        {
            var src = SelectedGroup();
            if (src == null) { SetStatus("请先在左侧选一个分组", true); return; }

            var roots = SelectionRoots();
            if (roots.Count == 0)
            {
                SetStatus("先选中要" + (copy ? "复制" : "移动") + "的条目", true);
                return;
            }

            int ti = PickTargetGroup(copy);
            if (ti < 0)
            {
                SetStatus("已取消（没有选中目标分组）", false);
                return;
            }
            MoveSelectionToGroup(ti, copy);
        }

        /// <summary>选目标分组（替身优先；否则弹 <see cref="GroupPickDialog"/>）。-1 = 用户取消。</summary>
        private int PickTargetGroup(bool copy)
        {
            if (_pickGroupOverride != null)
            {
                int forced = _pickGroupOverride(copy);
                if (forced < 0 || forced >= _groups.Count) return -1;
                if (ReferenceEquals(_groups[forced], SelectedGroup())) return -1;
                return forced;
            }

            List<int> indices;
            var labels = PickerLabels(copy, out indices);
            if (labels.Count == 0)
            {
                SetStatus("没有其它分组可" + (copy ? "复制" : "移动") + "（先在左侧新建一个分组）", true);
                return -1;
            }

            var dlg = new GroupPickDialog(copy, labels, indices);
            if (dlg.ShowDialog() != true) return -1;
            return dlg.SelectedGroupIndex;
        }

        /// <summary>
        /// 候选分组清单（**排除当前分组**）—— 薄壳，清单生成在
        /// <see cref="MenuTreeOps.PickerLabels"/>（自检也走这一份）。
        /// </summary>
        private List<string> PickerLabels(bool copy, out List<int> indices)
        {
            return MenuTreeOps.PickerLabels(_groups, SelectedGroup(), out indices);
        }

        // ============================================================
        // ★ B2 入口②：右键菜单（项由枚举生成）
        //   权威依据：
        //   · FrameworkElement.ContextMenu —— 元素属性，右键由 WPF 自动打开（气泡路由事件 ContextMenuOpening 可取消）
        //     https://learn.microsoft.com/zh-cn/dotnet/api/system.windows.frameworkelement.contextmenuopening
        //   · MenuItem.Click 是普通 RoutedEvent（不是命令），MenuItem 实例自己就能 RaiseEvent
        //     https://learn.microsoft.com/zh-cn/dotnet/api/system.windows.controls.menuitem.click
        // ============================================================

        /// <summary>按枚举**声明顺序**生成右键项；点击只调 <see cref="DoTreeAction"/>。</summary>
        private ContextMenu BuildTreeContextMenu()
        {
            var cm = new ContextMenu();
            foreach (TreeAction a in Enum.GetValues(typeof(TreeAction)))
            {
                var act = a;   // ★ 必须拷贝：闭包捕获的是变量本身，直接用 a 会让所有项都指向最后一项
                // ★ 必须写全名：本文件同时 using 了 RunEverCs.Models ⇒ MenuItem 有歧义
                var mi = new System.Windows.Controls.MenuItem
                {
                    Header = ActionLabel(act),
                    InputGestureText = ActionGesture(act),
                    ToolTip = ActionTip(act)
                };
                mi.Click += (s, e) => DoTreeAction(act);

                // 分四组：改名 / 改结构 / 复制 / 跨分组
                if (act == TreeAction.Delete || act == TreeAction.Duplicate || act == TreeAction.MoveToGroup)
                    cm.Items.Add(new Separator());
                cm.Items.Add(mi);
            }
            return cm;
        }

        private static string ActionLabel(TreeAction a)
        {
            switch (a)
            {
                case TreeAction.AddChild: return "新建子项";
                case TreeAction.Rename: return "重命名";
                case TreeAction.Delete: return "删除";
                case TreeAction.MoveUp: return "上移";
                case TreeAction.MoveDown: return "下移";
                case TreeAction.Indent: return "缩进";
                case TreeAction.Outdent: return "反缩进";
                case TreeAction.Duplicate: return "复制条目";
                case TreeAction.MoveToGroup: return "移动到分组…";
                case TreeAction.CopyToGroup: return "复制到分组…";
            }
            return a.ToString();
        }

        private static string ActionGesture(TreeAction a)
        {
            switch (a)
            {
                case TreeAction.AddChild: return "Insert";
                case TreeAction.Rename: return "F2";
                case TreeAction.Delete: return "Delete";
                case TreeAction.MoveUp: return "Alt+↑";
                case TreeAction.MoveDown: return "Alt+↓";
                case TreeAction.Indent: return "Tab";
                case TreeAction.Outdent: return "Shift+Tab";
            }
            return "";   // 复制条目 / 跨分组搬：故意不给全局键（避免与系统/其它编辑器的习惯冲突）
        }

        private static string ActionTip(TreeAction a)
        {
            switch (a)
            {
                case TreeAction.AddChild: return "选中组头 ⇒ 成为它的子项；选中普通条目 ⇒ 插在它后面（同层）";
                case TreeAction.Rename: return "跳到右侧「标题」框改名（与双击条目同一处输入）";
                case TreeAction.Delete: return "删除选中条目及其全部子项（会先确认）";
                case TreeAction.Indent: return "成为同层上一条的子项（同级第一条无法缩进）";
                case TreeAction.Outdent: return "升到父项的同层，排在父项之后";
                case TreeAction.Duplicate: return "复制这一条（含子项）到它后面；副本不带快捷键";
                case TreeAction.MoveToGroup: return "把选中的条目（含整棵子树）搬到另一个分组；顶层缩进按目标分组的层级重算";
                case TreeAction.CopyToGroup: return "把选中的条目（含整棵子树）复制到另一个分组；复制件是新条目（新 ID），原条目不动";
            }
            return null;
        }

        // ============================================================
        private void OnSave(object sender, RoutedEventArgs e)
        {
            SaveCore(true);
        }

        /// <summary>
        /// 保存主流程（★ C1：**产品路径与自检路径共用这一份**）。
        ///
        /// 顺序就是纪律：
        ///   ① 读进来过才许存（B4a-1 第二道闸）
        ///   ② 写盘前**全量结构校验**（闸在 `MenuConfWriter.Save` 里，先于备份/tmp/rename）
        ///   ③ 不合法 ⇒ 一个字节都不写 + 弹逐条验证窗（行号 / 条目名 / 原因，双击可跳）
        ///   ④ 合法 ⇒ 落盘 → 重载（回到原处）→ 刷新预览 → 通知托盘增量解析
        ///
        /// <paramref name="showValidationDialog"/> = false 时只把原因写进状态行
        /// （`--selftest-menueditor` 无窗口，弹窗会把自检挂死）。
        /// </summary>
        private string SaveCore(bool showValidationDialog)
        {
            // ★ B4a-1 第二道闸：按钮禁用了，但代码路径（键盘快捷键 / 未走 UI 的调用）
            //   仍可能走到这里 —— 这里再判一次，绝不拿"没读进来的空集合"去写盘。
            if (!_loadOk)
            {
                SetStatus("配置没读进来，已拒绝保存（否则会把 menus.conf 写成空白）。"
                    + "先修好 config\\menus.conf，再点「放弃修改」重试。", true);
                return _statusText.Text;
            }

            // ★ B4b-3：先记住"我在哪"（分组下标 + 条目标题），重载后回到原处
            int keepGroup = _groupList == null ? -1 : _groupList.SelectedIndex;
            string keepTitle = (_selectedItem == null) ? null : _selectedItem.Title;

            string msg;
            List<MenuStructureIssue> issues;
            bool ok = _writer.Save(_groups, out msg, out issues);

            if (!ok)
            {
                if (issues != null && issues.Count > 0)
                {
                    // ★ C1：结构校验没过 —— 磁盘没动，给逐条原因（不是"结构错误"四个字）
                    SetStatus("结构校验没通过（" + issues.Count + " 处），menus.conf 原样未动："
                        + issues[0].Where, true);
                    if (showValidationDialog) ShowValidationDialog(issues);
                }
                else
                {
                    SetStatus(msg.Replace("\n", "  |  "), true);
                }
                return _statusText.Text;
            }

            _dirty = false;
            Reload(keepGroup, keepTitle);

            // ★ D-5：目标为空**不阻止保存**（配置格式零变更 ⇒ 不加新字段/新校验存储），
            //   但保存后**必须在状态行给出警告**（不静默、不硬拦 —— I-4 风格）。
            int emptyTargets = CountEmptyTargets();
            string saveMsg = msg.Replace("\n", "  |  ");
            if (emptyTargets > 0)
                SetStatus(saveMsg + "  |  ⚠ 第 " + emptyTargets + " 条目标为空（自检会报为非法配置）", true);
            else
                SetStatus(saveMsg, false);

            // ★ v8 时机②（AC-5②）：保存后触发**后台增量解析**
            //   （托盘进程注入；独立进程打开设置时为 null = 不回调）。
            //   不放在这里的话，「按缓存隐藏」（hideMissing=1）会把刚加进来的条目直接隐藏。
            if (AfterSave != null)
            {
                try { AfterSave(); }
                catch { /* 回调异常不能把保存结果吞掉 */ }
            }

            return _statusText.Text;
        }

        /// <summary>
        /// 保存前的验证窗（C1）：逐条列「第 N 行 / 条目 X：原因」。
        /// 双击任一行 ⇒ 关窗并跳到该条目；「继续编辑」= 留在原地改；
        /// 「放弃修改」= 从磁盘重读（丢弃内存里的改动，磁盘本来就没被改过）。
        /// </summary>
        private void ShowValidationDialog(List<MenuStructureIssue> issues)
        {
            var dlg = new MenuValidationDialog(issues);
            if (dlg.ShowDialog() != true)
            {
                // 继续编辑（或直接关窗）—— 若用户双击了某行，先跳过去，方便它就地改
                if (dlg.SelectedIssue != null) JumpToIssue(dlg.SelectedIssue);
                return;
            }

            // 放弃修改：重新从磁盘读 —— 内存里的非法改动整体丢弃（磁盘自始至终没动）
            _dirty = false;
            Reload();
            SetStatus("已放弃修改（重新读回 menus.conf，磁盘内容未变）", false);
        }

        /// <summary>把焦点/选中定位到某条问题对应的条目（验证窗双击、或事后排查用）。</summary>
        internal void JumpToIssue(MenuStructureIssue issue)
        {
            if (issue == null) return;

            if (issue.GroupIndex >= 0 && _groupList != null && issue.GroupIndex < _groupList.Items.Count)
                _groupList.SelectedIndex = issue.GroupIndex;

            if (issue.Item == null) return;
            var node = FindNode(CurrentRoots(), issue.Item);
            if (node == null) return;

            SelectNode(node);
            RevealNode(node);
            RefreshPreview();
            SetStatus("已定位到 " + issue.Where, false);
        }


        /// <summary>
        /// 目标为空的条目数（D-5：保存后警告用；自检侧另有对应报告）。
        /// ★ `group` 类型（纯分组标题）目标**本来就空** ⇒ 不计入。
        /// ★ 分割线（---）没有目标天经地义 ⇒ 也不计入（否则每加一条横线就报一条"非法"）。
        /// </summary>
        private int CountEmptyTargets()
        {
            int n = 0;
            if (_groups == null) return 0;
            foreach (var g in _groups)
            {
                if (g == null || g.Items == null) continue;
                foreach (var it in g.Items)
                {
                    if (it == null || it.Type == ActionType.Group) continue;
                    if (it.IsSeparator) continue;
                    if (string.IsNullOrEmpty(it.Target) || it.Target.Trim().Length == 0) n++;
                }
            }
            return n;
        }

        private void MarkDirty()
        {
            _dirty = true;
            SetStatus("有未保存的修改", false);
        }

        private void SetStatus(string s, bool isError)
        {
            _statusText.Text = s;
            _statusText.Foreground = isError ? Brushes.Firebrick : Brushes.Gray;
        }

        public bool HasUnsaved { get { return _dirty; } }

    }
}
