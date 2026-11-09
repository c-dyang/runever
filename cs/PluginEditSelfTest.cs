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
    ///   ⑩–⑫ 「配置块」面板：FormatMetaText ↔ ParseMetaText 往返逐字符一致、
    ///         文件扫描与粘贴解析同源、Out=cmd 明确报错、缺键取默认值不抛
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

                // ============================================================
                // ⑩–⑫ 「配置块」面板（L4/P）：解析器只写一份 + 往返一致 + cmd 报错
                // ============================================================

                // ⑩ ParseMetaText 往返一致（配置块 → PluginInfo → 再生成 → 逐字符相同）
                var meta = new PluginInfo
                {
                    Name = "顺丰快递",
                    Match = "^SF\\d{10,}$",
                    Whole = true,
                    Prio = 100,
                    Out = "url",
                    Tpl = "https://www.sf-express.com/chn/sc/waybill/waybill-detail/{0}",
                    Icon = "chrome.exe",
                    Show = 1
                };
                string block = PluginScanner.FormatMetaText(meta);
                var rt = PluginScanner.ParseMetaText(block);
                string block2 = PluginScanner.FormatMetaText(rt);

                chk(block == block2, "⑩ 配置块往返逐字符相同（配置块 → PluginInfo → 再生成）",
                    block == block2 ? "" : "生成差异：" + block.Length + " vs " + block2.Length);
                chk(rt.Name == meta.Name && rt.Match == meta.Match && rt.Out == meta.Out
                    && rt.Tpl == meta.Tpl && rt.Icon == meta.Icon && rt.Whole == meta.Whole
                    && rt.Prio == meta.Prio && rt.Show == meta.Show && rt.Enabled == meta.Enabled,
                    "⑩ 往返后字段值一致");
                chk(string.IsNullOrEmpty(rt.BadReason), "⑩ 往返后无 BadReason", rt.BadReason);

                // 同一份文本走文件扫描入口 == 走粘贴解析入口（证明只有一套解析器）
                string rtFile = Path.Combine(dir, "roundtrip.ahk");
                File.WriteAllText(rtFile, block, new UTF8Encoding(false));
                var viaFile = new PluginScanner(root).ReadMeta(rtFile);
                chk(viaFile != null, "⑩ 文件扫描入口可读同一份配置块");
                if (viaFile != null)
                {
                    chk(viaFile.Name == rt.Name && viaFile.Match == rt.Match && viaFile.Out == rt.Out
                        && viaFile.Tpl == rt.Tpl && viaFile.Icon == rt.Icon && viaFile.Whole == rt.Whole
                        && viaFile.Prio == rt.Prio && viaFile.Show == rt.Show,
                        "⑩ 文件扫描与粘贴解析结果一致（共用同一套正则）");
                }

                // 文件里那种「Tab 对齐 + 行尾注释」的写法也必须能读，规范化后仍然幂等
                string messy =
                    "global RVE_FN_Name\t:=\t\"Tab 写法\"\t; 行尾注释\r\n"
                    + "global RVE_FN_Match\t:=\t\"^tab$\"\r\n"
                    + "global RVE_FN_Out\t:=\t\"action\"\r\n"
                    + "global RVE_FN_Enabled\t:=\tfalse ; 停用\r\n";
                var pTab = PluginScanner.ParseMetaText(messy);
                chk(pTab.Name == "Tab 写法" && pTab.Out == "action" && !pTab.Enabled,
                    "⑩ Tab/行尾注释写法可解析（与文件同一口径）",
                    "name=" + pTab.Name + " out=" + pTab.Out + " enabled=" + pTab.Enabled);
                string fmt1 = PluginScanner.FormatMetaText(pTab);
                chk(fmt1 == PluginScanner.FormatMetaText(PluginScanner.ParseMetaText(fmt1)),
                    "⑩ 规范化后再往返仍逐字符相同（幂等）");

                // ⑪ Out := "cmd" 必须返回明确错误（历史炸点）
                string cmdBlock =
                    "global RVE_FN_Name  := \"cmd 炸点\"\r\n"
                    + "global RVE_FN_Match := \"^cmdtest$\"\r\n"
                    + "global RVE_FN_Out   := \"cmd\"\r\n";
                PluginInfo pCmd = null;
                bool cmdNoThrow = true;
                try { pCmd = PluginScanner.ParseMetaText(cmdBlock); }
                catch { cmdNoThrow = false; }
                chk(cmdNoThrow && pCmd != null, "⑪ Out=cmd 解析不抛异常");
                if (pCmd != null)
                {
                    chk(!string.IsNullOrEmpty(pCmd.BadReason), "⑪ Out=cmd 返回明确错误（BadReason 非空）",
                        pCmd.BadReason);
                    chk(pCmd.BadReason.Contains("cmd"), "⑪ 错误文本点名 cmd", pCmd.BadReason);
                    chk(pCmd.BadReason.Contains("action"), "⑪ 错误文本给出正确写法 action", pCmd.BadReason);
                }
                chk(PluginScanner.CheckOutType("cmd").Contains("action"),
                    "⑪ CheckOutType(cmd) 指出应写 action");
                chk(PluginScanner.CheckOutType("action") == "", "⑪ CheckOutType(action) 合法");

                string cmdFile = Path.Combine(dir, "bad-cmd.ahk");
                File.WriteAllText(cmdFile, cmdBlock, new UTF8Encoding(false));
                var viaFileBad = new PluginScanner(root).ReadMeta(cmdFile);
                chk(viaFileBad != null && !string.IsNullOrEmpty(viaFileBad.BadReason),
                    "⑪ 文件扫描入口同样判 Out=cmd 非法（进 BadPlugins）");

                // ⑫ 缺键用默认值、不抛异常
                string minBlock =
                    "global RVE_FN_Name  := \"只有必需键\"\r\n"
                    + "global RVE_FN_Match := \"^mini$\"\r\n"
                    + "global RVE_FN_Out   := \"run\"\r\n";
                var pDef = PluginScanner.ParseMetaText(minBlock);
                chk(pDef.Whole && pDef.Prio == 100 && pDef.Show == -1 && pDef.Enabled
                    && pDef.Arg == "text" && !pDef.Menu && pDef.Tpl == "" && pDef.Icon == "",
                    "⑫ 缺键取默认值（Whole=true / Prio=100 / Show=-1 / Enabled / Arg=text / Menu=false）",
                    "whole=" + pDef.Whole + " prio=" + pDef.Prio + " show=" + pDef.Show
                    + " enabled=" + pDef.Enabled + " arg=" + pDef.Arg + " menu=" + pDef.Menu);
                chk(string.IsNullOrEmpty(pDef.BadReason), "⑫ 必需键齐全时无 BadReason", pDef.BadReason);

                bool emptyOk = true, nullOk = true;
                try
                {
                    var pe = PluginScanner.ParseMetaText("");
                    if (pe == null || !pe.BadReason.Contains("Name")) emptyOk = false;
                }
                catch { emptyOk = false; }
                chk(emptyOk, "⑫ 空文本返回 BadReason（缺 Name）而不是抛异常");

                try { if (PluginScanner.ParseMetaText(null) == null) nullOk = false; }
                catch { nullOk = false; }
                chk(nullOk, "⑫ null 输入不抛异常");

                // ⑬ 按块覆盖：只改写块里出现的键，Run() 脚本体原样不动
                string ovFile = Path.Combine(dir, "overwrite.ahk");
                var ovSrc = new StringBuilder();
                ovSrc.AppendLine("#Requires AutoHotkey v2.0");
                ovSrc.AppendLine("global RVE_FN_Name  := \"覆盖前\"");
                ovSrc.AppendLine("global RVE_FN_Match := \"^before$\"");
                ovSrc.AppendLine("global RVE_FN_Out   := \"action\"");
                ovSrc.AppendLine("global RVE_FN_Icon  := \"SHELL32.dll,71\"");
                ovSrc.AppendLine();
                ovSrc.AppendLine("Run(text) {");
                ovSrc.AppendLine("    return \"choco install \" text");
                ovSrc.AppendLine("}");
                File.WriteAllText(ovFile, ovSrc.ToString(), new UTF8Encoding(false));

                string ovBlock =
                    "global RVE_FN_Name  := \"覆盖后\"\r\n"
                    + "global RVE_FN_Match := \"^after$\"\r\n"
                    + "global RVE_FN_Out   := \"action\"\r\n";
                var ovWant = PluginScanner.ParseMetaText(ovBlock);

                int ovDone = 0;
                foreach (var k in ovWant.PresentKeys)
                {
                    string ck = PluginScanner.CanonicalKey(k);
                    if (ck == null) continue;
                    if (PluginPage.WriteMetaFlag(ovFile, ck, PluginScanner.MetaValueText(ovWant, ck)))
                        ovDone++;
                }
                chk(ovDone == ovWant.PresentKeys.Count, "⑬ 按块覆盖写入了块里出现的全部键",
                    ovDone + " / " + ovWant.PresentKeys.Count);

                var ovBack = new PluginScanner(root).ReadMeta(ovFile);
                chk(ovBack != null && ovBack.Name == "覆盖后" && ovBack.Match == "^after$",
                    "⑬ 覆盖后元数据读回新值",
                    ovBack == null ? "读回失败" : ovBack.Name + " / " + ovBack.Match);
                chk(ovBack != null && ovBack.Icon == "SHELL32.dll,71",
                    "⑬ 块里没出现的键保持原样（Icon 未被清空）",
                    ovBack == null ? "" : ovBack.Icon);

                string ovText = File.ReadAllText(ovFile, Encoding.UTF8);
                chk(ovText.Contains("return \"choco install \" text"),
                    "⑬ 脚本体 Run() 原样保留（覆盖只碰元数据行）");

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
