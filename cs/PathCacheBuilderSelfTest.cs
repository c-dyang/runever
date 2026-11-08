using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 路径缓存构建器 自检（对照 src\PathCache.ahk 的实测踩坑）。
    ///
    /// 断言：
    ///   ① 全量构建统计非零且合理（resolved 命中 ≥ 一定比例，且至少一条来自 ①② 级）
    ///   ② 二次构建 skipped == 全部（增量生效）
    ///   ③ 解析结果**真实存在**，且不落在 SysWOW64 / WinSxS 等「明确错误」位置
    ///      ★ 口径变更（2026-10-04，非回归）：同名多命中按架构契约 §4 / P1 = 文件创建
    ///        时间降序取第一条、不加排除表 ⇒ 测试桩抢占（…\TempSave\…\7z.exe）属预期行为，
    ///        原「不落在 \TempSave\」判据已按契约口径改写（见下方断言③注释）。
    ///   ④ es.exe 无法执行时降级不崩（用假 es.exe 桩，全程只走 ①② 级）
    ///
    /// ★ 不污染用户真实缓存：所有构建都写临时文件（%TEMP%\rve_pathcache_selftest_*.txt），
    ///   跑完删除；真实 cache\paths.txt 一个字节都不碰。
    ///
    /// 用法：RunEverCs.exe --selftest-pathcache &lt;root&gt; &lt;out.txt&gt;
    /// </summary>
    public static class PathCacheBuilderSelfTest
    {
        /// <summary>用于临时缓存的临时文件名（跑完即删）。</summary>
        private static readonly List<string> TempFiles = new List<string>();

        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;

            // ★ C# 7.3 的 Action<T1,T2> 不支持可选参数（默认参数值）⇒ 两参/三参各摆一个委托
            Action<bool, string, string> chk = null;
            chk = (cond, label, extra) =>
            {
                if (cond) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (extra != "" ? "   <<< " + extra : "")); }
            };
            Action<bool, string> chkOK = (cond, label) => chk(cond, label, "");

            // 真实 es.exe（③ 用）；桩 es.exe 目录（④ 用）
            string realEs = PathCacheBuilder.ResolveEsPath(null);
            string stubDir = Path.Combine(Path.GetTempPath(), "rve_es_stub_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string stubEs = Path.Combine(stubDir, "es.exe");
            string candRoot = "";        // ③-C1 候选集夹具目录（finally 里清理）

            try
            {
                sb.AppendLine("=== 路径缓存构建器 自检 ===");
                sb.AppendLine("root = " + root);
                sb.AppendLine("es.exe(自动探测) = " + (realEs == "" ? "<不可用>" : realEs));
                sb.AppendLine();

                // ============================================================
                // 测试用配置：优先用真实 menus.conf（更真实），否则用合成配置（更可控）
                // ============================================================
                string menus = Path.Combine(root, "config", "menus.conf");
                List<string> progs;
                string source;
                if (File.Exists(menus))
                {
                    var cp = new ConfParser(menus);
                    cp.Parse();
                    progs = new List<string>(cp.CollectPrograms());
                    source = "真实 config\\menus.conf";
                }
                else
                {
                    progs = new List<string> { "cmd.exe", "calc.exe", "notepad.exe", "mspaint.exe", "taskmgr.exe" };
                    source = "合成最小配置（未找到 menus.conf）";
                }
                sb.AppendLine("程序名来源：" + source + "，收集到 " + progs.Count + " 个 run 条目程序名");
                sb.AppendLine("名称列表：" + string.Join(", ", progs.ToArray()));
                sb.AppendLine();

                chk(progs.Count > 0, "① CollectPrograms 收集到程序名（非空）",
                    "数量=" + progs.Count);

                // ============================================================
                // ① 全量构建（es 真实可用则三级全跑）
                // ============================================================
                string tmpCache = NewTempFile("full");
                var cache = new PathCache(tmpCache);
                var logs = new List<string>();
                var b1 = new PathCacheBuilder(cache, realEs, s => logs.Add(s));
                BuildStats s1;
                var sw = Stopwatch.StartNew();
                s1 = b1.Build(progs, true);
                sw.Stop();

                sb.AppendLine("① 全量构建统计：" + s1);
                sb.AppendLine("   耗时 " + sw.ElapsedMilliseconds + "ms，缓存条目数 " + cache.Count);
                sb.AppendLine("   日志：");
                foreach (var l in logs) sb.AppendLine("     " + l);
                sb.AppendLine();

                chk(s1.Resolved > 0, "① 全量构建 resolved > 0", s1.ToString());
                chk(s1.Resolved + s1.Failed == progs.Count,
                    "① resolved+failed == 程序名总数（无遗漏）",
                    (s1.Resolved + s1.Failed) + " vs " + progs.Count);
                chk(s1.Resolved >= (progs.Count + 1) / 2,
                    "① resolved 至少过半（合理下限）",
                    s1.Resolved + "/" + progs.Count + " 失败=" + string.Join(",", s1.FailedList.ToArray()));
                chk(cache.Count == s1.Resolved, "① 缓存条目数 == resolved",
                    cache.Count + " vs " + s1.Resolved);
                // 至少一条来自 ①② 级：说明"不依赖 es 也能解析"这条主路径是通的
                int tier12 = 0;
                foreach (var r in s1.ResolvedList)
                    if (r.Tier == ResolveTier.AppPaths || r.Tier == ResolveTier.Path) tier12++;
                chk(tier12 > 0, "① 至少有 1 条由 ①② 级（非 es）解析", "tier12=" + tier12);
                sb.AppendLine("   来源分布：AppPaths=" + CountTier(s1, ResolveTier.AppPaths)
                    + " Path=" + CountTier(s1, ResolveTier.Path)
                    + " Everything=" + CountTier(s1, ResolveTier.Everything));
                sb.AppendLine();

                // ============================================================
                // ② 二次构建 ⇒ 增量，全部 skipped
                // ============================================================
                var cache2 = new PathCache(tmpCache);        // 重新从盘上读，验证落盘内容
                var b2 = new PathCacheBuilder(cache2, realEs, null);
                var s2 = b2.Build(progs, false);
                sb.AppendLine("② 二次（增量）构建统计：" + s2);
                // ★ 增量语义（AHK :335-342）：只对「已成功解析」的条目 skip。
                //   首次解析失败的条目本来就没进缓存，二次构建必然重试 —— 这是设计如此，不是漏 skip。
                chk(s2.Skipped == s1.Resolved,
                    "② 二次构建 skipped == 首次 resolved（成功条目全部增量跳过）",
                    "skipped=" + s2.Skipped + " resolved(1st)=" + s1.Resolved);
                chkOK(s2.Resolved == 0,
                    "② 二次构建零重查（已成功条目不再解析）");
                chkOK(s2.Failed == s1.FailedList.Count,
                    "② 二次构建只重试首次失败的条目（failed 数一致）");
                // 增量"自愈"：把缓存里一条指向不存在文件的记录清掉，应被重查（而非永久 skip）
                if (cache2.Count > 0)
                {
                    string victim = null;
                    foreach (var kv in Snapshot(cache2)) { victim = kv.Key; break; }
                    cache2.Set(victim, @"Z:\__rve_not_exist__\ghost.exe");
                    cache2.Save();
                    var cache3 = new PathCache(tmpCache);
                    var b3 = new PathCacheBuilder(cache3, realEs, null);
                    var s3 = b3.Build(progs, false);
                    chk(s3.Skipped == s1.Resolved - 1,
                        "② 失效条目不被永久 skip（文件不存在 ⇒ 重查）",
                        "skipped=" + s3.Skipped + " 期望 " + (s1.Resolved - 1));
                }
                sb.AppendLine();

                // ============================================================
                // ③ 结果必须真实存在，且不得落在「明确错误」的位置
                //   ★★ 口径变更（2026-10-04，**非回归**，依据架构契约 §4 / P1）：
                //      同名多命中已由"PathWeight 加权择优"改为"**文件创建时间降序取第一条，
                //      不加目录排除表**"。⇒ 测试桩抢占（本机 7z.exe 会解析到
                //      `…\TempSave\autoextract-v1.2\**\7z.exe`，创建于 2026-09-29）
                //      属**预期行为**（契约 :97-98 明写），故原先的
                //      「解析结果不落在 \TempSave\」断言**不再是判据**，已按契约口径改写。
                //   保留项：SysWOW64 / WinSxS（ExcludeFrag 硬排除未动，P1 只禁"新增排除表"）。
                // ============================================================
                var bad = new List<string>();
                foreach (var kv in Snapshot(cache))
                {
                    string p = kv.Value;
                    if (!File.Exists(p)) bad.Add(kv.Key + " → " + p + "（解析结果文件不存在）");
                    if (p.IndexOf(@"\SysWOW64\", StringComparison.OrdinalIgnoreCase) >= 0) bad.Add(kv.Key + " → " + p + "（32 位 SysWOW64）");
                    if (PathCacheBuilder.IsExcluded(p)) bad.Add(kv.Key + " → " + p + "（命中硬排除）");
                }
                chk(bad.Count == 0,
                    "③ 解析结果真实存在，且不落在 SysWOW64/WinSxS 等错误位置（契约 §4 创建时间口径）",
                    string.Join(" | ", bad.ToArray()));
                // 32 位系统程序必须解析到 System32（calc/taskmgr 类）
                foreach (var kv in Snapshot(cache))
                {
                    if (kv.Key == "calc.exe" || kv.Key == "taskmgr.exe" || kv.Key == "notepad.exe")
                    {
                        chk(kv.Value.IndexOf("System32", StringComparison.OrdinalIgnoreCase) >= 0,
                            "③ " + kv.Key + " 解析到 System32（非 SysWOW64）", kv.Value);
                    }
                }

                // 加权规则单测（照抄 AHK 规则的直接验证）
                // ★ 权重公式（AHK :158-176）：命中优先级列表第 i 项（1 起）得 100 + (n - i)。
                //   n=4 ⇒ "C:\Program Files\"(i=1)=103 > "C:\Windows\System32\"(i=3)=101 > "C:\Windows\"(i=4)=100。
                //   ⚠ AHK 里 System32 反而比 Program Files 低 2 分 —— 这是 AHK 原实现的既有次序
                //     （注释说"System32 > Program Files"，代码实际相反）。本迁移**逐条照抄代码**，不擅自改权重，
                //     且实际不构成问题：系统程序由第 ② 级 System32 优先解析，根本走不到 es 加权这一步。
                int wSys = PathCacheBuilder.PathWeight(@"C:\Windows\System32\calc.exe");
                int wPF = PathCacheBuilder.PathWeight(@"C:\Program Files\7-Zip\7zFM.exe");
                int wTemp = PathCacheBuilder.PathWeight(@"C:\Users\x\AppData\Local\Temp\a\7z.exe");
                int wWow = PathCacheBuilder.PathWeight(@"C:\Windows\SysWOW64\calc.exe");
                int wWowNested = PathCacheBuilder.PathWeight(@"C:\Windows\System32\SysWOW64\calc.exe");
                chkOK(wPF == 103 && wSys == 101 && wTemp != wPF && wTemp != wSys,
                    "③ 权重公式与 AHK 一致（PF=103 / System32=101）");
                chkOK(wTemp == -100, "③ 权重：Temp 命中降权片段 ⇒ -100");
                // ⚠ 忠实移植的坑：PenalizeFrag 写的是 "\SysWOW64\"（首尾都有反斜杠），
                //   而真实路径 C:\Windows\SysWOW64\calc.exe 里不存在这个子串（缺前导 \）⇒ 不降权。
                //   AHK 原实现同样如此；本迁移保持等价，不偷偷"修好"。
                chkOK(wWow == 0, "③ 权重：C:\\Windows\\SysWOW64\\ 不降权（AHK 同样如此，忠实移植）");
                // 含 \SysWOW64\ 子串的路径：前缀命中 System32(+101)，降权片段也命中(-100) ⇒ 净 +1
                chkOK(wWowNested == 1, "③ 权重：含 \\SysWOW64\\ 子串的路径被降权（+101-100=+1）");
                chkOK(PathCacheBuilder.IsExcluded(@"C:\Windows\WinSxS\amd64_x\notepad.exe"),
                    "③ 硬排除：WinSxS 命中");

                // ---- ③-C1 候选集约束（守门 Z-4③）：结果必须**位于候选集内** ----
                //   契约 §4 / C1 口径 = "同名多命中 ⇒ 从候选里挑一个"（不是凭空拼路径）。
                //   用注入桩后端造**确定性**候选集：候选= A(旧) / B(新) 两条，另放一条
                //   **不在候选集内**的同名文件（创建时间最新 —— 若被纳入必然胜出）
                //   ⇒ 断言：(a) 结果 ∈ 候选集；(b) 结果 ≠ 候选集外那条；(c) 取候选集中最新者。
                candRoot = Path.Combine(Path.GetTempPath(), "rve_cand_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                string dirA = Path.Combine(candRoot, "A");
                string dirB = Path.Combine(candRoot, "B");
                string dirOut = Path.Combine(candRoot, "OUT");
                Directory.CreateDirectory(dirA);
                Directory.CreateDirectory(dirB);
                Directory.CreateDirectory(dirOut);
                string candOld = Path.Combine(dirA, "rveprobe.exe");
                string candNew = Path.Combine(dirB, "rveprobe.exe");
                string outsider = Path.Combine(dirOut, "rveprobe.exe");
                File.WriteAllText(candOld, "a", new UTF8Encoding(false));
                File.WriteAllText(candNew, "b", new UTF8Encoding(false));
                File.WriteAllText(outsider, "c", new UTF8Encoding(false));
                File.SetCreationTimeUtc(candOld, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetCreationTimeUtc(candNew, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetCreationTimeUtc(outsider, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                var candStub = new CandBackend();
                candStub.Candidates.Add(candOld);
                candStub.Candidates.Add(candNew);
                var cache5 = new PathCache(NewTempFile("cand"));
                // 5 参重载 disableEsProbe=true ⇒ 全程只用注入的后端（自检不依赖机器上的 Everything/es）
                var b5 = new PathCacheBuilder(cache5, "", s => { }, candStub, true);
                var res5 = b5.ResolveOneResult("rveprobe.exe");
                chk(res5.Path.Length > 0 && candStub.Candidates.Exists(
                        c => string.Equals(c, res5.Path, StringComparison.OrdinalIgnoreCase)),
                    "③ 解析结果**位于候选集内**（候选集 " + candStub.Candidates.Count + " 条）",
                    "结果=" + res5.Path + " tier=" + res5.Tier);
                chk(!string.Equals(res5.Path, outsider, StringComparison.OrdinalIgnoreCase),
                    "③ 候选集**外**的同名文件（创建时间最新）不得被选中", "结果=" + res5.Path);
                chk(string.Equals(res5.Path, candNew, StringComparison.OrdinalIgnoreCase),
                    "③ 候选集内按创建时间降序取第一条（旧/新两条 ⇒ 取新）", "结果=" + res5.Path);
                // ★ 建议项（守门 r2）：原来只比条数 ⇒ 补"集合内容一致"（顺序无关、大小写不敏感）
                var sc = b5.SearchCandidates("rveprobe.exe");
                bool sameSet = (sc.Count == candStub.Candidates.Count);
                if (sameSet)
                {
                    foreach (var c in candStub.Candidates)
                    {
                        if (!sc.Exists(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase)))
                        {
                            sameSet = false;
                            break;
                        }
                    }
                }
                chk(sameSet,
                    "③ SearchCandidates 与解析同源（候选集**集合内容一致**，不只比条数）",
                    "注入=" + candStub.Candidates.Count + " 条 返回=" + sc.Count + " 条 ["
                        + string.Join(" | ", sc.ToArray()) + "]");
                sb.AppendLine();

                // ============================================================
                // ④ es.exe 不可执行 ⇒ 降级 ①② 级，不崩
                // ============================================================
                string degradeLog = "<未测>";
                try
                {
                    Directory.CreateDirectory(stubDir);
                    // 桩：0 字节目录项 —— File.Exists 为 true（证明"探测到"），但 CreateProcess 必失败
                    File.WriteAllBytes(stubEs, new byte[0]);
                    chkOK(File.Exists(stubEs), "④ 桩 es.exe 已生成");

                    string tmpCache4 = NewTempFile("degrade");
                    var cache4 = new PathCache(tmpCache4);
                    var logs4 = new List<string>();
                    var b4 = new PathCacheBuilder(cache4, stubEs, s => logs4.Add(s));
                    chk(!string.IsNullOrEmpty(b4.EsPath), "④ 桩 es 被探测到（走 es 分支）", b4.EsPath);
                    chkOK(b4.EsAvailable, "④ EsAvailable=true（桩存在）");

                    var s4 = b4.Build(progs, true);
                    degradeLog = s4.ToString();
                    chk(s4.Resolved > 0,
                        "④ 假 es.exe 下构建不崩且 ①② 级仍解析成功",
                        "resolved=" + s4.Resolved + " " + s4);
                    chk(s4.Failed == progs.Count - s4.Resolved, "④ 统计自洽（failed 补齐）", s4.ToString());
                    // 降级 = 没有任何一条来自 Everything tier
                    chk(CountTier(s4, ResolveTier.Everything) == 0,
                        "④ 降级后无 Everything 来源条目（确实没走 es）",
                        "everything=" + CountTier(s4, ResolveTier.Everything));
                    foreach (var l in logs4) sb.AppendLine("     " + l);
                }
                catch (Exception ex)
                {
                    chk(false, "④ 降级测试异常", ex.GetType().Name + ": " + ex.Message);
                }
                sb.AppendLine("④ 降级构建统计：" + degradeLog);
                sb.AppendLine();

                // ⑤ es 路径缺失时的探测语义：显式给了不存在的路径仍应能回退探测
                string missing = Path.Combine(stubDir, "no_such_es.exe");
                string probe = PathCacheBuilder.ResolveEsPath(missing);
                chk(probe == "" || File.Exists(probe),
                    "⑤ ResolveEsPath 永不返回不存在的路径", "返回=" + (probe == "" ? "<空>" : probe));
                chkOK(PathCacheBuilder.ResolveEsPath(null) == realEs || PathCacheBuilder.ResolveEsPath(null) == "",
                    "⑤ 自动探测结果稳定（可复现）");
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("  FAIL 自检整体异常: " + ex);
            }
            finally
            {
                // 清理：临时缓存 + 桩目录
                foreach (var f in TempFiles)
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                    try { if (File.Exists(f + ".tmp")) File.Delete(f + ".tmp"); } catch { }
                }
                try { if (Directory.Exists(stubDir)) Directory.Delete(stubDir, true); } catch { }
                try { if (candRoot != "" && Directory.Exists(candRoot)) Directory.Delete(candRoot, true); } catch { }
            }

            sb.AppendLine();
            sb.AppendLine("结果：PASS=" + pass + " FAIL=" + fail);
            sb.AppendLine(fail == 0 ? "全部通过" : "存在失败项");
            sb.AppendLine("临时缓存文件已清理（真实 cache\\paths.txt 未被触碰）");

            try
            {
                string dir = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));
            }
            catch { }

            // ★ 主程序是 WinExe（GUI 子系统），由 Start-Process 从父进程启动时**没有附加控制台**，
            //   此时写 Console.OutputEncoding / Console.WriteLine 会抛 IOException
            //   （实测：exe 跑完自检后带 0xE0434352 CLR20r3 System.IO.IOException 崩掉）。
            //   ⇒ 自检结果只以文件为准；控制台输出仅在真有控制台时尽力而为。
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine(sb.ToString());
            }
            catch
            {
                // 无控制台（双击/Start-Process/托盘拉起）—— 正常情况，不是错误
            }
            return fail == 0 ? 0 : 1;
        }

        /// <summary>
        /// 搜索后端桩（自检 ③-C1 用）：只返回**预设候选集** —— 用来断言
        /// "解析结果位于候选集内"（候选集外的同名文件绝不能被选中）。
        /// </summary>
        private sealed class CandBackend : ISearchBackend
        {
            public readonly List<string> Candidates = new List<string>();
            public readonly List<string> Queries = new List<string>();

            public string Name { get { return "stub-cand"; } }
            public bool Available { get { return true; } }

            public List<string> Search(string name, int max)
            {
                Queries.Add(name ?? "");
                var r = new List<string>();
                if ((name ?? "").IndexOf("rveprobe", StringComparison.OrdinalIgnoreCase) >= 0)
                    r.AddRange(Candidates);
                return r;
            }
        }

        private static int CountTier(BuildStats s, ResolveTier t)
        {
            int n = 0;
            foreach (var r in s.ResolvedList) if (r.Tier == t) n++;
            return n;
        }

        private static List<KeyValuePair<string, string>> Snapshot(PathCache c)
        {
            // PathCache 没暴露枚举接口 ⇒ 从落盘文件读回来（顺带验证落盘格式）
            var list = new List<KeyValuePair<string, string>>();
            try
            {
                string path = GetCachePath(c);
                if (path == null || !File.Exists(path)) return list;
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    list.Add(new KeyValuePair<string, string>(
                        line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
                }
            }
            catch { }
            return list;
        }

        /// <summary>反射拿 PathCache 的私有 _path（PathCache 无公开的 path 属性）。</summary>
        private static string GetCachePath(PathCache c)
        {
            try
            {
                var f = typeof(PathCache).GetField("_path",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f != null) return (string)f.GetValue(c);
            }
            catch { }
            return null;
        }

        private static string NewTempFile(string tag)
        {
            string f = Path.Combine(Path.GetTempPath(),
                "rve_pathcache_selftest_" + tag + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
            TempFiles.Add(f);
            return f;
        }
    }
}
