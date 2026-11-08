using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Services;
using RunEverCs.Views;

namespace RunEverCs
{
    /// <summary>
    /// 插件编辑写回自检：
    ///   ① WriteMetaFlag 逐字段替换正确（保留注释）
    ///   ② 命令模板替换（形态 B 的 Run 里 return 表达式）
    ///   ③ 写回后语法仍合法（AHK /validate）
    ///   ④ 元数据能被扫描器正确读回
    /// 用法：RunEverCs.exe --selftest-pluginedit &lt;root&gt; &lt;out.txt&gt;
    /// </summary>
    public static class PluginEditSelfTest
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            void chk(bool c, string label, string extra = "")
            {
                if (c) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (extra != "" ? "   <<< " + extra : "")); }
            }

            string dir = Path.Combine(root, "state", "pluginedit-test");
            try
            {
                sb.AppendLine("=== 插件编辑写回自检 ===");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);

                // ---- 造一个带注释的插件样本（形态 B）----
                string p = Path.Combine(dir, "sample-plugin.ahk");
                var src = new StringBuilder();
                src.AppendLine("#Requires AutoHotkey v2.0");
                src.AppendLine("; 测试样本");
                src.AppendLine("global RVE_FN_Name  := \"原名字\"");
                src.AppendLine("global RVE_FN_Match := \"^test$\"");
                src.AppendLine("global RVE_FN_Whole := true    ; ★ 这是要保留的注释");
                src.AppendLine("global RVE_FN_Prio  := 100     ; 优先级注释");
                src.AppendLine("global RVE_FN_Out   := \"action\"");
                src.AppendLine("global RVE_FN_Show  := false");
                src.AppendLine();
                src.AppendLine("Run(text) {");
                src.AppendLine("    vCmd := Trim(text, \" `t`r`n\")");
                src.AppendLine("    if (vCmd = \"\")");
                src.AppendLine("        return \"\"");
                src.AppendLine("    return \"choco install \" text");
                src.AppendLine("}");
                File.WriteAllText(p, src.ToString(), new UTF8Encoding(false));

                // ---- ① 逐字段写回 ----
                chk(PluginPage.WriteMetaFlag(p, "Name", "\"新名字\""), "① 写回 Name");
                chk(PluginPage.WriteMetaFlag(p, "Match", "\"^newpat$\""), "① 写回 Match");
                chk(PluginPage.WriteMetaFlag(p, "Prio", "250"), "① 写回 Prio");
                chk(PluginPage.WriteMetaFlag(p, "Show", "true"), "① 写回 Show");

                string after = File.ReadAllText(p, Encoding.UTF8);
                const string TAB = "\t";

                // ② 注释保留（格式：global RVE_FN_X<TAB>:=<TAB>值<TAB>; 注释）
                chk(after.Contains("; ★ 这是要保留的注释"), "② Whole 行注释保留");
                chk(after.Contains("; 优先级注释"), "② Prio 行注释保留（改值后仍在）");
                chk(after.Contains(TAB + ";"), "② 注释前有 Tab 分隔（防值与分号粘连）");

                // ③ 值正确 + Tab 格式
                chk(after.Contains("global RVE_FN_Name" + TAB + ":=" + TAB + "\"新名字\""),
                    "③ Name 值已更新（Tab 格式）");
                chk(after.Contains("global RVE_FN_Match" + TAB + ":=" + TAB + "\"^newpat$\""),
                    "③ Match 值已更新");
                chk(after.Contains("global RVE_FN_Show" + TAB + ":=" + TAB + "true"),
                    "③ Show 值已更新");

                // ④ 无重复键
                int cntPrio = 0;
                foreach (var l in after.Split('\n'))
                    if (l.Contains("RVE_FN_Prio")) cntPrio++;
                chk(cntPrio == 1, "④ Prio 行有且仅有一行", "实得 " + cntPrio);

                int cntShow = 0;
                foreach (var l in after.Split('\n'))
                    if (l.Contains("RVE_FN_Show")) cntShow++;
                chk(cntShow == 1, "④ Show 行有且仅有一行", "实得 " + cntShow);

                // ⑤ 新增不存在的键
                chk(PluginPage.WriteMetaFlag(p, "Enabled", "false"), "⑤ 新增 Enabled 键");
                after = File.ReadAllText(p, Encoding.UTF8);
                chk(after.Contains("RVE_FN_Enabled"), "⑤ Enabled 已写入");

                // ⑥ 元数据可被扫描器读回
                var sc = new PluginScanner(root);
                var info = sc.ReadMeta(p);
                chk(info != null, "⑥ 扫描器可解析");
                if (info != null)
                {
                    chk(info.Name == "新名字", "⑥ 名称读回", info.Name);
                    chk(info.Match == "^newpat$", "⑥ 匹配式读回", info.Match);
                    chk(info.Prio == 250, "⑥ 优先级读回", info.Prio.ToString());
                    chk(info.Show == 1, "⑥ show 读回", info.Show.ToString());
                    chk(!info.Enabled, "⑥ enabled=false 读回", info.Enabled.ToString());
                }

                // ⑦ 语法仍合法
                string syntax = PluginTemplate.ValidateAhkSyntax(File.ReadAllText(p, Encoding.UTF8));
                chk(string.IsNullOrEmpty(syntax), "⑦ 写回后语法合法", syntax);

                // ⑧ 命令模板替换
                bool cmdOk = PluginEditDialog_ReplaceCommand(p, "choco install {0}", "winget install {0}");
                after = File.ReadAllText(p, Encoding.UTF8);
                chk(cmdOk, "⑧ 命令模板替换执行");
                chk(after.Contains("\"winget install \" text"), "⑧ 新命令已写入",
                    after.Contains("choco") ? "旧命令仍在" : "");

                string syntax2 = PluginTemplate.ValidateAhkSyntax(File.ReadAllText(p, Encoding.UTF8));
                chk(string.IsNullOrEmpty(syntax2), "⑧ 替换后语法合法", syntax2);

                // ⑨ 原子写无残留
                chk(!File.Exists(p + ".tmp"), "⑨ 无 .tmp 残留");

                Directory.Delete(dir, true);
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message + "\n" + ex.StackTrace);
                fail++;
            }

            sb.AppendLine();
            sb.AppendLine("=== 结果: 通过 " + pass + " / 失败 " + fail + " ===");
            File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
            return fail > 0 ? 1 : 0;
        }

        /// <summary>转发到 PluginEditDialog 的私有静态方法（通过反射，避免改可见性）。</summary>
        private static bool PluginEditDialog_ReplaceCommand(string path, string oldT, string newT)
        {
            var mi = typeof(PluginEditDialog).GetMethod("TryReplaceCommand",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (mi == null) return false;
            return (bool)mi.Invoke(null, new object[] { path, oldT, newT });
        }
    }
}
