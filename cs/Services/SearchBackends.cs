// ============================================================
// RunEver · 搜索后端层 —— EV 后端实现 + 可用性探针 + 降级链解析
//
// 降级链：EV（Everything64.dll 直连）→ es.exe（子进程）→ PATH + App Paths（无搜索）
//   末级标记 backend=none。**任何一级失败都只是"降级"，不是异常。**
//
// 为什么探针判据是「命中数 > 0 且 GetLastError()==0」：
//   LoadLibrary 成功只说明 DLL 能被映射；Everything 未运行 / IPC 版本不符时，
//   查询会静默返回 0 命中并把 Everything_GetLastError() 置为 2（IPC 错误）。
//   只看"加载成功"会把一个永远查不到东西的后端当成可用 ⇒ 全部条目被误报成"未安装"。
//
// SDK 全局状态是进程级单例且**非线程安全** ⇒ 所有调用（探针 + 搜索）串行化到 EvLock.Sync。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>搜索后端抽象。注入 PathCacheBuilder 的搜索级；默认不注入 = 保持既有 es 行为。</summary>
    public interface ISearchBackend
    {
        string Name { get; }
        bool Available { get; }
        /// <summary>按文件名查询，返回候选全路径列表（查不到返回空表，**不抛**）。</summary>
        List<string> Search(string name, int max);
    }

    /// <summary>SDK 全局状态串行化用的锁（探针与搜索共用一把）。</summary>
    internal static class EvLock
    {
        internal static readonly object Sync = new object();
    }

    // ============================================================
    // 可用性探针
    // ============================================================
    /// <summary>
    /// EV 可用性探针（带缓存的单例）。探测顺序见 EvDllLocator.Candidates。
    /// ★ 探针不得在热键路径上跑（会阻塞消息循环）；只在后台自检/托盘刷新里调用。
    /// </summary>
    public static class EverythingProbe
    {
        /// <summary>探针结果缓存时长（ms）。避免每次解析都重新 LoadLibrary。</summary>
        public static int CacheMs = 120000;

        /// <summary>探针查询串：任何 Windows 都有 explorer.exe，用于验证"能返回结果"。</summary>
        public const string ProbeQuery = "explorer.exe";

        private static readonly object _lock = new object();
        private static BackendStatus _status;
        private static DateTime _stamp = DateTime.MinValue;
        private static EvApi _api;

        /// <summary>已加载的 SDK 句柄（探针失败时为 null）。仅供同程序集的后端使用。</summary>
        internal static EvApi Handle { get { lock (_lock) { return _api; } } }

        /// <summary>清缓存（自检用：需要从零重探）。</summary>
        public static void Reset()
        {
            lock (_lock)
            {
                if (_api != null) { try { _api.Dispose(); } catch { } }
                _api = null;
                _status = null;
                _stamp = DateTime.MinValue;
            }
        }

        /// <summary>取探针结论。<paramref name="configuredDll"/> 非空 = 优先且独占（填错即降级，不再猜）。</summary>
        public static BackendStatus Get(string configuredDll, bool force)
        {
            lock (_lock)
            {
                if (!force && _status != null
                    && (DateTime.UtcNow - _stamp).TotalMilliseconds < CacheMs)
                    return _status;
                _status = Probe(configuredDll);
                _stamp = DateTime.UtcNow;
                return _status;
            }
        }

        private static BackendStatus Probe(string configuredDll)
        {
            BackendStatus st = new BackendStatus();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            List<string> cands = EvDllLocator.Candidates(configuredDll);
            if (cands.Count == 0) { st.Reason = "无 DLL 候选"; return st; }

            var reasons = new List<string>();
            foreach (string path in cands)
            {
                EvApi api = new EvApi();
                string why;
                if (!api.TryLoad(path, out why)) { reasons.Add(why); continue; }

                st.DllPath = path;
                st.EvVersion = api.VersionText();
                st.DbLoaded = api.QueryDbLoaded();

                lock (EvLock.Sync)
                {
                    try
                    {
                        api.SetSearchW(ProbeQuery);
                        api.SetMatchPath(false);       // 只匹配文件名（wfn 语义）
                        api.SetRegex(false);
                        api.SetMatchWholeWord(false);
                        api.SetMatchCase(false);
                        api.SetMax(16);                // ★ 必须显式设：否则吃 Everything 的 max_results 默认上限
                        api.SetOffset(0);
                        bool ok = api.QueryW(true);
                        st.LastError = (int)api.GetLastError();
                        uint n = api.GetNumResults();
                        st.ProbeHits = (int)n;

                        if (ok && st.LastError == 0 && n > 0)
                        {
                            string full = api.FullPathAt(0);
                            string fn = full.Length > 0 ? Path.GetFileName(full) : "";
                            st.FullPathVerified =
                                string.Equals(fn, ProbeQuery, StringComparison.OrdinalIgnoreCase);
                            api.Cleanup();
                            sw.Stop();
                            st.ProbeMs = sw.ElapsedMilliseconds;
                            st.Kind = BackendKind.Everything;
                            if (!st.FullPathVerified)
                                st.Reason = "探针命中但读回不一致(第1条=" + full + ")";
                            if (!st.DbLoaded)
                                st.Reason = (st.Reason.Length > 0 ? st.Reason + "; " : "") + "IsDBLoaded=false";
                            _api = api;                 // 成功才保留句柄
                            return st;
                        }
                        api.Cleanup();
                        reasons.Add("探针未命中(" + path + "): hits=" + n
                            + " lastError=" + st.LastError + " dbLoaded=" + (st.DbLoaded ? "1" : "0")
                            + (st.LastError == 2 ? " [Everything 未运行/IPC 不通]" : ""));
                    }
                    catch (Exception ex)
                    {
                        reasons.Add("探针异常(" + path + "): " + ex.Message);
                    }
                }
                try { api.Dispose(); } catch { }
            }

            sw.Stop();
            st.ProbeMs = sw.ElapsedMilliseconds;
            st.Kind = BackendKind.None;
            st.Reason = reasons.Count > 0 ? string.Join(" | ", reasons.ToArray()) : "无可用 DLL";
            return st;
        }

        /// <summary>只做探测、不保留句柄的一次性检查（供 CLI 输出用）。</summary>
        public static BackendStatus ProbeOnce(string configuredDll)
        {
            Reset();
            return Get(configuredDll, true);
        }
    }

    // ============================================================
    // EV 搜索后端
    // ============================================================
    /// <summary>
    /// Everything64.dll 直连后端。
    /// ★ 客户端**严格按文件名全等过滤** —— 等价于 es.exe 的 `wfn:`（Match Whole Filename）与
    ///   用户要求的"精确匹配"：Everything 默认是子串语义，`code.exe` 会命中 `vscode.exe`，
    ///   加权择优再准也救不回"候选本身就错"。
    /// </summary>
    public sealed class EverythingBackend : ISearchBackend
    {
        /// <summary>单次查询返回上限（与 es 分支的 `-n 30` 对齐）。</summary>
        public const int DefaultMax = 30;

        private readonly string _configuredDll;
        private readonly bool _forceProbe;

        public EverythingBackend(string configuredDll, bool forceProbe)
        {
            _configuredDll = configuredDll ?? "";
            _forceProbe = forceProbe;
        }

        public string Name { get { return "EV(Everything64.dll)"; } }

        public bool Available
        {
            get { return EverythingProbe.Get(_configuredDll, _forceProbe).Kind == BackendKind.Everything; }
        }

        /// <summary>命中数 > 0 时返回全路径候选；否则空表。</summary>
        public List<string> Search(string name, int max)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(name)) return result;

            BackendStatus st = EverythingProbe.Get(_configuredDll, _forceProbe);
            if (st.Kind != BackendKind.Everything) return result;
            EvApi api = EverythingProbe.Handle;
            if (api == null) return result;

            if (max <= 0) max = DefaultMax;

            lock (EvLock.Sync)
            {
                try
                {
                    api.SetSearchW(name);
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
                        // err=2 ⇒ IPC 失败（Everything 退出/重启）；标记为不可用让上层回退 es
                        api.Cleanup();
                        return result;
                    }

                    for (uint i = 0; i < n; i++)
                    {
                        string full = api.FullPathAt(i);
                        if (full.Length == 0) continue;
                        // ★ 严格文件名全等（wfn 语义），挡掉 vscode.exe 命中 code.exe 这类子串误配
                        string fn;
                        try { fn = Path.GetFileName(full); } catch { continue; }
                        if (!string.Equals(fn, name, StringComparison.OrdinalIgnoreCase)) continue;
                        result.Add(full);
                    }
                    api.Cleanup();
                }
                catch
                {
                    try { api.Cleanup(); } catch { }
                    return new List<string>();
                }
            }
            return result;
        }
    }

    // ============================================================
    // 降级链解析（EV → es → none）+ auto.conf 读取
    // ============================================================
    public static class BackendResolver
    {
        /// <summary>读 auto.conf 的一个键（小写键名匹配；找不到返回 ""）。</summary>
        public static string ReadAutoConfKey(string autoConfPath, string key)
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
                    if (!string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    return line.Substring(eq + 1).Trim().Trim('"');
                }
            }
            catch { }
            return "";
        }

        public static string AutoConfPath(string root)
        {
            return Path.Combine(root ?? "", "config", "auto.conf");
        }

        /// <summary>auto.conf 的 evdll=（空 = 自动探测）。</summary>
        public static string ReadEvDll(string root)
        {
            return ReadAutoConfKey(AutoConfPath(root), "evdll");
        }

        /// <summary>
        /// 解析出**当前生效**的后端：EV 可用 ⇒ Everything；否则 es 可用 ⇒ Es；否则 None。
        /// 探针失败的原因一并带在 Reason 里（排障/自检要看得见"为什么降级"）。
        /// </summary>
        public static BackendStatus Resolve(string root, bool force)
        {
            BackendStatus st = EverythingProbe.Get(ReadEvDll(root), force);
            if (st.Kind == BackendKind.Everything) return st;

            string autoConf = AutoConfPath(root);
            string esPath = PathCacheBuilder.ResolveEsPath(PathCacheBuilder.ReadEsPathFromConfig(autoConf));
            st.EsPath = esPath;
            st.Kind = string.IsNullOrEmpty(esPath) ? BackendKind.None : BackendKind.Es;
            return st;
        }

        /// <summary>构造注入 PathCacheBuilder 搜索级的后端（null = 不要注入，走原 es 路径）。</summary>
        public static ISearchBackend MakeBackend(BackendStatus st)
        {
            if (st == null || st.Kind != BackendKind.Everything) return null;

            // ★ 透传**探测时实际用的那份 DLL**（st.DllPath）而不是空串：
            //   空串会让后端自己再走一遍自动探测 —— 若用户用 evdll= 指定了自定义位置，
            //   探针缓存（120s）过期后就会静默降级到别的 DLL 或不可用（守门 P2）。
            return new EverythingBackend(st.DllPath, false);
        }
    }
}
