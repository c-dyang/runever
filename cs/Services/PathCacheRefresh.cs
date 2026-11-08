using System;
using System.IO;

namespace RunEverCs.Services
{
    /// <summary>
    /// 「刷新路径缓存」的唯一定义 —— **托盘与 CLI 共用这一份**。
    ///
    /// 为什么抽出来：同一件事（收集 run 程序名 → 三级解析 → 落 cache\paths.txt）
    ///   若托盘一套、CLI 一套，两边必然漂移（本会话已经吃过"同一个文件两份作者"的亏）。
    ///   抽成服务后，CLI 能复现托盘行为 ⇒ 无 GUI 环境也能验证（`--refresh-cache`）。
    ///
    /// 搜索后端：**EV（Everything64.dll 直连）优先** → 自动降级 es.exe → 再降级
    ///   「App Paths + PATH」；降级原因带在结果里（排障要看得见"为什么降级"）。
    /// </summary>
    public static class PathCacheRefresh
    {
        public class Result
        {
            public bool Ok;
            public string Message = "";

            /// <summary>本次构建统计（resolved/failed/skipped/…）。</summary>
            public BuildStats Build;

            /// <summary>构建后缓存总条目数。</summary>
            public int CacheCount;

            /// <summary>实际生效的搜索后端描述（EV/es/none）。</summary>
            public string Backend = "";

            /// <summary>降级/失败原因（空 = 无异常）。</summary>
            public string BackendReason = "";

            public string SummaryLine()
            {
                if (!Ok) return "刷新失败：" + Message;
                return Build.ToString() + " 缓存条目=" + CacheCount + " 后端=" + Backend;
            }
        }

        public static Result Run(string root, Action<string> log)
        {
            return Run(root, log, null);
        }

        /// <summary>
        /// 刷新路径缓存。
        /// ★ v8 回灌单一入口（T6/AC-8）：`into` 传入**运行实例的 PathCache** 时，
        ///   构建结果**就地**写进同一实例（`PathCacheBuilder.Build` 内部 Set + 原子 Save），
        ///   于是图标解析器与执行器（持有同一引用）立刻看到新缓存 —— 修掉原 gap①
        ///   （旧实现新建 PathCache 实例落盘，运行期永远看不到）。
        ///   `into = null` 时保持既有行为（CLI / 一次性进程用）。
        /// </summary>
        public static Result Run(string root, Action<string> log, PathCache into)
        {
            var r = new Result();
            Action<string> lg = log ?? (s => { });

            try
            {
                // ---- 搜索后端（EV → es → none，带降级原因）----
                BackendStatus st = BackendResolver.Resolve(root, false);
                ISearchBackend backend = BackendResolver.MakeBackend(st);
                string esPath = st == null ? "" : st.EsPath;

                r.Backend = st == null ? "none" : st.KindText;
                r.BackendReason = st == null ? "" : st.Reason;
                if (st != null && st.Degraded && !string.IsNullOrEmpty(st.Reason))
                    lg("路径缓存: 搜索后端降级为 " + r.Backend + "（原因：" + st.Reason + "）");

                // ---- 待解析程序名（配置里 run 条目的首个 token）----
                string conf = Path.Combine(root, "config", "menus.conf");
                if (!File.Exists(conf))
                {
                    r.Ok = false;
                    r.Message = "配置文件不存在：" + conf;
                    lg("刷新路径缓存失败: " + r.Message);
                    return r;
                }

                var parser = new ConfParser(conf);
                parser.Parse();
                var progs = parser.CollectPrograms();

                lg("刷新路径缓存: 待解析 " + progs.Count + " 个程序，后端=" + r.Backend);

                // ---- 三级解析 + 落盘（Build 内部已原子 Save）----
                //   ★ into != null ⇒ 就地回灌运行实例（缓存引用不变，依赖方零改造）；
                //     into == null ⇒ 既有行为（新建实例）。
                var cache = (into != null) ? into : new PathCache(Path.Combine(root, "cache", "paths.txt"));
                var builder = new PathCacheBuilder(cache, esPath, lg, backend);
                r.Build = builder.Build(progs, false);
                r.CacheCount = cache.Count;
                r.Ok = true;

                lg("刷新路径缓存完成: " + r.SummaryLine());
                if (r.Build.FailedList.Count > 0)
                    lg("  未解析到（本机可能没装）：" + string.Join(", ", r.Build.FailedList.ToArray()));
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Message = ex.Message;
                lg("刷新路径缓存失败: " + ex.Message);
            }

            return r;
        }
    }
}
