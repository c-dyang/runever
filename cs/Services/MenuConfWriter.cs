using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// menus.conf 写回器 —— 序列化 + 备份 + 回读校验。
    ///
    /// ★ 安全纪律（吸取 AHK 版越界损坏配置的教训）：
    ///   ① 写前备份 menus.conf.bak.&lt;时间戳&gt;（保留最近 10 份）
    ///   ② 原子写（.tmp → 替换）
    ///   ③ ★ 写后立刻用 ConfParser 回读，结构不符即回滚
    ///
    /// ⚠️ 已知代价：注释会丢失（分组/条目的自由文本行无法保留原始装饰注释）。
    ///    如需保留注释请用「窄写回」模式（只改条目行），本轮未实现。
    /// </summary>
    public class MenuConfWriter
    {
        private readonly string _confPath;
        private const int MaxBackups = 10;

        public MenuConfWriter(string confPath)
        {
            _confPath = confPath;
        }

        /// <summary>序列化成分组列表 → 菜单语法文本。</summary>
        public static string Serialize(List<MenuGroup> groups)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ============================================================");
            sb.AppendLine("# RunEver 配置（由设置界面保存）");
            sb.AppendLine("# 编码: UTF-8 (无 BOM)");
            sb.AppendLine("#");
            sb.AppendLine("# 语法:");
            sb.AppendLine("#   ! <分组名>  <类型>  [关键词...]        分组（folder/ext/proc/text/always/fallback）");
            sb.AppendLine("#     <标题>  [_快捷键]  | <动作>  <目标>   条目（缩进 2 空格 = 层级）");
            sb.AppendLine("#   #                                      注释");
            sb.AppendLine("#");
            sb.AppendLine("# 动作类型:");
            sb.AppendLine("#   run    启动程序（目标 = 命令行，首个 token 走路径缓存解析）");
            sb.AppendLine("#   open   打开文件/文件夹");
            sb.AppendLine("#   url    打开网址");
            sb.AppendLine("#   action 调外部程序（cmd 管道）；可选 hide/show 控制执行窗口");
            sb.AppendLine("#   send   发送文本");
            sb.AppendLine("#   keys   发快捷键");
            sb.AppendLine("#   group  纯分组标题");
            sb.AppendLine("# ============================================================");
            sb.AppendLine();

            foreach (var g in groups)
            {
                string kw = g.Keywords.Count > 0 ? "  " + string.Join(" ", g.Keywords) : "";
                sb.AppendLine("! " + g.Name + "  " + MenuGroup.TypeToString(g.Type) + kw);

                foreach (var it in g.Items)
                    sb.AppendLine(FormatItem(it));

                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>单条目 → 一行文本（缩进按 Level）。</summary>
        public static string FormatItem(MenuItem it)
        {
            var sb = new StringBuilder();

            // ★★ 缩进必须是 Level*2 + 1 —— 解析器按 leading/2 反算层级。
            //    实测原始 menus.conf：Level=1 的条目缩进 = 3 空格（3/2 = 1）。
            //    曾写成 Level*2+2（=4，4/2=2）⇒ 往返后所有条目 Level 都 +1，
            //    属于会静默损坏配置的严重 bug（由往返自检抓出）。
            sb.Append(new string(' ', Math.Max(0, it.Level) * 2 + 1));

            sb.Append(it.Title);
            if (!string.IsNullOrEmpty(it.Hotkey))
                sb.Append("  _" + it.Hotkey);

            if (it.Type != ActionType.Group || !string.IsNullOrEmpty(it.Target))
            {
                sb.Append("  | ").Append(TypeToString(it.Type));

                if (it.Type == ActionType.Action && it.Flag != ActionFlag.None)
                {
                    sb.Append(' ').Append(it.Flag == ActionFlag.Show ? "show" : "hide");
                }

                if (!string.IsNullOrEmpty(it.Target))
                    sb.Append("  ").Append(it.Target);
            }

            return sb.ToString();
        }

        private static string TypeToString(ActionType t)
        {
            switch (t)
            {
                case ActionType.Run: return "run";
                case ActionType.Open: return "open";
                case ActionType.Url: return "url";
                case ActionType.Action: return "action";
                case ActionType.Send: return "send";
                case ActionType.Keys: return "keys";
                default: return "group";
            }
        }

        /// <summary>
        /// 保存：备份 → 原子写 → 回读校验（不符则回滚）。
        /// 返回 (成功, 说明)。
        /// </summary>
        public bool Save(List<MenuGroup> groups, out string message)
        {
            message = "";
            string backupPath = null;
            try
            {
                string text = Serialize(groups);

                // ① 备份
                if (File.Exists(_confPath))
                {
                    string dir = Path.GetDirectoryName(_confPath);
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    backupPath = Path.Combine(dir, "menus.conf.bak." + stamp);
                    try
                    {
                        File.Copy(_confPath, backupPath, true);
                        CleanupBackups(dir);
                    }
                    catch { backupPath = null; }
                }

                // ② 原子写
                string tmp = _confPath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(_confPath)) File.Delete(_confPath);
                File.Move(tmp, _confPath);

                // ③ ★ 回读校验：能解析且分组数/条目数一致才算成功
                var check = new ConfParser(_confPath);
                check.Parse();

                if (check.Groups.Count != groups.Count)
                {
                    Rollback(backupPath);
                    message = "回读校验失败：分组数 " + check.Groups.Count
                        + " ≠ 期望 " + groups.Count + "，已回滚";
                    return false;
                }

                int wantItems = 0, gotItems = 0;
                foreach (var g in groups) wantItems += g.Items.Count;
                foreach (var g in check.Groups) gotItems += g.Items.Count;

                if (wantItems != gotItems)
                {
                    Rollback(backupPath);
                    message = "回读校验失败：条目数 " + gotItems
                        + " ≠ 期望 " + wantItems + "，已回滚";
                    return false;
                }

                message = "已保存（分组 " + groups.Count + "，条目 " + wantItems + "）"
                    + (backupPath != null ? "\n备份：" + Path.GetFileName(backupPath) : "");
                return true;
            }
            catch (Exception ex)
            {
                Rollback(backupPath);
                message = "保存失败：" + ex.Message + "（已尝试回滚）";
                return false;
            }
        }

        private void Rollback(string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath) || !File.Exists(backupPath)) return;
            try
            {
                File.Copy(backupPath, _confPath, true);
            }
            catch { }
        }

        /// <summary>保留最近 N 份备份。</summary>
        private static void CleanupBackups(string dir)
        {
            try
            {
                var files = new List<string>(Directory.GetFiles(dir, "menus.conf.bak.*"));
                if (files.Count <= MaxBackups) return;

                files.Sort(StringComparer.OrdinalIgnoreCase);
                int remove = files.Count - MaxBackups;
                for (int i = 0; i < remove; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }
    }
}
