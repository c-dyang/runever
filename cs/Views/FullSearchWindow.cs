using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using RunEverCs.Models;
using RunEverCs.Services;
using RunEverCs.Native;

// System.Windows.Controls 里也有 MenuItem（WPF 菜单项）⇒ 显式指向配置条目模型
using MenuItem = RunEverCs.Models.MenuItem;

namespace RunEverCs.Views
{
    /// <summary>
    /// 全库搜索窗口的一行（分组节头 / 条目行）。
    ///
    /// ★ 匹配用的三份预计算串在**建数据时一次算完**（拼音转换只发生一次），
    ///   之后每次按键只做字符串包含 —— 这就是"缓存避免每次按键重复转换"的落地点。
    /// </summary>
    public sealed class FullSearchRow
    {
        /// <summary>true = 分组节头（不可点、不可选中）。</summary>
        public bool IsHeader { get; set; }

        /// <summary>所属分组名（条目行也带，查询时用于显示节头）。</summary>
        public string SectionName { get; set; } = "";

        /// <summary>
        /// 所属分组的序号（0 起）。**查询按序号分节，不按名字** ——
        /// 手改配置出现两个同名分组时，按名字分节会把两节并成一节（节头只剩一个）。
        /// </summary>
        public int SectionIndex { get; set; }

        /// <summary>条目（节头行为 null）。</summary>
        public MenuItem Item { get; set; }

        /// <summary>左侧显示文本。</summary>
        public string DisplayTitle { get; set; } = "";

        /// <summary>右侧淡字（条目目标；节头行为空）。颜色继承行前景色，只压低透明度。</summary>
        public string DisplayTarget { get; set; } = "";

        /// <summary>名称（小写）—— 子串匹配用。</summary>
        public string MatchName { get; set; } = "";

        /// <summary>名称的全拼（小写）—— 全拼匹配用。</summary>
        public string MatchFull { get; set; } = "";

        /// <summary>名称的首字母（小写）—— 首字母匹配用。</summary>
        public string MatchInitials { get; set; } = "";

        /// <summary>目标 + 分组名（小写）—— **只走子串**，不参与拼音。</summary>
        public string MatchAux { get; set; } = "";

        /// <summary>能不能被选中/执行（节头 = false ⇒ ListBoxItem 直接 disabled）。</summary>
        public bool IsSelectable { get { return !IsHeader && Item != null; } }
    }

    /// <summary>一个分组节（= 菜单里的一个分组，顺序与菜单一致）。</summary>
    public sealed class FullSearchSection
    {
        public string Name = "";
        public GroupType Type;
        public List<MenuItem> Items = new List<MenuItem>();
    }

    /// <summary>
    /// 搜索数据（一次构建、多次查询）。构建时把可见条目、分节、拼音一次性算好；
    /// 每次按键只重建行列表（纯字符串过滤），不再碰拼音转换与路径判定。
    /// </summary>
    public sealed class FullSearchData
    {
        private readonly List<FullSearchSection> _sections;
        private readonly List<FullSearchRow> _rows;

        internal FullSearchData(List<FullSearchSection> sections, List<FullSearchRow> rows)
        {
            _sections = sections ?? new List<FullSearchSection>();
            _rows = rows ?? new List<FullSearchRow>();
        }

        /// <summary>可见分节数（= 菜单里还能看到的分组数）。</summary>
        public int SectionCount { get { return _sections.Count; } }

        /// <summary>可见条目数（不含节头）。</summary>
        public int ItemCount { get { return _rows.Count; } }

        /// <summary>全部分节（只读遍历用；自检拿它核对"与菜单同一集合"）。</summary>
        public List<FullSearchSection> Sections { get { return _sections; } }

        /// <summary>全部可见条目（不分节、按显示顺序）。</summary>
        public List<MenuItem> Items()
        {
            var l = new List<MenuItem>(_rows.Count);
            foreach (var r in _rows) if (r.Item != null) l.Add(r.Item);
            return l;
        }

        /// <summary>
        /// 按查询串出行（含节头）。空查询 = 全部有效条目（分组序 + 组内序，与菜单一致）。
        /// 节头只在**该节至少有一条命中**时出现 —— 空节不占位（与菜单不建空子菜单同口径）。
        /// </summary>
        public List<FullSearchRow> Query(string query)
        {
            string q = NormalizeQuery(query);
            var res = new List<FullSearchRow>();

            int i = 0;
            while (i < _rows.Count)
            {
                int section = _rows[i].SectionIndex;
                string sectionName = _rows[i].SectionName;
                var hits = new List<FullSearchRow>();
                while (i < _rows.Count && _rows[i].SectionIndex == section)
                {
                    if (q.Length == 0 || Matches(_rows[i], q)) hits.Add(_rows[i]);
                    i++;
                }
                if (hits.Count == 0) continue;
                res.Add(new FullSearchRow
                {
                    IsHeader = true,
                    SectionIndex = section,
                    SectionName = sectionName,
                    DisplayTitle = sectionName
                });
                res.AddRange(hits);
            }
            return res;
        }

        /// <summary>查询串归一（去首尾空白 + 小写）。空串 = 不过滤。</summary>
        public static string NormalizeQuery(string query)
        {
            return (query ?? "").Trim().ToLowerInvariant();
        }

        /// <summary>
        /// 单行匹配（**唯一的匹配口径**，窗口与自检共用）：
        ///   · 条目名称 —— 子串 / 首字母 / 全拼（大小写不敏感，都是小写比对）
        ///   · 目标 + 分组名 —— **只走子串**（不做拼音：用户不会用拼音去猜路径）
        /// 节头行永不命中。
        /// </summary>
        public static bool Matches(FullSearchRow r, string query)
        {
            if (r == null || r.IsHeader) return false;
            string q = NormalizeQuery(query);
            if (q.Length == 0) return true;

            if (r.MatchName.IndexOf(q, StringComparison.Ordinal) >= 0) return true;
            if (r.MatchFull.IndexOf(q, StringComparison.Ordinal) >= 0) return true;
            if (r.MatchInitials.IndexOf(q, StringComparison.Ordinal) >= 0) return true;
            if (r.MatchAux.IndexOf(q, StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }

    /// <summary>
    /// 搜索数据构建 + 顺序 —— **与菜单同口径**的唯一实现。
    ///
    /// 过滤器复用菜单的两件东西，一行都不另写：
    ///   ① `PathResolvePolicy.InjectEntryResolved`（缺件位的唯一 IO 源）
    ///   ② `MatchEngine.FilterMissing`（丢弃缺件 + 空子树剪枝）
    /// 再把 auto.conf 的 `menuOrder` 套上（动态 → 进程 → 固定，组内保序）。
    /// ⇒ **菜单看不到的，搜索页也一定看不到**（含"隐藏失效"关掉时也不误杀）。
    /// </summary>
    public static class FullSearchModel
    {
        /// <summary>拼音是否已判定不可用（ChnCharInfo.dll 缺失时置位，之后整轮不再尝试）。</summary>
        private static bool _pinyinBroken;

        /// <summary>
        /// 把一条标题的拼音写进行里。
        /// ★ 单独一个方法：如果 ChnCharInfo.dll 不在 exe 旁边，**类型加载失败发生在调用这个方法时的 JIT**，
        ///   外面才 try 得住；直接写在 Build 里会导致整个 Build 的 JIT 失败（catch 不到）。
        /// </summary>
        private static void FillPinyin(FullSearchRow row, string title)
        {
            row.MatchFull = PinyinHelper.GetFull(title);
            row.MatchInitials = PinyinHelper.GetInitials(title);
        }

        /// <summary>
        /// 构建一份可查询的数据。
        /// <paramref name="hideMissing"/> = 用户设置「隐藏本机没有的条目」；
        /// <paramref name="menuOrder"/> = auto.conf 的三段顺序（null ⇒ 默认序）。
        /// </summary>
        public static FullSearchData Build(IList<MenuGroup> groups, PathCache cache,
            bool hideMissing, string[] menuOrder, Action<string> log)
        {
            var visible = VisibleLikeMenu(groups, cache, hideMissing, log);
            var ordered = OrderByMenuOrder(visible, menuOrder);

            var sections = new List<FullSearchSection>();
            var rows = new List<FullSearchRow>();

            foreach (var g in ordered)
            {
                // 与 MenuBuilder 的渲染护栏同一口径：空壳节点（无子项的组标题等）不出现。
                // hideMissing=0 时 FilterMissing 会原样放行 ⇒ 这里必须再剪一次。
                var items = SubtreePruner.Prune(g.Items, null, null);
                if (items.Count == 0) continue;

                var sec = new FullSearchSection { Name = g.Name ?? "", Type = g.Type, Items = items };
                sections.Add(sec);

                foreach (var it in items)
                {
                    // ★ 只收**可执行叶条目**：分组/子菜单标题不是"能跑的东西"，
                    //   放进列表就会出现"选了没反应"的行。分组名本身已经作为节头显示。
                    if (it == null || it.IsGroupHeader) continue;

                    string title = it.Title ?? "";
                    var row = new FullSearchRow
                    {
                        IsHeader = false,
                        SectionIndex = sections.Count - 1,
                        SectionName = sec.Name,
                        Item = it,
                        DisplayTitle = title,
                        DisplayTarget = it.Target ?? "",
                        MatchName = title.ToLowerInvariant(),
                        MatchAux = ((it.Target ?? "") + " " + sec.Name).ToLowerInvariant()
                    };

                    // 拼音（ChnCharInfo.dll）缺失时降级：窗口照常开，只是中文名不能被拼音命中。
                    if (!_pinyinBroken)
                    {
                        try { FillPinyin(row, title); }
                        catch (Exception ex)
                        {
                            _pinyinBroken = true;
                            if (log != null)
                                log("拼音不可用（exe 旁边缺少 ChnCharInfo.dll）⇒ 降级为仅子串匹配: "
                                    + ex.GetType().Name);
                        }
                    }

                    rows.Add(row);
                }
            }

            return new FullSearchData(sections, rows);
        }

        /// <summary>
        /// 全库可见分组 = 菜单的过滤器（缺件注入 → 丢弃缺件 + 剪空子树），**不**做上下文判定
        /// （搜索页是"全库浏览"，不该因为当前选中了什么东西而少一半条目）。
        /// </summary>
        public static List<MenuGroup> VisibleLikeMenu(IList<MenuGroup> groups, PathCache cache,
            bool hideMissing, Action<string> log)
        {
            var all = new List<MenuGroup>();
            if (groups != null)
                foreach (var g in groups)
                    if (g != null) all.Add(g);

            var ctx = new MatchContext();
            PathResolvePolicy.InjectEntryResolved(ctx, all, cache, hideMissing, log);
            return MatchEngine.FilterMissing(ctx, all, all);
        }

        /// <summary>按 auto.conf 的三段顺序重排分组（段内保持配置序）。</summary>
        public static List<MenuGroup> OrderByMenuOrder(IList<MenuGroup> groups, string[] menuOrder)
        {
            string[] tokens = MenuOrderSpec.Parse(
                menuOrder == null ? null : string.Join(",", menuOrder));

            var res = new List<MenuGroup>();
            if (groups == null) return res;

            foreach (string token in tokens)
                foreach (var g in groups)
                    if (g != null && SegmentOf(g.Type) == token) res.Add(g);
            return res;
        }

        /// <summary>分组类型 → 菜单三段（text/folder/ext/fallback 都归动态段，与判定不变量 I-1 一致）。</summary>
        public static string SegmentOf(GroupType t)
        {
            switch (t)
            {
                case GroupType.Proc: return MenuOrderSpec.Process;
                case GroupType.Always: return MenuOrderSpec.Always;
                default: return MenuOrderSpec.Dynamic;
            }
        }
    }

    /// <summary>按键意图（窗口行为契约；自检直接断言这张表，不必真开窗）。</summary>
    public enum FullSearchKeyIntent
    {
        None = 0,
        MoveUp,
        MoveDown,
        Accept,
        Cancel
    }

    /// <summary>
    /// 全库搜索窗口（WPF）—— S1，2026-11-10。
    ///
    /// 生命周期（用户定案）：**懒创建、关闭即释放**。
    ///   · 类里**没有任何静态窗口字段**（自检用反射守着这条）⇒ 不存在常驻实例；
    ///   · 每次点击 `ShowPicker` 新建一个，`finally` 里 `Release()` 断开可视树与数据源；
    ///   · 主菜单仍是 Win32（`NativeMenu` / `TrackPopupMenuEx`），这里只在"点了全库搜索"之后跑。
    ///
    /// 行为（用户定案）：光标附近弹出 / 480×400（可记忆）/ Esc 关 / Enter 或双击执行 /
    ///   方向键选择 / 分组分节且组头不可点 / 只读浏览（不碰 conf、不写回、不触发保存校验闸）。
    /// </summary>
    public sealed class FullSearchWindow : Window
    {
        /// <summary>默认宽（DIP）。用户定案 480×400。</summary>
        public const double DefaultWidth = 480;

        /// <summary>默认高（DIP）。</summary>
        public const double DefaultHeight = 400;

        private const double MinWidthLimit = 360;
        private const double MinHeightLimit = 240;

        private readonly FullSearchData _data;
        private readonly string _root;
        private readonly ListBox _list = new ListBox();
        private readonly TextBox _box = new TextBox();
        private readonly TextBlock _hint = new TextBlock();

        private MenuItem _picked;
        private bool _selecting;
        private double _loadedWidth;
        private double _loadedHeight;

        /// <summary>
        /// 打开搜索窗口并等用户选一条。返回 null = 取消（Esc / 点关闭 / 没有可选项）。
        /// ★ 阻塞式（ShowDialog）：调用方拿到结果后再 `Dispatch`，与菜单点击同一条执行路径。
        /// </summary>
        public static MenuItem ShowPicker(FullSearchData data, string root)
        {
            if (data == null) return null;

            var win = new FullSearchWindow(data, root);
            win.ShowActivated = true;
            try
            {
                win.ShowDialog();
                return win._picked;
            }
            finally
            {
                win.Release();     // 关闭即释放（可视树 / 数据源 / 模板全部断开）
            }
        }

        /// <summary>
        /// 强制前置（2026-11-10 补丁）：热键回调/后台进程打开窗口时 SetForegroundWindow
        /// 会被前台锁拒绝，且本窗口 ShowInTaskbar=false ⇒ 用户完全找不到它（误以为卡死）。
        /// 组合：先 TOPMOST 顶到最前 → 取消 TOPMOST → 借前台线程输入队列抢焦点 → Activate。
        /// </summary>
        private void BringToFront()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_SHOWWINDOW);
                Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);

                IntPtr fg = Win32.GetForegroundWindow();
                uint fgTid = Win32.GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint myTid = Win32.GetCurrentThreadId();
                bool attached = fgTid != 0 && fgTid != myTid && Win32.AttachThreadInput(myTid, fgTid, true);
                try
                {
                    Win32.SetForegroundWindow(hwnd);
                    Win32.BringWindowToTop(hwnd);
                    Activate();
                }
                finally
                {
                    if (attached) Win32.AttachThreadInput(myTid, fgTid, false);
                }
            }
            catch
            {
                // 前置失败不能弄死窗口：退化为普通弹出（用户可从 Alt-Tab 找到）
            }
        }

        /// <summary>按键 → 意图（窗口与自检共用同一张表）。</summary>
        public static FullSearchKeyIntent ResolveKey(Key key)
        {
            switch (key)
            {
                case Key.Escape: return FullSearchKeyIntent.Cancel;
                case Key.Enter: return FullSearchKeyIntent.Accept;
                case Key.Up: return FullSearchKeyIntent.MoveUp;
                case Key.Down: return FullSearchKeyIntent.MoveDown;
                default: return FullSearchKeyIntent.None;
            }
        }

        internal FullSearchWindow(FullSearchData data, string root)
        {
            _data = data;
            _root = root;

            double w, h;
            LoadSize(root, out w, out h);
            _loadedWidth = w;
            _loadedHeight = h;

            Title = "全库搜索 · RunEver";
            Width = w;
            Height = h;
            MinWidth = MinWidthLimit;
            MinHeight = MinHeightLimit;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            FontSize = 13;
            Background = SystemColors.WindowBrush;

            Content = BuildLayout();

            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += delegate
            {
                PositionNearCursor();
                _box.Focus();
                _box.CaretIndex = _box.Text.Length;
                BringToFront();
            };
            Closed += delegate { SaveSizeIfChanged(); };

            RefreshRows();
        }

        // ============================================================
        // 布局
        // ============================================================
        private FrameworkElement BuildLayout()
        {
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            _box.Margin = new Thickness(10, 10, 10, 6);
            _box.Padding = new Thickness(6, 4, 6, 4);
            _box.FontSize = 15;
            _box.BorderBrush = SystemColors.ControlDarkBrush;
            _box.TextChanged += delegate { RefreshRows(); };
            Grid.SetRow(_box, 0);
            grid.Children.Add(_box);

            _list.BorderThickness = new Thickness(0);
            _list.Margin = new Thickness(4, 0, 4, 0);
            _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            _list.ItemContainerStyle = BuildItemStyle();
            _list.ItemTemplate = BuildItemTemplate();
            _list.MouseDoubleClick += delegate { ExecuteSelected(); };
            _list.SelectionChanged += delegate { EnsureSelectable(); };
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            VirtualizingStackPanel.SetIsVirtualizing(_list, true);
            Grid.SetRow(_list, 1);
            grid.Children.Add(_list);

            _hint.Margin = new Thickness(12, 4, 12, 8);
            _hint.Foreground = Brushes.Gray;
            _hint.TextTrimming = TextTrimming.CharacterEllipsis;
            Grid.SetRow(_hint, 2);
            grid.Children.Add(_hint);

            return grid;
        }

        /// <summary>
        /// 列表项**容器模板**：一个 Border + ContentPresenter。
        /// ★ 为什么自己写：默认模板把"选中/未聚焦"的底色直接设在内部 Border 上
        ///   （模板触发器优先于我们设的 Background）⇒ 焦点在搜索框时当前行根本看不出被选中，
        ///   用户不知道回车会执行哪一条。自建模板后 Background 完全由样式决定。
        /// </summary>
        private static ControlTemplate BuildContainerTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border), "Bd");
            border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
            border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            border.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
            border.AppendChild(content);

            return new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        }

        /// <summary>列表项样式：节头行 disabled（不可点/不可选）+ 加粗灰字 + 上间距；当前行始终高亮。</summary>
        private static Style BuildItemStyle()
        {
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, BuildContainerTemplate()));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            // 节头 ⇒ IsSelectable=false ⇒ disabled：鼠标点不中、键盘也不会停在上面
            style.Setters.Add(new Setter(UIElement.IsEnabledProperty, new Binding("IsSelectable")));

            // ★ 当前行**不依赖键盘焦点**也高亮：焦点在搜索框里（用户正在打字），
            //   用户必须能一眼看到"现在回车会执行哪一条"。
            var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.HighlightBrush));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.HighlightTextBrush));
            style.Triggers.Add(selected);

            var header = new DataTrigger { Binding = new Binding("IsHeader"), Value = true };
            header.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            header.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.DimGray));
            header.Setters.Add(new Setter(Control.MarginProperty, new Thickness(0, 8, 0, 2)));
            style.Triggers.Add(header);
            return style;
        }

        /// <summary>列表项模板：左边标题、右边淡字目标（都带省略号，长路径不撑破窗口）。</summary>
        private static DataTemplate BuildItemTemplate()
        {
            var panel = new FrameworkElementFactory(typeof(DockPanel));

            // ★ 目标文字**不指定颜色**：让它继承行前景色（选中时自动变白），
            //   只压低透明度 —— 否则选中行会出现"蓝底灰字"的低对比。
            var target = new FrameworkElementFactory(typeof(TextBlock));
            target.SetValue(DockPanel.DockProperty, Dock.Right);
            target.SetValue(TextBlock.TextProperty, new Binding("DisplayTarget"));
            target.SetValue(UIElement.OpacityProperty, 0.6);
            target.SetValue(TextBlock.MarginProperty, new Thickness(12, 0, 0, 0));
            target.SetValue(TextBlock.MaxWidthProperty, 200.0);
            target.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            target.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            panel.AppendChild(target);

            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetValue(TextBlock.TextProperty, new Binding("DisplayTitle"));
            title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            title.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            panel.AppendChild(title);

            return new DataTemplate { VisualTree = panel };
        }

        // ============================================================
        // 交互
        // ============================================================
        private void RefreshRows()
        {
            var rows = _data.Query(_box.Text);
            _list.ItemsSource = rows;

            int items = 0;
            foreach (var r in rows) if (r.IsSelectable) items++;

            if (items == 0)
            {
                _hint.Text = (_box.Text ?? "").Trim().Length == 0
                    ? "没有可执行的条目（可能全被『隐藏本机没有的条目』隐藏了）· Esc 关闭"
                    : "无匹配 · Esc 关闭";
            }
            else
            {
                _hint.Text = "共 " + items + " 条（" + _data.SectionCount + " 个分组）"
                    + " · 名称/拼音均可搜 · ↑↓ 选择 · Enter 执行 · Esc 关闭";
            }

            SelectFirstSelectable();
        }

        private void SelectFirstSelectable()
        {
            _selecting = true;
            try
            {
                for (int i = 0; i < _list.Items.Count; i++)
                {
                    var row = _list.Items[i] as FullSearchRow;
                    if (row != null && row.IsSelectable)
                    {
                        _list.SelectedIndex = i;
                        _list.ScrollIntoView(row);
                        return;
                    }
                }
                _list.SelectedIndex = -1;
            }
            finally { _selecting = false; }
        }

        /// <summary>手选落到节头上时，往下一个可选行挪一格（节头永远不是"当前项"）。</summary>
        private void EnsureSelectable()
        {
            if (_selecting) return;
            var row = _list.SelectedItem as FullSearchRow;
            if (row == null || row.IsSelectable) return;
            Move(1);
        }

        private void Move(int delta)
        {
            int n = _list.Items.Count;
            if (n == 0) return;

            int i = _list.SelectedIndex;
            for (int step = 0; step < n; step++)
            {
                int j = i + delta;
                i = j;
                if (j < 0 || j >= n) return;      // 到边界就停（不环绕，与菜单手感一致）
                var row = _list.Items[j] as FullSearchRow;
                if (row != null && row.IsSelectable)
                {
                    _list.SelectedIndex = j;
                    _list.ScrollIntoView(row);
                    return;
                }
            }
        }

        private void ExecuteSelected()
        {
            var row = _list.SelectedItem as FullSearchRow;
            if (row == null || !row.IsSelectable) return;
            _picked = row.Item;
            Close();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (ResolveKey(e.Key))
            {
                case FullSearchKeyIntent.Cancel:
                    e.Handled = true;
                    Close();
                    break;
                case FullSearchKeyIntent.Accept:
                    e.Handled = true;
                    ExecuteSelected();
                    break;
                case FullSearchKeyIntent.MoveUp:
                    e.Handled = true;
                    Move(-1);
                    break;
                case FullSearchKeyIntent.MoveDown:
                    e.Handled = true;
                    Move(1);
                    break;
            }
        }

        // ============================================================
        // 位置 / 尺寸记忆
        // ============================================================
        private void PositionNearCursor()
        {
            try
            {
                var p = System.Windows.Forms.Cursor.Position;
                var screen = System.Windows.Forms.Screen.FromPoint(p);
                var wa = screen.WorkingArea;

                double s = DpiScale();
                double w = double.IsNaN(Width) ? DefaultWidth : Width;
                double h = double.IsNaN(Height) ? DefaultHeight : Height;

                double minX = wa.Left / s;
                double minY = wa.Top / s;
                double maxX = wa.Right / s - w - 8;
                double maxY = wa.Bottom / s - h - 8;

                Left = Clamp(p.X / s, minX, Math.Max(minX, maxX));
                Top = Clamp(p.Y / s, minY, Math.Max(minY, maxY));
            }
            catch { }
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>当前屏幕缩放（WPF 的坐标是 DIP，鼠标坐标是物理像素）。取不到就当 1.0。</summary>
        private static double DpiScale()
        {
            try
            {
                using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
                {
                    if (g != null && g.DpiX > 0) return g.DpiX / 96.0;
                }
            }
            catch { }
            return 1.0;
        }

        private static string SizeFile(string root)
        {
            if (string.IsNullOrEmpty(root)) return "";
            return Path.Combine(root, "state", "fullsearch-window.txt");
        }

        /// <summary>读上次的窗口尺寸（坏文件/缺文件 ⇒ 480×400）。**只读 state\，不碰 config\**。</summary>
        private static void LoadSize(string root, out double w, out double h)
        {
            w = DefaultWidth;
            h = DefaultHeight;
            string f = SizeFile(root);
            if (f.Length == 0 || !File.Exists(f)) return;
            try
            {
                foreach (string raw in File.ReadAllLines(f))
                {
                    string line = (raw ?? "").Trim();
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    double v;
                    if (!double.TryParse(line.Substring(eq + 1).Trim(), out v)) continue;
                    if (key == "w" && v >= MinWidthLimit && v <= 4000) w = v;
                    if (key == "h" && v >= MinHeightLimit && v <= 3000) h = v;
                }
            }
            catch { }
        }

        private void SaveSizeIfChanged()
        {
            try
            {
                double w = Width, h = Height;
                if (double.IsNaN(w) || double.IsNaN(h)) return;
                if (Math.Abs(w - _loadedWidth) < 0.5 && Math.Abs(h - _loadedHeight) < 0.5) return;

                string f = SizeFile(_root);
                if (f.Length == 0) return;
                string dir = Path.GetDirectoryName(f);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(f,
                    "w=" + ((int)Math.Round(w)) + Environment.NewLine
                    + "h=" + ((int)Math.Round(h)) + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>
        /// 关闭即释放：断开数据源/模板/可视树。窗口对象本身随后由调用方丢弃（无静态引用）。
        /// ★ 幂等：异常一律吞掉 —— 释放阶段绝不能把关闭流程卡住。
        /// </summary>
        public void Release()
        {
            try
            {
                _list.ItemsSource = null;
                _list.ItemTemplate = null;
                _list.ItemContainerStyle = null;
                Content = null;
            }
            catch { }
        }
    }
}
