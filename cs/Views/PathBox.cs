using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RunEverCs.Views
{
    /// <summary>
    /// 带「浏览…」按钮的输入框 —— 用于需要选择文件/目录/程序的字段。
    ///
    /// ★ 用户反馈："新建编辑时的路径没有快速选择；本来可以做快速选择的，
    ///   打开选择路径、填入之类的；类似的可以做快速选择的位置都做快速选择。"
    ///
    /// 支持三种模式：
    ///   File      选文件（带过滤器，如 *.exe）
    ///   Folder    选目录
    ///   PathAny   自动判断（有扩展名按文件选，否则按目录选）
    /// </summary>
    public class PathBox : UserControl
    {
        public enum Mode { File, Folder, PathAny }

        public TextBox Box { get; private set; }
        public event Action Changed;

        private readonly Mode _mode;
        private readonly string _filter;
        private readonly string _title;

        /// <summary>额外候选（下拉可选，如常用程序）。</summary>
        public ComboBox Candidates { get; private set; }

        public PathBox(Mode mode, string filter = null, string title = "选择",
            string[] candidates = null)
        {
            _mode = mode;
            _filter = filter;
            _title = title;

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Box = new TextBox
            {
                Padding = new Thickness(4, 3, 4, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            Box.TextChanged += (s, e) => { var h = Changed; if (h != null) h(); };
            Grid.SetColumn(Box, 0);
            g.Children.Add(Box);

            var btn = new Button
            {
                Content = "浏览…",
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(4, 0, 0, 0),
                MinWidth = 62
            };
            btn.Click += (s, e) => Browse();
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);

            var outer = new StackPanel();
            outer.Children.Add(g);

            // 候选下拉（可选）
            if (candidates != null && candidates.Length > 0)
            {
                Candidates = new ComboBox
                {
                    Margin = new Thickness(0, 3, 0, 0),
                    ToolTip = "常用候选，选择即填入"
                };
                Candidates.Items.Add("（从常用列表选择…）");
                foreach (var c in candidates) Candidates.Items.Add(c);
                Candidates.SelectedIndex = 0;
                Candidates.SelectionChanged += (s, e) =>
                {
                    if (Candidates.SelectedIndex > 0)
                    {
                        Box.Text = Candidates.SelectedItem.ToString();
                        Candidates.SelectedIndex = 0;
                    }
                };
                outer.Children.Add(Candidates);
            }

            Content = outer;
        }

        public string Text
        {
            get { return Box.Text; }
            set { Box.Text = value ?? ""; }
        }

        private void Browse()
        {
            try
            {
                Mode m = _mode;
                if (m == Mode.PathAny)
                {
                    // 已有内容且像文件路径 → 按文件选；否则按目录选
                    string cur = Box.Text.Trim().Trim('"');
                    bool looksLikeFile = cur.Length > 0
                        && System.IO.Path.HasExtension(cur)
                        && cur.IndexOf(' ') < 0;
                    m = looksLikeFile ? Mode.File : Mode.Folder;
                }

                if (m == Mode.Folder)
                {
                    // ★ 2026-10-06：目录选择改用**系统默认（Vista+）窗口**。
                    //   原实现走 WinForms `FolderBrowserDialog`，net48 下它就是
                    //   **老式树形文件夹对话框**（用户实报「不要用目前的这种树形」）。
                    string cur = Box.Text.Trim().Trim('"');
                    string picked = RunEverCs.Native.ModernFolderPicker.Pick(_title, cur);
                    if (!string.IsNullOrEmpty(picked))
                        Box.Text = picked;
                }
                else
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = _title,
                        Filter = string.IsNullOrEmpty(_filter)
                            ? "所有文件 (*.*)|*.*" : _filter
                    };
                    string cur = Box.Text.Trim().Trim('"');
                    try
                    {
                        if (cur.Length > 0 && System.IO.File.Exists(cur))
                            dlg.FileName = cur;
                    }
                    catch { }

                    if (dlg.ShowDialog() == true)
                        Box.Text = dlg.FileName;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("选择失败：" + ex.Message, "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
