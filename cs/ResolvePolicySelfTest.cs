using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 解析策略自检（AC-5 判据）—— 三个触发时机 + 后端降级链 + 三态 + 超时 + 现查回写。
    ///
    /// 用法：RunEverCs.exe --selftest-resolve &lt;root&gt; &lt;out.txt&gt;
    ///
    /// ★ 纪律（照 `PathCacheBuilderSelfTest.cs:20-21`）：**只写临时缓存文件**
    ///   （`%TEMP%\rve_resolve_*`），跑完删除；真实 `cache\paths.txt` 一个字节都不碰。
    /// ★ 后端用**桩**（`StubBackend`）注入，es 显式禁用 ⇒ 用例确定性、不依赖本机 Everything。
    /// ★ C4：时机② 的用例在 `hideMissing=1` 下断言"新条目保存后**立即可见**"。
    /// </summary>
    public static class ResolvePolicySelfTest
    {
        /// <summary>可注入的搜索后端桩（记录查询、可延时、可置不可用）。</summary>
        private class StubBackend : ISearchBackend
        {
            public string Name { get { return "stub-ev"; } }
            public bool Available { get; set; }

            public readonly Dictionary<string, string> Map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public readonly List<string> Queries = new List<string>();

            /// <summary>每次查询的人为延时（ms）—— 用于超时用例。</summary>
            public int DelayMs;

            public StubBackend() { Available = true; }

            public List<string> Search(string name, int max)
            {
                Queries.Add(name ?? "");
                if (DelayMs > 0) System.Threading.Thread.Sleep(DelayMs);
                var r = new List<string>();
                string p;
                if (Map.TryGetValue(name ?? "", out p) && !string.IsNullOrEmpty(p)) r.Add(p);
                return r;
            }
        }

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

            var log = new List<string>();
            Action<string> lg = s => log.Add(s);

            string tmpRoot = Path.Combine(Path.GetTempPath(), "rve_resolve_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            string tempCache = Path.Combine(tmpRoot, "paths.txt");

            try
            {
                sb.AppendLine("=== 解析策略自检（时机①/②/③ + 后端降级 + 三态 + 超时）===");
                sb.AppendLine("root = " + root);
                sb.AppendLine("临时工作目录 = " + tmpRoot);
                sb.AppendLine();

                // ---------------- 夹具 ----------------
                Directory.CreateDirectory(Path.Combine(tmpRoot, "config"));
                Directory.CreateDirectory(Path.Combine(tmpRoot, "apps"));
                string fakeExe = Path.Combine(tmpRoot, "apps", "rveprobe.exe");
                File.WriteAllText(fakeExe, "stub", new UTF8Encoding(false));

                string conf =
                    "# 自检夹具（临时）" + Environment.NewLine +
                    "! 固定  always" + Environment.NewLine +
                    "   探测程序      _P  | run rveprobe.exe" + Environment.NewLine +
                    "   系统程序      _C  | run cmd.exe" + Environment.NewLine +
                    "   查不到的程序  _X  | run notexist-rve.exe" + Environment.NewLine;
                File.WriteAllText(Path.Combine(tmpRoot, "config", "menus.conf"), conf, new UTF8Encoding(false));

                var parser = new ConfParser(Path.Combine(tmpRoot, "config", "menus.conf"));
                parser.Parse();
                var groups = parser.Groups;
                sb.AppendLine("夹具：分组数=" + groups.Count + " 程序名=" +
                    string.Join(",", new List<string>(parser.CollectPrograms()).ToArray()));
                ok(groups.Count == 1 && groups[0].Items.Count == 3, "夹具加载 1 组 3 条目");

                var cache = new PathCache(tempCache);
                var stub = new StubBackend();
                stub.Map["rveprobe.exe"] = fakeExe;
                var optStub = new ResolveOptions { Backend = stub, DisableEs = true };

                // ============================================================
                // ① C4：hideMissing=1 下"新条目保存后立即可见"
                // ============================================================
                sb.AppendLine();
                sb.AppendLine("--- 时机②：配置保存后增量解析（hideMissing=1 口径）---");

                var ctxBefore = new MatchContext();
                PathResolvePolicy.InjectEntryResolved(ctxBefore, groups, cache, true, lg);
                int missingBefore = ctxBefore.MissingItemIndex.Count;
                chk(missingBefore == 2 && !ctxBefore.IsEntryMissing(1),
                    "解析前：缓存里没有的 2 条被判缺件（cmd.exe 经 PATH 命中 ⇒ 保留）",
                    "缺件数=" + missingBefore);

                var dBefore = MatchEngine.Decide(ctxBefore, groups);
                chk(dBefore.ItemCount() == 1,
                    "解析前：hideMissing=1 ⇒ 菜单只剩 1 条可见（新条目被按缓存隐藏 = 死角）",
                    "可见条目=" + dBefore.ItemCount());

                var st1 = PathResolvePolicy.IncrementalAfterSave(tmpRoot, cache, lg, optStub);
                sb.AppendLine("  增量解析统计：" + st1);
                chk(st1.Resolved == 2 && st1.Failed == 1,
                    "增量解析：新增名被解析（resolved=2），解析不到的名字进 FailedList（failed=1）",
                    st1.ToString());
                chk(cache.Get("rveprobe.exe") == fakeExe,
                    "增量解析：新条目写入缓存（rveprobe.exe=" + cache.Get("rveprobe.exe") + "）", "");
                chk(st1.FailedList.Contains("notexist-rve.exe"),
                    "增量解析：失败清单含 notexist-rve.exe（下次重试的输入）", "");

                var ctxAfter = new MatchContext();
                PathResolvePolicy.InjectEntryResolved(ctxAfter, groups, cache, true, lg);
                var dAfter = MatchEngine.Decide(ctxAfter, groups);
                chk(ctxAfter.MissingItemIndex.Count == 1 && dAfter.ItemCount() == 2,
                    "★ C4 判据：解析后 **hideMissing=1 下新条目立即可见**（可见 1 → 2）",
                    "缺件=" + ctxAfter.MissingItemIndex.Count + " 可见=" + dAfter.ItemCount());

                var st2 = PathResolvePolicy.IncrementalAfterSave(tmpRoot, cache, lg, optStub);
                sb.AppendLine("  二次增量统计：" + st2);
                chk(st2.Skipped == 2 && st2.Resolved == 0 && st2.Failed == 1,
                    "增量语义：已成功条目 skipped（=2），只重试上次失败名（failed=1）",
                    st2.ToString());

                // ============================================================
                // ① 时机①：开机后台全盘扫（force=true）
                // ============================================================
                sb.AppendLine();
                sb.AppendLine("--- 时机①：开机全盘扫（force=true）---");
                string bootCache = Path.Combine(tmpRoot, "paths-boot.txt");
                var cacheBoot = new PathCache(bootCache);
                var stBoot = PathResolvePolicy.BootSweep(tmpRoot, cacheBoot, lg, optStub);
                sb.AppendLine("  全盘扫统计：" + stBoot);
                chk(stBoot.Resolved == 2 && stBoot.Skipped == 0,
                    "时机① 全量重建（resolved=2 skipped=0）", stBoot.ToString());

                // ============================================================
                // ③ 时机③：执行期现查（软件移位）—— 命中回写 / 未命中报错 / 三态
                // ============================================================
                sb.AppendLine();
                sb.AppendLine("--- 时机③：执行期现查（软件移位兜底）---");

                // ① 缓存命中且文件在 ⇒ 不查后端（Backend=cache）
                var cache3 = new PathCache(Path.Combine(tmpRoot, "paths-3.txt"));
                string cmdPath = PathCache.ResolveProgram("cmd.exe");
                cache3.Set("cmd.exe", cmdPath);
                cache3.Save();
                var stubQuiet = new StubBackend();
                var oHit = PathResolvePolicy.ResolveOnDemand(tmpRoot, "cmd.exe", cache3, lg,
                    new ResolveOptions { Backend = stubQuiet, DisableEs = true });
                chk(oHit.State == ResolveState.Ok && oHit.Backend == "cache" && stubQuiet.Queries.Count == 0,
                    "三态 Ok：缓存命中且文件在 ⇒ 直接命中，**不查后端**",
                    oHit.ToString() + " queries=" + stubQuiet.Queries.Count);

                // ② 移位：缓存指向已删文件 ⇒ 现查命中 ⇒ 启动路径 + 回写缓存
                cache3.Set("rveprobe.exe", Path.Combine(tmpRoot, "apps", "gone", "rveprobe.exe"));
                cache3.Save();
                var stubMove = new StubBackend();
                stubMove.Map["rveprobe.exe"] = fakeExe;
                var oMove = PathResolvePolicy.ResolveOnDemand(tmpRoot, "rveprobe.exe", cache3, lg,
                    new ResolveOptions { Backend = stubMove, DisableEs = true });
                sb.AppendLine("  移位现查结果：" + oMove);
                chk(oMove.State == ResolveState.Ok && oMove.Path == fakeExe && oMove.CacheWriteBack,
                    "移位：缓存失效 ⇒ 现查命中 + 标记回写", oMove.ToString());
                chk(cache3.Get("rveprobe.exe") == fakeExe,
                    "移位：**内存缓存**已更新为现查结果", "");
                bool onDisk = false;
                try
                {
                    string disk = File.ReadAllText(Path.Combine(tmpRoot, "paths-3.txt"), Encoding.UTF8);
                    onDisk = disk.IndexOf(fakeExe, StringComparison.OrdinalIgnoreCase) >= 0;
                }
                catch { }
                chk(onDisk, "移位：**cache 文件已落盘**含现查结果（原子 Save）", "");

                // ③ 未命中 ⇒ Missing（明确错误码，不是静默失败）
                var stubEmpty = new StubBackend();
                var oMiss = PathResolvePolicy.ResolveOnDemand(tmpRoot, "notexist-rve.exe", cache3, lg,
                    new ResolveOptions { Backend = stubEmpty, DisableEs = true });
                chk(oMiss.State == ResolveState.Missing && oMiss.ErrorCode == "not-found",
                    "三态 Missing：后端可用但查不到 ⇒ Missing + 错误码 not-found", oMiss.ToString());

                // ④ 后端不可用 ⇒ Unknown（不是"缺失"）
                var stubDown = new StubBackend();
                stubDown.Available = false;
                var oUnknown = PathResolvePolicy.ResolveOnDemand(tmpRoot, "notexist-rve.exe", cache3, lg,
                    new ResolveOptions { Backend = stubDown, DisableEs = true });
                chk(oUnknown.State == ResolveState.Unknown && oUnknown.ErrorCode == "backend-unavailable",
                    "后端降级链：EV/es 都不可用 ⇒ Unknown + backend-unavailable（**不得误报 Missing**）",
                    oUnknown.ToString());

                // ⑤ 超时 ⇒ Unknown + timeout，且不悬挂
                var stubSlow = new StubBackend();
                stubSlow.DelayMs = 3000;
                stubSlow.Map["rveprobe.exe"] = fakeExe;
                cache3.Set("rveprobe.exe", Path.Combine(tmpRoot, "apps", "gone", "rveprobe.exe"));
                cache3.Save();
                var swT = System.Diagnostics.Stopwatch.StartNew();
                var oTimeout = PathResolvePolicy.ResolveOnDemand(tmpRoot, "rveprobe.exe", cache3, lg,
                    new ResolveOptions { Backend = stubSlow, DisableEs = true, TimeoutMs = 300 });
                swT.Stop();
                chk(oTimeout.State == ResolveState.Unknown && oTimeout.ErrorCode == "timeout"
                    && swT.ElapsedMilliseconds < 1500,
                    "超时路径：现查超时 ⇒ Unknown + timeout，且调用方未被悬挂（实测 "
                    + swT.ElapsedMilliseconds + "ms < 1500ms）",
                    oTimeout.ToString());

                // ⑥ 单级降级：**EV 缺失 / es 在**（契约 §8 明文"EV 缺失 / es 缺失时各有断言"；
                //    es 缺失侧由 `--selftest-pathcache` ④ 桩 es 覆盖，此处补 EV 缺失侧）
                //    ★ 判据：只降**一级**，不是整体不可用 ⇒ 结果是 Missing 而不是 Unknown。
                string stubEsDir = Path.Combine(tmpRoot, "es-stub");
                Directory.CreateDirectory(stubEsDir);
                string stubEs = Path.Combine(stubEsDir, "es.exe");
                File.WriteAllBytes(stubEs, new byte[0]);   // 存在即"这一级在"（可执行性不参与降级判定）
                var stubEvDown = new StubBackend();
                stubEvDown.Available = false;
                var bEsUp = new PathCacheBuilder(cache3, stubEs, lg, stubEvDown, false);
                chk(bEsUp.EsAvailable && bEsUp.SearchBackend != null && !bEsUp.SearchBackend.Available
                    && bEsUp.SearchAvailable,
                    "单级降级：EV 不可用但 es 在 ⇒ 搜索级仍可用（SearchAvailable=true，不整体降级）",
                    "esAvailable=" + bEsUp.EsAvailable + " evAvailable="
                    + (bEsUp.SearchBackend != null ? bEsUp.SearchBackend.Available.ToString() : "<null>")
                    + " searchAvailable=" + bEsUp.SearchAvailable);
                cache3.Set("rveprobe.exe", Path.Combine(tmpRoot, "apps", "gone", "rveprobe.exe"));
                cache3.Save();
                var oEvDownEsUp = PathResolvePolicy.ResolveOnDemand(tmpRoot, "notexist-rve.exe", cache3, lg,
                    new ResolveOptions { Backend = stubEvDown, EsPathOverride = stubEs, DisableEs = false });
                chk(oEvDownEsUp.State == ResolveState.Missing && oEvDownEsUp.ErrorCode == "not-found",
                    "单级降级：EV 缺失 / es 在 ⇒ 降到 es 级判 Missing（**不得误报 Unknown/backend-unavailable**）",
                    oEvDownEsUp.ToString());

                // ⑦ 三态齐备
                ok(oHit.State == ResolveState.Ok && oMiss.State == ResolveState.Missing
                    && oUnknown.State == ResolveState.Unknown,
                    "三态齐备断言：Ok / Missing / Unknown 三种结果都被覆盖");

                // ---------------- 日志取证 ----------------
                sb.AppendLine();
                sb.AppendLine("--- 日志取证（节选，末 12 行）---");
                int from = Math.Max(0, log.Count - 12);
                for (int i = from; i < log.Count; i++) sb.AppendLine("  [log] " + log[i]);
            }
            catch (Exception ex)
            {
                fail++;
                sb.AppendLine("  FAIL 异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally
            {
                try { if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, true); } catch { }
            }

            sb.AppendLine();
            sb.AppendLine("结果：PASS=" + pass + " FAIL=" + fail);
            sb.AppendLine(fail == 0 ? "全部通过" : "有失败");
            sb.AppendLine("临时缓存文件已清理（真实 cache\\paths.txt 未被触碰）");
            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return fail == 0 ? 0 : 1;
        }
    }
}
