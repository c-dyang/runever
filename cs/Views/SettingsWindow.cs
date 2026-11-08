using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RunEverCs.Views
{
    /// <summary>
    /// 设置窗口（2026-11-07 全面重建）。
    ///
    /// 旧版：4 个顶部 Tab，其中「常规」一页塞 6 个 GroupBox。用户评价「太丑且难用」。
    ///
    /// 新版按微软官方《Guidelines for app settings》与 Windows 11 设置页的实际结构：
    ///   · **左侧导航列表 + 右侧内容区**（顶部 Tab 超过 4 个就开始横着挤，且无法分组）；
    ///   · 内容区单列、最大宽度 1000px、灰底白卡；
    ///   · 每页只讲一个主题：菜单与条目 / 触发与行为 / 外观 / 路径解析 / 同步 / 插件 / 日志与诊断 / 关于；
    ///   · 页面**懒加载**（点才建），避免一次构造 8 页拖慢开窗。
    /// </summary>
    public class SettingsWindow : Window
    {
        private readonly string _root;
        private readonly Action _afterConfigSaved;
        private readonly Action _afterAutoConfSaved;

        private MenuEditorPage _menuPage;
        private ListBox _nav;
        private ContentControl _host;
        private readonly Dictionary<string, FrameworkElement> _pages = new Dictionary<string, FrameworkElement>();
        private readonly List<AutoConfPage> _autoConfPages = new List<AutoConfPage>();

        public SettingsWindow(string root) : this(root, null, null) { }
        public SettingsWindow(string root, Action afterConfigSaved) : this(root, afterConfigSaved, null) { }

        public SettingsWindow(string root, Action afterConfigSaved, Action afterAutoConfSaved)
        {
            _root = root;
            _afterConfigSaved = afterConfigSaved;
            _afterAutoConfSaved = afterAutoConfSaved;

            // 窗口尺寸：左导航 196 + 内容区要装得下「菜单与条目」的三栏
            // （分组 235 + 条目 Star + 字段 340）⇒ 内容区至少 ~900，故总宽 1280。
            // 官方口径内容最大宽度 1000-1100，扣掉导航后恰好落在区间内。
            Title = "RunEver 设置";
            Width = 1280;
            Height = 780;
            MinWidth = 1000;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = SettingsKit.PageBg;

            try
            {
                string ico = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "assets", "runever.ico");
                if (System.IO.File.Exists(ico))
                    Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(ico));
            }
            catch { }

            // ---- 左：导航 ----
            _nav = new ListBox
            {
                Width = 196,
                BorderThickness = new Thickness(0, 0, 1, 0),
                BorderBrush = SettingsKit.CardBorder,
                Background = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFB)),
                Padding = new Thickness(0, 8, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            _nav.ItemContainerStyle = BuildNavStyle();
            _nav.SelectionChanged += (s, e) => ShowSelected();

            AddNav("菜单与条目", "菜单与条目");
            AddNav("触发与行为", "触发与行为");
            AddNav("路径解析", "路径解析");
            AddNav("同步", "同步");
            AddNav("插件", "插件");
            AddNav("日志与诊断", "日志与诊断");
            AddNav("关于", "关于");

            // ---- 右：内容 ----
            _host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };

            // ★★ 2026-11-07 关键修正：必须包一层 ScrollViewer（横向 Disabled）。
            //   WPF 的 Grid Star 列分配依据是「剩余空间」**和**「子元素 DesiredWidth」取大者。
            //   MenuEditorPage 里条目树的文本很长（"LockHunter64 _L · run · LockHunter64.exe"），
            //   把 UserControl 的 DesiredWidth 撑到 1495px ⇒ Star 列跟着取 1280，
            //   结果第三栏「字段编辑」被推出窗口右边界（截图实测）。
            //   ScrollViewer(Horizontal=Disabled) 会以**可用宽度**作为子元素的约束宽度，
            //   从而让三日栏老老实实按 235 / Star / 340 分配。
            var hostScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _host
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_nav, 0);
            grid.Children.Add(_nav);
            Grid.SetColumn(hostScroll, 1);
            grid.Children.Add(hostScroll);

            Content = grid;

            Closing += (s, e) =>
            {
                if (_menuPage != null && _menuPage.HasUnsaved)
                {
                    var r = MessageBox.Show(
                        "菜单项有未保存的修改，确定关闭吗？",
                        "提示", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
                }
                foreach (var p in _autoConfPages)
                {
                    if (!string.IsNullOrEmpty(p.LastWriteStatus) && p.LastWriteStatus.StartsWith("保存失败"))
                    {
                        var r2 = MessageBox.Show(
                            "有设置**没有成功写入** auto.conf：\n\n" + p.LastWriteStatus
                            + "\n\n仍要关闭吗？", "保存未成功",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning);
                        if (r2 != MessageBoxResult.Yes) e.Cancel = true;
                        break;
                    }
                }
            };

            Loaded += (s, e) => { if (_nav.Items.Count > 0) _nav.SelectedIndex = 0; };
        }

        private Style BuildNavStyle()
        {
            var st = new Style(typeof(ListBoxItem));
            st.Setters.Add(new Setter(PaddingProperty, new Thickness(18, 9, 18, 9)));
            st.Setters.Add(new Setter(MarginProperty, new Thickness(6, 1, 6, 1)));
            st.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Left));

            var trig = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            trig.Setters.Add(new Setter(BackgroundProperty, SettingsKit.NavSelected));
            trig.Setters.Add(new Setter(ForegroundProperty, SettingsKit.Accent));
            trig.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
            st.Triggers.Add(trig);

            return st;
        }

        private void AddNav(string label, string key)
        {
            _nav.Items.Add(new ListBoxItem { Content = label, Tag = key });
        }

        private void ShowSelected()
        {
            var item = _nav.SelectedItem as ListBoxItem;
            if (item == null) return;
            string key = item.Tag as string ?? "";

            FrameworkElement page;
            if (!_pages.TryGetValue(key, out page))
            {
                page = CreatePage(key);
                if (page == null) return;
                _pages[key] = page;
            }
            _host.Content = page;
        }

        private FrameworkElement CreatePage(string key)
        {
            switch (key)
            {
                case "菜单与条目":
                    _menuPage = new MenuEditorPage(_root);
                    _menuPage.AfterSave = _afterConfigSaved;
                    return _menuPage;

                case "插件":
                    return new PluginPage(_root);

                case "触发与行为":
                    {
                        var p = new TriggerPage(_root); Hook(p); return p;
                    }
                case "路径解析":
                    {
                        var p = new PathResolvePage(_root); Hook(p); return p;
                    }
                case "同步":
                    {
                        var p = new SyncPage(_root); Hook(p); return p;
                    }
                case "日志与诊断":
                    {
                        var p = new DiagnosticsPage(_root); Hook(p); return p;
                    }
                case "关于":
                    return new AboutPage(_root);
            }
            return null;
        }

        private void Hook(AutoConfPage p)
        {
            p.AfterSave = _afterAutoConfSaved;
            _autoConfPages.Add(p);
        }
    }
}
