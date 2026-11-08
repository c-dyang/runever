using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>解析三态（契约 §4）：Ok 已解析 / Missing EV 已确认缺失 / Unknown 后端不可用或不适用。</summary>
    public enum ResolveState
    {
        Ok = 0,
        Missing,
        Unknown
    }

    /// <summary>缺件判定结果（判定层只消费这个枚举，不看盘）。</summary>
    public enum EntryJudgement
    {
        /// <summary>保留。</summary>
        Keep = 0,

        /// <summary>隐藏（本机缺失）。</summary>
        Drop,

        /// <summary>不参与判定（占位符目标 / 无本地依赖的动作类型）。</summary>
        Skip
    }

    /// <summary>一次"现查"的结果（时机③）。</summary>
    public class ResolveOutcome
    {
        public ResolveState State = ResolveState.Unknown;
        public string Path = "";
        public string Backend = "";
        public string Reason = "";
        public string ErrorCode = "";
        public ResolveTier Tier = ResolveTier.None;

        /// <summary>命中的路径是否已回写 `cache\paths.txt`。</summary>
        public bool CacheWriteBack;

        public override string ToString()
        {
            return "state=" + State + " path=[" + Path + "] backend=" + Backend
                + " tier=" + Tier + " writeback=" + CacheWriteBack
                + (string.IsNullOrEmpty(ErrorCode) ? "" : " code=" + ErrorCode)
                + (string.IsNullOrEmpty(Reason) ? "" : " reason=" + Reason);
        }
    }

    /// <summary>
    /// 解析选项 —— 后端与超时可注入（自检需要确定性；生产路径用默认值 = 自动探测）。
    /// </summary>
    public class ResolveOptions
    {
        /// <summary>搜索后端（null = 按 root 自动探测 EV → es → none）。</summary>
        public ISearchBackend Backend;

        /// <summary>
        /// es.exe 路径覆盖：null = 自动探测；非空 = 按此路径（不存在则回落到自动探测）。
        /// </summary>
        public string EsPathOverride;

        /// <summary>
        /// **彻底禁用 es.exe**（跳过探测）：用来造"EV / es 都不可用 ⇒ Unknown"的场景（自检）。
        /// </summary>
        public bool DisableEs;

        /// <summary>现查硬超时（ms）。默认 <see cref="PathResolvePolicy.OnDemandTimeoutMs"/>。</summary>
        public int TimeoutMs = PathResolvePolicy.OnDemandTimeoutMs;
    }

    /// <summary>
    /// 解析策略 —— 契约 §4 的三个触发时机 + 后端降级链 + 三态 + 现查回写。
    ///
    /// 时机①（开机后台全盘扫）/ ②（配置保存后增量）/ ③（执行期软件移位兜底）
    /// 全部走这里；调用方负责"放后台线程"这件事本身（时机①/② 不得落在热键回调里）。
    ///
    /// ★ 本文件是缺件判定 **唯一的 IO 源**（C3）：`MissingFilter.Apply` 只做"数据注入/转发"，
    ///   它的逐条判定委托给这里的 `JudgeEntry`。
    /// </summary>
    public static class PathResolvePolicy
    {
        /// <summary>
        /// 执行期现查的**硬超时**（ms）。任务表 §2.4 建议 800–1500ms：超过即视为未命中，
        /// 绝不能把"点了没反应"换成"点了卡两秒"。
        /// </summary>
        public const int OnDemandTimeoutMs = 1200;

        // ============================================================
        // 纯字符串助手（原 MissingFilter 的实现，搬到解析层做单一源；
        // MissingFilter 保留同名转发方法，既有自检 --selftest-hide 零改动）
        // ============================================================

        /// <summary>取命令行的首个 token（支持带引号的路径）。</summary>
        public static string FirstToken(string target)
        {
            if (string.IsNullOrEmpty(target)) return "";
            string s = target.Trim();
            if (s.Length == 0) return "";

            if (s[0] == '"')
            {
                int e = s.IndexOf('"', 1);
                return e > 1 ? s.Substring(1, e - 1) : s.Substring(1);
            }

            int sp = s.IndexOfAny(new[] { ' ', '\t' });
            return sp > 0 ? s.Substring(0, sp) : s;
        }

        /// <summary>去掉包裹的双引号。</summary>
        public static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string v = s.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                v = v.Substring(1, v.Length - 2);
            return v;
        }

        /// <summary>目标里含运行时占位符（`%`）⇒ 判不了，不隐藏。</summary>
        public static bool NeedsRuntimeValue(string s)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf('%') >= 0;
        }

        // ============================================================
        // 缺件判定（判定层的唯一 IO 源）
        // ============================================================
        /// <summary>
        /// 逐条判定（run 四路 / open / 其余跳过），与现役 `MissingFilter.Peek` 口径逐字一致。
        /// 口径：run —— ① 路径缓存 ② 目标自带路径 ③ PATH + App Paths；
        ///       open —— 文件或目录存在（支持 %ENV% 展开）；
        ///       url/action/send/keys/子菜单标题 —— 不判。
        /// </summary>
        public static EntryJudgement JudgeEntry(MenuItem it, PathCache cache)
        {
            if (it == null) return EntryJudgement.Skip;

            switch (it.Type)
            {
                case ActionType.Run:
                    {
                        string prog = FirstToken(it.Target);
                        if (NeedsRuntimeValue(prog)) return EntryJudgement.Skip;
                        return RunTargetExists(prog, cache) ? EntryJudgement.Keep : EntryJudgement.Drop;
                    }

                case ActionType.Open:
                    {
                        string p = StripQuotes(it.Target);
                        if (NeedsRuntimeValue(p)) return EntryJudgement.Skip;
                        return OpenTargetExists(p) ? EntryJudgement.Keep : EntryJudgement.Drop;
                    }

                default:
                    // url / action / send / keys / Group：无本地依赖，不判
                    return EntryJudgement.Skip;
            }
        }

        /// <summary>run 目标是否可执行：① 路径缓存 ② 目标自带路径 ③ PATH/App Paths。</summary>
        public static bool RunTargetExists(string prog, PathCache cache)
        {
            if (string.IsNullOrEmpty(prog)) return true;   // 空目标判不了，留着

            if (cache != null)
            {
                string full = cache.Get(prog);
                if (!string.IsNullOrEmpty(full)) return File.Exists(full);
            }

            if (prog.IndexOf('\\') >= 0 || prog.IndexOf('/') >= 0)
                return File.Exists(prog);

            return !string.IsNullOrEmpty(PathCache.ResolveProgram(prog));
        }

        /// <summary>open 目标是否存在（文件或目录）。</summary>
        public static bool OpenTargetExists(string p)
        {
            if (string.IsNullOrEmpty(p)) return true;
            try
            {
                if (File.Exists(p) || Directory.Exists(p)) return true;

                string exp = Environment.ExpandEnvironmentVariables(p);
                if (!string.Equals(exp, p, StringComparison.OrdinalIgnoreCase))
                    return File.Exists(exp) || Directory.Exists(exp);

                return false;
            }
            catch { return true; }   // 路径非法（含通配符等）⇒ 不隐藏，交给执行期报错
        }

        // ============================================================
        // 缺件注入位（时机①/②/③ 的产物 → 判定层的**数据**入口）
        // ============================================================
        /// <summary>
        /// 遍历分组列表，把"本机是否可用"算成 `EntryResolved[]` + `MissingItemIndex` 注入 ctx。
        /// ★ 这是判定层唯一的缺件数据来源（判定层自己不查盘）。
        /// 下标口径 = 分组序 → 组内条目序 展平（与 MatchEngine.FilterMissing 一致）。
        /// </summary>
        public static void InjectEntryResolved(MatchContext ctx, IList<MenuGroup> groups,
            PathCache cache, bool enabled, Action<string> log)
        {
            if (ctx == null) return;

            int total = 0;
            if (groups != null)
                foreach (var g in groups)
                    if (g != null && g.Items != null) total += g.Items.Count;

            if (!enabled)
            {
                // 开关关 = 零判定（与现役 MissingFilter 的两态语义一致）
                ctx.EntryResolved = new bool[total];
                for (int i = 0; i < total; i++) ctx.EntryResolved[i] = true;
                ctx.MissingItemIndex = new HashSet<int>();
                return;
            }

            var resolved = new bool[total];
            var missing = new HashSet<int>();
            int k = 0;
            if (groups != null)
            {
                foreach (var g in groups)
                {
                    if (g == null || g.Items == null) continue;
                    foreach (var it in g.Items)
                    {
                        var d = JudgeEntry(it, cache);
                        bool keep = (d != EntryJudgement.Drop);
                        resolved[k] = keep;
                        if (!keep)
                        {
                            missing.Add(k);
                            if (log != null)
                                log("缺件隐藏: [" + g.Name + "] " + it.Title + "  → " + it.Target);
                        }
                        k++;
                    }
                }
            }

            ctx.EntryResolved = resolved;
            ctx.MissingItemIndex = missing;
            if (missing.Count > 0 && log != null)
                log("缺件过滤: 隐藏=" + missing.Count + " / 条目=" + total);
        }

        // ============================================================
        // 时机①/② —— 后台全盘扫 / 配置保存后增量
        // ============================================================
        /// <summary>
        /// 构建路径缓存（`force=false` 即增量：已缓存且文件仍存在 ⇒ skipped）。
        /// ★ 调用方必须在**后台线程**调用本方法（可能数十秒），不得放进热键回调。
        /// </summary>
        public static BuildStats BuildInto(string root, PathCache cache, bool force,
            Action<string> log, ISearchBackend backend)
        {
            return BuildInto(root, cache, force, log,
                new ResolveOptions { Backend = backend });
        }

        /// <summary>构建路径缓存（可注入后端 / es 路径 / 超时 —— 自检确定性入口）。</summary>
        public static BuildStats BuildInto(string root, PathCache cache, bool force,
            Action<string> log, ResolveOptions opt)
        {
            Action<string> lg = log ?? (s => { });
            if (opt == null) opt = new ResolveOptions();

            string conf = Path.Combine(root, "config", "menus.conf");
            if (!File.Exists(conf))
            {
                lg("解析策略: 配置文件不存在，跳过解析：" + conf);
                return new BuildStats();
            }

            var parser = new ConfParser(conf);
            parser.Parse();
            var progs = parser.CollectPrograms();

            BackendStatus st = BackendResolver.Resolve(root, false);
            ISearchBackend used = (opt.Backend != null) ? opt.Backend : BackendResolver.MakeBackend(st);
            string esPath = (opt.EsPathOverride != null) ? opt.EsPathOverride : ((st == null) ? "" : st.EsPath);
            if (st != null && st.Degraded && !string.IsNullOrEmpty(st.Reason))
                lg("解析策略: 搜索后端降级为 " + st.KindText + "（原因：" + st.Reason + "）");

            var builder = new PathCacheBuilder(cache, esPath, lg, used, opt.DisableEs);
            lg("解析策略: " + (force ? "全量" : "增量") + "解析 " + progs.Count
                + " 个程序名，后端=" + (st == null ? "none" : st.KindText));
            var stats = builder.Build(progs, force);
            lg("解析策略: 完成 " + stats);
            return stats;
        }

        /// <summary>时机①：开机后台全盘扫（force=true，一次性重建失效项）。</summary>
        public static BuildStats BootSweep(string root, PathCache cache, Action<string> log, ISearchBackend backend)
        {
            return BuildInto(root, cache, true, log, backend);
        }

        /// <summary>时机①（可注入选项）。</summary>
        public static BuildStats BootSweep(string root, PathCache cache, Action<string> log, ResolveOptions opt)
        {
            return BuildInto(root, cache, true, log, opt);
        }

        /// <summary>
        /// 时机②：配置保存后**增量**解析 —— 只解析新增名 + 上次失败名
        /// （新增名不在缓存里、失败名没进缓存 ⇒ 增量语义天然覆盖这两类）。
        /// </summary>
        public static BuildStats IncrementalAfterSave(string root, PathCache cache, Action<string> log, ISearchBackend backend)
        {
            return BuildInto(root, cache, false, log, backend);
        }

        /// <summary>时机②（可注入选项）。</summary>
        public static BuildStats IncrementalAfterSave(string root, PathCache cache, Action<string> log, ResolveOptions opt)
        {
            return BuildInto(root, cache, false, log, opt);
        }

        /// <summary>时机②/③ 的默认入口（自动探测后端，生产路径用）。</summary>
        public static BuildStats RefreshNow(string root, PathCache cache, bool force, Action<string> log)
        {
            return BuildInto(root, cache, force, log, (ResolveOptions)null);
        }

        /// <summary>
        /// 时机② 的"新增名清单"（供日志/自检取证）：缓存里没有的名字。
        /// </summary>
        public static List<string> NewNames(string root, PathCache cache)
        {
            var n = new List<string>();
            string conf = Path.Combine(root, "config", "menus.conf");
            if (!File.Exists(conf) || cache == null) return n;
            var parser = new ConfParser(conf);
            parser.Parse();
            foreach (var p in parser.CollectPrograms())
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (p.IndexOf('\\') >= 0 || p.IndexOf('/') >= 0) continue;
                string v = cache.Get(p);
                if (string.IsNullOrEmpty(v)) n.Add(p);
            }
            return n;
        }

        // ============================================================
        // 时机③ —— 执行期现查（软件移位兜底）：硬超时 + 命中回写 + 未命中报错
        // ============================================================
        /// <summary>
        /// 缓存命中但文件不存在（程序移位）时现查一次。
        ///   命中 ⇒ 返回 Ok + **回写 `cache\paths.txt`**（原子 Save，走 PathCache.Save）；
        ///   未命中 ⇒ Missing；后端不可用 / 超时 ⇒ Unknown。
        /// ★ 只允许在"点击之后"的执行路径里调用（不在热键回调内）。
        /// </summary>
        public static ResolveOutcome ResolveOnDemand(string root, string progName, PathCache cache,
            Action<string> log, ISearchBackend backend)
        {
            return ResolveOnDemand(root, progName, cache, log,
                new ResolveOptions { Backend = backend, TimeoutMs = OnDemandTimeoutMs });
        }

        /// <summary>时机③ 现查（默认选项 = 自动探测后端 + 默认硬超时）。</summary>
        public static ResolveOutcome ResolveOnDemand(string root, string progName, PathCache cache,
            Action<string> log)
        {
            return ResolveOnDemand(root, progName, cache, log, (ResolveOptions)null);
        }

        /// <summary>时机③ 现查（可注入后端 / es 路径 / 超时）。</summary>
        public static ResolveOutcome ResolveOnDemand(string root, string progName, PathCache cache,
            Action<string> log, ResolveOptions opt)
        {
            Action<string> lg = log ?? (s => { });
            if (opt == null) opt = new ResolveOptions();
            var o = new ResolveOutcome();

            if (string.IsNullOrEmpty(progName))
            {
                o.State = ResolveState.Unknown;
                o.Reason = "空程序名";
                o.ErrorCode = "empty-name";
                return o;
            }
            string name = progName.Trim().ToLowerInvariant();

            // ① 缓存命中且文件仍在 ⇒ 直接 Ok（不查 EV）
            if (cache != null)
            {
                string full = cache.Get(name);
                if (!string.IsNullOrEmpty(full) && File.Exists(full))
                {
                    o.State = ResolveState.Ok;
                    o.Path = full;
                    o.Backend = "cache";
                    return o;
                }
            }

            // ② 后端可用性（不可用 ⇒ Unknown，不是"缺失"）
            BackendStatus st = BackendResolver.Resolve(root, false);
            ISearchBackend used = (opt.Backend != null) ? opt.Backend : BackendResolver.MakeBackend(st);
            string esPath = (opt.EsPathOverride != null) ? opt.EsPathOverride : ((st == null) ? "" : st.EsPath);
            var builder = new PathCacheBuilder(cache, esPath, lg, used, opt.DisableEs);
            o.Backend = (used != null && used.Available) ? used.Name : (st == null ? "none" : st.KindText);

            if (!builder.SearchAvailable)
            {
                o.State = ResolveState.Unknown;
                o.Reason = "搜索后端不可用（EV / es 都不可用）";
                o.ErrorCode = "backend-unavailable";
                lg("现查: " + name + " → Unknown（" + o.Reason + "）");
                return o;
            }

            // ③ 硬超时现查（EV 单次百毫秒级；超时 = 未命中，绝不悬挂）
            int timeout = opt.TimeoutMs > 0 ? opt.TimeoutMs : OnDemandTimeoutMs;
            ResolveResult rr = null;
            try
            {
                var task = Task.Run(delegate { return builder.ResolveOneResult(name); });
                if (!task.Wait(timeout))
                {
                    o.State = ResolveState.Unknown;
                    o.Reason = "现查超时（>" + timeout + "ms）";
                    o.ErrorCode = "timeout";
                    lg("现查: " + name + " → Unknown（超时 " + timeout + "ms）");
                    return o;
                }
                rr = task.Result;
            }
            catch (Exception ex)
            {
                o.State = ResolveState.Unknown;
                o.Reason = "现查异常：" + ex.Message;
                o.ErrorCode = "query-exception";
                lg("现查: " + name + " → Unknown（" + o.Reason + "）");
                return o;
            }

            if (rr != null && !string.IsNullOrEmpty(rr.Path))
            {
                o.State = ResolveState.Ok;
                o.Path = rr.Path;
                o.Tier = rr.Tier;

                // 回写缓存（原子替换由 PathCache.Save 保证）—— 否则下次仍走现查
                if (cache != null)
                {
                    try
                    {
                        cache.Set(name, rr.Path);
                        cache.Save();
                        o.CacheWriteBack = true;
                        lg("现查: " + name + " → " + rr.Path + "（已回写 cache\\paths.txt）");
                    }
                    catch (Exception ex)
                    {
                        lg("现查: " + name + " 命中但回写缓存失败：" + ex.Message);
                    }
                }
                return o;
            }

            o.State = ResolveState.Missing;
            o.Reason = "EV 现查未命中";
            o.ErrorCode = "not-found";
            lg("现查: " + name + " → Missing（EV 未命中）");
            return o;
        }
    }
}
