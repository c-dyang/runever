// ============================================================
// RunEver · 配置驱动的全盘自检引擎（功能 B）
//
// 用户指令：「根据配置 扫描文件是否存在的全盘自检 自检结束给出未安装的软件清单 列出 choco
//   安装执行命令」。用户定案：**只生成/导出命令清单，不做一键安装**（不弹 UAC、不跑 choco）。
//
// ★ 核心纪律：「未安装」是结论，「待确认」是默认。
//   三级解析（缓存 → PATH/App Paths → 搜索）全未命中有三种成因，现象完全一样：
//     ① 真的没装  ② 便携版/绿色版（不在索引里）  ③ 搜索后端降级了（EV 不通、es 也没有）
//   ⇒ 只有在「EV 后端可用 且 探针命中 且 IsDBLoaded」时，才允许输出「未找到（已确认）」；
//     其余一律「未找到（待确认）」。降级态下**禁止**出现"未安装"字样（自检里专门断言）。
//
// 状态四态（S4 表）：
//   Ok       已安装      —— 解析到真实路径
//   Missing  未找到（已确认）—— 解析不到 且 EV 可用且 DB 已加载
//   Unknown  未找到（待确认）—— 解析不到 但后端降级/DB 未加载
//   Skipped  跳过（未检查）—— 含运行时占位符，或类型为 url/send/keys
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>自检条目状态。</summary>
    public enum CheckStatus { Ok = 0, Missing, Unknown, Skipped }

    /// <summary>单条自检结果。</summary>
    public sealed class SelfCheckItem
    {
        // ★ 必须是**属性**而不是字段：WPF DataGrid 绑定只认属性（字段绑定静默失败）
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Group { get; set; }
        public string Target { get; set; }
        public string Prog { get; set; }
        public string Path { get; set; }
        public string Tier { get; set; }
        public CheckStatus Status { get; set; }
        public string Package { get; set; }
        public string Note { get; set; }

        public SelfCheckItem()
        {
            Kind = ""; Title = ""; Group = ""; Target = "";
            Prog = ""; Path = ""; Tier = ""; Package = ""; Note = "";
            Status = CheckStatus.Skipped;
        }

        /// <summary>包名展示：已安装/跳过显示「-」，未命中且无映射显示「待确认」（禁留空白）。</summary>
        public string PackageText
        {
            get
            {
                if (Status == CheckStatus.Ok || Status == CheckStatus.Skipped) return "-";
                return string.IsNullOrEmpty(Package) ? "待确认" : Package;
            }
        }

        public static string StatusText(CheckStatus s)
        {
            switch (s)
            {
                case CheckStatus.Ok: return "已安装";
                case CheckStatus.Missing: return "未找到（已确认）";
                case CheckStatus.Unknown: return "未找到（待确认）";
                default: return "跳过（未检查）";
            }
        }

        public string StatusLabel { get { return StatusText(Status); } }
    }

    /// <summary>choco 包名映射表（人工维护；加载失败 = 空表 = 全部待确认，绝不抛）。</summary>
    public sealed class ChocoMap
    {
        private readonly Dictionary<string, string> _map =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public int Count { get { return _map.Count; } }

        /// <summary>已映射的程序名快照（自检校验用）。</summary>
        public List<string> Keys()
        {
            return new List<string>(_map.Keys);
        }

        /// <summary>从 config\choco-map.conf 读取（格式：prog=package，# 注释）。</summary>
        public static ChocoMap Load(string root)
        {
            var m = new ChocoMap();
            try
            {
                string f = Path.Combine(root ?? "", "config", "choco-map.conf");
                if (!File.Exists(f)) return m;
                foreach (string raw in File.ReadAllLines(f, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    if (k.Length > 0 && v.Length > 0) m._map[k] = v;
                }
            }
            catch { }
            return m;
        }

        public string Lookup(string prog)
        {
            if (string.IsNullOrEmpty(prog)) return "";
            string v;
            return _map.TryGetValue(prog.Trim().ToLowerInvariant(), out v) ? v : "";
        }
    }

    /// <summary>自检报告（引擎产物；窗口 / CLI / 导出都消费它）。</summary>
    public sealed class SelfCheckReport
    {
        public string Root = "";
        public BackendStatus Backend;
        public long ElapsedMs;
        public long CacheWriteBackCount;
        public string CacheWriteBackNote = "";

        /// <summary>本次自检的完成时刻（B4b-9 留痕用：窗口/状态里要能说"上次检查于几点"）。</summary>
        public DateTime FinishedAt = DateTime.Now;

        public readonly List<SelfCheckItem> Items = new List<SelfCheckItem>();

        public int Total;       // 实际检查数（run+open 且可判）
        public int Exists;
        public int Missing;
        public int Unknown;
        public int Skipped;

        public void Tally()
        {
            Total = Exists = Missing = Unknown = Skipped = 0;
            foreach (var it in Items)
            {
                switch (it.Status)
                {
                    case CheckStatus.Ok: Exists++; Total++; break;
                    case CheckStatus.Missing: Missing++; Total++; break;
                    case CheckStatus.Unknown: Unknown++; Total++; break;
                    case CheckStatus.Skipped: Skipped++; break;
                }
            }
        }

        /// <summary>未安装/待确认清单（窗口与命令清单的取材源）。</summary>
        public List<SelfCheckItem> Problems()
        {
            var l = new List<SelfCheckItem>();
            foreach (var it in Items)
                if (it.Status == CheckStatus.Missing || it.Status == CheckStatus.Unknown) l.Add(it);
            return l;
        }

        public string SummaryLine()
        {
            return "检查=" + Total + " 已安装=" + Exists + " 未找到(已确认)=" + Missing
                + " 未找到(待确认)=" + Unknown + " 跳过=" + Skipped
                + " 耗时=" + ElapsedMs + "ms 后端=" + (Backend != null ? Backend.KindText : "?");
        }

        /// <summary>
        /// 「上次检查于 HH:mm，结果：可用 N / 未找到 M」（B4b-9）。
        /// ★ 只给**时间 + 两个数**，是留痕用的最小信息；完整人话结论见 B5 的 `HumanSummary()`。
        /// </summary>
        public string LastCheckLine()
        {
            return "上次检查于 " + FinishedAt.ToString("HH:mm")
                + "，结果：可用 " + Exists + " / 未找到 " + (Missing + Unknown);
        }

        // ============================================================
        // B5 · 人话层（气泡 / 窗口用）
        //
        // 纪律：**这一层里不许出现** backend= / dbLoaded= / lastError= / 层级= /
        //   包名已核实 / probe= 这类机器字段 —— 那些只允许出现在「明细（折叠）」与
        //   报告文件里（`Render()` 保持原样，脚本仍可解析）。
        // ============================================================

        /// <summary>后端是否可信（EV 直连 + 探针有命中 + 索引已加载）。不可信 ⇒ 不许说"确实没有"。</summary>
        public bool BackendTrusted()
        {
            return Backend != null
                && Backend.Kind == BackendKind.Everything
                && Backend.ProbeHits > 0
                && Backend.DbLoaded;
        }

        /// <summary>搜索后端的一句话状态（人话，可给气泡/窗口）。</summary>
        public string BackendHumanLine()
        {
            if (Backend == null) return "搜索后端：未知（这次结果不可信）";

            if (Backend.Kind == BackendKind.Everything)
            {
                if (Backend.DbLoaded && Backend.ProbeHits > 0)
                    return "搜索正常（Everything 直连"
                        + (Backend.ProbeMs >= 0 ? "，" + Backend.ProbeMs + "ms" : "") + "）";
                return "Everything 已连上，但索引还没就绪（可能在重建索引），这次结果不可信";
            }
            if (Backend.Kind == BackendKind.Es)
                return "搜索降级：Everything 没连上，改用 es.exe 兜底，结果可能不全";
            return "搜索后端不可用（Everything 没开或找不到 Everything64.dll）";
        }

        /// <summary>B5-1：一句人话结论（三选一）。</summary>
        public string HumanConclusion()
        {
            if (!BackendTrusted())
                return "搜索后端异常，这次结果不可信 —— 先点「检查搜索后端」";

            int notFound = Missing + Unknown;
            if (notFound == 0)
                return "检查了 " + Total + " 项，全部可用，可以放心用";
            return Total + " 项里 " + notFound + " 项没找到（已在下面列好，附安装命令）";
        }

        /// <summary>
        /// 气泡用的短文案：结论 + 后端一句话 + 跳过说明（没有就用不到）。
        /// ★ 给脚本/日志的仍然是 `SummaryLine()`（字段名不变，B5-7 双文案并存）。
        /// </summary>
        public string HumanSummary()
        {
            var sb = new StringBuilder();
            sb.Append(HumanConclusion());
            sb.Append('\n').Append(BackendHumanLine());
            if (Skipped > 0) sb.Append('\n').Append(SkipExplanation());
            return sb.ToString();
        }

        /// <summary>B5-5：跳过项归类成人话（不是含糊的"跳过 9 项"）。</summary>
        public string SkipExplanation()
        {
            if (Skipped <= 0) return "";

            int notProgram = 0, placeholder = 0, other = 0;
            foreach (var it in Items)
            {
                if (it.Status != CheckStatus.Skipped) continue;
                string n = it.Note ?? "";
                if (n.IndexOf("占位符", StringComparison.Ordinal) >= 0) placeholder++;
                else if (n.IndexOf("类型不参与", StringComparison.Ordinal) >= 0) notProgram++;
                else other++;
            }

            var parts = new List<string>();
            if (notProgram > 0) parts.Add(notProgram + " 项不是程序（网址/按键/发送文本）");
            if (placeholder > 0) parts.Add(placeholder + " 项含运行时占位符（运行前才知道路径）");
            if (other > 0) parts.Add(other + " 项目标为空或类型不需要判断");

            return "跳过 " + Skipped + " 项：" + string.Join("、", parts.ToArray())
                + "，都不需要检查「存在性」，不影响上面的结论";
        }

        /// <summary>
        /// B5-2 第二段「需要你处理的」：一条 = 显示名 + 这是哪个软件/建议怎么装。
        /// </summary>
        public string HumanProblemLine(SelfCheckItem it, out bool isConfirmedMissing)
        {
            isConfirmedMissing = (it.Status == CheckStatus.Missing);

            string title = FirstNonEmpty(it.Title, it.Prog, it.Target, "(未命名条目)");
            string prog = FirstNonEmpty(it.Prog, it.Target, "");
            string from = string.IsNullOrEmpty(it.Group) ? "" : "（来自分组「" + it.Group + "」）";

            var sb = new StringBuilder();
            sb.Append(title);
            if (prog.Length > 0 && prog != title) sb.Append("  —  ").Append(prog);
            sb.Append(from).Append('\n');

            if (isConfirmedMissing)
            {
                if (it.Package.Length > 0)
                    sb.Append("    这台机器上确实没装。可用 choco 安装：choco install ")
                      .Append(it.Package).Append(" -y");
                else if (it.Note != null && it.Note.IndexOf("系统自带", StringComparison.Ordinal) >= 0)
                    sb.Append("    这是 Windows 自带程序，找不到通常说明系统文件缺失（不建议用 choco 补）");
                else
                    sb.Append("    这台机器上确实没装；不知道该对应哪个安装包，可先搜：choco search ")
                      .Append(SearchTerm(it));
            }
            else
            {
                sb.Append("    查不到，但「不代表没装」（搜索后端异常 / 便携版不在索引里）");
            }
            return sb.ToString();
        }

        private static string FirstNonEmpty(params string[] vals)
        {
            foreach (var v in vals)
                if (!string.IsNullOrEmpty(v) && v.Trim().Length > 0) return v;
            return "";
        }

        /// <summary>
        /// 可直接粘贴执行的 choco 命令 —— ★ **只对"已确认缺失"（Status=Missing）的条目成行**。
        ///
        /// 为什么：后端降级时"搜不到"≠"没装"，那种条目是 Unknown（待确认）；
        ///   给它一条可执行的 `choco install` 等于把不确定的结论变成一条待跑命令
        ///   （守门 P1：降级态 .bat 里必须一条 active 安装行都没有）。
        /// </summary>
        public List<string> ChocoCommands()
        {
            var lines = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in Problems())
            {
                if (it.Status != CheckStatus.Missing) continue;      // ★ 待确认不生成可执行命令
                if (it.Package.Length == 0) continue;
                if (!seen.Add(it.Package)) continue;
                lines.Add("choco install " + it.Package + " -y");
            }
            return lines;
        }

        /// <summary>
        /// 导出 .bat 文本。
        /// ★ 全 ASCII 注释：中文标题只出现在窗口/txt 里 —— .bat 在 cmd 下按 OEM 代码页解析，
        ///   UTF-8 中文注释会变乱码甚至被当成命令（bat-encoding-fix 的既有教训）。
        /// ★ 未确认包名一律 **REM 注释掉**，绝不出现 `choco install 待确认`。
        /// </summary>
        public string BuildBat()
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("REM RunEver self-check -> chocolatey install list");
            sb.AppendLine("REM generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("REM NOTE: review before running. Nothing is installed automatically.");
            sb.AppendLine("REM checked=" + Total + " installed=" + Exists
                + " missing_confirmed=" + Missing + " missing_unconfirmed=" + Unknown
                + " skipped=" + Skipped + " backend=" + (Backend != null ? Backend.KindText : "?"));
            if (Backend != null && Backend.Degraded)
                sb.AppendLine("REM WARNING: search backend degraded -> all not-found entries are UNCONFIRMED.");
            sb.AppendLine();

            List<string> cmds = ChocoCommands();
            if (cmds.Count == 0)
            {
                sb.AppendLine("REM (no confirmed-missing entries with verified package names)");
            }
            else
            {
                sb.AppendLine("REM --- confirmed MISSING, verified package names (" + cmds.Count + ") ---");
                foreach (string c in cmds) sb.AppendLine(c);
            }

            sb.AppendLine();
            sb.AppendLine("REM --- NOT confirmed missing: review / search first, do NOT guess ---");
            foreach (var it in Problems())
            {
                if (it.Status == CheckStatus.Missing) continue;     // 上面已处理
                string prog = AsciiOnly(it.Prog.Length > 0 ? it.Prog : it.Target);

                if (it.Package.Length > 0)
                {
                    // 降级态：包名虽已核实，但"是否真缺"未经确认 ⇒ 一律注释掉；
                    // 同时给 choco search 复核提示（用户可自行验证再决定装不装）
                    sb.AppendLine("REM " + prog + "  ->  choco search " + AsciiOnly(SearchTerm(it))
                        + "   ; UNCONFIRMED (not confirmed missing; backend degraded?)"
                        + " candidate: choco install " + it.Package + " -y");
                }
                else
                {
                    sb.AppendLine("REM " + prog + "  ->  choco search " + AsciiOnly(SearchTerm(it)));
                }
            }

            sb.AppendLine();
            sb.AppendLine("pause");
            return sb.ToString();
        }

        /// <summary>choco search 用的检索词（去扩展名；`.exe` 会干扰 choco 检索）。</summary>
        public static string SearchTerm(SelfCheckItem it)
        {
            string n = it.Prog.Length > 0 ? it.Prog : it.Target;
            n = Path.GetFileName(n.Trim());
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 4);
            return n;
        }

        /// <summary>可读文本报告（--selftest-selfcheck / 窗口"复制命令"用）。</summary>
        public string Render()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== RunEver 全盘自检 ===");
            sb.AppendLine("root = " + Root);
            sb.AppendLine("后端 = " + (Backend != null ? Backend.Describe() : "<null>"));
            sb.AppendLine(SummaryLine());
            sb.AppendLine();
            foreach (var it in Items)
            {
                sb.AppendLine("[" + SelfCheckItem.StatusText(it.Status) + "] " + it.Kind + " " + it.Prog
                    + " | 标题=" + it.Title
                    + " | 来源=" + it.Group
                    + " | 层级=" + (it.Tier.Length > 0 ? it.Tier : "-")
                    + " | 包名=" + (it.Package.Length > 0
                        ? it.Package
                        : (it.Status == CheckStatus.Missing || it.Status == CheckStatus.Unknown
                            ? "待确认" : "-"))
                    + (it.Note.Length > 0 ? " | " + it.Note : ""));
            }
            sb.AppendLine();
            sb.AppendLine("--- choco 命令清单 ---");
            var cmds = ChocoCommands();
            if (cmds.Count == 0) sb.AppendLine("(无已确认包名)");
            foreach (string c in cmds) sb.AppendLine(c);
            return sb.ToString();
        }

        private static string AsciiOnly(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(c < 128 ? c : '?');
            return sb.ToString();
        }
    }

    /// <summary>自检引擎（纯逻辑，无 UI；调用方负责放后台线程）。</summary>
    public static class SelfCheck
    {
        /// <summary>写回 cache\paths.txt 时与托盘"刷新路径缓存"互斥用的锁。</summary>
        public static readonly object CacheLock = new object();

        /// <summary>运行时占位符（命中即"运行前不可知" ⇒ 跳过，禁误杀）。</summary>
        public static readonly string[] Placeholders =
        { "%file%", "%text%", "%clip%", "%win%", "%proc%", "%s%" };

        /// <summary>
        /// 跑一次全盘自检。
        /// </summary>
        /// <param name="root">RunEver 根目录。</param>
        /// <param name="log">日志回调（可 null）。</param>
        /// <param name="progress">进度回调 (done,total)（可 null）。</param>
        /// <param name="cancel">取消判定（返回 true 即中断；可 null）。</param>
        /// <param name="writeBackCache">true = 把解析到的路径并入 cache\paths.txt（验收 B5）。</param>
        public static SelfCheckReport Run(string root, Action<string> log,
            Action<int, int> progress, Func<bool> cancel, bool writeBackCache)
        {
            if (log == null) log = delegate (string s) { };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var rep = new SelfCheckReport();
            rep.Root = root;

            rep.Backend = BackendResolver.Resolve(root, true);
            log("自检后端：" + rep.Backend.Describe());
            if (rep.Backend.Degraded)
                log("⚠ 后端已降级（" + rep.Backend.KindText + "）⇒ 所有未命中项只能标「待确认」");

            // ---- 读配置 ----
            var groups = new List<MenuGroup>();
            try
            {
                var p = new ConfParser(Path.Combine(root, "config", "menus.conf"));
                p.Parse();
                groups = p.Groups;
            }
            catch (Exception ex)
            {
                log("menus.conf 解析失败: " + ex.Message);
            }

            var map = ChocoMap.Load(root);
            log("choco 映射表条目=" + map.Count);

            // ---- 解析器（EV 注入；后端降级时 ISearchBackend=null ⇒ 退回 es 行为）----
            string cachePath = Path.Combine(root, "cache", "paths.txt");
            var cache = new PathCache(cachePath);
            string esPath = PathCacheBuilder.ResolveEsPath(
                PathCacheBuilder.ReadEsPathFromConfig(BackendResolver.AutoConfPath(root)));
            ISearchBackend backend = BackendResolver.MakeBackend(rep.Backend);
            var builder = new PathCacheBuilder(cache, esPath, log, backend);
            log("搜索级：" + (backend != null ? backend.Name : (builder.EsAvailable ? "es.exe" : "无（仅 PATH+App Paths）")));

            // ---- 枚举待检条目 ----
            var pending = new List<SelfCheckItem>();
            int notCheckable = 0;
            foreach (var g in groups)
            {
                foreach (var it in g.Items)
                {
                    if (it.Type == ActionType.Run || it.Type == ActionType.Open)
                    {
                        var row = new SelfCheckItem();
                        row.Kind = it.Type == ActionType.Run ? "run" : "open";
                        row.Title = it.Title;
                        row.Group = g.Name;
                        row.Target = it.Target ?? "";
                        pending.Add(row);
                    }
                    else if (it.Type != ActionType.Group)
                    {
                        // url / send / keys / action：**不参与判定**（A4/A5），但仍以「跳过」行列出，
                        // 让"共检查 N 条、跳过 M 条"有出处可查，而不是悄悄消失。
                        var row = new SelfCheckItem();
                        row.Kind = TypeName(it.Type);
                        row.Title = it.Title;
                        row.Group = g.Name;
                        row.Target = it.Target ?? "";
                        row.Status = CheckStatus.Skipped;
                        row.Note = "类型不参与判定（不检查存在性）";
                        pending.Add(row);
                        notCheckable++;
                    }
                }
            }
            log("待检条目=" + pending.Count + "（其中 " + notCheckable + " 条类型不参与判定，列为「跳过」）");

            // ---- 逐条判定 ----
            bool evTrusted = rep.Backend.Kind == BackendKind.Everything
                && rep.Backend.ProbeHits > 0 && rep.Backend.DbLoaded;
            int done = 0;
            foreach (var row in pending)
            {
                if (cancel != null && cancel()) { log("自检被取消（已完成 " + done + "/" + pending.Count + "）"); break; }
                CheckOne(row, cache, builder, map, evTrusted);
                rep.Items.Add(row);
                done++;
                if (progress != null)
                {
                    try { progress(done, pending.Count); } catch { }
                }
            }
            rep.Tally();

            // ---- B5：写回路径缓存 ----
            if (writeBackCache && !(cancel != null && cancel()))
            {
                rep.CacheWriteBackNote = WriteBack(cache, rep);
                log("路径缓存写回：" + rep.CacheWriteBackNote);
            }

            sw.Stop();
            rep.ElapsedMs = sw.ElapsedMilliseconds;
            rep.FinishedAt = DateTime.Now;
            rep.Tally();
            log("自检结束：" + rep.SummaryLine());
            return rep;
        }

        // ---- 单条判定 ----
        /// <summary>动作类型 → 报告里的 Kind 文本。</summary>
        private static string TypeName(ActionType t)
        {
            switch (t)
            {
                case ActionType.Run: return "run";
                case ActionType.Open: return "open";
                case ActionType.Url: return "url";
                case ActionType.Send: return "send";
                case ActionType.Keys: return "keys";
                case ActionType.Action: return "action";
                default: return "group";
            }
        }

        private static void CheckOne(SelfCheckItem row, PathCache cache,
            PathCacheBuilder builder, ChocoMap map, bool evTrusted)
        {
            // ★ 先挡一道：只有 run / open 参与判定。url/send/keys 已在枚举阶段标为「跳过」，
            //   若放它们进判定分支，会把 `http://example.com/` 当成程序名去解析 ⇒ 虚报缺件。
            if (row.Kind != "run" && row.Kind != "open") return;

            string target = (row.Target ?? "").Trim();

            if (row.Kind == "open")
            {
                row.Prog = "";
                if (HasPlaceholder(target))
                {
                    row.Status = CheckStatus.Skipped;
                    row.Note = "含运行时占位符（运行前不可知，不判）";
                    return;
                }
                string path = StripQuotes(target);
                try { path = Environment.ExpandEnvironmentVariables(path); } catch { }
                bool exists = false;
                try { exists = File.Exists(path) || Directory.Exists(path); } catch { }
                row.Path = exists ? path : "";
                row.Tier = exists ? "fs" : "-";
                row.Status = exists ? CheckStatus.Ok : NotDecided(evTrusted);
                if (!exists)
                    row.Note = StatusHint(row.Status) + "（open 目标路径）";
                return;
            }

            // run：首个 token = 程序名
            string prog = FirstToken(target);
            row.Prog = prog;
            if (prog.Length == 0 || HasPlaceholder(prog))
            {
                row.Status = CheckStatus.Skipped;
                row.Note = prog.Length == 0 ? "目标为空" : "含运行时占位符（运行前不可知，不判）";
                return;
            }

            string p;
            // ① 路径缓存（不带任何外部 IO）
            p = cache.Get(prog);
            if (!string.IsNullOrEmpty(p) && FileExistsSafe(p))
            {
                row.Path = p; row.Tier = "cache"; row.Status = CheckStatus.Ok; return;
            }

            // ② 完整解析链：App Paths → PATH → 搜索（EV/es）
            ResolveResult r;
            try { r = builder.ResolveOneEx(prog); }
            catch (Exception) { r = new ResolveResult { Name = prog, Tier = ResolveTier.None }; }

            if (!string.IsNullOrEmpty(r.Path))
            {
                row.Path = r.Path;
                row.Tier = r.Tier.ToString();
                row.Status = CheckStatus.Ok;
                return;
            }

            row.Status = NotDecided(evTrusted);
            FillPackage(row, map);
        }

        /// <summary>未命中的保守定级：EV 可信才敢说"已确认"，否则一律"待确认"。</summary>
        private static CheckStatus NotDecided(bool evTrusted)
        {
            return evTrusted ? CheckStatus.Missing : CheckStatus.Unknown;
        }

        /// <summary>未命中备注文案（降级态禁止说"未安装"）。</summary>
        private static string StatusHint(CheckStatus s)
        {
            return s == CheckStatus.Missing
                ? "路径不存在（EV 已确认）"
                : "待确认（搜索后端不可用/降级，无法断言不存在）";
        }

        /// <summary>填建议包名 / 备注。系统自带程序不进映射表（"缺失"是系统异常，choco 不是解法）。</summary>
        private static void FillPackage(SelfCheckItem row, ChocoMap map)
        {
            string prog = row.Prog.ToLowerInvariant();
            if (IsSystemBuiltin(prog))
            {
                row.Package = "";
                row.Note = "系统自带程序，缺失属系统异常（不提供 choco 命令）";
                return;
            }
            string pkg = map.Lookup(row.Prog);
            if (pkg.Length > 0)
            {
                row.Package = pkg;
                row.Note = "包名已核实";
            }
            else
            {
                row.Package = "";
                row.Note = "包名待确认 —— 先跑：choco search " + SelfCheckReport.SearchTerm(row);
            }
        }

        /// <summary>
        /// 系统自带程序（证据 state\v6-choco-evidence.txt：不进映射表）。
        /// ★ notepad.exe 也在内：它是 Windows 自带，且 choco 的 notepadplusplus
        ///   是 **另一个软件**（不提供 notepad.exe），映射过去等于给错建议（守门 P3）。
        /// </summary>
        public static bool IsSystemBuiltin(string prog)
        {
            switch ((prog ?? "").ToLowerInvariant())
            {
                case "mspaint.exe":
                case "taskmgr.exe":
                case "calc.exe":
                case "explorer.exe":
                case "notepad.exe":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>把解析到的真实路径并入缓存并原子保存（B5）。</summary>
        private static string WriteBack(PathCache cache, SelfCheckReport rep)
        {
            lock (CacheLock)
            {
                // ★ 重新读盘再合并：托盘「刷新路径缓存」可能刚写过，直接覆盖会把它的成果丢掉
                cache.Load();
                int added = 0, changed = 0;
                foreach (var it in rep.Items)
                {
                    if (it.Status != CheckStatus.Ok) continue;
                    if (it.Kind != "run" || it.Prog.Length == 0 || it.Path.Length == 0) continue;
                    if (string.Equals(it.Tier, "cache", StringComparison.OrdinalIgnoreCase)) continue;
                    string old = cache.Get(it.Prog);
                    if (string.Equals(old, it.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (old.Length == 0) added++; else changed++;
                    // 键统一小写：与 PathCacheBuilder.Build 写出的键格式保持一致（查表本身不区分大小写）
                    cache.Set(it.Prog.ToLowerInvariant(), it.Path);
                }
                if (added + changed == 0) return "无需更新（缓存已是最新）";
                cache.Save();

                // 回读校验（写盘纪律：写完必须能读回来）
                var verify = new PathCache(Path.Combine(rep.Root, "cache", "paths.txt"));
                if (verify.Count == 0) return "写入后回读为空（写盘可能失败）！added=" + added;
                rep.CacheWriteBackCount = added + changed;
                return "新增=" + added + " 更新=" + changed + " 缓存条目=" + verify.Count;
            }
        }

        // ---- 小工具 ----
        /// <summary>目标串首个 token（支持双引号包裹的带空格路径）。</summary>
        public static string FirstToken(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim();
            if (t.Length == 0) return "";
            if (t[0] == '"')
            {
                int e = t.IndexOf('"', 1);
                return StripQuotes(e > 1 ? t.Substring(0, e + 1) : t);
            }
            int sp = t.IndexOfAny(new[] { ' ', '\t' });
            return sp > 0 ? t.Substring(0, sp) : t;
        }

        public static string StripQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string v = s.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') v = v.Substring(1, v.Length - 2);
            return v;
        }

        /// <summary>含任何一个运行时占位符 ⇒ 判不了。</summary>
        public static bool HasPlaceholder(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.IndexOf('%') >= 0;
        }

        private static bool FileExistsSafe(string p)
        {
            try { return File.Exists(p); } catch { return false; }
        }
    }
}
