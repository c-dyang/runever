using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Xml;

namespace RunEverCs.Services
{
    /// <summary>WebDAV 失败的**可辨种类**（W5 要求 401/404/超时分别可辨）。</summary>
    public enum WebDavErrorKind
    {
        None,
        NotConfigured,
        ConnectFail,
        Timeout,
        Auth,
        NotFound,
        Conflict,
        ServerError,
        NotSupported,
        ResponseError
    }

    public sealed class WebDavError
    {
        public WebDavErrorKind Kind = WebDavErrorKind.None;
        public int HttpStatus;
        public string Message = "";

        /// <summary>中文可读种类；带 HTTP 状态码时附括号（如 <c>服务端错误(500)</c>）。</summary>
        public string KindText()
        {
            string t;
            switch (Kind)
            {
                case WebDavErrorKind.NotConfigured: t = "未配置"; break;
                case WebDavErrorKind.ConnectFail: t = "连接失败"; break;
                case WebDavErrorKind.Timeout: t = "超时"; break;
                case WebDavErrorKind.Auth: t = "认证失败"; break;
                case WebDavErrorKind.NotFound: t = "未找到"; break;
                case WebDavErrorKind.Conflict: t = "冲突"; break;
                case WebDavErrorKind.ServerError: t = "服务端错误"; break;
                case WebDavErrorKind.NotSupported: t = "协议不支持"; break;
                case WebDavErrorKind.ResponseError: t = "响应异常"; break;
                default: t = "无"; break;
            }
            if (HttpStatus > 0) t += "(" + HttpStatus.ToString(CultureInfo.InvariantCulture) + ")";
            return t;
        }

        public override string ToString()
        {
            string s = KindText();
            if (!string.IsNullOrEmpty(Message)) s += "：" + Message;
            return s;
        }
    }

    public sealed class WebDavException : Exception
    {
        public readonly WebDavError Error;

        public WebDavException(WebDavError e) : base(e == null ? "" : e.ToString())
        {
            Error = e ?? new WebDavError { Kind = WebDavErrorKind.ResponseError };
        }
    }

    /// <summary>一个远端条目的元数据（字段取不到时 <c>null</c>，调用方据此走"无元数据"分支）。</summary>
    public sealed class WebDavEntry
    {
        /// <summary>条目名（单段，不含 '/'）。</summary>
        public string Name = "";

        /// <summary>相对远端根（<c>webdavDir</c> 之下的）路径，用 '/' 分隔。</summary>
        public string Rel = "";

        public bool IsDir;

        /// <summary>远端 mtime（UTC）；解析失败/缺失 ⇒ null。</summary>
        public DateTime? MtimeUtc;

        /// <summary>远端字节数；缺失 ⇒ null。</summary>
        public long? Length;

        /// <summary>服务端原样给出的 <c>getlastmodified</c> 文本（诊断用）。</summary>
        public string RawMtime = "";
    }

    /// <summary>HTTP 日期解析（§U2 两个坑都在这里钉死）。</summary>
    public static class WebDavTime
    {
        /// <summary>
        /// 解析 <c>getlastmodified</c> / <c>Last-Modified</c>。
        ///
        /// 坑①：**必须先剥掉逗号前的星期前缀** —— <c>.NET</c> 会校验星期名与日期是否自洽，
        ///       "Wed, 01 Oct 2026 12:00:00 GMT"（2026-10-01 其实是周四）直接解析失败。
        /// 坑②：真实服务端两种格式都有（RFC1123 带 GMT / ISO8601 带 Z），两种都要吃。
        /// </summary>
        public static bool TryParseHttpDate(string raw, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (raw == null) return false;

            string s = raw.Trim();
            if (s.Length == 0) return false;

            int comma = s.IndexOf(',');
            string noWeekday = comma >= 0 ? s.Substring(comma + 1).Trim() : s;

            if (TryParseOne(noWeekday, out utc)) return true;

            // 原文再试一次（ISO8601 无逗号时 noWeekday == s，这里是纯保险）
            if (comma >= 0 && TryParseOne(s, out utc)) return true;

            utc = DateTime.MinValue;
            return false;
        }

        private static bool TryParseOne(string s, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrEmpty(s)) return false;

            DateTime t;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out t))
            {
                utc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
                return true;
            }
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal
                    | DateTimeStyles.AllowWhiteSpaces, out t))
            {
                utc = DateTime.SpecifyKind(t, DateTimeKind.Utc);
                return true;
            }
            return false;
        }

        /// <summary>秒级向下截断（比较策略的第一步）。</summary>
        public static DateTime TruncateToSecond(DateTime t)
        {
            long rem = t.Ticks % TimeSpan.TicksPerSecond;
            return new DateTime(t.Ticks - rem, t.Kind);
        }
    }

    /// <summary>
    /// PROPFIND 响应解析。
    ///
    /// 坑③（§U2 实测）：**必须按 <c>LocalName</c> 递归匹配、忽略命名空间前缀**。
    ///   <c>Descendants("D:response")</c> 在类型层就抛（名字里不许有 ':'）；
    ///   <c>GetElementsByTagName(..., "DAV:")</c> 会**漏掉无命名空间**的服务端。
    /// </summary>
    public static class WebDavXml
    {
        public static XmlDocument Load(byte[] data)
        {
            var doc = new XmlDocument();
            doc.XmlResolver = null;
            using (var ms = new MemoryStream(data ?? new byte[0]))
                doc.Load(ms);
            return doc;
        }

        /// <summary>按 LocalName 递归收集（忽略命名空间与前缀）。</summary>
        public static List<XmlElement> Descendants(XmlNode node, string localName)
        {
            var list = new List<XmlElement>();
            if (node != null) Walk(node, localName, list);
            return list;
        }

        private static void Walk(XmlNode node, string ln, List<XmlElement> outp)
        {
            foreach (XmlNode c in node.ChildNodes)
            {
                var e = c as XmlElement;
                if (e == null) continue;
                if (string.Equals(e.LocalName, ln, StringComparison.OrdinalIgnoreCase)) outp.Add(e);
                Walk(e, ln, outp);
            }
        }

        public static XmlElement First(XmlNode node, string localName)
        {
            var l = Descendants(node, localName);
            return l.Count > 0 ? l[0] : null;
        }

        /// <summary>解析一条 <c>&lt;response&gt;</c> 元素（命名空间无关）。</summary>
        public static WebDavEntry ParseResponse(XmlElement resp, string selfRel)
        {
            if (resp == null) return null;
            var e = new WebDavEntry();

            var href = First(resp, "href");
            string rawHref = href == null ? "" : (href.InnerText ?? "").Trim();
            string path;
            try { path = Uri.UnescapeDataString(rawHref); }
            catch { path = rawHref; }
            path = path.Replace('\\', '/').TrimEnd('/');

            int slash = path.LastIndexOf('/');
            e.Name = slash >= 0 ? path.Substring(slash + 1) : path;
            e.Rel = selfRel;

            var rt = First(resp, "resourcetype");
            e.IsDir = rt != null && First(rt, "collection") != null;

            var lm = First(resp, "getlastmodified");
            if (lm != null)
            {
                e.RawMtime = (lm.InnerText ?? "").Trim();
                DateTime utc;
                if (WebDavTime.TryParseHttpDate(e.RawMtime, out utc)) e.MtimeUtc = utc;
            }

            var cl = First(resp, "getcontentlength");
            if (cl != null)
            {
                long n;
                if (long.TryParse((cl.InnerText ?? "").Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out n)) e.Length = n;
            }

            return e;
        }
    }

    /// <summary>
    /// 裸协议 WebDAV 客户端（net48 无 WebDAV 客户端类 ⇒ 自定义动词 + 手写 XML）。
    ///
    /// 协议要点（§U1/§U3 实测，别改）：
    ///   · <c>HttpWebRequest.Method</c> 对 <c>PROPFIND/MKCOL/MOVE</c> **无动词白名单**，可直传
    ///   · **必设** <c>ServicePoint.Expect100Continue=false</c> + <c>Timeout/ReadWriteTimeout</c>
    ///   · 207 属 2xx **不抛**；401 + <c>NetworkCredential</c> 由 .NET 自动重试一次
    ///   · 404/500 ⇒ <c>ProtocolError</c> 且 <c>ex.Response.StatusCode</c> 可读；超时 ⇒ <c>Response==null</c>
    ///   · URL **逐段转义**（<c>EscapeDataString</c> 会把 '/' 转成 %2F ⇒ 只能逐段）+ 字符串拼 + new Uri
    ///     —— **禁** <c>new Uri(base, rel)</c>
    /// </summary>
    public sealed class WebDavClient
    {
        public const string ContentTypeXml = "application/xml; charset=utf-8";
        public const string ContentTypeText = "text/plain; charset=utf-8";

        private const string PropfindBody =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<D:propfind xmlns:D=\"DAV:\"><D:prop>"
            + "<D:getlastmodified/><D:getcontentlength/><D:resourcetype/>"
            + "</D:prop></D:propfind>";

        private static readonly byte[] PropfindBytes = new UTF8Encoding(false).GetBytes(PropfindBody);

        private readonly WebDavConfig _cfg;
        private readonly int _timeoutMs;
        private readonly string _base;
        private readonly string _root;

        public WebDavClient(WebDavConfig cfg) : this(cfg, 8000) { }

        public WebDavClient(WebDavConfig cfg, int timeoutMs)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");
            _cfg = cfg;
            _timeoutMs = timeoutMs > 0 ? timeoutMs : 8000;
            _base = (cfg.Url ?? "").Trim().TrimEnd('/');
            _root = (cfg.Dir ?? "").Trim().Trim('/');
        }

        public int TimeoutMs { get { return _timeoutMs; } }

        internal sealed class Response
        {
            public int Status;
            public byte[] Body = new byte[0];
            public string LastModified = "";
        }

        // ---------------------------------------------------------- URL

        /// <summary>逐段转义拼 URL（绝对）。<paramref name="dir"/> = true 时补尾斜杠。</summary>
        public string BuildUrl(string rel, bool dir)
        {
            var sb = new StringBuilder();
            sb.Append(_base);
            AppendSegs(sb, _root);
            AppendSegs(sb, rel);
            if (dir && !sb.ToString().EndsWith("/", StringComparison.Ordinal)) sb.Append('/');
            return sb.ToString();
        }

        private static void AppendSegs(StringBuilder sb, string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            string[] segs = path.Replace('\\', '/').Split('/');
            foreach (var s in segs)
            {
                if (s.Length == 0) continue;
                sb.Append('/');
                sb.Append(Uri.EscapeDataString(s));   // 逐段！整串转义会把 '/' 变 %2F
            }
        }

        private static string LastSegOf(string url)
        {
            string p = url;
            int q = p.IndexOf('?');
            if (q >= 0) p = p.Substring(0, q);
            p = p.TrimEnd('/');
            int i = p.LastIndexOf('/');
            string seg = i >= 0 ? p.Substring(i + 1) : p;
            try { return Uri.UnescapeDataString(seg); } catch { return seg; }
        }

        // ---------------------------------------------------------- 请求

        private Response Send(string method, string rel, bool dir, string depth,
                              byte[] body, string contentType, Dictionary<string, string> headers)
        {
            string url = BuildUrl(rel, dir);

            HttpWebRequest req;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(new Uri(url));
            }
            catch (Exception ex)
            {
                throw new WebDavException(new WebDavError
                {
                    Kind = WebDavErrorKind.NotConfigured,
                    Message = "远端地址不合法：" + ex.Message
                });
            }

            req.Method = method;
            req.ServicePoint.Expect100Continue = false;   // §U1 必设
            req.Timeout = _timeoutMs;
            req.ReadWriteTimeout = _timeoutMs;
            req.AllowAutoRedirect = false;
            req.UserAgent = "RunEver/1.0";
            req.KeepAlive = false;

            if (!string.IsNullOrEmpty(_cfg.User))
                req.Credentials = new NetworkCredential(_cfg.User, _cfg.Pass ?? "");

            if (!string.IsNullOrEmpty(depth)) req.Headers["Depth"] = depth;
            if (headers != null)
                foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;

            try
            {
                if (body != null)
                {
                    req.ContentType = contentType ?? ContentTypeXml;
                    req.ContentLength = body.Length;
                    using (var ws = req.GetRequestStream()) ws.Write(body, 0, body.Length);
                }

                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    var r = new Response();
                    r.Status = (int)resp.StatusCode;
                    r.LastModified = resp.Headers["Last-Modified"] ?? "";
                    using (var rs = resp.GetResponseStream())
                    {
                        var ms = new MemoryStream();
                        if (rs != null)
                        {
                            byte[] buf = new byte[16384];
                            int n;
                            while ((n = rs.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                        }
                        r.Body = ms.ToArray();
                    }
                    return r;
                }
            }
            catch (WebException we)
            {
                throw MapWeb(we);
            }
            catch (IOException io)
            {
                bool to = (io.Message ?? "").IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                          || (io.Message ?? "").IndexOf("超时", StringComparison.Ordinal) >= 0;
                throw new WebDavException(new WebDavError
                {
                    Kind = to ? WebDavErrorKind.Timeout : WebDavErrorKind.ConnectFail,
                    Message = _cfg.Redact(method + " " + io.Message)
                });
            }
        }

        /// <summary>异常 → 结构化可辨原因（W5：401/404/超时分别可辨）。</summary>
        private WebDavException MapWeb(WebException we)
        {
            var err = new WebDavError();
            var resp = we.Response as HttpWebResponse;

            if (resp != null)
            {
                err.HttpStatus = (int)resp.StatusCode;
                err.Message = "HTTP " + err.HttpStatus + " " + (resp.StatusDescription ?? "");
                switch (err.HttpStatus)
                {
                    case 401: case 403: case 407:
                        err.Kind = WebDavErrorKind.Auth; break;
                    case 404: case 410:
                        err.Kind = WebDavErrorKind.NotFound; break;
                    case 409: case 412: case 423:
                        err.Kind = WebDavErrorKind.Conflict; break;
                    case 405: case 501:
                        err.Kind = WebDavErrorKind.NotSupported; break;
                    default:
                        err.Kind = err.HttpStatus >= 500
                            ? WebDavErrorKind.ServerError : WebDavErrorKind.ResponseError;
                        break;
                }
                return new WebDavException(err);
            }

            // 超时的判据：Status == Timeout 且 Response == null（§U1 实测）
            if (we.Status == WebExceptionStatus.Timeout)
            {
                err.Kind = WebDavErrorKind.Timeout;
                err.Message = "请求超过 " + _timeoutMs + "ms 无响应";
                return new WebDavException(err);
            }

            err.Kind = WebDavErrorKind.ConnectFail;
            err.Message = we.Status + (string.IsNullOrEmpty(we.Message) ? "" : "：" + we.Message);
            err.Message = _cfg.Redact(err.Message);
            return new WebDavException(err);
        }

        private WebDavException MakeStatus(Response r, string what)
        {
            var err = new WebDavError { HttpStatus = r.Status, Message = what + " 返回 HTTP " + r.Status };
            if (r.Status == 401 || r.Status == 403 || r.Status == 407) err.Kind = WebDavErrorKind.Auth;
            else if (r.Status == 404) err.Kind = WebDavErrorKind.NotFound;
            else if (r.Status == 409 || r.Status == 412) err.Kind = WebDavErrorKind.Conflict;
            else if (r.Status == 405 || r.Status == 501) err.Kind = WebDavErrorKind.NotSupported;
            else if (r.Status >= 500) err.Kind = WebDavErrorKind.ServerError;
            else err.Kind = WebDavErrorKind.ResponseError;
            return new WebDavException(err);
        }

        private static XmlDocument ParseDoc(byte[] body)
        {
            try { return WebDavXml.Load(body); }
            catch (Exception ex)
            {
                // ★ XML 整段解析失败 ⇒ 中止；**绝不当作"远端为空"**（否则会凭空上传/覆盖）
                throw new WebDavException(new WebDavError
                {
                    Kind = WebDavErrorKind.ResponseError,
                    Message = "PROPFIND 响应解析失败（不视为远端为空）：" + ex.Message
                });
            }
        }

        // ---------------------------------------------------------- 动词

        /// <summary>读单条元数据。404 ⇒ false（其它错误抛）。</summary>
        public bool TryStat(string rel, out WebDavEntry entry)
        {
            entry = null;
            Response r;
            try { r = Send("PROPFIND", rel, false, "0", PropfindBytes, ContentTypeXml, null); }
            catch (WebDavException ex)
            {
                if (ex.Error.Kind == WebDavErrorKind.NotFound) return false;
                throw;
            }
            if (r.Status != 207 && r.Status != 200) throw MakeStatus(r, "PROPFIND");

            var doc = ParseDoc(r.Body);
            var list = WebDavXml.Descendants(doc.DocumentElement, "response");
            if (list.Count == 0) return false;
            entry = WebDavXml.ParseResponse(list[0], rel);
            return entry != null;
        }

        /// <summary>Depth:1 列目录（不含自身）。</summary>
        public List<WebDavEntry> ListDir(string rel)
        {
            var outp = new List<WebDavEntry>();
            Response r = Send("PROPFIND", rel, true, "1", PropfindBytes, ContentTypeXml, null);
            if (r.Status != 207 && r.Status != 200) throw MakeStatus(r, "PROPFIND");

            string self = LastSegOf(BuildUrl(rel, true));
            var doc = ParseDoc(r.Body);
            foreach (var el in WebDavXml.Descendants(doc.DocumentElement, "response"))
            {
                var e = WebDavXml.ParseResponse(el, rel);
                if (e == null || e.Name.Length == 0) continue;
                if (string.Equals(e.Name, self, StringComparison.OrdinalIgnoreCase)) continue;  // 自身
                outp.Add(e);
            }
            return outp;
        }

        /// <summary>
        /// 确保目录存在：PROPFIND → 404 ⇒ MKCOL；**405/301/302 ⇒ 视为"已存在"成功**；
        /// 403 等 ⇒ 抛（调用方必须本地零动作中止）。
        /// </summary>
        public void EnsureDir(string rel)
        {
            Response r;
            try
            {
                r = Send("PROPFIND", rel, true, "0", PropfindBytes, ContentTypeXml, null);
            }
            catch (WebDavException ex)
            {
                if (ex.Error.Kind != WebDavErrorKind.NotFound) throw;

                // ★ MKCOL 的"已存在"是 405（也有服务端回 301/302）—— 非 2xx 会抛 WebException，
                //   所以必须在这里按状态码认掉，不能靠返回值分支（那条分支永远走不到）。
                try
                {
                    Response mk = Send("MKCOL", rel, true, null, null, null, null);
                    int s = mk.Status;
                    if (s == 200 || s == 201 || s == 204) return;
                    throw MakeStatus(mk, "MKCOL");
                }
                catch (WebDavException mkEx)
                {
                    int hs = mkEx.Error.HttpStatus;
                    if (hs == 405 || hs == 301 || hs == 302) return;   // 已存在 = 成功
                    throw;
                }
            }

            int st = r.Status;
            if (st == 207 || st == 200 || st == 204) return;
            if (st == 405 || st == 301 || st == 302) return;
            throw MakeStatus(r, "PROPFIND");
        }

        /// <summary>GET；<paramref name="lastModifiedUtc"/> 取 <c>Last-Modified</c> 头（对齐用）。</summary>
        public void Download(string rel, out byte[] data, out DateTime? lastModifiedUtc)
        {
            Response r = Send("GET", rel, false, null, null, null, null);
            if (r.Status != 200) throw MakeStatus(r, "GET");

            data = r.Body;
            DateTime utc;
            lastModifiedUtc = WebDavTime.TryParseHttpDate(r.LastModified, out utc) ? (DateTime?)utc : null;
        }

        public void Upload(string rel, byte[] data)
        {
            Response r = Send("PUT", rel, false, null, data ?? new byte[0], ContentTypeText, null);
            if (r.Status < 200 || r.Status > 299) throw MakeStatus(r, "PUT");
        }

        /// <summary>远端覆盖前备份：MOVE 到同目录 <c>&lt;name&gt;.bak.&lt;ts&gt;</c>（Overwrite: F）。</summary>
        public void Move(string srcRel, string dstRel)
        {
            var h = new Dictionary<string, string>();
            h["Destination"] = BuildUrl(dstRel, false);
            h["Overwrite"] = "F";
            Response r = Send("MOVE", srcRel, false, null, null, null, h);
            if (r.Status < 200 || r.Status > 299) throw MakeStatus(r, "MOVE");
        }

        /// <summary>「测试连接」：PROPFIND Depth:0 于 <c>&lt;url&gt;/&lt;dir&gt;/</c>；成功返回 null。</summary>
        public WebDavError TestConnection()
        {
            try
            {
                Send("PROPFIND", "", true, "0", PropfindBytes, ContentTypeXml, null);
                return null;
            }
            catch (WebDavException ex) { return ex.Error; }
            catch (Exception ex) { return new WebDavError { Kind = WebDavErrorKind.ResponseError, Message = ex.Message }; }
        }
    }
}
