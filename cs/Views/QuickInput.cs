using System;
using System.Drawing;
using System.Windows.Forms;
using RunEverCs.Services;

namespace RunEverCs.Views
{
    /// <summary>
    /// 右键上下文菜单用的极简文本输入框（WinForms，模态）。
    ///
    /// ★ 调用时机（卡 D 的硬约束）：**只能在主菜单已关闭之后**弹（`MenuItemContextMenu` 把它排在
    ///   `PopupClosed` 里）。菜单消息泵期间同步开模态窗口会把菜单卡死。
    /// ★ 返回值：用户确定 ⇒ 新文本；取消/关闭 ⇒ `null`（调用方据此放弃、不写盘）。
    /// </summary>
    internal static class QuickInput
    {
        public static string Prompt(string current, MenuItemAction action)
        {
            string caption = action == MenuItemAction.Rename ? "重命名条目" : "编辑动作目标";
            try
            {
                using (var f = new Form())
                {
                    f.Text = caption;
                    f.FormBorderStyle = FormBorderStyle.FixedDialog;
                    f.StartPosition = FormStartPosition.CenterScreen;
                    f.MinimizeBox = false;
                    f.MaximizeBox = false;
                    f.ShowInTaskbar = false;
                    f.TopMost = true;
                    f.ClientSize = new Size(520, 112);

                    var lbl = new Label
                    {
                        Text = caption + "（留空 = 取消）：",
                        Left = 12,
                        Top = 14,
                        Width = 496,
                        AutoSize = false
                    };
                    var tb = new TextBox
                    {
                        Left = 12,
                        Top = 40,
                        Width = 496,
                        Text = current ?? ""
                    };
                    tb.SelectAll();
                    var ok = new Button
                    {
                        Text = "确定",
                        DialogResult = DialogResult.OK,
                        Left = 352,
                        Top = 74,
                        Width = 75
                    };
                    var cancel = new Button
                    {
                        Text = "取消",
                        DialogResult = DialogResult.Cancel,
                        Left = 433,
                        Top = 74,
                        Width = 75
                    };
                    f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
                    f.AcceptButton = ok;
                    f.CancelButton = cancel;

                    if (f.ShowDialog() != DialogResult.OK) return null;
                    return tb.Text;
                }
            }
            catch
            {
                // 输入框都开不起来 ⇒ 放弃本次编辑（不写盘），绝不向上抛
                return null;
            }
        }
    }
}
