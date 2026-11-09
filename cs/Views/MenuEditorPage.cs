using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RunEverCs.Models;
using RunEverCs.Services;

// ★ 消歧：WPF 也有 System.Windows.Controls.MenuItem，与我们的模型同名
using MItem = RunEverCs.Models.MenuItem;

namespace RunEverCs.Views
{
    /// <summary>
    /// 菜单项编辑页 —— 分组列表 × **树形条目编辑** × 字段编辑 × 实时预览。
    ///
    /// 布局（master-detail，参考微软 UWP UX 指南的"列表+详情"模式）：
    ///   ┌─ 左：分组列表 (235px) ─┬─ 中：条目【树形】 ──────────────────────┐
    ///   │ 分组名  分组依据        │ ▾ 通用菜单                             │
    ///   │ ...                    │   ▾ 系统        _E run explorer.exe …  │
    ///   │                        │      资源管理器                          │
    ///   │                        │      任务管理器                          │
    ///   │ [＋][－][↑][↓]         │ [＋][－][↑][↓][⇥][⇤]                  │
    ///   └────────────────────────┴───────────────────────────────────────────┘
    ///   下方：选中条目的字段编辑区 + 实时预览
    ///
    /// ★ v9（2026-10-06 用户要求）：中栏由**缩进 DataGrid** 改成**真 TreeView**（10-05 的
    ///   缩进列表只是"看起来像树"，没有真实父子结构）。现在的模型分工：
    ///     · **树 = 层级的唯一源**：每个节点 = 一个 `MItem`，父子关系 = 菜单层级
    ///     · 结构操作（增/删/上移/下移/缩进/拖拽）**全在树上做**，
    ///       做完调 <see cref="Flatten"/> 按 DFS 回写到 `g.Items`（`Level = 深度`）
    ///     · 🔴 **强制不变式**（见 <see cref="MenuTreeOps.LevelsWellFormed"/>）：**父项之后必须紧跟其
    ///       全部子项** + **禁止跳级** —— 由"树结构 + DFS 回写"天然保证；回写后再断言校验一次
    ///     · 折叠 = `TreeViewItem.IsExpanded`（纯视图态，按 MItem 记账，不写进配置）
    ///
    /// ★ C2（条目跨分组移动 / 复制）：**结构操作不再局限于当前分组** ——
    ///   右键「移动到分组… / 复制到分组…」把选中项（含整棵子树）搬到**任意其它分组**，
    ///   落到目标后按该组最外层 Level 重算缩进；复制生成新对象（新身份 / 新 ID）。
    ///   多选（Ctrl / Shift）一次搬多条；每次搬完立刻跑 C1 的
    ///   <see cref="MenuStructureValidator"/>，多出结构问题就**整体回滚**（不写盘）。
    ///   唯一实现在 <see cref="MoveSelectionToGroup"/>，右键 / 自检共用。
    /// </summary>
    public partial class MenuEditorPage : UserControl
    {
        private readonly string _confPath;
        private readonly string _cachePath;    // cache\paths.txt（文件名框的候选来源）
        private readonly MenuConfWriter _writer;
        private List<MenuGroup> _groups = new List<MenuGroup>();

        /// <summary>
        /// 保存成功后的回调（v8 AC-5②）：托盘进程注入"配置保存后增量解析"。
        /// null = 独立进程打开设置（无运行实例）⇒ 不回调。
        /// </summary>
        public Action AfterSave;

        private ListBox _groupList;
        private TreeView _itemTree;            // ★ v9：中栏 = 真 TreeView
        private StackPanel _fieldPanel;
        private TextBox _previewBox;
        private TextBlock _statusText;

        /// <summary>缓存实例（拖拽写回「文件名→完整路径」用；执行期 Executor 读同一份文件）。</summary>
        private RunEverCs.Services.PathCache _pathCache;

        private bool _dirty;

        /// <summary>
        /// ★ B4a-1：**配置是否真的读进来了**。`Reload()` 进来先置 false，`Parse()` 成功才置 true。
        /// 为什么需要它：解析失败时 `_groups` 为空，而「保存」按钮原来没有联动 ⇒
        /// 点一下就把 menus.conf 写成「0 组 0 条」（一键清空菜单，P0 数据事故）。
        /// 现在是三道闸：① 按钮 `IsEnabled` 跟随本标志 ② `OnSave` 开头二次防御
        /// ③ `MenuConfWriter.Save` 的兜底硬闸（兜住所有调用方）。
        /// </summary>
        private bool _loadOk;

        /// <summary>「保存」按钮（B4a-1：`IsEnabled` 跟随 <see cref="_loadOk"/>）。</summary>
        private Button _saveBtn;

        // ★ 折叠状态（**纯视图态**，不写进配置；按 MItem 记账 ⇒ 重建后不丢）+ 拖拽起手
        private readonly HashSet<MItem> _collapsed = new HashSet<MItem>();
        private Point _dragStart;
        private NodeRow _dragNode;
        private MItem _selectedItem;

        // ★ C2：多选（Ctrl / Shift）。**按 MItem 记账**（树重建后仍能对上）。
        //   为什么不复用 IsSelected：TreeView 是单选控件，容器选中态会被它强制互斥 ⇒
        //   多选高亮改用独立的 <see cref="NodeRow.IsMarked"/>（视觉上浅蓝，与"当前编辑"的浅绿区分）。
        private readonly HashSet<MItem> _marked = new HashSet<MItem>();

        /// <summary>★ C2：Shift 连选的锚点（最后一次普通单击 / Ctrl 单击的条目）。</summary>
        private MItem _anchor;

        /// <summary>
        /// ★ C2 自检缝：目标分组选择器的**替身**（null = 走真实弹窗）。
        /// 自检要验「右键菜单 → 选目标 → 搬」整条链路，但无窗口环境不能弹模态框。
        /// 产品路径恒为 null ⇒ 行为不受影响。
        /// </summary>
        private Func<bool, int> _pickGroupOverride;

        /// <summary>
        /// ★ v9：选中节点的**权威记录** —— 不依赖 `TreeView.SelectedItem`。
        /// 原因：无窗口/未布局时 TreeView 的容器尚未生成，`SelectedItem` 会滞后或为 null，
        /// 结构操作（⇥/⇤/↑↓/删除）就全部失效（自检实测踩到）。这里自持一份，随选择同步。
        /// </summary>
        private NodeRow _selNode;
        private TextBox _titleBox;      // 字段编辑区的标题框（双击定位用）
        private ComboBox _typeCombo;    // 字段编辑区的「动作类型」下拉（L5：中文显示 / 英文 Tag）
        private TextBlock _midHead;     // 条目区标题（显示当前分组名）

        // ★ 2026-11-08 定位式搜索（S1）：**不过滤**，只把命中的节点选中并滚进视口。
        //   为什么不做过滤：树的数据源是 `CurrentRoots()`（= 视图），过滤后重建视图会
        //   把"当前没显示"的条目从视图里摘掉，Flatten 再回写就真丢了（数据事故）。
        private TextBox _searchBox;         // 搜索输入框（命名契约：_searchBox）
        private TextBlock _searchHits;      // 命中计数「k/N」
        private List<MItem> _searchResults = new List<MItem>();
        private int _searchIndex = -1;
        private MItem _searchOrigin;        // Esc 清空后要回到的条目

        // ★ 2026-10-04 修复「编辑框输入一个字符就丢焦点」：
        //   RefreshGrid() 会 Clear + 重建行并还原 SelectedIndex ⇒ 触发 SelectionChanged
        //   ⇒ BuildFieldPanel() 里 _fieldPanel.Children.Clear() 把**正在输入的那个 TextBox**
        //   从可视树摘掉 ⇒ 键盘焦点丢失（每敲一个字符掉一次）。
        //   修法：RefreshGrid 重建期间置本标志，期间的选择变更只同步 _selectedItem、不重建面板；
        //   另外选中项没换（同一 MItem 实例）也一律不重建。
        private bool _suppressFieldRebuild;

        /// <summary>字段面板当前正在展示的条目（判「选中项是否真的换了」用，见 _suppressFieldRebuild）。</summary>
        private MItem _lastPanelItem;

        /// <summary>本分组"最外层"的 Level 基准（menus.conf 惯例 = 1）；Flatten 回写时统一加它。</summary>
        private int _baseLevel;

        /// <summary>条目树的右键菜单（B2：项由 <see cref="TreeAction"/> 生成，点击只调 <see cref="DoTreeAction"/>）。</summary>
        private ContextMenu _contextMenu;

        /// <summary>
        /// ★ B2：条目树的**结构动作**。按钮 / 右键菜单 / 键盘三个入口共用这一个枚举，
        /// 三者都只调 <see cref="DoTreeAction"/>，任何一处都不许自己再实现一遍结构操作。
        /// 声明顺序 = 右键菜单的显示顺序（<see cref="BuildTreeContextMenu"/> 按声明顺序生成）。
        /// </summary>
        internal enum TreeAction
        {
            AddChild,
            Rename,
            Delete,
            MoveUp,
            MoveDown,
            Indent,
            Outdent,
            Duplicate,
            MoveToGroup,
            CopyToGroup
        }

        public MenuEditorPage(string root)
        {
            _confPath = System.IO.Path.Combine(root, "config", "menus.conf");
            _cachePath = System.IO.Path.Combine(root, "cache", "paths.txt");
            _writer = new MenuConfWriter(_confPath);
            Build();
            Reload();
        }

        // ============================================================
        private void Build()
        {
            var outer = new Grid();
            // 2026-11-07 重排后只剩一行（原先第二行放"字段编辑 + 预览"横条）
            outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var main = new Grid { ClipToBounds = true };
            // ★ 2026-11-07：分组列 235→260（`proc: pwsh powershell cmd windowsterminal` 这类
            //   长依据原来被切掉），并给三列都设 MinWidth + 加 GridSplitter 支持手动拖拽。
            main.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(260),
                MinWidth = 190
            });
            // ★ Star 列必须设 MinWidth 且允许压缩：条目树里的文本很长
            //   （"LockHunter64 _L · run · LockHunter64.exe"），WPF 的 Star 列会按
            //   内容 DesiredWidth 撑开 ⇒ 第三列被挤出窗口右边界（截图实测）。
            main.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star),
                MinWidth = 180
            });
            // 2026-11-07 重排：字段编辑 + 预览从「下方横条」挪到**右侧第三栏**。
            // 业界同类（Servoy Menu Editor / NOMANA tree editor）都是「左树 + 右属性」——
            // 选中条目时眼睛不用往下跳，垂直空间也不再被切成两半。
            main.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(360),
                MinWidth = 280
            });
            Grid.SetRow(main, 0);

            // ---- 左：分组 ----
            var left = new DockPanel { Margin = new Thickness(8) };

            var leftHead = new TextBlock
            {
                Text = "分组",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(2, 0, 0, 6)
            };
            DockPanel.SetDock(leftHead, Dock.Top);
            left.Children.Add(leftHead);

            var leftBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };
            DockPanel.SetDock(leftBtns, Dock.Bottom);
            leftBtns.Children.Add(MkBtn("＋", "新建分组", OnAddGroup));
            // ★ 2026-11-07：分组编辑原来**只有双击分组卡片**这一个入口，
            //   用户完全发现不了（反馈「分组的依据本身不可调」）。
            //   补一个显式按钮，双击仍保留。
            leftBtns.Children.Add(MkBtn("✎", "编辑选中分组（名称 / 分组依据 / 关键词）",
                (s, e) => RenameGroup()));
            leftBtns.Children.Add(MkBtn("－", "删除分组", OnDelGroup));
            leftBtns.Children.Add(MkBtn("↑", "上移分组", (s, e) => MoveGroup(-1)));
            leftBtns.Children.Add(MkBtn("↓", "下移分组", (s, e) => MoveGroup(1)));
            left.Children.Add(leftBtns);

            _groupList = new ListBox
            {
                Margin = new Thickness(0, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };

            // ★ 2026-11-07 卡片化：分组从「纯文本两行」改成**卡片**
            //   （白底 + 圆角 + 细分隔线，选中态由容器样式的左侧绿条 + 浅绿底表达）。
            //   与设置页 SettingsKit 的卡片语言保持一致。
            // ★ B1-2：横向滚动必须关掉 —— 默认 Auto 会用**无限宽**去测量 item，
            //   于是 `TextWrapping=Wrap` 永远不换行，只会多出一条横向滚动条。
            ScrollViewer.SetHorizontalScrollBarVisibility(_groupList, ScrollBarVisibility.Disabled);
            _groupList.ItemTemplate = BuildGroupCardTemplate();
            _groupList.ItemContainerStyle = BuildGroupItemStyle();
            _groupList.ToolTip = "双击可编辑分组（名称 / 分组依据 / 关键词）";

            _groupList.SelectionChanged += (s, e) => { FillItems(); };
            _groupList.MouseDoubleClick += (s, e) => RenameGroup();
            left.Children.Add(_groupList);

            Grid.SetColumn(left, 0);
            main.Children.Add(left);

            // ---- 中：条目 ----
            var mid = new DockPanel { Margin = new Thickness(8) };

            // ★ 标题显示【当前分组名 + 分组依据】（用户反馈"条目编辑 分组名没显示"）
            _midHead = new TextBlock
            {
                Text = "条目",
                FontWeight = FontWeights.Bold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 0, 6)
            };
            DockPanel.SetDock(_midHead, Dock.Top);
            mid.Children.Add(_midHead);

            // ---- 中栏搜索条（定位式：不隐藏任何节点 ⇒ 清空后天然就是完整树）----
            // ★ 必须插在 _midHead 之后、_itemTree 之前：DockPanel 的 LastChildFill 要求
            //   `_itemTree` 始终是最后一个子元素，否则树会被挤成 Auto 高度。
            var searchBar = new DockPanel { Margin = new Thickness(2, 0, 2, 6) };
            DockPanel.SetDock(searchBar, Dock.Top);

            // Dock=Right 的元素**先加的靠最右**：计数 → ↓ → ↑
            _searchHits = new TextBlock
            {
                Text = "",
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                MinWidth = 44,
                TextAlignment = TextAlignment.Right
            };
            DockPanel.SetDock(_searchHits, Dock.Right);
            searchBar.Children.Add(_searchHits);

            var searchDown = MkBtn("▾", "下一个命中（Enter / F3）", (s, e) => SearchJump(1));
            searchDown.Margin = new Thickness(4, 0, 2, 0);
            DockPanel.SetDock(searchDown, Dock.Right);
            searchBar.Children.Add(searchDown);

            var searchUp = MkBtn("▴", "上一个命中（Shift+Enter / ↑）", (s, e) => SearchJump(-1));
            searchUp.Margin = new Thickness(4, 0, 2, 0);
            DockPanel.SetDock(searchUp, Dock.Right);
            searchBar.Children.Add(searchUp);

            _searchBox = new TextBox
            {
                Padding = new Thickness(4, 2, 4, 2),
                ToolTip = "定位（不过滤）：匹配标题 / 快捷键 / 目标。\n"
                    + "Enter 或 F3 = 下一个，Shift+Enter 或 ↑ = 上一个，Esc = 清空。",
                VerticalContentAlignment = VerticalAlignment.Center
            };
            _searchBox.TextChanged += (s, e) => ResetSearchHits();
            _searchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;
            searchBar.Children.Add(_searchBox);   // 最后一个 ⇒ 填满剩余宽度

            mid.Children.Add(searchBar);

            var midBtns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 6, 0, 0)
            };
            DockPanel.SetDock(midBtns, Dock.Bottom);
            // ★ B2：六个按钮**保留**（用户明确要求），但只调 DoTreeAction —— 不再自己实现结构操作。
            midBtns.Children.Add(MkBtn("＋ 条目", "新建子项（Insert）：组头下 ⇒ 成为它的子项；普通条目 ⇒ 插在它后面",
                (s, e) => DoTreeAction(TreeAction.AddChild)));
            midBtns.Children.Add(MkBtn("－", "删除选中条目（Delete）", (s, e) => DoTreeAction(TreeAction.Delete)));
            midBtns.Children.Add(MkBtn("↑", "上移（Alt+↑）", (s, e) => DoTreeAction(TreeAction.MoveUp)));
            midBtns.Children.Add(MkBtn("↓", "下移（Alt+↓）", (s, e) => DoTreeAction(TreeAction.MoveDown)));
            midBtns.Children.Add(MkBtn("⇥", "增加缩进（Tab，成为上一条的子项）",
                (s, e) => DoTreeAction(TreeAction.Indent)));
            midBtns.Children.Add(MkBtn("⇤", "减少缩进（Shift+Tab）", (s, e) => DoTreeAction(TreeAction.Outdent)));
            mid.Children.Add(midBtns);

            // ★ 2026-11-07：中栏从 TreeView 改为**递归 ItemsControl**。
            //
            // 为什么换：WPF 原生 TreeView 只能靠左移 19px 表达层级、没有连线，
            //   用户反馈「树形难看 / 层级不明显」。换框架代价太大（HandyControl 要推倒
            //   整套 SettingsKit），而递归 ItemsControl 完全可控：每层卡片自带缩进、
            //   层级连线、收缩箭头，且视觉语言与设置页卡片一致。
            //
            // 代价：ItemsControl 没有选中/展开概念 ⇒ 状态由 NodeRow.IsSelected / IsExpanded
            //   承载，任何变化后**重建树**（几十个节点，成本可忽略）。
            _itemTree = new TreeView
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.LightGray,
                AllowDrop = true,
                ToolTip = "层级 = 菜单层级（父项下的子项就是它的子菜单）。\n"
                    + "拖动：放到卡片上 = 成为它的子项；放到卡片的上/下缘 = 同层插到前/后。\n"
                    + "⇥ = 成为上一条的子项，⇤ = 升到上一级。双击 = 跳到下方字段编辑。"
            };
            _itemTree.ItemTemplate = BuildNodeTemplate();
            // ★ 2026-11-07 树形可见性：容器样式（回退时丢了，这次一并补回）。
            //   · 展开/选中仍双向绑到 NodeRow（TreeView 原生能力）
            //   · 每个 TreeViewItem 左边一条 1px 竖线 ⇒ 层级随缩进自然形成引导线
            //   · 卡片横向拉伸 + 选中浅绿
            _itemTree.ItemContainerStyle = BuildTreeItemStyle();

            _itemTree.SelectedItemChanged += ItemTree_SelectedItemChanged;
            _itemTree.PreviewKeyDown += ItemTree_PreviewKeyDown;
            _itemTree.PreviewMouseLeftButtonDown += ItemTree_PreviewMouseLeftButtonDown;
            _itemTree.PreviewMouseRightButtonDown += ItemTree_PreviewMouseRightButtonDown;
            _itemTree.MouseDoubleClick += ItemTree_MouseDoubleClick;
            _itemTree.MouseMove += ItemTree_MouseMove;
            // ★ B2：右键菜单只做「生成项 + 转发动作」。选中被点节点仍在
            //   PreviewMouseRightButtonDown 里（右键先选中 = 既有行为，保留）。
            //   权威依据（WPF 路由）：ContextMenu 是元素属性，右键由 WPF 自动打开；
            //   MenuItem.Click 是普通路由事件 ⇒ item 自己点自己即可，不需要容器。
            _contextMenu = BuildTreeContextMenu();
            _itemTree.ContextMenu = _contextMenu;
            _itemTree.DragOver += (s, e) =>
            {
                // 2026-11-07：区分两种拖拽 —— 从资源管理器拖文件进来是「复制」，
                // 树内拖动条目是「移动」。不区分会让光标一直显示 ✚/禁止，用户以为不支持。
                bool files = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
                e.Effects = files ? DragDropEffects.Copy : DragDropEffects.Move;
                e.Handled = true;
            };
            _itemTree.Drop += ItemTree_Drop;

            mid.Children.Add(_itemTree);

            Grid.SetColumn(mid, 1);
            main.Children.Add(mid);

            outer.Children.Add(main);

            // ---- 下：字段编辑 + 预览 ----
            // ---- 右：属性（字段编辑 + 语法预览，纵向堆叠）----
            var rightPanel = new DockPanel { Margin = new Thickness(8, 8, 8, 8) };

            var fieldBox = new GroupBox { Header = "字段编辑", Padding = new Thickness(8) };
            _fieldPanel = new StackPanel();
            fieldBox.Content = new ScrollViewer
            {
                Content = _fieldPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                // ★ 2026-11-07：必须禁横向滚动。默认 Auto 时 ScrollViewer 的
                //   DesiredWidth = 内容自然宽度 ⇒ 会把三栏里的第三列撑爆，
                //   字段编辑区整个被挤出窗口右边界（截图实测）。
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            DockPanel.SetDock(fieldBox, Dock.Top);
            rightPanel.Children.Add(fieldBox);

            var previewBox = new GroupBox
            {
                Header = "配置语法预览（实时）",
                Padding = new Thickness(8),
                Margin = new Thickness(0, 10, 0, 0)
            };
            _previewBox = new TextBox
            {
                IsReadOnly = true,              // 保留：能选中、能 Ctrl+C
                BorderThickness = new Thickness(0),   // ★ A2：去掉输入框外观（只读文本块的样子）
                Padding = new Thickness(8, 6, 8, 6),
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 250))
            };
            previewBox.Content = new ScrollViewer
            {
                Content = _previewBox,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            rightPanel.Children.Add(previewBox);

            rightPanel.ClipToBounds = true;
            // ---- 列宽拖拽（2026-11-07）：两处 5px 热区，拖了改相邻列宽 ----
            var split1 = new GridSplitter
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.SizeWE,
                // ★ 2026-11-07：列 0 右缘的对齐是 Right ⇒ 必须 CurrentAndNext。
                //   设 PreviousAndNext 时「前面没有列」，WPF 静默不响应（不报错也不动）。
                ResizeBehavior = GridResizeBehavior.CurrentAndNext,
                ResizeDirection = GridResizeDirection.Columns,
                ShowsPreview = false
            };
            Grid.SetColumn(split1, 0);
            Panel.SetZIndex(split1, 100);   // ★ 必须压住左栏 ListBox，否则拖不到
            main.Children.Add(split1);

            var split2 = new GridSplitter
            {
                Width = 5,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.SizeWE,
                // 列 2 左缘的对齐是 Left ⇒ PreviousAndCurrent（上一列 + 自己）
                ResizeBehavior = GridResizeBehavior.PreviousAndCurrent,
                ResizeDirection = GridResizeDirection.Columns,
                ShowsPreview = false
            };
            Grid.SetColumn(split2, 2);
            Panel.SetZIndex(split2, 100);
            main.Children.Add(split2);

            Grid.SetColumn(rightPanel, 2);
            main.Children.Add(rightPanel);

            // ---- 状态栏 ----
            var statusBar = new DockPanel
            {
                Margin = new Thickness(8, 0, 8, 8),
                LastChildFill = true
            };
            _statusText = new TextBlock
            {
                Text = "就绪",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray
            };
            _saveBtn = new Button
            {
                Content = "保存到 menus.conf",
                Width = 150,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(8, 0, 0, 0),
                IsEnabled = false      // ★ B4a-1：初始禁用，等 Reload() 解析成功才放开
            };
            // ★ 禁用态默认不弹 ToolTip —— 这里必须打开，否则用户看到灰按钮却不知道为什么
            //   （MS Learn：ToolTipService.ShowOnDisabled 附加属性）
            ToolTipService.SetShowOnDisabled(_saveBtn, true);
            _saveBtn.Click += OnSave;
            DockPanel.SetDock(_saveBtn, Dock.Right);
            statusBar.Children.Add(_saveBtn);

            var reloadBtn = new Button
            {
                Content = "放弃修改",
                Width = 100,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            // ★ B4a-1：加载失败时 Reload() 已经写了红字原因，这里不能再盖成「已重新加载」
            reloadBtn.Click += (s, e) => { Reload(); if (_loadOk) SetStatus("已重新加载", false); };
            DockPanel.SetDock(reloadBtn, Dock.Right);
            statusBar.Children.Add(reloadBtn);

            statusBar.Children.Add(_statusText);

            var root = new DockPanel();
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(statusBar);
            root.Children.Add(outer);

            Content = root;
        }

        /// <summary>
        /// 把 WPF 按键映射成 menus.conf 的单字符 hotkey。
        /// 返回 null = 该键不参与（修饰键 / 无对应字符）。
        /// </summary>
        /// <summary>
        /// 工具栏小按钮（2026-11-07 统一视觉）：白底圆角 6 + 细边框 + hover 变浅灰。
        /// 之前是裸 Button（系统默认直角灰底），和卡片语言不统一。
        /// </summary>
        private Button MkBtn(string text, string tip, RoutedEventHandler h)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 30,
                Height = 26,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = tip,
                Background = Brushes.White,
                BorderBrush = SettingsKit.CardBorder,
                BorderThickness = new Thickness(1),
                Foreground = SettingsKit.TextPrimary,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var tpl = new ControlTemplate(typeof(Button));
            var bd = new FrameworkElementFactory(typeof(Border));
            bd.Name = "Bd";
            bd.SetValue(Border.BackgroundProperty, new System.Windows.TemplateBindingExtension(Button.BackgroundProperty));
            bd.SetValue(Border.BorderBrushProperty, new System.Windows.TemplateBindingExtension(Button.BorderBrushProperty));
            bd.SetValue(Border.BorderThicknessProperty, new System.Windows.TemplateBindingExtension(Button.BorderThicknessProperty));
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            bd.SetValue(Border.PaddingProperty, new System.Windows.TemplateBindingExtension(Button.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            bd.AppendChild(cp);
            tpl.VisualTree = bd;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)), "Bd"));
            tpl.Triggers.Add(hover);

            b.Template = tpl;
            b.Click += h;
            return b;
        }

        // ============================================================
        // 数据加载
        // ============================================================
        public void Reload()
        {
            Reload(-1, null);
        }

        /// <summary>
        /// 重载配置。
        /// ★ B4b-3：`keepGroupIndex` / `keepItemTitle` 用于保存后的"原地重载"——
        ///   以前保存成功一律重载并选第 0 组第 0 条，正在编辑的第 30 条会跳走，
        ///   用户得重新找一遍（保存动作把上下文弄丢了，纯属反人类）。
        /// </summary>
        public void Reload(int keepGroupIndex, string keepItemTitle)
        {
            // ★ B4a-1：**进来先假定没读进来** —— 只有 Parse() 真的成功才允许保存。
            //   注意：失败时**故意不清 _groups**（不清空内存模型，避免一次瞬时读盘错误
            //   把用户正在编的东西从视图里抹掉）。保存已被禁用 ⇒ 留着也不会写盘。
            _loadOk = false;
            UpdateSaveEnabled();
            try
            {
                var p = new ConfParser(_confPath);
                p.Parse();
                _groups = p.Groups;
                _loadOk = true;
                _dirty = false;
                FillGroups(keepGroupIndex, keepItemTitle);
                SetStatus("已加载 " + _groups.Count + " 个分组", false);
            }
            catch (Exception ex)
            {
                SetStatus("加载失败：" + ex.Message
                    + " —— 配置没读进来，已禁用「保存」（防止把 menus.conf 写成空白）。"
                    + "先修好 config\\menus.conf（或从 menus.conf.bak.* 恢复），再点「放弃修改」重试。", true);
            }
            finally
            {
                UpdateSaveEnabled();
            }
        }

        /// <summary>
        /// ★ B4a-1：「保存」按钮的可用性**唯一同步点** —— 跟着 `_loadOk` 走。
        /// 禁用时给 ToolTip 说明原因（配合 `ToolTipService.ShowOnDisabled`）。
        /// </summary>
        private void UpdateSaveEnabled()
        {
            if (_saveBtn == null) return;
            _saveBtn.IsEnabled = _loadOk;
            _saveBtn.ToolTip = _loadOk
                ? null
                : "配置没读进来，已禁用保存（否则会把 menus.conf 写成空白）。"
                  + "先修好 config\\menus.conf，再点「放弃修改」重试。";
        }

        private void FillGroups()
        {
            FillGroups(-1, null);
        }

        private void FillGroups(int keepGroupIndex, string keepItemTitle)
        {
            _groupList.Items.Clear();
            foreach (var g in _groups)
                _groupList.Items.Add(new GroupRow(g));
            if (_groupList.Items.Count == 0)
            {
                FillItems();
                return;
            }

            int idx = (keepGroupIndex >= 0 && keepGroupIndex < _groupList.Items.Count)
                ? keepGroupIndex : 0;
            _groupList.SelectedIndex = idx;

            // 分组选好后条目树已重建（SelectionChanged → FillItems → SelectNode(第一根)）；
            // 这里再按标题回到原来那条。标题在同组内重复极少，找不到就停在第一条（不报错）。
            if (!string.IsNullOrEmpty(keepItemTitle))
            {
                var hit = FindNodeByTitle(CurrentRoots(), keepItemTitle);
                if (hit != null)
                {
                    SelectNode(hit.Item);
                    SelectNode(hit);
                    RevealNode(hit);
                }
            }
        }

        /// <summary>按标题深度优先找节点（B4b-3 恢复选中用）。</summary>
        private static NodeRow FindNodeByTitle(IEnumerable<NodeRow> nodes, string title)
        {
            foreach (var n in nodes)
            {
                if (n.Item != null && n.Item.Title == title) return n;
                var r = FindNodeByTitle(n.Children, title);
                if (r != null) return r;
            }
            return null;
        }

        // ============================================================
        // 分组操作
        // ============================================================
        private void OnAddGroup(object sender, RoutedEventArgs e)
        {
            var dlg = new GroupEditDialog("", GroupType.Always, null);
            if (dlg.ShowDialog() != true) return;

            var g = new MenuGroup
            {
                Name = dlg.GroupName,
                Type = dlg.GroupType
            };
            // ★ 关键词必须回传（旧版漏了 ⇒ "分组依据编辑和显示不全"）
            if (dlg.GroupKeywords != null)
                g.Keywords.AddRange(dlg.GroupKeywords);
            var first = MenuTreeOps.NewItem();   // 出厂值（新条目 / run notepad.exe）在 Services
            first.Level = 1;
            g.Items.Add(first);

            _groups.Add(g);
            FillGroups();
            _groupList.SelectedIndex = _groups.Count - 1;
            MarkDirty();
        }

        private void RenameGroup()
        {
            var g = SelectedGroup();
            if (g == null) return;

            var dlg = new GroupEditDialog(g.Name, g.Type, g.Keywords);
            if (dlg.ShowDialog() != true) return;

            g.Name = dlg.GroupName;
            g.Type = dlg.GroupType;
            // ★ 关键词回写（旧版从不回写 ⇒ 改了等于没改）
            g.Keywords.Clear();
            if (dlg.GroupKeywords != null)
                g.Keywords.AddRange(dlg.GroupKeywords);

            // 分组列表的显示文本要刷新（含类型/关键词）
            int keep = _groupList.SelectedIndex;
            FillGroups();
            if (keep >= 0 && keep < _groupList.Items.Count)
                _groupList.SelectedIndex = keep;
            MarkDirty();
        }

        private void OnDelGroup(object sender, RoutedEventArgs e)
        {
            var g = SelectedGroup();
            if (g == null) return;

            var r = MessageBox.Show(
                "删除分组「" + g.Name + "」及其 " + g.Items.Count + " 个条目？",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            _groups.Remove(g);
            FillGroups();
            MarkDirty();
        }

        private void MoveGroup(int dir)
        {
            int i = _groupList.SelectedIndex;
            if (i < 0) return;
            int j = i + dir;
            if (j < 0 || j >= _groups.Count) return;

            var tmp = _groups[i];
            _groups[i] = _groups[j];
            _groups[j] = tmp;
            FillGroups();
            _groupList.SelectedIndex = j;
            MarkDirty();
        }

        // ============================================================
        // ★ v9 树形中栏：层级校验 / 节点模板 / 拖拽（三种落点）
        //   层级不变式的**唯一实现**在 MenuTreeOps.LevelsWellFormed（保存闸与自检共用）；
        //   页面只在回写后断言一次（防御性，正常永不触发）。
        // ============================================================

        /// <summary>树节点模板：标题（组头加粗）+ 灰色 meta（快捷键 · 动作 · 目标）。</summary>
        /// <summary>
        /// 递归节点模板（2026-11-07 弃用 TreeView 后重写）。
        ///
        /// 结构：
        ///   StackPanel
        ///     ├─ Border  卡片（背景绑 IsSelected ⇒ 选中浅绿；Tag = NodeRow 供命中测试）
        ///     │    └─ Grid[箭头 22 | 标题+meta]
        ///     └─ ItemsControl  子级递归（ItemsSource=Children，Margin 左缩进 22）
        ///          └─ ItemTemplate = **本模板自身** ⇒ 无限层级自动展开
        ///
        /// 为什么要重建驱动：ItemsControl 没有 TreeViewItem 那套 IsExpanded/IsSelected
        /// 双向绑定回写，NodeRow 也没实现 INotifyPropertyChanged ⇒ 任何状态变化后由
        /// 调用方 RefreshGrid() 重建（几十个节点，成本可忽略）。
        /// </summary>
        private static HierarchicalDataTemplate BuildNodeTemplate()
        {
            var tpl = new HierarchicalDataTemplate(typeof(NodeRow));
            tpl.ItemsSource = new System.Windows.Data.Binding("Children");

            //   这里若填白会把绿底完全盖住（"条目选中没效果"就是这个原因）。
            var card = new FrameworkElementFactory(typeof(Border));
            card.Name = "Card";
            card.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            card.SetValue(Border.BorderThicknessProperty, new Thickness(0));
            card.SetValue(Border.PaddingProperty, new Thickness(4, 3, 6, 3));
            card.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

            var sp = new FrameworkElementFactory(typeof(StackPanel));
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);

            // 层级引导符（子项 └，根层空）
            var prefix = new FrameworkElementFactory(typeof(TextBlock));
            prefix.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Prefix"));
            prefix.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)));
            prefix.SetValue(TextBlock.FontSizeProperty, 12.0);
            prefix.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            sp.AppendChild(prefix);

            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
            tb.SetBinding(TextBlock.FontWeightProperty, new System.Windows.Data.Binding("TitleWeight"));
            tb.SetValue(TextBlock.FontSizeProperty, 13.0);
            tb.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            sp.AppendChild(tb);

            var meta = new FrameworkElementFactory(typeof(TextBlock));
            meta.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Meta"));
            meta.SetValue(TextBlock.ForegroundProperty, SettingsKit.TextSecondary);
            meta.SetValue(TextBlock.FontSizeProperty, 11.0);
            meta.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            meta.SetValue(FrameworkElement.MarginProperty, new Thickness(10, 0, 0, 0));
            meta.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            sp.AppendChild(meta);

            card.AppendChild(sp);
            tpl.VisualTree = card;

            // ★ C2 多选标记：浅蓝底（与"当前编辑"的浅绿区分）——
            //   蓝 = 会被一起搬的那批；绿 = 字段面板正在编辑的那一条。
            var marked = new DataTrigger
            {
                Binding = new System.Windows.Data.Binding("IsMarked"),
                Value = true
            };
            marked.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xE6, 0xEF, 0xFB)), "Card"));
            tpl.Triggers.Add(marked);

            return tpl;
        }
        /// <summary>左栏分组卡片模板：名称加粗 + 依据灰色小字（外框由容器模板负责）。</summary>
        private static DataTemplate BuildGroupCardTemplate()
        {
            var tpl = new DataTemplate(typeof(GroupRow));
            var sp = new FrameworkElementFactory(typeof(StackPanel));

            var name = new FrameworkElementFactory(typeof(TextBlock));
            name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Group.Name"));
            name.SetValue(TextBlock.FontSizeProperty, 13.0);
            name.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            // ★ B1-3：名称保持单行省略（卡片不被超高内容撑爆），但 ToolTip 给全名。
            //   `FrameworkElement.ToolTip` 是依赖属性 ⇒ 可以直接绑数据源属性。
            name.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding("Group.Name"));
            sp.AppendChild(name);

            var basis = new FrameworkElementFactory(typeof(TextBlock));
            basis.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Basis"));
            basis.SetValue(TextBlock.ForegroundProperty, SettingsKit.TextSecondary);
            basis.SetValue(TextBlock.FontSizeProperty, 11.0);
            basis.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
            // ★ B1-2：依据要**换行显示完整**，不许截断。
            //   TextTrimming 只在 TextWrapping=NoWrap 时才截断（MS Learn《How to: Enable Text Trimming》），
            //   所以这里去掉 trimming、改 Wrap —— 卡片高度随内容自适应（StackPanel 无固定高）。
            basis.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            sp.AppendChild(basis);

            tpl.VisualTree = sp;
            return tpl;
        }
        /// <summary>
        /// 树条目容器样式（2026-11-07 B 方案）：层级连线 + 自绘箭头。
        ///
        /// 用 XamlReader 解析整段 ControlTemplate，而不是 FrameworkElementFactory 搭——
        /// 因为 `Grid.ColumnDefinitions` 不是 DependencyProperty，FEF 无法设置集合。
        /// XAML 里能直接写 `<Grid.ColumnDefinitions>`，这也是社区解法（SO 19560466）。
        ///
        /// ★ 关键：`ItemsPresenter` 必须保留，否则子树会**静默消失**（v9 踩过）。
        /// ★ 与 C 方案（递归 ItemsControl）的差别：ControlTemplate 每次实例化 nameScope
        ///   独立，不存在自引用循环，不会报「未找到 Name」。
        /// </summary>
        private static Style BuildTreeItemStyle()
        {
            const string templateXaml = @"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                 TargetType='{x:Type TreeViewItem}'>
  <Grid Margin='0,0,0,2'>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='19'/>
      <ColumnDefinition Width='Auto'/>
      <ColumnDefinition Width='*'/>
    </Grid.ColumnDefinitions>
    <Grid.RowDefinitions>
      <RowDefinition Height='Auto'/>
      <RowDefinition Height='*'/>
    </Grid.RowDefinitions>

    <Rectangle x:Name='VerLn' Width='1' Fill='#DDDDDD' HorizontalAlignment='Left'
               Margin='9,0,0,0' Grid.RowSpan='2'/>
    <Rectangle x:Name='HorLn' Height='1' Width='9' Fill='#DDDDDD' HorizontalAlignment='Left'
               Margin='9,0,0,0' VerticalAlignment='Center'/>

    <!-- 展开箭头：ToggleButton 才能点（TextBlock 不可交互）-->
    <ToggleButton x:Name='Expander' Focusable='False' Cursor='Hand'
                  HorizontalAlignment='Left' Margin='3,0,0,0'
                  IsChecked='{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}}'>
      <ToggleButton.Template>
        <ControlTemplate TargetType='ToggleButton'>
          <TextBlock x:Name='Glyph' Text='&#x25BE;' FontSize='9' Foreground='#8A8A8A'
                     VerticalAlignment='Center'/>
          <ControlTemplate.Triggers>
            <Trigger Property='IsChecked' Value='False'>
              <Setter TargetName='Glyph' Property='Text' Value='&#x25B8;'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </ToggleButton.Template>
    </ToggleButton>

    <!-- 内容卡片：选中态靠 Bd 的背景表达 -->
    <Border x:Name='Bd' Grid.Column='1' Grid.ColumnSpan='2'
            Background='Transparent' CornerRadius='5' Padding='2,1'>
      <ContentPresenter x:Name='PART_Header' ContentSource='Header'
                        HorizontalAlignment='Stretch'/>
    </Border>

    <ItemsPresenter x:Name='ItemsHost' Grid.Row='1' Grid.Column='1' Grid.ColumnSpan='2'/>
  </Grid>
  <ControlTemplate.Triggers>
    <!-- 选中：浅绿底 + 左侧绿条 -->
    <Trigger Property='IsSelected' Value='True'>
      <Setter TargetName='Bd' Property='Background' Value='#E8F3EA'/>
      <Setter TargetName='Bd' Property='Padding' Value='2,1,2,1'/>
    </Trigger>
    <!-- 折叠：隐藏子树 -->
    <Trigger Property='IsExpanded' Value='False'>
      <Setter TargetName='ItemsHost' Property='Visibility' Value='Collapsed'/>
    </Trigger>
    <!-- 无子项：隐藏箭头与连线 -->
    <Trigger Property='HasItems' Value='False'>
      <Setter TargetName='Expander' Property='Visibility' Value='Collapsed'/>
      <Setter TargetName='VerLn' Property='Visibility' Value='Collapsed'/>
      <Setter TargetName='HorLn' Property='Visibility' Value='Collapsed'/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";


            var st = new Style(typeof(TreeViewItem));
            st.Setters.Add(new Setter(TreeViewItem.IsExpandedProperty,
                new System.Windows.Data.Binding("IsExpanded")
                { Mode = System.Windows.Data.BindingMode.TwoWay }));
            st.Setters.Add(new Setter(TreeViewItem.IsSelectedProperty,
                new System.Windows.Data.Binding("IsSelected")
                { Mode = System.Windows.Data.BindingMode.TwoWay }));
            st.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            st.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            st.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));

            try
            {
                var tpl = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(templateXaml);
                st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            }
            catch (Exception ex)
            {
                // 模板解析失败绝不拖垮设置窗口：退回原生模板（只是没有连线）
                System.Diagnostics.Debug.WriteLine("BuildTreeItemStyle: " + ex.Message);
            }

            return st;
        }

        private static Style BuildGroupItemStyle()
        {
            var st = new Style(typeof(ListBoxItem));
            st.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 6)));

            var tpl = new ControlTemplate(typeof(ListBoxItem));
            var border = new FrameworkElementFactory(typeof(Border));
            border.Name = "Bd";
            border.SetValue(Border.BackgroundProperty, Brushes.White);
            border.SetValue(Border.BorderBrushProperty, SettingsKit.CardBorder);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetValue(Border.PaddingProperty, new Thickness(10, 7, 8, 7));
            // ★ 卡片必须横向拉伸填满列表宽度，否则会按内容宽度收缩（截图实测宽度不齐）
            border.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
            border.AppendChild(cp);
            tpl.VisualTree = border;

            // 选中：浅绿底 + 绿色边框 + 左侧 3px 绿条
            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Border.BackgroundProperty, SettingsKit.NavSelected, "Bd"));
            sel.Setters.Add(new Setter(Border.BorderBrushProperty, SettingsKit.Accent, "Bd"));
            tpl.Triggers.Add(sel);

            // 悬停
            var hover = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7)), "Bd"));
            tpl.Triggers.Add(hover);

            st.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            return st;
        }

    }
}
