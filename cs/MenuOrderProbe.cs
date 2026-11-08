using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// 只读探针（`--dump-order`）：把 menus.conf 按指定上下文判定一遍，
    /// 打印**四段实际显示顺序**（组类型 + 组名 + 条目数）。
    ///
    /// 为什么需要它：菜单顺序问题在"看截图猜"里来回打转 —— 截图只能看到"看起来谁在上面"，
    /// 分不清是判定层没排、渲染层乱排、还是某些组因为 hideMissing 被整个跳过。
    /// 这里把「判定 → Flat(order) → 实际喂给渲染器的组列表」原样打出来，
    /// 与 `--dump-parser` / `--dump-menutext` 同一家族：只读、写文件（WinExe 无 stdout）、退出码恒 0。
    ///
    /// 用法：
    ///   RunEverCs.exe --dump-order &lt;root&gt; &lt;out.txt&gt; --order=dynamic,process,always,fallback
    ///                 --ctx=none --ctx=text --ctx=file:pdf --ctx=folder --ctx=proc:qq.exe
    /// </summary>
    public static class MenuOrderProbe
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];

            string confPath = Path.Combine(root, "config", "menus.conf");
            string orderRaw = null;
            bool orderGiven = false;
            var ctxs = new List<KeyValuePair<string, MatchContext>>();

            for (int i = 3; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("--order=", StringComparison.OrdinalIgnoreCase))
                {
                    orderRaw = a.Substring("--order=".Length);
                    orderGiven = true;
                }
                else if (a.StartsWith("--conf=", StringComparison.OrdinalIgnoreCase))
                {
                    confPath = a.Substring("--conf=".Length);
                }
                else if (a.StartsWith("--ctx=", StringComparison.OrdinalIgnoreCase))
                {
                    ctxs.Add(MakeCtx(a.Substring("--ctx=".Length)));
                }
            }
            if (ctxs.Count == 0) ctxs.Add(new KeyValuePair<string, MatchContext>("none", new MatchContext()));

            var sb = new StringBuilder();
            try
            {
                var conf = new ConfParser(confPath);
                conf.Parse();

                sb.AppendLine("[dump-order] conf=" + confPath);
                sb.AppendLine("[dump-order] groups=" + conf.Groups.Count);
                sb.AppendLine("[dump-order] order=" + MenuOrderSpec.ToConf(MenuOrderSpec.Parse(orderRaw))
                              + (orderGiven ? "（来自 --order）" : "（未给 --order ⇒ 默认序）"));

                foreach (var kv in ctxs)
                {
                    var d = MatchEngine.Decide(kv.Value, conf.Groups);
                    var flat = d.Flat(MenuOrderSpec.Parse(orderRaw));

                    sb.AppendLine();
                    sb.AppendLine("--- ctx=" + kv.Key
                                  + "  rule=" + d.Rule
                                  + "  dynamic=" + d.DynamicCount
                                  + "  fallback=" + (d.FallbackShown ? 1 : 0)
                                  + "  process=" + d.ProcessCount
                                  + "  always=" + d.AlwaysCount
                                  + "  违规=" + d.Violations.Count
                                  + "  ⇒ 实际显示 " + flat.Count + " 组");
                    foreach (var v in d.Violations)
                        sb.AppendLine("    [违规] " + v.Kind + "  " + v.Detail + (v.Line > 0 ? "  @line " + v.Line : ""));
                    for (int i = 0; i < flat.Count; i++)
                    {
                        var g = flat[i];
                        sb.AppendLine("    " + (i + 1) + ". [" + g.Type + "] " + g.Name
                                      + "  条目=" + (g.Items == null ? 0 : g.Items.Count));
                    }
                    if (flat.Count == 0) sb.AppendLine("    （空菜单 —— 所有槽位都没命中）");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("[dump-order] FAIL " + ex.GetType().Name + ": " + ex.Message);
                sb.AppendLine(ex.StackTrace);
                WriteOut(outFile, sb);
                return 1;
            }

            WriteOut(outFile, sb);
            return 0;
        }

        private static void WriteOut(string outFile, StringBuilder sb)
        {
            try
            {
                string dir = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>构造判定上下文：none / text / file:pdf,md / folder / mixed / proc:qq.exe。</summary>
        private static KeyValuePair<string, MatchContext> MakeCtx(string spec)
        {
            string s = (spec ?? "").Trim();
            var ctx = new MatchContext();
            string shown = s.Length == 0 ? "none" : s;

            if (s.StartsWith("proc:", StringComparison.OrdinalIgnoreCase))
            {
                ctx.ProcessName = s.Substring(5);
            }
            else if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Kind = SelectionKind.File;
                ctx.FileCount = 1;
                foreach (string e in s.Substring(5).Split(','))
                {
                    string n = e.Trim().TrimStart('.').ToLowerInvariant();
                    if (n.Length > 0) ctx.ExtensionsLower.Add(n);
                }
            }
            else if (string.Equals(s, "folder", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Kind = SelectionKind.Folder;
                ctx.FolderCount = 1;
            }
            else if (string.Equals(s, "mixed", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Kind = SelectionKind.Mixed;
                ctx.FileCount = 1;
                ctx.FolderCount = 1;
            }
            else if (string.Equals(s, "text", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Kind = SelectionKind.Text;
                ctx.Text = "hi";
            }

            return new KeyValuePair<string, MatchContext>(shown, ctx);
        }
    }
}
