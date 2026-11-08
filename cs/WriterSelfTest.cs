using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 写回器往返一致性自检：
    ///   解析 → 序列化 → 再解析 → 比对结构
    /// 并验证备份、回滚、原子写。
    /// 用法：RunEverCs.exe --selftest-writer &lt;menus.conf&gt; &lt;out.txt&gt;
    /// </summary>
    public static class WriterSelfTest
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string conf = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            Action<bool, string, string> chk = (cond, label, extra) =>
            {
                if (cond) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (extra != "" ? "   <<< " + extra : "")); }
            };

            try
            {
                sb.AppendLine("=== 写回器往返一致性自检 ===");

                // ---- 1. 解析原文件 ----
                var p1 = new ConfParser(conf);
                p1.Parse();
                int g1 = p1.Groups.Count;
                int i1 = 0; foreach (var g in p1.Groups) i1 += g.Items.Count;
                sb.AppendLine("原文件: 分组=" + g1 + " 条目=" + i1);

                // ---- 2. 序列化 ----
                string text = MenuConfWriter.Serialize(p1.Groups);

                // ---- 3. 写到临时文件再解析 ----
                string tmpConf = Path.Combine(Path.GetDirectoryName(outFile), "writer-roundtrip.conf");
                File.WriteAllText(tmpConf, text, new UTF8Encoding(false));

                var p2 = new ConfParser(tmpConf);
                p2.Parse();
                int g2 = p2.Groups.Count;
                int i2 = 0; foreach (var g in p2.Groups) i2 += g.Items.Count;

                chk(g2 == g1, "往返：分组数一致 (" + g2 + " = " + g1 + ")", g2 + " vs " + g1);
                chk(i2 == i1, "往返：条目数一致 (" + i2 + " = " + i1 + ")", i2 + " vs " + i1);

                // ---- 4. 逐字段比对 ----
                int diffs = 0;
                for (int gi = 0; gi < Math.Min(g1, g2); gi++)
                {
                    var a = p1.Groups[gi];
                    var b = p2.Groups[gi];
                    if (a.Name != b.Name) { diffs++; sb.AppendLine("    分组名差异: [" + a.Name + "] vs [" + b.Name + "]"); }
                    if (a.Type != b.Type) { diffs++; sb.AppendLine("    分组类型差异: " + a.Name); }
                    if (string.Join(",", a.Keywords) != string.Join(",", b.Keywords))
                    {
                        diffs++;
                        sb.AppendLine("    关键词差异 [" + a.Name + "]: ["
                            + string.Join(",", a.Keywords) + "] vs [" + string.Join(",", b.Keywords) + "]");
                    }

                    for (int ii = 0; ii < Math.Min(a.Items.Count, b.Items.Count); ii++)
                    {
                        var x = a.Items[ii];
                        var y = b.Items[ii];
                        if (x.Title != y.Title) { diffs++; sb.AppendLine("    标题差异: [" + x.Title + "] vs [" + y.Title + "]"); }
                        if (x.Type != y.Type) { diffs++; sb.AppendLine("    类型差异 [" + x.Title + "]: " + x.Type + " vs " + y.Type); }
                        if (x.Flag != y.Flag) { diffs++; sb.AppendLine("    flag差异 [" + x.Title + "]: " + x.Flag + " vs " + y.Flag); }
                        if (x.Target != y.Target) { diffs++; sb.AppendLine("    目标差异 [" + x.Title + "]: [" + x.Target + "] vs [" + y.Target + "]"); }
                        if (x.Level != y.Level) { diffs++; sb.AppendLine("    层级差异 [" + x.Title + "]: " + x.Level + " vs " + y.Level); }
                        if (x.Hotkey != y.Hotkey) { diffs++; sb.AppendLine("    快捷键差异 [" + x.Title + "]: " + x.Hotkey + " vs " + y.Hotkey); }
                    }
                }
                chk(diffs == 0, "往返：全字段逐条一致（差异 " + diffs + " 处）",
                    diffs > 0 ? "见上方明细" : "");

                // ---- 5. 备份 + 磁阵写 + 回读校验（在副本上做，不动真文件）----
                string workDir = Path.Combine(Path.GetDirectoryName(outFile), "writer-test");
                if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
                Directory.CreateDirectory(workDir);
                string workConf = Path.Combine(workDir, "menus.conf");
                File.Copy(conf, workConf, true);

                var w = new MenuConfWriter(workConf);
                string msg;
                bool ok = w.Save(p2.Groups, out msg);
                chk(ok, "Save 成功（含回读校验）", msg);

                var baks = Directory.GetFiles(workDir, "menus.conf.bak.*");
                chk(baks.Length == 1, "生成 1 份备份", "实得 " + baks.Length);

                chk(!File.Exists(workConf + ".tmp"), "原子写：无 .tmp 残留", "");

                // 回读校验后的文件应仍可解析
                var p3 = new ConfParser(workConf);
                p3.Parse();
                chk(p3.Groups.Count == g1, "保存后回读分组数一致", p3.Groups.Count + " vs " + g1);

                // ---- 6. 修改后再存（模拟编辑）----
                p3.Groups[0].Items[0].Title = "★改过的标题";
                p3.Groups[0].Items[0].Target = @"C:\test path\prog.exe --flag";
                bool ok2 = w.Save(p3.Groups, out msg);
                chk(ok2, "二次保存成功", msg);

                var p4 = new ConfParser(workConf);
                p4.Parse();
                chk(p4.Groups[0].Items[0].Title == "★改过的标题",
                    "编辑生效：标题", p4.Groups[0].Items[0].Title);
                chk(p4.Groups[0].Items[0].Target == @"C:\test path\prog.exe --flag",
                    "编辑生效：含空格目标", p4.Groups[0].Items[0].Target);

                // ---- 7. 新增分组/条目 ----
                var ng = new MenuGroup { Name = "新分组", Type = GroupType.Ext };
                ng.Keywords.AddRange(new[] { "foo", "bar" });
                ng.Items.Add(new MenuItem { Title = "测试项", Type = ActionType.Action, Flag = ActionFlag.Show, Target = "echo hi", Level = 1 });
                p4.Groups.Add(ng);
                bool ok3 = w.Save(p4.Groups, out msg);
                chk(ok3, "新增分组保存成功", msg);

                var p5 = new ConfParser(workConf);
                p5.Parse();
                var last = p5.Groups[p5.Groups.Count - 1];
                chk(last.Name == "新分组" && last.Type == GroupType.Ext,
                    "新分组往返正确", last.Name + "/" + last.Type);
                chk(last.Keywords.Count == 2, "新分组关键词数=2", last.Keywords.Count.ToString());
                chk(last.Items.Count == 1 && last.Items[0].Flag == ActionFlag.Show,
                    "新条目 flag=show 往返正确",
                    last.Items.Count > 0 ? last.Items[0].Flag.ToString() : "无条目");

                // ---- 8. 占位符清单一致性（界面承诺的必须真的生效）----
                //   界面 MenuEditorPage.Placeholders 与执行器 Executor.ReplaceVars
                //   必须一一对应，否则界面会向用户承诺一个实际不生效的占位符。
                int phChecked = 0, phFail = 0;
                foreach (var kv in Views.MenuEditorPage.Placeholders)
                {
                    var t = new Executor.Task
                    {
                        Text = "TXT", Clipboard = "CLIP", WinTitle = "WIN", Proc = "PROC",
                        Files = new System.Collections.Generic.List<string> { "FILE" }
                    };
                    string outv = Executor.ReplaceVars(kv.Key, t);
                    phChecked++;
                    bool replaced = outv != kv.Key;   // 被替换过 = 真的生效
                    if (replaced) pass++;
                    else
                    {
                        phFail++; fail++;
                        sb.AppendLine("  FAIL 占位符 " + kv.Key + " 未被执行器替换（界面承诺了但无效）");
                    }
                }
                if (phFail == 0)
                    sb.AppendLine("  OK   占位符清单一致：" + phChecked + " 个全部被执行器替换 ✓");

                // ---- 9. TargetSplit 往返（v8b AC-B5）----
                //   编辑期把 Target 拆成「文件名 / 参数」两框只是视图 ⇒ 拆分口径必须与
                //   运行期一致。这里对 8 种形状断言 Join(Split(t)) 与期望拆分结果。
                //   ⚠ 2026-10-05 口径修订（用户定案）：支持"未加引号的含空格路径"。
                //     ① 路径**前后引号剥离**（两框里显示干净路径）；② 回拼时**含空白即套引号**。
                //     ⇒ 判据随之改变：带引号/不带引号的含空格路径都**不再与原文逐字节相同**
                //       （R3/R4 因引号剥离后再套回、恰好仍逐字节；R6/R9 套了引号 ⇒ 不逐字节）。
                //     ⇒ 非逐字节的判据从旧的"分词等价"改为 **二次拆分幂等**
                //       （旧判据在新口径下必假 —— `C:\a b\x.exe` 被套引号后分词天然不同，那正是修复目标）。
                sb.AppendLine();
                sb.AppendLine("--- 目标「文件名 / 参数」拆分往返（唯一口径 = TargetSplit.Split）---");
                Func<string, string, string, bool> idemOk = (joined, ef, ea) =>
                {
                    string f2, a2;
                    TargetSplit.Split(joined, out f2, out a2);
                    if (string.CompareOrdinal(f2, ef) != 0 || string.CompareOrdinal(a2, ea) != 0) return false;
                    return string.CompareOrdinal(TargetSplit.Join(f2, a2), joined) == 0;
                };
                Action<string, string, string, bool> rt = (input, expFile, expArgs, byteExact) =>
                {
                    string f, a;
                    TargetSplit.Split(input, out f, out a);
                    string joined = TargetSplit.Join(f, a);
                    bool fileOk = string.CompareOrdinal(f, expFile) == 0;
                    bool argsOk = string.CompareOrdinal(a, expArgs) == 0;
                    bool joinOk = byteExact
                        ? string.CompareOrdinal(joined, input) == 0
                        : idemOk(joined, f, a);
                    chk(fileOk && argsOk && joinOk,
                        "拆分往返（" + (byteExact ? "逐字节" : "幂等") + "）：「"
                        + (input.Length > 36 ? input.Substring(0, 36) + "…" : input) + "」",
                        "file=[" + f + "]/期望[" + expFile + "] args=[" + a + "]/期望[" + expArgs + "]");
                };
                rt("notepad.exe", "notepad.exe", "", true);                                        // R1
                rt("notepad.exe %file%", "notepad.exe", "%file%", true);                           // R2
                rt("\"C:\\Program Files\\My App\\prog.exe\" --flag \"a b.txt\"",                   // R3 带引号+参数
                    "C:\\Program Files\\My App\\prog.exe", "--flag \"a b.txt\"", true);
                rt("\"C:\\有 空格\\中文程序.exe\"",                                                 // R4 带引号无参数
                    "C:\\有 空格\\中文程序.exe", "", true);
                rt("  notepad.exe   ", "notepad.exe", "", false);                                  // R5 首尾空白
                rt("C:\\没有引号的 空格路径.exe", "C:\\没有引号的 空格路径.exe", "", false);          // R6 ★未加引号含空格（无参数）
                rt("notepad.exe --a   --b", "notepad.exe", "--a   --b", true);                      // R7 内部多空格
                rt("C:\\Program Files\\Listary\\Listary.exe -a",                                   // R9 ★用户实报形状（无引号含空格 + 参数）
                    "C:\\Program Files\\Listary\\Listary.exe", "-a", false);
                {
                    var bigSb = new StringBuilder();
                    for (int bi = 0; bi < 375; bi++) bigSb.Append("参数段 ");     // 恰好 1500 字符（含中文与空格）
                    string bigArg = bigSb.ToString();
                    rt("notepad.exe " + bigArg, "notepad.exe", bigArg, true);                       // R8
                    string bf2, ba2;
                    TargetSplit.Split("notepad.exe " + bigArg, out bf2, out ba2);
                    chk(ba2.Length == 1500 && string.CompareOrdinal(bf2, "notepad.exe") == 0,
                        "R8 超长参数（1500 字符）无截断 / 无 Substring 越界",
                        "args.Length=" + ba2.Length);
                }
                // AppliesTo：只有 run/action/open 拆两框（D-1）
                chk(TargetSplit.AppliesTo(ActionType.Run)
                    && TargetSplit.AppliesTo(ActionType.Action)
                    && TargetSplit.AppliesTo(ActionType.Open)
                    && !TargetSplit.AppliesTo(ActionType.Url)
                    && !TargetSplit.AppliesTo(ActionType.Send)
                    && !TargetSplit.AppliesTo(ActionType.Keys)
                    && !TargetSplit.AppliesTo(ActionType.Group),
                    "两框化范围 = 仅 run / action / open（url / send / keys / group 保持单框）", "");

                // ---- 10. 空目标 = 非法配置（D-5：报告、不静默、不硬拦）----
                sb.AppendLine();
                sb.AppendLine("--- 空目标非法配置（自检口径，I-4 风格）---");
                var dg = new MenuGroup { Name = "空目标组", Type = GroupType.Always };
                dg.Items.Add(new MenuItem { Title = "空目标条", Type = ActionType.Run, Target = "" });
                int emptyCount = CountEmptyTargets(new List<MenuGroup> { dg });
                chk(emptyCount == 1,
                    "非法配置检测：空目标条目被计入（第 " + emptyCount + " 条目标为空）",
                    "实得 " + emptyCount);
                sb.AppendLine("  [INFO] 现役 menus.conf 空目标条数 = " + CountEmptyTargets(p1.Groups)
                    + "（0 = 无非法配置；保存时不阻断、仅在状态行警告，见 MenuEditorPage.OnSave）");

                // ---- 清理测试目录 ----
                try { Directory.Delete(workDir, true); } catch { }

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

        /// <summary>
        /// 目标为空的条目数 —— 自检侧的"非法配置"口径（D-5，与
        /// `MenuEditorPage.CountEmptyTargets` 同一判据：报告、不静默、不硬拦）。
        /// ★ `group` 类型（纯分组标题）目标**本来就空** ⇒ 不计入。
        /// </summary>
        private static int CountEmptyTargets(List<MenuGroup> groups)
        {
            int n = 0;
            if (groups == null) return 0;
            foreach (var g in groups)
            {
                if (g == null || g.Items == null) continue;
                foreach (var it in g.Items)
                {
                    if (it == null || it.Type == ActionType.Group) continue;
                    if (string.IsNullOrEmpty(it.Target) || it.Target.Trim().Length == 0) n++;
                }
            }
            return n;
        }
    }
}
