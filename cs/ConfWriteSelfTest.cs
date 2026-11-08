using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using RunEverCs.Models;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 段 1.6 热修自检（B1 写盘留痕 / B2 显示开关即时生效）。
    ///
    /// 用法：RunEverCs.exe --selftest-confwrite &lt;root&gt; &lt;out.txt&gt;
    ///
    /// ★ 全部用例都在**临时 root** 上跑（`%TEMP%\runever-cw-*`）：
    ///   验证"写盘"绝不能碰用户配置（runever\config\auto.conf）。
    /// ★ 5 个沙箱（live / write / backupfail / page / pagefail）**各自在 finally 里自清**
    ///   ⇒ 本套跑完 `%TEMP%\runever-cw-*` 残量为 0（G-5）。
    ///
    /// 覆盖：
    ///   B2-① 运行中把 hideMissing 0→1 ⇒ **下一次判定即生效**（LiveHideMissing，不吃 CacheSec）
    ///   B2-② 旧口径（读 Conf 内存态）确实拿不到新值（= 本轮要修的病，留作对照）
    ///   B2-③ 热键期取值开销实测（快路径 stat / 变更后重读）+ ReloadConf 通知腿
    ///   B1-① 写盘成功：字节数 + SHA256 + .bak + settings-write.log(OK)
    ///   B1-② 回读校验失败 ⇒ 回滚 + 状态行/日志双留痕（FAIL，原因在行里）
    ///   B1-③ 写不进去 ⇒ FAIL 且原因明确（异常类型 + 路径）
    ///   B1-④ 备份失败 ⇒ **继续写**但 Warning 可见（原实现 `catch { backup = null; }` 全静默）
    ///   B1-P1 被 `_loading` 拦下 ⇒ SKIP 留痕（真 WPF GeneralPage，反射驱动）
    ///   B1-P2 真页面端到端：勾选复选框 → 落盘 + AfterSave 回调 + "已保存（字节·SHA256）"状态行
    ///   B1-P3 真页面写盘失败 ⇒ 红字状态行 + FAIL 留痕 + HasSaveFailure（关窗会再喊一次）
    /// </summary>
    public static class ConfWriteSelfTest
    {
        private static StringBuilder _sb;
        private static int _fail;
        private static int _pass;

        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;      // 与 --selftest-capture / --selftest-automatch 同口径
            string root = args[1];
            string outFile = args[2];
            if (!Path.IsPathRooted(outFile)) outFile = Path.Combine(root, outFile);

            _sb = new StringBuilder();
            _fail = 0;
            _pass = 0;
            _sb.AppendLine("=== RunEver 段1.6 热修自检（B1 写盘留痕 / B2 显示开关即时生效）===");
            _sb.AppendLine("root = " + root + "（仅用于解析相对 out 路径；用例一律跑在临时 root 上）");

            LiveSwitchCases();
            WritePathCases();
            RealPageCases();

            _sb.AppendLine(_fail == 0
                ? "=== 段1.6 自检全部通过（" + _pass + " 项）==="
                : "=== 段1.6 自检失败 " + _fail + " 项（通过 " + _pass + "）===");

            try
            {
                string dir = Path.GetDirectoryName(outFile);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(outFile, _sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
            return _fail == 0 ? 0 : 1;
        }

        // ============================================================
        // 断言辅助
        // ============================================================
        private static void Check(string title, bool ok, string detail)
        {
            if (ok) { _pass++; _sb.AppendLine("[OK]   " + title + (detail.Length == 0 ? "" : "  " + detail)); }
            else
            {
                _sb.AppendLine("[FAIL] " + title + (detail.Length == 0 ? "" : "  " + detail));
                _fail++;
            }
        }

        private static void Eq(string title, string got, string want)
        {
            Check(title, got == want, got == want ? "" : "期望=[" + want + "] 实得=[" + got + "]");
        }

        private static string MakeSandbox(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(),
                "runever-cw-" + tag + "-" + DateTime.Now.Ticks);
            Directory.CreateDirectory(Path.Combine(dir, "config", "functions"));
            Directory.CreateDirectory(Path.Combine(dir, "state"));
            return dir;
        }

        /// <summary>
        /// ★ G-5：用完即清（与 `HotReloadSelfTest.DropSandbox` 同口径）。
        /// 修正前 `--selftest-confwrite` **每跑一次留 5 个** `%TEMP%\runever-cw-*`
        /// （守门复验实测：清理存量后跑一次 17 套又重新出现 5 个）—— 那是"只清存量"的假达标。
        /// 这里在每个用例的 `finally` 里落自清 ⇒ 跑完残量为 0，不再需要外部清理清单。
        /// </summary>
        private static void DropSandbox(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { _sb.AppendLine("  [warn] 沙箱清理失败: " + dir + " :: " + ex.Message); }
        }

        /// <summary>写一份最小 auto.conf（模拟"用户在别处改了开关"这一步）。</summary>
        private static void WriteAutoConf(string root, string hideMissing, string cacheSec)
        {
            string p = Path.Combine(root, "config", "auto.conf");
            var sb = new StringBuilder();
            sb.AppendLine("# 段1.6 自检夹具 auto.conf");
            sb.AppendLine("enabled=1");
            sb.AppendLine("minLen=4");
            sb.AppendLine("cacheSeconds=" + cacheSec);
            sb.AppendLine("showByDefault=0");
            sb.AppendLine("waitWhenShown=1");
            sb.AppendLine("log=1");
            sb.AppendLine("hotkey=`");
            sb.AppendLine("hideMissing=" + hideMissing);
            File.WriteAllText(p, sb.ToString(), new UTF8Encoding(false));
        }

        private static bool LogContains(string root, string needle)
        {
            try
            {
                string p = SettingsWriteLog.LogPath(root);
                if (!File.Exists(p)) return false;
                return File.ReadAllText(p, Encoding.UTF8).IndexOf(needle, StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        private static string LastLogLine(string root, string stateMark)
        {
            string found = "";
            try
            {
                foreach (var line in File.ReadAllLines(SettingsWriteLog.LogPath(root), Encoding.UTF8))
                    if (line.IndexOf(stateMark, StringComparison.Ordinal) >= 0) found = line;
            }
            catch { }
            return found;
        }

        // ============================================================
        // B2：显示开关"改完即生效"
        // ============================================================
        private static void LiveSwitchCases()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- B2. 显示开关（hideMissing）运行中即时生效 ---");
            string root = MakeSandbox("live");
            try
            {
                WriteAutoConf(root, "0", "300");        // ★ cacheSeconds=300：节流就是这里来的
                var am = new AutoMatcher(root, s => _sb.AppendLine("  [am] " + s));

                am.Load(true);                          // 托盘启动那一刻：读到 hideMissing=0
                Eq("B2 启动态 hidemissing=0", am.Conf.HideMissing.ToString(), "0");

                // 夹具：一个"缓存里有、文件已被删"的条目（= 缺件判定的最小现场）
                string staleExe = Path.Combine(root, "state", "gone.exe");   // 故意不创建
                string cacheFile = Path.Combine(root, "state", "paths.txt");
                File.WriteAllLines(cacheFile, new[] { "stale.exe=" + staleExe }, new UTF8Encoding(false));
                var cache = new PathCache(cacheFile);

                var r0 = MissingFilter.Apply(MakeGroups(), cache, am.LiveHideMissing() != 0, null);
                Check("B2 开关=0 ⇒ 缺件条目可见（1 条）",
                    CountItems(r0.Groups) == 1 && HasTitle(r0.Groups, "RUN-缺件"), 
                    "实得条数=" + CountItems(r0.Groups) + " 隐藏=" + r0.Stats.Hidden);

                // === 运行中改开关 ===（等价于：用户在设置页勾选，或外部编辑器改 auto.conf）
                WriteAutoConf(root, "1", "300");
                _sb.AppendLine("  （运行中把 auto.conf 的 hideMissing 由 0 改 1）");

                // 旧口径（=本轮要修的病）：Conf 那份内存态没人刷新
                Eq("B2 对照：旧口径 Conf.HideMissing 仍是旧值（这就是「改了不生效」）",
                    am.Conf.HideMissing.ToString(), "0");

                // 新口径：热键期实时取值 ⇒ 下一次判定即生效
                Eq("B2 LiveHideMissing() 立即取到 1", am.LiveHideMissing().ToString(), "1");
                Eq("B2 顺带把 Conf 同步为 1", am.Conf.HideMissing.ToString(), "1");

                var r1 = MissingFilter.Apply(MakeGroups(), cache, am.LiveHideMissing() != 0, null);
                Check("B2 下一次判定：缺件条目被隐藏",
                    CountItems(r1.Groups) == 0 && r1.Stats.Hidden == 1,
                    "实得条数=" + CountItems(r1.Groups) + " 隐藏=" + r1.Stats.Hidden
                    + " 清单=" + string.Join("|", r1.Stats.HiddenTitles.ToArray()));

                // === 开销实测（I-5：热键路径禁阻塞）===
                const int N = 500;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < N; i++) am.LiveHideMissing();
                double fastUs = sw.Elapsed.TotalMilliseconds * 1000.0 / N;
                _sb.AppendLine("  [开销] 快路径（文件未变，仅 stat）: " + fastUs.ToString("F2")
                    + " µs/次（" + N + " 次平均）");

                WriteAutoConf(root, "0", "300");       // 让 stat 失配 ⇒ 逼出"整份重读"路径
                sw.Restart();
                int v = am.LiveHideMissing();
                double slowMs = sw.Elapsed.TotalMilliseconds;
                Eq("B2 变更后重读拿到新值 0", v.ToString(), "0");
                _sb.AppendLine("  [开销] 变更路径（重读整份 auto.conf " 
                    + new FileInfo(Path.Combine(root, "config", "auto.conf")).Length
                    + " 字节）: " + slowMs.ToString("F3") + " ms —— 只读这一个文件，"
                    + "不扫插件目录、不重建 AutoMatcher");
                Check("B2 快路径明显低于一次文件重读", fastUs < 1000 && fastUs < slowMs * 1000,
                    "快=" + fastUs.ToString("F2") + "µs 慢=" + slowMs.ToString("F3") + "ms");

                // === 通知腿：ReloadConf（托盘收到「常规页保存成功」时走这条）===
                WriteAutoConf(root, "1", "300");
                am.ReloadConf();
                Eq("B2 ReloadConf 通知腿生效", am.Conf.HideMissing.ToString(), "1");
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] B2 用例异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally { DropSandbox(root); }
        }

        // ============================================================
        // B1：写盘三条路径（成功 / 校验失败回滚 / 写不进去）
        // ============================================================
        private static void WritePathCases()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- B1. 写盘留痕三路径（临时 root）---");
            string root = MakeSandbox("write");
            string root4 = null;                        // ★ G-5：在 try 外声明 ⇒ finally 里可自清
            try
            {
                string conf = Path.Combine(root, "config", "auto.conf");
                WriteAutoConf(root, "0", "300");

                var vals = new Dictionary<string, string> { { "hideMissing", "1" } };

                // ---- ① 成功 ----
                var r1 = AutoConfStore.Write(conf, vals, null, root);
                Check("B1① 写盘成功", r1.Ok, "Error=" + r1.Error);
                Eq("B1① 回读已变 1", AutoConfStore.Read(conf)["hideMissing"], "1");
                Check("B1① 报告字节数 + SHA256",
                    r1.Bytes > 0 && r1.Sha256.Length == 64, "字节=" + r1.Bytes + " sha=" + r1.Sha8);
                Check("B1① 生成 .bak 备份且可回滚",
                    r1.Backup.Length > 0 && File.Exists(r1.Backup), "备份=" + r1.Backup);
                Check("B1① 状态行文案含「已保存 HH:mm:ss + 字节 + SHA256」",
                    r1.StatusText().Contains("已保存") && r1.StatusText().Contains("字节")
                    && r1.StatusText().Contains("SHA256"), r1.StatusText());
                string okLine = LastLogLine(root, "| OK |");
                Check("B1① settings-write.log 落 OK 行", okLine.Length > 0, okLine);
                _sb.AppendLine("  [留痕] " + (okLine.Length > 70 ? okLine.Substring(0, 70) + "..." : okLine));

                // ---- ② 回读校验失败 ⇒ 回滚（用"含换行的值"制造写进去读不回）----
                string shaBefore = AutoConfStore.Sha256Of(conf);
                var bad = new Dictionary<string, string> { { "hideMissing", "1\ninjected=1" } };
                var r2 = AutoConfStore.Write(conf, bad, null, root);
                Check("B1② 校验失败被捕获（方法不抛）",
                    !r2.Ok && r2.Error.Contains("写入校验失败"), "Error=" + r2.Error);
                Check("B1② 已回滚到备份（内容与写前逐字节一致）",
                    r2.RolledBack && AutoConfStore.Sha256Of(conf) == shaBefore,
                    "RolledBack=" + r2.RolledBack + " sha=" + AutoConfStore.Sha256Of(conf));
                Check("B1② 状态行文案以「✗ 保存失败：」开头且含原因",
                    r2.StatusText().StartsWith("✗ 保存失败：") && r2.StatusText().Contains("校验失败"),
                    r2.StatusText());
                string failLine = LastLogLine(root, "| FAIL |");
                Check("B1② settings-write.log 落 FAIL 行（原因在行里）", failLine.Length > 0, failLine);
                _sb.AppendLine("  [留痕] " + (failLine.Length > 90 ? failLine.Substring(0, 90) + "..." : failLine));

                // ---- ③ 写不进去（目标目录不存在）----
                string gone = Path.Combine(root, "no-such-dir", "auto.conf");
                var r3 = AutoConfStore.Write(gone, vals, null, root);
                Check("B1③ 写盘失败：Ok=false 且原因明确",
                    !r3.Ok && r3.Error.Length > 0, "Error=" + r3.Error);
                Check("B1③ 原因含异常类型与失败路径",
                    r3.Error.Contains("DirectoryNotFound") && r3.Error.Contains("no-such-dir"),
                    r3.Error);
                Check("B1③ 失败也留痕（FAIL 行数与上面同源、非静默）",
                    LastLogLine(root, "| FAIL |").Contains("no-such-dir"),
                    LastLogLine(root, "| FAIL |"));
                _sb.AppendLine("  [留痕] " + r3.StatusText());

                // ---- ④ 备份失败：判断 = 「继续写，但必须可见」（原实现是 catch { backup = null; } 全静默）----
                //   制造方式：把本次/随后 2 秒的备份文件名位置**预先占成目录** ⇒ File.Copy 必失败。
                root4 = MakeSandbox("backupfail");
                WriteAutoConf(root4, "0", "300");
                string conf4 = Path.Combine(root4, "config", "auto.conf");
                var t0 = DateTime.Now;
                for (int k = 0; k <= 2; k++)
                {
                    try
                    {
                        Directory.CreateDirectory(conf4 + ".bak."
                            + t0.AddSeconds(k).ToString("yyyyMMdd-HHmmss"));
                    }
                    catch { }
                }
                var r4 = AutoConfStore.Write(conf4,
                    new Dictionary<string, string> { { "hideMissing", "1" } }, null, root4);
                Check("B1④ 备份失败**不阻断写盘**（判断：备份是后悔药、不是前置条件）",
                    r4.Ok && AutoConfStore.Read(conf4)["hideMissing"] == "1",
                    "Ok=" + r4.Ok + " Error=" + r4.Error);
                Check("B1④ 备份失败**可见**（Warning 进状态行 + 日志，不再静默）",
                    r4.Warning.Contains("备份失败") && r4.Backup.Length == 0
                    && r4.StatusText().Contains("备份失败")
                    && LastLogLine(root4, "| OK |").Contains("备份失败"),
                    "Warning=" + r4.Warning + " 状态行=" + r4.StatusText());
                _sb.AppendLine("  [状态行] " + r4.StatusText());
                _sb.AppendLine("  [留痕] " + LastLogLine(root4, "| OK |"));
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] B1 写盘用例异常: " + ex.Message + "\n" + ex.StackTrace);
            }
            finally { DropSandbox(root); DropSandbox(root4); }
        }

        // ============================================================
        // B1/B2：真 WPF 页面驱动（_loading 拦截留痕 + 勾选端到端）
        //   ★ 说明：真 GUI 不弹窗、不显示，只构造 GeneralPage 并反射调用它的私有 Save()
        //     —— 走的**就是**用户点复选框时执行的那段代码。
        // ============================================================
        private static void RealPageCases()
        {
            _sb.AppendLine();
            _sb.AppendLine("--- B1-P1…P3. 真页面驱动（GeneralPage，无 GUI 显示）---");
            string root = MakeSandbox("page");
            string root2 = null;                        // ★ G-5：在 try 外声明 ⇒ finally 里可自清
            try
            {
                WriteAutoConf(root, "0", "300");
                string conf = Path.Combine(root, "config", "auto.conf");

                var page = new RunEverCs.Views.GeneralPage(root);   // 构造即 Build()+Load()

                var tPage = typeof(RunEverCs.Views.GeneralPage);
                var fLoading = tPage.GetField("_loading", BindingFlags.NonPublic | BindingFlags.Instance);
                var mSave = tPage.GetMethod("Save", BindingFlags.NonPublic | BindingFlags.Instance);
                var fHide = tPage.GetField("_hideMissing", BindingFlags.NonPublic | BindingFlags.Instance);
                var fStatus = tPage.GetField("_status", BindingFlags.NonPublic | BindingFlags.Instance);
                Check("① 反射拿到 _loading / Save / _hideMissing / _status",
                    fLoading != null && mSave != null && fHide != null && fStatus != null, "");

                // ---- B1-P1：Load 期间（_loading=true）本来就会吞掉事件回灌，但必须留痕 ----
                Check("B1-P1 Load 期间的事件回灌已留痕（SKIP）",
                    LogContains(root, "| SKIP |") && LogContains(root, "Load 期间事件回灌"),
                    LastLogLine(root, "| SKIP |"));

                // ---- B1-P2 前置：把 _loading 钉成 true（模拟"恒真"这一静默形态）----
                fLoading.SetValue(page, true);
                string shaBefore = AutoConfStore.Sha256Of(conf);
                mSave.Invoke(page, null);
                string stuckLine = LastLogLine(root, "疑似 _loading 未复位");
                Check("B1-P1 _loading 恒真时保存被拦且留痕（不再静默）", stuckLine.Length > 0, stuckLine);
                Check("B1-P1 被拦下的保存确实没写盘（哈希不变）",
                    AutoConfStore.Sha256Of(conf) == shaBefore, AutoConfStore.Sha256Of(conf));
                var status = (TextBlock)fStatus.GetValue(page);
                Check("B1-P1 状态行同时给出可见文本",
                    status.Text.Contains("已跳过保存") && status.Text.Contains("settings-write.log"),
                    status.Text);
                _sb.AppendLine("  [留痕] " + stuckLine);
                _sb.AppendLine("  [状态行] " + status.Text);

                // ---- B1-P2/B2：恢复 _loading=false，勾选复选框 ⇒ 真落盘 + 通知回调 ----
                fLoading.SetValue(page, false);
                int notified = 0;
                page.AfterSave = delegate () { notified++; };
                var cb = (CheckBox)fHide.GetValue(page);
                cb.IsChecked = true;                     // 触发 Checked → Save()

                Eq("B1-P2 勾选后 auto.conf 真落盘（hideMissing=1）",
                    AutoConfStore.Read(conf)["hideMissing"], "1");
                Check("B2   勾选后 AfterSave 回调被触发（= 本轮给常规页补的钩子）",
                    notified == 1, "回调次数=" + notified);
                Check("B1-P2 状态行给出「已保存 + 字节 + SHA256」",
                    status.Text.Contains("已保存") && status.Text.Contains("SHA256")
                    && status.Text.EndsWith("）"), status.Text);
                Check("B1-P2 落盘也进了 settings-write.log（OK 行）",
                    LogContains(root, "| OK |"), LastLogLine(root, "| OK |"));
                Check("B1-P2 生成了 .bak（下一次改坏还能回滚）",
                    Directory.GetFiles(Path.GetDirectoryName(conf), "auto.conf.bak.*").Length > 0, "");
                _sb.AppendLine("  [状态行] " + status.Text);
                _sb.AppendLine("  [留痕] " + LastLogLine(root, "| OK |"));

                // ---- B1-P3：写盘失败必须"可见 + 留痕"（走 Save() 的失败分支）----
                //   做法：在 conf 旁边放一个**同名 .tmp 目录**占位 ⇒ 原子写的"临时件"写不进去
                //   （等价于写盘目标不可达）。不需要改任何生产字段，失败是确定性的。
                root2 = MakeSandbox("pagefail");
                WriteAutoConf(root2, "0", "300");
                string conf2 = Path.Combine(root2, "config", "auto.conf");
                Directory.CreateDirectory(conf2 + ".tmp");      // ★ 占位目录 ⇒ File.WriteAllText 必失败
                var page2 = new RunEverCs.Views.GeneralPage(root2);
                ((CheckBox)fHide.GetValue(page2)).IsChecked = true;
                var status2 = (TextBlock)fStatus.GetValue(page2);
                Check("B1-P3 写盘失败：状态行以「✗ 保存失败：」开头（红字）",
                    status2.Text.StartsWith("✗ 保存失败："), status2.Text);
                Check("B1-P3 写盘失败：留痕 FAIL",
                    LogContains(root2, "| FAIL |"), LastLogLine(root2, "| FAIL |"));
                Check("B1-P3 失败时 HasSaveFailure=true（关窗会再喊一次）",
                    page2.HasSaveFailure, "");
                _sb.AppendLine("  [状态行] " + status2.Text);
                _sb.AppendLine("  [留痕] " + LastLogLine(root2, "| FAIL |"));

                // 收尾：把 Running 的 WPF 控件丢掉（自检进程随后 Environment.Exit）
                GC.KeepAlive(page);
            }
            catch (Exception ex)
            {
                _fail++;
                _sb.AppendLine("[FAIL] 真页面用例异常: " + ex.GetType().Name + ": " + ex.Message
                    + "\n" + ex.StackTrace);
            }
            finally { DropSandbox(root); DropSandbox(root2); }
        }

        // ============================================================
        // 夹具
        // ============================================================
        private static List<MenuGroup> MakeGroups()
        {
            var g = new MenuGroup { Name = "段1.6 夹具组", Type = GroupType.Always, Line = 1 };
            g.Items.Add(new RunEverCs.Models.MenuItem
            {
                Title = "RUN-缺件",
                Type = ActionType.Run,
                Target = "stale.exe"
            });
            return new List<MenuGroup> { g };
        }

        private static int CountItems(List<MenuGroup> groups)
        {
            int n = 0;
            foreach (var g in groups) n += g.Items.Count;
            return n;
        }

        private static bool HasTitle(List<MenuGroup> groups, string title)
        {
            foreach (var g in groups)
                foreach (var it in g.Items)
                    if (it.Title == title) return true;
            return false;
        }
    }
}
