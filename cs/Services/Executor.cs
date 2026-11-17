using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 执行器 —— 执行契约（架构契约 §6）的能力矩阵。
    ///
    /// 动作类型：`run` / `open` / `url` / `action` / `send` / `keys` / `evsearch`
    ///   · run      —— 程序名 → 解析全路径 → 启动；支持命令行参数；
    ///                 缓存命中但文件不存在（**软件移位**）⇒ EV 现查一次 + 回写缓存（时机③），
    ///                 仍未命中 ⇒ **用户可见报错**（不再"点了没反应"）。
    ///   · open     —— 打开文件 / 文件夹（多目标按引号分隔）。
    ///   · url      —— 模板注入占位符后打开。
    ///   · action   —— 命令 / 脚本 + 参数；flag 控制窗口显示 / 隐藏。
    ///   · send     —— 文本发到当前输入框。
    ///   · keys     —— 模拟按键序列。
    ///   · evsearch —— 本地搜索（卡 F / 2026-11-17b）：用 Everything 语义查询目标串，
    ///                 命中结果按"打开文件"执行。后端链：Everything64.dll（首选）→ es.exe（兜底）。
    ///                 上限 = auto.conf 的 `submenuLimit`（默认 20）；
    ///                 0 命中 ⇒ `evsearch-no-result`；两后端都不可用 ⇒ `evsearch-backend-unavailable`；
    ///                 **任何情况都不静默成功、不抛异常**。
    ///
    /// ★ 占位符展开只走 `PlaceholderExpander`（唯一源）：本类的 `ReplaceVars` 只做委托，
    ///   **禁止两份替换逻辑**。
    /// ★ `run` / `action` / `open` 且**参数为空** ⇒ 自动追加选中文件（夹）路径 / 选中文本
    ///   （契约 §6，Q7 修订）；参数里写了占位符则不追加。落点在 `Execute`，
    ///   详见该方法内的规则注释。
    ///
    /// ★ action 的显示/隐藏（用户 2026-10-03 定案，两级开关）：
    ///     ① 条目 flag（menus.conf 写 `| action show xxx`）—— 最具体，优先
    ///     ② 插件 show（自动执行插件的 RVE_FN_Show）—— 批量策略
    ///     ③ 都缺省 → 隐藏（存量行为，零迁移）
    ///   显示时【等待】命令结束，否则窗口被连带关闭。
    /// </summary>
    public class Executor
    {
        public class Task
        {
            public string Type;                 // run/open/url/action/send/keys/evsearch
            public string Target;
            public string Text = "";
            public List<string> Files = new List<string>();
            public string Proc = "";
            public string WinTitle = "";
            public string Clipboard = "";
            public ActionFlag Flag = ActionFlag.None;

            /// <summary>插件级显示窗口开关（自动执行路径用）。</summary>
            public bool? Show;

            /// <summary>运行日志回调。</summary>
            public Action<string> Log = delegate { };
        }

        private readonly PathCache _cache;
        public Executor(PathCache cache) { _cache = cache; }

        /// <summary>
        /// 显示窗口时是否等待命令结束 —— 来自 auto.conf 的 `waitWhenShown`。
        ///   true（默认）= 等待：cmd 窗口留存，进度/结果/报错都看得见
        ///   false        = 不等待：立即返回，主程序零阻塞
        ///   隐藏模式一律不等待（与本开关无关）。
        /// </summary>
        public bool WaitWhenShown = true;

        /// <summary>
        /// auto.conf 的自动执行段（本类只取 `BatchMax`）—— 多选批量执行的条数上限来源。
        /// ★ 未接线（null）⇒ 按 `AutoConf.BatchMax` 的默认值 10（与配置默认一致）。
        /// ★ 接线点 = 宿主把 `AutoMatcher.Conf` 挂上来（本刀不动 RunEverApp.cs）。
        ///   ⚠ `AutoMatcher.ReadConf` 每次都 **new AutoConf** ⇒ 挂的是实例引用，conf 重载后
        ///   必须重挂（赋值点应与 `Executor.WaitWhenShown` 的镜像处同址）。
        /// </summary>
        public AutoConf Conf;

        /// <summary>批量上限（读 <see cref="Conf"/>，未接线/非法值一律回落默认 10，下限 1）。</summary>
        private int BatchLimit
        {
            get
            {
                int n = (Conf != null) ? Conf.BatchMax : 10;
                return n < 1 ? 1 : n;
            }
        }

        // ============================================================
        // evsearch 接线（卡 F / 2026-11-17b）
        // ============================================================
        /// <summary>
        /// 配置根（可选）：非空 ⇒ evsearch 从 `&lt;root&gt;\config\auto.conf` 读
        /// `submenulimit` / `espath` / `evdll`。`Conf` 已接线时以 `Conf` 为准。
        /// </summary>
        public string ConfigRoot;

        /// <summary>
        /// 注入点：Everything 直连查询（query, max）⇒ 命中全路径列表。
        /// 返回 **null = 该后端不可用**（与"0 命中 = 空表"严格区分）；抛异常按不可用处理。
        /// 未注入 ⇒ 走内置 `EverythingProbe` + `EvApi`。
        /// </summary>
        public Func<string, int, List<string>> EvQuery;

        /// <summary>注入点：es.exe 查询，语义同 <see cref="EvQuery"/>。未注入 ⇒ 真跑 es.exe。</summary>
        public Func<string, int, List<string>> EsQuery;

        /// <summary>最近一次 evsearch 实际使用的后端（"EV" / "es" / ""）。</summary>
        public string LastEvSearchBackend = "";

        /// <summary>最近一次 evsearch 的命中（已去重、已按上限截断；自检与日志共用）。</summary>
        public readonly List<string> LastEvSearchHits = new List<string>();

        /// <summary>
        /// evsearch 结果上限 —— **唯一来源** = auto.conf 的 `submenuLimit`（默认值取 `AutoConf` 本身，
        /// 不在这里另写数字）。`Conf` 未接线时读 `ConfigRoot` 的 auto.conf。
        /// </summary>
        private int SearchLimit
        {
            get
            {
                int dflt = new AutoConf().SubmenuLimit;
                if (Conf != null) return Conf.SubmenuLimit > 0 ? Conf.SubmenuLimit : dflt;
                if (!string.IsNullOrEmpty(ConfigRoot))
                {
                    try
                    {
                        string v = BackendResolver.ReadAutoConfKey(
                            BackendResolver.AutoConfPath(ConfigRoot), "submenulimit");
                        int n;
                        if (int.TryParse(v, out n) && n > 0) return n;
                    }
                    catch { }
                }
                return dflt;
            }
        }

        // ============================================================
        // v8 新增：桩 / 取证 / 错误通道（自检与"禁静默失败"的落地）
        // ============================================================
        /// <summary>
        /// 演练模式：`open`/`url`/`send`/`keys`/`evsearch` 只记录不真动作
        /// （`action` 记录后不启动；`run` 仍真启动 —— 自检用非 GUI 的 cmd.exe）。
        /// 目的：自检里**绝不**真打开 GUI 程序、**绝不**真把按键发到桌面。
        /// </summary>
        public bool DryRun;

        /// <summary>演练模式记录的展开结果（自检断言用）。</summary>
        public string LastSend = "";
        public string LastKeys = "";
        public string LastUrl = "";
        public string LastOpenTarget = "";

        /// <summary>最近一次失败的原因与错误码（"" = 成功）。</summary>
        public string LastError = "";
        public string LastErrorCode = "";

        /// <summary>失败通知记录（每次失败一条；禁静默的可观测出口）。</summary>
        public readonly List<string> Notices = new List<string>();

        /// <summary>用户可见错误出口（RunEverApp 接托盘气泡）。</summary>
        public Action<string> ErrorNotify;

        /// <summary>
        /// 时机③ 现查钩子：程序名 → 全路径（"" = 未命中）。RunEverApp 接
        /// `PathResolvePolicy.ResolveOnDemand`（含硬超时与缓存回写）。
        /// ★ 未接线时 `run` 的"缓存失效"分支按未命中处理并报错（不静默）。
        /// </summary>
        public Func<string, string> OnDemandResolve;

        /// <summary>能力分发表（类型名 → 处理器）。大小写不敏感。</summary>
        private Dictionary<string, Func<string, Task, bool>> _table;

        private void EnsureTable()
        {
            if (_table != null) return;
            _table = new Dictionary<string, Func<string, Task, bool>>(StringComparer.OrdinalIgnoreCase);
            _table["run"] = DoRun;
            _table["open"] = DoOpen;
            _table["url"] = DoUrl;
            _table["action"] = DoCmd;
            _table["send"] = DoSend;
            _table["keys"] = DoKeys;
            _table["evsearch"] = DoEvSearch;
        }

        /// <summary>执行一个任务；返回是否成功（失败原因见 LastError / LastErrorCode）。</summary>
        public bool Execute(Task t)
        {
            LastError = "";
            LastErrorCode = "";
            if (t == null || string.IsNullOrEmpty(t.Type))
                return Fail(t, "bad-task", "任务为空或缺少动作类型");

            EnsureTable();

            // ============================================================
            // ★ 自动追加选中内容（契约 §6 / §7-Q7 修订）—— **唯一实现点就是这里**
            //   不许放进 ReplaceVars / PlaceholderExpander.Expand（那会撞
            //   ExecutorSelfTest 的"占位符唯一源"断言，且展开是纯函数、拿不到 Files）；
            //   也不许放进 DoRun/DoOpen/DoCmd（三套实现必然漂移，且那里 target 已展开、
            //   判不出"参数原为空"）。
            //
            //   顺序**硬约束**：判空（对 **raw 模板**）→ 展开占位符 → **最后追加**。
            //   反序（先追加再展开）会让选中的文本/路径里字面含 `%file%` 时被二次替换。
            //
            //   判空口径 = TargetSplit.ArgsEmpty（复用 ConfParser.SplitWhitespace）；
            //   "含占位符"用 **精确 token 判定** ContainsPlaceholder（不是"含 %"的
            //   HasPlaceholder —— 后者会把 `C:\100%\out` 误判）；判定范围 = **整个 raw**。
            //
            //   ★ **禁止回写 t.Target**：热键可连续触发，回写会让第二次执行重复追加。
            //     去重完全靠 argsEmpty && !hasPh 这一个前置条件。
            // ============================================================
            string raw = t.Target ?? "";
            bool autoAppend = TargetSplit.AppliesTo(t.Type)
                              && TargetSplit.ArgsEmpty(raw)
                              && !PlaceholderExpander.ContainsPlaceholder(raw);

            string target = ReplaceVars(raw, t);
            if (autoAppend)
            {
                string suffix = AppendSelected(t);        // 含前导空格；无选中 ⇒ ""
                if (suffix.Length > 0)
                {
                    // 目标为空时不带前导空格（如 open + 空目标 + 选中了文件）
                    target = (target.Length == 0) ? suffix.Substring(1) : target + suffix;
                }
            }

            // ============================================================
            // ★ 多选批量执行 = **循环体**（用户 2026-11-09 定口径）。
            //   入参形态："同一 Target × N 个文件"（N ≥ 2）且动作类型支持逐文件 ⇒ 循环；
            //   **N ≤ 1 一律走下面的原路径**（N=1 结果必须与改前逐字一致 = 回归红线）。
            //   上限 = auto.conf 的 batchMax（<see cref="BatchLimit"/>）。
            // ============================================================
            bool batch = t.Files != null && t.Files.Count > 1 && IsBatchType(t.Type);

            if (batch)
                t.Log("DoAction type=" + t.Type + " batch=" + t.Files.Count + " batchMax=" + BatchLimit);
            else
                t.Log("DoAction type=" + t.Type + " resolvedTarget=[" + target + "]");

            Func<string, Task, bool> handler;
            if (!_table.TryGetValue(t.Type.Trim(), out handler))
                return Fail(t, "unknown-action", "未知动作类型: " + t.Type);

            if (batch) return ExecuteBatch(raw, t, autoAppend, handler);

            try
            {
                return handler(target, t);
            }
            catch (Exception ex)
            {
                return Fail(t, "action-exception", "动作异常（" + t.Type + "）：" + ex.Message);
            }
        }

        // ============================================================
        // ---- 自动追加选中内容（契约 §6）----
        // ============================================================
        /// <summary>
        /// 追加到已展开目标的**后缀**（含前导空格；无可追加内容 ⇒ 返回 ""）。
        ///
        /// 契约 §6 口径：
        ///   · 选中文件（夹）⇒ 各自**带引号**的完整路径、空格分隔（`"p1" "p2"`）；
        ///   · 只有选中文本 ⇒ 该文本原文（不加引号）；
        ///   · 都没有 ⇒ 不追加。
        /// ★ 文件列表取 `Task.Files`（**全量**）——`MatchContext` 只有 `FirstFilePath`，
        ///   用它实现会丢多选。
        /// </summary>
        private static string AppendSelected(Task t)
        {
            string s = "";
            if (t.Files != null && t.Files.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < t.Files.Count; i++)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append('"').Append(t.Files[i]).Append('"');
                }
                s = sb.ToString();
            }
            else if (!string.IsNullOrEmpty(t.Text))
            {
                s = t.Text;
            }
            return s.Length == 0 ? "" : " " + s;
        }

        // ============================================================
        // ---- 多选批量执行：同一 Target × N 个文件 = 循环体 ----
        // ============================================================
        /// <summary>
        /// 会按文件循环的动作类型：`run` / `open`（主场景）+ `action` / `send` / `keys`
        /// （用户口径：建议循环，但受 batchMax 总量上限）。
        /// `url` / `evsearch` 不在内：前者是单串模板（无"文件 × 条目"语义），
        /// 后者自己就是"一次查询 N 条结果"（上限走 submenuLimit，不走 batchMax）。
        /// </summary>
        private static bool IsBatchType(string type)
        {
            string k = (type ?? "").Trim();
            return string.Equals(k, "run", StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, "open", StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, "action", StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, "send", StringComparison.OrdinalIgnoreCase)
                || string.Equals(k, "keys", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 循环体：对选中的每个文件跑一次同一个条目。
        ///
        ///   · **逐文件替换** —— `%file%` 按**当前文件**展开（其余占位符取自 Task 不变）；
        ///     参数为空时只追加**当前文件**（不是全部，否则每个文件都会重复带上整批）。
        ///   · **失败继续** —— 第 k 个失败不短路，剩下的照跑（短路会让"3 个里坏 1 个"
        ///     变成"只处理了 1 个"，用户以为全好了）。
        ///   · **上限** —— 只做前 `batchMax` 个，超出部分不执行、不算失败，日志点明"已截断"。
        ///   · **汇总** —— 结束时一行：`N 成功 / M 失败（失败文件: ...）`；成功数只数
        ///     **真执行过且成功**的，被截断的既不记成功也不记失败。
        /// </summary>
        private bool ExecuteBatch(string raw, Task t, bool autoAppend, Func<string, Task, bool> handler)
        {
            int total = t.Files.Count;
            int max = BatchLimit;
            int run = Math.Min(total, max);
            bool truncated = total > run;

            if (truncated)
            {
                t.Log("批量：选中 " + total + " 个文件，超过上限 batchMax=" + max
                    + " ⇒ **已截断**，只做前 " + run + " 个");
            }

            int okCount = 0;
            var failed = new List<string>();
            string lastCode = "", lastMsg = "";

            for (int i = 0; i < run; i++)
            {
                string file = t.Files[i] ?? "";
                string target = ReplaceVars(raw, t, file);
                if (autoAppend)
                {
                    // 与单文件路径同规则：目标为空时不带前导空格
                    string quoted = "\"" + file + "\"";
                    target = (target.Length == 0) ? quoted : target + " " + quoted;
                }

                t.Log("批量[" + (i + 1) + "/" + run + "] file=[" + file + "] resolvedTarget=[" + target + "]");

                LastError = "";
                LastErrorCode = "";
                bool ok;
                try
                {
                    ok = handler(target, t);
                }
                catch (Exception ex)
                {
                    ok = Fail(t, "action-exception", "动作异常（" + t.Type + "）：" + ex.Message);
                }

                if (ok)
                {
                    okCount++;
                }
                else
                {
                    failed.Add(file);
                    lastCode = LastErrorCode;
                    lastMsg = LastError;
                }
                t.Log("批量[" + (i + 1) + "/" + run + "] " + (ok ? "成功" : "失败") + " " + file);
            }

            var sb = new StringBuilder();
            sb.Append(okCount).Append(" 成功 / ").Append(failed.Count).Append(" 失败");
            if (failed.Count > 0)
                sb.Append("（失败文件: ").Append(string.Join(", ", failed.ToArray())).Append("）");
            string summary = sb.ToString();
            t.Log("批量汇总：" + "共 " + total + " 个文件，执行 " + run + " 个 → " + summary);

            if (failed.Count == 0) return true;

            LastErrorCode = "batch-partial-fail";
            LastError = "批量执行 " + summary + "；最后失败[" + lastCode + "]: " + lastMsg;
            return false;
        }

        // ============================================================
        // ---- run: 启动程序（首个 token 解析全路径 + 移位兜底）----
        // ============================================================
        private bool DoRun(string target, Task t)
        {
            var parts = ConfParser.SplitWhitespace(target);
            if (parts.Count < 1) return Fail(t, "run-empty-target", "run 目标为空");

            // ★ 与编辑器**同口径**（唯一口径源 = `TargetSplit.Split`）：未加引号的含空格路径
            //   必须在这里也切对，否则 prog 会变成 `C:\Program` ⇒ File.Exists 失败 ⇒ run-missing
            //   （2026-10-05 用户实报：`C:\Program Files\Listary\Listary.exe` 启动失败）。
            string progRaw, rest;
            TargetSplit.Split(target, out progRaw, out rest);
            string prog = (progRaw ?? "").Trim('"');
            if (prog.Length == 0) return Fail(t, "run-empty-target", "run 目标为空");

            string full = "";
            bool quoted = true;
            if (prog.IndexOf('\\') >= 0 || prog.IndexOf('/') >= 0)
            {
                full = prog;
            }
            else
            {
                full = _cache != null ? _cache.Get(prog) : "";
                if (string.IsNullOrEmpty(full))
                {
                    // 缓存未命中 → 交系统原生解析（PATH + App Paths）
                    full = PathCache.ResolveProgram(prog);
                    if (string.IsNullOrEmpty(full))
                    {
                        t.Log("缓存未命中且系统解析失败，交 ShellExecute: " + prog);
                        quoted = false;
                    }
                }
            }

            if (!string.IsNullOrEmpty(full) && !File.Exists(full))
            {
                // ★ 时机③（AC-5③）：缓存命中但文件不存在 = 软件移位 ⇒ EV 现查一次 + 回写缓存。
                //   未命中 ⇒ **必须报错**（旧行为只写日志后 return false = 用户侧"点了没反应"）。
                string cachedPath = full;
                string found = TryOnDemand(prog, t);
                if (string.IsNullOrEmpty(found))
                {
                    return Fail(t, "run-missing",
                        "程序不存在且现查未命中：程序=" + prog + " 缓存路径=" + cachedPath
                        + " ⇒ 请用托盘「刷新路径缓存」重建");
                }
                full = found;
            }

            string cmd = quoted && !string.IsNullOrEmpty(full) ? "\"" + full + "\"" : prog;
            if (!string.IsNullOrEmpty(rest)) cmd += " " + rest;

            t.Log("Run: " + cmd);
            if (DryRun)
            {
                t.Log("DryRun run（不真启动）");
                return true;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = quoted && !string.IsNullOrEmpty(full) ? full : prog,
                    Arguments = rest,
                    UseShellExecute = true
                });
                return true;
            }
            catch (Exception ex)
            {
                return Fail(t, "run-launch-failed", "启动失败 " + prog + "：" + ex.Message);
            }
        }

        /// <summary>时机③ 现查（软接线：未接钩子 ⇒ 视为未命中，不静默）。</summary>
        private string TryOnDemand(string prog, Task t)
        {
            if (OnDemandResolve == null)
            {
                if (t != null) t.Log("现查跳过：未接线 OnDemandResolve=null");
                return "";
            }
            try
            {
                string p = OnDemandResolve(prog);
                if (!string.IsNullOrEmpty(p))
                {
                    if (t != null) t.Log("现查命中: " + prog + " → " + p);
                    return p;
                }
                if (t != null) t.Log("现查未命中: " + prog);
            }
            catch (Exception ex)
            {
                if (t != null) t.Log("现查异常: " + ex.Message);
            }
            return "";
        }

        /// <summary>
        /// ★ I-5 廉价预判：本次执行是否**可能**走时机③ 现查（缓存命中但文件不存在）。
        ///
        /// 用途：调用方（`RunEverApp.Dispatch` / `DispatchAuto`）据此把「现查 + 启动」
        /// 整体移交 worker 线程 —— 现查内部是 `task.Wait(≤1200ms)`
        /// （`PathResolvePolicy.cs:428`，硬超时 `OnDemandTimeoutMs=1200`），在托盘热键的
        /// **消息线程**上同步等待会占住消息泵（`_menuBusy` 期间第二次热键被静默丢弃）。
        ///
        /// 边界（**只读、零等待** ⇒ 消息线程可安全调用）：
        ///   · 只做 `ReplaceVars` + 缓存查表 + 一次 `File.Exists`；**不查 Everything、不等待**；
        ///   · 判定口径与 `DoRun:179-192` 的插入点同源：显式路径（含 `\` `/`）不走缓存分支 ⇒ false；
        ///     缓存未命中则由系统解析兜底（`DoRun:167-176`）⇒ false；
        ///   · 返回 true 只是"可能现查"，**不改变 `DoRun` 自身行为**（真判定仍在 DoRun 内做）。
        ///   · ★ 批量（U-D）：循环体里每个文件都是一次独立启动 ⇒ **逐个预判**，
        ///     任一文件"可能要现查"就整体移交 worker（否则后面的文件会在消息线程上等）。
        /// </summary>
        public bool NeedsOnDemandResolve(Task t)
        {
            if (t == null || OnDemandResolve == null) return false;
            if (!string.Equals((t.Type ?? "").Trim(), "run", StringComparison.OrdinalIgnoreCase))
                return false;

            if (t.Files != null && t.Files.Count > 1)
            {
                int run = Math.Min(t.Files.Count, BatchLimit);
                for (int i = 0; i < run; i++)
                    if (NeedsOnDemandFor(t, t.Files[i])) return true;
                return false;
            }
            return NeedsOnDemandFor(t, null);
        }

        /// <summary>单个文件的现查预判（currentFile = null ⇒ 既有单文件口径）。</summary>
        private bool NeedsOnDemandFor(Task t, string currentFile)
        {
            string target = ReplaceVars(t.Target, t, currentFile);
            var parts = ConfParser.SplitWhitespace(target);
            if (parts.Count < 1) return false;

            string prog = parts[0].Trim('"');
            if (prog.IndexOf('\\') >= 0 || prog.IndexOf('/') >= 0) return false;

            string full = _cache != null ? _cache.Get(prog) : "";
            if (string.IsNullOrEmpty(full)) return false;

            try { return !File.Exists(full); }
            catch { return false; }
        }

        private static void LogOpenResolve(Task t, string name, string full)
        {
            try { if (t != null) t.Log("  open 解析: " + name + " → " + full); } catch { }
        }
        // ---- open: 打开文件/文件夹（多目标按引号分隔）----
        private bool DoOpen(string target, Task t)
        {
            if (string.IsNullOrEmpty(target)) return Fail(t, "open-empty-target", "open 目标为空");

            int opened = 0;
            foreach (var p in ConfParser.SplitWhitespace(target))
            {
                string path = p.Trim('"');
                if (path.Length == 0) continue;

                // ★ 2026-11-07（用户口径）：配置**只存文件名**，完整路径靠缓存拼回。
                //   拖拽时已把 "文件名 → 完整路径" 写进 PathCache；这里先按名字解析。
                //   已是绝对路径且存在 ⇒ 直接用（向后兼容存量配置）。
                bool exists;
                try { exists = File.Exists(path) || Directory.Exists(path); }
                catch { exists = false; }

                if (!exists && _cache != null
                    && path.IndexOf('\\') < 0 && path.IndexOf('/') < 0)
                {
                    string cached = _cache.Get(path);
                    if (!string.IsNullOrEmpty(cached) && (File.Exists(cached) || Directory.Exists(cached)))
                    {
                        LogOpenResolve(t, path, cached);
                        path = cached;
                        exists = true;
                    }
                }

                if (!exists) return Fail(t, "open-not-found", "open 目标找不到（路径与缓存都未命中）: " + path);

                if (DryRun)
                {
                    LastOpenTarget = path;
                    opened++;
                    t.Log("DryRun open（不真打开）: " + path);
                    continue;
                }

                try
                {
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                    opened++;
                }
                catch (Exception ex)
                {
                    return Fail(t, "open-launch-failed", "打开失败 " + path + "：" + ex.Message);
                }
            }

            if (opened == 0) return Fail(t, "open-no-target", "open 没有可打开的目标");
            return true;
        }

        // ---- url: 打开网址（模板已展开）----
        private bool DoUrl(string target, Task t)
        {
            if (string.IsNullOrEmpty(target)) return Fail(t, "url-empty-target", "url 目标为空");
            LastUrl = target;
            t.Log("Url: " + target);

            if (DryRun)
            {
                t.Log("DryRun url（不真打开）");
                return true;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                return Fail(t, "url-launch-failed", "打开网址失败: " + ex.Message);
            }
        }

        // ---- action: cmd 管道 ----
        private bool DoCmd(string target, Task t)
        {
            if (string.IsNullOrEmpty(target)) return Fail(t, "action-empty-target", "action 目标为空");

            // 定夺显示/隐藏
            bool show = false;
            if (t.Flag == ActionFlag.Show) show = true;
            else if (t.Flag == ActionFlag.Hide) show = false;
            else if (t.Show.HasValue) show = t.Show.Value;

            t.Log("Cmd: " + target + "  show=" + (show ? "1" : "0")
                + " flag=[" + (t.Flag == ActionFlag.None ? "" : t.Flag.ToString().ToLowerInvariant()) + "]");

            if (DryRun)
            {
                t.Log("DryRun action（不真执行）");
                return true;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = "/c " + target,
                    UseShellExecute = true,
                    CreateNoWindow = !show,
                    WindowStyle = show ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
                };
                var proc = Process.Start(psi);

                // ★ 显示时等待与否由 auto.conf 的 waitWhenShown 决定（默认等待）：
                //   1 = 等待 —— cmd 窗口留存，进度/结果/报错都看得见（AHK 版行为）
                //   0 = 不等待 —— 立即返回；窗口由 cmd 自己持有，主程序/托盘零阻塞
                //   ⚠ 隐藏模式一律不等待（架构约束：主程序零阻塞）
                if (show && proc != null && WaitWhenShown)
                    proc.WaitForExit();

                return true;
            }
            catch (Exception ex)
            {
                return Fail(t, "action-launch-failed", "命令执行失败: " + ex.Message);
            }
        }

        // ---- send: 发送文本 ----
        private bool DoSend(string target, Task t)
        {
            LastSend = EscapeSendKeys(target);
            if (DryRun)
            {
                t.Log("DryRun send（不真发键）: " + target);
                return true;
            }
            if (string.IsNullOrEmpty(target)) return Fail(t, "send-empty-target", "send 文本为空");
            try
            {
                System.Windows.Forms.SendKeys.SendWait(LastSend);
                return true;
            }
            catch (Exception ex)
            {
                return Fail(t, "send-failed", "发送文本失败: " + ex.Message);
            }
        }

        // ---- keys: 发快捷键 ----
        private bool DoKeys(string target, Task t)
        {
            LastKeys = target ?? "";
            if (DryRun)
            {
                t.Log("DryRun keys（不真发键）: " + target);
                return true;
            }
            if (string.IsNullOrEmpty(target)) return Fail(t, "keys-empty-target", "keys 序列为空");
            try
            {
                System.Windows.Forms.SendKeys.SendWait(target);
                return true;
            }
            catch (Exception ex)
            {
                return Fail(t, "keys-failed", "发送按键失败: " + ex.Message);
            }
        }

        // ============================================================
        // ---- evsearch: 本地搜索（Everything 语义查询 → 按"打开文件"执行）----
        //
        // 分层纪律：查询**只在这里**（执行层）发生 —— 判定层（MatchEngine / MenuDecision）
        // 不读盘、不查 Everything 的硬边界不受影响。
        //
        // 后端链（与 PathCacheBuilder 的解析链同源，但查询语义不同）：
        //   ① Everything64.dll 直连（`EverythingProbe` 探针 + 缓存的 EvApi 句柄，EvLock 串行化）
        //   ② es.exe（auto.conf 的 espath，候选位置兜底）
        //   两条都不可用 ⇒ `evsearch-backend-unavailable`（与 0 命中严格区分）。
        //
        // ★ 与 `EverythingBackend.Search` 的区别：那里按**文件名全等**过滤（wfn 语义，解析程序名用）；
        //   这里要的是**关键字查询**（用户写的搜索词），所以直接下发 SetSearchW 并保留 Everything
        //   自己的匹配语义，不再做全等过滤。
        // ============================================================
        private bool DoEvSearch(string target, Task t)
        {
            LastEvSearchBackend = "";
            LastEvSearchHits.Clear();

            string query = (target ?? "").Trim();
            if (query.Length == 0)
            {
                // 不走"未实现"占位：空查询是配置/占位符问题，单独给码（禁静默，也别谎报）。
                return Fail(t, "evsearch-empty-query",
                    "evsearch 查询为空：目标串（占位符展开后）没有可搜索的关键字");
            }

            int max = SearchLimit;
            string evWhy = "", esWhy = "";
            List<string> hits = null;
            string backend = "";

            hits = QueryEverything(query, max, out evWhy);
            if (hits != null) backend = "EV";
            else
            {
                hits = QueryEs(query, max, out esWhy);
                if (hits != null) backend = "es";
            }

            if (hits == null)
            {
                return Fail(t, "evsearch-backend-unavailable",
                    "evsearch 两条后端都不可用：EV[" + evWhy + "]；es[" + esWhy + "]；查询=" + query);
            }

            // 去重（保持后端返回顺序）+ 按上限截断（上限来自 auto.conf 的 submenuLimit）
            var uniq = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in hits)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (seen.Add(p)) uniq.Add(p);
            }
            if (uniq.Count > max) uniq.RemoveRange(max, uniq.Count - max);
            LastEvSearchHits.AddRange(uniq);
            LastEvSearchBackend = backend;

            if (uniq.Count == 0)
            {
                return Fail(t, "evsearch-no-result",
                    "evsearch 无结果：后端=" + backend + " 查询=[" + query + "] 上限=" + max);
            }

            t.Log("evsearch backend=" + backend + " query=[" + query + "] 命中=" + uniq.Count
                + " 上限(submenuLimit)=" + max);

            if (DryRun)
            {
                LastOpenTarget = uniq[0];
                t.Log("DryRun evsearch（不真打开）: " + string.Join(" | ", uniq.ToArray()));
                return true;
            }

            // 按"打开文件"执行：逐个 ShellExecute；索引陈旧（文件已不在）只跳过并记日志，
            // 绝不因为一条幽灵候选把整次动作判失败（与 run 的"移位"兜底同精神）。
            int opened = 0;
            foreach (string path in uniq)
            {
                bool exists;
                try { exists = File.Exists(path) || Directory.Exists(path); }
                catch { exists = false; }

                if (!exists)
                {
                    t.Log("evsearch 跳过（索引陈旧/已不在盘上）: " + path);
                    continue;
                }
                try
                {
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                    opened++;
                }
                catch (Exception ex)
                {
                    t.Log("evsearch 打开失败: " + path + " :: " + ex.Message);
                }
            }

            if (opened == 0)
            {
                return Fail(t, "evsearch-open-failed",
                    "evsearch 命中 " + uniq.Count + " 条，但没有一条能打开（全部已不在盘上或启动失败）");
            }

            t.Log("evsearch 已打开 " + opened + "/" + uniq.Count + " 条");
            return true;
        }

        /// <summary>auto.conf 的 `evdll`（未接 ConfigRoot ⇒ 自动探测）。</summary>
        private string EvDllForSearch()
        {
            if (string.IsNullOrEmpty(ConfigRoot)) return "";
            try { return BackendResolver.ReadEvDll(ConfigRoot); }
            catch { return ""; }
        }

        /// <summary>
        /// Everything64.dll 直连查询。返回 null = **后端不可用**（加载/探针/IPC 失败）；
        /// 空表 = 可用但 0 命中。绝不抛。
        /// </summary>
        private List<string> QueryEverything(string query, int max, out string why)
        {
            why = "";

            if (EvQuery != null)
            {
                try
                {
                    List<string> r = EvQuery(query, max);
                    if (r == null) { why = "注入后端报告不可用"; return null; }
                    return r;
                }
                catch (Exception ex) { why = "注入后端异常: " + ex.Message; return null; }
            }

            BackendStatus st;
            try { st = EverythingProbe.Get(EvDllForSearch(), false); }
            catch (Exception ex) { why = "EV 探针异常: " + ex.Message; return null; }

            if (st == null || st.Kind != BackendKind.Everything)
            {
                why = "EV 不可用" + ((st != null && !string.IsNullOrEmpty(st.Reason)) ? "（" + st.Reason + "）" : "");
                return null;
            }

            EvApi api = EverythingProbe.Handle;
            if (api == null) { why = "EV 句柄为空（探针成功但未保留实例）"; return null; }

            var list = new List<string>();
            lock (EvLock.Sync)
            {
                try
                {
                    api.SetSearchW(query);
                    api.SetMatchPath(false);
                    api.SetRegex(false);
                    api.SetMatchWholeWord(false);
                    api.SetMatchCase(false);
                    api.SetMax((uint)max);
                    api.SetOffset(0);

                    bool ok = api.QueryW(true);
                    int err = (int)api.GetLastError();
                    uint n = api.GetNumResults();

                    if (!ok || err != 0)
                    {
                        try { api.Cleanup(); } catch { }
                        why = "EV 查询失败 lastError=" + err + (err == 2 ? "（Everything 未运行/IPC 不通）" : "");
                        return null;
                    }

                    for (uint i = 0; i < n && i < (uint)max; i++)
                    {
                        string full = api.FullPathAt(i);
                        if (!string.IsNullOrEmpty(full)) list.Add(full);
                    }
                    try { api.Cleanup(); } catch { }
                }
                catch (Exception ex)
                {
                    try { api.Cleanup(); } catch { }
                    why = "EV 查询异常: " + ex.Message;
                    return null;
                }
            }
            return list;
        }

        /// <summary>
        /// es.exe 兜底查询（`-n &lt;max&gt; -export-txt &lt;tmp&gt; &lt;query&gt;`）。
        /// 返回 null = 不可用 / 调用失败；空表 = 0 命中。绝不抛。
        /// </summary>
        private List<string> QueryEs(string query, int max, out string why)
        {
            why = "";

            if (EsQuery != null)
            {
                try
                {
                    List<string> r = EsQuery(query, max);
                    if (r == null) { why = "注入后端报告不可用"; return null; }
                    return r;
                }
                catch (Exception ex) { why = "注入后端异常: " + ex.Message; return null; }
            }

            string esPath = "";
            try
            {
                if (!string.IsNullOrEmpty(ConfigRoot))
                    esPath = PathCacheBuilder.ResolveEsPath(
                        PathCacheBuilder.ReadEsPathFromConfig(BackendResolver.AutoConfPath(ConfigRoot)));
            }
            catch { }
            if (string.IsNullOrEmpty(esPath))
            {
                try { esPath = PathCacheBuilder.ResolveEsPath(null); } catch { }
            }
            if (string.IsNullOrEmpty(esPath))
            {
                why = "es.exe 不可用（auto.conf 的 espath 与候选位置都没命中）";
                return null;
            }

            string outFile = null;
            try
            {
                outFile = Path.Combine(Path.GetTempPath(),
                    "rv_evsearch_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".txt");

                var psi = new ProcessStartInfo(esPath);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                // -export-txt = es 自己写 UTF-8 文件（cmd 重定向会丢编码）；查询串整体按一个参数传
                psi.Arguments = "-n " + max + " -export-txt "
                    + PathCacheBuilder.QuoteArg(outFile) + " " + PathCacheBuilder.QuoteArg(query);

                using (var proc = new Process())
                {
                    proc.StartInfo = psi;
                    var sbErr = new StringBuilder();
                    proc.OutputDataReceived += delegate { };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) sbErr.AppendLine(e.Data); };
                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    if (!proc.WaitForExit(PathCacheBuilder.EsTimeoutMs))
                    {
                        try { proc.Kill(); } catch { }
                        why = "es 查询超时（>" + PathCacheBuilder.EsTimeoutMs + "ms）";
                        return null;
                    }
                    proc.WaitForExit();

                    if (proc.ExitCode != 0)
                    {
                        why = "es 退出码 " + proc.ExitCode
                            + (sbErr.Length > 0 ? "：" + sbErr.ToString().Trim() : "");
                        return null;
                    }
                }

                if (!File.Exists(outFile))
                {
                    why = "es 未产出导出文件（0 命中时也可能不建文件，按 0 命中处理）";
                    return new List<string>();
                }

                var list = new List<string>();
                foreach (string raw in File.ReadAllLines(outFile, Encoding.UTF8))
                {
                    string line = raw.Trim().Trim(' ', '\t', '\r', '"');
                    if (line.Length == 0) continue;
                    list.Add(line);
                    if (list.Count >= max) break;
                }
                return list;
            }
            catch (Exception ex)
            {
                why = "es 查询异常: " + ex.Message;
                return null;
            }
            finally
            {
                try { if (outFile != null && File.Exists(outFile)) File.Delete(outFile); } catch { }
            }
        }

        // ============================================================
        // 失败出口 —— 禁静默：错误码 + 日志 + 用户可见通知
        // ============================================================
        private bool Fail(Task t, string code, string msg)
        {
            LastErrorCode = code;
            LastError = msg;
            if (t != null) t.Log("失败[" + code + "]: " + msg);
            Notify(msg);
            return false;
        }

        private void Notify(string msg)
        {
            Notices.Add(msg);
            if (ErrorNotify != null)
            {
                try { ErrorNotify(msg); }
                catch { /* 通知通道本身出错不能反过来炸执行器 */ }
            }
        }

        /// <summary>把普通文本转义成 SendKeys 语法（+^%~(){} 需转义）。</summary>
        private static string EscapeSendKeys(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if ("+^%~(){}[]".IndexOf(c) >= 0) sb.Append('{').Append(c).Append('}');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // ============================================================
        // 占位符替换 —— 唯一源 = PlaceholderExpander（本方法只做委托）
        //   %text% 选中文本  %file% 首个选中文件  %clip% 剪贴板
        //   %win%  窗口标题  %proc% 进程名        %s 搜索词（文本优先，其次剪贴板）
        // ============================================================
        public static string ReplaceVars(string s, Task t)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (t == null) return s;
            return PlaceholderExpander.Expand(s, ToMatchContext(t));
        }

        /// <summary>
        /// 批量执行用：`%file%` 按**当前文件**展开（其余占位符仍取自 Task）。
        /// 仍是同一唯一源的委托 —— 不新增第二份替换逻辑。
        /// </summary>
        public static string ReplaceVars(string s, Task t, string currentFile)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (t == null) return s;
            return PlaceholderExpander.Expand(s, ToMatchContext(t), currentFile);
        }

        /// <summary>执行任务 → 判定上下文（占位符唯一源的入参形态）。</summary>
        public static MatchContext ToMatchContext(Task t)
        {
            var c = new MatchContext();
            if (t == null) return c;
            c.Text = t.Text ?? "";
            c.Clipboard = t.Clipboard ?? "";
            c.WinTitle = t.WinTitle ?? "";
            c.ProcessName = t.Proc ?? "";
            if (t.Files != null && t.Files.Count >= 1) c.FirstFilePath = t.Files[0];
            return c;
        }
    }
}
