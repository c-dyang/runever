using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// 「同步配置」的**唯一定义** —— 托盘（W6/W7）、CLI（<c>--sync</c>，W10）、自检（W9）
    /// 三处**共用这一份**。理由同 <see cref="PathCacheRefresh"/>：两套实现必然漂移。
    ///
    /// 同步范围（W3 定案）：<c>config\menus.conf</c> + <c>config\functions\*.ahk</c>（不递归、
    /// 不含 <c>*.bak.*</c>）。**不含** <c>auto.conf</c> / <c>cache\paths.txt</c>（机器专属）。
    ///
    /// 三条硬纪律：
    ///   1. 未配置 <c>webdavUrl</c> ⇒ **零动作**（不发请求、不建目录、不碰文件），状态="未配置"，退出码 0
    ///   2. 失败**原子无害**：本地写入一律 <c>.tmp</c> + <c>Move</c>，覆盖前必有备份，备份失败即放弃覆盖
    ///   3. **防乒乓**：传输成功后必须把本地 mtime 对齐到远端（PUT 后回读校验），对齐**只发生在成功之后**
    /// </summary>
    public static class ConfigSync
    {
        public const int DefaultTimeoutMs = 8000;

        /// <summary>mtime 比较容差（秒）。低于此值视为"同一版本"，避免无意义互刷。</summary>
        public const int ToleranceSeconds = 2;

        public const string StatusNotConfigured = "未配置";
        public const string StatusOk = "完成";
        public const string StatusFailed = "失败";

        public const string SnapshotRel = "state\\webdav-sync.txt";

        public sealed class Result
        {
            public bool Ok;
            public string Status = "";
            public int Up;
            public int Down;
            public int Skip;
            public int Backup;
            public string ErrorKind = "";
            public string Message = "";
            public List<string> Details = new List<string>();

            public bool NotConfigured { get { return Status == StatusNotConfigured; } }

            public string SummaryLine()
            {
                return "上传=" + Up + " 下载=" + Down + " 跳过=" + Skip + " 备份=" + Backup;
            }
        }

        public static Result Run(string root, Action<string> log)
        {
            return Run(root, log, DefaultTimeoutMs);
        }

        public static Result Run(string root, Action<string> log, int timeoutMs)
        {
            var r = new Result();
            Action<string> lg = log ?? (s => { });

            WebDavConfig cfg;
            try { cfg = WebDavConfig.Load(root); }
            catch (Exception ex)
            {
                r.Ok = false; r.Status = StatusFailed; r.Message = ex.Message;
                r.ErrorKind = "配置读取失败";
                lg("SYNC 配置读取失败：" + ex.Message);
                return r;
            }

            // ============================================================
            // ★ 未配置 = 零动作（W4）。**此分支之前不许有任何网络/文件动作。**
            // ============================================================
            if (!cfg.IsConfigured)
            {
                r.Ok = true;
                r.Status = StatusNotConfigured;
                r.Message = "未配置 WebDAV（webdavUrl 为空），已跳过同步";
                r.Details.Add(r.Message);
                lg("SYNC " + r.Message);
                return r;
            }

            var client = new WebDavClient(cfg, timeoutMs);
            var snap = LoadSnapshot(root);

            try
            {
                lg("SYNC 开始：远端=" + cfg.Describe() + " 远端目录=" + DescribeDir(cfg) + "（口令不写日志）");

                if (cfg.Dir.Length > 0) client.EnsureDir("");
                client.EnsureDir("functions");      // ★ 无条件先建，否则 PUT 到 functions/ 会 409

                var local = LocalCandidates(root);
                var remoteOnly = RemoteOnlyCandidates(client, local);

                var keys = new List<string>();
                keys.Add("menus.conf");
                var fnKeys = new List<string>();
                foreach (var k in local.Keys)
                    if (k.StartsWith("functions/", StringComparison.OrdinalIgnoreCase)) fnKeys.Add(k);
                fnKeys.Sort(StringComparer.OrdinalIgnoreCase);
                keys.AddRange(fnKeys);
                foreach (var k in remoteOnly)
                    if (!keys.Contains(k)) keys.Add(k);

                foreach (var rel in keys)
                {
                    string localPath = LocalPathOf(root, rel);
                    bool localExists = File.Exists(localPath);

                    WebDavEntry re = null;
                    bool remoteExists = client.TryStat(rel, out re);

                    if (!localExists && !remoteExists)
                    {
                        continue;   // 两边都没有：无需动作（也不创建空文件）
                    }

                    string reason;
                    int action = Decide(localPath, localExists, remoteExists, re, snap, out reason);

                    if (action == 0)
                    {
                        r.Skip++;
                        r.Details.Add("跳过 " + RelDisplay(rel) + "（原因: " + reason + "）");
                        lg("SYNC 跳过 " + RelDisplay(rel) + "（原因: " + reason + "）");
                        continue;
                    }

                    if (action > 0)
                    {
                        // 上传：先远端备份（仅当远端已存在）→ PUT → 成功后才对齐 mtime
                        // ★ PUT 失败 ⇒ 把备份 MOVE 回原位再抛（守门 v7 中1：否则远端该路径是空的）
                        string bakRel = remoteExists ? BackupRemote(client, rel, r, lg) : null;
                        byte[] data = File.ReadAllBytes(localPath);
                        try
                        {
                            client.Upload(rel, data);
                        }
                        catch (Exception)
                        {
                            RollbackRemote(client, rel, bakRel, r, lg);
                            throw;
                        }
                        r.Up++;
                        AlignAfterUpload(client, root, localPath, rel, r, lg, snap);
                        r.Details.Add("上传 " + RelDisplay(rel) + "（原因: " + reason + "）");
                        lg("SYNC 上传 " + RelDisplay(rel) + "（原因: " + reason + "）");
                    }
                    else
                    {
                        DownloadToLocal(client, root, localPath, rel, r, lg, snap);
                        r.Details.Add("下载 " + RelDisplay(rel) + "（原因: " + reason + "）");
                        lg("SYNC 下载 " + RelDisplay(rel) + "（原因: " + reason + "）");
                    }
                }

                r.Ok = true;
                r.Status = StatusOk;
                r.Message = "";
                lg("SYNC 完成：" + r.SummaryLine());
            }
            catch (WebDavException ex)
            {
                r.Ok = false;
                r.Status = StatusFailed;
                r.ErrorKind = ex.Error.KindText();
                r.Message = cfg.Redact(ex.Error.Message);
                r.Details.Add("中止：" + r.ErrorKind + " " + r.Message);
                // ★ 一个文件失败 ⇒ 中止本次同步（已完成的保留、未处理的不动）
                lg("SYNC 中止：" + r.ErrorKind + "（已完成的保留，未处理的未动） " + r.Message);
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Status = StatusFailed;
                r.ErrorKind = "本地错误";
                r.Message = cfg.Redact(ex.Message);
                r.Details.Add("中止：" + r.ErrorKind + " " + r.Message);
                lg("SYNC 中止：" + r.ErrorKind + " " + r.Message);
            }

            return r;
        }

        private static string DescribeDir(WebDavConfig cfg)
        {
            return cfg.Dir.Length == 0 ? "(webdavUrl 目录本身)" : cfg.Dir;
        }

        // ============================================================ 决策

        /// <summary>
        /// 返回 1=上传（本地胜）/ -1=下载（远端胜）/ 0=跳过。原因写进 <paramref name="reason"/>。
        ///
        /// 比较策略（定死）：秒级向下截断 + ±<see cref="ToleranceSeconds"/>s 容差 + 大小兜底；
        /// 大小不同但 mtime 在容差内 ⇒ 本地胜 + 远端先备份；
        /// mtime 与大小都缺 ⇒ SKIP(no-metadata)；远端 XML 解析失败在更上层就已中止。
        /// </summary>
        private static int Decide(string localPath, bool localExists, bool remoteExists,
                                  WebDavEntry re, Dictionary<string, Snap> snap, out string reason)
        {
            reason = "";

            if (localExists && !remoteExists) { reason = "仅本地存在"; return 1; }
            if (!localExists && remoteExists) { reason = "仅远端存在"; return -1; }

            long llen = new FileInfo(localPath).Length;
            DateTime lmt = WebDavTime.TruncateToSecond(File.GetLastWriteTimeUtc(localPath));

            DateTime? rmt = re != null && re.MtimeUtc.HasValue
                ? (DateTime?)WebDavTime.TruncateToSecond(re.MtimeUtc.Value) : null;
            long? rlen = re == null ? null : re.Length;

            if (!rmt.HasValue && !rlen.HasValue)
            {
                reason = "no-metadata";
                return 0;
            }

            if (!rmt.HasValue)
            {
                if (rlen.Value == llen)
                {
                    Snap s;
                    if (snap != null && snap.TryGetValue(re.Rel ?? "", out s) && s != null
                        && s.Len == llen && s.Ticks == lmt.Ticks)
                    {
                        reason = "snapshot-match";
                        return 0;
                    }
                    reason = "no-metadata-size-equal";
                    return 0;
                }
                reason = "local-wins-by-size";
                return 1;
            }

            int diff = (int)Math.Round((lmt - rmt.Value).TotalSeconds, MidpointRounding.AwayFromZero);

            if (Math.Abs(diff) <= ToleranceSeconds)
            {
                if (!rlen.HasValue || rlen.Value == llen)
                {
                    reason = "两端一致";
                    return 0;
                }
                reason = "local-wins-by-size";
                return 1;
            }

            if (diff > 0) { reason = "本地新 " + diff + "s"; return 1; }
            reason = "远端新 " + (-diff) + "s";
            return -1;
        }

        // ============================================================ 候选

        /// <summary>本地候选：<c>menus.conf</c> + <c>functions\*.ahk</c>（顶层、非 <c>.bak.</c>）。</summary>
        public static Dictionary<string, string> LocalCandidates(string root)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string conf = Path.Combine(root, "config", "menus.conf");
            if (File.Exists(conf)) d["menus.conf"] = conf;

            string fnDir = Path.Combine(root, "config", "functions");
            if (Directory.Exists(fnDir))
            {
                foreach (var f in Directory.GetFiles(fnDir, "*.ahk"))
                {
                    string name = Path.GetFileName(f);
                    if (name.IndexOf(".bak.", StringComparison.OrdinalIgnoreCase) >= 0) continue;  // 不把备份当候选
                    d["functions/" + name] = f;
                }
            }
            return d;
        }

        /// <summary>远端有、本地没有的候选（只认与本地同形的名字：不递归、非 .bak.）。</summary>
        private static List<string> RemoteOnlyCandidates(WebDavClient client, Dictionary<string, string> local)
        {
            var outp = new List<string>();

            List<WebDavEntry> rootList = SafeList(client, "");
            foreach (var e in rootList)
            {
                if (e.IsDir) continue;
                if (!string.Equals(e.Name, "menus.conf", StringComparison.OrdinalIgnoreCase)) continue;
                AddIfNew(outp, local, "menus.conf");
            }

            List<WebDavEntry> fnList = SafeList(client, "functions");
            foreach (var e in fnList)
            {
                if (e.IsDir) continue;
                if (!e.Name.EndsWith(".ahk", StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Name.IndexOf(".bak.", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                AddIfNew(outp, local, "functions/" + e.Name);
            }

            return outp;
        }

        private static void AddIfNew(List<string> outp, Dictionary<string, string> local, string rel)
        {
            if (local.ContainsKey(rel)) return;
            if (outp.Contains(rel)) return;
            outp.Add(rel);
        }

        private static List<WebDavEntry> SafeList(WebDavClient client, string rel)
        {
            try { return client.ListDir(rel); }
            catch (WebDavException ex)
            {
                if (ex.Error.Kind == WebDavErrorKind.NotFound) return new List<WebDavEntry>();
                throw;
            }
        }

        public static string LocalPathOf(string root, string rel)
        {
            if (string.Equals(rel, "menus.conf", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(root, "config", "menus.conf");
            if (rel.StartsWith("functions/", StringComparison.OrdinalIgnoreCase))
                return Path.Combine(root, "config", "functions", rel.Substring("functions/".Length));
            return Path.Combine(root, "config", rel.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>日志里显示的本地形态路径（<c>config\functions\x.ahk</c>）。</summary>
        public static string RelDisplay(string rel)
        {
            return "config\\" + rel.Replace('/', '\\');
        }

        // ============================================================ 备份 / 对齐 / 快照

        /// <summary>
        /// 远端覆盖前备份：同目录 <c>MOVE</c> 到 <c>&lt;name&gt;.bak.&lt;ts&gt;</c>（Overwrite: F）。
        /// **MOVE 失败 ⇒ 放弃这次上传**（不许裸 PUT 覆盖）。
        /// ★ 返回备份后的远端相对路径（调用方在 PUT 失败时用它回滚）。
        /// </summary>
        private static string BackupRemote(WebDavClient client, string rel, Result r, Action<string> lg)
        {
            string bakRel = rel + ".bak." + WebDavConfig.StampNow();
            try
            {
                client.Move(rel, bakRel);
            }
            catch (WebDavException ex)
            {
                throw new WebDavException(new WebDavError
                {
                    Kind = ex.Error.Kind,
                    HttpStatus = ex.Error.HttpStatus,
                    Message = "远端备份失败（" + bakRel + "），放弃覆盖 " + RelDisplay(rel) + "：" + ex.Error.Message
                });
            }
            r.Backup++;
            lg("SYNC 远端备份 " + bakRel);
            return bakRel;
        }

        /// <summary>
        /// 远端回滚：把备份 MOVE 回原路径。
        ///
        /// ★ 为什么必须做（守门 v7 中1）：上传流程是「远端备份 MOVE → PUT」，
        ///   MOVE 成功后 PUT 若失败（网络/500/超时），**远端原路径会是空的** ——
        ///   数据虽在 .bak 里、本地也安全，但远端这个文件在用户看来"消失了"。
        ///   成功路径不受影响（PUT 成功后备份就该留着当历史）。
        /// ★ 回滚本身也可能失败（同样网络不通）⇒ 只记日志不抛，并把备份路径写进明细，
        ///   让人能手工 MOVE 回来 —— 绝不因为"回滚失败"而吞掉原始错误。
        /// </summary>
        private static void RollbackRemote(WebDavClient client, string rel, string bakRel,
            Result r, Action<string> lg)
        {
            if (string.IsNullOrEmpty(bakRel)) return;
            try
            {
                client.Move(bakRel, rel);
                r.Backup--;                       // 备份已被撤回到原位，不再算一次备份
                lg("SYNC 远端回滚：" + bakRel + " → " + RelDisplay(rel) + "（PUT 失败，已恢复原文件）");
                r.Details.Add("远端回滚 " + RelDisplay(rel) + "（PUT 失败，备份已 MOVE 回原位）");
            }
            catch (Exception ex)
            {
                lg("SYNC ⚠ 远端回滚失败：" + RelDisplay(rel) + " 原文件在 " + bakRel
                    + "，请手工 MOVE 回来（原因：" + ex.Message + "）");
                r.Details.Add("⚠ 远端回滚失败，原文件暂存于 " + bakRel + "，需手工恢复");
            }
        }

        /// <summary>
        /// 下载：先备份本地 → 写 <c>.tmp</c> + 校验大小 → <c>Move</c> 替换（原子）→ 对齐 mtime。
        /// 中途任何一步失败，**原文件字节与 mtime 都不变**（备份/临时文件是无害副作用）。
        /// </summary>
        private static void DownloadToLocal(WebDavClient client, string root, string localPath,
                                            string rel, Result r, Action<string> lg,
                                            Dictionary<string, Snap> snap)
        {
            byte[] data;
            DateTime? lm;
            client.Download(rel, out data, out lm);      // 抛 ⇒ 本地此时尚未被碰过

            string dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // ① 覆盖前必须有备份；备份失败 ⇒ 放弃覆盖
            if (File.Exists(localPath))
            {
                string bak = localPath + ".bak." + WebDavConfig.StampNow();
                try { File.Copy(localPath, bak, false); }
                catch (Exception ex)
                {
                    throw new IOException("本地备份失败（" + Path.GetFileName(bak) + "），放弃覆盖 "
                        + RelDisplay(rel) + "：" + ex.Message);
                }
                r.Backup++;
                lg("SYNC 本地备份 " + Path.GetFileName(bak));
            }

            // ② 原子写（照抄 GeneralPage.cs 的 .tmp 范式）
            string tmp = localPath + ".tmp";
            if (File.Exists(tmp)) File.Delete(tmp);
            File.WriteAllBytes(tmp, data);
            long got = new FileInfo(tmp).Length;
            if (got != data.Length)
            {
                try { File.Delete(tmp); } catch { }
                throw new IOException("临时文件大小校验失败（" + got + " != " + data.Length + "）");
            }
            if (File.Exists(localPath)) File.Delete(localPath);
            File.Move(tmp, localPath);
            r.Down++;

            // ③ 对齐本地 mtime —— 只在成功之后（GET 的 Last-Modified 优先，回退 PROPFIND）
            DateTime? mt = lm;
            if (!mt.HasValue)
            {
                WebDavEntry e;
                if (client.TryStat(rel, out e)) mt = e.MtimeUtc;
            }
            ApplyLocalMtime(root, localPath, rel, mt, snap, lg);
        }

        /// <summary>
        /// PUT 成功后的**唯一**对齐点：PROPFIND Depth:0 回读远端 mtime 写回本地 <c>LastWriteTimeUtc</c>。
        /// 不做这一步 ⇒ 服务端 mtime = 服务器当前时间 ⇒ 每轮互刷、不报错、永不收敛（防乒乓）。
        /// </summary>
        private static void AlignAfterUpload(WebDavClient client, string root, string localPath,
                                             string rel, Result r, Action<string> lg,
                                             Dictionary<string, Snap> snap)
        {
            DateTime? remoteMtime = null;
            try
            {
                WebDavEntry e;
                if (client.TryStat(rel, out e) && e.MtimeUtc.HasValue) remoteMtime = e.MtimeUtc;
            }
            catch (WebDavException ex)
            {
                lg("SYNC 回读远端 mtime 失败（转快照兜底）：" + ex.Error.KindText());
            }
            ApplyLocalMtime(root, localPath, rel, remoteMtime, snap, lg);
        }

        private static void ApplyLocalMtime(string root, string localPath, string rel,
                                            DateTime? remoteMtime, Dictionary<string, Snap> snap,
                                            Action<string> lg)
        {
            bool aligned = false;
            if (remoteMtime.HasValue)
            {
                try
                {
                    File.SetLastWriteTimeUtc(localPath, WebDavTime.TruncateToSecond(remoteMtime.Value));
                    aligned = true;
                }
                catch (Exception ex)
                {
                    lg("SYNC 本地 mtime 对齐失败（忽略）：" + ex.Message);
                }
            }

            snap[rel] = new Snap
            {
                Ticks = File.GetLastWriteTimeUtc(localPath).Ticks,
                Len = new FileInfo(localPath).Length
            };
            SaveSnapshot(root, snap);

            if (aligned) lg("SYNC 已对齐本地 mtime → 远端（防乒乓）");
            else lg("SYNC 远端 mtime 取不到，落快照 " + SnapshotRel);
        }

        /// <summary>远端元数据取不到时的兜底快照（<c>state\webdav-sync.txt</c>）。</summary>
        public sealed class Snap
        {
            public long Ticks;
            public long Len;
        }

        private static Dictionary<string, Snap> LoadSnapshot(string root)
        {
            var d = new Dictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);
            try
            {
                string p = Path.Combine(root, "state", "webdav-sync.txt");
                if (!File.Exists(p)) return d;

                foreach (var raw in File.ReadAllLines(p, Encoding.UTF8))
                {
                    if (raw == null) continue;
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length < 3) continue;

                    long t, l;
                    if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) continue;
                    if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) continue;
                    d[parts[0]] = new Snap { Ticks = t, Len = l };
                }
            }
            catch { }
            return d;
        }

        private static void SaveSnapshot(string root, Dictionary<string, Snap> snap)
        {
            try
            {
                string dir = Path.Combine(root, "state");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string p = Path.Combine(dir, "webdav-sync.txt");

                var sb = new StringBuilder();
                sb.Append("# RunEver WebDAV 同步快照：rel<TAB>utcTicks<TAB>len\n");
                var keys = new List<string>(snap.Keys);
                keys.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var k in keys)
                {
                    var s = snap[k];
                    sb.Append(k).Append('\t')
                      .Append(s.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(s.Len.ToString(CultureInfo.InvariantCulture)).Append('\n');
                }

                string tmp = p + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(p)) File.Delete(p);
                File.Move(tmp, p);
            }
            catch { }
        }
    }

    /// <summary><c>--sync [root]</c> CLI 入口 —— 与托盘入口**共用同一份** <see cref="ConfigSync.Run"/>。</summary>
    public static class SyncCli
    {
        public static int Run(string defaultRoot, string[] args)
        {
            string root = defaultRoot;
            if (args != null && args.Length >= 2 && !string.IsNullOrEmpty(args[1]) && Directory.Exists(args[1]))
                root = args[1];

            Console.WriteLine("root=" + root);

            WebDavConfig cfg = WebDavConfig.Load(root);
            // ★ 只打印"已配置/未配置"，**绝不打印 URL 全文与口令**
            Console.WriteLine("webdavUrl=" + cfg.Describe());
            Console.WriteLine("远端目录=" + (cfg.Dir.Length == 0 ? "(webdavUrl 目录本身)" : cfg.Dir));

            var r = ConfigSync.Run(root, s =>
            {
                Console.Error.WriteLine(s);
                AppendAppLog(root, s);        // ★ CLI 也写 state\runever-cs.log（与托盘同一份，排障不用两头找）
            });

            Console.WriteLine("状态=" + r.Status + (r.ErrorKind.Length > 0 ? " 错误=" + r.ErrorKind : ""));
            foreach (var d in r.Details) Console.WriteLine("  " + d);
            Console.WriteLine(r.SummaryLine());
            return r.Ok ? 0 : 1;
        }

        /// <summary>
        /// 把 CLI 的同步日志追加进 <c>state\runever-cs.log</c>（与托盘路径同一份日志）。
        /// 守门 v7 低-3：此前 `--sync` 只写 stderr ⇒ 事后排障时"托盘干的"在日志里、
        /// "命令行干的"什么都没有。任何写失败都不许影响同步本身。
        /// </summary>
        private static void AppendAppLog(string root, string line)
        {
            try
            {
                string dir = Path.Combine(root, "state");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "runever-cs.log"),
                    DateTime.Now.ToString("HH:mm:ss") + " [cli] " + line + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
