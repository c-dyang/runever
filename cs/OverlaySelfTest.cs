// ============================================================
// RunEver · 卡 F 自检（2026-11-17b）
//
//   --selftest-evsearch <root> <out.txt>   F1：evsearch 动作（Everything 直链 → es 兜底）
//   --selftest-overlay  <root> <out.txt>   F2：menulocal.conf 本机覆盖层
//
// 纪律：
//   · WinExe 无 stdout ⇒ 一律写 out.txt（UTF-8 无 BOM）；参数不足 ⇒ exit=2。
//   · **绝不**真打开 GUI / 真弹菜单：evsearch 用例全程 `DryRun=true`，
//     后端用注入钩子（`Executor.EvQuery` / `Executor.EsQuery`）造场景，不依赖本机装没装 Everything。
//   · 只在 `<root>\state\` 下建沙箱；结束后尽力清理。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    // ============================================================
    // F1 · evsearch
    // ============================================================
    public static class EvSearchSelfTest
    {
        public static int Run(string[] args)
        {
            if (args == null || args.Length < 3) return 2;
            string root = args[1];
            string outPath = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            Action<bool, string, string> chk = (cond, label, extra) =>
            {
                if (cond) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (string.IsNullOrEmpty(extra) ? "" : "   <<< " + extra)); }
            };

            sb.AppendLine("=== evsearch 自检（--selftest-evsearch）===");
            sb.AppendLine("  root: " + root);
            sb.AppendLine("  时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            string sandbox = Path.Combine(root, "state", "cb-f-evsearch");
            try
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
                Directory.CreateDirectory(sandbox);

                // 夹具根（没有 auto.conf ⇒ submenuLimit 取 AutoConf 默认值）
                string fixRoot = Path.Combine(sandbox, "root-default");
                Directory.CreateDirectory(Path.Combine(fixRoot, "config"));

                // ---------------- ① 库可用：命中数 ≤ submenuLimit ----------------
                sb.AppendLine("--- ① EV 可用：给 50 条命中 ⇒ 截到上限 ---");
                var ex1 = new Executor(null) { DryRun = true, ConfigRoot = fixRoot };
                ex1.EvQuery = delegate (string q, int max) { return MakeHits(60, "ev") ; };
                bool ok1 = ex1.Execute(new Executor.Task
                {
                    Type = "evsearch", Target = "季度报告", Log = delegate { }
                });
                int limitDefault = new AutoConf().SubmenuLimit;
                chk(ok1, "①-1 有命中 ⇒ 执行成功（DryRun：只记录不打开）", ex1.LastErrorCode + " / " + ex1.LastError);
                chk(ex1.LastEvSearchHits.Count == limitDefault,
                    "①-2 命中条数 = submenuLimit 默认值 " + limitDefault + "（后端给 60 条 ⇒ 截断）",
                    "实际=" + ex1.LastEvSearchHits.Count);
                chk(ex1.LastEvSearchBackend == "EV", "①-3 首选后端 = EV（Everything64.dll）", ex1.LastEvSearchBackend);

                // ---------------- ④ 上限来自配置（auto.conf 的 submenuLimit） ----------------
                sb.AppendLine();
                sb.AppendLine("--- ④ 上限来自 auto.conf（submenuLimit=3）---");
                File.WriteAllText(Path.Combine(fixRoot, "config", "auto.conf"),
                    "# 自检夹具" + Environment.NewLine + "submenuLimit=3" + Environment.NewLine,
                    new UTF8Encoding(false));
                var ex4 = new Executor(null) { DryRun = true, ConfigRoot = fixRoot };
                ex4.EvQuery = delegate (string q, int max) { return MakeHits(60, "lim"); };
                bool ok4 = ex4.Execute(new Executor.Task { Type = "evsearch", Target = "x" });
                chk(ok4 && ex4.LastEvSearchHits.Count == 3,
                    "④ 改 submenuLimit=3 ⇒ 最多 3 条（上限确实从配置读）",
                    "命中=" + ex4.LastEvSearchHits.Count + " err=" + ex4.LastError);
                chk(ex4.LastEvSearchHits.Count <= 3, "④-2 命中数 ≤ submenuLimit", ex4.LastEvSearchHits.Count.ToString());

                // ---------------- ③ 0 命中 ⇒ 明确"无结果"错误码 ----------------
                sb.AppendLine();
                sb.AppendLine("--- ③ 0 命中 ⇒ 明确错误码（不是成功）---");
                var ex3 = new Executor(null) { DryRun = true, ConfigRoot = fixRoot };
                var notify3 = new List<string>();
                ex3.ErrorNotify = s => notify3.Add(s);
                ex3.EvQuery = delegate (string q, int max) { return new List<string>(); };
                bool ok3 = ex3.Execute(new Executor.Task { Type = "evsearch", Target = "不存在的关键字" });
                chk(!ok3 && ex3.LastErrorCode == "evsearch-no-result",
                    "③-1 0 命中 ⇒ false + evsearch-no-result", ex3.LastErrorCode);
                chk(ex3.LastEvSearchHits.Count == 0, "③-2 命中表为空（不残留上一次结果）",
                    ex3.LastEvSearchHits.Count.ToString());
                chk(notify3.Count == 1 && notify3[0].IndexOf("无结果", StringComparison.Ordinal) >= 0,
                    "③-3 已推送用户可见通知（禁静默）", "通知数=" + notify3.Count);

                // ---------------- ② 库不可用：退 es / 明确错误码，且不抛 ----------------
                sb.AppendLine();
                sb.AppendLine("--- ② EV 不可用 → 退 es.exe；两条都不可用 ⇒ 明确错误码 ---");
                var ex2a = new Executor(null) { DryRun = true, ConfigRoot = fixRoot };
                var esHits = MakeHits(2, "es");
                ex2a.EvQuery = delegate { return null; };                       // EV 报告不可用
                ex2a.EsQuery = delegate (string q, int max) { return esHits; };  // es 命中
                bool ok2a = ex2a.Execute(new Executor.Task { Type = "evsearch", Target = "关键字" });
                chk(ok2a && ex2a.LastEvSearchBackend == "es" && ex2a.LastEvSearchHits.Count == 2,
                    "②-1 EV 不可用 ⇒ 自动退 es.exe（后端=es，命中 2）",
                    ex2a.LastEvSearchBackend + " / " + ex2a.LastError);

                var ex2b = new Executor(null) { DryRun = true, ConfigRoot = fixRoot };
                ex2b.EvQuery = delegate { throw new InvalidOperationException("模拟 DLL 加载失败"); };
                ex2b.EsQuery = delegate (string q, int max) { return null; };     // es 也不可用
                bool ok2b = ex2b.Execute(new Executor.Task { Type = "evsearch", Target = "关键字" });
                chk(!ok2b && ex2b.LastErrorCode == "evsearch-backend-unavailable",
                    "②-2 两条后端都不可用 ⇒ false + evsearch-backend-unavailable（区别于 0 命中）",
                    ex2b.LastErrorCode);
                chk(ex2b.LastError.IndexOf("模拟 DLL 加载失败", StringComparison.Ordinal) >= 0,
                    "②-3 EV 抛异常被吞成「不可用」（**不把异常抛给调用方**）", ex2b.LastError);

                var ex2c = new Executor(null) { DryRun = true, ConfigRoot = fixRoot };
                ex2c.EvQuery = delegate { throw new InvalidOperationException("boom"); };
                ex2c.EsQuery = delegate (string q, int max) { return MakeHits(3, "fallback"); };
                bool ok2c = ex2c.Execute(new Executor.Task { Type = "evsearch", Target = "关键字" });
                chk(ok2c && ex2c.LastEvSearchBackend == "es",
                    "②-4 EV 异常 ⇒ 仍能退 es 并成功（异常不打断后端链）", ex2c.LastError);

                // ---------------- 查询串 = 占位符展开后的目标串 ----------------
                sb.AppendLine();
                sb.AppendLine("--- 查询串来源（占位符已按既有规则展开）---");
                string fakeFile = Path.Combine(sandbox, "选中文件.txt");
                File.WriteAllText(fakeFile, "x", new UTF8Encoding(false));
                string seenQuery = "";
                var ex5 = new Executor(null) { DryRun = true, ConfigRoot = fixRoot };
                ex5.EvQuery = delegate (string q, int max) { seenQuery = q; return MakeHits(1, "q"); };
                ex5.Execute(new Executor.Task
                {
                    Type = "evsearch",
                    Target = "%file%",
                    Files = new List<string> { fakeFile }
                });
                chk(string.Equals(seenQuery, fakeFile, StringComparison.Ordinal),
                    "查询串 = %file% 展开后的选中文件全路径（同一唯一源 PlaceholderExpander）",
                    "实际=" + seenQuery);

                // ---------------- 空查询：单独错误码 ----------------
                var ex6 = new Executor(null) { DryRun = true, ConfigRoot = fixRoot };
                bool ok6 = ex6.Execute(new Executor.Task { Type = "evsearch", Target = "   " });
                chk(!ok6 && ex6.LastErrorCode == "evsearch-empty-query",
                    "空查询 ⇒ false + evsearch-empty-query（既不静默成功，也不谎报未实现）",
                    ex6.LastErrorCode);

                // ---------------- 真后端（不注入）：不抛异常即可 ----------------
                sb.AppendLine();
                sb.AppendLine("--- 真后端探测（无注入；只要求「不抛 + 明确结论」）---");
                var ex7 = new Executor(null) { DryRun = true, ConfigRoot = root };
                bool ok7 = ex7.Execute(new Executor.Task { Type = "evsearch", Target = "explorer.exe" });
                string code7 = ex7.LastErrorCode;
                bool known = ok7
                    || code7 == "evsearch-no-result"
                    || code7 == "evsearch-backend-unavailable";
                chk(known,
                    "真后端：命中 / 0 命中 / 两后端不可用 —— 三种明确结论之一，未抛异常",
                    "ok=" + ok7 + " code=" + code7 + " err=" + ex7.LastError);
                sb.AppendLine("  [环境] backend=" + (ex7.LastEvSearchBackend.Length == 0 ? "(不可用)" : ex7.LastEvSearchBackend)
                    + " 命中=" + ex7.LastEvSearchHits.Count
                    + " 结论=" + (code7.Length == 0 ? "成功" : code7));
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("  FAIL 自检异常（不该发生）: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
            }

            sb.AppendLine();
            sb.AppendLine("=== 结果: 通过 " + pass + " / 失败 " + fail + " ===");
            WriteText(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        /// <summary>造 n 条假的命中路径（不需要真实存在：DryRun 不打开）。</summary>
        private static List<string> MakeHits(int n, string tag)
        {
            var list = new List<string>();
            for (int i = 0; i < n; i++)
                list.Add(@"D:\rve-evsearch\" + tag + @"\hit-" + i.ToString("00") + ".txt");
            return list;
        }

        internal static void WriteText(string path, string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // ============================================================
    // F2 · menulocal.conf 覆盖层
    // ============================================================
    public static class OverlaySelfTest
    {
        public static int Run(string[] args)
        {
            if (args == null || args.Length < 3) return 2;
            string root = args[1];
            string outPath = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            Action<bool, string, string> chk = (cond, label, extra) =>
            {
                if (cond) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (string.IsNullOrEmpty(extra) ? "" : "   <<< " + extra)); }
            };

            sb.AppendLine("=== menulocal.conf 覆盖层自检（--selftest-overlay）===");
            sb.AppendLine("  root: " + root);
            sb.AppendLine("  时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();

            string sandbox = Path.Combine(root, "state", "cb-f-overlay");
            string liveConf = Path.Combine(root, "config", "menus.conf");

            try
            {
                if (!File.Exists(liveConf))
                {
                    sb.AppendLine("  FAIL 夹具缺失：找不到 " + liveConf);
                    EvSearchSelfTest.WriteText(outPath, sb.ToString());
                    return 1;
                }

                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
                Directory.CreateDirectory(sandbox);

                string a = MakeFixture(sandbox, "a", liveConf);   // 无覆盖层
                string b = MakeFixture(sandbox, "b", liveConf);   // 有覆盖层（覆盖 1 + 新增 1）
                string c = MakeFixture(sandbox, "c", liveConf);   // 坏覆盖层（跳级）
                string d = MakeFixture(sandbox, "d", liveConf);   // 坏覆盖层（合并后父子关系被破坏）

                // ============================================================
                // ① 无 menulocal.conf ⇒ 与基线逐字节相同
                // ============================================================
                sb.AppendLine("--- ① 无覆盖层 ⇒ 与基线逐字节相同 ---");
                string confA = Path.Combine(a, "config", "menus.conf");
                byte[] liveBytes0 = File.ReadAllBytes(liveConf);
                string dumpLive0 = Dump(liveConf, Path.Combine(sandbox, "dump-live-0.txt"));
                string dumpA0 = Dump(confA, Path.Combine(sandbox, "dump-a-0.txt"));

                var pA = new ConfParser(confA);
                pA.Parse();
                int aGroups = pA.Groups.Count, aItems = CountItems(pA.Groups);
                var logA = new List<string>();
                OverlayResult rA = MenuLocalOverlay.Apply(pA, confA, s => logA.Add(s));

                chk(!rA.Present && !rA.Applied,
                    "①-1 无 menulocal.conf ⇒ Present=false / Applied=false（零变化分支）",
                    rA.Describe());
                chk(pA.Groups.Count == aGroups && CountItems(pA.Groups) == aItems,
                    "①-2 分组/条目数与解析后完全一致（" + aGroups + " 组 / " + aItems + " 条）",
                    pA.Groups.Count + " 组 / " + CountItems(pA.Groups) + " 条");

                string dumpA1 = Dump(confA, Path.Combine(sandbox, "dump-a-1.txt"));
                chk(dumpA0 == dumpA1,
                    "①-3 --dump-menutext 产物逐字节相同（Apply 前后）",
                    "len " + dumpA0.Length + " vs " + dumpA1.Length);

                string dumpLive1 = Dump(liveConf, Path.Combine(sandbox, "dump-live-1.txt"));
                chk(dumpLive0 == dumpLive1 && BytesEqual(liveBytes0, File.ReadAllBytes(liveConf)),
                    "①-4 现役 menus.conf：dump 与文件字节都未被本自检改动",
                    "dump len " + dumpLive0.Length + " vs " + dumpLive1.Length);

                // ============================================================
                // ② 有覆盖层（改一个条目 + 加一个条目）⇒ 生效且只影响这两处
                // ============================================================
                sb.AppendLine();
                sb.AppendLine("--- ② 覆盖 1 条 + 新增 1 条 ⇒ 只影响这两处 ---");
                string confB = Path.Combine(b, "config", "menus.conf");
                string ovlB = Path.Combine(b, "config", MenuLocalOverlay.FileName);
                WriteOverlay(ovlB, OvlOverrideGroup, OvlOverrideTitle, OvlOverrideTarget,
                    OvlNewTitle, OvlNewTarget);

                var pB = new ConfParser(confB);
                pB.Parse();
                List<string> sigBase = Signature(pB.Groups);
                string dumpBeforeOverride = Dump(confB, Path.Combine(sandbox, "dump-b-base.txt"));

                var logB = new List<string>();
                OverlayResult rB = MenuLocalOverlay.Apply(pB, confB, s => logB.Add(s));
                List<string> sigMerged = Signature(pB.Groups);

                chk(rB.Present && rB.Applied, "②-1 覆盖层被应用", rB.Describe());
                chk(rB.Overridden == 1 && rB.Added == 1 && rB.NewGroups == 0,
                    "②-2 计数可枚举：覆盖 1 条 / 新增 1 条 / 新分组 0 个",
                    "覆盖=" + rB.Overridden + " 新增=" + rB.Added + " 新组=" + rB.NewGroups);

                MenuItem overridden;
                bool foundOverride = FindByTitle(pB.Groups, OvlOverrideGroup, OvlOverrideTitle, out overridden, out _);
                chk(foundOverride && overridden != null && overridden.Target == OvlOverrideTarget,
                    "②-3 覆盖生效：目标串换成覆盖层写的值",
                    foundOverride ? overridden.Target : "(找不到条目)");

                chk(FindByTitle(pB.Groups, OvlOverrideGroup, OvlNewTitle, out _, out _),
                    "②-4 新增生效：新条目落在同名分组里", OvlNewTitle);

                // 只影响这两处 = 签名多重集差：去掉 1 条老签名、加回 2 条新签名
                List<string> removed, added;
                DiffSignatures(sigBase, sigMerged, out removed, out added);
                chk(removed.Count == 1 && added.Count == 2,
                    "②-5 差异恰为 2 处（1 条被覆盖 = 1 老 + 1 新；1 条新增）",
                    "removed=" + removed.Count + " added=" + added.Count);
                chk(added.Count == 2
                    && added.Exists(s => s.IndexOf(OvlNewTitle, StringComparison.Ordinal) >= 0)
                    && added.Exists(s => s.IndexOf(OvlOverrideTarget, StringComparison.Ordinal) >= 0),
                    "②-6 差异内容就是「新条目」与「被覆盖条目的新目标」",
                    string.Join(" || ", added.ToArray()));

                // 新增项落在分组末尾（= 该组顶层条目的"父项子树末尾"）
                MenuGroup gOv; int gIdxOv;
                bool gotGroup = FindGroup(pB.Groups, OvlOverrideGroup, out gOv, out gIdxOv);
                chk(gotGroup && gIdxOv >= 0 && gOv.Items.Count > 0
                    && string.Equals(gOv.Items[gOv.Items.Count - 1].Title, OvlNewTitle, StringComparison.Ordinal),
                    "②-7 新增条目插在该分组末尾（父项子树末尾口径）",
                    gotGroup ? ("末条=" + gOv.Items[gOv.Items.Count - 1].Title) : "(找不到分组)");

                chk(dumpBeforeOverride.Length > 0, "②-8 基线 dump 可用（对照）", "");

                // 基线文件与本自检无关：覆盖层不写盘
                chk(!File.ReadAllText(confB, Encoding.UTF8).Contains(OvlNewTitle),
                    "②-9 合并只在内存：基文件里没有新条目", confB);

                // ============================================================
                // ⑤ 应用层唯一合并点：RunEverApp.ReloadMenus / HotReloadCheck 真的吃到覆盖层
                //   （不走 Init：Init 会起采集预热与后台刷新，自检不该有那些副作用）
                // ============================================================
                sb.AppendLine();
                sb.AppendLine("--- ⑤ 应用层合并点（ReloadMenus / HotReloadCheck）---");
                var app = new RunEverApp(b);
                bool reloaded = app.ReloadMenus();
                var fld = typeof(RunEverApp).GetField("_parser",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var pApp = (fld == null) ? null : (fld.GetValue(app) as ConfParser);

                MenuItem appOver;
                bool appOverride = pApp != null
                    && FindByTitle(pApp.Groups, OvlOverrideGroup, OvlOverrideTitle, out appOver, out _)
                    && appOver.Target == OvlOverrideTarget;
                chk(reloaded && pApp != null && HasTitle(pApp.Groups, OvlNewTitle) && appOverride,
                    "⑤-1 ReloadMenus 后的菜单模型 = 合并结果（覆盖值 + 新条目都在）",
                    "reloaded=" + reloaded + " 新条目=" + (pApp != null && HasTitle(pApp.Groups, OvlNewTitle)));

                // 覆盖层单独变更 ⇒ 热键期的 1 次 stat 就能发现并重读（无需重启）
                WriteOverlay(ovlB, OvlOverrideGroup, OvlOverrideTitle, OvlOverrideTarget,
                    OvlNewTitle, OvlNewTarget + ".v2");
                app.HotReloadCheck();
                var pApp2 = (fld == null) ? null : (fld.GetValue(app) as ConfParser);
                MenuItem v2;
                chk(pApp2 != null
                    && FindByTitle(pApp2.Groups, OvlOverrideGroup, OvlNewTitle, out v2, out _)
                    && v2.Target == OvlNewTarget + ".v2",
                    "⑤-2 只改 menulocal.conf ⇒ HotReloadCheck 重读并生效（1 次 stat，未变 0 读盘）",
                    pApp2 == null ? "(parser=null)" : "v2 未见");

                // 还原成 ② 的覆盖层内容（后续 ③/④ 夹具互不影响：它们各自独立目录）
                WriteOverlay(ovlB, OvlOverrideGroup, OvlOverrideTitle, OvlOverrideTarget,
                    OvlNewTitle, OvlNewTarget);

                // ============================================================
                // ③ 故意写坏的覆盖层 ⇒ 整份丢弃 + 基配置仍正常
                // ============================================================
                sb.AppendLine();
                sb.AppendLine("--- ③ 坏覆盖层 ⇒ 整份丢弃 + 基配置仍正常 ---");
                string confC = Path.Combine(c, "config", "menus.conf");
                string ovlC = Path.Combine(c, "config", MenuLocalOverlay.FileName);
                File.WriteAllText(ovlC,
                    "# 故意跳级：lv3 找不到 lv2 父项" + Environment.NewLine
                    + "! " + OvlOverrideGroup + "  always" + Environment.NewLine
                    + "       " + OvlNewTitle + " | run  " + OvlNewTarget + Environment.NewLine,
                    new UTF8Encoding(false));

                var pC = new ConfParser(confC);
                pC.Parse();
                List<string> sigCBase = Signature(pC.Groups);
                OverlayResult rC = MenuLocalOverlay.Apply(pC, confC, delegate { });

                chk(rC.Present && !rC.Applied,
                    "③-1 跳级覆盖层 ⇒ Present=true / Applied=false（整份丢弃）", rC.Describe());
                chk(rC.Reason.IndexOf("跳级", StringComparison.Ordinal) >= 0
                    || rC.Reason.IndexOf("父条目", StringComparison.Ordinal) >= 0,
                    "③-2 丢弃原因写明「跳级/父条目不存在」", rC.Reason);
                chk(SameSignature(sigCBase, Signature(pC.Groups)),
                    "③-3 基配置一字节未动（合并失败不产出坏树）",
                    "条目 " + CountItems(pC.Groups));

                // ③b 合并后**重新校验**才发现的破坏：把既有 group 标题改成 run（父项带子项）
                string confD = Path.Combine(d, "config", "menus.conf");
                string ovlD = Path.Combine(d, "config", MenuLocalOverlay.FileName);
                File.WriteAllText(ovlD,
                    "# 合并后层级不变式被破坏：把有子项的 group 标题覆盖成 run" + Environment.NewLine
                    + "! " + OvlGroupHeaderGroup + "  always" + Environment.NewLine
                    + "   " + OvlGroupHeaderTitle + " | run  " + OvlNewTarget + Environment.NewLine,
                    new UTF8Encoding(false));

                var pD = new ConfParser(confD);
                pD.Parse();
                List<string> sigDBase = Signature(pD.Groups);
                OverlayResult rD = MenuLocalOverlay.Apply(pD, confD, delegate { });

                chk(rD.Present && !rD.Applied,
                    "③-4 覆盖后父项变成非 group ⇒ Applied=false", rD.Describe());
                chk(rD.Reason.IndexOf("层级不变式", StringComparison.Ordinal) >= 0,
                    "③-5 由「合并后重新校验」抓到（MenuStructureValidator 同一套判据）", rD.Reason);
                chk(SameSignature(sigDBase, Signature(pD.Groups)),
                    "③-6 基配置仍完整（坏覆盖层零影响）", "");

                // ============================================================
                // ④ 保存：只写 menus.conf，绝不写入覆盖层内容
                // ============================================================
                sb.AppendLine();
                sb.AppendLine("--- ④ 保存路径只写 menus.conf（覆盖层是只读输入）---");
                var pWrite = new ConfParser(confB);      // 编辑器 / 写盘侧口径：不叠覆盖层
                pWrite.Parse();
                chk(!HasTitle(pWrite.Groups, OvlNewTitle),
                    "④-1 写盘侧的解析结果不含覆盖层新条目（设计：合并点不在 Parse 内）", "");

                MenuItem wOver;
                chk(FindByTitle(pWrite.Groups, OvlOverrideGroup, OvlOverrideTitle, out wOver, out _)
                    && wOver.Target != OvlOverrideTarget,
                    "④-2 写盘侧看到的是基配置的目标（覆盖值不回流）",
                    wOver == null ? "(找不到)" : wOver.Target);

                byte[] beforeSave = File.ReadAllBytes(confB);
                var writer = new MenuConfWriter(confB);
                string msg; List<MenuStructureIssue> issues;
                bool saved = writer.Save(pWrite.Groups, out msg, out issues);
                byte[] afterSave = File.ReadAllBytes(confB);
                string afterText = File.ReadAllText(confB, Encoding.UTF8);

                chk(saved && issues.Count == 0,
                    "④-3 无改动保存成功（结构校验 0 问题）", msg);
                chk(BytesEqual(beforeSave, afterSave),
                    "④-4 menus.conf 保存后逐字节不变（窄写回没被覆盖层扰动）",
                    beforeSave.Length + " → " + afterSave.Length);
                chk(afterText.IndexOf(OvlNewTitle, StringComparison.Ordinal) < 0
                    && afterText.IndexOf(OvlNewTarget, StringComparison.Ordinal) < 0,
                    "④-5 menus.conf 内容不含覆盖层条目/目标（硬约束）", "");
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("  FAIL 自检异常（不该发生）: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
            }

            sb.AppendLine();
            sb.AppendLine("=== 结果: 通过 " + pass + " / 失败 " + fail + " ===");
            EvSearchSelfTest.WriteText(outPath, sb.ToString());
            return fail == 0 ? 0 : 1;
        }

        // ---- 夹具 ----
        // 现役 config\menus.conf 里稳定存在的三条（只依赖结构，不依赖用户机器上的程序）：
        //   ! 文件夹 folder       →  LockHunter64（叶）/ 7-Zip / 解压到当前目录
        //   ! 通用菜单 always     →  工具（group，带 4 个子项）
        private const string OvlOverrideGroup = "文件夹";
        private const string OvlOverrideTitle = "7-Zip";
        private const string OvlOverrideTarget = @"D:\rve-overlay\7zFM-override.exe";
        private const string OvlNewTitle = "本机新增条目";
        private const string OvlNewTarget = @"D:\rve-overlay\本机新增.exe";
        private const string OvlGroupHeaderGroup = "通用菜单";
        private const string OvlGroupHeaderTitle = "工具";

        private static string MakeFixture(string sandbox, string name, string liveConf)
        {
            string dir = Path.Combine(sandbox, name);
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            File.Copy(liveConf, Path.Combine(dir, "config", "menus.conf"), true);
            return dir;
        }

        private static void WriteOverlay(string path, string group, string title, string target,
            string newTitle, string newTarget)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 本机覆盖（自检夹具）：同 Level + 同标题 ⇒ 覆盖；否则追加到父项子树末尾");
            sb.AppendLine("! " + group + "  folder");
            sb.AppendLine("   " + title + " | run  " + target);
            sb.AppendLine("   " + newTitle + " | run  " + newTarget);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>用产品自己的 --dump-menutext 产物做"逐字节"比对（与判据口径一致）。</summary>
        private static string Dump(string confPath, string outPath)
        {
            try
            {
                MenuTextDump.Run(new[] { "--dump-menutext", confPath, outPath });
                return File.Exists(outPath) ? File.ReadAllText(outPath, Encoding.UTF8) : "";
            }
            catch { return ""; }
        }

        // ---- 结构比对 ----
        private static int CountItems(List<MenuGroup> groups)
        {
            int n = 0;
            foreach (MenuGroup g in groups) if (g != null && g.Items != null) n += g.Items.Count;
            return n;
        }

        private static List<string> Signature(List<MenuGroup> groups)
        {
            var list = new List<string>();
            foreach (MenuGroup g in groups)
            {
                if (g == null) { list.Add("#G|<null>"); continue; }
                list.Add("#G|" + (g.Name ?? "") + "|" + MenuGroup.TypeToString(g.Type)
                    + "|" + string.Join(",", (g.Keywords ?? new List<string>()).ToArray()));
                foreach (MenuItem it in g.Items ?? new List<MenuItem>())
                {
                    if (it == null) { list.Add("#I|null"); continue; }
                    list.Add("#I|" + it.Level + "|" + (it.Title ?? "") + "|" + (it.Hotkey ?? "")
                        + "|" + it.Type + "|" + it.Flag + "|" + (it.Sub ? "1" : "0")
                        + "|" + (it.Target ?? ""));
                }
            }
            return list;
        }

        private static bool SameSignature(List<string> x, List<string> y)
        {
            if (x.Count != y.Count) return false;
            for (int i = 0; i < x.Count; i++)
                if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>多重集差：removed = 只在 before 里的行；added = 只在 after 里的行（顺序稳定）。</summary>
        private static void DiffSignatures(List<string> before, List<string> after,
            out List<string> removed, out List<string> added)
        {
            var beforeCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string s in before)
            {
                int n; beforeCount.TryGetValue(s, out n);
                beforeCount[s] = n + 1;
            }

            added = new List<string>();
            var afterCount = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string s in after)
            {
                int n; afterCount.TryGetValue(s, out n);
                afterCount[s] = n + 1;
            }

            removed = new List<string>();
            foreach (string s in before)
            {
                int n;
                if (afterCount.TryGetValue(s, out n) && n > 0) afterCount[s] = n - 1;
                else removed.Add(s);
            }

            foreach (string s in after)
            {
                int n;
                if (beforeCount.TryGetValue(s, out n) && n > 0) beforeCount[s] = n - 1;
                else added.Add(s);
            }
        }

        private static bool FindGroup(List<MenuGroup> groups, string name, out MenuGroup g, out int idx)
        {
            g = null; idx = -1;
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] == null) continue;
                if (string.Equals((groups[i].Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                { g = groups[i]; idx = i; return true; }
            }
            return false;
        }

        private static bool FindByTitle(List<MenuGroup> groups, string groupName, string title,
            out MenuItem item, out int idx)
        {
            item = null; idx = -1;
            MenuGroup g; int gi;
            if (!FindGroup(groups, groupName, out g, out gi)) return false;
            for (int i = 0; i < g.Items.Count; i++)
            {
                MenuItem it = g.Items[i];
                if (it == null) continue;
                if (string.Equals((it.Title ?? "").Trim(), title, StringComparison.Ordinal))
                { item = it; idx = i; return true; }
            }
            return false;
        }

        private static bool HasTitle(List<MenuGroup> groups, string title)
        {
            foreach (MenuGroup g in groups)
            {
                if (g == null || g.Items == null) continue;
                foreach (MenuItem it in g.Items)
                    if (it != null && string.Equals((it.Title ?? "").Trim(), title, StringComparison.Ordinal))
                        return true;
            }
            return false;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
