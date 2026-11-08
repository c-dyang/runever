// ============================================================
// RunEver 路径缓存构建器（PathCacheBuilder）
// 等价移植 src\PathCache.ahk 的「批量构建」部分：
//   解析顺序 ① App Paths 注册表 → ② PATH 语义 → ③ Everything（EV SDK 优先）
//   ★ es.exe 不是必经环节：它只是 EV SDK 不可用时的**最后兜底**，
//     日志里的 `es兜底=不可用` 表示本机没装 es.exe，不代表解析失败。
//
// 为什么单独成一个 Builder 而不塞进 PathCache：
//   PathCache 是「缓存读写 + 单点解析」的纯数据结构，构建要跑外部进程（es.exe）、
//   要能放后台线程跑、要回调日志、要能在 es.exe 缺失时降级 —— 这些副作用不该污染缓存读写。
//   （PathCache.Load/Get/Set/Save 保持既有行为不动。）
//
// 线程模型：Build 是同步阻塞方法（可能数十秒），调用方（托盘）自行放后台线程；
//   内部不创建线程、不加锁 —— 锁由 PathCache 自身／调用方保证单实例构建。
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace RunEverCs.Services
{
    /// <summary>三级解析来源标注（供验收/排障，对照 AHK ResolveOneEx 的 tier）。</summary>
    public enum ResolveTier
    {
        None = 0,
        AppPaths,
        Path,
        Everything
    }

    /// <summary>单个程序名的解析结果（路径 + 来源）。</summary>
    public sealed class ResolveResult
    {
        public string Name { get; set; }        // 小写程序名
        public string Path { get; set; }        // 全路径，空 = 未解析到
        public ResolveTier Tier { get; set; }

        public ResolveResult()
        {
            Name = "";
            Path = "";
            Tier = ResolveTier.None;
        }
    }

    /// <summary>批量构建统计（写日志用）。</summary>
    public sealed class BuildStats
    {
        public int Resolved;                     // 本次新解析成功
        public int Failed;                       // 本次解析失败
        public int Skipped;                      // 已缓存且文件仍存在 → 跳过
        public int Excluded;                     // 命中硬排除（仅计数，供排障）
        public bool EsAvailable;                 // es.exe 是否可用（false = 已降级到 ①②）
        public string EsPath;                    // 实际使用的 es.exe 路径（"" = 无）

        /// <summary>
        /// 本次真正生效的**搜索后端**描述（EV / es.exe / 无搜索）。
        /// ★ 为什么单独记：EsPath 在后端是 EV 时也有值（它是降级目标），
        ///   日志只打 `es=` 会让人误以为在用 es —— 排障时是致命误导。
        /// </summary>
        public string SearchDesc = "";

        public readonly List<string> FailedList = new List<string>();
        public readonly List<ResolveResult> ResolvedList = new List<ResolveResult>();

        /// <summary>供日志/自检输出的一行摘要。</summary>
        public override string ToString()
        {
            return string.Format(
                "resolved={0} failed={1} skipped={2} excluded={3} search={4} es兜底={5}",
                Resolved, Failed, Skipped, Excluded,
                string.IsNullOrEmpty(SearchDesc) ? "?" : SearchDesc,
                EsAvailable ? "可用" : "不可用");
        }
    }

    /// <summary>
    /// 程序路径缓存构建器 —— 三级解析 + 加权择优 + 增量构建。
    /// </summary>
    public class PathCacheBuilder
    {
        // ---- 加权规则（逐条照抄 src\PathCache.ahk:44-66，禁凭印象简化）----

        /// <summary>硬排除：这些位置的文件永远不作为结果（系统备份/缓存）。AHK :45</summary>
        public static readonly string[] ExcludeFrag = {
            @"\WinSxS\", @"\servicing\", @"\Prefetch\", @"\SystemResources\"
        };

        /// <summary>
        /// 高权前缀（命中即 +100 + 序位微调）：正规安装位。AHK :48-53，逐条忠实移植。
        /// ★ 列表顺序有意义：**越靠前权重越高** —— 实际得分为
        ///   Program Files=103 &gt; Program Files (x86)=102 &gt; System32=101 &gt; Windows=100。
        ///   （AHK 原文注释写的是"System32 优先"，与它自己的代码相反；行为按代码，不改。）
        /// </summary>
        public static readonly string[] PreferPrefix = {
            @"C:\Program Files\",
            @"C:\Program Files (x86)\",
            @"C:\Windows\System32\",
            @"C:\Windows\"
        };

        /// <summary>
        /// 降权片段（命中即 -100）：临时/会话工作副本，绝不该当正式路径。AHK :58-66
        /// ★ 只放「明确错误的位置」；「同名 exe 恰好也在别处」归第②级 PATH 语义解决，
        ///   塞进来就是过拟合（AHK 原文的实测教训）。
        /// </summary>
        public static readonly string[] PenalizeFrag = {
            @"\AppData\Local\Temp\",
            @"\Windows\Temp\",
            @"\TempSave\",
            @"\.dsh\sessions\",
            @"\SysWOW64\",
            @"\chocolatey\tools\",          // choco 内部解包工具，非用户的 7z
            @"\chocolatey\lib\"             // choco 包载荷（版本化目录）
        };

        /// <summary>AHK :32 记录的可靠 es.exe 路径 —— 作为探测候选之一（非唯一来源）。
        /// ★ 不写死用户名：按当前用户目录拼（换机/换用户名都成立）。</summary>
        public static string DefaultEsPathAhk { get { return EsInUserProfile(); } }

        /// <summary>当前用户目录下的 es.exe 常见位置（取不到返回空串，候选列表继续试）。</summary>
        private static string EsInUserProfile()
        {
            try
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    @".workbuddy\binaries\es.exe");
            }
            catch { return ""; }
        }

        /// <summary>es.exe 单次调用超时（ms）。es 走 Everything IPC，正常 &lt;1s；
        /// 超时说明索引/服务异常，宁可降级也别把托盘线程挂死。</summary>
        public const int EsTimeoutMs = 8000;

        /// <summary>探测 es.exe 的常见位置（配置文件未指定时按序试）。</summary>
        public static readonly string[] EsCandidates = {
            EsInUserProfile(),        // AHK :32 记录的路径
            @"C:\Program Files\Everything\es.exe",
            @"C:\Program Files (x86)\Everything\es.exe",
            @"C:\Program Files\Everything 1.5a\es.exe",
            @"C:\Program Files (x86)\Everything 1.5a\es.exe"
        };

        private readonly PathCache _cache;
        private readonly string _esPath;
        private readonly Action<string> _log;

        /// <summary>
        /// 可选的搜索后端（EV）。**null = 完全不注入 = 保持既有 es.exe 行为**（零回归）。
        /// 注入后搜索级顺序为「先 EV，EV 查不到再回 es」（见 TryQuerySearch）。
        /// </summary>
        private readonly ISearchBackend _searchBackend;

        /// <param name="cache">目标缓存（已 Load）。</param>
        /// <param name="esPath">
        /// es.exe 路径；null/空 = 自动探测（候选位置 + PATH）。传不存在的路径 = 显式降级（供自检用）。
        /// </param>
        /// <param name="log">日志回调，可为 null（后台线程调用时不弹 UI，只写日志）。</param>
        public PathCacheBuilder(PathCache cache, string esPath, Action<string> log)
        {
            _cache = cache;
            _esPath = ResolveEsPath(esPath);
            _log = log;
        }

        /// <summary>带搜索后端注入的重载（功能 B：EV 直连）。不传后端时行为与三参构造**逐字节相同**。</summary>
        public PathCacheBuilder(PathCache cache, string esPath, Action<string> log,
            ISearchBackend searchBackend)
            : this(cache, esPath, log)
        {
            _searchBackend = searchBackend;
        }

        /// <summary>
        /// 测试/降级专用重载：`disableEsProbe = true` ⇒ **彻底不用 es.exe**
        /// （跳过自动探测，`EsAvailable=false`），用于造"搜索后端全不可用 ⇒ Unknown"的场景。
        /// 生产路径不用这个重载（默认行为 = 探测 es）。
        /// </summary>
        public PathCacheBuilder(PathCache cache, string esPath, Action<string> log,
            ISearchBackend searchBackend, bool disableEsProbe)
            : this(cache, esPath, log, searchBackend)
        {
            if (disableEsProbe) _esPath = "";
        }

        /// <summary>实际生效的搜索后端（null = 未注入，走 es）。</summary>
        public ISearchBackend SearchBackend { get { return _searchBackend; } }

        /// <summary>③ 搜索级是否可用（EV 或 es 任一可用，否则跳过搜索级）。</summary>
        public bool SearchAvailable
        {
            get
            {
                if (_searchBackend != null && _searchBackend.Available) return true;
                return EsAvailable;
            }
        }

        /// <summary>实际生效的 es.exe 路径（"" = 不可用，已降级到 ①②级）。</summary>
        public string EsPath { get { return _esPath; } }

        /// <summary>es.exe 是否可用。</summary>
        public bool EsAvailable { get { return !string.IsNullOrEmpty(_esPath); } }

        private void Log(string msg)
        {
            if (_log != null)
            {
                try { _log(msg); } catch { /* 日志回调本身出错不能影响构建 */ }
            }
        }

        // ============================================================
        // es.exe 定位（配置化：auto.conf 的 espath= > 候选探测 > PATH）
        // ============================================================
        /// <summary>
        /// 定位 es.exe。三级：显式配置 → 候选路径 → PATH 扫描。
        /// ★ 全部失败返回 ""（不抛）—— 调用方降级到 ①② 级，不能让缺 es 变成崩溃。
        /// </summary>
        public static string ResolveEsPath(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string c = configured.Trim().Trim('"');
                if (File.Exists(c)) return c;
                // 配置写错了也不静默当没事：继续探测，但由调用方日志体现
            }

            foreach (var cand in EsCandidates)
            {
                try { if (File.Exists(cand)) return cand; } catch { }
            }

            // PATH 里找（用户可能把 Everything 目录加进了 PATH）
            try
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in path.Split(';'))
                {
                    string d = dir.Trim().Trim('"');
                    if (d.Length == 0 || d.IndexOf('%') >= 0) continue;
                    try
                    {
                        string full = Path.Combine(d, "es.exe");
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            catch { }

            return "";
        }

        /// <summary>从 auto.conf 目录树读取 espath= 配置（没有则返回 ""）。</summary>
        public static string ReadEsPathFromConfig(string autoConfPath)
        {
            try
            {
                if (!File.Exists(autoConfPath)) return "";
                foreach (string raw in File.ReadAllLines(autoConfPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    if (k != "espath") continue;
                    return line.Substring(eq + 1).Trim().Trim('"');
                }
            }
            catch { }
            return "";
        }
        // ============================================================
        // 三级解析（对照 AHK :193-210 的 ResolveOneEx）
        // ============================================================
        /// <summary>
        /// 三级解析：① App Paths ② PATH 语义 ③ es 搜索+加权。
        /// 顺序不可换 —— ①② 是 Windows 自身的语义，③ 只是兜底启发式。
        /// </summary>
        public ResolveResult ResolveOneEx(string progName)
        {
            var r = new ResolveResult { Name = (progName ?? "").ToLowerInvariant() };
            if (string.IsNullOrWhiteSpace(progName)) return r;

            string p;

            p = ResolveViaAppPaths(progName);
            if (p.Length > 0) { r.Path = p; r.Tier = ResolveTier.AppPaths; return r; }

            p = ResolveViaPath(progName);
            if (p.Length > 0) { r.Path = p; r.Tier = ResolveTier.Path; return r; }

            // ③ 兜底：搜索级不可用（未装/未配置/后端降级）时直接判失败，绝不抛
            if (SearchAvailable)
            {
                p = ResolveViaEverything(progName);
                if (p.Length > 0) { r.Path = p; r.Tier = ResolveTier.Everything; return r; }
            }

            r.Tier = ResolveTier.None;
            return r;
        }

        /// <summary>只要路径（对照 AHK ResolveOne）。</summary>
        public string ResolveOne(string progName)
        {
            return ResolveOneEx(progName).Path;
        }

        // ---- ① App Paths 注册表（AHK :216-233）----
        /// <summary>
        /// Windows 的 Start→Run 对话框就用这个键解析程序名。
        /// 值：默认值 = exe 全路径（Path 值 = 工作目录，本模块不需要）。
        /// ★ AHK 版手写 WOW6432Node 路径；C# 侧改用 RegistryView —— 语义等价且不受进程位数影响。
        /// </summary>
        public static string ResolveViaAppPaths(string progName)
        {
            string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + progName;
            // 顺序对齐 AHK：HKCU → HKLM(64) → HKLM(32)
            // ★ 显式给每项标 RegistryHive 类型 —— 数组元素是匿名类型，用隐式常量会有重载歧义
            var probes = new[]
            {
                new { Hive = RegistryHive.CurrentUser, View = RegistryView.Registry64 },
                new { Hive = RegistryHive.LocalMachine, View = RegistryView.Registry64 },
                new { Hive = RegistryHive.LocalMachine, View = RegistryView.Registry32 }
            };

            foreach (var pr in probes)
            {
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(pr.Hive, pr.View))
                    using (var k = baseKey.OpenSubKey(sub))
                    {
                        if (k == null) continue;                 // 键不存在是常态，继续
                        string v = k.GetValue("") as string;
                        if (!string.IsNullOrEmpty(v) && File.Exists(v)) return v;
                    }
                }
                catch { }
            }
            return "";
        }

        // ---- ② PATH 语义（AHK :239-265）----
        /// <summary>
        /// 复刻 Windows 解析顺序：System32 → Windows 目录 → %PATH% 各目录按序。
        /// ★ System32 必须先于 PATH —— 这是 Windows 自身顺序，也是修正
        ///   「SysWOW64 抢答」的关键（System32 才是 64 位正主）。
        /// </summary>
        public static string ResolveViaPath(string progName)
        {
            // 系统目录（★ 顺序不可调换）
            string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] sysDirs = { Path.Combine(winDir, "System32"), winDir };

            foreach (var probeName in NameVariants(progName))
            {
                foreach (var dir in sysDirs)
                {
                    string full = Path.Combine(dir, probeName);
                    try { if (File.Exists(full)) return full; } catch { }
                }

                string path;
                try { path = Environment.GetEnvironmentVariable("PATH") ?? ""; }
                catch { path = ""; }
                if (path.Length == 0) continue;

                foreach (var raw in path.Split(';'))
                {
                    string d = raw.Trim().Trim(' ', '\t', '"');
                    if (d.Length == 0) continue;
                    // 未展开的变量条目（如 %SOMEVAR%\bin）跳过，避免误判（AHK :257-259）
                    if (d.IndexOf('%') >= 0) continue;
                    try
                    {
                        string full = Path.Combine(d.TrimEnd('\\'), probeName);
                        if (File.Exists(full)) return full;
                    }
                    catch { }
                }
            }
            return "";
        }

        /// <summary>
        /// 程序名变体：AHK 侧配置里写的是 "calc.exe" 这类带扩展名的名字（原样当文件名查），
        /// 但用户也可能写 "calc" —— 补 .exe 再试一遍即可，两种写法都能解析。
        /// </summary>
        private static IEnumerable<string> NameVariants(string progName)
        {
            yield return progName;
            if (!progName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && progName.IndexOf('.') < 0)          // 已有其它扩展名（.bat/.cmd）才不补 .exe
            {
                yield return progName + ".exe";
                yield return progName + ".bat";
                yield return progName + ".cmd";
            }
        }

        // ---- ③ 搜索 + 加权兜底（AHK :269-314；后端可为 EV 直连或 es.exe）----
        private string ResolveViaEverything(string progName)
        {
            var all = new List<string>();

            foreach (var probeName in NameVariants(progName))
            {
                List<string> cands;
                if (TryQuerySearch(probeName, out cands))
                    all.AddRange(cands);

                // 变体一已命中且名字带 .exe ⇒ 不用再跑变体二（省一次查询）
                if (all.Count > 0) break;
            }

            // 过滤硬排除项（AHK :289）
            var kept = new List<string>();
            int excluded = 0;
            foreach (var c in all)
            {
                if (IsExcluded(c)) { excluded++; continue; }
                kept.Add(c);
            }
            if (excluded > 0)
                Log("  [排除] " + progName + " 丢弃 " + excluded + " 个硬排除候选");

            // 幽灵候选（搜索索引里有、盘上已不在）先剔除：
            //   ★ 契约 §4 的裁决口径是"文件创建时间降序"—— 只有**真实存在**的文件才有
            //     有意义的创建时间；不剔除的话，索引陈旧时会把一个不存在的路径写进缓存，
            //     然后执行期必然"点了没反应"。剔除后"解析结果必然存在"成为实现保证。
            var existing = new List<string>();
            int phantom = 0;
            foreach (var c in kept)
            {
                if (!FileExistsSafe(c)) { phantom++; continue; }
                existing.Add(c);
            }
            if (phantom > 0)
                Log("  [过滤] " + progName + " 丢弃 " + phantom + " 个不存在的候选（索引陈旧）");

            var best = PickBest(existing);
            return best ?? "";
        }

        /// <summary>
        /// 搜索级查询：先 EV 直连（若注入了后端），查不到再回 es.exe。
        /// ★ 未注入后端时本方法**逐字等价于** TryEsQuery —— 这是 `--selftest-pathcache` diff 为空的前提。
        /// </summary>
        private bool TryQuerySearch(string progName, out List<string> cands)
        {
            cands = new List<string>();
            if (_searchBackend != null && _searchBackend.Available)
            {
                try
                {
                    var r = _searchBackend.Search(progName, 30);
                    if (r != null && r.Count > 0) { cands = r; return true; }
                }
                catch (Exception ex)
                {
                    Log("  [警告] " + _searchBackend.Name + " 查询 " + progName + " 失败: " + ex.Message);
                }
                // EV 命中 0 条 ⇒ 回落到 es（若可用），保证不因 EV 数据缺失而少解析
            }
            if (EsAvailable) return TryEsQuery(progName, out cands);
            return false;
        }

        /// <summary>
        /// 执行一次 es.exe 查询并读回候选列表（保持 es 的时间倒序）。
        /// 返回 false = 查询失败（超时/异常/无输出文件）—— 与「查到 0 条」区分开，便于排障。
        /// </summary>
        private bool TryEsQuery(string progName, out List<string> cands)
        {
            cands = new List<string>();
            string outFile = null;
            try
            {
                outFile = Path.Combine(Path.GetTempPath(),
                    "rv_es_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".txt");

                // ★ AHK :277-278 的查询形态：-n 30 -sort date-modified-descending -export-txt <file> wfn:<name>
                //   - wfn: = Match Whole Filename（精确匹配整个文件名）
                //   - -export-txt = es 原生写文件（cmd 重定向会丢编码 → 0 字节）
                //   - 查询串不能自行加引号（AHK :179-182 实测：加了引号 es 收到字面量引号 → 不匹配）
                //   ★ net48 的 ProcessStartInfo 没有 ArgumentList（.NET Core 才有），
                //     所以用 Arguments 字符串 + 自己的 QuoteArg 逐参数引号 —— 等价于"按参数边界传"，
                //     路径含空格/中文也能正确传给 es.exe。
                var psi = new ProcessStartInfo(_esPath);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.Arguments = "-n 30 -sort date-modified-descending -export-txt "
                    + QuoteArg(outFile) + " wfn:" + QuoteArg(progName);

                using (var proc = new Process())
                {
                    proc.StartInfo = psi;
                    var sbOut = new StringBuilder();
                    var sbErr = new StringBuilder();
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) sbOut.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    if (!proc.WaitForExit(EsTimeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                        Log("  [超时] es 查询 " + progName + " 超过 " + EsTimeoutMs + "ms，已终止");
                        return false;
                    }
                    proc.WaitForExit();          // 排空异步输出缓冲

                    if (proc.ExitCode != 0)
                        Log("  [警告] es 查询 " + progName + " 退出码 " + proc.ExitCode
                            + (sbErr.Length > 0 ? "：" + sbErr.ToString().Trim() : ""));
                }

                if (!File.Exists(outFile))
                    return false;

                // AHK :284 用 UTF-8 读导出文件 —— es 的 -export-txt 是 UTF-8
                foreach (var raw in File.ReadAllLines(outFile, Encoding.UTF8))
                {
                    string line = raw.Trim().Trim(' ', '\t', '\r', '"');
                    if (line.Length == 0) continue;
                    cands.Add(line);
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("  [异常] es 查询 " + progName + " 失败: " + ex.Message);
                return false;
            }
            finally
            {
                try { if (outFile != null && File.Exists(outFile)) File.Delete(outFile); } catch { }
            }
        }

        /// <summary>
        /// 同名多命中的消解 —— **架构契约 §4 / P1 定案：按文件创建时间降序取第一条**。
        ///
        /// ★ 口径变更说明（2026-10-04，非回归）：这里原来是 `PathWeight` 加权择优
        ///   （AHK :301-312 的移植）。用户定案改为"通行操作"口径 —— 创建时间最新者胜，
        ///   **不加任何目录排除表**（契约 §4）。已知代价：本机 `7z.exe` 会解析到
        ///   `…\TempSave\autoextract-v1.2\**\7z.exe` 的测试桩（创建于 2026-09-29），
        ///   契约 :97-98 已把它写成**预期行为**。
        ///
        /// ★ `PathWeight` / `PreferPrefix` / `PenalizeFrag` / `IsExcluded` **全部保留**
        ///   （含 `--selftest-pathcache` 的五条数值断言），身份从"最终裁决"降为
        ///   "候选优先级信息"；`IsExcluded`（WinSxS / servicing / Prefetch / SystemResources）
        ///   仍在 `ResolveViaEverything` 里做**候选硬排除**，不受本次口径变更影响。
        /// ★ 取不到创建时间（文件不存在 / 无权限）的候选按 `DateTime.MinValue` 处理，
        ///   永远不抢占"真存在"的候选；全都不存在时保持**原顺序第一条**（稳定，不随机）。
        /// </summary>
        public static string PickBest(List<string> cands)
        {
            if (cands == null || cands.Count == 0) return "";
            string best = cands[0];
            DateTime bestTime = CreationTimeSafe(best);
            for (int i = 1; i < cands.Count; i++)
            {
                DateTime t = CreationTimeSafe(cands[i]);
                if (t > bestTime) { best = cands[i]; bestTime = t; }
            }
            return best;
        }

        /// <summary>文件创建时间（取不到 ⇒ DateTime.MinValue，绝不误胜）。</summary>
        private static DateTime CreationTimeSafe(string p)
        {
            try
            {
                if (string.IsNullOrEmpty(p)) return DateTime.MinValue;
                if (!File.Exists(p)) return DateTime.MinValue;
                return File.GetCreationTime(p);
            }
            catch { return DateTime.MinValue; }
        }

        /// <summary>
        /// 路径权重（越大越优先）。AHK :158-176。
        ///   高权前缀 +100 +（列表序位微调）—— 只取第一个命中的前缀
        ///   降权片段 -100（只计一次）
        /// </summary>
        public static int PathWeight(string p)
        {
            if (string.IsNullOrEmpty(p)) return 0;
            int w = 0;

            int n = PreferPrefix.Length;
            for (int i = 0; i < n; i++)
            {
                if (IndexOfIgnoreCase(p, PreferPrefix[i]))
                {
                    w += 100 + (n - (i + 1));     // 列表越靠前加分越多（Program Files > System32）
                    break;                        // 只取第一个命中的前缀
                }
            }

            foreach (var frag in PenalizeFrag)
            {
                if (IndexOfIgnoreCase(p, frag)) { w -= 100; break; }
            }

            return w;
        }

        /// <summary>是否命中硬排除（AHK :317-323）。</summary>
        public static bool IsExcluded(string p)
        {
            if (string.IsNullOrEmpty(p)) return true;
            foreach (var frag in ExcludeFrag)
                if (IndexOfIgnoreCase(p, frag)) return true;
            return false;
        }

        /// <summary>
        /// 子串匹配（不区分大小写）—— 对齐 AHK InStr 的默认行为（StringCaseSense Off）。
        /// 路径里盘符/目录大小写不一致很常见，必须忽略大小写。
        /// </summary>
        private static bool IndexOfIgnoreCase(string haystack, string needle)
        {
            return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ============================================================
        // 批量构建（对照 AHK :329-364 的 Build）
        // ============================================================
        /// <summary>
        /// 批量构建/更新缓存。
        /// </summary>
        /// <param name="progNames">程序名集合（大小写不敏感去重）。</param>
        /// <param name="force">true = 全部重查；false = 增量（已缓存且文件仍存在 ⇒ skipped）。</param>
        public BuildStats Build(IEnumerable<string> progNames, bool force)
        {
            var stats = new BuildStats
            {
                EsAvailable = EsAvailable,
                EsPath = EsPath,
                SearchDesc = (_searchBackend != null && _searchBackend.Available)
                    ? _searchBackend.Name
                    : (EsAvailable ? "es.exe（EV 降级兜底）" : "无搜索兜底（仅 App Paths + PATH）")
            };

            Log("路径缓存构建开始（" + (force ? "全量" : "增量") + "，搜索后端=" + stats.SearchDesc + "）");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawName in progNames ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(rawName)) continue;
                string name = rawName.Trim().ToLowerInvariant();
                if (!seen.Add(name)) continue;                   // 小写名去重（AHK 用 Map 天然去重）
                if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0) continue;  // 已含路径的不管

                if (!force)
                {
                    string existing = _cache.Get(name);
                    // 增量语义（AHK :335-342）：缓存命中且文件仍存在 ⇒ 跳过。
                    // PathCache.Get 不校验存在性，所以这里必须自己判 —— 否则删掉的程序永远不再重建。
                    if (!string.IsNullOrEmpty(existing) && FileExistsSafe(existing))
                    {
                        stats.Skipped++;
                        continue;
                    }
                }

                ResolveResult r;
                try
                {
                    r = ResolveOneEx(name);
                }
                catch (Exception ex)
                {
                    // 单个程序解析异常不能中断整批（AHK :344-348 的 try/catch 同理）
                    Log("  [异常] 解析 " + name + " 失败: " + ex.Message);
                    r = new ResolveResult { Name = name, Tier = ResolveTier.None };
                }

                if (!string.IsNullOrEmpty(r.Path))
                {
                    _cache.Set(name, r.Path);
                    stats.Resolved++;
                    stats.ResolvedList.Add(r);
                    Log("  [OK/" + r.Tier + "] " + name + " → " + r.Path);
                }
                else
                {
                    stats.Failed++;
                    stats.FailedList.Add(name);
                    // ★ 2026-11-07（t041875 后半截）：解析失败**必须清掉陈旧缓存**。
                    //   不然旧路径一直留着：hideMissing 会误判"本机有"（菜单照显、点下去执行失败），
                    //   增量分支也会因"缓存命中 + 旧文件还在"而永远跳过重试。
                    bool stale = _cache.Remove(name);
                    Log("  [失败] " + name + "（三级都未命中）"
                        + (stale ? "，已清除陈旧缓存" : ""));
                }
            }

            _cache.Save();
            Log("路径缓存构建结束：" + stats);
            return stats;
        }

        /// <summary>
        /// 命令行参数引号（MSVCRT 规则）：含空格/制表符/引号的参数加双引号，
        /// 内部的 " 用 \" 转义，反斜杠数按规则翻倍。
        /// ★ 为什么不自己拼裸字符串：路径含空格/中文（本机 TempSave 路径就含中文）时
        ///   不加引号会被 es.exe 当成两个参数，曾经 AHK 侧正是踩了「加引号 vs 不加引号」的坑。
        /// </summary>
        public static string QuoteArg(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.IndexOf(' ') < 0 && s.IndexOf('\t') < 0 && s.IndexOf('"') < 0) return s;

            var sb = new StringBuilder();
            sb.Append('"');
            int backslashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }
                sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 单个程序名的解析（**带来源/三态信息**的公开入口）—— 供**时机③「执行期现查」**
        /// （软件移位兜底）使用：`PathResolvePolicy.ResolveOnDemand` 在硬超时窗口内调它一次。
        /// 与批量 `Build` 共用同一份 `ResolveOneEx`，保证"现查"与"全盘扫"口径一致；
        /// 只返回路径字符串的那个 `ResolveOne` 不含 tier 信息，故这里另给一个结果型入口。
        /// </summary>
        public ResolveResult ResolveOneResult(string name)
        {
            if (string.IsNullOrEmpty(name))
                return new ResolveResult { Name = "", Tier = ResolveTier.None };

            string n = name.Trim().ToLowerInvariant();
            try
            {
                return ResolveOneEx(n);
            }
            catch (Exception ex)
            {
                // 单个名字解析异常不能把调用方（点击执行路径）炸掉
                Log("  [异常] 现查 " + n + " 失败: " + ex.Message);
                return new ResolveResult { Name = n, Tier = ResolveTier.None };
            }
        }

        /// <summary>文件存在性检查（吞异常：非法路径字符/无权限目录不应中断构建）。</summary>
        private static bool FileExistsSafe(string p)
        {
            try { return File.Exists(p); } catch { return false; }
        }

        /// <summary>
        /// 单个程序名的**候选集**（③ 搜索级拿到的原始候选，含"硬排除/幽灵"过滤**之前**的表）。
        /// 用途：自检取证 —— 断言"解析结果**位于候选集内**"（契约 §4 / C1：结果是**从候选里挑一个**，
        /// 不是凭空拼出来的路径）。抽样与 `ResolveViaEverything` 同一来源、同一变体顺序。
        /// ★ 只读：不改缓存、不写盘。
        /// </summary>
        public List<string> SearchCandidates(string name)
        {
            var all = new List<string>();
            if (string.IsNullOrWhiteSpace(name)) return all;
            try
            {
                foreach (var probeName in NameVariants(name.Trim().ToLowerInvariant()))
                {
                    List<string> cands;
                    if (TryQuerySearch(probeName, out cands)) all.AddRange(cands);
                    if (all.Count > 0) break;          // 与 ResolveViaEverything 同口径：变体一命中即止
                }
            }
            catch (Exception ex)
            {
                Log("  [异常] 候选集查询 " + name + " 失败: " + ex.Message);
            }
            return all;
        }
    }
}
