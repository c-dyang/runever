using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RunEverCs.Views
{
    /// <summary>
    /// 设置页基础控件（2026-11-07 全面重建设置 UI）。
    ///
    /// 设计依据（微软官方 Guidelines for app settings + Windows 11 设置页实际结构）：
    ///   · **左导航 + 右内容**，不是顶部 Tab（Tab 超过 4 个就开始横着挤，且无法分组）；
    ///   · **卡片式分组**：每张卡一个主题，卡内一行一个设置；
    ///   · 每行 = 「标签 + 说明」在左、**控件右对齐**（SettingsCard 的 ContentAlignment.Right）；
    ///   · 内容最大宽度 1000-1100px，单列滚动（宽屏下不散架）；
    ///   · **改完立即生效**，不留"保存"按钮（官方明确：不要确认按钮）。
    ///
    /// 本类只提供视觉构件，不含任何业务逻辑 —— 各 Page 自己决定绑哪个配置键。
    /// </summary>
    internal static class SettingsKit
    {
        // ---- 配色（对齐 Windows 11 浅色主题）----
        public static readonly Brush PageBg = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
        public static readonly Brush CardBg = Brushes.White;
        public static readonly Brush CardBorder = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5));
        public static readonly Brush HairlineBrush = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xED));
        public static readonly Brush TextPrimary = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        public static readonly Brush TextSecondary = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        public static readonly Brush NavSelected = new SolidColorBrush(Color.FromRgb(0xE8, 0xF3, 0xEA));
        public static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x46));

        /// <summary>
        /// 把 WPF 按键映射成 menus.conf / auto.conf 的单字符热键。
        /// 返回 null = 该键不参与（修饰键 / 无对应字符）。
        /// 供「菜单项编辑器」和「触发与行为页」共用。
        ///
        /// ★ B4b-4：补上 **F1–F24**（返回 "F1".."F24" 这类**键名**，不是字符）——
        ///   `HotkeySpec` 本来就把 F1-F24 / Space 当"命名键"解析（见其 NamedKeys），
        ///   只有这个映射表漏了，于是设置页承诺能用 F 键、实际按 F8 毫无反应。
        /// ★ 空格返回 "Space"（命名键）而不是字面空格：字面空格过一遍 `Trim()` 就没了，
        ///   而且输入框里一个空格看不出是"空格"还是"没填"。
        /// </summary>
        public static string KeyToHotkeyChar(System.Windows.Input.Key key)
        {
            // F1..F24：Key 枚举里是连续区间，直接算序号
            if (key >= System.Windows.Input.Key.F1 && key <= System.Windows.Input.Key.F24)
                return "F" + ((int)key - (int)System.Windows.Input.Key.F1 + 1);

            if (key >= System.Windows.Input.Key.A && key <= System.Windows.Input.Key.Z)
                return ((char)('A' + (key - System.Windows.Input.Key.A))).ToString().ToLowerInvariant();
            if (key >= System.Windows.Input.Key.D0 && key <= System.Windows.Input.Key.D9)
                return ((char)('0' + (key - System.Windows.Input.Key.D0))).ToString();
            switch (key)
            {
                case System.Windows.Input.Key.OemTilde: return "`";
                case System.Windows.Input.Key.OemMinus: return "-";
                case System.Windows.Input.Key.OemPlus: return "=";
                case System.Windows.Input.Key.OemOpenBrackets: return "[";
                case System.Windows.Input.Key.OemCloseBrackets: return "]";
                case System.Windows.Input.Key.OemPipe: return "\\";
                case System.Windows.Input.Key.OemSemicolon: return ";";
                case System.Windows.Input.Key.OemQuotes: return "'";
                case System.Windows.Input.Key.OemComma: return ",";
                case System.Windows.Input.Key.OemPeriod: return ".";
                case System.Windows.Input.Key.OemQuestion: return "/";
                case System.Windows.Input.Key.Space: return "Space";
            }
            return null;
        }

        /// <summary>
        /// 热键框的取值规整：**只去掉首尾空白，且不允许把"纯空格"抹成空串**。
        /// 纯空格（用户手输/粘贴进来的）按命名键 "Space" 处理 —— 否则 `Trim()` 会把它
        /// 变成一个空串，`HotkeySpec` 判"热键不能为空"后静默回落默认键（B4b-4 的原始症状）。
        /// </summary>
        public static string TrimHotkeyText(string s)
        {
            if (s == null) return "";
            string t = s.Trim();
            if (t.Length == 0 && s.Length > 0) return "Space";
            return t;
        }
        /// <summary>分区标题（微软用 BodyStrong；这里 14px 半粗 + 上下留白）。</summary>
        public static TextBlock SectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                Margin = new Thickness(2, 20, 0, 8)
            };
        }

        /// <summary>卡片：白底 + 1px 边框 + 8px 圆角。rows 之间自动插 hairline。</summary>
        public static Border Card(params UIElement[] rows)
        {
            var stack = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) stack.Children.Add(Hairline());
                stack.Children.Add(rows[i]);
            }
            return new Border
            {
                Background = CardBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = stack,
                Margin = new Thickness(0, 0, 0, 4),
                // 卡片必须拉伸填满容器宽度（否则会退化成"按内容宽度"，右侧留白）
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
        }

        public static Border Hairline()
        {
            return new Border
            {
                Height = 1,
                Background = HairlineBrush,
                Margin = new Thickness(16, 0, 16, 0)
            };
        }

        /// <summary>
        /// 一行设置：左侧「标签 + 说明」，右侧控件（垂直居中、右对齐）。
        /// 这是整套设置页唯一的行布局 —— 所有页面都走它，保证对齐一致。
        /// </summary>
        public static Grid Row(string header, string description, FrameworkElement control)
        {
            var g = new Grid { Margin = new Thickness(16, 10, 16, 10), MinHeight = 40 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 13,
                Foreground = TextPrimary,
                TextWrapping = TextWrapping.Wrap
            });
            if (!string.IsNullOrEmpty(description))
            {
                left.Children.Add(new TextBlock
                {
                    Text = description,
                    FontSize = 11.5,
                    Foreground = TextSecondary,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            if (control != null)
            {
                control.VerticalAlignment = VerticalAlignment.Center;
                control.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(control, 1);
                g.Children.Add(control);
            }
            return g;
        }

        // ============================================================
        // 常用行（带即时保存回调）
        //
        // ★ 2026-11-08（B4b-1）：回调由 `Action<T>` 改成 `Func<T,bool>`。
        //   原因：auto.conf 写盘失败时，控件**必须自己退回旧值** —— 否则界面上是"已改"、
        //   磁盘上是"没改"，用户下次开窗才莫名其妙地发现设置没了（原实现就是静默吞掉）。
        //   `Func` 返回 false = 没写成功 ⇒ 这里把控件回滚到上一次成功的值。
        //   （各页传的都是 `v => Set(...)`，本来就是 bool，调用点零改动。）
        // ============================================================
        public static Grid Toggle(string header, string desc, bool value, Func<bool, bool> onChange)
        {
            var cb = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center };

            bool last = value;
            bool reverting = false;
            Action<bool> commit = v =>
            {
                if (reverting) return;
                bool ok = onChange == null || onChange(v);
                if (ok) { last = v; return; }
                reverting = true;
                cb.IsChecked = last;      // 写盘失败 ⇒ 控件回滚
                reverting = false;
            };

            cb.Checked += (s, e) => commit(true);
            cb.Unchecked += (s, e) => commit(false);
            return Row(header, desc, cb);
        }

        public static Grid Text(string header, string desc, string value, Func<string, bool> onChange, double width = 220)
        {
            var tb = new TextBox
            {
                Text = value ?? "",
                Width = width,
                Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            string last = value ?? "";
            Action commit = () =>
            {
                string v = tb.Text ?? "";
                if (v == last) return;
                bool ok = onChange == null || onChange(v);
                if (ok) last = v;
                else tb.Text = last;      // 写盘失败 ⇒ 控件回滚
            };
            tb.LostFocus += (s, e) => commit();
            tb.KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) commit();
            };
            return Row(header, desc, tb);
        }

        public static Grid Combo(string header, string desc, string[] items, string selected, Func<string, bool> onChange)
        {
            var cb = new ComboBox { Width = 180, SelectedItem = selected };
            foreach (var it in items) cb.Items.Add(it);
            if (cb.SelectedIndex < 0) cb.SelectedIndex = 0;

            object last = cb.SelectedItem;
            bool reverting = false;
            cb.SelectionChanged += (s, e) =>
            {
                if (reverting || cb.SelectedItem == null) return;
                string v = cb.SelectedItem.ToString();
                bool ok = onChange == null || onChange(v);
                if (ok) { last = cb.SelectedItem; return; }
                reverting = true;
                cb.SelectedItem = last;   // 写盘失败 ⇒ 控件回滚
                reverting = false;
            };
            return Row(header, desc, cb);
        }

        public static Grid ButtonRow(string header, string desc, string buttonText, Action onClick)
        {
            var b = new Button
            {
                Content = buttonText,
                Padding = new Thickness(14, 5, 14, 5),
                MinWidth = 90
            };
            b.Click += (s, e) => { if (onClick != null) onClick(); };
            return Row(header, desc, b);
        }

        /// <summary>只读信息行（路径、版本这类）。</summary>
        public static Grid Info(string header, string value)
        {
            var tb = new TextBox
            {
                Text = value ?? "",
                IsReadOnly = true,
                Width = 420,
                Padding = new Thickness(6, 4, 6, 4),
                Foreground = TextSecondary,
                Background = new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA))
            };
            return Row(header, null, tb);
        }

        /// <summary>
        /// 页面容器：灰底 + 单列 + 拉伸填满可用宽度。
        ///
        /// ★ 2026-11-07 用户反馈「UI 内部宽度是固定的，不是根据布局自动计算」：
        ///   原来 inner 是 `HorizontalAlignment = Left` + `MaxWidth = 1000`，
        ///   StackPanel 按**内容自然宽度**排布 ⇒ 卡片只有 ~900px 宽，窗口拉大后
        ///   右侧一大片空白，看着像固定宽度。
        ///   现在改成 Stretch（填满内容区），只保留一个更宽松的上限防止超宽屏行太长。
        /// </summary>
        public static ScrollViewer PageHost(UIElement content)
        {
            var inner = new StackPanel
            {
                MaxWidth = 1400,          // 超宽屏时才生效；正常窗口下填满
                Margin = new Thickness(28, 8, 28, 28),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            inner.Children.Add(content);

            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = PageBg,
                Content = inner
            };
            return sv;
        }
    }
}
