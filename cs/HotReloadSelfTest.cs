using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 段 1.7 自检：**配置级热加载**（`menus.conf` 整体换代 + `auto.conf` 全键实时 + `waitWhenShown` 实时化）。
    ///
    /// 用法：RunEverCs.exe --selftest-hotreload &lt;root&gt; &lt;out.txt&gt;
    ///
    /// ★ 全部用例跑在 `%TEMP%\runever-hr-*`（自建自清），**不读不写**用户 `config\`。
    /// ★ 覆盖（对照任务表 S3 的 8 条）：
    ///   C1 未变 ⇒ 0 解析 / 0 读盘（**计数器**行为级证据，不是注释）
    ///   C2 变更 ⇒ 生效（menus 换代可见；auto.conf 新值可见）
    ///   C3 外部写（**独立进程**改文件）也触发
    ///   C4 H1 下标平移（最强形态：旧下标位置=存在的项、新下标位置=缺件项；含**反例可判别**断言）
    ///   C5 H2 对象同一性（真换代 + 注入/判定同快照）
    ///   C6 H3 跨层一致性（判定 → 渲染 IdMap → 派发同一引用）
    ///   C7 难点③ 四态（缺文件 / 半截-抛异常 / 语法错 / **解析成功但 0 组**⇒保留旧表引用，条目数不变）
    ///   C7b **同节拍边界**（`D-2w` 显式用例化：等长内容 + 还原 mtime ⇒ 判据判"未变"，
    ///        属**已知边界**，断言当前行为、**不阻断**交付；只检验"变更能否被感知"的断言
    ///        已由 C1/C2/C3/C8/C9 在**显式推进 mtime** 的夹具下覆盖）
    ///   C8 难点④ `hotkey` 不自动重注册（结构级 + 文档口径证据）
    ///   C9 难点⑥ `enabled=0` 不被短路
    ///   C10 I-5 开销实测（沿用 ConfWriteSelfTest.cs:184-201 口径：N=500、µs/次、守卫
    ///       `fastUs < 1000 && fastUs < slowMs*1000`，另补 p50/p95/max）
    /// </summary>
    public static class HotReloadSelfTest
    {
        private static StringBuilder _sb;
        private static int _fail;
        private static int _pass;
        private static string _root;

        // 夹具常量：两个"本机存在"的假程序（写进缓存 + 真实建文件）+ 一个必然查不到的名字
        private const string KeepA = "keepa-rve.exe";
        private const string KeepB = "keepb-rve.exe";
        private const string Gone = "gone-rve-notexist-9x7.exe";

        private const string T_A = "存在项A";
        private const string T_B = "存在项B";
        private const string T_MISS = "缺件项";

        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;      // 与 --selftest-confwrite / --selftest-capture 同口径
            string root = args[1];
            string outFile = args[2];
            if (!Path.IsPathRooted(outFile)) outFile = Path.Combine(root, outFile);
            _root = root;

            _sb = new StringBuilder();
            _fail = 0;
            _pass = 0;
            _sb.AppendLine("=== RunEver 段1.7 自检（配置级热加载：menus.conf 换代 / auto.conf 全键 / waitWhenShown 实时 / I-5 开销）===");
            _sb.AppendLine("root = " + root + "（用例一律跑在临时 root 上；仅 C8 读 docs 文件核对口径）");

            CaseNoChangeZeroWork();
            CaseExternalWriteAndIndexShift();
            CaseRaceAndParseErrors();
            CaseSameTickBoundary();
            CaseAutoConfLive();
            CaseHotkeyContract();
            CaseOverhead();

            _sb.AppendLine(_fail == 0
                ? "=== 段1.7 自检全部通过（" + _pass + " 项）==="
                : "=== 段1.7 自检失败 " + _fail + " 项（通过 " + _pass + "）===");

            try
            {
                string dir = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, _sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return _fail == 0 ? 0 : 1;
        }

        // ============================================================
        // 断言辅助
        // ============================================================
        private static void Check(string title, bool ok, string detail)
        {
            if (ok) { _pass++; _sb.AppendLine("[OK]   " + title + (detail.Length == 0 ? "" : "  " + detail)); }
            else
            {
                _sb.AppendLine("[FAIL] " + title + (detail.Length == 0 ? "" : "  " + detail));
                _fail++;
            }
        }

        private static void Eq(string title, string got, string want)
        {
            Check(title, got == want, got == want ? "" : "期望=[" + want + "] 实得=[" + got + "]");
        }

        private static string MakeSandbox(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "runever-hr-" + tag + "-" + DateTime.Now.Ticks);
            Directory.CreateDirectory(Path.Combine(dir, "config", "functions"));
            Directory.CreateDirectory(Path.Combine(dir, "state"));
            Directory.CreateDirectory(Path.Combine(dir, "apps"));
            return dir;
        }

        /// <summary>用完即清（`runever-hr-*` 不在 D-2y 的 `runever-cw-*` 清理范围内，自己负责）。</summary>
        private static void DropSandbox(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { _sb.AppendLine("  [warn] 沙箱清理失败: " + ex.Message); }
        }

        private static string ConfPath(string root) { return Path.Combine(root, "config", "menus.conf"); }
        private static string AutoPath(string root) { return Path.Combine(root, "config", "auto.conf"); }

        // ---------------- 夹具 ----------------

        /// <summary>旧表：存在项A(展平下标 0) + 缺件项(展平下标 1)</summary>
        private static string MenusOldText()
        {
            return "# 段1.7 自检夹具 menus.conf（旧）" + Environment.NewLine
                 + "! 固定  always" + Environment.NewLine
                 + "   " + T_A + "  _A | run " + KeepA + Environment.NewLine
                 + "   " + T_MISS + "   _X | run " + Gone + Environment.NewLine;
        }

        /// <summary>新表：在缺件项**前面插入**一个本机存在的项 ⇒ 缺件项下标 1 → 2</summary>
        private static string MenusNewText()
        {
            return "# 段1.7 自检夹具 menus.conf（新）" + Environment.NewLine
                 + "! 固定  always" + Environment.NewLine
                 + "   " + T_A + "  _A | run " + KeepA + Environment.NewLine
                 + "   " + T_B + "  _B | run " + KeepB + Environment.NewLine
                 + "   " + T_MISS + "   _X | run " + Gone + Environment.NewLine;
        }

        private static string AutoConfText(bool enabled, string hideMissing, string waitWhenShown)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# 段1.7 自检夹具 auto.conf");
            sb.AppendLine("enabled=" + (enabled ? "1" : "0"));
            sb.AppendLine("minLen=4");
            sb.AppendLine("cacheSeconds=300");
            sb.AppendLine("showByDefault=0");
            sb.AppendLine("waitWhenShown=" + waitWhenShown);
            sb.AppendLine("log=1");
            sb.AppendLine("hotkey=`");
            sb.AppendLine("hideMissing=" + hideMissing);
            return sb.ToString();
        }

        /// <summary>写夹具（**进程内写**；"外部写"另见 ExternalWrite）。</summary>
        private static void WriteFile(string path, string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// <summary>
        /// 真·外部写：本进程只把内容落到中转文件，**由独立进程 `cmd.exe`** 完成对目标文件的覆盖写。
        /// 判据 = 写者进程不同（AC-C6④）。
        /// </summary>
        private static bool ExternalWrite(string target, string content, out string detail)
        {
            detail = "";
            try
            {
                string tmp = target + ".extsrc";
                File.WriteAllText(tmp, content, new UTF8Encoding(false));
                var psi = new ProcessStartInfo("cmd.exe",
                    "/c copy /y \"" + tmp + "\" \"" + target + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                var p = Process.Start(psi);
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                p.WaitForExit();
                try { File.Delete(tmp); } catch { }
                detail = "writer=cmd.exe pid=" + p.Id + " exit=" + p.ExitCode;
                if (p.ExitCode != 0) detail += " out=[" + so.Trim() + "] err=[" + se.Trim() + "]";
                if (p.ExitCode == 0)
                    detail += " ; " + ForceNewerMtime(target);
                return p.ExitCode == 0;
            }
            catch (Exception ex)
            {
                detail = "外部写异常: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// ★ G-1 夹具确定化：把外部写后的目标文件 mtime **显式推进**到严格大于写前值的时刻。
        ///
        /// 为什么必须做：`cmd copy` / `File.Copy` 会**继承源文件 mtime**（实测：`copy` 后目标戳
        /// == `.extsrc` 的戳，而不是写入时刻）。⇒ "等长内容 + 两版 mtime 落在同一系统时钟节拍"
        /// 时，`RefreshConfIfChanged` / `MenuConfWatcher` 的快路径 `(ticks== && len==)`
        /// 会把一次真实变更判成"未变"，断言随机变红（守门 T13 实测 8/30 轮）。
        /// 这里把"变更一定被判成变"钉死，使断言只检验**变更能否被感知**，不夹带同节拍竞态。
        /// 注意：只改 mtime，**写者仍是独立进程 cmd.exe**（AC-C6④ 的"写者进程不同"不受影响）。
        /// "同节拍 ⇒ 判为未变"这一**已知边界**不因此被掩盖 —— 它由 C6 的显式用例
        /// （`CaseSameTickBoundary`）单独断言。
        /// </summary>
        private static string ForceNewerMtime(string path)
        {
            try
            {
                long before = new FileInfo(path).LastWriteTimeUtc.Ticks;
                DateTime t = new DateTime(before, DateTimeKind.Utc).AddSeconds(1);
                if (t <= DateTime.UtcNow) t = DateTime.UtcNow.AddMilliseconds(5);
                File.SetLastWriteTimeUtc(path, t);
                long after = new FileInfo(path).LastWriteTimeUtc.Ticks;
                return "mtime " + before + "→" + after + "(更晚=" + (after > before) + ")";
            }
            catch (Exception ex)
            {
                return "mtime 推进失败: " + ex.Message;
            }
        }

        /// <summary>
        /// ★ G-1 同节拍用例的夹具：与 <paramref name="content"/> **等长但内容不同**的文本
        /// （翻转第一处 `1`↔`0`；没有 `1`/`0` 就翻转第一个字符；退无可退时原地返回）。
        /// </summary>
        private static string SameLengthText(string content)
        {
            var a = content.ToCharArray();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == '1') { a[i] = '0'; return new string(a); }
                if (a[i] == '0') { a[i] = '1'; return new string(a); }
            }
            if (a.Length > 0) a[0] = (a[0] == 'A') ? 'B' : 'A';
            return new string(a);
        }

        private static DateTime TicksToUtc(long ticks)
        {
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        /// <summary>建立"存在项"现场：真实文件 + 路径缓存（缺件项**故意不进缓存**）。</summary>
        private static PathCache MakeCache(string root, out string detail)
        {
            string apps = Path.Combine(root, "apps");
            string fa = Path.Combine(apps, KeepA);
            string fb = Path.Combine(apps, KeepB);
            File.WriteAllText(fa, "stub", new UTF8Encoding(false));
            File.WriteAllText(fb, "stub", new UTF8Encoding(false));
            string cacheFile = Path.Combine(root, "state", "paths.txt");
            File.WriteAllLines(cacheFile, new[]
            {
                KeepA + "=" + fa,
                KeepB + "=" + fb
            }, new UTF8Encoding(false));
            detail = "缓存=" + cacheFile + "（" + KeepA + "/" + KeepB + " 指向真实文件；" + Gone + " 不在缓存）";
            return new PathCache(cacheFile);
        }

        // ---------------- 标题口径辅助（一律按 Title 比对，不按下标）----------------

        private static List<string> AllTitles(IList<MenuGroup> groups)
        {
            var r = new List<string>();
            if (groups != null)
                foreach (var g in groups)
                    if (g != null && g.Items != null)
                        foreach (var it in g.Items) r.Add(it.Title);
            return r;
        }

        private static List<string> VisibleTitles(MenuDecision d)
        {
            return AllTitles(d == null ? null : d.Flat());
        }

        /// <summary>隐藏清单 = 原表标题 − 判定后可见标题（按 Title 比对，不按下标）。</summary>
        private static List<string> HiddenTitles(IList<MenuGroup> all, MenuDecision d)
        {
            var vis = VisibleTitles(d);
            var r = new List<string>();
            foreach (var t in AllTitles(all))
                if (!Contains(vis, t)) r.Add(t);
            return r;
        }

        private static bool Contains(List<string> list, string s)
        {
            return IndexOf(list, s) >= 0;
        }

        private static int IndexOf(List<string> list, string s)
        {
            for (int i = 0; i < list.Count; i++)
                if (string.Equals(list[i], s, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static string Join(List<string> list)
        {
            return "[" + string.Join("|", list.ToArray()) + "]";
        }

        private static bool SameSet(List<string> a, params string[] b)
        {
            if (a.Count != b.Length) return false;
            foreach (var s in b) if (!Contains(a, s)) return false;
            return true;
        }

        private static MenuItem FindItem(IList<MenuGroup> groups, string title)
        {
            foreach (var g in groups)
                if (g != null && g.Items != null)
                    foreach (var it in g.Items)
                        if (string.Equals(it.Title, title, StringComparison.Ordinal)) return it;
            return null;
        }

        private static bool AllGroupsByRef(IList<MenuGroup> subset, IList<MenuGroup> all)
        {
            foreach (var g in subset)
            {
                bool found = false;
                foreach (var o in all) if (ReferenceEquals(o, g)) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        private static bool AllItemsByRef(ICollection<MenuItem> subset, IList<MenuGroup> all)
        {
            var flat = new List<MenuItem>();
            foreach (var g in all)
                if (g != null && g.Items != null) flat.AddRange(g.Items);
            foreach (var it in subset)
            {
                bool found = false;
                foreach (var o in flat) if (ReferenceEquals(o, it)) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        // ============================================================
        // C1：未变 ⇒ 0 解析 / 0 读盘（AC-C6①）
        // ============================================================
        private static void CaseNoChangeZeroWork()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- C1. 未变 ⇒ 0 解析 / 0 读盘（计数器证据；排除「根本没接上」）---");
            string root = MakeSandbox("nochange");
            try
            {
                WriteFile(ConfPath(root), MenusOldText());
                WriteFile(AutoPath(root), AutoConfText(true, "1", "1"));

                var am = new AutoMatcher(root, null);
                am.Load(true);                                   // 托盘启动态（含一次 ReadConf）

                var w = new MenuConfWatcher(ConfPath(root), null);
                var p0 = new ConfParser(ConfPath(root));
                p0.Parse();
                w.AcceptBaseline(p0);                            // 启动解析后登记基线

                am.LiveShowConf();                               // 热身：建立 auto.conf 的版本戳
                int parse0 = w.ParseCount;
                int read0 = am.ConfReadCount;

                const int K = 200;
                for (int i = 0; i < K; i++)                      // 热键期检查 ×K（未变）
                {
                    ConfParser fresh;
                    w.TryReloadIfChanged(out fresh);
                    am.LiveShowConf();
                }

                Eq("C1 menus.conf 未变 ⇒ 解析次数 +0（K=" + K + "）",
                    (w.ParseCount - parse0).ToString(), "0");
                Eq("C1 auto.conf 未变 ⇒ 读盘次数 +0（K=" + K + "）",
                    (am.ConfReadCount - read0).ToString(), "0");
                Check("C1 stat 确实执行过（版本戳已建立 —— 否则上面的 0 可能只是「没接上」）",
                    w.HasStamp && am.LiveStampTicks != -1,
                    "menusStamp=" + (w.HasStamp ? "已建立" : "未建立")
                    + " autoStamp=" + am.LiveStampTicks);
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] C1 用例异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally { DropSandbox(root); }
        }

        // ============================================================
        // C2/C3/C4/C5/C6：外部写 ⇒ 换代；H1 下标平移 / H2 同一性 / H3 跨层一致
        // ============================================================
        private static void CaseExternalWriteAndIndexShift()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- C2/C3. 外部写（独立进程）⇒ menus.conf 整体换代 ---");
            string root = MakeSandbox("extwrite");
            try
            {
                WriteFile(ConfPath(root), MenusOldText());
                string det;
                var cache = MakeCache(root, out det);
                _sb.AppendLine("  夹具：" + det);

                var w = new MenuConfWatcher(ConfPath(root), s => _sb.AppendLine("  [w] " + s));
                var pOld = new ConfParser(ConfPath(root));
                pOld.Parse();
                w.AcceptBaseline(pOld);
                var oldGroups = pOld.Groups;
                _sb.AppendLine("  旧表：分组=" + oldGroups.Count + " 条目=" + Join(AllTitles(oldGroups)));

                // 前置：旧表缺件下标 = {1}（唯一的那个缺件项）
                var ctxPre = new MatchContext();
                var hotPre = MenuDecisionPipeline.InjectAndDecide(ctxPre, oldGroups, cache, true, null);
                Eq("C2 前置：旧表缺件数=1", hotPre.HiddenCount.ToString(), "1");
                Check("C2 前置：旧表缺件的是「" + T_MISS + "」",
                    SameSet(HiddenTitles(oldGroups, hotPre.Decision), T_MISS),
                    "隐藏=" + Join(HiddenTitles(oldGroups, hotPre.Decision)));

                // ---- 外部写（独立进程）----
                long len0 = new FileInfo(ConfPath(root)).Length;
                long t0 = new FileInfo(ConfPath(root)).LastWriteTimeUtc.Ticks;
                bool wrote = ExternalWrite(ConfPath(root), MenusNewText(), out det);
                Check("C3 外部写成功（独立进程写者）", wrote, det);
                long len1 = new FileInfo(ConfPath(root)).Length;
                long t1 = new FileInfo(ConfPath(root)).LastWriteTimeUtc.Ticks;
                Check("C3 前置：版本戳确实变了（长度或 mtime）—— 否则下面会假绿",
                    len1 != len0 || t1 != t0,
                    "len " + len0 + "→" + len1 + " mtime " + (t1 != t0 ? "变化" : "未变"));

                // ---- 热键期检查 ⇒ 整体换代 ----
                ConfParser fresh;
                bool reloaded = w.TryReloadIfChanged(out fresh);
                Check("C3 外部写 ⇒ 热键期检查触发重解析（返回新实例）", reloaded && fresh != null, "reloaded=" + reloaded);
                Eq("C3 重解析计数 = 1", w.ParseCount.ToString(), "1");
                var newGroups = (fresh != null) ? fresh.Groups : oldGroups;
                _sb.AppendLine("  新表：分组=" + newGroups.Count + " 条目=" + Join(AllTitles(newGroups)));

                // ============================================================
                // H2 · 对象同一性（AC-C1：判定层/渲染层拿到的必须是**新**对象）
                // ============================================================
                _sb.AppendLine();
                _sb.AppendLine("--- C5. H2 对象同一性断言 ---");
                Check("H2 新旧 Groups 容器不是同一实例（真换代，不是就地改）",
                    !ReferenceEquals(oldGroups, newGroups), "");
                Check("H2 新旧首组不是同一实例", !ReferenceEquals(oldGroups[0], newGroups[0]), "");
                bool anyItemSame = false;
                foreach (var oi in oldGroups[0].Items)
                    foreach (var ni in newGroups[0].Items)
                        if (ReferenceEquals(oi, ni)) anyItemSame = true;
                Check("H2 新旧 MenuItem 无任何引用复用（整体换代，非部分替换）", !anyItemSame, "");

                // H1/H3 走生产同一条收口
                var ctxNew = new MatchContext();
                var hot = MenuDecisionPipeline.InjectAndDecide(ctxNew, newGroups, cache, true, null);
                // ★ G-4：原此处是 `InjectedWith==DecidedWith && Snapshot==newGroups` —— 单参数收口下
                //   恒真（无判别力，守门 P3 只在"故意分叉"时才能证伪）。换成有判别力的断言：
                //   · 注入层看的是**全量**快照（ItemTotal == 快照条目数）；
                //   · 判定层输出的每个条目必须**按引用**落在同一份新快照里（判定与快照同源）；
                //   · 隐藏数必须由**新**快照的下标算出（旧表口径会得 2 ⇒ 反例可判别）。
                Eq("H2 注入层消费的条目数 == 新快照条目数（快照被完整消费，未跨表分叉）",
                    hot.ItemTotal.ToString(), FlatItems(newGroups).Count.ToString());
                Eq("H2 判定产物条目数 == 新快照条目数 − 被判隐藏数（判定确实在**新**快照上跑）",
                    FlatItems(hot.Decision.Flat()).Count.ToString(),
                    (FlatItems(newGroups).Count - hot.HiddenCount).ToString());
                Check("H2 判定命中的每个条目都能在**新快照**里按引用找到（判定与快照同源）",
                    AllItemsByRef(FlatItems(hot.Decision.Flat()), newGroups),
                    "判定条目=" + Join(VisibleTitles(hot.Decision)));
                Eq("H2 反面证据：判定确实在**新快照**上跑（若拿未注入的旧表算，隐藏数会是 2 而不是 1）",
                    hot.HiddenCount.ToString(), "1");
                Check("H2 收口证据：注入与判定同实例 —— 若调用方传容器副本即分叉（守门 P3 形态），此处必红",
                    ReferenceEquals(hot.InjectedWith, hot.DecidedWith)
                    && ReferenceEquals(hot.Snapshot, newGroups),
                    "InjectedWith==DecidedWith ? " + ReferenceEquals(hot.InjectedWith, hot.DecidedWith)
                    + " ; Snapshot==newGroups ? " + ReferenceEquals(hot.Snapshot, newGroups));

                // ============================================================
                // H1 · 下标平移（**最强形态**）
                //   旧表：[存在项A(0), 缺件项(1)]        缺件下标 = 1
                //   新表：[存在项A(0), 存在项B(1), 缺件项(2)]  缺件下标 = 2
                //   ⇒ 旧下标位置(1)上是"存在的项"，新下标位置(2)上是"缺件项"
                //     若用旧下标判定，会隐藏【存在项B】且漏掉【缺件项】⇒ 两个方向都能断言
                // ============================================================
                _sb.AppendLine();
                _sb.AppendLine("--- C4. H1 下标平移断言（最强形态）---");
                Eq("H1 正确路径：缺件下标随新表平移（旧 1 → 新 2 ⇒ 缺件数仍为 1）",
                    hot.HiddenCount.ToString(), "1");
                Check("H1 正确路径：隐藏的仍是【按 Title 比对】" + T_MISS,
                    SameSet(HiddenTitles(newGroups, hot.Decision), T_MISS),
                    "隐藏=" + Join(HiddenTitles(newGroups, hot.Decision)));
                Check("H1 正确路径：新表两个「存在的项」都可见、缺件项不可见",
                    Contains(VisibleTitles(hot.Decision), T_A)
                    && Contains(VisibleTitles(hot.Decision), T_B)
                    && !Contains(VisibleTitles(hot.Decision), T_MISS),
                    "可见=" + Join(VisibleTitles(hot.Decision)));

                // ---- 反例（错位形态）：旧表算下标 + 新表消费 ⇒ 必须与本断言结果不同 ----
                var ctxBad = new MatchContext();
                PathResolvePolicy.InjectEntryResolved(ctxBad, oldGroups, cache, true, null);
                var dBad = MatchEngine.Decide(ctxBad, newGroups);
                var hiddenBad = HiddenTitles(newGroups, dBad);
                var visibleBad = VisibleTitles(dBad);
                Check("H1 反例可判别：旧下标 + 新表 ⇒ 隐藏的是【" + T_B + "】且【" + T_MISS + "】漏网",
                    SameSet(hiddenBad, T_B) && Contains(visibleBad, T_MISS) && !Contains(visibleBad, T_B),
                    "隐藏=" + Join(hiddenBad) + " 可见=" + Join(visibleBad));
                Check("H1 反例与正确结果**确实不同**（证明本断言组有判别力，不是恒真）",
                    !SameSet(hiddenBad, T_MISS) && hot.HiddenCount == 1 && ctxBad.MissingItemIndex.Count == 1,
                    "反例隐藏=" + Join(hiddenBad));

                // ============================================================
                // H3 · 跨层一致性（判定 → 图标 → 渲染(IdMap) → 派发）
                // ============================================================
                _sb.AppendLine();
                _sb.AppendLine("--- C6. H3 跨层一致性断言 ---");
                var flat = hot.Decision.Flat();
                Check("H3 decision.Flat() 的每个**条目**都 ∈ 新快照（按引用）—— 判定 → 渲染 → 派发同源",
                    AllItemsByRef(FlatItems(flat), newGroups),
                    "flat 组数=" + flat.Count + " 可见=" + Join(VisibleTitles(hot.Decision)));
                // ★ 如实标注既有语义：hideMissing=1 时 `MatchEngine.FilterMissing` 会产出**新的包装
                //   MenuGroup**（Name/Type/Keywords/Line 复制、Items 仍是快照里的原 MenuItem 引用）。
                //   这是段 1.x 就有的行为（与 MissingFilter 一致），**不是**本段引入，也不影响同源保证。
                Check("H3 如实标注：hideMissing=1 时 Flat() 的分组是 FilterMissing 新建的包装对象（Items 仍同源）",
                    flat.Count == 1 && !ReferenceEquals(flat[0], newGroups[0])
                    && AllItemsByRef(FlatItems(flat), newGroups),
                    "flat[0] 与快照[0] 同实例 ? " + ReferenceEquals(flat[0], newGroups[0]));

                // 对照组：hideMissing=0 ⇒ 判定层零重建（Flat 的分组**就是**快照分组，按引用）
                var ctxOff = new MatchContext();
                var hotOff = MenuDecisionPipeline.InjectAndDecide(ctxOff, newGroups, cache, false, null);
                Check("H3 对照：hideMissing=0 ⇒ Flat() 分组与快照分组引用相同（零重建，与 MissingFilterSelfTest:64 同口径）",
                    AllGroupsByRef(hotOff.Decision.Flat(), newGroups) && hotOff.HiddenCount == 0,
                    "缺件数=" + hotOff.HiddenCount);

                var icons = new IconResolver(cache);
                icons.ResolveAll(flat);        // 图标层就地写 IconSource（不持有 Groups ⇒ 不换对象）
                Check("H3 图标解析后 flat 的条目仍是同一批新快照对象（IconResolver 不持 Groups）",
                    AllItemsByRef(FlatItems(flat), newGroups), "");

                var built = MenuBuilder.Build(flat);
                int idB = -1;
                foreach (var kv in built.IdMap)
                    if (string.Equals(kv.Value.Title, T_B, StringComparison.Ordinal)) idB = kv.Key;
                Check("H3 built.IdMap 命中「" + T_B + "」（渲染层可点条目）", idB > 0,
                    "id=" + idB + " 可点条目=" + built.ItemCount);
                Check("H3 IdMap[id] 的 MenuItem 与**新快照**同一条目是同一引用（判定/渲染/派发同源）",
                    idB > 0 && ReferenceEquals(built.IdMap[idB], FindItem(newGroups, T_B)),
                    idB > 0 ? ("IdMap 项=" + built.IdMap[idB].Title + " 快照项=" + FindItem(newGroups, T_B).Title) : "");
                Check("H3 IdMap 里每个 MenuItem 都 ∈ 新快照条目集合（按引用）",
                    AllItemsByRef(new List<MenuItem>(built.IdMap.Values), newGroups), "");
                bool missInMenu = false;
                foreach (var kv in built.IdMap)
                    if (string.Equals(kv.Value.Title, T_MISS, StringComparison.Ordinal)) missInMenu = true;
                Check("H3 缺件项已被判定层丢弃 ⇒ 不在 IdMap（否则会派发出本不该出现的条目）",
                    !missInMenu, "IdMap 标题=" + ItemsOf(built.IdMap));
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] C2/C3/H1/H2/H3 用例异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally { DropSandbox(root); }
        }

        private static List<MenuItem> FlatItems(IList<MenuGroup> groups)
        {
            var r = new List<MenuItem>();
            foreach (var g in groups)
                if (g != null && g.Items != null) r.AddRange(g.Items);
            return r;
        }

        private static string ItemsOf(Dictionary<int, MenuItem> idmap)
        {
            var t = new List<string>();
            foreach (var kv in idmap) t.Add(kv.Value.Title);
            return Join(t);
        }

        // ============================================================
        // C7：难点③ 写盘竞态的三种坏态（缺文件 / 半截 / 语法错）⇒ 保留旧表
        // ============================================================
        private static void CaseRaceAndParseErrors()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- C7. 难点③：坏态 ⇒ 保留旧 Groups（不切换、不清空）—— 四态全覆盖 ---");
            string root = MakeSandbox("race");
            try
            {
                WriteFile(ConfPath(root), MenusNewText());      // 好表：3 条目
                var w = new MenuConfWatcher(ConfPath(root), s => _sb.AppendLine("  [w] " + s));
                var good = new ConfParser(ConfPath(root));
                good.Parse();
                w.AcceptBaseline(good);
                int goodCount = good.Groups.Count;
                int goodItems = good.Groups[0].Items.Count;
                int parseBefore = w.ParseCount;

                // ---- ① 缺文件（= tmp → delete → move 的暂缺窗口）----
                long l0 = new FileInfo(ConfPath(root)).Length;
                File.Delete(ConfPath(root));
                ConfParser f1;
                bool r1 = w.TryReloadIfChanged(out f1);
                Check("③-① 缺文件 ⇒ 不切换（返回 false、无新实例）", !r1 && f1 == null, "r=" + r1);
                Check("③-① 缺文件 ⇒ Current 仍是**同一个旧对象**（引用相等）",
                    ReferenceEquals(w.Current, good), "");
                Check("③-① 缺文件 ⇒ 旧表条目数不变（不清空：menus.conf 无默认值语义）",
                    w.Current != null && w.Current.Groups.Count == goodCount
                    && w.Current.Groups[0].Items.Count == goodItems,
                    "分组=" + (w.Current == null ? -1 : w.Current.Groups.Count)
                    + " 条目=" + (w.Current == null ? -1 : w.Current.Groups[0].Items.Count));
                Check("③-① 未因缺文件而多解析（无 ParseCount 增长）",
                    w.ParseCount == parseBefore, "parse=" + w.ParseCount);

                // ---- ② 半截文件（分组行被截断 ⇒ 解析必抛异常）----
                //   ★ G-3：本形态属"**抛异常**"路径（未知分组类型 ⇒ FormatException），
                //     与下面新增的 ③-④（Parse **不抛** 但内容退化成 0 组）是**两条不同的路径**。
                string half = "! 固定 always" + Environment.NewLine
                            + "   " + T_A + "  _A | run " + KeepA + Environment.NewLine
                            + "! 新增组 fo";                       // ← 被截断的分组行
                WriteFile(ConfPath(root), half);
                long l2 = new FileInfo(ConfPath(root)).Length;
                Check("③-② 前置：文件确实重新出现且版本戳已变",
                    File.Exists(ConfPath(root)) && l2 != l0, "len=" + l2 + "（缺文件前=" + l0 + "）");
                ConfParser f2;
                bool r2 = w.TryReloadIfChanged(out f2);
                Check("③-② 半截 ⇒ 解析失败但不切换（返回 false）", !r2 && f2 == null, "r=" + r2);
                Check("③-② Current 仍是同一个旧对象（引用相等）", ReferenceEquals(w.Current, good), "");
                Check("③-② 旧表条目数不变", w.Current.Groups[0].Items.Count == goodItems,
                    "条目=" + w.Current.Groups[0].Items.Count);

                // ---- ③ 语法错（未知分组类型 ⇒ FormatException）----
                WriteFile(ConfPath(root), "! 坏组 nosuchtype" + Environment.NewLine
                            + "   " + T_A + "  _A | run " + KeepA + Environment.NewLine);
                ConfParser f3;
                bool r3 = w.TryReloadIfChanged(out f3);
                Check("③-③ 语法错 ⇒ 解析失败但不切换（返回 false）", !r3 && f3 == null, "r=" + r3);
                Check("③-③ Current 仍是同一个旧对象（引用相等）", ReferenceEquals(w.Current, good), "");
                Check("③-③ 旧表条目数不变（绝不允许 Groups 变空表或 null）",
                    w.Current != null && w.Current.Groups.Count > 0
                    && w.Current.Groups[0].Items.Count == goodItems,
                    "条目=" + w.Current.Groups[0].Items.Count);

                int parse3 = w.ParseCount;

                // ---- ③-④ **内容退化但 Parse 不抛**（G-2/G-3：注释-only ⇒ 解析成功、Groups.Count==0）----
                //   守门 T8a 实测：该形态**不抛异常** —— 修正前的实现会"解析成功 ⇒ 整体换代"，
                //   把菜单换成**空表**（正是难点③ 要避免的"菜单没了"）。
                //   与 ③-② 的区别：③-② 走 catch（FormatException），本形态走**守卫**（组数=0 而文件非空）。
                string degraded = "# 只剩注释：用户截断/编辑到一半" + Environment.NewLine;
                long l3Before = new FileInfo(ConfPath(root)).Length;
                WriteFile(ConfPath(root), degraded);
                long l3 = new FileInfo(ConfPath(root)).Length;
                Check("③-④ 前置：注释-only 文件确实落盘且版本戳已变（长度 " + l3Before + "→" + l3 + "）",
                    File.Exists(ConfPath(root)) && l3 != l3Before, "len=" + l3 + "（写前=" + l3Before + "）");
                ConfParser f5;
                bool r5 = w.TryReloadIfChanged(out f5);
                Eq("③-④ 解析成功但语义退化（0 分组而文件非空）⇒ 不换代（返回 false）",
                    r5.ToString().ToLower(), "false");
                Check("③-④ 无新实例交出（fresh 必须为 null）", f5 == null,
                    f5 == null ? "" : "分组=" + f5.Groups.Count);
                Check("③-④ Current 仍是**同一个旧对象**（引用相等 ⇒ 菜单没被清空）",
                    ReferenceEquals(w.Current, good), "");
                Check("③-④ 旧表分组数 / 条目数都不变（绝不允许变成空表）",
                    w.Current != null && w.Current.Groups.Count == goodCount
                    && w.Current.Groups[0].Items.Count == goodItems,
                    "分组=" + (w.Current == null ? -1 : w.Current.Groups.Count)
                    + " 条目=" + (w.Current == null ? -1 : w.Current.Groups[0].Items.Count));
                Eq("③-④ 该形态确实被**重解析**过一次（走的是守卫，不是「未变」快路径）",
                    (w.ParseCount - parse3).ToString(), "1");
                _sb.AppendLine("  ③-④ 守卫日志：见上方 [w] 行（「语义退化」）");

                // ---- ③-⑤ **0 字节文件**（M-2：写盘被中途打断的典型形态）----
                //   判据必须用 `len`（不是 `text.Trim()`）：0 字节是"文件被清空/写盘中断"，
                //   放行即把菜单换成空表 —— 与 ③-④ 同属"菜单没了"，必须一并保留旧表。
                int parse4 = w.ParseCount;
                string det5;
                ExternalWrite(ConfPath(root), "", out det5);      // 独立进程写 0 字节
                long l5 = new FileInfo(ConfPath(root)).Length;
                Check("③-⑤ 前置：0 字节文件确实落盘且版本戳已变（len=" + l5 + "）",
                    File.Exists(ConfPath(root)) && l5 == 0, "len=" + l5 + " " + det5);
                ConfParser f6;
                bool r6 = w.TryReloadIfChanged(out f6);
                Eq("③-⑤ 0 字节文件 ⇒ 不换代（返回 false；不得换成空表）",
                    r6.ToString().ToLower(), "false");
                Check("③-⑤ 无新实例交出（fresh 必须为 null）", f6 == null,
                    f6 == null ? "" : "分组=" + f6.Groups.Count);
                Check("③-⑤ Current 仍是**同一个旧对象**（引用相等 ⇒ 旧表未被换成空表）",
                    ReferenceEquals(w.Current, good), "");
                Check("③-⑤ 旧表分组数 / 条目数都不变（拒绝 0 组空表）",
                    w.Current != null && w.Current.Groups.Count == goodCount
                    && w.Current.Groups[0].Items.Count == goodItems,
                    "分组=" + (w.Current == null ? -1 : w.Current.Groups.Count)
                    + " 条目=" + (w.Current == null ? -1 : w.Current.Groups[0].Items.Count));
                Eq("③-⑤ 该形态确实被**重解析过一次**（走的是守卫，不是「未变」快路径）",
                    (w.ParseCount - parse4).ToString(), "1");

                // ---- ③-⑥ **全空白文件**（len=9："   \r\n  \r\n"；M-2 另一形态）----
                int parse5 = w.ParseCount;
                string det6;
                ExternalWrite(ConfPath(root), "   " + Environment.NewLine + "  " + Environment.NewLine, out det6);
                long l6 = new FileInfo(ConfPath(root)).Length;
                Check("③-⑥ 前置：全空白文件确实落盘且长度 > 0（len=" + l6 + "）",
                    File.Exists(ConfPath(root)) && l6 > 0, "len=" + l6 + " " + det6);
                ConfParser f7;
                bool r7 = w.TryReloadIfChanged(out f7);
                Eq("③-⑥ 全空白文件 ⇒ 不换代（返回 false；`len>0` 走守卫，不看 trim）",
                    r7.ToString().ToLower(), "false");
                Check("③-⑥ 无新实例交出（fresh 必须为 null）", f7 == null,
                    f7 == null ? "" : "分组=" + f7.Groups.Count);
                Check("③-⑥ Current 仍是**同一个旧对象**（引用相等 ⇒ 旧表未被换成空表）",
                    ReferenceEquals(w.Current, good), "");
                Check("③-⑥ 旧表分组数 / 条目数都不变（拒绝 0 组空表）",
                    w.Current != null && w.Current.Groups.Count == goodCount
                    && w.Current.Groups[0].Items.Count == goodItems,
                    "分组=" + (w.Current == null ? -1 : w.Current.Groups.Count)
                    + " 条目=" + (w.Current == null ? -1 : w.Current.Groups[0].Items.Count));
                Eq("③-⑥ 该形态确实被**重解析过一次**（走的是守卫，不是「未变」快路径）",
                    (w.ParseCount - parse5).ToString(), "1");
                _sb.AppendLine("  ③-⑤/③-⑥ 守卫日志：见上方 [w] 行（「语义退化 … 0 字节文件 / 文件非空」）");

                // ---- ④ 自愈：坏文件被修好 ⇒ 下一次热键即换代 ----
                string det;
                ExternalWrite(ConfPath(root), MenusOldText(), out det);
                ConfParser f4;
                bool r4 = w.TryReloadIfChanged(out f4);
                Check("③-④ 坏文件修好 ⇒ 自动自愈（重解析成功并换代）",
                    r4 && f4 != null && f4.Groups[0].Items.Count == 2,
                    "r=" + r4 + " 条目=" + (f4 == null ? -1 : f4.Groups[0].Items.Count) + " " + det);
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] C7 用例异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally { DropSandbox(root); }
        }

        // ============================================================
        // C7b：**同节拍边界**（G-1 要求 2 = `D-2w` 的显式用例化）
        //   形态：内容已变（等长）但 mtime 与版本戳**完全相同**（`cmd copy` / `File.Copy`
        //   继承源戳 + 两次写落在同一系统时钟节拍 ⇒ 可达，非纯理论）。
        //   断言**当前行为**（判为"未变" ⇒ 0 解析、0 换代），并显式标注这是**已知边界、
        //   不阻断交付** —— 目的是让它以后不再以"随机假红"的形式冒出来。
        //   功能性修复（内容指纹旁路）按任务表豁免，不在本段范围。
        // ============================================================
        private static void CaseSameTickBoundary()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- C7b. D-2w 已知边界（显式用例）：等长内容 + 同节拍/还原 mtime ⇒ 判据判「未变」---");
            string root = MakeSandbox("sametick");
            try
            {
                WriteFile(ConfPath(root), MenusNewText());
                var w = new MenuConfWatcher(ConfPath(root), s => _sb.AppendLine("  [w] " + s));
                var good = new ConfParser(ConfPath(root));
                good.Parse();
                w.AcceptBaseline(good);                       // 建立版本戳基线

                long t0 = new FileInfo(ConfPath(root)).LastWriteTimeUtc.Ticks;
                long l0 = new FileInfo(ConfPath(root)).Length;
                int parse0 = w.ParseCount;
                string changed = SameLengthText(MenusNewText());   // ★ 等长、但内容确实不同

                string det;
                bool wrote = ExternalWrite(ConfPath(root), changed, out det);
                Check("C7b 前置①：外部写成功（独立进程）", wrote, det);
                long l1 = new FileInfo(ConfPath(root)).Length;
                Check("C7b 前置②：新旧内容长度**相等**（这正是本边界的必要条件）",
                    l1 == l0, "len " + l0 + "→" + l1);
                Check("C7b 前置③：内容确实不同（等长不等于等同）",
                    !string.Equals(ReadText(ConfPath(root)), MenusNewText(), StringComparison.Ordinal), "");

                File.SetLastWriteTimeUtc(ConfPath(root), TicksToUtc(t0));   // ★ 还原 mtime ⇒ 同戳
                long t2 = new FileInfo(ConfPath(root)).LastWriteTimeUtc.Ticks;
                Check("C7b 前置④：mtime 已还原成基线戳（同戳同长 ⇒ 命中快路径）",
                    t2 == t0, "t0=" + t0 + " 写后还原=" + t2);

                ConfParser fresh;
                bool reloaded = w.TryReloadIfChanged(out fresh);
                Eq("C7b 【已知边界·不阻断】同戳同长 ⇒ 判据判「未变」（返回 false，0 换代）",
                    reloaded.ToString().ToLower(), "false");
                Check("C7b 【已知边界】无新实例交出（fresh=null ⇒ 沿用旧表）", fresh == null, "");
                Check("C7b 【已知边界】旧表引用未变（w.Current 仍是原对象）",
                    ReferenceEquals(w.Current, good), "");
                Eq("C7b 【已知边界】0 次重解析（ParseCount 零增量 —— 连读盘都没发生）",
                    (w.ParseCount - parse0).ToString(), "0");
                _sb.AppendLine("  ★ 结论：`(ticks== && len==)` 快路径下「等长 + 同节拍」的变更**不可感知**；");
                _sb.AppendLine("    这是 I-7 判据的**已知窗口**（任务表已豁免本段不加内容指纹旁路），");
                _sb.AppendLine("    故**不计入失败**；其余用例一律用显式推进 mtime 的夹具 ⇒ 不再随机假红。");
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] C7b 用例异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally { DropSandbox(root); }
        }

        // ============================================================
        // C8/C9：auto.conf 全键实时（waitWhenShown = D-2v）+ enabled=0 不被短路
        // ============================================================
        private static void CaseAutoConfLive()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- C8. auto.conf 实时：waitWhenShown（D-2v）+ hideMissing（AC-C3/C4）---");
            string root = MakeSandbox("autoconf");
            try
            {
                WriteFile(AutoPath(root), AutoConfText(true, "0", "1"));
                var am = new AutoMatcher(root, s => _sb.AppendLine("  [am] " + s));
                am.Load(true);
                Eq("C8 启动态 hideMissing=0", am.Conf.HideMissing.ToString(), "0");
                Eq("C8 启动态 waitWhenShown=1", am.Conf.WaitWhenShown.ToString(), "1");

                var s0 = am.LiveShowConf();                    // 热身 + 建立版本戳
                // ★ G-4：原来是 `Check("C8 热身…", true, "")` —— 恒真、只加断言数、无判别力。
                //   改成信息行（不计入断言数）；真正的判别力在下面 s1/s2 的两条 Eq 上。
                _sb.AppendLine("  [信息] C8 热身：热键期快照可读（hideMissing=" + s0.HideMissing
                    + " waitWhenShown=" + s0.WaitWhenShown + "）");
                int read0 = am.ConfReadCount;

                // ---- 外部写（独立进程）：两个键同时翻转 + enabled 关掉 ----
                string det;
                bool ok = ExternalWrite(AutoPath(root),
                    AutoConfText(false, "1", "0"), out det);
                Check("C8 外部写（独立进程）auto.conf 成功", ok, det);

                var s1 = am.LiveShowConf();
                Eq("C8 waitWhenShown 实时（1→0）：热键期即拿到新值 —— D-2v 修复点",
                    s1.WaitWhenShown.ToString(), "0");
                Eq("C8 hideMissing 实时（0→1）：热键期即拿到新值", s1.HideMissing.ToString(), "1");
                Eq("C8 反面证据：变更路径**真的读了一次盘**（ConfReadCount +1，不是「没接上」）",
                    (am.ConfReadCount - read0).ToString(), "1");
                Eq("C8 旧口径对照：Load 之外的 Conf 内存态在此刻也已是新值（同一次读盘刷新）",
                    am.Conf.WaitWhenShown.ToString(), "0");

                // ---- enabled=0 不短路（难点⑥）----
                Eq("C9 enabled=0 ⇒ 判定层实时取值**仍在工作**（监听不被总开关短路）",
                    s1.HideMissing.ToString(), "1");
                int read1 = am.ConfReadCount;
                ExternalWrite(AutoPath(root), AutoConfText(false, "0", "1"), out det);
                var s2 = am.LiveShowConf();
                Eq("C9 enabled=0 下再改 hideMissing ⇒ 仍然立即生效（1→0）", s2.HideMissing.ToString(), "0");
                Eq("C9 enabled=0 下再改 waitWhenShown ⇒ 仍然立即生效（0→1）", s2.WaitWhenShown.ToString(), "1");
                Eq("C9 enabled=0 下变更仍走「读盘一次」（监听未停）",
                    (am.ConfReadCount - read1).ToString(), "1");

                // ---- 三类分栏中的"类型 i 需经 TryAutoExec"—— 断言其消费点结构 ----
                _sb.AppendLine("  （类型 i 的 enabled/minLen/showByDefault/cacheSeconds 生效**经过** TryAutoExec：");
                _sb.AppendLine("    无选中文本 ⇒ RunEverApp.cs TryAutoExec 提前 return，本轮不消费 —— 见下结构断言）");
                string app = ReadText(Path.Combine(_root, "cs", "RunEverApp.cs"));
                Check("C8 结构证据：TryAutoExec 在空文本时提前返回（enabled/minLen 等需有选中文本才消费）",
                    app != null && app.Contains("if (text.Length == 0) return false;"),
                    app == null ? "读不到 RunEverApp.cs" : "");
                Check("C8 结构证据：TryAutoExec 之后才 Load(false)（插件扫描受 CacheSec 节流，本段不动）",
                    app != null && app.Contains("_autoMatcher.Load(false);"), "");

                // 反面：判定层两个键**不**经过 TryAutoExec（消费点在 ShowMenuFlow 的 HotReloadCheck）
                Check("C8 结构证据：hideMissing/waitWhenShown 的取值点在流程入口 HotReloadCheck（与选中文本无关）",
                    app != null && app.Contains("HotReloadCheck();")
                    && app.Contains("_autoMatcher.LiveShowConf()"), "");

                // ============================================================
                // C12（G-2 要求②）：**「语义退化守卫」的分界证据** —— 只限热键路径的自动重载
                //   守卫在 MenuConfWatcher 内 ⇒ 只要**显式通道**的代码路径不经过本类，
                //   它就必然"尊重文件实际内容（含空表）"。用**源码级**证据断言这条分界
                //   （不走 `new RunEverApp(Ini)` 实跑：那会 `ConfigBootstrap.Ensure` 建用户
                //     config\ 布局并起后台刷新线程，属自检不该有的副作用）。
                // ============================================================
                _sb.AppendLine();
                _sb.AppendLine("--- C12. G-2 分界：守卫只限热键路径；显式通道（托盘「重载配置」/ 保存后重读）不受限 ---");
                string tray = ReadText(Path.Combine(_root, "cs", "TrayApp.cs"));
                int trayWatcherRefs = 0;
                if (tray != null)
                    trayWatcherRefs = (tray.Split(new[] { "MenuConfWatcher" }, StringSplitOptions.None).Length - 1);
                Check("C12 托盘「重载配置」走 new RunEverApp(_root) + Init()（不经 MenuConfWatcher ⇒ 无守卫）",
                    tray != null && tray.Contains("_app = new RunEverApp(_root);")
                    && tray.Contains("ok = _app.Init();"),
                    tray == null ? "读不到 TrayApp.cs" : "");
                Eq("C12 TrayApp.cs 全文**零**处引用 MenuConfWatcher（分界成立的硬证据）",
                    trayWatcherRefs.ToString(), "0");
                Check("C12 保存后重读 ReloadMenus 直读文件（new ConfParser + Parse，不经 MenuConfWatcher）",
                    app != null && app.Contains("var p = new ConfParser(Path.Combine(Root, \"config\", \"menus.conf\"));")
                    && app.Contains("_parser = p;"),
                    app == null ? "读不到 RunEverApp.cs" : "");
                Check("C12 契约 §4 已写明该分界（显式通道按文件实际内容呈现，含空表）",
                    (ReadText(Path.Combine(_root, "docs", "架构契约.md")) ?? "")
                        .Contains("显式通道不受此限制"), "");

                // ============================================================
                // C12b（低成本建议②）：把"显式通道**尊重空表**"从**文本级**升级为**行为级**。
                //   文本级断言（上面 3 条 Contains）挡不住"把守卫插进 ReloadMenus 的 Parse 与
                //   赋值之间" —— 字符串仍在、行为已变。这里直接驱动解析路径（守门 B3 给的代码）。
                //   安全性：`RunEverApp(root)` 构造函数只存 `Root` + 拼 `state\runever-cs.log` 路径
                //   （**无副作用**，副作用全在 `Init()` 里）⇒ 不建用户 `config\` 布局、不起后台线程。
                // ============================================================
                string appRoot = MakeSandbox("explicit");
                try
                {
                    WriteFile(ConfPath(appRoot), "# 只剩注释：0 组" + Environment.NewLine);
                    var app2 = new RunEverApp(appRoot);      // ★ 不调 Init()
                    var fld = typeof(RunEverApp).GetField("_parser",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    var before = (fld == null) ? null : (fld.GetValue(app2) as ConfParser);
                    bool reloadOk = app2.ReloadMenus();
                    var cp = (fld == null) ? null : (fld.GetValue(app2) as ConfParser);

                    Eq("C12b 显式通道 ReloadMenus 返回 true（解析成功 ⇒ 采纳）",
                        reloadOk.ToString().ToLower(), "true");
                    Check("C12b 显式通道**尊重空表**：ReloadMenus 后 `_parser` 就是刚解析的 0 组实例"
                        + "（热键路径同形态必须保留旧表 —— 见 C7 ③-④/③-⑤/③-⑥，二者方向相反）",
                        cp != null && cp.Groups.Count == 0 && !ReferenceEquals(cp, before),
                        cp == null ? "反射取不到 _parser" : "分组=" + cp.Groups.Count);
                    Check("C12b 前置：空表实例确实是 ReloadMenus 换上的（写前 _parser 不是它）",
                        before == null || !ReferenceEquals(before, cp),
                        before == null ? "写前 _parser=null" : "写前分组=" + before.Groups.Count);
                }
                catch (Exception ex)
                {
                    _fail++;
                    _sb.AppendLine("[FAIL] C12b 用例异常: " + ex.Message + "\n" + ex.StackTrace);
                }
                finally { DropSandbox(appRoot); }
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] C8/C9 用例异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally { DropSandbox(root); }
        }

        private static string ReadText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
            catch { return null; }
        }

        // ============================================================
        // C10：难点④ hotkey 不自动重注册（结构级 + 文档口径）
        // ============================================================
        private static void CaseHotkeyContract()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- C10. 难点④：hotkey 不由热加载处理（注册期键）---");
            try
            {
                // ① 结构证据：AutoConf 仍是 6 个字段、且**没有** hotkey ⇒ 不存在"文件 vs 内存"双路径
                var fields = typeof(AutoConf).GetFields(BindingFlags.Public | BindingFlags.Instance);
                var names = new List<string>();
                foreach (var f in fields) names.Add(f.Name);
                _sb.AppendLine("  AutoConf 字段（" + fields.Length + "）= " + Join(names));
                // 2026-11-08：8 个字段 —— 菜单显示后端（uiBackend）与采集后端（captureBackend）
                // 两个模式开关已随 WPF 菜单后端整体撤掉；其余仍是"热键期不该反复重读"的开关。
                Eq("C10 AutoConf 字段数 = 8（enabled/minLen/cacheSeconds/showByDefault/waitWhenShown/hideMissing/menuOrder/memTrimIdle）",
                    fields.Length.ToString(), "8");
                bool hasHotkeyField = false;
                foreach (var f in fields)
                    if (f.Name.IndexOf("hotkey", StringComparison.OrdinalIgnoreCase) >= 0) hasHotkeyField = true;
                Check("C10 AutoConf **不含** hotkey 字段（禁止「文件 vs 内存」两条读取路径）", !hasHotkeyField, "");

                // ② 结构证据：热键期快照只装判定层/执行器要用的键
                var sfields = typeof(LiveConfSnapshot).GetFields(BindingFlags.Public | BindingFlags.Instance);
                Check("C10 热键期快照 LiveConfSnapshot 只含 3 个键（hideMissing/waitWhenShown/menuOrder），不含 hotkey",
                    sfields.Length == 3, "字段数=" + sfields.Length);

                // ③ AutoMatcher 源码：ReadConf 不解析 hotkey（它是 AutoConf 的 6 键解析器）
                string am = ReadText(Path.Combine(_root, "cs", "Services", "AutoMatcher.cs"));
                Check("C10 结构证据：AutoMatcher.ReadConf 不解析 hotkey（不接管注册期键）",
                    am != null && am.IndexOf("case \"hotkey\"", StringComparison.OrdinalIgnoreCase) < 0,
                    am == null ? "读不到 AutoMatcher.cs" : "");

                // ④ 文档口径证据：契约 §4 必须写明"hotkey 为注册期键，改动需经托盘「重载配置」生效"
                string doc = ReadText(Path.Combine(_root, "docs", "架构契约.md"));
                Check("C10 文档证据：docs\\架构契约.md §4 写明 hotkey 的生效路径（注册期键）",
                    doc != null && doc.Contains("hotkey") && doc.Contains("重载配置"),
                    doc == null ? "读不到 docs\\架构契约.md" : "");
                Check("C10 文档证据：契约写明「外部编辑 auto.conf 不会自动重注册」",
                    doc != null && doc.Contains("不会自动重注册"), "");

                // ⑤ 结构证据：TrayApp 的 ApplyHotkey 只有两条调用链（OpenSettings 关窗 / 托盘重载）
                string tray = ReadText(Path.Combine(_root, "cs", "TrayApp.cs"));
                int calls = CountOccurrences(tray, "ApplyHotkey()");
                _sb.AppendLine("  TrayApp.cs 中 \"ApplyHotkey()\" 出现次数 = " + calls
                    + "（1 次定义 + 2 次调用 = 3）");
                Eq("C10 TrayApp.ApplyHotkey 仅两条调用链（OpenSettings 关窗 :428 / ReloadConfig :450）",
                    calls.ToString(), "3");
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] C10 用例异常: " + ex.Message + "\n" + ex.StackTrace);
            }
        }

        private static int CountOccurrences(string s, string needle)
        {
            if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(needle)) return 0;
            int n = 0, i = 0;
            while ((i = s.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // ============================================================
        // C11：I-5 开销实测（AC-C5）—— 沿用 ConfWriteSelfTest.cs:184-201 口径 + p50/p95/max
        // ============================================================
        private static void CaseOverhead()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- C11. I-5 开销实测（N=500，confwrite 同口径 + p50/p95/max）---");
            string root = MakeSandbox("overhead");
            try
            {
                WriteFile(ConfPath(root), MenusNewText());
                WriteFile(AutoPath(root), AutoConfText(true, "1", "1"));

                var am = new AutoMatcher(root, null);
                am.Load(true);
                var w = new MenuConfWatcher(ConfPath(root), null);
                var p0 = new ConfParser(ConfPath(root));
                p0.Parse();
                w.AcceptBaseline(p0);

                // 基线（段 1.6 既有形态 = 1 次 stat）：LiveHideMissing
                am.LiveHideMissing();
                const int N = 500;

                var baseSamples = new List<double>(N);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < N; i++)
                {
                    long t = sw.ElapsedTicks;
                    am.LiveHideMissing();
                    baseSamples.Add((sw.ElapsedTicks - t) * 1000000.0 / Stopwatch.Frequency);
                }
                double baseUs = sw.Elapsed.TotalMilliseconds * 1000.0 / N;

                // 本段热键期总检查（menus stat + auto.conf stat）= 2 次 stat
                var hotSamples = new List<double>(N);
                int parse0 = w.ParseCount, read0 = am.ConfReadCount;
                sw.Restart();
                for (int i = 0; i < N; i++)
                {
                    long t = sw.ElapsedTicks;
                    ConfParser fresh;
                    w.TryReloadIfChanged(out fresh);
                    am.LiveShowConf();
                    hotSamples.Add((sw.ElapsedTicks - t) * 1000000.0 / Stopwatch.Frequency);
                }
                double fastUs = sw.Elapsed.TotalMilliseconds * 1000.0 / N;

                _sb.AppendLine("  [开销] 基线（段1.6 既有：1×stat，LiveHideMissing）: " + baseUs.ToString("F2")
                    + " µs/次（" + N + " 次平均）");
                _sb.AppendLine("  [开销] 本段热键期（2×stat：menus.conf + auto.conf，均未变）: " + fastUs.ToString("F2")
                    + " µs/次；" + Pct(baseSamples, "基线") + "；" + Pct(hotSamples, "本段"));
                _sb.AppendLine("  [开销] 增量 = " + (fastUs - baseUs).ToString("F2")
                    + " µs（≈ 一次额外 stat；基线锚 25–40µs 量级，AC-C5 要求增量 ≤ ~10µs 同量级）");

                Eq("C11 快路径真的没解析（ParseCount 增量=0）", (w.ParseCount - parse0).ToString(), "0");
                Eq("C11 快路径真的没读盘（ConfReadCount 增量=0）", (am.ConfReadCount - read0).ToString(), "0");

                // 慢路径（变更首读）：外部写 ⇒ 一次 Parse + 一次 ReadConf
                string det;
                ExternalWrite(ConfPath(root), MenusOldText(), out det);
                int pc1 = w.ParseCount;
                sw.Restart();
                ConfParser f2;
                w.TryReloadIfChanged(out f2);
                double slowMs = sw.Elapsed.TotalMilliseconds;
                Eq("C11 慢路径真的发生了 Parse（反面证据：ParseCount +1）", (w.ParseCount - pc1).ToString(), "1");
                _sb.AppendLine("  [开销] 变更路径（重解析整份 menus.conf "
                    + new FileInfo(ConfPath(root)).Length + " 字节）: " + slowMs.ToString("F3")
                    + " ms —— 只读这一个文本文件，不扫插件、不重建匹配器/图标/执行器");

                // ★ 断言形态照抄 ConfWriteSelfTest.cs:200（同一守卫）
                Check("C11 快路径明显低于一次文件重读（fastUs<1000 && fastUs<slowMs*1000，confwrite 同守卫）",
                    fastUs < 1000 && fastUs < slowMs * 1000,
                    "快=" + fastUs.ToString("F2") + "µs 慢=" + slowMs.ToString("F3") + "ms");
                Check("C11 增量 ≤ 一次 stat 量级（I-5：热键路径不得出现读盘/解析/重扫）",
                    (fastUs - baseUs) < 100.0 && fastUs < 1000.0,
                    "基线=" + baseUs.ToString("F2") + "µs 本段=" + fastUs.ToString("F2")
                    + "µs 增量=" + (fastUs - baseUs).ToString("F2") + "µs");
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] C11 用例异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally { DropSandbox(root); }
        }

        private static string Pct(List<double> samples, string tag)
        {
            var sorted = new List<double>(samples);
            sorted.Sort();
            double p50 = sorted[sorted.Count / 2];
            double p95 = sorted[(int)(sorted.Count * 0.95)];
            double max = sorted[sorted.Count - 1];
            return tag + " p50/p95/max=" + p50.ToString("F2") + "/" + p95.ToString("F2")
                + "/" + max.ToString("F2") + " µs";
        }
    }
}
