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
    ///     · 🔴 **强制不变式**（见 <see cref="LevelsWellFormed"/>）：**父项之后必须紧跟其
    ///       全部子项** + **禁止跳级** —— 由"树结构 + DFS 回写"天然保证；回写后再断言校验一次
    ///     · 折叠 = `TreeViewItem.IsExpanded`（纯视图态，按 MItem 记账，不写进配置）
    /// </summary>
    public class MenuEditorPage : UserControl
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

        // ★ 折叠状态（**纯视图态**，不写进配置；按 MItem 记账 ⇒ 重建后不丢）+ 拖拽起手
        private readonly HashSet<MItem> _collapsed = new HashSet<MItem>();
        private Point _dragStart;
        private NodeRow _dragNode;
        private MItem _selectedItem;

        /// <summary>
        /// ★ v9：选中节点的**权威记录** —— 不依赖 `TreeView.SelectedItem`。
        /// 原因：无窗口/未布局时 TreeView 的容器尚未生成，`SelectedItem` 会滞后或为 null，
        /// 结构操作（⇥/⇤/↑↓/删除）就全部失效（自检实测踩到）。这里自持一份，随选择同步。
        /// </summary>
        private NodeRow _selNode;
        private TextBox _titleBox;      // 字段编辑区的标题框（双击定位用）
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
            midBtns.Children.Add(MkBtn("＋ 条目", "在当前分组新增条目", OnAddItem));
            midBtns.Children.Add(MkBtn("－", "删除选中条目", OnDelItem));
            midBtns.Children.Add(MkBtn("↑", "上移", (s, e) => MoveItem(-1)));
            midBtns.Children.Add(MkBtn("↓", "下移", (s, e) => MoveItem(1)));
            midBtns.Children.Add(MkBtn("⇥", "增加缩进（成为子项）", (s, e) => IndentItem(1)));
            midBtns.Children.Add(MkBtn("⇤", "减少缩进", (s, e) => IndentItem(-1)));
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
                IsReadOnly = true,
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
            var saveBtn = new Button
            {
                Content = "保存到 menus.conf",
                Width = 150,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            saveBtn.Click += OnSave;
            DockPanel.SetDock(saveBtn, Dock.Right);
            statusBar.Children.Add(saveBtn);

            var reloadBtn = new Button
            {
                Content = "放弃修改",
                Width = 100,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(8, 0, 0, 0)
            };
            reloadBtn.Click += (s, e) => { Reload(); SetStatus("已重新加载", false); };
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
            try
            {
                var p = new ConfParser(_confPath);
                p.Parse();
                _groups = p.Groups;
                _dirty = false;
                FillGroups();
                SetStatus("已加载 " + _groups.Count + " 个分组", false);
            }
            catch (Exception ex)
            {
                SetStatus("加载失败：" + ex.Message, true);
            }
        }

        private void FillGroups()
        {
            _groupList.Items.Clear();
            foreach (var g in _groups)
                _groupList.Items.Add(new GroupRow(g));
            if (_groupList.Items.Count > 0)
                _groupList.SelectedIndex = 0;
            else
                FillItems();
        }

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
        // 字段编辑区 + 预览
        // ============================================================
        /// <summary>
        /// 2026-11-07：右侧字段区改「两列网格」——标签右对齐固定 88px + 控件拉伸。
        ///
        /// 原来 StackPanel 里标签一行、控件一行、宽度还各不一样（见改前截图），
        /// 视觉上就是「一团散沙」，这是用户说「设置面太丑」最直观的一条。
        /// fullRow=true 用于占位符这种本来就该占满整行的块。
        /// </summary>
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
            var tbKey = MkEdit(it.Hotkey);
            tbKey.MaxLength = 2;
            tbKey.ToolTip = "按一个键即可填入（Backspace 清空）；也可直接输入";
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
                    tbKey.Text = ch;
                    e.Handled = true;      // 吃掉原按键，避免同时输入原始字符
                }
            };
            tbKey.TextChanged += (s, e) =>
            {
                it.Hotkey = tbKey.Text.Trim();
                RefreshGrid(); MarkDirty();
            };
            AddField("快捷键", tbKey);

            // 动作类型
            var cbType = new ComboBox { Margin = new Thickness(0) };
            foreach (var t in new[] { "group", "run", "open", "url", "action", "send", "keys" })
                cbType.Items.Add(t);
            cbType.SelectedItem = TypeStr(it.Type);
            cbType.SelectionChanged += (s, e) =>
            {
                if (cbType.SelectedItem == null) return;
                it.Type = ParseType(cbType.SelectedItem.ToString());
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

                fileBox = new PathBox(PathBox.Mode.PathAny, null, "选择程序或文件", ProgramCandidates());
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
            _baseLevel = 0;
            if (g.Items.Count > 0)
            {
                _baseLevel = g.Items[0].Level;
                foreach (var it in g.Items) if (it.Level < _baseLevel) _baseLevel = it.Level;
                if (_baseLevel < 0) _baseLevel = 0;
            }

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

        /// <summary>选中某个 MItem 的节点（自动展开其祖先）。</summary>
        private void SelectNode(MItem it)
        {
            _selNode = null;
            if (it == null) return;
            _selectedItem = it;
            var roots = CurrentRoots();
            var node = FindNode(roots, it);
            if (node == null) return;
            // ★ 2026-11-07：ItemsControl 不像 TreeView 会自动取消别的项的选中态，
            //   必须显式清空，否则点过的每一项都会保持高亮。
            ClearSelection(roots);
            for (var p = node.Parent; p != null; p = p.Parent) p.IsExpanded = true;
            node.IsSelected = true;
            _selNode = node;
        }

        /// <summary>当前选中的树节点（null = 无选中）。</summary>
        private NodeRow SelectedNode()
        {
            if (_selNode != null) return _selNode;
            return _itemTree == null ? null : _itemTree.SelectedItem as NodeRow;
        }

        /// <summary>
        /// ★ 树 → 扁平 `g.Items` 回写：DFS 顺序 + `Level = 深度`。
        /// **层级的唯一源是树** ⇒ 结构天然满足 🔴 不变式「父项后紧跟其全部子项 + 不跳级」；
        /// 回写后再断言一次（防御性，正常永不触发）。
        /// </summary>
        private void Flatten()
        {
            var g = SelectedGroup();
            if (g == null) return;

            var flat = new List<MItem>();
            FlattenWalk(CurrentRoots(), _baseLevel, flat);
            g.Items.Clear();
            g.Items.AddRange(flat);

            string why;
            if (!LevelsWellFormed(g.Items, out why))
                SetStatus("⚠ 内部层级异常（请反馈）：" + why, true);
        }

        private static void FlattenWalk(IEnumerable<NodeRow> nodes, int depth, List<MItem> flat)
        {
            foreach (var n in nodes)
            {
                n.Item.Level = depth;
                flat.Add(n.Item);
                FlattenWalk(n.Children, depth + 1, flat);
            }
        }

        /// <summary>某节点子树的最后一个后代在扁平列表里的下标（含自身；用于"同层插到其后"）。</summary>
        private static int LastDescendantIndex(List<MItem> items, MItem it)
        {
            int i = items.IndexOf(it);
            if (i < 0) return items.Count - 1;
            int lv = items[i].Level;
            int j = i + 1;
            while (j < items.Count && items[j].Level > lv) j++;
            return j - 1;
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

        private TextBlock MkLabel(string t)
        {
            return new TextBlock
            {
                Text = t,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 0)
            };
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
            g.Items.Add(new MItem
            {
                Title = "新条目",
                Type = ActionType.Run,
                Target = "notepad.exe",
                Level = 1
            });

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
        // 条目操作
        // ============================================================
        private void OnAddItem(object sender, RoutedEventArgs e)
        {
            var g = SelectedGroup();
            if (g == null)
            {
                SetStatus("请先选一个分组", true);
                return;
            }

            var sel = SelectedNode();
            var it = new MItem
            {
                Title = "新条目",
                Type = ActionType.Run,
                Target = "notepad.exe",
                Level = 0
            };
            var node = new NodeRow(this, it);

            if (sel == null)
            {
                // 无选中 ⇒ 追加到根层末尾
                var roots = CurrentRoots();
                roots.Add(node);
                _itemTree.ItemsSource = roots;
            }
            else if (sel.Item.Type == ActionType.Group)
            {
                // 组头 ⇒ 新条目**直接成为它的子项**（树里最自然的"加子项"）
                node.Parent = sel;
                sel.Children.Add(node);
                sel.IsExpanded = true;
            }
            else if (sel.Parent != null)
            {
                // 普通条目 ⇒ 作为它的下一个兄弟
                node.Parent = sel.Parent;
                int idx = sel.Parent.Children.IndexOf(sel);
                sel.Parent.Children.Insert(idx + 1, node);
            }
            else
            {
                var roots = CurrentRoots();
                int idx = roots.IndexOf(sel);
                roots.Insert(idx + 1, node);
                _itemTree.ItemsSource = roots;
            }

            Flatten();
            RefreshGrid();
            SelectNode(it);
            RefreshPreview();
            MarkDirty();
        }

        private void OnDelItem(object sender, RoutedEventArgs e)
        {
            var g = SelectedGroup();
            var sel = SelectedNode();
            if (g == null || sel == null)
            {
                SetStatus("先选中要删除的条目", true);
                return;
            }

            int total = CountNodes(sel);
            string msg = total > 1
                ? "删除「" + sel.Item.Title + "」及其 " + (total - 1) + " 个子项？"
                : "删除条目「" + sel.Item.Title + "」？";
            if (MessageBox.Show(msg, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;

            DeleteNodeCore(sel);
        }

        /// <summary>
        /// 删除节点（含子树）。★ 与确认对话框分离 ⇒ 自检可无人值守驱动同一份实现。
        /// </summary>
        private void DeleteNodeCore(NodeRow sel)
        {
            int total = CountNodes(sel);

            // 删完选谁：同层后继 → 否则父项
            MItem next = null;
            if (sel.Parent != null)
            {
                int idx = sel.Parent.Children.IndexOf(sel);
                next = (idx + 1 < sel.Parent.Children.Count)
                    ? sel.Parent.Children[idx + 1].Item
                    : sel.Parent.Item;
            }
            else
            {
                var roots = CurrentRoots();
                int idx = roots.IndexOf(sel);
                if (idx + 1 < roots.Count) next = roots[idx + 1].Item;
            }

            ForgetCollapsed(sel);
            Detach(sel);
            _selectedItem = null;
            _lastPanelItem = null;      // 面板必须重建

            Flatten();
            RefreshGrid();
            if (next != null) SelectNode(next);
            RefreshPreview();
            MarkDirty();
            SetStatus("已删除 " + total + " 个条目", false);
        }

        private static int CountNodes(NodeRow n)
        {
            int c = 1;
            foreach (var ch in n.Children) c += CountNodes(ch);
            return c;
        }

        private void MoveItem(int dir)
        {
            var g = SelectedGroup();
            var sel = SelectedNode();
            if (g == null || sel == null) return;

            var list = sel.Parent == null ? CurrentRoots() : sel.Parent.Children;
            int i = list.IndexOf(sel);
            int j = i + dir;
            if (i < 0 || j < 0 || j >= list.Count) return;

            list.RemoveAt(i);
            list.Insert(j, sel);
            if (sel.Parent == null) _itemTree.ItemsSource = list;

            Flatten();
            RefreshGrid();
            SelectNode(sel.Item);
            RefreshPreview();
            MarkDirty();
            SetStatus((dir < 0 ? "上移" : "下移") + "到同层第 " + (j + 1) + " 位"
                + (sel.Children.Count > 0 ? "（连同 " + sel.Children.Count + " 个子项）" : ""), false);
        }

        /// <summary>
        /// ⇥ = 成为**上一条（同层前驱）的子项**；⇤ = 升到**父项的同层后继位置**。
        /// ★ v9：直接在树上搬家 ⇒ 结构天然合法（原"改 Level + 事后校验"的写法不再需要，
        ///   但 <see cref="Flatten"/> 仍会断言一次不变式）。
        /// </summary>
        private void IndentItem(int delta)
        {
            var g = SelectedGroup();
            var sel = SelectedNode();
            if (g == null || sel == null) return;

            if (delta > 0)
            {
                var siblings = sel.Parent == null ? CurrentRoots() : sel.Parent.Children;
                int i = siblings.IndexOf(sel);
                if (i <= 0)
                {
                    SetStatus("缩进被拒绝：它是同级第一条，没有可挂靠的上一条（先用 ↑↓ 挪次序）", true);
                    return;
                }
                var prev = siblings[i - 1];
                Detach(sel);
                sel.Parent = prev;
                prev.Children.Add(sel);
                prev.IsExpanded = true;
                SetStatus("已成为「" + prev.Item.Title + "」的子项", false);
            }
            else
            {
                if (sel.Parent == null)
                {
                    SetStatus("已在最外层，无法反缩进", true);
                    return;
                }
                var parent = sel.Parent;
                var grand = parent.Parent;
                Detach(sel);
                sel.Parent = grand;
                if (grand != null)
                {
                    int idx = grand.Children.IndexOf(parent);
                    grand.Children.Insert(idx + 1, sel);
                }
                else
                {
                    var roots = CurrentRoots();
                    int idx = roots.IndexOf(parent);
                    roots.Insert(idx + 1, sel);
                    _itemTree.ItemsSource = roots;
                }
                SetStatus("已升到「" + parent.Item.Title + "」的同层", false);
            }

            Flatten();
            RefreshGrid();
            SelectNode(sel.Item);
            RefreshPreview();
            MarkDirty();
        }

        // ============================================================
        // ★ v9 树形中栏：层级校验 / 节点模板 / 拖拽（三种落点）
        // ============================================================

        /// <summary>
        /// 层级合法性（**强制不变式**）。运行期 `MatchEngine` / `MissingFilter` 用
        /// `while (j &lt; n &amp;&amp; src[j].Level &gt; it.Level) j++` —— 即**按"紧随其后"**扫描子条目。
        /// 因此只允许"逐级加深"：出现**跳级**（前一条 lv1、后一条 lv3）会让父子关系错乱、
        /// 子菜单建错。判据 = 除首条外，`Level ≤ 前一条.Level + 1`。
        /// ★ v9 起该不变式由「树结构 + <see cref="Flatten"/> DFS 回写」**天然保证**，
        /// 本方法保留作回写后的防御性断言。
        /// </summary>
        private static bool LevelsWellFormed(List<MItem> items, out string why)
        {
            why = "";
            for (int i = 1; i < items.Count; i++)
            {
                if (items[i].Level > items[i - 1].Level + 1)
                {
                    why = "第 " + (i + 1) + " 条跳级（前一条 lv" + items[i - 1].Level
                        + " → lv" + items[i].Level + "）";
                    return false;
                }
            }
            return true;
        }

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
            sp.AppendChild(name);

            var basis = new FrameworkElementFactory(typeof(TextBlock));
            basis.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Basis"));
            basis.SetValue(TextBlock.ForegroundProperty, SettingsKit.TextSecondary);
            basis.SetValue(TextBlock.FontSizeProperty, 11.0);
            basis.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
            basis.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
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

        /// <summary>
        /// 选中一个节点（ItemsControl 版）：写 _selNode + 按需重建字段面板。
        /// ★ 焦点保护（沿用 v9 语义）：树重建期间只同步选中项；选中项与当前编辑项
        ///   是同一个 MItem 时不重建（否则会撞输入框焦点，边打字边跳）。
        /// </summary>
        private void SelectNode(NodeRow node)
        {
            if (node == null) return;
            // 先清掉其它节点的选中标记（ItemsControl 没有 TreeView 的自动取消）
            ClearSelection(CurrentRoots());
            node.IsSelected = true;          // INotifyPropertyChanged ⇒ DataTrigger 立即生效

            _selNode = node;
            _selectedItem = node.Item;
            if (_suppressFieldRebuild) return;
            if (ReferenceEquals(_lastPanelItem, node.Item)) return;
            BuildFieldPanel(_selectedItem);
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

        /// <summary>
        /// **纯模型层**：DFS 遍历当前树，返回命中的 `MItem`（不碰任何控件 ⇒ 可无人值守自检）。
        /// 大小写不敏感用 `IndexOf(..., OrdinalIgnoreCase)`，不造临时小写串。
        /// </summary>
        internal List<MItem> T_Search(string q)
        {
            var hits = new List<MItem>();
            if (string.IsNullOrEmpty(q)) return hits;
            SearchWalk(CurrentRoots(), q, hits);
            return hits;
        }

        private static void SearchWalk(IEnumerable<NodeRow> nodes, string q, List<MItem> hits)
        {
            foreach (var n in nodes)
            {
                var it = n.Item;
                if (ContainsNoCase(it.Title, q) || ContainsNoCase(it.Hotkey, q) || ContainsNoCase(it.Target, q))
                    hits.Add(it);
                SearchWalk(n.Children, q, hits);
            }
        }

        private static bool ContainsNoCase(string s, string q)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

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

        /// <summary>树里 Ctrl+F ⇒ 聚焦搜索框（WPF 原生 PreviewKeyDown 隧道，先于 TreeView 自己的按键处理）。</summary>
        private void ItemTree_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (_searchBox != null) { _searchBox.Focus(); _searchBox.SelectAll(); }
                e.Handled = true;
            }
        }
        private void ItemTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var node = NodeAt(e.OriginalSource as DependencyObject);
            if (node == null) return;
            _selNode = node;
            _selectedItem = node.Item;
            BuildFieldPanel(_selectedItem);
            FocusTitleBox();
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
            if (node != null) SelectNode(node);
        }

        private void ItemTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var node = NodeAt(e.OriginalSource as DependencyObject);
            if (node != null) SelectNode(node);
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
                    // 拖到组头 ⇒ 作为它的子项（与 OnAddItem 的组头分支一致）
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

        private void FinishDrop(NodeRow src, string msg)
        {
            Flatten();
            RefreshGrid();
            SelectNode(src.Item);
            RefreshPreview();
            MarkDirty();
            SetStatus(msg, false);
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

        // ============================================================
        private void OnSave(object sender, RoutedEventArgs e)
        {
            string msg;
            bool ok = _writer.Save(_groups, out msg);
            SetStatus(msg.Replace("\n", "  |  "), !ok);
            if (ok)
            {
                _dirty = false;
                Reload();

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
            }
        }

        /// <summary>
        /// 目标为空的条目数（D-5：保存后警告用；自检侧另有对应报告）。
        /// ★ `group` 类型（纯分组标题）目标**本来就空** ⇒ 不计入。
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

        // ============================================================
        // 列表行包装（DataGrid 绑定用）
        // ============================================================
        private class GroupRow
        {
            public MenuGroup Group;
            public GroupRow(MenuGroup g) { Group = g; }

            /// <summary>分组依据显示：类型 + 关键词（否则"显示不全"）。</summary>
            public string Basis
            {
                get
                {
                    string t = MenuGroup.TypeToString(Group.Type);
                    if ((Group.Type == GroupType.Ext || Group.Type == GroupType.Proc)
                        && Group.Keywords.Count > 0)
                        return t + ": " + string.Join(" ", Group.Keywords);
                    return t;
                }
            }

            public string CountText
            {
                get { return Group.Items.Count + " 项"; }
            }

            public override string ToString()
            {
                return Group.Name + "   (" + Basis + ")";
            }
        }

        /// <summary>
        /// ★ v9 树节点（TreeView 绑定用）：包一个 `MItem` + 子节点集合。
        /// 父子结构 = 菜单层级；`Children` 是 `HierarchicalDataTemplate.ItemsSource` 的来源。
        /// （原 `ItemRow` 是 DataGrid 的扁平行 —— 缩进只是视觉效果，已废弃。）
        /// </summary>
        internal class NodeRow : System.ComponentModel.INotifyPropertyChanged
        {
            private readonly MenuEditorPage _page;
            public readonly MItem Item;
            private readonly List<NodeRow> _children = new List<NodeRow>();

            /// <summary>
            /// 子节点集合（`HierarchicalDataTemplate.ItemsSource` 的来源）。
            /// ★ 必须是**属性**：WPF 数据绑定**不认字段**（只认属性）——
            ///   踩过：写成 `public readonly List&lt;NodeRow&gt; Children;` 字段时，
            ///   树**只渲染根节点**、子项静默丢失（无异常、无提示，绑定错误只在 trace 里）。
            /// </summary>
            public List<NodeRow> Children { get { return _children; } }

            /// <summary>父节点（null = 根层）；构建/搬家时维护，用于展开祖先与子树判定。</summary>
            public NodeRow Parent;

            /// <summary>
            /// 层级引导符（2026-11-07 树形可见性）：WPF 原生 TreeViewItem 只靠左移 19px
            /// 表达层级、没有连线，用户反馈「树形显示实际也不是特别明显」。
            /// 子项加 `└ ` 前缀 + 原生缩进 ⇒ 一眼能分层。
            /// ★ 必须是属性（WPF 绑定不认字段）；拖拽重建树时会重新求值，无需通知。
            /// </summary>
            public string Prefix
            {
                get { return Parent == null ? "" : "└ "; }
            }

            public NodeRow(MenuEditorPage page, MItem it)
            {
                _page = page;
                Item = it;
            }

            /// <summary>
            /// 展开态（双向绑定到 `TreeViewItem.IsExpanded`）。
            /// 折叠账本按 `MItem` 记账 ⇒ 树重建后折叠状态不丢（纯视图态，不写进配置）。
            /// </summary>
            public bool IsExpanded
            {
                get { return !_page._collapsed.Contains(Item); }
                set
                {
                    if (value) _page._collapsed.Remove(Item);
                    else _page._collapsed.Add(Item);
                    Raise("IsExpanded");
                }
            }

            private bool _selected;

            /// <summary>
            /// 选中态。
            /// ★ 2026-11-07 换成 ItemsControl 后必须发通知：TreeView 靠容器的双向绑定回写，
            ///   ItemsControl 没有这层，DataTrigger 只有收到 PropertyChanged 才会重新求值。
            /// </summary>
            public bool IsSelected
            {
                get { return _selected; }
                set { if (_selected != value) { _selected = value; Raise("IsSelected"); } }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            private void Raise(string name)
            {
                var h = PropertyChanged;
                if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }

            public string Title { get { return Item.Title; } }

            /// <summary>组头（`group` 类型 = 纯分组标题）加粗，与普通条目区分。</summary>
            public FontWeight TitleWeight
            {
                get { return Item.Type == ActionType.Group ? FontWeights.Bold : FontWeights.Normal; }
            }

            /// <summary>
            /// 标题后的灰色 meta：`_快捷键 · 动作 · 目标`。
            /// 树节点随深度整体右移 ⇒ 分列对齐本就无意义，故压成一行（信息不丢、更紧凑）。
            /// </summary>
            public string Meta
            {
                get
                {
                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(Item.Hotkey)) parts.Add("_" + Item.Hotkey);
                    parts.Add(Item.Type == ActionType.Group ? "group" : TypeStr(Item.Type));
                    if (!string.IsNullOrEmpty(Item.Target)) parts.Add(Item.Target);
                    return "  " + string.Join("  ·  ", parts.ToArray());
                }
            }
        }

        // ============================================================
        // ★ 测试缝（`--selftest-menueditor` 用）
        //   把「结构操作」暴露给同程序集的**无人值守**自检 —— 不经 UI 事件、不弹对话框；
        //   产品路径与自检路径调用的**是同一份实现**（OnAddItem / DeleteNodeCore / MoveItem /
        //   IndentItem / ApplyDropCore），不存在"测试缝里另有一套逻辑"。
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
        internal MItem T_Selected() { return _selectedItem; }
        internal NodeRow T_Find(MItem it) { return FindNode(CurrentRoots(), it); }
        internal void T_Select(MItem it) { SelectNode(it); }
        internal void T_AddItem() { OnAddItem(null, null); }
        internal void T_Delete() { var s = SelectedNode(); if (s != null) DeleteNodeCore(s); }
        internal void T_Move(int dir) { MoveItem(dir); }
        internal void T_Indent(int delta) { IndentItem(delta); }
        internal void T_Drop(NodeRow src, NodeRow target, DropWhere where) { ApplyDropCore(src, target, where); }
        /// <summary>自检缝：外部拖文件入树（与产品路径同一个 `DropFiles`；只按后缀识别，路径不必存在）。</summary>
        internal void T_DropFiles(string[] paths, NodeRow anchor) { DropFiles(paths, anchor); }
        internal bool T_WellFormed(out string why) { return LevelsWellFormed(SelectedGroup().Items, out why); }
        internal int T_GroupCount() { return _groupList.Items.Count; }
        internal void T_SelectGroup(int i) { if (i >= 0 && i < _groupList.Items.Count) _groupList.SelectedIndex = i; }
        internal int T_BaseLevel() { return _baseLevel; }
        internal string T_Status() { return _statusText.Text; }

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
                default: return "group";
            }
        }

        private static ActionType ParseType(string s)
        {
            switch (s)
            {
                case "run": return ActionType.Run;
                case "open": return ActionType.Open;
                case "url": return ActionType.Url;
                case "action": return ActionType.Action;
                case "send": return ActionType.Send;
                case "keys": return ActionType.Keys;
                default: return ActionType.Group;
            }
        }
    }
}
