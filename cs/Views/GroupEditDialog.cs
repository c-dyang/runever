using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RunEverCs.Models;

namespace RunEverCs.Views
{
    /// <summary>
    /// 分组新建/编辑对话框 —— 名称 + 类型 + 关键词。
    ///
    /// ★ 修复记录（2026-10-03 用户反馈"分组依据编辑和显示不全"）：
    ///   旧版三个缺陷：
    ///     ① 关键词输入框初始为空 —— 编辑已有分组时看不到现有关键词
    ///     ② 关键词从不回写 —— 调用方没读 Keywords，改了等于没改
    ///     ③ 类型下拉项与关键词框无联动 —— 选 ext 时不知道要填什么
    ///   现在：关键词双向绑定 + 类型切换时给实时提示。
    /// </summary>
    public class GroupEditDialog : Window
    {
        public string GroupName { get; private set; }
        public GroupType GroupType { get; private set; }
        public List<string> GroupKeywords { get; private set; }

        private readonly TextBox _name;
        private readonly ComboBox _type;
        private readonly TextBox _kw;
        private readonly TextBlock _hint;
        private readonly TextBlock _error;

        public GroupEditDialog(string name, GroupType type, List<string> keywords = null)
        {
            Title = string.IsNullOrEmpty(name) ? "新建分组" : "编辑分组";
            // 2026-11-07 统一视觉：与设置页同一套卡片语言
            Width = 560;
            Background = SettingsKit.PageBg;
            Height = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;

            GroupKeywords = new List<string>();

            var sp = new StackPanel { Margin = new Thickness(18) };

            // ---- 分组名 ----
            sp.Children.Add(Lbl("分组名"));
            _name = new TextBox
            {
                Text = name ?? "",
                Padding = new Thickness(5, 4, 5, 4),
                Margin = new Thickness(0, 3, 0, 12),
                FontSize = 13
            };
            sp.Children.Add(_name);

            // ---- 类型 ----
            sp.Children.Add(Lbl("分组依据（类型）"));
            _type = new ComboBox
            {
                Margin = new Thickness(0, 3, 0, 4),
                Padding = new Thickness(5, 3, 5, 3)
            };
            _type.Items.Add("always    — 永久显示（无条件）");
            _type.Items.Add("fallback  — 兜底（其他都没命中时才显示）");
            _type.Items.Add("folder    — 选中文件夹时显示");
            _type.Items.Add("ext       — 按扩展名匹配（需填关键词）");
            _type.Items.Add("proc      — 按前台进程匹配（需填关键词）");
            _type.Items.Add("text      — 有选中文本时显示");
            _type.SelectedIndex = TypeIndex(type);
            _type.SelectionChanged += (s, e) => UpdateHint();
            sp.Children.Add(_type);

            // ---- 关键词 ----
            _hint = new TextBlock
            {
                Text = "关键词",
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 0)
            };
            sp.Children.Add(_hint);

            _kw = new TextBox
            {
                Text = keywords == null ? "" : string.Join(" ", keywords),
                Padding = new Thickness(5, 4, 5, 4),
                Margin = new Thickness(0, 3, 0, 2),
                FontFamily = new FontFamily("Consolas")
            };
            sp.Children.Add(_kw);

            var example = new TextBlock
            {
                Text = "ext 例：pdf txt md log    proc 例：chrome msedge firefox",
                FontSize = 11,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            sp.Children.Add(example);

            _error = new TextBlock
            {
                Foreground = Brushes.Firebrick,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                MinHeight = 18
            };
            sp.Children.Add(_error);

            // ---- 按钮 ----
            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };
            var ok = new Button
            {
                Content = "确定",
                Width = 96,
                Height = 30,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 0),
                Background = SettingsKit.Accent,
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                IsDefault = true
            };
            ok.Click += OnOk;
            var cancel = new Button { Content = "取消", Width = 90, IsCancel = true };
            btns.Children.Add(ok);
            btns.Children.Add(cancel);
            sp.Children.Add(btns);

            Content = sp;
            UpdateHint();
        }

        /// <summary>按类型启用/禁用关键词框，并给出对应说明。</summary>
        private void UpdateHint()
        {
            var t = IndexType(_type.SelectedIndex);
            bool needKw = (t == GroupType.Ext || t == GroupType.Proc);

            _kw.IsEnabled = needKw;
            if (needKw)
            {
                _hint.Text = t == GroupType.Ext
                    ? "扩展名关键词（空格分隔，小写，不含点）— 必需"
                    : "进程名关键词（空格分隔）— 必需";
                _hint.Foreground = Brushes.Black;
            }
            else if (t == GroupType.Folder)
            {
                _hint.Text = "关键词（folder 类型不需要，留空即可）";
                _hint.Foreground = Brushes.Gray;
            }
            else
            {
                _hint.Text = "关键词（本类型不需要，留空即可）";
                _hint.Foreground = Brushes.Gray;
            }
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            string nm = _name.Text.Trim();
            if (nm.Length == 0)
            {
                _error.Text = "分组名不能为空";
                return;
            }

            var t = IndexType(_type.SelectedIndex);
            var kws = new List<string>();
            foreach (var part in _kw.Text.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries))
                kws.Add(part.Trim().ToLowerInvariant());

            if ((t == GroupType.Ext || t == GroupType.Proc) && kws.Count == 0)
            {
                _error.Text = t == GroupType.Ext
                    ? "ext 类型必须至少填一个扩展名（例：pdf txt）"
                    : "proc 类型必须至少填一个进程名（例：chrome msedge）";
                return;
            }

            // 类型与关键词不匹配时给警告但不阻止（用户可能有意）
            GroupName = nm;
            GroupType = t;
            GroupKeywords = kws;
            DialogResult = true;
        }

        private TextBlock Lbl(string t)
        {
            return new TextBlock
            {
                Text = t,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 4, 0, 0)
            };
        }

        private static int TypeIndex(GroupType t)
        {
            switch (t)
            {
                case GroupType.Fallback: return 1;
                case GroupType.Folder: return 2;
                case GroupType.Ext: return 3;
                case GroupType.Proc: return 4;
                case GroupType.Text: return 5;
                default: return 0;
            }
        }

        private static GroupType IndexType(int i)
        {
            switch (i)
            {
                case 1: return GroupType.Fallback;
                case 2: return GroupType.Folder;
                case 3: return GroupType.Ext;
                case 4: return GroupType.Proc;
                case 5: return GroupType.Text;
                default: return GroupType.Always;
            }
        }
    }
}
