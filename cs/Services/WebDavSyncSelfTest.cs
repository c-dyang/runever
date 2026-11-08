using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RunEverCs.Services
{
    internal sealed class SelftestAssertException : Exception
    {
        public SelftestAssertException(string msg) : base(msg) { }
    }

    /// <summary>自检报告：逐条用例 + 退出码决策（有 FAIL ⇒ 非绿）。</summary>
    internal sealed class SelftestReport
    {
        private readonly List<string> _lines = new List<string>();

        public int Pass;
        public int Fail;
        public int Skip;

        /// <summary>全部 SKIP 的**原因**（不许把 [SKIP] 当 [PASS] 用）。</summary>
        public readonly List<string> SkipReasons = new List<string>();

        public void Line(string s) { _lines.Add(s == null ? "" : s); }
        public void Blank() { _lines.Add(""); }

        public void Case(int n, string name, bool ok, string detail)
        {
            string tag = ok ? "[PASS]" : "[FAIL]";
            if (ok) Pass++; else Fail++;
            Line(tag + " " + n.ToString("D2", CultureInfo.InvariantCulture) + " " + name
                 + (string.IsNullOrEmpty(detail) ? "" : "  —— " + detail));
        }

        public void Skipped(int n, string name, string reason)
        {
            Skip++;
            SkipReasons.Add(reason);
            Line("[SKIP] " + n.ToString("D2", CultureInfo.InvariantCulture) + " " + name
                 + "  —— [SKIP] 原因: " + reason);
        }

        /// <summary>只有"0 FAIL 且 0 SKIP"才算全绿（[SKIP] 不许当 [PASS] 用）。</summary>
        public bool AllGreen { get { return Fail == 0 && Skip == 0; } }

        public string Text() { return string.Join(Environment.NewLine, _lines.ToArray()); }
    }

    /// <summary>
    /// 进程内最小 WebDAV 服务器（自检专用，**离线可复现**）。
    ///
    /// 三层绑定降级（§U4）：<c>127.0.0.1</c> → <c>localhost</c> → <c>TcpListener</c> 手写最小 HTTP；
    /// 全失败 ⇒ 调用方必须输出 <c>[SKIP] 原因:</c> 而**不许报绿**。
    ///
    /// 两个故意的"坑注入"（把 §U2 的坑钉进回归）：
    ///   · 每条 <c>response</c> 轮换三种命名空间形态（<c>D:</c> / <c>d:</c> / 无前缀无命名空间）
    ///   · 可按路径注入"坏 mtime 串"与"错星期名"（<c>Wed, 01 Oct 2026</c>，那天其实是周四）
    /// </summary>
    internal sealed class SelftestServer : IDisposable
    {
        // ------------------------------------------------ 存储

        private sealed class FileRec
        {
            public byte[] Data = new byte[0];
            public DateTime MtimeUtc;
            public string MtimeOverride;      // 非空 ⇒ 原样当 getlastmodified 发出去
        }

        private readonly Dictionary<string, FileRec> _files = new Dictionary<string, FileRec>(StringComparer.Ordinal);
        private readonly HashSet<string> _dirs = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _lock = new object();

        // ------------------------------------------------ 注入开关

        public bool RequireAuth;
        public string AuthUser = "u";
        public string AuthPass = "p";
        public bool FailPut500;
        public int SleepMsOnPut;
        public int SleepMsOnPropfind;

        /// <summary>
        /// 让 <c>PROPFIND Depth:0</c> 对**已存在的目录**回 404（模拟"不支持 PROPFIND 目录探测"的服务端）
        /// —— 只有这样 <c>MKCOL</c> 才会真的被调用到并回 405（用例 12 的构造条件）。
        /// </summary>
        public bool Propfind404OnDirs;

        /// <summary>假服务器自身抛异常时的最后一条错误（诊断用，不让它静默）。</summary>
        public string LastServerError = "";

        // ------------------------------------------------ 观测

        private readonly List<string> _requests = new List<string>();     // "METHOD rawPath"
        private readonly List<string> _methods = new List<string>();
        private readonly List<int> _statuses = new List<int>();

        public string BaseUrl = "";
        public string Transport = "";
        public string LastMoveDestination = "";
        public string LastMoveOverwrite = "";

        public int RequestCount { get { lock (_lock) return _requests.Count; } }
        public List<string> RequestsCopy() { lock (_lock) return new List<string>(_requests); }
        public List<string> MethodsCopy() { lock (_lock) return new List<string>(_methods); }
        public List<int> StatusesCopy() { lock (_lock) return new List<int>(_statuses); }

        // ------------------------------------------------ 生命周期

        private HttpListener _listener;
        private TcpListener _tcp;
        private Thread _thread;
        private volatile bool _stop;

        public void Stop()
        {
            _stop = true;
            try { if (_listener != null && _listener.IsListening) _listener.Stop(); } catch { }
            try { if (_tcp != null) _tcp.Stop(); } catch { }
            try { if (_thread != null && !_thread.Join(2000)) { } } catch { }
            try { if (_listener != null) _listener.Close(); } catch { }
        }

        public void Dispose() { Stop(); }

        /// <summary>
        /// 三层降级启动。<paramref name="forceFail"/> = true 时**强制**两层前缀都失败（用例 21 用）。
        /// </summary>
        public static SelftestServer TryStart(out string reason, bool forceFail)
        {
            return TryStart(out reason, forceFail, false);
        }

        /// <summary>
        /// 三层降级启动。<paramref name="forceFail"/> = true 时**强制**两层前缀都失败（用例 21 用）。
        /// <paramref name="tcpOnly"/> = true 时直接走 <c>TcpListener</c> ——
        /// 401 挑战用例必须用它：<c>HttpListenerResponse.Headers</c> **禁止**设置
        /// <c>WWW-Authenticate</c>（restricted response header，实测抛 ArgumentException），
        /// 手写最小 HTTP 才能真正发出 Basic 挑战。
        /// </summary>
        public static SelftestServer TryStart(out string reason, bool forceFail, bool tcpOnly)
        {
            reason = "";
            if (forceFail)
            {
                reason = "已注入绑定失败：HttpListener(http://127.0.0.1:*) 与 TcpListener 均不可用";
                return null;
            }

            var rand = new Random(Environment.TickCount);
            string[] hosts = new string[] { "127.0.0.1", "localhost" };

            for (int h = 0; !tcpOnly && h < hosts.Length; h++)
            {
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    int port = rand.Next(18000, 20000);
                    string prefix = "http://" + hosts[h] + ":" + port + "/";
                    var srv = new SelftestServer();
                    HttpListener l = null;
                    try
                    {
                        l = new HttpListener();
                        l.Prefixes.Add(prefix);
                        l.Start();
                    }
                    catch (Exception ex1)
                    {
                        try { if (l != null) l.Close(); } catch { }
                        reason = "HttpListener " + prefix + " 绑定失败：" + ex1.GetType().Name + " " + ex1.Message;
                        continue;
                    }

                    srv._listener = l;
                    srv.BaseUrl = prefix;
                    srv.Transport = "HttpListener";
                    srv.StartThread();
                    reason = "";
                    return srv;
                }
            }

            // ---- 第三层：TcpListener 手写最小 HTTP ----
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int port = rand.Next(18000, 20000);
                var srv = new SelftestServer();
                TcpListener t = null;
                try
                {
                    t = new TcpListener(IPAddress.Loopback, port);
                    t.Start();
                }
                catch (Exception ex2)
                {
                    try { if (t != null) t.Stop(); } catch { }
                    reason = "TcpListener 127.0.0.1:" + port + " 绑定失败：" + ex2.GetType().Name + " " + ex2.Message;
                    continue;
                }

                srv._tcp = t;
                srv.BaseUrl = "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/";
                srv.Transport = "TcpListener";
                srv.StartThread();
                reason = "";
                return srv;
            }

            if (reason.Length == 0) reason = "HttpListener 与 TcpListener 均绑定失败（未知原因）";
            return null;
        }

        private void StartThread()
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;   // ★ 独立后台线程（同线程 Begin/End 交错会让带 body 的 PROPFIND 假超时）
            _thread.Name = "runever-selftest-webdav";
            _thread.Start();
        }

        // ================================================ 存储 API（用例构造用）

        public void Reset()
        {
            lock (_lock)
            {
                _files.Clear();
                _dirs.Clear();
                _requests.Clear();
                _methods.Clear();
                _statuses.Clear();
                RequireAuth = false;
                FailPut500 = false;
                SleepMsOnPut = 0;
                SleepMsOnPropfind = 0;
                Propfind404OnDirs = false;
                LastServerError = "";
                LastMoveDestination = "";
                LastMoveOverwrite = "";
            }
        }

        public void MkDir(string path) { lock (_lock) _dirs.Add(Norm(path)); }

        public void PutRemote(string path, string text, DateTime mtimeUtc)
        {
            PutRemoteBytes(path, new UTF8Encoding(false).GetBytes(text ?? ""), mtimeUtc, null);
        }

        public void PutRemoteBytes(string path, byte[] data, DateTime mtimeUtc, string mtimeOverride)
        {
            string p = Norm(path);
            lock (_lock)
            {
                var rec = new FileRec();
                rec.Data = data ?? new byte[0];
                rec.MtimeUtc = mtimeUtc;
                rec.MtimeOverride = mtimeOverride;
                _files[p] = rec;
            }
        }

        public void SetMtimeOverride(string path, string raw)
        {
            lock (_lock)
            {
                FileRec r;
                if (_files.TryGetValue(Norm(path), out r)) r.MtimeOverride = raw;
            }
        }

        public bool RemoteFileExists(string path) { lock (_lock) return _files.ContainsKey(Norm(path)); }
        public bool RemoteDirExists(string path) { lock (_lock) return DirExistsLocked(Norm(path)); }

        public string RemoteText(string path)
        {
            lock (_lock)
            {
                FileRec r;
                if (!_files.TryGetValue(Norm(path), out r)) return null;
                return new UTF8Encoding(false).GetString(r.Data);
            }
        }

        public byte[] RemoteBytes(string path)
        {
            lock (_lock)
            {
                FileRec r;
                if (!_files.TryGetValue(Norm(path), out r)) return null;
                return (byte[])r.Data.Clone();
            }
        }

        public DateTime RemoteMtime(string path)
        {
            lock (_lock)
            {
                FileRec r;
                if (!_files.TryGetValue(Norm(path), out r)) return DateTime.MinValue;
                return r.MtimeUtc;
            }
        }

        /// <summary>匹配 <c>&lt;prefix&gt;.bak.&lt;ts&gt;</c> 的远端备份名（找不到 ⇒ null）。</summary>
        public string FindRemoteBackup(string pathPrefix)
        {
            string p = Norm(pathPrefix) + ".bak.";
            lock (_lock)
            {
                foreach (var k in _files.Keys)
                    if (k.StartsWith(p, StringComparison.Ordinal)) return k;
            }
            return null;
        }

        /// <summary>远端某目录下的直接子文件名（不含目录）。</summary>
        public List<string> RemoteChildren(string dirPath)
        {
            string d = Norm(dirPath).TrimEnd('/') + "/";
            var outp = new List<string>();
            lock (_lock)
            {
                foreach (var k in _files.Keys)
                {
                    if (!k.StartsWith(d, StringComparison.Ordinal)) continue;
                    string rest = k.Substring(d.Length);
                    if (rest.Length == 0 || rest.IndexOf('/') >= 0) continue;
                    outp.Add(rest);
                }
            }
            return outp;
        }

        // ================================================ 传输层

        internal sealed class HttpResponseData
        {
            public int Status = 200;
            public byte[] Body = new byte[0];
            public string ContentType = "";
            public string LastModified = "";
            public string WwwAuthenticate = "";
        }

        private void Loop()
        {
            if (_listener != null) LoopHttpListener();
            else if (_tcp != null) LoopTcpListener();
        }

        private void LoopHttpListener()
        {
            while (!_stop)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { if (_stop) return; continue; }

                var c = ctx;
                var t = new Thread(delegate () { ServeHttpListener(c); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void ServeHttpListener(HttpListenerContext ctx)
        {
            try
            {
                var req = ctx.Request;
                byte[] body = ReadAll(req.InputStream, req.ContentLength64);

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string k in req.Headers.AllKeys)
                    if (k != null) headers[k] = req.Headers[k];

                string raw = StripQuery(req.RawUrl ?? "/");
                var resp = Handle(req.HttpMethod, raw, Decode(Norm(raw)), headers, body);

                ctx.Response.StatusCode = resp.Status;
                if (resp.ContentType.Length > 0) ctx.Response.ContentType = resp.ContentType;
                if (resp.LastModified.Length > 0) ctx.Response.Headers["Last-Modified"] = resp.LastModified;
                if (resp.WwwAuthenticate.Length > 0) ctx.Response.Headers["WWW-Authenticate"] = resp.WwwAuthenticate;
                ctx.Response.ContentLength64 = resp.Body.Length;
                if (resp.Body.Length > 0) ctx.Response.OutputStream.Write(resp.Body, 0, resp.Body.Length);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                LastServerError = ex.GetType().Name + ": " + ex.Message;
                try { ctx.Response.Abort(); } catch { }
            }
        }

        /// <summary>
        /// 第三层降级：手写最小 HTTP/1.1。
        /// **不支持 <c>Transfer-Encoding: chunked</c>**（只认 Content-Length）—— 本进程的客户端
        /// 一律 <c>ContentLength</c> 显式给长度，且 <c>KeepAlive=false</c>（每请求一连接、响应带 Connection: close）。
        /// </summary>
        private void LoopTcpListener()
        {
            while (!_stop)
            {
                TcpClient cli;
                try { cli = _tcp.AcceptTcpClient(); }
                catch { if (_stop) return; continue; }

                var c2 = cli;
                var t = new Thread(delegate () { ServeTcp(c2); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void ServeTcp(TcpClient cli)
        {
            try
            {
                using (cli)
                using (var ns = cli.GetStream())
                {
                    string head = ReadHead(ns);
                    if (head.Length == 0) return;

                    string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);
                    string[] parts = lines[0].Split(' ');
                    if (parts.Length < 2) return;

                    string method = parts[0];
                    string raw = StripQuery(parts[1]);

                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length; i++)
                    {
                        int c = lines[i].IndexOf(':');
                        if (c <= 0) continue;
                        headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
                    }

                    int clen;
                    int.TryParse(headers.ContainsKey("Content-Length") ? headers["Content-Length"] : "0",
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out clen);

                    byte[] body = new byte[0];
                    if (clen > 0)
                    {
                        body = new byte[clen];
                        int got = 0;
                        while (got < clen)
                        {
                            int n = ns.Read(body, got, clen - got);
                            if (n <= 0) break;
                            got += n;
                        }
                    }

                    var resp = Handle(method, raw, Decode(Norm(raw)), headers, body);

                    var sb = new StringBuilder();
                    sb.Append("HTTP/1.1 ").Append(resp.Status).Append(' ').Append(ReasonOf(resp.Status)).Append("\r\n");
                    if (resp.ContentType.Length > 0) sb.Append("Content-Type: ").Append(resp.ContentType).Append("\r\n");
                    if (resp.LastModified.Length > 0) sb.Append("Last-Modified: ").Append(resp.LastModified).Append("\r\n");
                    if (resp.WwwAuthenticate.Length > 0) sb.Append("WWW-Authenticate: ").Append(resp.WwwAuthenticate).Append("\r\n");
                    sb.Append("Content-Length: ").Append(resp.Body.Length).Append("\r\n");
                    sb.Append("Connection: close\r\n\r\n");

                    byte[] hb = Encoding.ASCII.GetBytes(sb.ToString());
                    ns.Write(hb, 0, hb.Length);
                    if (resp.Body.Length > 0) ns.Write(resp.Body, 0, resp.Body.Length);
                    ns.Flush();
                }
            }
            catch { }
        }

        private static string ReadHead(Stream s)
        {
            var ms = new MemoryStream();
            int state = 0;
            while (state < 4)
            {
                int b = s.ReadByte();
                if (b < 0) break;
                ms.WriteByte((byte)b);
                if ((state == 0 || state == 2) && b == '\r') state++;
                else if ((state == 1 || state == 3) && b == '\n') state++;
                else state = 0;
                if (ms.Length > 65536) break;
            }
            return Encoding.ASCII.GetString(ms.ToArray());
        }

        private static byte[] ReadAll(Stream s, long len)
        {
            if (s == null || len <= 0) return new byte[0];
            var ms = new MemoryStream();
            byte[] buf = new byte[8192];
            long left = len;
            while (left > 0)
            {
                int n = s.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0) break;
                ms.Write(buf, 0, n);
                left -= n;
            }
            return ms.ToArray();
        }

        // ================================================ 请求分发

        private HttpResponseData Handle(string method, string rawPath, string path,
                                        Dictionary<string, string> headers, byte[] body)
        {
            RecordRequest(method, rawPath);

            var resp = new HttpResponseData();

            if (RequireAuth && !AuthOk(headers))
            {
                resp.Status = 401;
                resp.WwwAuthenticate = "Basic realm=\"runever-selftest\"";
                RecordStatus(401);
                return resp;
            }

            string m = (method ?? "").ToUpperInvariant();
            if (m == "PROPFIND") return Propfind(path, headers, resp);
            if (m == "GET") return Get(path, resp);
            if (m == "PUT") return Put(path, body, resp);
            if (m == "MKCOL") return Mkcol(path, resp);
            if (m == "MOVE") return Move(path, headers, resp);
            if (m == "DELETE") return Delete(path, resp);

            resp.Status = 501;
            RecordStatus(501);
            return resp;
        }

        private HttpResponseData Propfind(string path, Dictionary<string, string> headers, HttpResponseData resp)
        {
            if (SleepMsOnPropfind > 0) Thread.Sleep(SleepMsOnPropfind);

            string depth = headers.ContainsKey("Depth") ? headers["Depth"] : "1";

            lock (_lock)
            {
                if (Propfind404OnDirs && depth == "0" && !_files.ContainsKey(path))
                {
                    resp.Status = 404;                       // 模拟：目录探测不可用，只能靠 MKCOL 探
                }
                else if (_files.ContainsKey(path))
                {
                    var one = new List<KeyValuePair<string, bool>>();
                    one.Add(new KeyValuePair<string, bool>(path, false));
                    resp.Body = Xml(one);
                    resp.Status = 207;
                }
                else if (DirExistsLocked(path))
                {
                    var list = new List<KeyValuePair<string, bool>>();
                    list.Add(new KeyValuePair<string, bool>(path, true));
                    if (depth != "0")
                    {
                        string prefix = path.TrimEnd('/') + "/";
                        var keys = new List<string>(_files.Keys);
                        keys.Sort(StringComparer.Ordinal);
                        foreach (var k in keys)
                        {
                            if (!k.StartsWith(prefix, StringComparison.Ordinal)) continue;
                            string rest = k.Substring(prefix.Length);
                            if (rest.Length == 0 || rest.IndexOf('/') >= 0) continue;
                            list.Add(new KeyValuePair<string, bool>(k, false));
                        }
                        var ds = new List<string>(_dirs);
                        ds.Sort(StringComparer.Ordinal);
                        foreach (var d in ds)
                        {
                            if (!d.StartsWith(prefix, StringComparison.Ordinal)) continue;
                            string rest = d.Substring(prefix.Length);
                            if (rest.Length == 0 || rest.IndexOf('/') >= 0) continue;
                            list.Add(new KeyValuePair<string, bool>(d, true));
                        }
                    }
                    resp.Body = Xml(list);
                    resp.Status = 207;
                }
                else
                {
                    resp.Status = 404;
                }
            }

            resp.ContentType = "application/xml; charset=utf-8";
            RecordStatus(resp.Status);
            return resp;
        }

        private HttpResponseData Get(string path, HttpResponseData resp)
        {
            lock (_lock)
            {
                FileRec r;
                if (!_files.TryGetValue(path, out r))
                {
                    resp.Status = 404;
                }
                else
                {
                    resp.Body = (byte[])r.Data.Clone();
                    resp.LastModified = RawMtimeOf(r);
                    resp.ContentType = "application/octet-stream";
                    resp.Status = 200;
                }
            }
            RecordStatus(resp.Status);
            return resp;
        }

        private HttpResponseData Put(string path, byte[] body, HttpResponseData resp)
        {
            if (FailPut500)
            {
                resp.Status = 500;
                RecordStatus(500);
                return resp;
            }
            if (SleepMsOnPut > 0) Thread.Sleep(SleepMsOnPut);

            lock (_lock)
            {
                if (!DirExistsLocked(ParentOf(path)))
                {
                    resp.Status = 409;
                }
                else
                {
                    bool existed = _files.ContainsKey(path);
                    var rec = new FileRec();
                    rec.Data = body ?? new byte[0];
                    // ★ 模拟真实服务端：PUT 后 mtime = **服务器当前时间**（防乒乓的根源）
                    rec.MtimeUtc = DateTime.UtcNow;
                    _files[path] = rec;
                    resp.Status = existed ? 204 : 201;
                }
            }
            RecordStatus(resp.Status);
            return resp;
        }

        private HttpResponseData Mkcol(string path, HttpResponseData resp)
        {
            lock (_lock)
            {
                if (path == "/" || DirExistsLocked(path))
                {
                    resp.Status = 405;              // 已存在
                }
                else if (!DirExistsLocked(ParentOf(path)))
                {
                    resp.Status = 409;
                }
                else
                {
                    _dirs.Add(path);
                    resp.Status = 201;
                }
            }
            RecordStatus(resp.Status);
            return resp;
        }

        private HttpResponseData Move(string path, Dictionary<string, string> headers, HttpResponseData resp)
        {
            string dest = headers.ContainsKey("Destination") ? headers["Destination"] : "";
            string ovw = headers.ContainsKey("Overwrite") ? headers["Overwrite"] : "T";
            string destPath = PathOfAbsoluteUrl(dest);

            lock (_lock)
            {
                LastMoveDestination = dest;
                LastMoveOverwrite = ovw;

                FileRec r;
                if (!_files.TryGetValue(path, out r)) resp.Status = 404;
                else if (destPath.Length == 0) resp.Status = 400;
                else if (_files.ContainsKey(destPath) && string.Equals(ovw, "F", StringComparison.OrdinalIgnoreCase)) resp.Status = 412;
                else if (!DirExistsLocked(ParentOf(destPath))) resp.Status = 409;
                else
                {
                    _files[destPath] = r;
                    _files.Remove(path);
                    resp.Status = 201;
                }
            }
            RecordStatus(resp.Status);
            return resp;
        }

        private HttpResponseData Delete(string path, HttpResponseData resp)
        {
            lock (_lock)
            {
                if (_files.Remove(path)) resp.Status = 204;
                else if (_dirs.Remove(path)) resp.Status = 204;
                else resp.Status = 404;
            }
            RecordStatus(resp.Status);
            return resp;
        }

        private static string Norm(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/";
            string p = path.Replace('\\', '/');
            if (p[0] != '/') p = "/" + p;
            while (p.Length > 1 && p.EndsWith("/", StringComparison.Ordinal)) p = p.Substring(0, p.Length - 1);
            return p;
        }

        // ================================================ 辅助

        private static string StripQuery(string url)
        {
            if (url == null) return "/";
            int q = url.IndexOf('?');
            if (q >= 0) url = url.Substring(0, q);
            return url.Length == 0 ? "/" : url;
        }

        private static string Decode(string s)
        {
            if (s == null) return "/";
            try { return Uri.UnescapeDataString(s); }
            catch { return s; }
        }

        private static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string ParentOf(string path)
        {
            string p = Norm(path).TrimEnd('/');
            int i = p.LastIndexOf('/');
            if (i <= 0) return "/";
            return p.Substring(0, i);
        }

        private bool DirExistsLocked(string path)
        {
            if (path == "/") return true;
            if (_dirs.Contains(path)) return true;
            string prefix = path.TrimEnd('/') + "/";
            foreach (var k in _files.Keys)
                if (k.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }

        private static string ReasonOf(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 204: return "No Content";
                case 207: return "Multi-Status";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 412: return "Precondition Failed";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                default: return "Status";
            }
        }

        private static string HttpDate(DateTime utc)
        {
            // "R" = RFC1123，**星期名与日期自洽**（错误星期名由用例按路径单独注入）
            return utc.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
        }

        private string RawMtimeOf(FileRec r)
        {
            if (r.MtimeOverride != null && r.MtimeOverride.Length > 0) return r.MtimeOverride;
            return HttpDate(r.MtimeUtc);
        }

        private static string EncodeHref(string path)
        {
            var sb = new StringBuilder();
            foreach (var seg in Norm(path).Split('/'))
            {
                if (seg.Length == 0) continue;
                sb.Append('/').Append(Uri.EscapeDataString(seg));
            }
            return sb.Length == 0 ? "/" : sb.ToString();
        }

        private static string PathOfAbsoluteUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            try
            {
                var u = new Uri(url);
                return Norm(u.AbsolutePath);
            }
            catch { return ""; }
        }

        /// <summary>
        /// PROPFIND 响应：**故意按 <c>response</c> 序号轮换三种命名空间形态**
        /// （<c>D:</c> / <c>d:</c> / 无前缀无命名空间）—— 钉死"必须按 LocalName 匹配"这条。
        /// </summary>
        private static readonly string[] NsPrefixes = new string[] { "D:", "d:", "" };

        /// <summary>三种命名空间形态各被用过多少次（自检报告里打印，证明轮换真的发生了）。</summary>
        private static readonly int[] NsUse = new int[3];

        /// <summary>供报告打印的形态计数摘要。</summary>
        public static string NsUseSummary()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < NsPrefixes.Length; i++)
            {
                if (i > 0) sb.Append(" / ");
                sb.Append(string.IsNullOrEmpty(NsPrefixes[i]) ? "(无前缀无命名空间)" : NsPrefixes[i]);
                sb.Append("=").Append(NsUse[i]);
            }
            return sb.ToString();
        }

        private byte[] Xml(List<KeyValuePair<string, bool>> items)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.Append("<D:multistatus xmlns:D=\"DAV:\" xmlns:d=\"DAV:\">");

            for (int i = 0; i < items.Count; i++)
            {
                int nsIdx = i % NsPrefixes.Length;
                NsUse[nsIdx]++;                    // ★ 计数：报告里看得见三种形态真的都被用到
                string p = NsPrefixes[nsIdx];
                string path = items[i].Key;
                bool isDir = items[i].Value;

                string href = EncodeHref(path);
                if (isDir && href != "/") href += "/";

                string lm = "";
                long len = 0;
                if (!isDir)
                {
                    FileRec rec;
                    lock (_lock) rec = _files.ContainsKey(path) ? _files[path] : null;
                    if (rec != null)
                    {
                        lm = RawMtimeOf(rec);
                        len = rec.Data.Length;
                    }
                }

                sb.Append('<').Append(p).Append("response>");
                sb.Append('<').Append(p).Append("href>").Append(Esc(href))
                  .Append("</").Append(p).Append("href>");
                sb.Append('<').Append(p).Append("propstat><").Append(p).Append("prop>");

                if (isDir)
                {
                    sb.Append('<').Append(p).Append("resourcetype><").Append(p)
                      .Append("collection/></").Append(p).Append("resourcetype>");
                }
                else
                {
                    sb.Append('<').Append(p).Append("getlastmodified>").Append(Esc(lm))
                      .Append("</").Append(p).Append("getlastmodified>");
                    sb.Append('<').Append(p).Append("getcontentlength>")
                      .Append(len.ToString(CultureInfo.InvariantCulture))
                      .Append("</").Append(p).Append("getcontentlength>");
                    sb.Append('<').Append(p).Append("resourcetype/>");
                }

                sb.Append("</").Append(p).Append("prop><").Append(p)
                  .Append("status>HTTP/1.1 200 OK</").Append(p).Append("status>");
                sb.Append("</").Append(p).Append("propstat>");
                sb.Append("</").Append(p).Append("response>");
            }

            sb.Append("</D:multistatus>");
            return new UTF8Encoding(false).GetBytes(sb.ToString());
        }

        private bool AuthOk(Dictionary<string, string> headers)
        {
            string h;
            if (headers == null || !headers.TryGetValue("Authorization", out h) || h == null) return false;
            if (!h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
            try
            {
                string raw = Encoding.UTF8.GetString(Convert.FromBase64String(h.Substring(6).Trim()));
                return string.Equals(raw, AuthUser + ":" + AuthPass, StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private void RecordRequest(string method, string rawPath)
        {
            lock (_lock)
            {
                _methods.Add((method ?? "").ToUpperInvariant());
                _requests.Add((method ?? "").ToUpperInvariant() + " " + (rawPath ?? ""));
            }
        }

        private void RecordStatus(int s)
        {
            lock (_lock) _statuses.Add(s);
        }

        /// <summary>统计某一轮同步（自 <paramref name="fromIndex"/> 起）里的某类请求数。</summary>
        public int CountMethodsSince(int fromIndex, params string[] methods)
        {
            var all = MethodsCopy();
            int n = 0;
            for (int i = fromIndex; i < all.Count; i++)
                foreach (var m in methods)
                    if (string.Equals(all[i], m, StringComparison.Ordinal)) { n++; break; }
            return n;
        }

        /// <summary>自 <paramref name="fromIndex"/> 起的方法序列是否包含给定**子序列**。</summary>
        public bool HasSubsequence(int fromIndex, params string[] want)
        {
            var all = MethodsCopy();
            int w = 0;
            for (int i = fromIndex; i < all.Count && w < want.Length; i++)
                if (string.Equals(all[i], want[w], StringComparison.Ordinal)) w++;
            return w == want.Length;
        }

        public bool HasStatus(int code)
        {
            var all = StatusesCopy();
            for (int i = 0; i < all.Count; i++) if (all[i] == code) return true;
            return false;
        }
    }

    /// <summary>
    /// <c>--selftest-webdav &lt;root&gt; &lt;out.txt&gt;</c> —— §4 的 22 条用例矩阵，逐条断言。
    ///
    /// 退出码：有 <c>[FAIL]</c> ⇒ 1；只有 <c>[SKIP]</c>+<c>[PASS]</c> ⇒ 0，但**输出必须显式标
    /// <c>[SKIP] 原因:</c> 且汇总明确写"未全绿"**（[SKIP] 不许当 [PASS] 用）。
    /// 所有临时数据在 <see cref="Path.GetTempPath"/> 下自建自清，**不碰仓库 config\**。
    /// </summary>
    public static class WebDavSyncSelfTest
    {
        private const int ClientTimeoutMs = 4000;

        /// <summary>脱敏用例的探针口令（必须只出现在内存与 auto.conf 里）。</summary>
        private const string ProbePass = "S3cr3tP@ss-7788";

        public static int Run(string[] args)
        {
            string rootArg = args != null && args.Length >= 2 ? args[1] : "";
            string outPath = args != null && args.Length >= 3 ? args[2] : null;

            var rep = new SelftestReport();
            var ctx = new Ctx();
            ctx.Report = rep;
            ctx.Scratch = Path.Combine(Path.GetTempPath(),
                "runever-webdav-selftest-" + System.Diagnostics.Process.GetCurrentProcess().Id
                + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

            rep.Line("=== RunEver WebDAV 配置同步自检（--selftest-webdav）===");
            rep.Line("root(参数)=" + (string.IsNullOrEmpty(rootArg) ? "(未给)" : rootArg));
            rep.Line("scratch=" + ctx.Scratch + "  （临时目录，自建自清；不污染仓库 config\\）");
            rep.Line("客户端超时=" + ClientTimeoutMs + "ms（仅超时用例用短超时；生产默认 "
                     + ConfigSync.DefaultTimeoutMs + "ms）");

            SelftestServer srv = null;
            SelftestServer srvTcp = null;
            string reason = "";
            try
            {
                Directory.CreateDirectory(ctx.Scratch);
                srv = SelftestServer.TryStart(out reason, false);
                ctx.Srv = srv;

                if (srv == null)
                {
                    // ★ 全失败才 [SKIP]，且整体**不许报绿**
                    rep.Line("server=(启动失败)");
                    rep.Blank();
                    for (int i = 1; i <= 22; i++)
                        rep.Skipped(i, "用例 " + i, reason);
                }
                else
                {
                    // 401 挑战用例专用（TcpListener 手写 HTTP，才能发 WWW-Authenticate）
                    string reasonTcp = "";
                    srvTcp = SelftestServer.TryStart(out reasonTcp, false, true);
                    ctx.SrvTcp = srvTcp;

                    rep.Line("server=" + srv.BaseUrl + " 传输=" + srv.Transport);
                    rep.Line("server(401 挑战用)=" + (srvTcp == null
                        ? "(启动失败: " + reasonTcp + ")"
                        : srvTcp.BaseUrl + " 传输=" + srvTcp.Transport));
                    rep.Blank();
                    RunCases(ctx);
                }
            }
            catch (Exception ex)
            {
                rep.Line("[FAIL] 自检框架异常：" + ex.GetType().Name + " " + ex.Message);
                rep.Fail++;
            }
            finally
            {
                try { if (srv != null) srv.Dispose(); } catch { }
                try { if (srvTcp != null) srvTcp.Dispose(); } catch { }
            }

            // ---- 脱敏最终复查：报告全文（= out.txt 内容）里不许出现口令 ----
            string finalText = rep.Text();
            if (srv != null && finalText.IndexOf(ProbePass, StringComparison.Ordinal) >= 0)
            {
                rep.Line("[FAIL] 22-final 最终 out.txt/报告全文口令复查：发现口令出现（严重）");
                rep.Fail++;
                finalText = rep.Text();
            }

            rep.Blank();
            rep.Line("结果: 用例 " + (rep.Pass + rep.Fail + rep.Skip) + " 条  PASS=" + rep.Pass
                     + "  FAIL=" + rep.Fail + "  SKIP=" + rep.Skip
                     + (rep.AllGreen ? "  —— 全绿" : "  —— 未全绿（存在 FAIL 或 SKIP）"));
            rep.Line("命名空间形态实跑计数（LocalName 匹配的判别力证据）："
                + SelftestServer.NsUseSummary());
            if (rep.SkipReasons.Count > 0)
                rep.Line("（以上 " + rep.SkipReasons.Count + " 条 [SKIP] 已逐条标注原因，[SKIP] 未计入 [PASS]）");
            finalText = rep.Text();

            try { Console.WriteLine(finalText); } catch { }

            if (!string.IsNullOrEmpty(outPath))
            {
                try
                {
                    string d = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
                    File.WriteAllText(outPath, finalText + Environment.NewLine, new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    try { Console.Error.WriteLine("写 out.txt 失败: " + ex.Message); } catch { }
                }
            }

            try { if (Directory.Exists(ctx.Scratch)) Directory.Delete(ctx.Scratch, true); } catch { }

            return rep.Fail > 0 ? 1 : 0;
        }

        // ============================================================ 上下文与工具

        private sealed class Ctx
        {
            public string Scratch;
            public SelftestReport Report;
            public SelftestServer Srv;
            public SelftestServer SrvTcp;      // 401 挑战专用（TcpListener）
            public readonly List<string> AllLogs = new List<string>();
            private readonly List<string> _lastLog = new List<string>();

            public string NewRoot(int n)
            {
                string root = Path.Combine(Scratch, "case" + n.ToString("D2", CultureInfo.InvariantCulture));
                if (Directory.Exists(root)) Directory.Delete(root, true);
                Directory.CreateDirectory(Path.Combine(root, "config", "functions"));
                return root;
            }

            /// <summary>写一份 auto.conf；<paramref name="url"/> = null 表示**不含** webdavUrl 键。</summary>
            public void AutoConf(string root, string url, string user, string pass, string dir)
            {
                var sb = new StringBuilder();
                sb.Append("# selftest auto.conf（临时目录，不是仓库配置）").Append('\n');
                if (url != null) sb.Append("webdavUrl=").Append(url).Append('\n');
                sb.Append("webdavUser=").Append(user ?? "").Append('\n');
                sb.Append("webdavPass=").Append(pass ?? "").Append('\n');
                if (dir != null) sb.Append("webdavDir=").Append(dir).Append('\n');
                sb.Append("syncOnStart=0").Append('\n');
                File.WriteAllText(Path.Combine(root, "config", "auto.conf"), sb.ToString(), new UTF8Encoding(false));
            }

            public static void WriteLocal(string root, string rel, string content, DateTime mtimeUtc)
            {
                string p = ConfigSync.LocalPathOf(root, rel);
                string d = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(d) && !Directory.Exists(d)) Directory.CreateDirectory(d);
                File.WriteAllBytes(p, new UTF8Encoding(false).GetBytes(content ?? ""));
                File.SetLastWriteTimeUtc(p, mtimeUtc);
            }

            public static string ReadLocal(string root, string rel)
            {
                string p = ConfigSync.LocalPathOf(root, rel);
                if (!File.Exists(p)) return null;
                return new UTF8Encoding(false).GetString(File.ReadAllBytes(p));
            }

            public ConfigSync.Result RunSync(string root, int timeoutMs)
            {
                _lastLog.Clear();
                var r = ConfigSync.Run(root, delegate (string s) { _lastLog.Add(s); AllLogs.Add(s); }, timeoutMs);
                return r;
            }

            public bool LastLogHas(string needle)
            {
                foreach (var s in _lastLog)
                    if (s != null && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                return false;
            }
        }

        private static void Assert(bool cond, string msg)
        {
            if (!cond) throw new SelftestAssertException(msg);
        }

        private static void AssertEq(string got, string want, string what)
        {
            if (!string.Equals(got, want, StringComparison.Ordinal))
                throw new SelftestAssertException(what + " 期望[" + want + "] 实际["
                    + (got == null ? "<null>" : got) + "]");
        }

        /// <summary>config 目录快照：文件名 + 长度 + mtime + 内容字节（零动作/原子性断言用）。</summary>
        private static string Snapshot(string dir)
        {
            if (!Directory.Exists(dir)) return "(no dir)";
            var files = new List<string>(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                sb.Append(f.Substring(dir.Length)).Append('|')
                  .Append(new FileInfo(f).Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(File.GetLastWriteTimeUtc(f).Ticks.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(Convert.ToBase64String(File.ReadAllBytes(f))).Append('\n');
            }
            return sb.ToString();
        }

        private static string FindLocalBackup(string dir, string fileNamePrefix)
        {
            if (!Directory.Exists(dir)) return null;
            var files = new List<string>(Directory.GetFiles(dir, fileNamePrefix + ".bak.*"));
            files.Sort(StringComparer.Ordinal);
            return files.Count > 0 ? files[files.Count - 1] : null;
        }

        private static void Case(SelftestReport rep, int n, string name, Action body)
        {
            try
            {
                body();
                rep.Case(n, name, true, "");
            }
            catch (SelftestAssertException ex)
            {
                rep.Case(n, name, false, ex.Message);
            }
            catch (Exception ex)
            {
                rep.Case(n, name, false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // ============================================================ 22 条用例

        private static void RunCases(Ctx ctx)
        {
            SelftestReport rep = ctx.Report;
            SelftestServer srv = ctx.Srv;
            SelftestServer srvA = ctx.SrvTcp;      // 401 挑战必须走 TcpListener
            DateTime baseT = WebDavTime.TruncateToSecond(DateTime.UtcNow);

            // ★ 把基准时间往前挪 1 小时（守门 v7 中2 的整改）：
            //   原先 baseT = UtcNow，假服务器 PUT 后也把 mtime 设成 UtcNow ⇒
            //   "没对齐"与"已对齐"只差约 1 秒，落在 ±2s 容差内 ⇒ 用例 20 的
            //   计数断言（第二轮 Up==0）可能**假绿**，只有精确时刻断言才能判红。
            //   基准前移后，未对齐的差值达 1 小时，远超容差 ⇒ 计数断言本身就有判别力。
            baseT = baseT.AddHours(-1);
            string fnDirName = "functions";

            // ---------------------------------------------------- 1
            Case(rep, 1, "未配置 = 零动作（0 请求 + config 快照完全不变）", delegate ()
            {
                string root = ctx.NewRoot(1);
                ctx.AutoConf(root, null, "", "", "RunEver");       // ★ 没有 webdavUrl 键
                Ctx.WriteLocal(root, "menus.conf", "M1", baseT);

                srv.Reset();
                int before = srv.RequestCount;
                string snapBefore = Snapshot(Path.Combine(root, "config"));

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok && r.NotConfigured, "状态应为「未配置」且 Ok=true，实际 status=" + r.Status);
                Assert(r.Up == 0 && r.Down == 0 && r.Backup == 0, "不得有任何传输/备份计数");
                Assert(srv.RequestCount - before == 0, "假服务器必须收到 0 个请求，实际 "
                    + (srv.RequestCount - before));
                AssertEq(Snapshot(Path.Combine(root, "config")), snapBefore,
                    "config 目录文件清单/长度/mtime/内容");
            });

            // ---------------------------------------------------- 2
            Case(rep, 2, "本地新 → 上传（远端被覆盖为本地内容）", delegate ()
            {
                string root = ctx.NewRoot(2);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "menus.conf", "LOCAL-V2", baseT);
                srv.PutRemote("/RunEver/menus.conf", "REMOTE-V1", baseT.AddSeconds(-60));

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "应成功：" + r.ErrorKind + " " + r.Message);
                Assert(r.Up == 1, "上传数应为 1，实际 " + r.Up);
                AssertEq(srv.RemoteText("/RunEver/menus.conf"), "LOCAL-V2", "远端内容");
            });

            // ---------------------------------------------------- 3
            Case(rep, 3, "远端新 → 下载（本地内容与 mtime 都被对齐）", delegate ()
            {
                string root = ctx.NewRoot(3);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "menus.conf", "LOCAL-V1", baseT.AddSeconds(-60));
                srv.PutRemote("/RunEver/menus.conf", "REMOTE-V2", baseT);

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "应成功：" + r.ErrorKind + " " + r.Message);
                Assert(r.Down == 1, "下载数应为 1，实际 " + r.Down);
                AssertEq(Ctx.ReadLocal(root, "menus.conf"), "REMOTE-V2", "本地内容");
                DateTime lm = File.GetLastWriteTimeUtc(ConfigSync.LocalPathOf(root, "menus.conf"));
                Assert(WebDavTime.TruncateToSecond(lm) == baseT,
                    "本地 mtime 应对齐远端（" + baseT.ToString("o") + "），实际 " + lm.ToString("o"));
            });

            // ---------------------------------------------------- 4
            Case(rep, 4, "单边存在（仅本地）→ 上传", delegate ()
            {
                string root = ctx.NewRoot(4);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "functions/only-local.ahk", "OL", baseT);

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok && r.Up == 1, "应上传 1 个：" + r.ErrorKind + " 实际 Up=" + r.Up);
                AssertEq(srv.RemoteText("/RunEver/functions/only-local.ahk"), "OL", "远端内容");
            });

            // ---------------------------------------------------- 5
            Case(rep, 5, "单边存在（仅远端）→ 下载", delegate ()
            {
                string root = ctx.NewRoot(5);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                srv.PutRemote("/RunEver/functions/only-remote.ahk", "OR", baseT);

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok && r.Down == 1, "应下载 1 个：" + r.ErrorKind + " 实际 Down=" + r.Down);
                AssertEq(Ctx.ReadLocal(root, "functions/only-remote.ahk"), "OR", "本地内容");
            });

            // ---------------------------------------------------- 6
            Case(rep, 6, "两端一致 → 跳过（0 PUT / 0 GET）", delegate ()
            {
                string root = ctx.NewRoot(6);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "menus.conf", "SAME", baseT);
                srv.PutRemote("/RunEver/menus.conf", "SAME", baseT);

                int idx = srv.RequestCount;
                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "应成功：" + r.ErrorKind);
                Assert(r.Skip >= 1, "应计入跳过，实际 Skip=" + r.Skip);
                Assert(srv.CountMethodsSince(idx, "PUT", "GET") == 0,
                    "不得有 PUT/GET，实际 " + srv.CountMethodsSince(idx, "PUT", "GET"));
            });

            // ---------------------------------------------------- 7
            Case(rep, 7, "容差窗口（±1s 内视为同一版本）→ 跳过", delegate ()
            {
                string root = ctx.NewRoot(7);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "menus.conf", "SAME", baseT);
                srv.PutRemote("/RunEver/menus.conf", "SAME", baseT.AddSeconds(-1));

                int idx = srv.RequestCount;
                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "应成功：" + r.ErrorKind);
                Assert(r.Skip >= 1 && r.Up == 0 && r.Down == 0, "1s 差应落进 ±2s 容差窗口");
                Assert(srv.CountMethodsSince(idx, "PUT", "GET") == 0, "不得有 PUT/GET");
            });

            // ---------------------------------------------------- 8
            Case(rep, 8, "大小不同 + mtime 相等 → 本地胜（PUT）+ 远端先备份", delegate ()
            {
                string root = ctx.NewRoot(8);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "menus.conf", "AAAA", baseT);
                srv.PutRemote("/RunEver/menus.conf", "BB", baseT);

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "应成功：" + r.ErrorKind);
                Assert(r.Up == 1, "本地胜应上传，实际 Up=" + r.Up);
                string bak = srv.FindRemoteBackup("/RunEver/menus.conf");
                Assert(bak != null, "远端备份 <name>.bak.<ts> 必须存在");
                AssertEq(srv.RemoteText(bak), "BB", "远端备份内容（应为覆盖前的旧值）");
                AssertEq(srv.RemoteText("/RunEver/menus.conf"), "AAAA", "远端内容");
                Assert(ctx.LastLogHas("local-wins-by-size"), "日志必须写明原因 local-wins-by-size");
            });

            // ---------------------------------------------------- 9
            Case(rep, 9, "本地备份确实生成，且内容是覆盖前的旧值", delegate ()
            {
                string root = ctx.NewRoot(9);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "menus.conf", "OLD-LOCAL", baseT.AddSeconds(-60));
                srv.PutRemote("/RunEver/menus.conf", "NEW-REMOTE", baseT);

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok && r.Down == 1, "应下载 1 个：" + r.ErrorKind + " Down=" + r.Down);
                Assert(r.Backup >= 1, "备份计数应 >=1，实际 " + r.Backup);
                string bak = FindLocalBackup(Path.Combine(root, "config"), "menus.conf");
                Assert(bak != null, "本地 config\\menus.conf.bak.<ts> 必须存在");
                AssertEq(new UTF8Encoding(false).GetString(File.ReadAllBytes(bak)), "OLD-LOCAL",
                    "本地备份内容（应为覆盖前的旧值）");
            });

            // ---------------------------------------------------- 10
            Case(rep, 10, "远端覆盖前备份：MOVE + Destination + Overwrite: F", delegate ()
            {
                string root = ctx.NewRoot(10);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "menus.conf", "NEW-LOCAL", baseT);
                srv.PutRemote("/RunEver/menus.conf", "OLD-REMOTE", baseT.AddSeconds(-60));

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok && r.Up == 1, "应上传 1 个：" + r.ErrorKind + " Up=" + r.Up);
                Assert(srv.CountMethodsSince(0, "MOVE") == 1, "应恰好 1 次 MOVE，实际 "
                    + srv.CountMethodsSince(0, "MOVE"));
                Assert(srv.LastMoveDestination.Length > 0, "MOVE 必须带 Destination 头");
                AssertEq(srv.LastMoveOverwrite, "F", "MOVE Overwrite 头");
                string bak = srv.FindRemoteBackup("/RunEver/menus.conf");
                Assert(bak != null, "远端备份必须存在");
                AssertEq(srv.RemoteText(bak), "OLD-REMOTE", "远端备份内容（覆盖前旧值）");
            });

            // ---------------------------------------------------- 11
            Case(rep, 11, "目录不存在 → PROPFIND 404 ⇒ MKCOL（含 functions 子目录）", delegate ()
            {
                string root = ctx.NewRoot(11);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();                                   // ★ 空服务器：/RunEver 不存在
                Ctx.WriteLocal(root, "menus.conf", "M1", baseT);

                int idx = srv.RequestCount;
                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "应成功：" + r.ErrorKind + " " + r.Message);
                Assert(srv.HasSubsequence(idx, "PROPFIND", "MKCOL", "MKCOL", "PUT"),
                    "请求序列应为 PROPFIND→MKCOL→MKCOL(functions)→PUT，实际 "
                    + string.Join(",", srv.MethodsCopy().GetRange(idx, srv.MethodsCopy().Count - idx).ToArray()));
                Assert(srv.RemoteDirExists("/RunEver"), "远端 /RunEver 必须存在");
                Assert(srv.RemoteDirExists("/RunEver/" + fnDirName), "远端 functions 子目录必须**无条件先建**");
                Assert(r.Up == 1, "随后 PUT 应成功");
            });

            // ---------------------------------------------------- 12
            Case(rep, 12, "MKCOL 405（已存在）→ 视为成功，继续 PUT", delegate ()
            {
                string root = ctx.NewRoot(12);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                srv.MkDir("/RunEver");
                srv.MkDir("/RunEver/functions");
                srv.Propfind404OnDirs = true;               // ★ 让 MKCOL 真的被调用到（否则 PROPFIND 直接 207）
                Ctx.WriteLocal(root, "menus.conf", "NEW", baseT);
                srv.PutRemote("/RunEver/menus.conf", "OLD", baseT.AddSeconds(-60));

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "405 必须视为已存在，不得中止：" + r.ErrorKind + " " + r.Message);
                Assert(srv.HasStatus(405), "服务器应回过 405（构造条件）；服务器错误=["
                    + srv.LastServerError + "]");
                Assert(r.Up == 1, "后续 PUT 应成功，实际 Up=" + r.Up);
            });

            // ---------------------------------------------------- 13
            Case(rep, 13, "远端 401（无凭据）→ 失败但本地字节+mtime 全不变", delegate ()
            {
                Assert(srvA != null, "401 挑战用例需要 TcpListener 传输，但它未启动");
                string root = ctx.NewRoot(13);
                ctx.AutoConf(root, srvA.BaseUrl, "", "", "RunEver");
                srvA.Reset();
                srvA.RequireAuth = true;                 // ★ Reset 之后再开注入
                srvA.AuthUser = "runever";
                srvA.AuthPass = ProbePass;
                Ctx.WriteLocal(root, "menus.conf", "L1", baseT);

                string snapBefore = Snapshot(Path.Combine(root, "config"));
                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(!r.Ok, "应失败");
                Assert(r.ErrorKind.IndexOf("认证失败", StringComparison.Ordinal) >= 0,
                    "错误种类应为认证失败，实际 " + r.ErrorKind + "；服务器错误=["
                    + srvA.LastServerError + "]");
                AssertEq(Snapshot(Path.Combine(root, "config")), snapBefore, "本地字节+mtime");
            });

            // ---------------------------------------------------- 14
            Case(rep, 14, "远端 401（有凭据）→ .NET 自动重试成功，日志零口令", delegate ()
            {
                Assert(srvA != null, "401 挑战用例需要 TcpListener 传输，但它未启动");
                string root = ctx.NewRoot(14);
                ctx.AutoConf(root, srvA.BaseUrl, "runever", ProbePass, "RunEver");
                srvA.Reset();
                srvA.RequireAuth = true;
                srvA.AuthUser = "runever";
                srvA.AuthPass = ProbePass;
                Ctx.WriteLocal(root, "menus.conf", "L1", baseT);

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "带凭据应自动重试成功：" + r.ErrorKind + " " + r.Message
                    + "；服务器错误=[" + srvA.LastServerError + "]");
                Assert(srvA.HasStatus(401), "服务器应回过 401（证明确实发生了挑战）");
                Assert(r.Up == 1, "最终应上传成功，实际 Up=" + r.Up);
                foreach (var l in ctx.AllLogs)
                    Assert(l == null || l.IndexOf(ProbePass, StringComparison.Ordinal) < 0, "日志不得含口令");
            });

            // ---------------------------------------------------- 15
            Case(rep, 15, "远端 500 → 失败、本地不变、且中止（后续文件未处理）", delegate ()
            {
                string root = ctx.NewRoot(15);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                srv.FailPut500 = true;
                Ctx.WriteLocal(root, "menus.conf", "L1", baseT);
                Ctx.WriteLocal(root, "functions/aaa.ahk", "A1", baseT);

                string snapBefore = Snapshot(Path.Combine(root, "config"));
                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(!r.Ok, "应失败");
                Assert(r.ErrorKind.IndexOf("服务端错误", StringComparison.Ordinal) >= 0
                       && r.ErrorKind.IndexOf("(500)", StringComparison.Ordinal) >= 0,
                    "错误种类应为 服务端错误(500)，实际 " + r.ErrorKind);
                AssertEq(Snapshot(Path.Combine(root, "config")), snapBefore, "本地字节+mtime");
                Assert(!srv.RemoteFileExists("/RunEver/functions/aaa.ahk"),
                    "失败后必须中止本次同步：后续文件 functions/aaa.ahk 不应被处理");
            });

            // ---------------------------------------------------- 23
            Case(rep, 23, "远端备份后 PUT 失败 ⇒ 远端原文件必须回滚（不留空、备份撤回）", delegate ()
            {
                // 守门 v7 中1 的回归：上传流程是「远端 MOVE 备份 → PUT」，
                // PUT 失败若不回滚，远端原路径会**变空**（数据只在 .bak 里）。
                string root = ctx.NewRoot(23);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                srv.PutRemote("/RunEver/menus.conf", "OLD-REMOTE", baseT.AddSeconds(-60));
                Ctx.WriteLocal(root, "menus.conf", "NEW-LOCAL", baseT);   // 本地新 ⇒ 走上传
                srv.FailPut500 = true;

                string snapBefore = Snapshot(Path.Combine(root, "config"));
                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(!r.Ok, "应失败");
                Assert(srv.RemoteFileExists("/RunEver/menus.conf"),
                    "★ 回滚：远端原路径必须仍有文件（否则用户看来该文件消失了）");
                AssertEq(Snapshot(Path.Combine(root, "config")), snapBefore, "本地字节+mtime 不变");
                Assert(r.Backup == 0, "备份计数应被撤回（回滚成功 ⇒ 不再算一次备份），实际 Backup=" + r.Backup);
                Assert(ctx.LastLogHas("远端回滚"), "日志应写明发生了远端回滚");
            });

            // ---------------------------------------------------- 16
            Case(rep, 16, "超时 → 失败但本地不变，且与 401/404 可辨", delegate ()
            {
                string root = ctx.NewRoot(16);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                srv.SleepMsOnPut = 3000;                   // 远大于客户端 1000ms
                Ctx.WriteLocal(root, "menus.conf", "L1", baseT);

                string snapBefore = Snapshot(Path.Combine(root, "config"));
                var r = ctx.RunSync(root, 1000);

                Assert(!r.Ok, "应失败");
                AssertEq(r.ErrorKind, "超时", "错误种类（必须与认证失败/未找到可辨）");
                AssertEq(Snapshot(Path.Combine(root, "config")), snapBefore, "本地字节+mtime");
            });

            // ---------------------------------------------------- 17
            Case(rep, 17, "候选过滤：.bak. 与子目录都不算候选", delegate ()
            {
                string root = ctx.NewRoot(17);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "functions/x.ahk.bak.20261003-120000", "BAK", baseT);
                Ctx.WriteLocal(root, "functions/sub/y.ahk", "SUB", baseT);
                srv.PutRemote("/RunEver/functions/z.ahk.bak.20261003-120000", "RBAK", baseT);
                srv.PutRemote("/RunEver/functions/sub/y2.ahk", "RSUB", baseT);

                var cand = ConfigSync.LocalCandidates(root);
                Assert(!cand.ContainsKey("functions/x.ahk.bak.20261003-120000"), "本地 .bak. 文件不得为候选");
                foreach (var k in cand.Keys)
                    Assert(k.IndexOf("functions/sub", StringComparison.OrdinalIgnoreCase) < 0,
                        "不得递归子目录，实际候选 " + k);

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "应成功：" + r.ErrorKind);
                Assert(!srv.RemoteFileExists("/RunEver/functions/x.ahk.bak.20261003-120000"),
                    "本地 .bak. 文件不得被上传");
                Assert(!File.Exists(Path.Combine(root, "config", "functions", "z.ahk.bak.20261003-120000")),
                    "远端 .bak. 文件不得被下载");
                Assert(!File.Exists(Path.Combine(root, "config", "functions", "sub", "y2.ahk")),
                    "远端子目录文件不得被下载（不递归）");
            });

            // ---------------------------------------------------- 18
            Case(rep, 18, "中文/空格文件名：逐段转义 + round-trip 字节一致", delegate ()
            {
                string root = ctx.NewRoot(18);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "functions/中文 x.ahk", "CONTENT-中文", baseT);

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok && r.Up == 1, "应上传：" + r.ErrorKind + " Up=" + r.Up);
                AssertEq(srv.RemoteText("/RunEver/functions/中文 x.ahk"), "CONTENT-中文", "远端内容（字节一致）");

                bool escaped = false;
                foreach (var req in srv.RequestsCopy())
                    if (req.IndexOf("%E4%B8%AD%E6%96%87%20x.ahk", StringComparison.OrdinalIgnoreCase) >= 0)
                        escaped = true;
                Assert(escaped, "服务器收到的原始路径必须是逐段转义后的 %E4%B8%AD%E6%96%87%20x.ahk");

                // round-trip：远端改新 + mtime 更新 ⇒ 下一轮应 GET 回来
                DateTime localMt = File.GetLastWriteTimeUtc(ConfigSync.LocalPathOf(root, "functions/中文 x.ahk"));
                srv.PutRemote("/RunEver/functions/中文 x.ahk", "FROM-REMOTE", localMt.AddSeconds(60));

                var r2 = ctx.RunSync(root, ClientTimeoutMs);
                Assert(r2.Ok && r2.Down == 1, "round-trip 应下载：" + r2.ErrorKind + " Down=" + r2.Down);
                AssertEq(Ctx.ReadLocal(root, "functions/中文 x.ahk"), "FROM-REMOTE", "round-trip 本地内容");
            });

            // ---------------------------------------------------- 19
            Case(rep, 19, "mtime 坏串 ⇒ SKIP(no-metadata)；错星期名必须能解析", delegate ()
            {
                // ---- 19a 坏串：不崩溃、不动文件、计入跳过（不计失败）
                string root = ctx.NewRoot(19);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "menus.conf", "SAME", baseT);
                srv.PutRemote("/RunEver/menus.conf", "SAME", baseT);
                srv.SetMtimeOverride("/RunEver/menus.conf", "not-a-date-at-all");

                var r = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r.Ok, "坏 mtime 不应导致失败：" + r.ErrorKind + " " + r.Message);
                Assert(r.Up == 0 && r.Down == 0, "不得动文件");
                Assert(r.Skip >= 1, "应计入跳过，实际 Skip=" + r.Skip);
                Assert(ctx.LastLogHas("no-metadata"), "日志应写明 no-metadata");
                AssertEq(Ctx.ReadLocal(root, "menus.conf"), "SAME", "本地内容不变");

                // ---- 19b 错星期名（2026-10-01 其实是周四，故意写 Wed）必须解析成功
                DateTime parsed;
                bool ok = WebDavTime.TryParseHttpDate("Wed, 01 Oct 2026 12:00:00 GMT", out parsed);
                Assert(ok, "错星期名必须先剥逗号前前缀再解析（.NET 校验星期自洽，不剥必 False）");
                Assert(parsed == new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                    "解析结果应为 2026-10-01T12:00:00Z，实际 " + parsed.ToString("o"));

                string root2 = ctx.NewRoot(19);
                ctx.AutoConf(root2, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                DateTime t19 = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
                Ctx.WriteLocal(root2, "menus.conf", "SAME", t19);
                srv.PutRemote("/RunEver/menus.conf", "SAME", t19);
                srv.SetMtimeOverride("/RunEver/menus.conf", "Wed, 01 Oct 2026 12:00:00 GMT");

                var r2 = ctx.RunSync(root2, ClientTimeoutMs);
                Assert(r2.Ok, "应成功：" + r2.ErrorKind);
                Assert(r2.Skip == 1 && r2.Up == 0 && r2.Down == 0,
                    "错星期名应被正常解析为元数据 ⇒ 走「两端一致」跳过，实际 Skip=" + r2.Skip);
                Assert(ctx.LastLogHas("两端一致"), "日志原因应为「两端一致」而不是 no-metadata");
                Assert(!ctx.LastLogHas("no-metadata"), "错星期名不得被当成 mtime 缺失");
            });

            // ---------------------------------------------------- 20  ★ 防乒乓
            Case(rep, 20, "防乒乓：连跑两轮，第二轮 0 上传 0 下载（请求全是 PROPFIND）", delegate ()
            {
                string root = ctx.NewRoot(20);
                ctx.AutoConf(root, srv.BaseUrl, "", "", "RunEver");
                srv.Reset();
                Ctx.WriteLocal(root, "menus.conf", "V1", baseT);
                Ctx.WriteLocal(root, "functions/a.ahk", "A1", baseT);

                var r1 = ctx.RunSync(root, ClientTimeoutMs);
                Assert(r1.Ok, "第一轮应成功：" + r1.ErrorKind + " " + r1.Message);
                Assert(r1.Up == 2, "第一轮应上传 2 个（menus.conf + functions/a.ahk），实际 " + r1.Up);

                // 对齐证据：本地 mtime 必须 == 远端 mtime（秒级）
                string[] rels = new string[] { "menus.conf", "functions/a.ahk" };
                foreach (var rel in rels)
                {
                    string p = ConfigSync.LocalPathOf(root, rel);
                    DateTime lm = WebDavTime.TruncateToSecond(File.GetLastWriteTimeUtc(p));
                    DateTime rm = WebDavTime.TruncateToSecond(srv.RemoteMtime("/RunEver/" + rel));
                    Assert(lm == rm, rel + " 本地 mtime 未对齐远端：local=" + lm.ToString("o")
                        + " remote=" + rm.ToString("o"));
                }

                int idx2 = srv.RequestCount;
                var r2 = ctx.RunSync(root, ClientTimeoutMs);

                Assert(r2.Ok, "第二轮应成功：" + r2.ErrorKind);
                Assert(r2.Up == 0 && r2.Down == 0,
                    "★ 第二轮必须 0 上传 0 下载（否则就是乒乓），实际 Up=" + r2.Up + " Down=" + r2.Down);
                Assert(r2.Skip == 2, "第二轮应全部跳过，实际 Skip=" + r2.Skip);
                Assert(srv.CountMethodsSince(idx2, "PUT", "GET", "MOVE") == 0,
                    "第二轮不得出现 PUT/GET/MOVE");
                int total2 = srv.RequestCount - idx2;
                Assert(srv.CountMethodsSince(idx2, "PROPFIND") == total2,
                    "第二轮的请求必须全是 PROPFIND，实际 " + total2 + " 个里只有 "
                    + srv.CountMethodsSince(idx2, "PROPFIND") + " 个是 PROPFIND");
            });

            // ---------------------------------------------------- 21
            Case(rep, 21, "绑定失败不假绿：强制两层前缀都失败 ⇒ [SKIP] 原因 + 不报绿", delegate ()
            {
                string reason2;
                SelftestServer srv2 = SelftestServer.TryStart(out reason2, true);
                try { Assert(srv2 == null, "强制失败时不应拿到服务器实例"); }
                finally { if (srv2 != null) srv2.Dispose(); }

                Assert(!string.IsNullOrEmpty(reason2), "必须给出可读的 [SKIP] 原因");

                var demo = new SelftestReport();
                demo.Skipped(0, "假 WebDAV 服务器启动", reason2);
                string txt = demo.Text();

                Assert(txt.IndexOf("[SKIP] 原因:", StringComparison.Ordinal) >= 0,
                    "报告必须显式输出 [SKIP] 原因:，实际 [" + txt + "]");
                Assert(!demo.AllGreen, "[SKIP] 不许当 [PASS] 用：整体不得报绿");
                Assert(demo.Skip == 1 && demo.Pass == 0, "SKIP 不得计入 PASS");
            });

            // ---------------------------------------------------- 22
            Case(rep, 22, "脱敏：口令在报告/日志/异常消息/runever-cs.log 四处 0 命中", delegate ()
            {
                // ① 异常/失败消息（故意用错口令 ⇒ 走真实 401 失败路径）
                Assert(srvA != null, "401 挑战用例需要 TcpListener 传输，但它未启动");
                string root = ctx.NewRoot(22);
                ctx.AutoConf(root, srvA.BaseUrl, "runever", ProbePass, "RunEver");
                srvA.Reset();
                srvA.RequireAuth = true;
                srvA.AuthUser = "runever";
                srvA.AuthPass = "wrong-on-purpose";
                Ctx.WriteLocal(root, "menus.conf", "L1", baseT);

                var rf = ctx.RunSync(root, ClientTimeoutMs);
                Assert(!rf.Ok, "故意错的凭据应失败");
                Assert(rf.Message == null || rf.Message.IndexOf(ProbePass, StringComparison.Ordinal) < 0,
                    "失败/异常消息不得含口令");
                Assert(rf.ErrorKind.IndexOf(ProbePass, StringComparison.Ordinal) < 0, "错误种类不得含口令");

                // ② 全部日志行
                foreach (var l in ctx.AllLogs)
                    Assert(l == null || l.IndexOf(ProbePass, StringComparison.Ordinal) < 0, "日志有行含口令");

                // ③ 报告全文（= out.txt 内容）
                Assert(rep.Text().IndexOf(ProbePass, StringComparison.Ordinal) < 0, "报告/out.txt 含口令");

                // ④ scratch 下若真出现 runever-cs.log，同样不许含口令
                var logs = Directory.GetFiles(ctx.Scratch, "runever-cs.log", SearchOption.AllDirectories);
                foreach (var f in logs)
                {
                    string c = new UTF8Encoding(false).GetString(File.ReadAllBytes(f));
                    Assert(c.IndexOf(ProbePass, StringComparison.Ordinal) < 0, f + " 含口令");
                }

                // ⑤ 反向确认：口令确实被读进过内存（否则本用例是"假通过"）
                WebDavConfig cfg = WebDavConfig.Load(root);
                AssertEq(cfg.Pass, ProbePass, "探针口令应确实被读进内存");
                AssertEq(cfg.Describe(), "已配置", "Describe() 只给「已配置/未配置」");
                Assert(cfg.Redact("x" + ProbePass + "y").IndexOf(ProbePass, StringComparison.Ordinal) < 0,
                    "Redact() 必须能抹掉口令");
            });
        }
    }
}
