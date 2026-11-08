using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// 设置写盘留痕（段 1.6 · B1：「让『是否落盘』变成可取证的事实」）。
    ///
    /// 单一出口 = `&lt;root&gt;\state\settings-write.log`，一行一条，UTF-8 追加：
    ///   2026-10-04 15:55:31.123 | pid=768 | save | OK   | conf=... 字节=5290 sha256=3f2a... | 备份=...
    ///   2026-10-04 15:55:31.123 | pid=768 | save | FAIL | conf=... 原因=...
    ///   2026-10-04 15:55:31.123 | pid=768 | save | SKIP | conf=... 原因=_loading ...
    ///
    /// 三态齐全（OK / FAIL / SKIP）—— 用户报「勾了开关没生效」时，先看这个文件：
    ///   没有任何 save 行        ⇒ 那次操作根本没走到写盘（窗口没打开 / 页没加载）
    ///   有 SKIP 行              ⇒ 被 `_loading` 拦下（B1 主嫌）
    ///   有 FAIL 行              ⇒ 写盘失败，原因就在行里（无需再猜）
    ///   有 OK 行但开关仍不生效  ⇒ 写盘成功，问题在「谁读它」（段 1.6 B2）
    ///
    /// ★ 本类只做 IO、不碰 UI（无 WPF 依赖）⇒ 自检可直接驱动它做实证。
    /// ★ 永久不抛：日志写不进去不能连累保存本身（保存结果由状态行负责可见）。
    /// </summary>
    public static class SettingsWriteLog
    {
        public const string FileName = "settings-write.log";

        public static string LogPath(string root)
        {
            return Path.Combine(root ?? "", "state", FileName);
        }

        /// <summary>追加一行。state = OK / FAIL / SKIP（自检据此断言）。</summary>
        public static void Append(string root, string action, string state, string detail)
        {
            if (string.IsNullOrEmpty(root)) return;
            try
            {
                string path = LogPath(root);
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
                    + " | pid=" + Process.GetCurrentProcess().Id
                    + " | " + (action ?? "-")
                    + " | " + (state ?? "-")
                    + " | " + (detail ?? "");
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }   // 见类注释：日志自身失败绝不外抛
        }

        /// <summary>
        /// 被 `_loading` 拦下的保存：落一行 SKIP 诊断，并返回状态行文本。
        /// ★ 为什么留痕而不是直接 return：`_loading` 恒真会让**每一次**保存被静默吃掉，
        ///   表现就是用户看到的「勾了开关，重启后还是原样」。留痕把这条路径变成可取证事实。
        /// </summary>
        /// <param name="phase">调用时机描述（Load 期间的事件回灌 / Load 之外）。</param>
        public static string RecordSkippedByLoading(string root, string confPath, string phase)
        {
            Append(root, "save", "SKIP",
                "conf=" + confPath + " 原因=_loading（" + phase + "）未写盘");
            return "⚠ 已跳过保存（页面正在加载配置 · _loading=true）：未写盘，已记 state\\"
                + FileName;
        }

        /// <summary>配置「读取」阶段留痕（只记失败：读失败会让页面停在默认值上）。</summary>
        public static void AppendLoadFailure(string root, string confPath, string reason)
        {
            Append(root, "load", "FAIL", "conf=" + confPath + " 原因=" + reason);
        }

        /// <summary>保存后通知（回调托盘）留痕 —— 通知失败 = 显示类开关不会即时生效。</summary>
        public static void AppendNotifyFailure(string root, string reason)
        {
            Append(root, "notify", "FAIL", "原因=" + reason);
        }
    }
}
