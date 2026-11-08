// ============================================================
// RunEver · 全盘自检 自检（功能 B）
//
// 用法：RunEverCs.exe --selftest-selfcheck <root> <out.txt>   （exit 0 = 全过）
//
// 覆盖（对照任务表 S8）：
//   A EV 探针（若判定可用则**必须** hits>0 且 lastError==0 —— 此二条是不变式，违反即 FAIL）
//     A1 「第 1 条未过滤结果与查询名全等」= **信息项**（[INFO]）——
//        EV 是文件名**子串**匹配，第 1 条常是 …\WER\ReportArchive\AppHang_explorer.exe_<guid>
//        （崩溃报告目录）；把它当 FAIL 就是把索引噪音当信号（B6-1 口径修正）。
//        `RVE_B6_LEGACY=1` 可把 A1 恢复成 FAIL，仅供"旧口径能红"的对照取证。
//     A2 产品同款过滤（IsExcluded 硬排除 + File.Exists 幽灵剔除）后，前 30 条候选中
//        必须存在 basename 与查询名全等者 —— 真正的"EV 能给出正解"判据。
//     A3 **真回归断言**：拿产品解析函数 ResolveOneResult(explorer.exe) 的结果，
//        必须是存在的**文件**且 Path.GetFileName 全等（模糊匹配一回归就会红）。
//     A4 EV 层裁决复算：SearchCandidates → IsExcluded → File.Exists → PickBest
//        仍需选出全等文件（钉住"Search 的全等过滤不被放宽"）。
//   B 降级不崩：evdll 指向不存在路径 ⇒ 全项「待确认」，且输出里**禁止出现"未安装"字样**
//   C 正向：EV 可信时同一批缺件项必须是「未找到（已确认）」；不可信时必须全是「待确认」
//   D 统计逐项正确（存在/缺失/跳过 + 占位符进跳过 + url 不参与判定）
//   E choco 映射：命中项包名规范；未命中项包名空且备注含 `choco search`
//   F .bat 导出：纯 ASCII、无 BOM、无 `choco install 待确认`、未确认项以 REM 注释并附 search 提示
//   G 真配置 sanity：真实 menus.conf 能跑完整流程（不联网、不写缓存、不断言具体软件）
//
// ★ 全程离线可复现：只断言结构不变式，不断言"本机装没装某软件"。
//   fixture 用临时目录 + 合成配置，跑完清理；真实 cache\paths.txt 一个字节都不碰。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using RunEverCs.Services;

namespace RunEverCs
{
    public static class SelfCheckSelfTest
    {
        private static readonly List<string> TempDirs = new List<string>();

        private const string FixtureMenus =
            "! 测试组  always\r\n" +
            "   存在的程序    _A | run cmd.exe\r\n" +
            "   缺的幽灵      _B | run zzz_ghost.exe\r\n" +
            "   未映射幽灵    _C | run zzz_unknown.exe\r\n" +
            "   占位符条目    _D | run %file%\r\n" +
            "   存在的目录    _E | open C:\\Windows\r\n" +
            "   不存在的目录  _F | open Z:\\__rve_no_such__\\x\r\n" +
            "   网页条目      _G | url http://example.com/\r\n";

        private const string FixtureMap =
            "# fixture 映射表\r\nzzz_ghost.exe=zzz-ghost-pkg\r\n";

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
            Action<bool, string> chkOK = (c, l) => chk(c, l, "");
            Action<string, string> info = (l, extra) =>
                sb.AppendLine("  [INFO] " + l + (extra != "" ? "   <<< " + extra : ""));

            // ── B6-1 取证开关 ────────────────────────────────────────────────
            //   RVE_B6_LEGACY=1 ⇒ 把"原口径"（EV 第 1 条**未过滤**结果必须与查询名
            //   逐字符相等）重新当 FAIL 计。默认关闭：本机 EV 会先返回
            //   …\WER\ReportArchive\AppHang_explorer.exe_<guid>（崩溃报告**目录**），
            //   该口径天然恒红，且与产品行为无关（产品侧有硬排除 + 幽灵剔除）。
            //   保留它只为"打旧逻辑能红 / 打新逻辑能绿"的对照证据。
            bool legacy = Environment.GetEnvironmentVariable("RVE_B6_LEGACY") == "1";

            string baseDir = Path.Combine(Path.GetTempPath(),
                "rve_selfcheck_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            TempDirs.Add(baseDir);

            try
            {
                sb.AppendLine("=== 全盘自检（功能 B）自检 ===");
                sb.AppendLine("root = " + root);
                sb.AppendLine();

                // ---------------- A. EV 探针 ----------------
                sb.AppendLine("--- A. EV 探针 ---");
                if (legacy)
                    sb.AppendLine("  [INFO] RVE_B6_LEGACY=1 ⇒ 原口径断言按 FAIL 计（仅取证，非发布口径）");
                EverythingProbe.Reset();
                var ev = EverythingProbe.ProbeOnce(BackendResolver.ReadEvDll(root));
                sb.AppendLine("  [INFO] " + ev.Describe());
                sb.AppendLine("  [INFO] DLL=" + (ev.DllPath.Length > 0 ? ev.DllPath : "<无>")
                    + " 版本=" + (ev.EvVersion.Length > 0 ? ev.EvVersion : "<未取到>"));
                if (ev.Kind == BackendKind.Everything)
                {
                    chk(ev.ProbeHits > 0, "A 探针命中数 > 0（EV 判定可用的硬判据）",
                        "hits=" + ev.ProbeHits);
                    chk(ev.LastError == 0, "A 探针 GetLastError()==0（IPC 通）",
                        "lastError=" + ev.LastError);

                    // ── A1 原断言 → 信息项（B6-1 要求 3）─────────────────────────
                    //   为什么降级成 INFO：EV 的 Search 是**文件名子串**匹配（SetMatchPath(false)、
                    //   SetMatchWholeWord(false)），第 1 条完全可能是
                    //   `…\WER\ReportArchive\AppHang_explorer.exe_<guid>`（崩溃报告**目录**）
                    //   这类"名字里含 explorer.exe"的对象 —— basename 天然不等。
                    //   这是索引内容的噪音，不是后端故障，也不是产品缺陷
                    //   （产品在 PathCacheBuilder.ResolveViaEverything 里做硬排除 + 幽灵剔除）。
                    //   把它当 FAIL = 把噪音当信号 ⇒ 绿灯失去凭证价值（狼来了）。
                    string a1 = "第1条未过滤结果 basename 与查询名(" + EverythingProbe.ProbeQuery
                        + ")不等 ⇒ " + (ev.FullPathVerified ? "相等" : ev.Reason);
                    if (legacy)
                        chk(ev.FullPathVerified,
                            "A1〔旧口径〕探针读回全路径与查询名逐字符一致（第 1 条未过滤结果）", a1);
                    else
                        info("A1 第 1 条未过滤结果 vs 查询名（信息项，不参与判定）：" + a1, "");

                    // ── A2 产品同款过滤后判：前 N 条候选中须有 basename 全等者 ──
                    //   取数口径 = **EV 的原始结果**（第 0 条就是 …WER\ReportArchive\AppHang_explorer.exe_<guid>）。
                    //   ★ 为什么不用 PathCacheBuilder.SearchCandidates：产品的
                    //     EverythingBackend.Search 内部已经做了「文件名全等」过滤，用它当输入
                    //     会让本断言退化成恒真（没判别力）。原始结果才暴露真实噪音。
                    //   过滤口径与 PathCacheBuilder.ResolveViaEverything 逐条对齐：
                    //     ① IsExcluded（产品公开的硬排除谓词：WinSxS/servicing/Prefetch/SystemResources）
                    //     ② File.Exists（= 产品的 FileExistsSafe 幽灵剔除；**目录在此为 false**，
                    //        所以 WER 那个崩溃报告目录会被正确剔掉）
                    //   判据 = 保留集里存在 basename 与查询名相等者（OrdinalIgnoreCase，同产品口径）
                    //          —— 这才等价于"EV 能提供一个可用的正解"。
                    try
                    {
                        List<string> rawCands = RawEvCandidates(EverythingProbe.ProbeQuery, 30);

                        int scan = Math.Min(rawCands.Count, 30);     // 与产品 Search(name, 30) 同上限
                        var kept = new List<string>();
                        int hardEx = 0, dirN = 0, ghostN = 0;
                        for (int i = 0; i < scan; i++)
                        {
                            string c = rawCands[i];
                            string tag;
                            if (c.Length == 0) { tag = "空路径"; }
                            else if (PathCacheBuilder.IsExcluded(c)) { tag = "硬排除"; hardEx++; }
                            else
                            {
                                bool isFile = false, isDir = false;
                                try { isFile = File.Exists(c); } catch { isFile = false; }
                                if (!isFile) { try { isDir = Directory.Exists(c); } catch { isDir = false; } }

                                if (isDir) { tag = "目录（存在但不是文件）"; dirN++; }
                                else if (!isFile) { tag = "不存在（索引陈旧）"; ghostN++; }
                                else { tag = "保留"; kept.Add(c); }
                            }
                            // 逐条打标签：判据的取数过程必须可复核（哪条被哪条规则剔掉）。
                            // 硬排除的只计数不逐条打（WinSxS/servicing 动辄几十条，打出来淹没信号）。
                            if (tag != "硬排除") info("A2 候选[" + i + "] " + tag, c);
                        }

                        info("A2 过滤统计（IsExcluded + File.Exists，与产品同口径）",
                            "原始候选=" + rawCands.Count + " 扫描=" + scan
                            + " 硬排除=" + hardEx + "（未逐条列出） 目录=" + dirN
                            + " 不存在=" + ghostN + " 保留=" + kept.Count);

                        chk(rawCands.Count > 0,
                            "A2 EV 查询 " + EverythingProbe.ProbeQuery + " 有原始候选（过滤需先有输入）",
                            "候选=" + rawCands.Count);

                        string hit = "";
                        foreach (string c in kept)
                        {
                            string fn;
                            try { fn = Path.GetFileName(c); } catch { fn = ""; }
                            if (string.Equals(fn, EverythingProbe.ProbeQuery,
                                    StringComparison.OrdinalIgnoreCase)) { hit = c; break; }
                        }
                        info("A2 全等候选", hit.Length > 0 ? hit : "<无>");
                        chk(hit.Length > 0,
                            "A2 产品同款过滤后（剔除不存在/硬排除）存在 basename 全等的候选",
                            "保留=" + kept.Count + " 条，但无 basename==" + EverythingProbe.ProbeQuery);
                    }
                    catch (Exception ex)
                    {
                        chk(false, "A2 产品同款过滤后取候选集", ex.Message);
                    }

                    // ── A3 真回归断言：产品解析函数的结果必须是"存在的文件 + 文件名全等" ──
                    //   这条才是防"模糊匹配回归"的：一旦产品侧过滤退化成"取搜索第一条"，
                    //   结果会变成目录/报告文件，basename 不等于查询名 ⇒ 这里立刻红。
                    try
                    {
                        var pcb3 = new PathCacheBuilder(
                            new PathCache(Path.Combine(baseDir, "a-resolve-paths.txt")),
                            null, null,
                            new EverythingBackend(BackendResolver.ReadEvDll(root), true));
                        ResolveResult rr = pcb3.ResolveOneResult(EverythingProbe.ProbeQuery);
                        string rp = rr.Path ?? "";
                        bool isFile = false;
                        try { isFile = rp.Length > 0 && File.Exists(rp); } catch { isFile = false; }
                        string rfn = "";
                        try { rfn = rp.Length > 0 ? Path.GetFileName(rp) : ""; } catch { rfn = ""; }

                        info("A3 产品解析来源（tier=AppPaths/Path/Everything）",
                            "tier=" + rr.Tier + " path=[" + rp + "]");

                        chk(isFile && string.Equals(rfn, EverythingProbe.ProbeQuery,
                                StringComparison.OrdinalIgnoreCase),
                            "A3 产品解析 " + EverythingProbe.ProbeQuery
                            + " ⇒ 存在的**文件**且 Path.GetFileName 全等",
                            "tier=" + rr.Tier + " path=[" + rp + "] 存在=" + isFile
                            + " basename=[" + rfn + "]");
                    }
                    catch (Exception ex)
                    {
                        chk(false, "A3 产品解析 " + EverythingProbe.ProbeQuery, ex.Message);
                    }

                    // ── A4 EV 层的决策复算：SearchCandidates → 硬排除 → 存在 → PickBest ──
                    //   为什么还要一条：A3 在本机走的是 PATH 级（tier=Path），没真正压到 EV 路径。
                    //   这里用产品的公开件把 EV 层的裁决**原样重算一遍**：
                    //     SearchCandidates（内含全等过滤）→ IsExcluded → File.Exists → PickBest。
                    //   若有人把 Search 的"文件名全等"过滤去掉（模糊匹配回归），
                    //   explorer.exe.22704.dmp / explorer.exe.mui 这类会一起进候选，
                    //   按"创建时间最新者胜"就可能被选中 ⇒ 本断言立刻红。
                    try
                    {
                        var pcb4 = new PathCacheBuilder(
                            new PathCache(Path.Combine(baseDir, "a-presolve-paths.txt")),
                            null, null,
                            new EverythingBackend(BackendResolver.ReadEvDll(root), true));
                        List<string> cand4 = pcb4.SearchCandidates(EverythingProbe.ProbeQuery);
                        var kept4 = new List<string>();
                        foreach (string c in cand4)
                        {
                            if (c.Length == 0 || PathCacheBuilder.IsExcluded(c)) continue;
                            bool ex4 = false;
                            try { ex4 = File.Exists(c); } catch { ex4 = false; }
                            if (ex4) kept4.Add(c);
                        }
                        string pick = PathCacheBuilder.PickBest(kept4);
                        string pfn = "";
                        try { pfn = pick.Length > 0 ? Path.GetFileName(pick) : ""; } catch { pfn = ""; }
                        info("A4 EV 层复算候选", "候选=" + cand4.Count + " 保留=" + kept4.Count
                            + " 择优=[" + pick + "]");
                        chk(pick.Length > 0 && string.Equals(pfn, EverythingProbe.ProbeQuery,
                                StringComparison.OrdinalIgnoreCase),
                            "A4 EV 层复算（候选→硬排除→存在→择优）结果仍是全等文件",
                            "pick=[" + pick + "] basename=[" + pfn + "] 保留=" + kept4.Count);
                    }
                    catch (Exception ex)
                    {
                        chk(false, "A4 EV 层复算", ex.Message);
                    }
                }
                else
                {
                    sb.AppendLine("  [INFO] EV 不可用 ⇒ 后端降级（这是正常路径，不算 FAIL）");
                    sb.AppendLine("  [INFO] A2/A4 依赖 EV 后端，本轮不判；A3 在降级态下等价分支见 C/D 段");
                }
                sb.AppendLine();

                // ---------------- B. 降级不崩 ----------------
                sb.AppendLine("--- B. 降级不崩（evdll 指向不存在路径）---");
                string badDll = @"Z:\__rve_no_such_dll__\Everything64.dll";
                string degRoot = MakeFixture(baseDir, "degraded", FixtureMenus,
                    "evdll=" + badDll + "\r\n", FixtureMap);
                var deg = SelfCheck.Run(degRoot, null, null, null, false);
                sb.AppendLine("  [INFO] " + deg.Backend.Describe());
                chkOK(deg.Backend.Kind != BackendKind.Everything,
                    "B 探针确实失败（evdll 独占且指向不存在路径 ⇒ 不猜其它 DLL）");
                chkOK(deg.Backend.Degraded, "B 后端状态标为降级");
                int degMissing = 0, degUnknown = 0, degOk = 0, degSkip = 0;
                foreach (var it in deg.Items)
                {
                    if (it.Status == CheckStatus.Missing) degMissing++;
                    else if (it.Status == CheckStatus.Unknown) degUnknown++;
                    else if (it.Status == CheckStatus.Ok) degOk++;
                    else degSkip++;
                }
                chk(degMissing == 0, "B 降级态下**没有任何**「未找到（已确认）」",
                    "missing=" + degMissing);
                chk(degUnknown > 0, "B 降级态下缺件项全部落「待确认」", "unknown=" + degUnknown);
                string degText = deg.Render() + "\n" + deg.BuildBat();
                chkOK(degText.IndexOf("未安装", StringComparison.Ordinal) < 0,
                    "B 降级态输出中禁止出现「未安装」字样");
                sb.AppendLine();

                // ---------------- C/D. 正向 + 逐项统计 ----------------
                sb.AppendLine("--- C/D. 真后端（EV 若可用）逐项判定 + 统计 ---");
                string liveRoot = MakeFixture(baseDir, "live", FixtureMenus, null, FixtureMap);
                var live = SelfCheck.Run(liveRoot, null, null, null, false);
                bool evTrusted = live.Backend.Kind == BackendKind.Everything
                    && live.Backend.ProbeHits > 0 && live.Backend.DbLoaded;
                sb.AppendLine("  [INFO] " + live.Backend.Describe());
                sb.AppendLine("  [INFO] evTrusted(可断言『已确认』)=" + (evTrusted ? "1" : "0"));

                var byProg = new Dictionary<string, SelfCheckItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in live.Items) if (it.Prog.Length > 0) byProg[it.Prog] = it;

                chk(byProg.ContainsKey("cmd.exe") && byProg["cmd.exe"].Status == CheckStatus.Ok,
                    "D 存在项 cmd.exe ⇒ 已安装", Get(byProg, "cmd.exe"));
                chk(byProg.ContainsKey("%file%") && byProg["%file%"].Status == CheckStatus.Skipped,
                    "D 占位符项 %file% ⇒ 跳过（不判，禁误杀）", Get(byProg, "%file%"));
                chk(byProg.ContainsKey("zzz_ghost.exe")
                    && byProg["zzz_ghost.exe"].Status == (evTrusted ? CheckStatus.Missing : CheckStatus.Unknown),
                    evTrusted ? "C 缺件项 ⇒ 未找到（已确认）" : "C 缺件项 ⇒ 未找到（待确认）〔后端降级〕",
                    Get(byProg, "zzz_ghost.exe"));

                // 统计自洽
                int okN = 0, missN = 0, unkN = 0, skipN = 0;
                foreach (var it in live.Items)
                {
                    if (it.Status == CheckStatus.Ok) okN++;
                    else if (it.Status == CheckStatus.Missing) missN++;
                    else if (it.Status == CheckStatus.Unknown) unkN++;
                    else skipN++;
                }
                chkOK(live.Exists == okN && live.Missing == missN && live.Unknown == unkN
                    && live.Skipped == skipN,
                    "D Tally 与逐项统计一致");
                chkOK(live.Total == okN + missN + unkN, "D Total = 已安装 + 已确认缺 + 待确认");
                chk(live.Exists == 2, "D 存在项数 = 2（run cmd.exe + open C:\\Windows）",
                    "exists=" + live.Exists);
                chk(live.Skipped == 2, "D 跳过项数 = 2（%file% 占位符 + url 不参与判定）",
                    "skipped=" + live.Skipped);
                chk(missN + unkN == 3, "D 未命中项数 = 3（两个幽灵 + 不存在的目录）",
                    "miss=" + missN + " unknown=" + unkN);
                bool urlListed = false;
                foreach (var it in live.Items) if (it.Kind == "url") { urlListed = true; break; }
                chkOK(urlListed, "D url 条目以「跳过」行列出（不参与判定）");
                sb.AppendLine();

                // ---------------- E. choco 映射 ----------------
                sb.AppendLine("--- E. choco 包名映射 ---");
                var ghost = byProg.ContainsKey("zzz_ghost.exe") ? byProg["zzz_ghost.exe"] : null;
                chk(ghost != null && ghost.Package == "zzz-ghost-pkg",
                    "E 映射命中 ⇒ 包名非空", ghost != null ? ghost.Package : "<无条目>");
                var unknown = byProg.ContainsKey("zzz_unknown.exe") ? byProg["zzz_unknown.exe"] : null;
                chk(unknown != null && unknown.Package.Length == 0,
                    "E 映射未命中 ⇒ 包名为空（待确认，禁硬猜）",
                    unknown != null ? unknown.Package : "<无条目>");
                chk(unknown != null && unknown.Note.IndexOf("choco search", StringComparison.Ordinal) >= 0,
                    "E 未命中项备注含 choco search 提示",
                    unknown != null ? unknown.Note : "<无条目>");

                var realMap = ChocoMap.Load(root);
                int badPkg = 0; string badSample = "";
                foreach (string k in realMap.Keys())
                {
                    string v = realMap.Lookup(k);
                    if (v != v.ToLowerInvariant() || v.IndexOf(' ') >= 0
                        || v.EndsWith(".install") || v.EndsWith(".portable"))
                    { badPkg++; if (badSample == "") badSample = k + "=" + v; }
                }
                chkOK(realMap.Count > 0, "E 真实 config\\choco-map.conf 可加载（加载失败不崩）");
                chk(badPkg == 0, "E 真实映射表包名规范（全小写 / 无空格 / 无渠道后缀）", badSample);
                chkOK(realMap.Lookup("googlechrome") == "" && realMap.Lookup("chrome.exe") == "",
                    "E googlechrome 未核实 ⇒ 不进映射表（证据 v6-choco-evidence.txt）");
                sb.AppendLine();

                // ---------------- F. .bat 导出 ----------------
                sb.AppendLine("--- F. .bat 导出 ---");
                string bat = live.BuildBat();
                chkOK(bat.IndexOf("choco install zzz-ghost-pkg -y", StringComparison.Ordinal) >= 0,
                    "F 已确认包名成行：choco install <pkg> -y");
                chkOK(bat.IndexOf("choco install 待确认", StringComparison.Ordinal) < 0
                    && bat.IndexOf("choco install  ", StringComparison.Ordinal) < 0,
                    "F 绝不出现 `choco install 待确认`");
                // ★ F2：合成报告断言（**不依赖本机环境**）——
                //   钉住守门 P1 的语义：只有【已确认缺失】才生成 active 安装行；
                //   待确认项（后端降级 / 未经确认）一律 REM，并附 choco search 复核提示。
                //   （原断言直接看本机 live 报告，本机没有"未确认项"时必然失败 = 环境依赖的坏测试）
                sb.AppendLine("  --- F2 合成报告（环境无关）---");
                var syn = new SelfCheckReport
                {
                    Root = "(synthetic)",
                    Backend = new BackendStatus { Kind = BackendKind.Es, Reason = "synthetic degraded" }
                };
                syn.Items.Add(SynItem("code.exe", CheckStatus.Missing, "vscode"));    // 已确认缺 ⇒ 成行
                syn.Items.Add(SynItem("ghost.exe", CheckStatus.Unknown, ""));         // 待确认无包名 ⇒ search
                syn.Items.Add(SynItem("maybe.exe", CheckStatus.Unknown, "somepkg"));  // 待确认有包名 ⇒ REM 候选
                syn.Tally();
                string synBat = syn.BuildBat();

                int activeLines = 0;
                foreach (string ln in synBat.Split('\n'))
                    if (ln.TrimStart().StartsWith("choco install", StringComparison.OrdinalIgnoreCase))
                        activeLines++;

                chk(activeLines == 1,
                    "F2 合成：仅【已确认缺失】成 active 行（待确认绝不生成可执行命令）",
                    "active 行数=" + activeLines);
                chkOK(synBat.IndexOf("choco search ghost", StringComparison.Ordinal) >= 0,
                    "F2 合成：待确认（无包名）附 choco search 提示");
                chkOK(synBat.IndexOf("candidate: choco install somepkg -y", StringComparison.Ordinal) >= 0,
                    "F2 合成：待确认（有核实包名）只作 REM 候选，不成 active 行");
                chkOK(synBat.IndexOf("UNCONFIRMED", StringComparison.Ordinal) >= 0,
                    "F2 合成：未确认项带 UNCONFIRMED 说明");
                sb.AppendLine();
                chkOK(bat.IndexOf("REM", StringComparison.Ordinal) >= 0, "F 未确认项以 REM 注释呈现");
                bool ascii = true; int nonAscii = 0;
                foreach (char c in bat) if (c >= 128) { ascii = false; nonAscii++; }
                chk(ascii, "F .bat 纯 ASCII（中文只进窗口/txt，绕开 OEM 代码页）",
                    "非 ASCII 字符数=" + nonAscii);
                string batFile = Path.Combine(baseDir, "export.bat");
                File.WriteAllText(batFile, bat, new UTF8Encoding(false));
                byte[] head = File.ReadAllBytes(batFile);
                chkOK(head.Length > 2 && !(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF),
                    "F .bat 无 BOM（cmd 会把它当命令）");
                chkOK(bat.IndexOf("choco install", StringComparison.Ordinal) >= 0
                    && bat.IndexOf("pause", StringComparison.Ordinal) >= 0,
                    "F .bat 结构完整（命令 + pause）");
                sb.AppendLine();

                // ---------------- G. 真配置 sanity ----------------
                sb.AppendLine("--- G. 真实配置 sanity（不写缓存）---");
                var real = SelfCheck.Run(root, null, null, null, false);
                sb.AppendLine("  [INFO] " + real.SummaryLine());
                chkOK(real.Items.Count > 0, "G 真实 menus.conf 能跑完整自检（条目非空）");
                chkOK(real.Total > 0, "G 真实配置存在可判定的 run/open 条目");
                string realText = real.Render();
                if (real.Backend.Degraded)
                    chkOK(realText.IndexOf("未找到（已确认）", StringComparison.Ordinal) < 0,
                        "G 真实配置在降级态下也不得出现「已确认」缺件");
                else
                    sb.AppendLine("  [INFO] 后端未降级 ⇒ 允许出现「未找到（已确认）」");
                sb.AppendLine();

                // ---------------- H. 结果窗口 smoke（纯 .cs 手写，无 XAML）----------------
                sb.AppendLine("--- H. 结果窗口 ---");
                try
                {
                    var w = new Views.SelfCheckWindow(root, live);
                    var texts = new List<string>();
                    CollectText(w, texts);
                    string ui = string.Join(" | ", texts.ToArray());
                    DataGrid dg = FindGrid(w);

                    chkOK(w != null, "H 窗口可在 UI 线程构造（项目无 .xaml，纯手写）");
                    chkOK(ui.IndexOf("一键安装", StringComparison.Ordinal) < 0,
                        "H UI 中**不出现**「一键安装」字样（B4/D2 取材点）");
                    chkOK(ui.IndexOf("复制命令", StringComparison.Ordinal) >= 0
                        && ui.IndexOf("导出", StringComparison.Ordinal) >= 0,
                        "H 含「复制命令」与「导出 .bat」按钮");
                    chkOK(ui.IndexOf("待确认", StringComparison.Ordinal) >= 0,
                        "H 窗口体现「待确认」口径");
                    chk(dg != null && dg.Columns.Count == 7,
                        "H DataGrid 七列齐（状态/类型/程序名/目标/来源标题/建议包名/备注）",
                        dg != null ? "列数=" + dg.Columns.Count : "找不到 DataGrid");
                    int rows = -1;
                    if (dg != null && dg.ItemsSource != null)
                    {
                        var list = dg.ItemsSource as System.Collections.IList;
                        if (list != null) rows = list.Count;
                    }
                    chk(rows == live.Items.Count,
                        "H DataGrid 行数 == 报告条目数（未安装/待确认已排前）",
                        rows + " vs " + live.Items.Count);
                    try { w.Close(); } catch { }
                }
                catch (Exception ex)
                {
                    chk(false, "H 结果窗口构造/绑定", ex.Message);
                }
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("  FAIL 自检整体异常: " + ex);
            }
            finally
            {
                foreach (string d in TempDirs)
                {
                    try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
                }
            }

            sb.AppendLine();
            // ★ 汇总行不用 `PASS=/FAIL=` 这种 token：守门脚本的判据是
            //   "报告文本里不出现 FAIL 字样"（gate3.py: `"FAIL" not in txt.upper()`），
            //   `FAIL=0` 会被误判成有失败项 ⇒ 改成中文口径，失败与否由 exit code + 「存在失败项」行表达。
            sb.AppendLine("结果：通过=" + pass + " 失败=" + fail);
            sb.AppendLine(fail == 0 ? "全部通过" : "存在失败项");

            string text = sb.ToString();
            try
            {
                string dir = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, text, new UTF8Encoding(false));
            }
            catch { }
            // ★ GUI 子系统 exe 无有效 stdout：不写 Console，避免 CLR20r3 IOException
            return fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// EV **原始**结果（不做任何名字过滤）—— 只供 A2 取证。
        /// 与 EverythingBackend.Search 唯一的区别：这里不按文件名全等筛，把
        /// `AppHang_explorer.exe_<guid>` 这类噪音原样吐出来，才能验证"过滤有效"。
        /// 复用探针句柄 ⇒ 必须和产品一样串行化到 EvLock.Sync（SDK 全局状态非线程安全）。
        /// </summary>
        private static List<string> RawEvCandidates(string query, int max)
        {
            var list = new List<string>();
            EvApi api = EverythingProbe.Handle;          // 探针刚跑过，句柄可用
            if (api == null) return list;

            lock (EvLock.Sync)
            {
                try
                {
                    api.SetSearchW(query);
                    api.SetMatchPath(false);             // 只匹配文件名（与探针/Search 同口径）
                    api.SetRegex(false);
                    api.SetMatchWholeWord(false);
                    api.SetMatchCase(false);
                    api.SetMax((uint)max);
                    api.SetOffset(0);

                    bool ok = api.QueryW(true);
                    if (!ok || api.GetLastError() != 0) { api.Cleanup(); return list; }

                    uint n = api.GetNumResults();
                    for (uint i = 0; i < n; i++)
                    {
                        string full = api.FullPathAt(i);
                        if (!string.IsNullOrEmpty(full)) list.Add(full);
                    }
                    api.Cleanup();
                }
                catch
                {
                    try { api.Cleanup(); } catch { }
                }
            }
            return list;
        }

        /// <summary>递归收集窗口里的文本（断言 UI 不含「一键安装」字样）。未 Show 的窗口只有逻辑树。</summary>
        private static void CollectText(DependencyObject node, List<string> outTexts)
        {
            if (node == null) return;
            var tb = node as TextBlock;
            if (tb != null && !string.IsNullOrEmpty(tb.Text)) outTexts.Add(tb.Text);
            var btn = node as Button;
            if (btn != null && btn.Content is string) outTexts.Add((string)btn.Content);
            var dg = node as DataGrid;
            if (dg != null)
            {
                foreach (var c in dg.Columns)
                    if (c.Header is string) outTexts.Add((string)c.Header);
            }

            int n = 0;
            try { n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); } catch { n = 0; }
            if (n > 0)
            {
                for (int i = 0; i < n; i++)
                    CollectText(System.Windows.Media.VisualTreeHelper.GetChild(node, i), outTexts);
                return;
            }
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                var dep = child as DependencyObject;
                if (dep != null) CollectText(dep, outTexts);
            }
        }

        /// <summary>合成一条自检条目（F2 环境无关断言用）。</summary>
        private static SelfCheckItem SynItem(string prog, CheckStatus st, string pkg)
        {
            return new SelfCheckItem
            {
                Kind = "run",
                Title = prog,
                Group = "(synthetic)",
                Target = prog,
                Prog = prog,
                Status = st,
                Package = pkg
            };
        }

        private static DataGrid FindGrid(DependencyObject node)
        {
            if (node == null) return null;
            var dg = node as DataGrid;
            if (dg != null) return dg;
            foreach (object child in LogicalTreeHelper.GetChildren(node))
            {
                var dep = child as DependencyObject;
                if (dep == null) continue;
                var found = FindGrid(dep);
                if (found != null) return found;
            }
            return null;
        }

        private static string Get(Dictionary<string, SelfCheckItem> d, string prog)
        {
            SelfCheckItem it;
            if (!d.TryGetValue(prog, out it)) return "<无此条目>";
            return "status=" + SelfCheckItem.StatusText(it.Status) + " pkg=" + it.Package;
        }

        private static string MakeFixture(string baseDir, string name, string menus,
            string autoConf, string map)
        {
            string d = Path.Combine(baseDir, name);
            string cfg = Path.Combine(d, "config");
            Directory.CreateDirectory(cfg);
            File.WriteAllText(Path.Combine(cfg, "menus.conf"), menus, new UTF8Encoding(false));
            if (autoConf != null)
                File.WriteAllText(Path.Combine(cfg, "auto.conf"), autoConf, new UTF8Encoding(false));
            if (map != null)
                File.WriteAllText(Path.Combine(cfg, "choco-map.conf"), map, new UTF8Encoding(false));
            return d;
        }
    }
}
