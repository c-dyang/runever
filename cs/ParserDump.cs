using System;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 一致性验证工具：C# 解析器 vs AHK 解析器。
    /// 用法：RunEverCs.exe --dump-parser &lt;menus.conf&gt; &lt;out.txt&gt;
    /// </summary>
    public static class ParserDump
    {
        public static int Run(string[] args)
        {
            // args: [0]=--dump-parser  [1]=conf  [2]=out
            if (args.Length < 3) return 2;
            string conf = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            try
            {
                var p = new ConfParser(conf);
                p.Parse();
                sb.AppendLine("分组数 = " + p.Groups.Count);
                int totalItems = 0;
                foreach (var g in p.Groups)
                {
                    totalItems += g.Items.Count;
                    sb.AppendLine("G|" + g.Name + "|" + MenuGroup.TypeToString(g.Type)
                        + "|" + string.Join(",", g.Keywords) + "|" + g.Items.Count);
                    foreach (var it in g.Items)
                    {
                        sb.AppendLine("  I|lv" + it.Level + "|" + it.Title + "|_" + it.Hotkey
                            + "|" + it.Type.ToString().ToLowerInvariant()
                            + "|" + FlagStr(it.Flag)
                            + "|" + it.Target);
                    }
                }
                sb.AppendLine("条目总数 = " + totalItems);
            }
            catch (Exception ex)
            {
                sb.AppendLine("ERROR: " + ex.Message);
                File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
                return 1;
            }

            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            return 0;
        }

        private static string FlagStr(ActionFlag f)
        {
            switch (f)
            {
                case ActionFlag.Hide: return "hide";
                case ActionFlag.Show: return "show";
                default: return "";
            }
        }
    }
}
