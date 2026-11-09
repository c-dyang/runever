using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using RunEverCs.Models;
using RunEverCs.Native;

namespace RunEverCs.Services
{
    /// <summary>
    /// 图标源解析 —— 对应 AHK RunEver.ahk 的 ResolveIconPath。
    ///
    /// 规则：
    ///   · group 类型无图标
    ///   · 图标位是 `http://` / `https://` 网址 ⇒ 网页 favicon（U-C，2026-11-09）
    ///       - 磁盘缓存命中 ⇒ 直接用（零网络、零解码）
    ///       - 未命中 ⇒ **后台**取图，本次先返回空（渲染层走兜底图标），取回后写缓存，下一次弹菜单生效
    ///   · run 类型：取 target 首个 token 的程序全路径
    ///       - 已含路径 → 直接用
    ///       - 裸名 → 查路径缓存
    ///   · 其他类型优先用条目自带的 Icon 源，没有则返回空（渲染时走兜底图标）
    ///
    /// ★ 纪律（用户 2026-11-09 拍板）：**绝不能因为一个图标拖慢弹菜单**。
    ///   菜单是同步弹的，所以这里只做「文件存在性检查」；网络一律交给后台线程，
    ///   超时上限 3s，失败/超时/非 200/非图片 ⇒ 静默回落 assets\runever.ico 或系统图标。
    /// </summary>
    public class IconResolver
    {
        /// <summary>取图硬超时（ms）。★ 硬约束：≤ 3000。</summary>
        public static int UrlFetchTimeoutMs { get { return UrlIconCache.FetchTimeoutMs; } }

        /// <summary>
        /// 自检缝：关掉后只记账、不起线程。
        /// 自检必须**离线可跑**（CI / 无网机器上也要绿），不能真的去联网。
        /// </summary>
        internal static bool BackgroundFetchEnabled = true;

        private static int _enqueued;
        private static int _fetched;
        private static int _failed;

        /// <summary>累计排入后台队列的次数（自检断言"未命中确实入队"）。</summary>
        public static int EnqueuedCount { get { return Volatile.Read(ref _enqueued); } }

        /// <summary>累计取图成功（含 304 未改）次数。</summary>
        public static int FetchedCount { get { return Volatile.Read(ref _fetched); } }

        /// <summary>累计取图失败次数。</summary>
        public static int FailedCount { get { return Volatile.Read(ref _failed); } }

        /// <summary>同一 URL 只允许一个在飞（避免连续弹菜单把同一 favicon 排成一队）。</summary>
        private static readonly ConcurrentDictionary<string, byte> InFlight =
            new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private readonly PathCache _cache;
        public IconResolver(PathCache cache) { _cache = cache; }

        // ============================================================
        // URL 判定（协议白名单）
        // ============================================================

        /// <summary>图标位是不是一个网址。**只认 http:// / https://**，其余一概不算 URL 图标。</summary>
        public static bool IsUrlIcon(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.Length < 7) return false;
            return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 带协议头但不是 http/https（`file:` / `ftp:` / `javascript:` …）。
        /// 这类源**必须拒绝**：不能当本地路径用，更不能拿去联网。
        /// 盘符（`C:\…`、`C:/…`）不是协议，单独放行。
        /// </summary>
        private static bool IsOtherScheme(string s, out string scheme)
        {
            scheme = null;
            if (string.IsNullOrEmpty(s)) return false;

            string t = s.Trim();
            int colon = t.IndexOf(':');
            if (colon <= 0 || colon > 12) return false;

            // 盘符：C:\ / C:/ —— 不是协议
            if (colon == 1 && char.IsLetter(t[0])) return false;

            if (!char.IsLetter(t[0])) return false;
            for (int i = 1; i < colon; i++)
            {
                char c = t[i];
                if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.') return false;
            }

            scheme = t.Substring(0, colon).ToLowerInvariant();
            return true;
        }

        // ============================================================
        // 解析
        // ============================================================

        public string Resolve(MenuItem item)
        {
            string keepUrl;
            return ResolveCore(item, out keepUrl);
        }

        /// <summary>
        /// 解析一条。
        /// `keepUrl` = 本轮用的原始 URL（或从缓存 sidecar 找回的 URL）。未命中时调用方要把
        /// 它**写回 IconSource**，否则下一次弹菜单就再也看不到这个网址了（见 ResolveAll）。
        /// </summary>
        private string ResolveCore(MenuItem item, out string keepUrl)
        {
            keepUrl = null;
            if (item == null) return "";
            if (item.Type == ActionType.Group) return "";

            string src = item.IconSource ?? "";

            if (src.Length > 0)
            {
                // ① 图标位写着网址 ⇒ favicon
                if (IsUrlIcon(src))
                {
                    keepUrl = src.Trim();
                    return ResolveUrl(keepUrl);
                }

                // ② 其它协议一律拒绝（留一行日志，不弹窗）
                string scheme;
                if (IsOtherScheme(src, out scheme))
                {
                    UrlIconCache.Log("拒绝图标源：协议不在白名单（只允许 http/https）scheme="
                        + scheme + " src=" + src);
                    return "";
                }

                // ③ 是上一轮写回的缓存图标 ⇒ 从 sidecar 找回原 URL（缓存被清时可自愈）
                string recovered = UrlIconCache.TryRecoverUrl(src);
                if (recovered != null)
                {
                    keepUrl = recovered;
                    return ResolveUrl(recovered);
                }
            }

            // 条目显式指定了图标源 → 优先
            if (!string.IsNullOrEmpty(item.IconSource))
                return item.IconSource;

            if (item.Type == ActionType.Run)
            {
                string tgt = item.Target ?? "";
                var parts = ConfParser.SplitWhitespace(tgt);
                if (parts.Count < 1) return "";

                string prog = parts[0].Trim('"');
                if (prog.IndexOf('\\') >= 0 || prog.IndexOf('/') >= 0)
                    return File.Exists(prog) ? prog : "";

                if (_cache != null)
                {
                    string p = _cache.Get(prog);
                    if (!string.IsNullOrEmpty(p)) return p;
                }
                return "";
            }

            return "";
        }

        /// <summary>
        /// URL 图标：命中返回渲染用的 ICO 路径；未命中**立即**返回空（兜底图标）并把取图排到后台。
        /// 这里绝不等待网络 —— 菜单是同步弹的。
        /// </summary>
        private static string ResolveUrl(string url)
        {
            string ico;
            if (UrlIconCache.TryGet(url, out ico)) return ico;

            EnqueueFetch(url);
            return "";
        }

        /// <summary>给一组分组里的所有条目解析图标源。</summary>
        public void ResolveAll(List<MenuGroup> groups)
        {
            // 第一轮：叶子条目按自身动作解析
            foreach (var g in groups)
                foreach (var it in g.Items)
                {
                    string keepUrl;
                    string resolved = ResolveCore(it, out keepUrl);

                    // ★ 本方法是**就地回写**。URL 图标这一轮没取到（resolved 为空）时若写成 ""，
                    //   下一次弹菜单就再也看不到这个网址了 —— 后台取回的 favicon 永远不会生效。
                    //   所以：没用上缓存时把 URL 留在位上（渲染层拿到网址 ⇒ ExtractIconExW 失败 ⇒
                    //   走兜底图标），下一次再来就能命中缓存。
                    it.IconSource = (resolved.Length == 0 && keepUrl != null) ? keepUrl : resolved;
                }

            // ★ group 标题不再在这里补图标（2026-11-08 用户口径）：
            //   子菜单行统一用**系统文件夹图标**，由 MenuBuilder 调
            //   `NativeMenu.SetFolderIconByPosition()` 直接设 —— 与"第一个子项"无关。
        }

        // ============================================================
        // 后台取图（唯一允许联网的地方；永远不在弹菜单的线程上跑）
        // ============================================================

        private static void EnqueueFetch(string url)
        {
            Interlocked.Increment(ref _enqueued);

            if (!BackgroundFetchEnabled) return;              // 自检：只记账，不联网
            if (!InFlight.TryAdd(url, 0)) return;             // 同一 URL 已在排队/在飞

            ThreadPool.QueueUserWorkItem(delegate
            {
                try { FetchOne(url); }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _failed);
                    UrlIconCache.Log("取图失败: " + url + " → " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    byte _;
                    InFlight.TryRemove(url, out _);
                }
            });
        }

        /// <summary>
        /// 后台取一个 favicon 并写缓存。
        ///
        /// 复用说明：`HttpWebRequest` 由 System.Net 内部的 ServicePoint **自动复用连接**，
        /// 不要在这里 new HttpClient/WebClient（每 URL 一个客户端 = 连接池打满、端口耗尽）。
        /// 超时：连接/响应头 ≤ 3s，读体 ≤ 3s（硬约束，用户可感知的是"弹菜单有没有卡"）。
        /// </summary>
        private static void FetchOne(string url)
        {
            string etag, lastModified;
            UrlIconCache.ReadMeta(url, out etag, out lastModified);

            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = UrlIconCache.FetchTimeoutMs;
            req.ReadWriteTimeout = UrlIconCache.FetchTimeoutMs;
            req.MaximumAutomaticRedirections = 3;
            req.UserAgent = "RunEver/1.0 (favicon)";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

            try { if (!string.IsNullOrEmpty(etag)) req.Headers[HttpRequestHeader.IfNoneMatch] = etag; } catch { }
            try
            {
                if (!string.IsNullOrEmpty(lastModified))
                    req.Headers[HttpRequestHeader.IfModifiedSince] = lastModified;
            }
            catch { }

            byte[] body;
            string newEtag, newLastModified;

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                int code = (int)resp.StatusCode;
                if (code == 304) { Interlocked.Increment(ref _fetched); return; }   // 未改：缓存照用
                if (code != 200) { Fail(url, "HTTP " + code); return; }

                newEtag = resp.Headers["ETag"];
                newLastModified = resp.Headers["Last-Modified"];

                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[8192];
                    int n;
                    int total = 0;
                    while (s != null && (n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > UrlIconCache.MaxBytes)
                        {
                            Fail(url, "响应体超过上限 " + UrlIconCache.MaxBytes + " 字节");
                            return;
                        }
                        ms.Write(buf, 0, n);
                    }
                    body = ms.ToArray();
                }
            }

            string error;
            if (!UrlIconCache.Store(url, body, newEtag, newLastModified, out error))
            {
                Fail(url, error);
                return;
            }
            Interlocked.Increment(ref _fetched);
        }

        private static void Fail(string url, string why)
        {
            Interlocked.Increment(ref _failed);
            UrlIconCache.Log("取图失败: " + url + " → " + (why ?? "未知原因") + "（本次用兜底图标）");
        }
    }
}
