// ============================================================
// RunEver · 全盘自检 自检（功能 B）
//
// 用法：RunEverCs.exe --selftest-selfcheck <root> <out.txt>   （exit 0 = 全过）
//
// 覆盖（对照任务表 S8）：
//   A EV 探针（信息输出；若判定可用则**必须** hits>0 且 lastError==0 —— 这是不变式，违反即 FAIL）
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
                    chk(ev.FullPathVerified, "A 探针读回全路径与查询名逐字符一致",
                        "hits=" + ev.ProbeHits);
                }
                else
                {
                    sb.AppendLine("  [INFO] EV 不可用 ⇒ 后端降级（这是正常路径，不算 FAIL）");
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
            sb.AppendLine("结果：PASS=" + pass + " FAIL=" + fail);
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
