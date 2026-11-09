using System;
using System.Collections.Generic;
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
        private void FillItems()
        {
            _fieldPanel.Children.Clear();
            _previewBox.Text = "";
            _selectedItem = null;
            _lastPanelItem = null;      // 换了分组 ⇒ 面板内容必然不同（见 _suppressFieldRebuild）

            var g = SelectedGroup();
            if (g == null)
            {
                if (_midHead != null) _midHead.Text = "条目（左侧选一个分组）";
                if (_itemTree != null) _itemTree.ItemsSource = null;
                return;
            }

            // ★ 标题里显示当前分组名 + 依据（用户反馈"分组名没显示"）
            string basis = MenuGroup.TypeToString(g.Type);
            if ((g.Type == GroupType.Ext || g.Type == GroupType.Proc) && g.Keywords.Count > 0)
                basis += ": " + string.Join(" ", g.Keywords);
            if (_midHead != null)
                _midHead.Text = "条目 — 「" + g.Name + "」   依据: " + basis
                    + "   （共 " + g.Items.Count + " 项）";

            RebuildTree();
            var roots = CurrentRoots();
            if (roots.Count > 0) SelectNode(roots[0].Item);
        }

        private MenuGroup SelectedGroup()
        {
            var r = _groupList.SelectedItem as GroupRow;
            return r == null ? null : r.Group;
        }

        // ============================================================
        // ★ v9 树形中栏：构建 / 选中 / 回写
        // ============================================================

        /// <summary>由「扁平 `g.Items`（`Level`）」构建树；折叠态按 MItem 记账 ⇒ 重建后不丢。</summary>
        private void RebuildTree()
        {
            if (_itemTree == null) return;
            var g = SelectedGroup();
            if (g == null) { _itemTree.ItemsSource = null; return; }

            // ★ 层级基准：本配置里"最外层"的 Level 是多少（menus.conf 惯例是 1，不是 0）。
            //   Flatten 回写时统一加这个基准 ⇒ 结构操作**不会顺手把整份配置的层级整体挪一位**
            //   （否则保存后 diff 会波及每一行，用户看到的是"我改一条，全文件都被重排了"）。
            _baseLevel = MenuTreeOps.BaseLevelOf(g.Items);

            var roots = new List<NodeRow>();
            var stack = new List<NodeRow>();     // stack[d] = 深度 d 上的最近节点
            foreach (var it in g.Items)
            {
                int lv = it.Level - _baseLevel;
                if (lv < 0) lv = 0;
                while (stack.Count > lv) stack.RemoveAt(stack.Count - 1);

                var node = new NodeRow(this, it);
                NodeRow parent = (lv > 0 && stack.Count >= lv) ? stack[lv - 1] : null;
                if (parent != null) { node.Parent = parent; parent.Children.Add(node); }
                else roots.Add(node);

                while (stack.Count <= lv) stack.Add(null);
                stack[lv] = node;
            }

            _itemTree.ItemsSource = roots;
        }

        private List<NodeRow> CurrentRoots()
        {
            var e = _itemTree == null ? null : _itemTree.ItemsSource as IEnumerable<NodeRow>;
            return e == null ? new List<NodeRow>() : new List<NodeRow>(e);
        }

        /// <summary>在树里按 MItem 找节点（引用相等）。</summary>
        private static NodeRow FindNode(IEnumerable<NodeRow> nodes, MItem it)
        {
            foreach (var n in nodes)
            {
                if (ReferenceEquals(n.Item, it)) return n;
                var r = FindNode(n.Children, it);
                if (r != null) return r;
            }
            return null;
        }

        /// <summary>递归清除所有节点的选中标记（ItemsControl 版必须手动维护，没有 TreeView 的容器管理）。</summary>
        private static void ClearSelection(IEnumerable<NodeRow> nodes)
        {
            if (nodes == null) return;
            foreach (var n in nodes)
            {
                n.IsSelected = false;
                ClearSelection(n.Children);
            }
        }

        /// <summary>选中某个 MItem 的节点（自动展开其祖先）。★ 单选语义：会清空多选。</summary>
        private void SelectNode(MItem it)
        {
            _selNode = null;
            _marked.Clear();
            _anchor = it;
            if (it == null) { ApplyMarks(); return; }
            _selectedItem = it;
            var roots = CurrentRoots();
            var node = FindNode(roots, it);
            if (node == null) { ApplyMarks(); return; }
            // ★ 2026-11-07：ItemsControl 不像 TreeView 会自动取消别的项的选中态，
            //   必须显式清空，否则点过的每一项都会保持高亮。
            ClearSelection(roots);
            for (var p = node.Parent; p != null; p = p.Parent) p.IsExpanded = true;
            node.IsSelected = true;
            _selNode = node;
            _marked.Add(it);
            ApplyMarks();
        }

        /// <summary>当前选中的树节点（null = 无选中）。</summary>
        private NodeRow SelectedNode()
        {
            if (_selNode != null) return _selNode;
            return _itemTree == null ? null : _itemTree.SelectedItem as NodeRow;
        }

        /// <summary>
        /// ★ 树 → 扁平 `g.Items` 回写：DFS 顺序 + `Level = 基准 + 深度`。
        /// **层级的唯一源是树** ⇒ 结构天然满足 🔴 不变式「父项后紧跟其全部子项 + 不跳级」；
        /// 算法在 <see cref="MenuTreeOps.FlattenTree"/>，回写后再断言一次（防御性，正常永不触发）。
        /// </summary>
        private void Flatten()
        {
            var g = SelectedGroup();
            if (g == null) return;

            var flat = new List<MItem>();
            MenuTreeOps.FlattenTree(CurrentRoots(), n => n.Item, n => n.Children, 0, _baseLevel, flat);
            g.Items.Clear();
            g.Items.AddRange(flat);

            string why;
            if (!MenuTreeOps.LevelsWellFormed(g.Items, out why))
                SetStatus("⚠ 内部层级异常（请反馈）：" + why, true);
        }

        /// <summary>把节点从当前父项 / 根集合里摘掉（拖拽 / 移动用）。</summary>
        private void Detach(NodeRow node)
        {
            if (node == null) return;
            if (node.Parent != null) { node.Parent.Children.Remove(node); }
            else
            {
                var roots = CurrentRoots();
                roots.Remove(node);
                _itemTree.ItemsSource = roots;
            }
        }

        /// <summary>节点是否落在 ancestor 的子树里（防"拖到自己肚子里"）。</summary>
        private static bool IsInSubtree(NodeRow node, NodeRow ancestor)
        {
            for (var p = node; p != null; p = p.Parent)
                if (ReferenceEquals(p, ancestor)) return true;
            return false;
        }

        /// <summary>折叠子树里所有节点从折叠账本里清掉（删除节点时防账本悬挂）。</summary>
        private void ForgetCollapsed(NodeRow node)
        {
            if (node == null) return;
            _collapsed.Remove(node.Item);
            foreach (var c in node.Children) ForgetCollapsed(c);
        }

        /// <summary>
        /// 选中一个节点（单选）：写 _selNode + 清空多选 + 按需重建字段面板。
        /// ★ 焦点保护（沿用 v9 语义）：树重建期间只同步选中项；选中项与当前编辑项
        ///   是同一个 MItem 时不重建（否则会撞输入框焦点，边打字边跳）。
        /// </summary>
        private void SelectNode(NodeRow node)
        {
            if (node == null) return;
            _marked.Clear();
            _marked.Add(node.Item);
            _anchor = node.Item;
            SetPrimary(node);
        }

        /// <summary>
        /// ★ C2：把某节点设为「当前编辑项」（绿底），**不动多选集合**（蓝底那批）。
        /// 多选与单选的分工：绿底 = 字段面板正在编辑的那一条；蓝底 = 会被一起搬的那批。
        /// </summary>
        private void SetPrimary(NodeRow node)
        {
            if (node == null) return;
            // 先清掉其它节点的选中标记（TreeView 单选，必须显式清）
            ClearSelection(CurrentRoots());
            node.IsSelected = true;          // INotifyPropertyChanged ⇒ DataTrigger 立即生效

            _selNode = node;
            _selectedItem = node.Item;
            ApplyMarks();
            if (_suppressFieldRebuild) return;
            if (ReferenceEquals(_lastPanelItem, node.Item)) return;
            BuildFieldPanel(_selectedItem);
        }

        /// <summary>把 <see cref="_marked"/> 同步到树节点的 <see cref="NodeRow.IsMarked"/>（视觉蓝底）。</summary>
        private void ApplyMarks()
        {
            ApplyMarksWalk(CurrentRoots());
        }

        private void ApplyMarksWalk(IEnumerable<NodeRow> nodes)
        {
            if (nodes == null) return;
            foreach (var n in nodes)
            {
                bool m = _marked.Contains(n.Item);
                if (n.IsMarked != m) n.IsMarked = m;
                ApplyMarksWalk(n.Children);
            }
        }

        /// <summary>按 DFS 顺序收集树节点（多选区间 / 搬动顺序都依赖这个顺序）。</summary>
        private static void CollectNodes(IEnumerable<NodeRow> nodes, List<NodeRow> into)
        {
            if (nodes == null) return;
            foreach (var n in nodes)
            {
                into.Add(n);
                CollectNodes(n.Children, into);
            }
        }

        /// <summary>
        /// ★ C2：树上的「单击选中」**唯一实现**（产品鼠标事件与自检共用）。
        ///   · 普通单击 = 单选（清空多选）
        ///   · Ctrl + 单击 = 反选该条（至少留一条）
        ///   · Shift + 单击 = 从锚点连选到该条（DFS 区间）
        /// </summary>
        internal void HandleNodeClick(NodeRow node, ModifierKeys mods)
        {
            if (node == null) return;

            bool ctrl = (mods & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (mods & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (shift && _anchor != null)
            {
                MarkRangeTo(node);
                return;
            }
            if (ctrl)
            {
                if (!_marked.Remove(node.Item)) _marked.Add(node.Item);
                if (_marked.Count == 0) _marked.Add(node.Item);   // 永远不空选
                _anchor = node.Item;
                SetPrimary(node);
                return;
            }
            SelectNode(node);
        }

        /// <summary>Shift 连选：把锚点与目标之间的 DFS 区间整段标记（含两端）。</summary>
        private void MarkRangeTo(NodeRow node)
        {
            var order = new List<NodeRow>();
            CollectNodes(CurrentRoots(), order);

            int ia = -1, ib = -1;
            for (int i = 0; i < order.Count; i++)
            {
                if (ia < 0 && ReferenceEquals(order[i].Item, _anchor)) ia = i;
                if (ReferenceEquals(order[i], node)) ib = i;
            }
            if (ia < 0 || ib < 0) { SelectNode(node); return; }

            int lo = Math.Min(ia, ib), hi = Math.Max(ia, ib);
            _marked.Clear();
            for (int i = lo; i <= hi; i++) _marked.Add(order[i].Item);
            SetPrimary(node);
        }

        /// <summary>
        /// ★ C2：本次要搬动的**顶层**条目（DFS 顺序）= 标记集合 ∪ 主选中项；
        /// 父子都被选中时只保留祖先。判据与去重都在 <see cref="MenuTreeOps.SelectionRootIndices"/>。
        /// </summary>
        private List<MItem> SelectionRoots()
        {
            var res = new List<MItem>();
            var g = SelectedGroup();
            if (g == null) return res;
            foreach (var i in MenuTreeOps.SelectionRootIndices(g.Items, _marked, _selectedItem))
                res.Add(g.Items[i]);
            return res;
        }

        private void ItemTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var node = e.NewValue as NodeRow;
            if (node == null) return;
            _selNode = node;
            _selectedItem = node.Item;
            if (_suppressFieldRebuild) return;
            if (ReferenceEquals(_lastPanelItem, node.Item)) return;
            BuildFieldPanel(_selectedItem);
        }

        /// <summary>
        /// 把节点滚进视口。
        /// WPF 的 `TreeView` **没有** `ScrollIntoView`（那是 `ListBox` 的）；
        /// `BringIntoView()` 是 `FrameworkElement` 上的方法，但它只对**已生成的容器**有效。
        /// 容器是按需生成的 ⇒ 必须从根沿路径逐级用 `ItemContainerGenerator.ContainerFromItem`
        /// 取下一层，且**每层取之前先 UpdateLayout()**（展开后不跑布局，子层会返回 null）。
        /// 无窗口 / 未布局 / 折叠未展开时容器为 null ⇒ **静默返回**，绝不抛异常
        /// （`--selftest-menueditor` 就是无窗口环境，主路径必须能跑）。
        /// </summary>
        internal void RevealNode(NodeRow node)
        {
            if (node == null || _itemTree == null) return;
            if (!_itemTree.IsLoaded) return;   // 无窗口：容器永远不会生成，直接跳过

            try
            {
                // 根 → 目标的节点路径（祖先展开已由 SelectNode 负责，这里只按路径取容器）
                var path = new List<NodeRow>();
                for (var n = node; n != null; n = n.Parent) path.Insert(0, n);

                ItemsControl parent = _itemTree;
                TreeViewItem container = null;
                foreach (var n in path)
                {
                    parent.UpdateLayout();   // 上一级刚展开 ⇒ 需要一次布局，子层容器才存在
                    container = parent.ItemContainerGenerator.ContainerFromItem(n) as TreeViewItem;
                    if (container == null) return;   // 虚拟化 / 未展开 / 未布局：静默放弃
                    parent = container;
                }
                if (container != null) container.BringIntoView();
            }
            catch
            {
                // 滚动只是"锦上添花"：任何布局期异常都不许冒泡到拖放 / 搜索主路径
            }
        }

        // ============================================================
        // ★ 定位式搜索（S1）：匹配 标题 / 快捷键 / 目标；**只选中 + 滚动，不过滤**。
        //   不过滤的原因见字段声明处（过滤会把视图外的条目从 Flatten 源里摘掉）。
        // ============================================================

        /// <summary>输入变化 ⇒ 重新求命中集（不跳转），计数显示「k/N」或 "-/N"。</summary>
        private void ResetSearchHits()
        {
            _searchResults = T_Search(_searchBox == null ? null : _searchBox.Text);
            _searchIndex = -1;
            ShowSearchCount();
        }

        private void ShowSearchCount()
        {
            if (_searchHits == null) return;
            if (_searchBox == null || string.IsNullOrEmpty(_searchBox.Text)) { _searchHits.Text = ""; return; }
            _searchHits.Text = (_searchIndex < 0 ? "-" : (_searchIndex + 1).ToString())
                + "/" + _searchResults.Count;
        }

        /// <summary>
        /// 跳到下（dir=1）/ 上（dir=-1）一个命中：选中 + 展开祖先（`SelectNode` 内部已做）+ 滚到可见。
        /// 每次跳转都重求命中集 ⇒ 边输入边 Enter 也不会用到过期结果。
        /// </summary>
        private void SearchJump(int dir)
        {
            if (_searchBox == null) return;
            string q = _searchBox.Text;
            if (string.IsNullOrEmpty(q)) return;

            _searchResults = T_Search(q);
            int total = _searchResults.Count;
            if (total == 0)
            {
                _searchIndex = -1;
                ShowSearchCount();
                SetStatus("没有匹配「" + q + "」的条目", true);
                return;
            }

            if (_searchIndex < 0) _searchIndex = dir > 0 ? 0 : total - 1;
            else _searchIndex = ((_searchIndex + dir) % total + total) % total;   // 环形

            // 第一次跳转前记下"原条目"，Esc 清空时回去
            if (_searchOrigin == null) _searchOrigin = _selectedItem;

            var node = FindNode(CurrentRoots(), _searchResults[_searchIndex]);
            if (node == null)
            {
                SetStatus("命中项不在当前视图里（可先切回该分组）", true);
                return;
            }
            // 命中项可能在折叠的分组里 ⇒ 先走 `SelectNode(MItem)` 的既有职责（展开祖先链 +
            // 清旧选中），再走 `SelectNode(NodeRow)` 建字段面板（焦点护栏在它内部）。
            SelectNode(node.Item);
            SelectNode(node);
            RevealNode(node);
            RefreshPreview();
            ShowSearchCount();
            SetStatus("定位 " + (_searchIndex + 1) + "/" + total + "：" + _searchResults[_searchIndex].Title, false);
        }

        /// <summary>Esc：清空输入并回到搜索前的条目（不隐藏过任何节点 ⇒ 无需"还原树"）。</summary>
        private void SearchClear()
        {
            if (_searchBox != null) _searchBox.Text = "";   // 触发 TextChanged ⇒ ResetSearchHits
            var back = _searchOrigin;
            _searchOrigin = null;
            if (back != null)
            {
                var node = FindNode(CurrentRoots(), back);
                if (node != null) { SelectNode(node); RevealNode(node); RefreshPreview(); }
            }
            ShowSearchCount();
            if (_searchBox != null) _searchBox.Focus();
        }

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            switch (e.Key)
            {
                case Key.Enter:
                case Key.F3:
                case Key.Down:
                    SearchJump(shift ? -1 : 1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    SearchJump(-1);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    SearchClear();
                    e.Handled = true;
                    break;
            }
        }

        // ============================================================
        // ★ B2 入口③：键盘（PreviewKeyDown = 隧道事件，先于 TreeView 自己的按键处理）
        //   权威依据：PreviewKeyDown 路由策略 = Tunneling，对应冒泡事件 KeyDown，
        //   两者共用同一份事件数据 ⇒ 这里 Handled=true 后 TreeView 的默认行为（Tab 跳焦点、
        //   ↑↓ 折叠/移动选中）不会再跑。
        //   https://learn.microsoft.com/zh-cn/dotnet/api/system.windows.uielement.previewkeydown
        //   Alt 组合键：WPF 把 e.Key 置为 Key.System，真实键在 e.SystemKey
        //   https://learn.microsoft.com/zh-cn/dotnet/desktop/wpf/advanced/input-overview
        // ============================================================

        private void ItemTree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;

            if (ctrl && e.Key == Key.F)
            {
                if (_searchBox != null) { _searchBox.Focus(); _searchBox.SelectAll(); }
                e.Handled = true;
                return;
            }

            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            var a = KeyToAction(key, Keyboard.Modifiers, IsFieldEditorFocused());
            if (!a.HasValue) return;

            e.Handled = true;      // 先吃掉，再执行（执行里会重建树，事件源可能已失效）
            DoTreeAction(a.Value);
        }

        /// <summary>
        /// 键盘 → 动作的**唯一映射**（纯函数 ⇒ 自检可无人值守断言，不必造 WPF 事件参数）。
        /// <paramref name="editorFocused"/> = 焦点在字段编辑框里 ⇒ **一律返回 null**：
        /// 否则用户在标题框里按 Delete 会把整条删掉（"打字删条目"）。
        /// </summary>
        internal static TreeAction? KeyToAction(Key key, ModifierKeys mods, bool editorFocused)
        {
            if (editorFocused) return null;

            bool ctrl = (mods & ModifierKeys.Control) == ModifierKeys.Control;
            bool alt = (mods & ModifierKeys.Alt) == ModifierKeys.Alt;
            bool shift = (mods & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (ctrl) return null;     // Ctrl+X 之类留给系统 / 其它功能（Ctrl+F 在调用方）

            switch (key)
            {
                case Key.Delete: return TreeAction.Delete;
                case Key.F2: return TreeAction.Rename;
                case Key.Insert: return TreeAction.AddChild;
                case Key.Up: return alt ? TreeAction.MoveUp : (TreeAction?)null;
                case Key.Down: return alt ? TreeAction.MoveDown : (TreeAction?)null;
                case Key.Tab: return shift ? TreeAction.Outdent : TreeAction.Indent;
            }
            return null;
        }

        /// <summary>
        /// 焦点是否落在**字段编辑框**（TextBox / ComboBox 及其内部子元素）里。
        /// 说明：焦点在右侧字段区时，按键的隧道路由本来就不经过 `_itemTree`（树不是祖先），
        /// 这一层是**双保险** —— 防止以后把字段面板挪进树里、或树里再嵌编辑框时静默回归。
        /// </summary>
        private static bool IsFieldEditorFocused()
        {
            var d = Keyboard.FocusedElement as DependencyObject;
            while (d != null)
            {
                if (d is TextBox || d is ComboBox) return true;

                DependencyObject parent = null;
                var visual = d as Visual;
                var v3d = d as System.Windows.Media.Media3D.Visual3D;
                if (visual != null || v3d != null)
                {
                    try { parent = VisualTreeHelper.GetParent(d); }
                    catch (InvalidOperationException) { parent = null; }
                }
                if (parent == null) parent = LogicalTreeHelper.GetParent(d);
                d = parent;
            }
            return false;
        }

        private void ItemTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var node = NodeAt(e.OriginalSource as DependencyObject);
            if (node == null) return;
            // 双击 = 改名入口（与 F2 / 右键「重命名」同一份实现，见 BeginRenameItem）
            SelectNode(node.Item);
            DoTreeAction(TreeAction.Rename);
            e.Handled = true;
        }

        // ---- 拖拽：三种落点（上缘=同层插前 / 下缘=同层插后 / 中间=成为子项）----

        private void ItemTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // ★ 命中展开箭头（Tag="toggle"）⇒ 切换折叠并吃掉事件，不改选中
            var el = e.OriginalSource as FrameworkElement;
            while (el != null)
            {
                if (el.Tag is string && (string)el.Tag == "toggle")
                {
                    var n0 = NodeAt(el);
                    if (n0 != null) { n0.IsExpanded = !n0.IsExpanded; e.Handled = true; }
                    return;
                }
                el = System.Windows.Media.VisualTreeHelper.GetParent(el) as FrameworkElement;
            }

            var node = NodeAt(e.OriginalSource as DependencyObject);
            _dragStart = e.GetPosition(null);
            _dragNode = node;

            // ItemsControl 没有 SelectedItemChanged ⇒ 选中必须在这里做
            // ★ C2：Ctrl / Shift 多选走 HandleNodeClick（与自检同一份实现）
            if (node != null) HandleNodeClick(node, Keyboard.Modifiers);
        }

        private void ItemTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var node = NodeAt(e.OriginalSource as DependencyObject);
            if (node == null) return;

            // ★ C2：右键点在**已多选**的条目上 ⇒ 保留这批选择（否则右键一下就把多选打散，
            //   "一次搬 N 条"永远走不到）。点未选中的条目 ⇒ 回到单选。
            if (_marked.Count > 1 && _marked.Contains(node.Item)) { SetPrimary(node); return; }
            SelectNode(node);
        }

        private void ItemTree_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _dragNode == null) return;
            Point p = e.GetPosition(null);
            if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;

            DragDrop.DoDragDrop(_itemTree, _dragNode, DragDropEffects.Move);
        }

        private void ItemTree_Drop(object sender, DragEventArgs e)
        {
            // 2026-11-07：先从资源管理器拖文件进来 —— 那是「新增条目」，不是内部移动。
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null && paths.Length > 0)
                {
                    DropFiles(paths, NodeAt(e.OriginalSource as DependencyObject));
                    e.Handled = true;
                    return;
                }
            }

            var src = e.Data == null ? null : e.Data.GetData(typeof(NodeRow)) as NodeRow;
            if (src == null) return;

            var container = ContainerAt(e.OriginalSource as DependencyObject);
            Point p = e.GetPosition(_itemTree);

            // 空白处 = 追加到根层末尾
            if (container == null)
            {
                ApplyDropCore(src, null, DropWhere.End);
                return;
            }

            ApplyDropCore(src, NodeAt(e.OriginalSource as DependencyObject), WhereFor(container, p));
        }

        /// <summary>
        /// 2026-11-07：从资源管理器拖文件/文件夹进树 ⇒ 批量新增条目。
        ///
        /// 口径（沿用 C1 契约「配置只存程序名 / 部分路径」）：
        ///   · .exe / .lnk ⇒ `run <文件名>`（**只存程序名**，换机不用重配路径）
        ///   · 文件夹 / 其他文件 ⇒ `open <完整路径>`
        /// 落点：拖到组头上成为它的子项；拖到条目上插在它后面；拖到空白追加到末尾。
        /// </summary>
        private void DropFiles(string[] paths, NodeRow anchor)
        {
            var g = SelectedGroup();
            if (g == null)
            {
                SetStatus("请先在左侧选一个分组，再拖入文件", true);
                return;
            }

            // anchor 已经是 NodeRow（ItemsControl 版）
            if (anchor != null && !ReferenceEquals(anchor.Item, null)) { /* anchor 有效 */ }
            else anchor = null;

            var made = new List<NodeRow>();
            int skipped = 0;

            foreach (var p in paths)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(p)) { skipped++; continue; }

                    bool isDir = System.IO.Directory.Exists(p);
                    bool isExe = !isDir && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                    bool isLnk = !isDir && p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

                    string title = System.IO.Path.GetFileNameWithoutExtension(p);
                    if (string.IsNullOrEmpty(title)) title = System.IO.Path.GetFileName(p);
                    if (string.IsNullOrEmpty(title)) { skipped++; continue; }

                    var it = new MItem { Title = title, Level = 0 };
                    // 快捷键：取标题首字符（小写），用户可再改
                    it.Hotkey = title.Substring(0, 1).ToLowerInvariant();

                    if (isExe)
                    {
                        // exe ⇒ run 程序名（只存名字，换机靠 EV 重新解析）
                        it.Type = ActionType.Run;
                        string exeName = System.IO.Path.GetFileName(p);
                        it.Target = exeName;

                        // 拖入时已知完整路径 ⇒ 立即写缓存，省掉一次 EV 搜索
                        try
                        {
                            if (_pathCache == null) _pathCache = new RunEverCs.Services.PathCache(_cachePath);
                            _pathCache.Set(exeName, p);
                            _pathCache.Save();
                        }
                        catch { }
                    }
                    else if (isDir)
                    {
                        // 文件夹只能靠路径打开（没有"程序名"可存）
                        it.Type = ActionType.Open;
                        it.Target = p;
                    }
                    else
                    {
                        // ★ 2026-11-07（用户口径）：**配置一律只存文件名**，完整路径进 PathCache。
                        //   执行时 DoOpen 先查缓存拼回路径，再按 Win+R 语义交给系统打开。
                        //   这样 menus.conf 全程不含绝对路径 ⇒ 换机/改盘符都不用重配。
                        string fname = System.IO.Path.GetFileName(p);
                        if (string.IsNullOrEmpty(fname)) fname = p;
                        it.Type = ActionType.Open;
                        it.Target = fname;

                        // 把「文件名 → 完整路径」写进路径缓存（执行期拼回用）
                        try
                        {
                            if (_pathCache == null) _pathCache = new RunEverCs.Services.PathCache(_cachePath);
                            _pathCache.Set(fname, p);
                            _pathCache.Save();
                        }
                        catch { }
                    }

                    // 诊断：记录系统给的原始路径与最终 target
                    try
                    {
                        System.IO.File.AppendAllText(
                            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(_confPath), "..", "state", "drop-debug.log"),
                            DateTime.Now.ToString("HH:mm:ss") + " DROP p=[" + p + "] type=" + it.Type
                            + " hotkey=[" + it.Hotkey + "] target=[" + it.Target + "]\n");
                    }
                    catch { }

                    made.Add(new NodeRow(this, it));
                }
                catch { skipped++; }
            }
            if (made.Count == 0)
            {
                SetStatus("拖入的内容无法识别为条目", true);
                return;
            }

            // ---- 插入到树 ----
            // ★ P0-1：`CurrentRoots()` 每次返回**新 List 拷贝**。插入与赋给 ItemsSource 必须
            //   是**同一个变量**；否则节点插进了拷贝 A、视图读的是拷贝 B、Flatten 再读拷贝 C
            //   ⇒ 新条目既不在视图也不在 `g.Items`，界面却提示"已新增 N 个"（今天的 bug）。
            if (anchor == null || anchor.Item.Type == ActionType.Group)
            {
                if (anchor != null)
                {
                    // 拖到组头 ⇒ 作为它的子项（与 AddChildCore 的组头分支一致）
                    foreach (var node in made)
                    {
                        node.Parent = anchor;
                        anchor.Children.Add(node);   // Children 是活集合，刷新交给 RefreshGrid
                    }
                    anchor.IsExpanded = true;
                }
                else
                {
                    var roots = CurrentRoots();
                    foreach (var node in made) { node.Parent = null; roots.Add(node); }
                    _itemTree.ItemsSource = roots;   // 与上面插入的是同一个 list
                }
            }
            else
            {
                // 拖到条目上 ⇒ 依次插在它后面
                var parent = anchor.Parent;
                var list = parent != null ? parent.Children : CurrentRoots();
                int idx = list.IndexOf(anchor);          // 同一个 list 上取下标
                for (int i = 0; i < made.Count; i++)
                {
                    made[i].Parent = parent;
                    list.Insert(idx + 1 + i, made[i]);
                }
                if (parent == null) _itemTree.ItemsSource = list;   // 同上：同一个 list
            }

            // ★ 收尾顺序与 FinishDrop 完全一致，**不许调换**：
            //   Flatten 把树写回 `g.Items` → RefreshGrid 按它重建**全部** NodeRow。
            //   重建后 `made` 里的 NodeRow 引用已作废，所以必须按 `MItem` 重新 FindNode。
            Flatten();
            RefreshGrid();
            var last = made[made.Count - 1];
            SelectNode(FindNode(CurrentRoots(), last.Item));   // 选中最后一条（会建字段面板）
            RevealNode(_selNode);                              // 新条目可能在视口外
            RefreshPreview();
            MarkDirty();
            SetStatus(string.Format("已从拖入内容新增 {0} 个条目，已选中最后一条「{1}」{2}",
                made.Count, last.Item.Title,
                skipped > 0 ? "（跳过 " + skipped + " 个）" : ""), false);
        }

        /// <summary>
        /// 拖放落点的**唯一实现**：`End` / `Before` / `After` / `Child`。
        /// ★ 与命中测试分离 ⇒ `--selftest-menueditor` 能无人值守驱动同一份逻辑。
        /// </summary>
        internal void ApplyDropCore(NodeRow src, NodeRow target, DropWhere where)
        {
            if (src == null) return;

            if (target == null || where == DropWhere.End)
            {
                Detach(src);
                src.Parent = null;
                var all = CurrentRoots();
                all.Add(src);
                _itemTree.ItemsSource = all;
                FinishDrop(src, "已移到最外层末尾");
                return;
            }

            if (ReferenceEquals(target, src)) return;
            if (IsInSubtree(target, src))
            {
                SetStatus("拖拽被拒绝：不能把节点拖进它自己的子树里", true);
                return;
            }

            if (where == DropWhere.Child)
            {
                Detach(src);
                src.Parent = target;
                target.Children.Add(src);
                target.IsExpanded = true;
                FinishDrop(src, "已成为「" + target.Item.Title + "」的子项");
                return;
            }

            bool before = where == DropWhere.Before;
            Detach(src);
            src.Parent = target.Parent;
            if (target.Parent != null)
            {
                int idx = target.Parent.Children.IndexOf(target);
                target.Parent.Children.Insert(before ? idx : idx + 1, src);
            }
            else
            {
                var all = CurrentRoots();
                int idx = all.IndexOf(target);
                all.Insert(before ? idx : idx + 1, src);
                _itemTree.ItemsSource = all;
            }
            FinishDrop(src, "已插到「" + target.Item.Title + "」"
                + (before ? "之前" : "之后") + "（同层）");
        }

        /// <summary>拖放收尾：把树回写进 `g.Items` 后走**同一条**结构操作收尾（AfterStructOp）。</summary>
        private void FinishDrop(NodeRow src, string msg)
        {
            Flatten();
            AfterStructOp(src.Item, msg);
        }

        internal enum DropWhere { Before, After, Child, End }

        /// <summary>落点判定：节点行高上 30% = 插前，下 30% = 插后，中间 40% = 成为子项。</summary>
        private DropWhere WhereFor(FrameworkElement container, Point pInTree)
        {
            double h = container.ActualHeight;
            if (h <= 1) return DropWhere.Child;
            double top = container.TranslatePoint(new Point(0, 0), _itemTree).Y;
            double rel = pInTree.Y - top;
            if (rel < h * 0.3) return DropWhere.Before;
            if (rel > h * 0.7) return DropWhere.After;
            return DropWhere.Child;
        }

        /// <summary>命中的容器（null = 空白处）。ItemsControl 的容器是 ContentPresenter。</summary>
        private TreeViewItem ContainerAt(DependencyObject d)
        {
            if (d == null) return null;
            return ItemsControl.ContainerFromElement(_itemTree, d) as TreeViewItem;
        }

        /// <summary>命中的树节点（null = 空白处）。</summary>
        private NodeRow NodeAt(DependencyObject d)
        {
            var c = ContainerAt(d);
            if (c == null) return null;
            var n = c.Header as NodeRow;
            return n ?? (c.DataContext as NodeRow);
        }

    }
}
