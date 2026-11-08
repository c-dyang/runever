using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 菜单文本 dump —— 检查 & 快捷键注入结果。
    /// 用法：RunEverCs.exe --dump-menutext &lt;menus.conf&gt; &lt;out.txt&gt;
    /// </summary>
    public static class MenuTextDump
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;

            // ★ 参数必须是 **menus.conf 路径**（不是 root）—— 传错要立刻报错退出，
            //   不能像早期那样静默产出空报告还返回 0（假成功比失败更坑）。
            string conf = args[1];
            string outFile = args[2];
            if (!File.Exists(conf))
            {
                try
                {
                    File.WriteAllText(outFile,
                        "参数错误：配置文件不存在 → " + conf
                        + "\n用法：RunEverCs.exe --dump-menutext <menus.conf> <out.txt>",
                        new UTF8Encoding(false));
                }
                catch { }
                return 2;
            }

            var sb = new StringBuilder();
            try
            {
                var p = new ConfParser(conf);
                p.Parse();

                foreach (var g in p.Groups)
                {
                    sb.AppendLine("=== 分组: " + g.Name + " (" + MenuGroup.TypeToString(g.Type) + ") ===");
                    foreach (var it in g.Items)
                    {
                        string rendered = MenuBuilder.FormatMenuText(it.Title, it.Hotkey, it.IsGroupHeader);
                        sb.AppendLine("  lv" + it.Level
                            + " 原[" + it.Title + "] 键[" + it.Hotkey + "]"
                            + " → [" + rendered.Replace("\t", "\\t") + "]"
                            + "  含&=" + (rendered.IndexOf('&') >= 0 ? "是" : "否"));
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message);
            }
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            return 0;
        }
    }
}
