// ============================================================
// RunEver · 全盘自检结果窗口（WPF，纯 .cs 手写 —— 本项目 0 个 .xaml）
//
// ★ 2026-11-08（B5）三段式：
//   ① 结论（一句人话）
//   ② 需要你处理的（未找到条目：显示名 + 这是哪个软件/建议怎么装）
//   ③ 明细（**默认折叠**）：原机器行原样保留，只给排障看
//
//   为什么这么改：用户原话是"看不懂，也不知道该不该紧张"。第一屏全是
//   `backend=EV … dbLoaded=1 lastError=0`、`层级=cache` 这种字段，属于把日志
//   直接糊在脸上。人话区**不许出现** backend= / dbLoaded= / lastError= / 层级= /
//   包名已核实 / probe= —— 那些只允许出现在第③段和 state\selfcheck-report.txt。
//
// ★★ 硬约束（用户定案）：**不做一键安装** —— 不弹 UAC、不提权、不自动跑 choco。
//   UI 上不得出现「一键安装」字样。清单文件只生成、只打开，跑不跑由用户。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    public class SelfCheckWindow : Window
    {
        private readonly string _root;
        private readonly SelfCheckReport _rep;
        private readonly List<SelfCheckItem> _rows = new List<SelfCheckItem>();
        private readonly TextBox _cmdBox;
        private readonly Button _probeBtn;

        /// <summary>安装清单的固定落点（B5-6）。</summary>
        private string ChecklistPath { get { return Path.Combine(_root, "state", "selfcheck-choco.bat"); } }

        public SelfCheckWindow(string root, SelfCheckReport rep)
        {
            _root = root;
            _rep = rep;

            Title = "RunEver 全盘自检";
            Width = 1080;
            Height = 720;
            MinWidth = 820;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // ① 结论
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // ② 需要处理
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // ③ 明细
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // 按钮

            // ================= ① 结论 =================
            var head = new StackPanel { Margin = new Thickness(14, 12, 14, 6) };
            head.Children.Add(new TextBlock
            {
                Text = BuildConclusion(),
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ConclusionBrush()
            });
            head.Children.Add(new TextBlock
            {
                Text = BuildHumanSubLine(),
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = Brushes.DimGray,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(head, 0);
            grid.Children.Add(head);

            // ================= ② 需要你处理 =================
            var todo = new StackPanel { Margin = new Thickness(14, 4, 14, 6) };
            todo.Children.Add(new TextBlock
            {
                // 这句是**口径图例**：两类"没找到"含义完全不同，必须当场说清
                Text = "需要你处理的（「确实没有」= 已确认缺失；「待确认」= 查不到，不代表没装）",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });
            todo.Children.Add(BuildProblemList());
            Grid.SetRow(todo, 1);
            grid.Children.Add(todo);

            // ================= ③ 明细（默认折叠）=================
            var dg = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                Margin = new Thickness(0, 4, 0, 0),
                // ★ 固定高度：它外层是 StackPanel（无限高），DataGrid 不限高会按内容撑开，
                //   把 Expander 顶出可视区（明细是"要看得见才滚"的东西，不是要撑满）。
                Height = 240,
                SelectionMode = DataGridSelectionMode.Extended
            };
            dg.Columns.Add(Col("状态", "StatusLabel", 140));
            dg.Columns.Add(Col("类型", "Kind", 60));
            dg.Columns.Add(Col("程序名", "Prog", 140));
            dg.Columns.Add(Col("目标", "Target", 200));
            dg.Columns.Add(Col("来源标题", "Title", 150));
            dg.Columns.Add(Col("建议包名", "PackageText", 110));
            dg.Columns.Add(Col("备注", "Note", 220));
            dg.ItemsSource = _rows;

            var detailBody = new StackPanel();
            detailBody.Children.Add(new TextBlock
            {
                Text = "下面是原始字段（后端 / 层级 / 耗时 / 备注 等），只用于排障；"
                     + "完整机器版报告另存为 state\\selfcheck-report.txt。",
                Foreground = Brushes.Gray,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4)
            });
            detailBody.Children.Add(dg);

            var expander = new Expander
            {
                // 标题里点明「已确认 / 待确认」的原始口径：这层的状态列就是这么写的，
                // 免得人和上面的「确实没有 / 查不到」对不上号。
                Header = "明细（默认折叠 · " + _rep.Items.Count + " 行原始数据 · 状态列为「已确认」「待确认」口径）",
                IsExpanded = false,
                Content = detailBody,
                Margin = new Thickness(14, 0, 14, 6)
            };
            Grid.SetRow(expander, 2);
            grid.Children.Add(expander);

            // ================= 命令清单（只读可选择；也放进折叠区更合理）=================
            _cmdBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                Height = 110,
                Margin = new Thickness(0, 6, 0, 0),
                FontFamily = new FontFamily("Consolas"),
                Text = BuildCommands()
            };
            detailBody.Children.Add(new TextBlock
            {
                Text = "choco 命令清单（复制后自行执行；包名未确认的项不在此处，见上表「备注」列）",
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brushes.Gray,
                FontSize = 11.5
            });
            detailBody.Children.Add(_cmdBox);

            // ================= ④ 按钮条 =================
            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(14, 2, 14, 12)
            };

            _probeBtn = Btn("检查搜索后端", (s, e) => ProbeBackend());
            btns.Children.Add(_probeBtn);

            // ★ B5-6：确认缺失 ⇒ 给「打开清单」；仍不自动装、不弹 UAC、不跑 choco
            if (HasConfirmedMissing())
            {
                btns.Children.Add(Btn("打开清单", (s, e) => OpenChecklist()));
            }
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

        // ============================================================
        // 构建
        // ============================================================
        private string BuildConclusion()
        {
            if (_rep == null) return "没有拿到自检结果";
            return _rep.HumanConclusion();
        }

        private Brush ConclusionBrush()
        {
            if (_rep == null) return Brushes.Black;
            if (!_rep.BackendTrusted()) return Brushes.Firebrick;
            if (_rep.Missing + _rep.Unknown > 0) return Brushes.DarkOrange;
            return new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x46));
        }

        /// <summary>结论下面那一行：结论的补充而不重复，且**只有人话**。</summary>
        private string BuildHumanSubLine()
        {
            if (_rep == null) return "";
            var sb = new StringBuilder();

            // 留痕（B4b-9）：上次检查于 HH:mm，结果：可用 N / 未找到 M
            sb.Append(_rep.LastCheckLine());
            sb.Append("　·　").Append(_rep.BackendHumanLine());
            if (_rep.Skipped > 0) sb.Append('\n').Append(_rep.SkipExplanation());

            if (HasConfirmedMissing())
            {
                sb.Append("\n已生成安装清单：state\\selfcheck-choco.bat（先看一眼再运行；本程序不会替你装）。");
            }
            return sb.ToString();
        }

        private UIElement BuildProblemList()
        {
            var host = new StackPanel();

            if (_rep == null)
            {
                host.Children.Add(Gray("没有结果。"));
                return host;
            }

            var problems = _rep.Problems();
            if (problems.Count == 0)
            {
                host.Children.Add(Gray(_rep.BackendTrusted()
                    ? "没有需要处理的：配置里能检查的条目都找到了。"
                    : "这次没有「未找到」条目，但搜索后端异常时，找不到也说明不了什么，建议先点「检查搜索后端」。"));
                return host;
            }

            // 有「查不到」的条目时先把口径说清楚（这两类处理方式完全不同）
            bool anyUnknown = false;
            foreach (var it in problems) if (it.Status == CheckStatus.Unknown) anyUnknown = true;
            if (anyUnknown)
            {
                host.Children.Add(Gray("说明：标「查不到」的条目状态是「待确认」 —— "
                    + "搜索后端异常、或便携版不在索引里，都会表现为找不到，不代表没装。"));
            }

            foreach (var it in problems)
            {
                bool confirmed;
                string line = _rep.HumanProblemLine(it, out confirmed);
                host.Children.Add(new TextBlock
                {
                    Text = "• " + line.Replace("\n", "\n  "),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    Margin = new Thickness(0, 0, 0, 6),
                    Foreground = confirmed ? Brushes.Black : Brushes.DimGray
                });
            }

            // 问题多的时候别把窗口撑爆：放进一个带滚动的最小/最大高度里
            var sv = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 190,
                Content = host
            };
            return sv;
        }

        private static TextBlock Gray(string s)
        {
            return new TextBlock
            {
                Text = s,
                Foreground = Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5
            };
        }

        private bool HasConfirmedMissing()
        {
            if (_rep == null) return false;
            foreach (var it in _rep.Items)
                if (it.Status == CheckStatus.Missing) return true;
            return false;
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
            var b = new Button { Content = text, MinWidth = 96, Height = 28, Margin = new Thickness(8, 0, 0, 0) };
            b.Padding = new Thickness(8, 0, 8, 0);
            b.Click += onClick;
            return b;
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

        // ============================================================
        // 动作
        // ============================================================

        /// <summary>B5-4：只读探针，重探一次搜索后端（不改配置、不装东西）。</summary>
        private void ProbeBackend()
        {
            _probeBtn.IsEnabled = false;
            _probeBtn.Content = "检查中…";

            var worker = new System.Threading.Thread(() =>
            {
                string human;
                try
                {
                    EverythingProbe.Reset();     // 从零重探，别拿缓存糊弄自己
                    var st = EverythingProbe.Get(BackendResolver.ReadEvDll(_root), true);
                    bool ok = st != null && st.Kind == BackendKind.Everything
                              && st.ProbeHits > 0 && st.DbLoaded;
                    if (ok)
                    {
                        human = "搜索后端正常：Everything 直连"
                            + (st.ProbeMs >= 0 ? "（" + st.ProbeMs + "ms 返回结果）" : "")
                            + "。\n\n如果刚才有「没找到」的条目，现在可以重新自检一次，结论会更可信。";
                    }
                    else
                    {
                        human = "搜索后端仍然不可用："
                            + (st != null && st.Kind == BackendKind.Everything
                                ? "Everything 连上了但索引没就绪（可能正在建索引）"
                                : "没找到 Everything64.dll 或 Everything 没在运行")
                            + "。\n\n此时「没找到」的条目「不代表没装」。";
                    }
                    if (st != null) _rep.Backend = st;
                }
                catch (Exception ex)
                {
                    human = "检查搜索后端失败：" + ex.Message;
                }

                Action done = () =>
                {
                    _probeBtn.IsEnabled = true;
                    _probeBtn.Content = "检查搜索后端";
                    MessageBox.Show(human, "RunEver 搜索后端", MessageBoxButton.OK, MessageBoxImage.Information);
                };
                try { Dispatcher.BeginInvoke(done); } catch { }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        /// <summary>B5-6：打开（必要时先生成）安装清单 —— 只打开，不执行。</summary>
        private void OpenChecklist()
        {
            try
            {
                string f = ChecklistPath;
                if (!File.Exists(f))
                {
                    string dir = Path.GetDirectoryName(f);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    // UTF-8 无 BOM + 纯 ASCII 注释（cmd 的 OEM 代码页教训）
                    File.WriteAllText(f, _rep.BuildBat(), new UTF8Encoding(false));
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开清单失败：" + ex.Message + "\n\n路径：" + ChecklistPath,
                    "RunEver", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
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
