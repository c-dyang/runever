using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RunEverCs
{
    /// <summary>
    /// 架构自检（拆-0）：**分层引用方向**断言。只读源码文本，不改任何东西。
    ///
    /// 用法：RunEverCs.exe --selftest-arch &lt;root&gt; &lt;out.txt&gt;
    ///       注意：**禁带 --root**（root 由位置参数给；带 --root ⇒ exit=2）
    ///
    /// 准绳：`~/.dsh/skills/runever-maintenance/references/architecture.md` 第六节
    ///       「目标架构：五层 + 四刀」。本套只断言方向，不搬文件（搬迁是拆-1/2/3 的活）。
    ///
    /// 判定口径（硬约束，见文件尾「局限」）：
    ///   · 只扫 `.cs` 源文本里的 `using` 行（含 `using X = Ns.Type;` 别名形式）；
    ///   · 层的归属**只看目录**（Models\ / Services\ / Native\ / Views\），不看命名空间；
    ///   · 不引 Roslyn、不引第三方 —— net48 零依赖是硬约束，正则足够。
    ///
    /// 为什么不用 `Assembly.GetReferencedAssemblies()`：本仓全部代码同属 RunEverCs 一个
    /// 程序集，程序集级引用只能看到外部 DLL，**层与层之间的引用一条都看不到** ⇒ 恒绿，
    /// 等于没测。必须落到源码文本。
    /// </summary>
    public static class ArchSelfTest
    {
        /// <summary>被断言的四个分层目录（顺序 = 报告顺序）。</summary>
        private static readonly string[] Layers = { "Models", "Services", "Native", "Views" };

        // ---------------------------------------------------------------
        // 规则表：某层里出现某命名空间的 using ⇒ 违规
        //   只收"方向"规则；白名单里的 Services→WPF 也必须有对应规则，否则白名单永远 0 命中。
        // ---------------------------------------------------------------
        private sealed class Rule
        {
            public string Layer;        // 文件所在层（目录名）
            public string Namespace;    // 禁止的命名空间前缀（含自身）
            public string Kind;         // 违规类型标签（报告里的一眼识别符）
            public string Basis;        // 依据（architecture.md 六）
        }

        private static readonly Rule[] Rules =
        {
            new Rule { Layer = "Models",   Namespace = "RunEverCs.Services", Kind = "Models→Services",
                       Basis = "L0 契约与模型不得反向依赖 L1/L2/L3" },
            new Rule { Layer = "Models",   Namespace = "RunEverCs.Views",   Kind = "Models→Views",
                       Basis = "L0 不得依赖 L4 呈现" },
            new Rule { Layer = "Models",   Namespace = "RunEverCs.Native",  Kind = "Models→Native",
                       Basis = "L0 不得依赖 L4 呈现（Native 属呈现适配）" },
            new Rule { Layer = "Services", Namespace = "RunEverCs.Views",   Kind = "Services→Views",
                       Basis = "L1/L2/L3 不得依赖 L4 呈现" },
            new Rule { Layer = "Native",   Namespace = "RunEverCs.Views",   Kind = "Native→Views",
                       Basis = "同层（L4）内 Native 与 Views 互不依赖" },
            new Rule { Layer = "Services", Namespace = "System.Windows",    Kind = "Services→WPF",
                       Basis = "第六节「唯一现存违规」：WPF 混进服务层（IconDiskCache 引 System.Windows.Media）" },
        };

        /// <summary>Views 层额外口径：只许引用这三个下层，其余 `RunEverCs*` 一律算反向/自引。</summary>
        private static readonly string[] ViewsAllowed =
        {
            "RunEverCs.Models", "RunEverCs.Services", "RunEverCs.Native"
        };

        // ---------------------------------------------------------------
        // 白名单：**按文件 + 违规类型**精确列，不许写成"整层忽略"。
        //   一条 = 一个已知违规的落地位置；`RemoveWhen` 是删条条件（拆-2 用得到）。
        // ---------------------------------------------------------------
        // 拆-2 起白名单 = 0 条 ⇒ 这里不再实例化任何豁免项，字段只剩「读」没有「写」，
        // CS0649「从未赋值」是预期结果（结构保留，方便将来真的需要豁免时直接用）。
#pragma warning disable 0649
        private sealed class WhitelistItem
        {
            public string File;             // 相对 root 的路径（反斜杠）
            public string[] Namespaces;     // 覆盖的命名空间（含其子命名空间）
            public string Kind;             // 违规类型
            public string Reason;           // 为什么现在不改
            public string RemoveWhen;       // 什么条件下必须删掉本条
            public int Hits;                // 运行期命中数（=0 ⇒ 过期）
        }
#pragma warning restore 0649

        private static List<WhitelistItem> BuildWhitelist()
        {
            var list = new List<WhitelistItem>();
            return list;
        }

        // ---------------------------------------------------------------
        // 运行期数据结构
        // ---------------------------------------------------------------
        private sealed class UsingLine
        {
            public int Line;
            public string Text;
            public string Ns;
        }

        private sealed class Hit
        {
            public string File;
            public int Line;
            public string UsingText;
            public string Kind;
            public WhitelistItem White;
        }

        private sealed class LayerStat
        {
            public string Layer;
            public int Files;
            public int Lines;
        }

        private sealed class Result
        {
            public List<Hit> NewHits = new List<Hit>();
            public List<Hit> WhiteHits = new List<Hit>();
            public List<LayerStat> Stats = new List<LayerStat>();
            public List<WhitelistItem> Whitelist = new List<WhitelistItem>();
            public List<string> Problems = new List<string>();      // 结构性失败（层目录缺失等）
            public List<string> Notes = new List<string>();         // 不计违规的附注
            public int ScanFiles;
            public int RootFiles;
            public int RootLines;
        }

        /// <summary>`^using (static)? (Alias =)? Ns ;` —— 只认这一种行形。</summary>
        private static readonly Regex UsingRe = new Regex(
            @"^using\s+(?:static\s+)?(?:(?<alias>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*)?(?<ns>[A-Za-z_][A-Za-z0-9_.]*)\s*;\s*(?://.*)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // ===============================================================
        // 入口
        // ===============================================================
        public static int Run(string[] args)
        {
            if (args == null || args.Length < 3) return 2;

            string root = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int rc;

            if (HasArg(args, "--root"))
            {
                // 带了 --root 时位置参数已被挤歪（args[1] 成了 "--root"）⇒ 用最后一个 .txt 参数
                // 当报告落点，避免"exit=2 但一个文件都没写"的静默失败。
                string last = args[args.Length - 1];
                if (!last.StartsWith("-", StringComparison.Ordinal)
                    && last.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    outFile = last;

                sb.AppendLine("=== 架构自检（拆-0 · 分层引用方向）===");
                sb.AppendLine("  用法错误：本套**禁带 --root**，root 必须由位置参数给。");
                sb.AppendLine("  正确：RunEverCs.exe --selftest-arch <root> <out.txt>");
                sb.AppendLine();
                sb.AppendLine("结果: 用法错误 ✗");
                rc = 2;
            }
            else
            {
                try
                {
                    var r = Analyze(root);
                    Render(sb, root, r);
                    rc = (r.NewHits.Count == 0 && r.Problems.Count == 0) ? 0 : 1;
                }
                catch (Exception ex)
                {
                    sb.Clear();
                    sb.AppendLine("=== 架构自检（拆-0 · 分层引用方向）===");
                    sb.AppendLine("  FAIL 异常: " + ex.GetType().Name + ": " + ex.Message);
                    sb.AppendLine(ex.StackTrace);
                    sb.AppendLine();
                    sb.AppendLine("结果: 失败 1 项 ✗");
                    rc = 1;
                }
            }

            WriteFile(outFile, sb.ToString());
            return rc;
        }

        private static bool HasArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ===============================================================
        // 分析
        // ===============================================================
        private static Result Analyze(string root)
        {
            var r = new Result();
            r.Whitelist = BuildWhitelist();

            string csDir = Path.Combine(root, "cs");
            if (!Directory.Exists(csDir))
            {
                r.Problems.Add("找不到源码目录：" + csDir + "（root 传错？）");
                return r;
            }

            foreach (string layer in Layers)
            {
                string dir = Path.Combine(csDir, layer);
                var stat = new LayerStat { Layer = layer };
                r.Stats.Add(stat);

                if (!Directory.Exists(dir))
                {
                    // ★ 层目录缺失必须失败：否则规则集恒空 ⇒ 假绿
                    r.Problems.Add("层目录缺失：" + Path.Combine("cs", layer));
                    continue;
                }

                var files = new List<string>(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
                files.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (string f in files)
                {
                    if (IsUnderBuildDir(f)) continue;

                    var lines = ReadLines(f);
                    stat.Files++;
                    stat.Lines += lines.Length;
                    r.ScanFiles++;

                    string rel = RelPath(root, f);
                    CheckFile(r, layer, rel, lines);
                }
            }

            // 非分层（cs 根目录）：只报数，不参与断言
            foreach (string f in Directory.GetFiles(csDir, "*.cs", SearchOption.TopDirectoryOnly))
            {
                var lines = ReadLines(f);
                r.RootFiles++;
                r.RootLines += lines.Length;
            }

            if (r.ScanFiles == 0)
                r.Problems.Add("四层共扫到 0 个 .cs（root 传错或被过滤光了）");

            // 白名单过期（不算失败，但要显眼）。扫描没跑全时不判定 —— 否则全是假过期。
            if (r.Problems.Count == 0)
            {
                foreach (var w in r.Whitelist)
                    if (w.Hits == 0)
                        r.Notes.Add("白名单过期：" + w.File + " 已无 " + string.Join(" / ", w.Namespaces)
                            + " 命中 ⇒ 移除条件（" + w.RemoveWhen + "）可能已满足，请删条");
            }

            r.NewHits.Sort(CompareHit);
            r.WhiteHits.Sort(CompareHit);
            return r;
        }

        private static int CompareHit(Hit a, Hit b)
        {
            int c = string.Compare(a.File, b.File, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return a.Line.CompareTo(b.Line);
        }

        /// <summary>扫一个文件：命中哪条规则、是否在白名单里。</summary>
        private static void CheckFile(Result r, string layer, string rel, string[] lines)
        {
            foreach (var u in ReadUsings(lines))
            {
                string kind = MatchRule(layer, u.Ns);
                if (kind == null) continue;

                var hit = new Hit
                {
                    File = rel,
                    Line = u.Line,
                    UsingText = u.Text,
                    Kind = kind,
                    White = MatchWhitelist(r.Whitelist, rel, u.Ns),
                };

                if (hit.White != null)
                {
                    hit.White.Hits++;
                    r.WhiteHits.Add(hit);
                }
                else r.NewHits.Add(hit);
            }

            if (string.Equals(layer, "Services", StringComparison.OrdinalIgnoreCase))
                NoteServicesToNative(r, rel, lines);
        }

        /// <summary>返回命中的违规类型；不命中返回 null。</summary>
        private static string MatchRule(string layer, string ns)
        {
            for (int i = 0; i < Rules.Length; i++)
            {
                if (!string.Equals(Rules[i].Layer, layer, StringComparison.OrdinalIgnoreCase)) continue;
                if (NsMatches(ns, Rules[i].Namespace)) return Rules[i].Kind;
            }

            // Views：只许往下用 Models / Services / Native，其余 RunEverCs* 一律算
            if (string.Equals(layer, "Views", StringComparison.OrdinalIgnoreCase))
            {
                if (!ns.StartsWith("RunEverCs", StringComparison.Ordinal)) return null;
                for (int i = 0; i < ViewsAllowed.Length; i++)
                    if (NsMatches(ns, ViewsAllowed[i])) return null;
                return "Views→上层/自身";
            }
            return null;
        }

        /// <summary>`ns` 等于 `target` 或落在其子命名空间里。</summary>
        private static bool NsMatches(string ns, string target)
        {
            if (string.Equals(ns, target, StringComparison.Ordinal)) return true;
            return ns.StartsWith(target + ".", StringComparison.Ordinal);
        }

        /// <summary>白名单按「文件 + 命名空间（最长优先）」精确匹配，不做同层豁免。</summary>
        private static WhitelistItem MatchWhitelist(List<WhitelistItem> list, string rel, string ns)
        {
            WhitelistItem best = null;
            int bestLen = -1;
            foreach (var w in list)
            {
                if (!string.Equals(Normalize(w.File), Normalize(rel), StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string n in w.Namespaces)
                {
                    if (!NsMatches(ns, n)) continue;
                    if (n.Length > bestLen) { best = w; bestLen = n.Length; }
                }
            }
            return best;
        }

        private static string Normalize(string p)
        {
            return (p ?? "").Replace('/', '\\').Trim();
        }

        /// <summary>Services→Native 只是附注（拆-2 的待办），不计违规。</summary>
        private static void NoteServicesToNative(Result r, string rel, string[] lines)
        {
            foreach (var u in ReadUsings(lines))
            {
                if (!NsMatches(u.Ns, "RunEverCs.Native")) continue;
                r.Notes.Add("附注（不计违规）：" + rel + ":" + u.Line + "  " + u.Text
                    + "  —— 呈现层暂留 Services，拆-2 处理");
            }
        }

        // ===============================================================
        // 读文本
        // ===============================================================
        private static string[] ReadLines(string path)
        {
            try { return File.ReadAllLines(path, Encoding.UTF8); }
            catch { return new string[0]; }
        }

        /// <summary>抽 using 行。跳过 `/* */` 块内的行（避免把注释掉的 using 当真）。</summary>
        private static List<UsingLine> ReadUsings(string[] lines)
        {
            var outp = new List<UsingLine>();
            bool inBlock = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string s = (lines[i] ?? "").Trim();
                if (s.Length == 0) continue;

                if (inBlock)
                {
                    int ce = s.IndexOf("*/", StringComparison.Ordinal);
                    if (ce < 0) continue;
                    s = s.Substring(ce + 2).Trim();
                    inBlock = false;
                    if (s.Length == 0) continue;
                }

                if (s.StartsWith("/*", StringComparison.Ordinal))
                {
                    int ce = s.IndexOf("*/", StringComparison.Ordinal);
                    if (ce < 0) { inBlock = true; continue; }
                    s = s.Substring(ce + 2).Trim();
                    if (s.Length == 0) continue;
                }

                // 行注释不可能是 using（`// using X;` 被注掉）
                if (s.StartsWith("//", StringComparison.Ordinal)) continue;

                var m = UsingRe.Match(s);
                if (!m.Success) continue;

                outp.Add(new UsingLine { Line = i + 1, Text = s, Ns = m.Groups["ns"].Value });
            }
            return outp;
        }

        private static bool IsUnderBuildDir(string path)
        {
            string p = path.Replace('/', '\\');
            return p.IndexOf(@"\obj\", StringComparison.OrdinalIgnoreCase) >= 0
                || p.IndexOf(@"\bin\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string RelPath(string root, string full)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\', '/');
            string f = Path.GetFullPath(full);
            if (f.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase))
                return f.Substring(r.Length + 1);
            return f;
        }

        private static void WriteFile(string path, string text)
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, text, new UTF8Encoding(false));
            }
            catch { }
        }

        // ===============================================================
        // 报告
        // ===============================================================
        private static void Render(StringBuilder sb, string root, Result r)
        {
            sb.AppendLine("=== 架构自检（拆-0 · 分层引用方向）===");
            sb.AppendLine("  根目录: " + Path.GetFullPath(root));
            sb.AppendLine("  时间  : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("  口径  : 只认 `using` 行（含 `using X = Ns.Type;` 别名）；层的归属只看目录；不引 Roslyn");
            sb.AppendLine();

            sb.AppendLine("[规则]  R1..R5 = 断言项；命中且不在白名单 ⇒ 本套 exit=1");
            sb.AppendLine("  R1  Models\\**    ✗ using RunEverCs.Services / .Views / .Native");
            sb.AppendLine("  R2  Services\\**  ✗ using RunEverCs.Views");
            sb.AppendLine("  R3  Native\\**    ✗ using RunEverCs.Views");
            sb.AppendLine("  R4  Views\\**     ✗ 引用 Models / Services / Native 以外的 RunEverCs 命名空间");
            sb.AppendLine("  R5  Services\\**  ✗ using System.Windows*（服务层禁 WPF）");
            sb.AppendLine();

            sb.AppendLine("[白名单] " + r.Whitelist.Count + " 条（按「文件 + 违规类型」精确列，无整层豁免）");
            for (int i = 0; i < r.Whitelist.Count; i++)
            {
                var w = r.Whitelist[i];
                sb.AppendLine("  " + (i + 1) + ") " + w.File + "   [" + w.Kind + "]   命中 " + w.Hits + " 处");
                sb.AppendLine("     命名空间 : " + string.Join(" / ", w.Namespaces));
                sb.AppendLine("     原因     : " + w.Reason);
                sb.AppendLine("     移除条件 : " + w.RemoveWhen);
            }
            sb.AppendLine();

            sb.AppendLine("[命中明细] 共 " + (r.WhiteHits.Count + r.NewHits.Count)
                + " 处（白名单 " + r.WhiteHits.Count + " / 新增 " + r.NewHits.Count + "）");
            if (r.WhiteHits.Count + r.NewHits.Count == 0)
                sb.AppendLine("  （无）");

            var all = new List<Hit>(r.WhiteHits);
            all.AddRange(r.NewHits);
            all.Sort(CompareHit);
            foreach (var h in all)
            {
                string tag = h.White != null ? "[白名单]" : "[新增]  ";
                sb.AppendLine("  " + tag + " " + h.File + ":" + h.Line
                    + "  " + h.UsingText + "   (" + h.Kind + ")");
            }
            sb.AppendLine();

            sb.AppendLine("[分层计数]");
            int tf = 0, tl = 0;
            foreach (var s in r.Stats)
            {
                tf += s.Files; tl += s.Lines;
                sb.AppendLine(string.Format("  {0,-9} {1,3} 文件 {2,7} 行", s.Layer, s.Files, s.Lines));
            }
            sb.AppendLine(string.Format("  {0,-9} {1,3} 文件 {2,7} 行  ← 断言范围（cs\\Models|Services|Native|Views）",
                "合计", tf, tl));
            sb.AppendLine(string.Format("  {0,-9} {1,3} 文件 {2,7} 行  ← 非分层（cs 根：自检/RunEverApp/TrayApp/CLI，不参与断言）",
                "非分层", r.RootFiles, r.RootLines));
            sb.AppendLine();

            if (r.Problems.Count > 0)
            {
                sb.AppendLine("[结构问题] " + r.Problems.Count + " 项（任何一项 ⇒ exit=1）");
                foreach (string p in r.Problems) sb.AppendLine("  FAIL " + p);
                sb.AppendLine();
            }

            if (r.Notes.Count > 0)
            {
                sb.AppendLine("[附注] " + r.Notes.Count + " 条（不计违规）");
                foreach (string n in r.Notes) sb.AppendLine("  " + n);
                sb.AppendLine();
            }

            int stale = 0;
            if (r.Problems.Count == 0)
                foreach (var w in r.Whitelist) if (w.Hits == 0) stale++;

            sb.AppendLine(string.Format("白名单 {0} 条 / 新增违规 {1} 条 / 白名单过期 {2} 条",
                r.Whitelist.Count, r.NewHits.Count, stale));
            if (r.Problems.Count > 0)
                sb.AppendLine("结构问题 " + r.Problems.Count + " 项");
            sb.AppendLine();

            bool ok = r.NewHits.Count == 0 && r.Problems.Count == 0;
            sb.AppendLine("结果: " + (ok
                ? "全部通过 ✓   (断言 " + CountAssertions(r) + " 条 / 命中 " + (r.WhiteHits.Count + r.NewHits.Count) + " 处，全部在白名单)"
                : "失败 " + (r.NewHits.Count + r.Problems.Count) + " 项 ✗   (新增违规 "
                    + r.NewHits.Count + " / 结构问题 " + r.Problems.Count + ")"));
        }

        private static int CountAssertions(Result r)
        {
            // 断言条数 = 规则条数 + Views 口径 1 条 + 每条白名单 1 条（"该违规仍在原处"）
            return Rules.Length + 1 + r.Whitelist.Count;
        }
    }
}
