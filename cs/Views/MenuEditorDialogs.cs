using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    /// <summary>
    /// 保存前的结构校验窗（C1）。
    ///
    /// 为什么不能只弹一句"结构错误"：用户改不动。每行必须是
    /// **「第 N 行 / 条目 X：原因」**，且双击能直接跳到那条目。
    ///
    /// 按钮语义（严格二分，与「要么整份写成功、要么原文件一字节不动」对应）：
    ///   · 继续编辑 → 关窗，留在编辑器里改（磁盘没被改过）
    ///   · 放弃修改 → 从磁盘重读，把内存里的改动整体丢掉
    /// 无「仍然保存」按钮 —— 那等于把闸拆了。
    /// </summary>
    internal sealed class MenuValidationDialog : Window
    {
        private readonly ListBox _list = new ListBox();
        private readonly string _hint;

        /// <summary>双击选中的那一条（null = 没双击）。</summary>
        public MenuStructureIssue SelectedIssue;

        public string HintText { get { return _hint; } }

        public MenuValidationDialog(List<MenuStructureIssue> issues)
        {
            int n = (issues == null) ? 0 : issues.Count;

            Title = "保存前的结构校验没通过（" + n + " 处）—— 未写盘";
            Width = 720;
            Height = 420;
            MinWidth = 520;
            MinHeight = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            Background = Brushes.White;

            _hint = "这些结构问题会让菜单读回来时错位（或直接读不回来），所以本次没有写盘 ——"
                + " menus.conf 一个字节都没动。\n"
                + "双击某一行可直接跳到该条目去改；「放弃修改」会丢掉本次改动并从磁盘重读。";

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            var head = new TextBlock
            {
                Text = "有 " + n + " 处结构问题，本次保存已取消",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.Firebrick,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            var hint = new TextBlock
            {
                Text = _hint,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SettingsKit.TextSecondary,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            _list.Margin = new Thickness(0, 0, 0, 12);
            _list.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            _list.BorderThickness = new Thickness(1);
            _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            if (issues != null)
                foreach (var iss in issues) _list.Items.Add(iss.ToString());
            // 双击 = 跳到该条目（跳完关窗，让用户就地改）
            _list.MouseDoubleClick += (s, e) =>
            {
                int i = _list.SelectedIndex;
                if (issues == null || i < 0 || i >= issues.Count) return;
                SelectedIssue = issues[i];
                DialogResult = false;   // "继续编辑"分支 —— 由调用方负责跳转（ShowDialog 会自动关窗）
            };
            Grid.SetRow(_list, 2);
            root.Children.Add(_list);

            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var keep = new Button
            {
                Content = "继续编辑",
                MinWidth = 96,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                IsCancel = true
            };
            keep.Click += (s, e) => { DialogResult = false; };
            bar.Children.Add(keep);

            var discard = new Button
            {
                Content = "放弃修改",
                MinWidth = 96,
                Padding = new Thickness(12, 4, 12, 4)
            };
            discard.Click += (s, e) => { DialogResult = true; };
            bar.Children.Add(discard);

            Grid.SetRow(bar, 3);
            root.Children.Add(bar);

            Content = root;
        }

        /// <summary>窗里逐条显示的文案（自检用来断言"行号 + 条目名 + 原因"齐备）。</summary>
        public List<string> IssueTexts()
        {
            var list = new List<string>();
            foreach (var o in _list.Items) list.Add(Convert.ToString(o));
            return list;
        }

        /// <summary>窗里的按钮文案（自检用来断言两个按钮都在）。</summary>
        public List<string> ButtonTexts()
        {
            var list = new List<string>();
            var root = Content as Grid;
            if (root == null) return list;
            foreach (var o in root.Children)
            {
                var bar = o as StackPanel;
                if (bar == null) continue;
                foreach (var b in bar.Children)
                {
                    var btn = b as Button;
                    if (btn != null) list.Add(Convert.ToString(btn.Content));
                }
            }
            return list;
        }
    }

    /// <summary>
    /// 「移动到分组… / 复制到分组…」的**目标分组选择窗**（C2）。
    ///
    /// 只做一件事：从候选分组里挑一个（单选列表 + 确认）。搬 / 拷的语义全在
    /// <see cref="MenuEditorPage.MoveSelectionToGroup"/>，这里**不碰模型** ——
    /// 弹窗只负责"把用户选的下标交回去"，这样自检才能用替身绕过模态框而
    /// 仍然驱动同一份搬运实现。
    ///
    /// 候选清单由 <see cref="MenuEditorPage.PickerLabels"/> 生成，**已排除当前分组**
    /// （组内调序用 ↑↓ / ⇥，组内复制用「复制条目」）。
    /// </summary>
    internal sealed class GroupPickDialog : Window
    {
        private readonly ListBox _list = new ListBox();
        private readonly List<int> _indices;
        private readonly string _hint;

        public GroupPickDialog(bool copy, List<string> labels, List<int> indices)
        {
            _indices = indices ?? new List<int>();
            string verb = copy ? "复制" : "移动";

            Title = verb + "到分组 —— 选一个目标分组";
            Width = 520;
            Height = 400;
            MinWidth = 380;
            MinHeight = 260;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            Background = Brushes.White;

            _hint = copy
                ? "复制件是新条目（新 ID）：标题 / 快捷键 / 动作 / 目标原样，原条目留在原分组不动。"
                : "整棵子树一起搬：落到目标分组后按该组的最外层重算缩进，源分组不会留下孤儿节点。";

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 标题
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 说明
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            var head = new TextBlock
            {
                Text = verb + "到哪个分组？（当前分组不在列表里）",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 6)
            };
            Grid.SetRow(head, 0);
            root.Children.Add(head);

            var hint = new TextBlock
            {
                Text = _hint,
                TextWrapping = TextWrapping.Wrap,
                Foreground = SettingsKit.TextSecondary,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(hint, 1);
            root.Children.Add(hint);

            _list.Margin = new Thickness(0, 0, 0, 12);
            _list.BorderBrush = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
            _list.BorderThickness = new Thickness(1);
            _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            if (labels != null) foreach (var s in labels) _list.Items.Add(s);
            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            // 双击 = 直接确认（列表类选择器的常见手势）
            _list.MouseDoubleClick += (s, e) =>
            {
                if (SelectedGroupIndex >= 0) DialogResult = true;
            };
            Grid.SetRow(_list, 2);
            root.Children.Add(_list);

            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var ok = new Button
            {
                Content = verb,
                MinWidth = 96,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true,
                IsEnabled = _list.Items.Count > 0
            };
            ok.Click += (s, e) => { if (SelectedGroupIndex >= 0) DialogResult = true; };
            bar.Children.Add(ok);

            var cancel = new Button
            {
                Content = "取消",
                MinWidth = 96,
                Padding = new Thickness(12, 4, 12, 4),
                IsCancel = true
            };
            cancel.Click += (s, e) => { DialogResult = false; };
            bar.Children.Add(cancel);

            Grid.SetRow(bar, 3);
            root.Children.Add(bar);

            Content = root;
        }

        /// <summary>用户选中的目标分组在 `_groups` 里的下标（-1 = 没选中）。</summary>
        public int SelectedGroupIndex
        {
            get
            {
                int i = _list.SelectedIndex;
                if (i < 0 || i >= _indices.Count) return -1;
                return _indices[i];
            }
        }

        /// <summary>窗里的候选行文案（自检用）。</summary>
        public List<string> ItemTexts()
        {
            var list = new List<string>();
            foreach (var o in _list.Items) list.Add(Convert.ToString(o));
            return list;
        }

        /// <summary>窗里的按钮文案（自检用：应为「移动/复制」+「取消」）。</summary>
        public List<string> ButtonTexts()
        {
            var list = new List<string>();
            var root = Content as Grid;
            if (root == null) return list;
            foreach (var o in root.Children)
            {
                var bar = o as StackPanel;
                if (bar == null) continue;
                foreach (var b in bar.Children)
                {
                    var btn = b as Button;
                    if (btn != null) list.Add(Convert.ToString(btn.Content));
                }
            }
            return list;
        }

        /// <summary>说明文案（自检用来断言"复制 = 新条目 / 移动 = 整棵子树"的口径）。</summary>
        public string HintText { get { return _hint; } }
    }
}
