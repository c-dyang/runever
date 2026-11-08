// ============================================================
// RunEver · 全盘自检结果窗口（WPF，纯 .cs 手写 —— 本项目 0 个 .xaml）
//
// 列：类型 / 目标 / 来源标题 / 建议包名 / 状态
// 顶部：统计条 + 后端信息     底部：复制命令 / 导出 .bat / 关闭
//
// ★★ 硬约束（用户定案）：**不做一键安装** —— 不弹 UAC、不提权、不自动跑 choco。
//   UI 上不得出现「一键安装」字样。用户自己复制命令或跑导出的 .bat。
// ============================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    public class SelfCheckWindow : Window
    {
        private readonly string _root;
        private readonly SelfCheckReport _rep;
        private readonly List<SelfCheckItem> _rows = new List<SelfCheckItem>();
        private readonly TextBox _cmdBox;

        public SelfCheckWindow(string root, SelfCheckReport rep)
        {
            _root = root;
            _rep = rep;

            Title = "RunEver 全盘自检";
            Width = 1080;
            Height = 700;
            MinWidth = 820;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- ① 统计条 ----
            var head = new StackPanel { Margin = new Thickness(12, 10, 12, 6) };
            head.Children.Add(new TextBlock
            {
                Text = BuildTitle(),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap
            });
            head.Children.Add(new TextBlock
            {
                Text = BuildSubTitle(),
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = System.Windows.Media.Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            // ---- ② 明细表 ----
            var dg = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                Margin = new Thickness(12, 0, 12, 6),
                SelectionMode = DataGridSelectionMode.Extended
            };
            dg.Columns.Add(Col("状态", "StatusLabel", 140));
            dg.Columns.Add(Col("类型", "Kind", 60));
            dg.Columns.Add(Col("程序名", "Prog", 140));
            dg.Columns.Add(Col("目标", "Target", 200));
            dg.Columns.Add(Col("来源标题", "Title", 150));
            dg.Columns.Add(Col("建议包名", "PackageText", 130));
            dg.Columns.Add(Col("备注", "Note", 220));
            dg.ItemsSource = _rows;
            Grid.SetRow(dg, 1);
            grid.Children.Add(dg);

            // ---- ③ 命令清单（只读可选择）----
            var cmdPanel = new StackPanel { Margin = new Thickness(12, 0, 12, 6) };
            cmdPanel.Children.Add(new TextBlock
            {
                Text = "choco 命令清单（复制后自行执行；未确认包名的项不在此处，见明细表「备注」列）",
                Margin = new Thickness(0, 0, 0, 4)
            });
            _cmdBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 110,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Text = BuildCommands()
            };
            cmdPanel.Children.Add(_cmdBox);
            Grid.SetRow(cmdPanel, 2);
            grid.Children.Add(cmdPanel);

            // ---- ④ 按钮条 ----
            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 12, 12)
            };
            btns.Children.Add(Btn("复制命令", (s, e) => CopyCommands()));
            btns.Children.Add(Btn("导出 .bat…", (s, e) => ExportBat()));
            btns.Children.Add(Btn("打开报告", (s, e) => OpenReport()));
            btns.Children.Add(Btn("关闭", (s, e) => Close()));
            Grid.SetRow(btns, 3);
            grid.Children.Add(btns);

            Content = grid;

            // 列表：未安装/待确认排前面，方便直接看结论
            foreach (var it in rep.Problems()) _rows.Add(it);
            foreach (var it in rep.Items)
                if (it.Status == CheckStatus.Ok || it.Status == CheckStatus.Skipped) _rows.Add(it);
        }

        private static DataGridTextColumn Col(string header, string path, int width)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Width = width,
                Binding = new System.Windows.Data.Binding(path)
            };
        }

        private static Button Btn(string text, RoutedEventHandler onClick)
        {
            var b = new Button { Content = text, Width = 110, Height = 28, Margin = new Thickness(8, 0, 0, 0) };
            b.Click += onClick;
            return b;
        }

        private string BuildTitle()
        {
            if (_rep == null) return "自检报告不可用";
            return "全盘自检：" + _rep.SummaryLine();
        }

        private string BuildSubTitle()
        {
            if (_rep == null || _rep.Backend == null) return "";
            var sb = new StringBuilder();
            sb.Append("后端：").Append(_rep.Backend.Describe());
            if (_rep.Backend.Degraded)
            {
                sb.Append("\n⚠ 搜索后端已降级 ⇒ 未命中项**只能**标「待确认」，不代表没安装"
                    + "（便携版 / 索引未覆盖 / 后端降级三者现象相同）");
            }
            sb.Append("\n路径缓存写回：")
              .Append(_rep.CacheWriteBackNote.Length > 0 ? _rep.CacheWriteBackNote : "（本次未写回）");
            sb.Append("\n本页面只给命令清单，**不会自动安装任何东西**（不弹 UAC、不跑 choco）。");
            return sb.ToString();
        }

        private string BuildCommands()
        {
            if (_rep == null) return "";
            List<string> cmds = _rep.ChocoCommands();
            var sb = new StringBuilder();
            if (cmds.Count == 0) sb.AppendLine("(没有已确认包名的缺失项)");
            foreach (string c in cmds) sb.AppendLine(c);
            sb.AppendLine();
            sb.AppendLine("# 包名未确认的项（禁硬猜，先核实）：");
            foreach (var it in _rep.Problems())
            {
                if (it.Package.Length > 0) continue;
                sb.AppendLine("#   " + (it.Prog.Length > 0 ? it.Prog : it.Target)
                    + "   →  choco search " + SelfCheckReport.SearchTerm(it));
            }
            return sb.ToString();
        }

        private void CopyCommands()
        {
            try
            {
                Clipboard.SetText(_cmdBox.Text);
                MessageBox.Show("命令清单已复制到剪贴板。\n\n注意：本程序不会替你安装，请自行核对后执行。",
                    "RunEver", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("复制失败：" + ex.Message, "RunEver", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ExportBat()
        {
            try
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出 choco 命令清单",
                    FileName = "runever-choco-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bat",
                    Filter = "批处理文件 (*.bat)|*.bat|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                    InitialDirectory = Directory.Exists(Path.Combine(_root, "state"))
                        ? Path.Combine(_root, "state") : _root
                };
                bool? ok = dlg.ShowDialog(this);
                if (ok != true) return;

                // ★ UTF-8 无 BOM + 纯 ASCII 注释：中文标题不进 .bat，绕开 cmd 的 OEM 代码页
                File.WriteAllText(dlg.FileName, _rep.BuildBat(), new UTF8Encoding(false));
                MessageBox.Show("已导出：\n" + dlg.FileName
                    + "\n\n双击前请先审阅内容。本程序不会自动执行它。",
                    "RunEver", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "RunEver", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenReport()
        {
            try
            {
                string f = Path.Combine(_root, "state", "selfcheck-report.txt");
                File.WriteAllText(f, _rep.Render(), new UTF8Encoding(false));
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开报告失败：" + ex.Message, "RunEver", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
