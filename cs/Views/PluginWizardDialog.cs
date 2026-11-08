using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    /// <summary>
    /// 插件模板向导 —— 「填表 → 生成可用插件」。
    ///
    /// 四种模板：
    ///   1. 匹配文本 → 执行命令（含"是否显示 cmd 窗口"）
    ///   2. 匹配文本 → 打开网址（URL 模板带 {0} 变量）
    ///   3. 匹配文本 → 打开路径
    ///   4. 空壳（自己写逻辑）
    /// </summary>
    public class PluginWizardDialog : Window
    {
        public string CreatedPath { get; private set; }

        private readonly string _pluginDir;
        private readonly ComboBox _kind;
        private readonly TextBox _name, _match, _prio, _icon;
        private readonly CheckBox _whole, _showWin;
        private readonly TextBox _command, _urlTpl;
        private readonly TextBox _preview;
        private readonly StackPanel _cmdPanel, _urlPanel;
        private readonly TextBlock _error;

        public PluginWizardDialog(string root)
        {
            _pluginDir = Path.Combine(root, "config", "functions");

            Title = "新建插件（模板向导）";
            Width = 720;
            Height = 660;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var sp = new StackPanel { Margin = new Thickness(16) };

            // ---- 模板选择 ----
            sp.Children.Add(Lbl("模板类型"));
            _kind = new ComboBox { Margin = new Thickness(0, 3, 0, 10) };
            _kind.Items.Add("① 匹配文本 → 执行命令");
            _kind.Items.Add("② 匹配文本 → 打开网址");
            _kind.Items.Add("③ 匹配文本 → 打开路径");
            _kind.Items.Add("④ 空壳（自己写逻辑）");
            _kind.SelectedIndex = 0;
            _kind.SelectionChanged += (s, e) => UpdatePanels();
            sp.Children.Add(_kind);

            // ---- 通用字段 ----
            var common = new Grid();
            common.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            common.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            common.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var c1 = new StackPanel();
            c1.Children.Add(Lbl("插件名（显示名，必需）"));
            _name = Edit("");
            _name.TextChanged += (s, e) => UpdatePreview();
            c1.Children.Add(_name);
            Grid.SetColumn(c1, 0);

            var c2 = new StackPanel();
            c2.Children.Add(Lbl("优先级（大者先匹配，默认 100）"));
            _prio = Edit("100");
            _prio.TextChanged += (s, e) => UpdatePreview();
            c2.Children.Add(_prio);
            Grid.SetColumn(c2, 2);

            common.Children.Add(c1);
            common.Children.Add(c2);
            sp.Children.Add(common);

            sp.Children.Add(Lbl("匹配式（正则；整段匹配时自动加 ^ $）"));
            _match = Edit("");
            _match.TextChanged += (s, e) => UpdatePreview();
            _match.FontFamily = new FontFamily("Consolas");
            sp.Children.Add(_match);

            var hints = new TextBlock
            {
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 2),
                Text = "示例：  顺丰单号  ^SF\\d{10,}$        网址  ^https?://\\S+$        "
                     + "choco 命令  ^choco\\s+(install|upgrade)\\b"
            };
            sp.Children.Add(hints);

            // ---- 命令模板 ----
            _cmdPanel = new StackPanel();
            _cmdPanel.Children.Add(Lbl("执行命令（{0} = 选中的文本）"));
            _command = Edit("choco install {0}");
            _command.FontFamily = new FontFamily("Consolas");
            _command.TextChanged += (s, e) => UpdatePreview();
            _cmdPanel.Children.Add(_command);

            _showWin = new CheckBox
            {
                Content = "显示命令窗口（命令行工具看输出/进度时勾选）",
                Margin = new Thickness(0, 6, 0, 0)
            };
            _showWin.Checked += (s, e) => UpdatePreview();
            _showWin.Unchecked += (s, e) => UpdatePreview();
            _cmdPanel.Children.Add(_showWin);
            sp.Children.Add(_cmdPanel);

            // ---- URL 模板 ----
            _urlPanel = new StackPanel();
            _urlPanel.Children.Add(Lbl("URL 模板（{0} = 选中的文本）"));
            _urlTpl = Edit("https://www.example.com/search?q={0}");
            _urlTpl.FontFamily = new FontFamily("Consolas");
            _urlTpl.TextChanged += (s, e) => UpdatePreview();
            _urlPanel.Children.Add(_urlTpl);
            sp.Children.Add(_urlPanel);

            // ---- 图标 ----
            sp.Children.Add(Lbl("图标源（可空，例：chrome.exe 或 C:\\path\\app.exe）"));
            _icon = Edit("");
            _icon.TextChanged += (s, e) => UpdatePreview();
            sp.Children.Add(_icon);

            // ---- 整段匹配 ----
            _whole = new CheckBox
            {
                Content = "整段匹配（选中文本必须完全符合正则；取消则允许包含）",
                IsChecked = true,
                Margin = new Thickness(0, 8, 0, 4)
            };
            _whole.Checked += (s, e) => UpdatePreview();
            _whole.Unchecked += (s, e) => UpdatePreview();
            sp.Children.Add(_whole);

            // ---- 预览 ----
            sp.Children.Add(Lbl("生成的插件源码（预览）"));
            _preview = new TextBox
            {
                IsReadOnly = true,
                Height = 150,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                FontSize = 11.5,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = new SolidColorBrush(Color.FromRgb(250, 250, 250))
            };
            sp.Children.Add(_preview);

            _error = new TextBlock
            {
                Foreground = Brushes.Firebrick,
                Margin = new Thickness(0, 6, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            sp.Children.Add(_error);

            // ---- 按钮 ----
            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var ok = new Button
            {
                Content = "生成插件",
                Width = 110,
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                FontWeight = FontWeights.Bold
            };
            ok.Click += OnCreate;
            var cancel = new Button { Content = "取消", Width = 80, IsCancel = true };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            sp.Children.Add(btns);

            Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            UpdatePanels();
        }

        // ============================================================
        private void UpdatePanels()
        {
            int k = _kind.SelectedIndex;
            _cmdPanel.Visibility = (k == 0) ? Visibility.Visible : Visibility.Collapsed;
            _urlPanel.Visibility = (k == 1) ? Visibility.Visible : Visibility.Collapsed;

            // 默认图标随类型变
            if (string.IsNullOrWhiteSpace(_icon.Text))
            {
                if (k == 1) _icon.Text = "chrome.exe";
                else if (k == 2) _icon.Text = "explorer.exe";
            }
            UpdatePreview();
        }

        private PluginTemplate.Spec MakeSpec()
        {
            var s = new PluginTemplate.Spec();
            int k = _kind.SelectedIndex;
            s.Kind = k == 1 ? PluginTemplate.Kind.OpenUrl
                : (k == 2 ? PluginTemplate.Kind.OpenPath
                : (k == 3 ? PluginTemplate.Kind.Custom : PluginTemplate.Kind.CmdRun));

            s.Name = _name.Text.Trim();
            s.Match = _match.Text.Trim();
            s.Whole = _whole.IsChecked == true;
            int pr;
            s.Prio = int.TryParse(_prio.Text.Trim(), out pr) ? pr : 100;
            s.Icon = _icon.Text.Trim();
            s.Command = _command.Text.Trim();
            s.UrlTemplate = _urlTpl.Text.Trim();
            s.PathTemplate = "{0}";
            s.ShowWindow = _showWin.IsChecked == true;
            return s;
        }

        private void UpdatePreview()
        {
            try
            {
                _preview.Text = PluginTemplate.Generate(MakeSpec());
                _error.Text = "";
            }
            catch (Exception ex)
            {
                _preview.Text = "(预览失败)";
                _error.Text = ex.Message;
            }
        }

        private void OnCreate(object sender, RoutedEventArgs e)
        {
            var s = MakeSpec();
            if (string.IsNullOrWhiteSpace(s.Name))
            {
                _error.Text = "插件名不能为空";
                return;
            }
            if (string.IsNullOrWhiteSpace(s.Match))
            {
                _error.Text = "匹配式不能为空";
                return;
            }

            // 正则合法性预检（避免生成一个永远匹配不上的插件）
            try
            {
                string pat = s.Match;
                if (s.Whole && !pat.StartsWith("^")) pat = "^" + pat;
                if (s.Whole && !pat.EndsWith("$")) pat = pat + "$";
                new System.Text.RegularExpressions.Regex(pat);
            }
            catch (Exception ex)
            {
                _error.Text = "匹配式不是合法正则：" + ex.Message;
                return;
            }

            string err;
            string path = PluginTemplate.Write(_pluginDir, s, out err);
            if (string.IsNullOrEmpty(path))
            {
                _error.Text = err;
                return;
            }

            CreatedPath = path;
            MessageBox.Show(
                "已生成插件：\n" + Path.GetFileName(path)
                + "\n\n位置：" + _pluginDir
                + "\n\n立即可用（选中匹配的文本 → 按热键）。",
                "生成成功", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
        }

        // ============================================================
        private TextBlock Lbl(string t)
        {
            return new TextBlock
            {
                Text = t,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 0)
            };
        }

        private TextBox Edit(string v)
        {
            return new TextBox
            {
                Text = v ?? "",
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(0, 3, 0, 4)
            };
        }
    }
}
