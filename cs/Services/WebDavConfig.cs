using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// v7 WebDAV 同步配置 —— **只读** <c>config\auto.conf</c> 取 5 个键。
    ///
    /// 纪律：
    ///   · auto.conf **只读不写**（同文件另有 GeneralPage.WriteConf 这个作者，双作者必漂移）
    ///   · <see cref="Pass"/> 只许留在内存里：不进日志 / 气泡 / out.txt / 异常消息
    ///   · 未配置（<c>webdavUrl</c> 空）⇒ 调用方必须**零动作**返回，不是错误
    /// </summary>
    public sealed class WebDavConfig
    {
        public const string KeyUrl = "webdavUrl";
        public const string KeyUser = "webdavUser";
        public const string KeyPass = "webdavPass";
        public const string KeyDir = "webdavDir";
        public const string KeySyncOnStart = "syncOnStart";

        public const string DefaultDir = "RunEver";

        /// <summary>
        /// 备份时间戳格式 —— **全局唯一定义点**（本地 <c>&lt;name&gt;.bak.&lt;ts&gt;</c> 与远端
        /// <c>MOVE</c> 备份共用）。要改格式只改这里一处。
        /// </summary>
        public const string BackupStampFormat = "yyyyMMdd-HHmmss";

        /// <summary>远端根 URL（已 rstrip '/'）。空 = 未配置。</summary>
        public string Url = "";

        public string User = "";
        public string Pass = "";

        /// <summary>远端根下的子目录（空 = 直接用 Url 指向的目录）。</summary>
        public string Dir = DefaultDir;

        /// <summary>auto.conf 里是否出现过 <c>webdavDir</c> 键（区分"缺失=默认"与"存在但为空"）。</summary>
        public bool DirSeen;

        /// <summary>启动时自动同步（仅在托盘就绪后异步跑）。</summary>
        public bool SyncOnStart;

        /// <summary>未配置 ⇒ 连一个 HTTP 请求都不许发。</summary>
        public bool IsConfigured
        {
            get { return Url != null && Url.Trim().Length > 0; }
        }

        /// <summary>可安全写进日志的描述（**绝不含 URL 全文与口令**）。</summary>
        public string Describe()
        {
            return IsConfigured ? "已配置" : "未配置";
        }

        public static string Stamp(DateTime t)
        {
            return t.ToString(BackupStampFormat, System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string StampNow()
        {
            return Stamp(DateTime.Now);
        }

        /// <summary>读 <c>&lt;root&gt;\config\auto.conf</c>；文件缺失 ⇒ 未配置对象（不抛）。</summary>
        public static WebDavConfig Load(string root)
        {
            string conf = Path.Combine(root, "config", "auto.conf");
            if (!File.Exists(conf)) return new WebDavConfig();

            string[] lines;
            try { lines = File.ReadAllLines(conf, Encoding.UTF8); }
            catch { return new WebDavConfig(); }

            return FromLines(lines);
        }

        /// <summary>按行解析（与 GeneralPage.ReadConf 同款口径：跳过注释/无 '=' 行）。</summary>
        public static WebDavConfig FromLines(IEnumerable<string> lines)
        {
            var c = new WebDavConfig();
            if (lines == null) return c;

            foreach (var raw in lines)
            {
                if (raw == null) continue;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();

                if (string.Equals(k, KeyUrl, StringComparison.OrdinalIgnoreCase))
                {
                    c.Url = v.TrimEnd('/');
                }
                else if (string.Equals(k, KeyUser, StringComparison.OrdinalIgnoreCase))
                {
                    c.User = v;
                }
                else if (string.Equals(k, KeyPass, StringComparison.OrdinalIgnoreCase))
                {
                    c.Pass = v;
                }
                else if (string.Equals(k, KeyDir, StringComparison.OrdinalIgnoreCase))
                {
                    // 键"缺失" ⇒ 默认 RunEver；键"存在但为空" ⇒ 直接用 webdavUrl 指向的目录
                    // （auto.conf 注释里写明的语义，两种情形必须分开）
                    c.DirSeen = true;
                    c.Dir = v.Trim().Trim('/');
                }
                else if (string.Equals(k, KeySyncOnStart, StringComparison.OrdinalIgnoreCase))
                {
                    c.SyncOnStart = IsTruthy(v);
                }
            }

            if (c.Dir == null) c.Dir = "";
            if (c.Dir.Length == 0 && !c.DirSeen) c.Dir = DefaultDir;   // 键缺失 ⇒ 默认 RunEver
            return c;
        }

        /// <summary>真值判定：1 / true / yes / on（大小写不敏感）。</summary>
        public static bool IsTruthy(string v)
        {
            if (v == null) return false;
            v = v.Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }

        /// <summary>口令脱敏用：把值里可能出现的口令替换成 ***（兜底，正常路径根本不该拼进去）。</summary>
        public string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            if (string.IsNullOrEmpty(Pass)) return text;
            return text.Replace(Pass, "***");
        }
    }
}
