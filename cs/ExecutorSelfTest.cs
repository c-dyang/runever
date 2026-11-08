using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 执行能力矩阵自检（AC-9 判据）—— run / open / url / action / send / keys / evsearch
    /// 逐项 exit 码断言 + 占位符 `%s %text% %file% %clip% %win% %proc%` 逐项替换断言。
    ///
    /// 用法：RunEverCs.exe --selftest-exec &lt;root&gt; &lt;out.txt&gt;
    ///
    /// ★ 桩设计（**不得真启动 GUI / 不得真发键到桌面**）：
    ///   · `run`   —— 用 `C:\Windows\System32\cmd.exe /c exit 0`（无窗口、立即退出）
    ///   · `action`—— 用 `cmd /c echo`（隐藏窗口）
    ///   · `open` / `url` / `send` / `keys` —— 走 `DryRun`（只记录展开结果，不真动作）
    /// ★ `evsearch` 本段只留分发位：断言返回**明确错误码**（不得静默成功）。
    /// </summary>
    public static class ExecutorSelfTest
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;

            Action<bool, string, string> chk = null;
            chk = (cond, label, extra) =>
            {
                if (cond) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (extra != "" ? "   <<< " + extra : "")); }
            };
            Action<bool, string> ok = (cond, label) => chk(cond, label, "");

            string tmp = Path.Combine(Path.GetTempPath(), "rve_exec_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                sb.AppendLine("=== 执行能力矩阵自检（run/open/url/action/send/keys/evsearch + 占位符）===");
                sb.AppendLine("root = " + root);
                sb.AppendLine();

                Directory.CreateDirectory(tmp);
                string realFile = Path.Combine(tmp, "存在的文件.txt");
                File.WriteAllText(realFile, "x", new UTF8Encoding(false));
                string gonePath = Path.Combine(tmp, "已经不存在", "rveprobe.exe");
                string cacheFile = Path.Combine(tmp, "paths.txt");
                File.WriteAllLines(cacheFile, new[]
                {
                    "rveprobe.exe=" + gonePath
                }, new UTF8Encoding(false));

                var cache = new PathCache(cacheFile);
                var ex = new Executor(cache);
                var notices = new List<string>();
                ex.ErrorNotify = s => notices.Add(s);

                string cmdExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "cmd.exe");
                chk(File.Exists(cmdExe), "夹具：cmd.exe 存在（run 用例的非 GUI 目标）", cmdExe);

                // ---------------- 分发位：未知类型 / 空任务 ----------------
                sb.AppendLine();
                sb.AppendLine("--- 能力分发 ---");
                chk(!ex.Execute(new Executor.Task { Type = "no-such-action", Target = "x" })
                    && ex.LastErrorCode == "unknown-action",
                    "未知动作类型 ⇒ false + unknown-action（不静默成功）", ex.LastErrorCode);
                chk(!ex.Execute(null) && ex.LastErrorCode == "bad-task",
                    "空任务 ⇒ false + bad-task", ex.LastErrorCode);

                // ---------------- run（真启动，非 GUI） ----------------
                sb.AppendLine();
                sb.AppendLine("--- run ---");
                bool runOk = ex.Execute(new Executor.Task
                {
                    Type = "run",
                    Target = cmdExe + " /c exit 0"
                });
                chk(runOk && ex.LastErrorCode == "",
                    "run：带参数的绝对路径程序 ⇒ 启动成功（cmd /c exit 0）", ex.LastError);

                // ---- run：解析不到的程序（缓存未命中 + 系统解析失败 ⇒ 交 ShellExecute，失败即报错）----
                bool runBad = ex.Execute(new Executor.Task { Type = "run", Target = "notexist-rve-xyz.exe" });
                chk(!runBad && (ex.LastErrorCode == "run-missing" || ex.LastErrorCode == "run-launch-failed")
                    && !string.IsNullOrEmpty(ex.LastError),
                    "run：解析不到的程序 ⇒ false + 明确错误码（**禁静默**，走 ShellExecute 兜底后报错）",
                    ex.LastErrorCode + " / " + ex.LastError);
                chk(notices.Count >= 1
                    && notices[notices.Count - 1].IndexOf("notexist-rve-xyz", StringComparison.Ordinal) >= 0,
                    "run：失败已推送到用户可见通道（ErrorNotify 内容含程序名）",
                    "通知数=" + notices.Count);

                // ---- run：缓存命中但文件不存在（软件移位）+ 未接线现查 ⇒ 明确报错 ----
                bool staleNoHook = ex.Execute(new Executor.Task { Type = "run", Target = "rveprobe.exe /c exit 0" });
                chk(!staleNoHook && ex.LastErrorCode == "run-missing"
                    && ex.LastError.IndexOf("缓存路径=", StringComparison.Ordinal) >= 0,
                    "run：缓存指向已删文件且现查未命中 ⇒ run-missing（含缓存路径，不再\"点了没反应\"）",
                    ex.LastErrorCode + " / " + ex.LastError);

                // ---------------- run：时机③ 现查回写（钩子） ----------------
                int hookCalls = 0;
                ex.OnDemandResolve = delegate (string prog)
                {
                    hookCalls++;
                    if (prog.Equals("rveprobe.exe", StringComparison.OrdinalIgnoreCase)) return cmdExe;
                    return "";
                };
                bool runMoved = ex.Execute(new Executor.Task
                {
                    Type = "run",
                    Target = "rveprobe.exe /c exit 0"
                });
                chk(runMoved && hookCalls == 1,
                    "run：缓存指向已删文件（软件移位）⇒ 触发现查钩子并改用现查结果启动",
                    "hookCalls=" + hookCalls + " err=" + ex.LastError);
                ex.OnDemandResolve = null;

                // ---------------- open（DryRun） ----------------
                sb.AppendLine();
                sb.AppendLine("--- open ---");
                ex.DryRun = true;
                bool openOk = ex.Execute(new Executor.Task { Type = "open", Target = "\"" + realFile + "\"" });
                chk(openOk && ex.LastOpenTarget == realFile,
                    "open：存在路径 ⇒ 成功（DryRun 只记录不真打开）", ex.LastOpenTarget);
                chk(!ex.Execute(new Executor.Task { Type = "open", Target = Path.Combine(tmp, "没有这个.txt") })
                    && ex.LastErrorCode == "open-not-found",
                    "open：路径不存在 ⇒ false + open-not-found", ex.LastErrorCode);
                // 多目标（契约 §6："多目标按引号分隔"）—— 逐个处理，每个都记录
                string realFile2 = Path.Combine(tmp, "存在的文件2.txt");
                File.WriteAllText(realFile2, "y", new UTF8Encoding(false));
                var openLogs = new List<string>();
                bool openMulti = ex.Execute(new Executor.Task
                {
                    Type = "open",
                    Target = "\"" + realFile + "\" \"" + realFile2 + "\"",
                    Log = s => openLogs.Add(s)
                });
                int openHits = openLogs.FindAll(s => s.StartsWith("DryRun open", StringComparison.Ordinal)).Count;
                chk(openMulti && openHits == 2
                    && openLogs.Exists(s => s.StartsWith("DryRun open", StringComparison.Ordinal)
                                            && s.IndexOf(realFile, StringComparison.Ordinal) >= 0)
                    && openLogs.Exists(s => s.StartsWith("DryRun open", StringComparison.Ordinal)
                                            && s.IndexOf(realFile2, StringComparison.Ordinal) >= 0),
                    "open：多目标（引号分隔）逐个打开 ⇒ 两个目标都被处理（DryRun 记录 2 条）",
                    "成功=" + openMulti + " 打开记录=" + openHits + " 日志=" + string.Join(" | ", openLogs.ToArray()));
                ex.DryRun = false;

                // ---------------- url + 占位符展开 ----------------
                sb.AppendLine();
                sb.AppendLine("--- url（含占位符展开） ---");
                ex.DryRun = true;
                var tUrl = new Executor.Task
                {
                    Type = "url",
                    Target = "https://x/?q=%s&t=%text%&f=%file%&c=%clip%&w=%win%&p=%proc%",
                    Text = "选中文本",
                    Clipboard = "剪贴板",
                    WinTitle = "窗口",
                    Proc = "chrome.exe",
                    Files = new List<string> { realFile }
                };
                bool urlOk = ex.Execute(tUrl);
                string expectUrl = "https://x/?q=选中文本&t=选中文本&f=" + realFile + "&c=剪贴板&w=窗口&p=chrome.exe";
                chk(urlOk && ex.LastUrl == expectUrl,
                    "url：模板注入占位符后打开（DryRun 记录展开串）",
                    "实际=" + ex.LastUrl);
                ok(!ex.Execute(new Executor.Task { Type = "url", Target = "" })
                    && ex.LastErrorCode == "url-empty-target",
                    "url：空目标 ⇒ false + url-empty-target");

                // ---------------- action（真跑 cmd，隐藏窗口） ----------------
                sb.AppendLine();
                sb.AppendLine("--- action ---");
                ex.DryRun = false;
                bool actOk = ex.Execute(new Executor.Task
                {
                    Type = "action",
                    Target = "echo RVE_EXEC_SELFTEST",
                    Flag = ActionFlag.Hide
                });
                chk(actOk, "action：命令 + 隐藏窗口 ⇒ 执行成功（cmd /c echo）", ex.LastError);
                ex.DryRun = true;
                ok(ex.Execute(new Executor.Task { Type = "action", Target = "echo x", Flag = ActionFlag.None }),
                    "action：flag 缺省（= 隐藏）+ DryRun ⇒ 记录并成功");
                // flag 的 **Show 分支**（契约 §6："flag 控制窗口显示 / 隐藏"）：
                //   Flag=Show ⇒ show=1；Flag 缺省 + Show=true（AutoMatcher 的 RVE_FN_Show 路径）⇒ show=1。
                //   DryRun 不真开窗口，但**显示/隐藏判定分支**由日志 "show=1" 断言。
                var actLogs = new List<string>();
                bool actShow = ex.Execute(new Executor.Task
                {
                    Type = "action", Target = "echo x", Flag = ActionFlag.Show,
                    Log = s => actLogs.Add(s)
                });
                chk(actShow && actLogs.Exists(s => s.IndexOf("show=1", StringComparison.Ordinal) >= 0),
                    "action：Flag=Show ⇒ 走显示分支（DryRun 断言日志 show=1，不真开窗口）",
                    string.Join(" | ", actLogs.ToArray()));
                var actLogs2 = new List<string>();
                bool actShow2 = ex.Execute(new Executor.Task
                {
                    Type = "action", Target = "echo x", Show = true,
                    Log = s => actLogs2.Add(s)
                });
                chk(actShow2 && actLogs2.Exists(s => s.IndexOf("show=1", StringComparison.Ordinal) >= 0),
                    "action：Show=true（Flag 缺省，AutoMatcher RVE_FN_Show 路径）⇒ 同样走显示分支",
                    string.Join(" | ", actLogs2.ToArray()));

                // ---------------- send / keys（DryRun，不真发键） ----------------
                sb.AppendLine();
                sb.AppendLine("--- send / keys ---");
                bool sendOk = ex.Execute(new Executor.Task { Type = "send", Target = "a+b" });
                chk(sendOk && ex.LastSend == "a{+}b",
                    "send：文本经 SendKeys 转义后记录（DryRun 不真发键）", "LastSend=" + ex.LastSend);
                bool keysOk = ex.Execute(new Executor.Task { Type = "keys", Target = "^c" });
                chk(keysOk && ex.LastKeys == "^c", "keys：按键序列记录（DryRun 不真发键）", ex.LastKeys);
                ex.DryRun = false;

                // ---------------- evsearch（只留分发位） ----------------
                sb.AppendLine();
                sb.AppendLine("--- evsearch（本段只留分发位）---");
                chk(!ex.Execute(new Executor.Task { Type = "evsearch", Target = "%file%" })
                    && ex.LastErrorCode == "evsearch-not-implemented",
                    "evsearch：返回**未实现**明确错误码（不得静默返回成功）", ex.LastErrorCode);

                // ---------------- 占位符逐项（唯一源 = PlaceholderExpander） ----------------
                sb.AppendLine();
                sb.AppendLine("--- 占位符逐项断言 ---");
                var pc = new MatchContext
                {
                    Text = "T",
                    Clipboard = "C",
                    WinTitle = "W",
                    ProcessName = "P",
                    FirstFilePath = "F"
                };
                ok(PlaceholderExpander.Expand("%text%", pc) == "T", "%text% → 选中文本");
                ok(PlaceholderExpander.Expand("%file%", pc) == "F", "%file% → 首个选中文件");
                ok(PlaceholderExpander.Expand("%clip%", pc) == "C", "%clip% → 剪贴板");
                ok(PlaceholderExpander.Expand("%win%", pc) == "W", "%win% → 窗口标题");
                ok(PlaceholderExpander.Expand("%proc%", pc) == "P", "%proc% → 进程名");

                // %s 两分支
                ok(PlaceholderExpander.Expand("%s", pc) == "T", "%s 分支①：有选中文本 ⇒ 用文本");
                var pcNoText = new MatchContext { Text = "", Clipboard = "C" };
                ok(PlaceholderExpander.Expand("%s", pcNoText) == "C", "%s 分支②：无文本 ⇒ 回落剪贴板");

                // 边界
                ok(PlaceholderExpander.Expand("%file%", new MatchContext()) == "",
                    "%file% 边界：无选中文件 ⇒ 空串");
                ok(PlaceholderExpander.Expand("%clip%", new MatchContext()) == "",
                    "%clip% 边界：剪贴板空 ⇒ 空串");
                ok(PlaceholderExpander.Expand("a%%b", pc) == "a%%b",
                    "边界：模板含 %% ⇒ 原样保留（不是占位符）");
                ok(PlaceholderExpander.Expand("%TEXT%", pc) == "%TEXT%",
                    "边界：占位符名**大小写敏感**（%TEXT% 不替换 —— 现役口径，逐字保留）");
                ok(PlaceholderExpander.Expand("", pc) == "", "边界：空模板 ⇒ 空串");
                ok(PlaceholderExpander.Expand("%s", null) == "%s", "边界：ctx=null ⇒ 原样返回");

                // 唯一源：Executor.ReplaceVars 必须等于 PlaceholderExpander.Expand
                var t2 = new Executor.Task
                {
                    Text = "T",
                    Clipboard = "C",
                    WinTitle = "W",
                    Proc = "P",
                    Files = new List<string> { "F" }
                };
                string tpl = "%text%|%file%|%clip%|%win%|%proc%|%s";
                chk(Executor.ReplaceVars(tpl, t2)
                    == PlaceholderExpander.Expand(tpl, Executor.ToMatchContext(t2)),
                    "唯一源：Executor.ReplaceVars == PlaceholderExpander.Expand（不存两份替换逻辑）", "");

                // ------------------------------------------------------------
                // ★ Q7 修订（契约 §6，2026-10-04）：
                //   旧口径「不做自动追加」**作废** ⇒ 参数为空时自动追加选中文件（夹）/ 文本。
                //   改写为三条：① ReplaceVars 仍是**纯展开**（追加不在这一层，唯一源断言不受影响）；
                //   ② 「含占位符」= 精确 token 判定；③ Execute 层的 5 分支（B1–B6）。
                // ------------------------------------------------------------
                sb.AppendLine();
                sb.AppendLine("--- Q7 修订：参数为空 ⇒ 自动追加选中内容（契约 §6）---");
                ok(Executor.ReplaceVars("固定参数", t2) == "固定参数",
                    "Q7 修订①：ReplaceVars 仍是纯展开 —— 不含自动追加（追加只在 Execute 层）");
                chk(!PlaceholderExpander.ContainsPlaceholder("固定参数")
                    && !PlaceholderExpander.ContainsPlaceholder("C:\\100%\\out")
                    && !PlaceholderExpander.ContainsPlaceholder("%TEXT%")
                    && PlaceholderExpander.ContainsPlaceholder("%file%")
                    && PlaceholderExpander.ContainsPlaceholder("x %s y")
                    && PlaceholderExpander.ContainsPlaceholder("%proc%"),
                    "Q7 修订②：含占位符 = 精确 token 判定（C:\\100%\\out 不算；%TEXT% 大小写敏感）",
                    "HasPlaceholder('C:\\100%\\out')=" + PlaceholderExpander.HasPlaceholder("C:\\100%\\out"));

                // 取 Execute 展开后的目标串（DoAction 日志里的 resolvedTarget）
                Func<List<string>, string> pickResolved = delegate (List<string> ls)
                {
                    foreach (var s in ls)
                    {
                        int i = s.IndexOf("resolvedTarget=[", StringComparison.Ordinal);
                        if (i < 0) continue;
                        int st = i + "resolvedTarget=[".Length;
                        int en = s.LastIndexOf(']');
                        if (en > st) return s.Substring(st, en - st);
                    }
                    return "<无日志>";
                };
                Func<Executor.Task, string> resolved = delegate (Executor.Task tk)
                {
                    var lg = new List<string>();
                    tk.Log = s => lg.Add(s);
                    ex.Execute(tk);
                    return pickResolved(lg);
                };

                ex.DryRun = true;

                // B1：open + 参数为空（空目标）+ 选中文件（多个）⇒ 追加各自带引号的完整路径
                var b1logs = new List<string>();
                var b1t = new Executor.Task
                {
                    Type = "open",
                    Target = "",
                    Files = new List<string> { realFile, realFile2 },
                    Log = s => b1logs.Add(s)
                };
                bool b1ok = ex.Execute(b1t);
                int b1hits = b1logs.FindAll(s => s.StartsWith("DryRun open", StringComparison.Ordinal)).Count;
                chk(b1ok && b1hits == 2
                    && string.CompareOrdinal(pickResolved(b1logs),
                        "\"" + realFile + "\" \"" + realFile2 + "\"") == 0,
                    "B1 open + 参数为空 + 选中文件 ⇒ 追加 \"p1\" \"p2\"（各自带引号、空格分隔）",
                    "hits=" + b1hits + " resolved=[" + pickResolved(b1logs) + "]"
                    + " 期望=[" + "\"" + realFile + "\" \"" + realFile2 + "\"]");

                // B2：action + 参数为空 + 只选中文本 ⇒ 追加该文本原文（不加引号）
                var b2t = new Executor.Task { Type = "action", Target = "cmd.exe", Text = "选中文本" };
                string b2 = resolved(b2t);
                ok(string.CompareOrdinal(b2, "cmd.exe 选中文本") == 0,
                    "B2 action + 参数为空 + 只选中文本 ⇒ 追加文本原文（不加引号）：" + b2);

                // B3：无选中（Files 空 && Text 空）⇒ 不追加
                var b3t = new Executor.Task { Type = "action", Target = "cmd.exe" };
                string b3 = resolved(b3t);
                ok(string.CompareOrdinal(b3, "cmd.exe") == 0, "B3 无选中 ⇒ 不追加，目标原样：" + b3);

                // B4：参数含占位符 ⇒ 按占位符替换，不追加（防重复）
                var b4t = new Executor.Task
                {
                    Type = "action",
                    Target = "cmd.exe %file%",
                    Files = new List<string> { realFile }
                };
                string b4 = resolved(b4t);
                ok(string.CompareOrdinal(b4, "cmd.exe " + realFile) == 0,
                    "B4 参数含占位符 ⇒ 按占位符替换、不追加（无重复）：" + b4);

                // B6：文件名位写着占位符（Target 整个含 token）⇒ 不追加（D-2 口径 = 整个 raw）
                var b6t = new Executor.Task
                {
                    Type = "action",
                    Target = "%file%",
                    Files = new List<string> { realFile }
                };
                string b6 = resolved(b6t);
                ok(string.CompareOrdinal(b6, realFile) == 0,
                    "B6 整个 Target 含占位符（%file% 在文件名位）⇒ 不追加，防重复：" + b6);

                // 不回写 t.Target：同一条目连跑两次，结果必须逐字符一致（热键可连续触发）
                var b7t = new Executor.Task
                {
                    Type = "action",
                    Target = "cmd.exe",
                    Files = new List<string> { realFile, realFile2 }
                };
                string b7a = resolved(b7t);
                string b7b = resolved(b7t);
                chk(string.CompareOrdinal(b7a, b7b) == 0
                    && string.CompareOrdinal(b7t.Target ?? "", "cmd.exe") == 0,
                    "防重复：Execute 不回写 t.Target（连跑两次结果一致，Target 仍为原值）",
                    "第一次=[" + b7a + "] 第二次=[" + b7b + "] Target=[" + b7t.Target + "]");

                // B5：url / send / keys + 参数为空 + 有选中 ⇒ 不追加（不适用）
                var b5url = new Executor.Task
                {
                    Type = "url", Target = "", Files = new List<string> { realFile }
                };
                bool b5urlOk = !ex.Execute(b5url) && ex.LastErrorCode == "url-empty-target";
                var b5send = new Executor.Task
                {
                    Type = "send", Target = "a+b", Files = new List<string> { realFile }
                };
                bool b5sendOk = ex.Execute(b5send) && ex.LastSend == "a{+}b";
                var b5keys = new Executor.Task
                {
                    Type = "keys", Target = "^c", Files = new List<string> { realFile }
                };
                bool b5keysOk = ex.Execute(b5keys) && ex.LastKeys == "^c";
                chk(b5urlOk && b5sendOk && b5keysOk,
                    "B5 url / send / keys 不适用自动追加（url 空目标仍报 url-empty-target）",
                    "url=" + ex.LastErrorCode + " send=" + ex.LastSend + " keys=" + ex.LastKeys);

                ex.DryRun = false;
            }
            catch (Exception ex2)
            {
                fail++;
                sb.AppendLine("  FAIL 异常: " + ex2.Message + "\n" + ex2.StackTrace);
            }
            finally
            {
                try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
            }

            sb.AppendLine();
            sb.AppendLine("结果：PASS=" + pass + " FAIL=" + fail);
            sb.AppendLine(fail == 0 ? "全部通过" : "有失败");
            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return fail == 0 ? 0 : 1;
        }
    }
}
