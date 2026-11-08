using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// 写盘结果（段 1.6 · B1）：成功给「字节数 + SHA256」，失败给「明确原因」，
    /// 非致命异常（备份失败 / 回滚失败）走 Warning —— 三者都要能被状态行与日志看到。
    /// </summary>
    public class ConfWriteResult
    {
        public bool Ok;
        public string Path = "";
        public int Bytes;
        public string Sha256 = "";
        public string Backup = "";        // 本次生成的备份（无备份 = ""）
        public bool RolledBack;           // 回读校验失败后是否成功回滚
        public string Warning = "";       // 非致命但必须留痕（备份失败 / 回滚失败）
        public string Error = "";         // 致命原因（Ok=false 时非空）
        public int KeyCount;

        public string Sha8
        {
            get { return Sha256.Length >= 8 ? Sha256.Substring(0, 8) : Sha256; }
        }

        /// <summary>状态行文本（UI 唯一出口）。★ 失败文案必须含「保存失败」四个字：
        /// GeneralPage.SaveThenCheck() 以该子串判定"测试连接 / 立即同步"能否继续。</summary>
        public string StatusText()
        {
            if (!Ok) return "✗ 保存失败：" + Error;
            string s = "已保存 " + DateTime.Now.ToString("HH:mm:ss")
                + "（" + Bytes + " 字节 · SHA256 " + Sha8 + "）";
            if (!string.IsNullOrEmpty(Warning)) s += "  ⚠ " + Warning;
            return s;
        }

        /// <summary>日志 detail（单行；不含口令 —— 值一律不落日志，只落键名与文件事实）。</summary>
        public string LogDetail()
        {
            string s = "conf=" + Path + " 键数=" + KeyCount;
            if (Ok)
            {
                s += " 字节=" + Bytes + " sha256=" + Sha256;
                s += " 备份=" + (string.IsNullOrEmpty(Backup) ? "<无（写前文件不存在）>" : Backup);
            }
            else
            {
                s += " 失败原因=" + Error;
                s += " 文件事实=字节" + Bytes + " sha256=" + Sha8
                    + (RolledBack ? "（已回滚到备份）" : "");
            }
            if (!string.IsNullOrEmpty(Warning)) s += " 警告=" + Warning;
            return s;
        }
    }

    /// <summary>
    /// auto.conf 写盘（段 1.6 · B1：原 GeneralPage.WriteConf 的 WPF 无关核心）。
    ///
    /// 三件套（项目纪律）：备份 → 原子写 → 回读校验/回滚。
    /// ★ 为什么从 GeneralPage 里抽出来：留在 WPF 类里就只能"打开窗口点一下"才验得到，
    ///   自检无法驱动；抽成纯 IO 类后三条路径（成功 / 校验失败回滚 / 写不进去）
    ///   都能在临时 root 上做实证，且**每次写盘自动落一条 settings-write.log**。
    /// ★ 本方法**不抛**（异常一律收进 ConfWriteResult.Error）—— 调用方少一个静默出口。
    /// </summary>
    public static class AutoConfStore
    {
        /// <param name="confPath">目标文件（如 &lt;root&gt;\config\auto.conf）。</param>
        /// <param name="vals">要落盘的键值（其余行原样保留）。</param>
        /// <param name="sanitize">落 UI / 日志前的脱敏钩子（口令红线）；null = 不脱敏。</param>
        /// <param name="root">日志 root（null = 不写 settings-write.log，仅自检内部用）。</param>
        public static ConfWriteResult Write(string confPath,
            Dictionary<string, string> vals,
            Func<string, string> sanitize = null,
            string root = null)
        {
            var r = new ConfWriteResult();
            r.Path = confPath ?? "";
            r.KeyCount = vals == null ? 0 : vals.Count;

            try
            {
                // ---- 1) 重排全文：保留注释骨架，只改值行；缺失的键补到尾部 ----
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var outLines = new List<string>();

                if (File.Exists(confPath))
                {
                    foreach (var raw in File.ReadAllLines(confPath, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        {
                            outLines.Add(raw);
                            continue;
                        }
                        int eq = line.IndexOf('=');
                        if (eq <= 0) { outLines.Add(raw); continue; }

                        string key = line.Substring(0, eq).Trim();
                        string v;
                        if (vals.TryGetValue(key, out v))
                        {
                            outLines.Add(key + "=" + v);
                            seen.Add(key);
                        }
                        else
                        {
                            outLines.Add(raw);
                        }
                    }
                }

                foreach (var kv in vals)
                    if (!seen.Contains(kv.Key))
                        outLines.Add(kv.Key + "=" + kv.Value);

                // ---- 2) 备份（★ 失败不再无声：记 Warning，写盘继续 —— 理由见下）----
                //   为什么备份失败仍继续写：备份是"后悔药"，不是写盘前置条件。
                //   备份失败通常是权限/占用类问题，紧接着的原子写会以异常形式暴露出来
                //   （那时 Ok=false + Failure 原因 + Warning 双留痕）；若在这里直接拒写，
                //   反而制造一次"用户改了没生效"的假象 —— 那正是本轮要消灭的东西。
                //   ⚠ 但"无备份可回滚"必须写进日志与状态行，绝不静默。
                string backup = null;
                try
                {
                    if (File.Exists(confPath))
                    {
                        backup = confPath + ".bak." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        File.Copy(confPath, backup, true);
                        PruneBackups(confPath + ".bak.", 10);
                        r.Backup = backup;
                    }
                }
                catch (Exception bex)
                {
                    backup = null;
                    r.Backup = "";
                    r.Warning = "备份失败（已继续写，但本次不可回滚）：" + San(sanitize, bex.Message);
                }

                // ---- 3) 原子写（临时件 → 替换）----
                string tmp = confPath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
                File.WriteAllText(tmp, string.Join(Environment.NewLine, outLines) + Environment.NewLine,
                    new UTF8Encoding(false));
                if (File.Exists(confPath)) File.Delete(confPath);
                File.Move(tmp, confPath);

                // ---- 4) 回读校验：读不回刚写的键值 ⇒ 回滚（有备份时）----
                var check = Read(confPath);
                foreach (var kv in vals)
                {
                    string got;
                    if (!check.TryGetValue(kv.Key, out got)
                        || !string.Equals(got, kv.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        string msg = "写入校验失败：" + kv.Key + " 期望[" + kv.Value + "] 实际["
                            + (got ?? "<缺失>") + "]";
                        if (backup != null)
                        {
                            try
                            {
                                File.Copy(backup, confPath, true);
                                r.RolledBack = true;
                                msg += "（已回滚到备份）";
                            }
                            catch (Exception rex)
                            {
                                // ★ 回滚失败 = 文件停在"校验不过"的状态上，必须最显眼
                                msg += "（★ 回滚失败：" + rex.Message + "；备份仍在 " + backup
                                    + "，请手动恢复）";
                                r.Warning = Join(r.Warning,
                                    "回滚失败（文件可能停在错误内容上）：" + San(sanitize, rex.Message));
                            }
                        }
                        else
                        {
                            msg += "（★ 无备份可回滚 —— 备份阶段就失败了："
                                + (r.Warning.Length == 0 ? "原因未知" : r.Warning) + "）";
                        }

                        r.Ok = false;
                        r.Error = San(sanitize, msg);
                        return Finish(r, confPath);
                    }
                }

                r.Ok = true;
                return Finish(r, confPath);
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Error = San(sanitize, "写盘异常（" + ex.GetType().Name + "）：" + ex.Message);
                return Finish(r, confPath);
            }
            finally
            {
                // ★ 留痕是**每条路径**的义务（成功/失败/回滚都在这里落一次）
                if (!string.IsNullOrEmpty(root))
                    SettingsWriteLog.Append(root, "save", r.Ok ? "OK" : "FAIL", r.LogDetail());
            }
        }

        /// <summary>补齐"最终文件事实"（写前/写后/回滚后都取一次；失败也不能少）。</summary>
        private static ConfWriteResult Finish(ConfWriteResult r, string confPath)
        {
            try
            {
                if (File.Exists(confPath))
                {
                    r.Bytes = (int)new FileInfo(confPath).Length;
                    r.Sha256 = Sha256Of(confPath);
                }
            }
            catch { }
            return r;
        }

        /// <summary>读 key=value（跳过注释；只取第一个 = ，值里可含 = ）。</summary>
        public static Dictionary<string, string> Read(string confPath)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(confPath) || !File.Exists(confPath)) return d;

            foreach (var raw in File.ReadAllLines(confPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return d;
        }

        /// <summary>只保留最近 N 份备份（默认 10）。时间戳在文件名里 ⇒ 字典序即时间序。</summary>
        public static int PruneBackups(string prefix, int keep)
        {
            int deleted = 0;
            try
            {
                string dir = Path.GetDirectoryName(prefix);
                string pat = Path.GetFileName(prefix) + "*";
                var list = new List<string>(Directory.GetFiles(dir, pat));
                if (list.Count > keep)
                {
                    list.Sort(StringComparer.Ordinal);
                    for (int i = 0; i < list.Count - keep; i++)
                    {
                        try { File.Delete(list[i]); deleted++; } catch { }
                    }
                }
            }
            catch { }
            return deleted;
        }

        public static string Sha256Of(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(fs);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string Join(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "；" + b;
        }

        private static string San(Func<string, string> f, string s)
        {
            if (f == null || s == null) return s;
            try { return f(s); } catch { return s; }
        }
    }
}
