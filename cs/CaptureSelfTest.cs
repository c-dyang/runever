using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RunEverCs.Services;

namespace RunEverCs
{
    /// <summary>
    /// 采集链自检：C# → AHK 采集器 → JSON → Context，**并覆盖非 ASCII 路径的编码口径**
    /// （契约 §1-D12 / AC-B1…AC-B4）。
    /// 用法：RunEverCs.exe --selftest-capture &lt;root&gt; &lt;out.txt&gt;
    ///
    /// ★★ 测试环境纪律（本轮教训）：编码类断言**必须在无控制台条件下**做 ——
    ///    有控制台时连旧写法（`cmd &gt;` 重定向）也产出 UTF-8，断言会**假绿**。
    ///    本自检天然满足：所有采集器/PS 进程都按 `CaptureClient.cs:108-110` 的
    ///    `UseShellExecute=false / CreateNoWindow=true / WindowStyle=Hidden` 启动。
    ///    报告里**显式打印**该事实，否则后人无法判断"这次是不是有控制台"。
    ///
    /// ★★ 为什么用 `--clipfiles-from`（采集器的可选第 2 参）：
    ///    采集器主流程会先清剪贴板再 `SendInput ^c`，而自检进程的前台窗口不是资源
    ///    管理器 ⇒ 剪贴板被清空、`GetClipboardFiles()` 根本不会被调用 ⇒
    ///    "设 CF_HDROP + 跑采集器" 是**空跑假绿**。故产品级用例走该测试缝把中文路径
    ///    直接喂进采集器（生产调用点只传 1 个参数 ⇒ 运行期零行为变化）。
    ///
    /// 用例：C1 夹具 / C2 CF_HDROP 夹具自验 / C3-C5 产品级逐字符相等 /
    ///       片段级（修法本身：无 BOM + 逐字符相等）/ C7 U+FFFD 检测器 /
    ///       C6 负对照（INFO，不计 pass/fail）/ C8 复原。
    /// </summary>
    public static class CaptureSelfTest
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;
            string root = args[1];
            string outFile = args[2];

            var sb = new StringBuilder();
            int pass = 0, fail = 0;
            int rc = 0;

            Action<bool, string, string> chk = null;
            chk = (cond, label, extra) =>
            {
                if (cond) { pass++; sb.AppendLine("  OK   " + label); }
                else { fail++; sb.AppendLine("  FAIL " + label + (extra != "" ? "   <<< " + extra : "")); }
            };
            Action<bool, string> ok = (cond, label) => chk(cond, label, "");

            string fixtureDir = null;
            string fixtureFile = null;
            bool clipoSaved = false;
            List<string> clipoHdrop = null;
            string clipoText = null;
            bool clipoTextSaved = false;

            try
            {
                sb.AppendLine("=== C# → AHK 采集链自检 ===");
                var c = new CaptureClient(root);
                sb.AppendLine("采集器路径 = " + FormatPath(root));
                sb.AppendLine("可用 = " + c.IsAvailable());
                sb.AppendLine("无控制台条件 = UseShellExecute=False, CreateNoWindow=True, WindowStyle=Hidden"
                    + "（复刻 CaptureClient.cs:108-110）");
                sb.AppendLine("  ★ 本自检天然跑在「无控制台」链路 —— 有控制台时旧写法也是 UTF-8，会假绿。");

                // ---------- [1] 原链路（生产调用点：只传 1 个参数） ----------
                sb.AppendLine();
                sb.AppendLine("--- [1] 原链路：CaptureClient（生产调用点，只传 1 个参数）---");
                var logs = new StringBuilder();
                var ctx = c.Capture(s => logs.AppendLine("  [log] " + s));
                sb.AppendLine(logs.ToString());
                sb.AppendLine("text      = [" + ctx.Text + "]");
                sb.AppendLine("files     = " + ctx.Files.Count + " 个");
                foreach (var f in ctx.Files) sb.AppendLine("    " + f);
                sb.AppendLine("winTitle  = [" + ctx.WinTitle + "]");
                sb.AppendLine("winClass  = [" + ctx.WinClass + "]");
                sb.AppendLine("proc      = [" + ctx.Proc + "]");
                sb.AppendLine("clipboard = [" + (ctx.Clipboard.Length > 60
                    ? ctx.Clipboard.Substring(0, 60) + "..." : ctx.Clipboard) + "]");
                sb.AppendLine("hasFolder = " + ctx.HasFolder);
                sb.AppendLine("扩展名    = " + string.Join(",", ctx.GetExtensions()));

                // ---------- [2] C1 夹具 ----------
                sb.AppendLine();
                sb.AppendLine("--- [2] C1 夹具（中文目录 + 中文文件）---");
                fixtureDir = Path.Combine(Path.GetTempPath(),
                    "rve_cap_中文测试_" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(fixtureDir);
                fixtureFile = Path.Combine(fixtureDir, "中文文件.txt");
                File.WriteAllText(fixtureFile, "夹具", new UTF8Encoding(false));
                ok(Directory.Exists(fixtureDir) && File.Exists(fixtureFile),
                    "C1 夹具创建成功：" + fixtureFile);

                // 保存原剪贴板（C8 复原用）
                clipoHdrop = GetClipboardHdrop();
                clipoSaved = clipoHdrop != null && clipoHdrop.Count > 0;
                clipoText = GetClipboardText();
                clipoTextSaved = !string.IsNullOrEmpty(clipoText);

                // ---------- [3] C2 CF_HDROP 夹具自验 ----------
                sb.AppendLine();
                sb.AppendLine("--- [3] C2 剪贴板 CF_HDROP 夹具（先验夹具自身）---");
                bool setOk = SetClipboardHdrop(new List<string> { fixtureFile });
                var back = GetClipboardHdrop();
                chk(setOk && back != null && back.Count == 1
                    && string.CompareOrdinal(back[0], fixtureFile) == 0,
                    "C2 CF_HDROP 夹具：Clipboard.GetFileDropList()[0] == 原路径（夹具自身先验）",
                    back == null ? "读回失败" : string.Join("|", back.ToArray()));

                // ---------- [4] C3/C4/C5 产品级（走测试缝，无控制台） ----------
                sb.AppendLine();
                sb.AppendLine("--- [4] C3/C4/C5 产品级：dist\\CaptureRunner.exe --clipfiles-from（无控制台）---");
                string runner = RunnerExe(root);
                if (runner == null)
                {
                    sb.AppendLine("  [SKIP] 未找到编译版采集器（dist\\CaptureRunner.exe）⇒ 产品级用例降级");
                    sb.AppendLine("         AC-B4 仅覆盖片段级，采集器级为人工验证（缺口已声明）");
                }
                else
                {
                    sb.AppendLine("  采集器 = " + runner);
                    sb.AppendLine("  测试缝调用 = \"" + runner + "\" \"<state\\cap_*.json>\" --clipfiles-from \"<listfile>\"");
                    string listFile = Path.Combine(Path.GetTempPath(),
                        "rve_caplist_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                    // 列表文件以 UTF-8 无 BOM 写：喂给采集器的就是"原路径"本身
                    File.WriteAllLines(listFile, new[] { fixtureFile }, new UTF8Encoding(false));

                    string stateDir = Path.Combine(root, "state");
                    if (!Directory.Exists(stateDir)) Directory.CreateDirectory(stateDir);
                    string capOut = Path.Combine(stateDir, "cap_selftest_" + DateTime.Now.Ticks + ".json");
                    try { if (File.Exists(capOut)) File.Delete(capOut); } catch { }

                    string runLog;
                    int code = RunHiddenExe(runner,
                        "\"" + capOut + "\" --clipfiles-from \"" + listFile + "\"", 5000, out runLog);
                    chk(code == 0, "C3a 采集器 exit=0（无控制台）", runLog);

                    if (File.Exists(capOut))
                    {
                        string json = File.ReadAllText(capOut, Encoding.UTF8);
                        var got = ReadFilesFromJson(json);
                        chk(got.Count == 1, "C3 采集结果 files.Count == 1", "实得 " + got.Count);
                        bool eq = got.Count == 1
                            && string.CompareOrdinal(got[0], fixtureFile) == 0;
                        chk(eq, "中文路径 = 逐字符相等 ✓",
                            got.Count > 0 ? "[" + got[0] + "] vs [" + fixtureFile + "]" : "无文件项");
                        chk(got.Count == 1 && got[0].IndexOf('\uFFFD') < 0,
                            "C5 采集结果无替换字符 U+FFFD",
                            got.Count > 0 ? got[0] : "无文件项");
                    }
                    else
                    {
                        chk(false, "中文路径 = 逐字符相等 ✓", "采集器未产出 " + capOut);
                    }
                    try { if (File.Exists(listFile)) File.Delete(listFile); } catch { }
                    try { if (File.Exists(capOut)) File.Delete(capOut); } catch { }
                }

                // ---------- [5] 片段级：修法本身（无 BOM + 逐字符相等） ----------
                sb.AppendLine();
                sb.AppendLine("--- [5] 片段级：新写法 PS 直写 UTF-8 无 BOM（CreateNoWindow=true）---");
                {
                    string ps1 = Path.Combine(Path.GetTempPath(),
                        "rve_capps_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ps1");
                    string psOut = Path.Combine(Path.GetTempPath(),
                        "rve_cappsout_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                    // 与采集器生成的 ps1 同源：param 收路径 + WriteAllLines + UTF8Encoding($false)
                    string ps = "param([string]$out)\r\n"
                        + "Add-Type -AssemblyName System.Windows.Forms\r\n"
                        + "$a = @()\r\n"
                        + "foreach ($f in [System.Windows.Forms.Clipboard]::GetFileDropList()) { $a += [string]$f }\r\n"
                        + "[IO.File]::WriteAllLines($out, [string[]]$a, (New-Object System.Text.UTF8Encoding($false)))\r\n";
                    File.WriteAllText(ps1, ps, new UTF8Encoding(false));
                    string psl;
                    int pc = RunHiddenExe(PowerShellExe(),
                        "-NoProfile -ExecutionPolicy Bypass -File \"" + ps1 + "\" \"" + psOut + "\"", 15000, out psl);
                    if (pc == 0 && File.Exists(psOut))
                    {
                        byte[] head = new byte[3];
                        using (var fs = File.OpenRead(psOut)) { fs.Read(head, 0, 3); }
                        bool noBom = !(head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF);
                        chk(noBom, "片段级：输出文件前 3 字节不是 EF BB BF（无 BOM）",
                            "head=" + head[0].ToString("X2") + " " + head[1].ToString("X2") + " " + head[2].ToString("X2"));
                        string txt = File.ReadAllText(psOut, Encoding.UTF8).TrimEnd('\r', '\n');
                        chk(string.CompareOrdinal(txt, fixtureFile) == 0,
                            "片段级：PS 直写内容与中文路径逐字符相等", "[" + txt + "] vs [" + fixtureFile + "]");
                    }
                    else
                    {
                        chk(false, "片段级：PS 新写法执行成功", "exit=" + pc + " " + psl);
                    }
                    try { if (File.Exists(ps1)) File.Delete(ps1); } catch { }
                    try { if (File.Exists(psOut)) File.Delete(psOut); } catch { }
                }

                // ---------- [6] C7 U+FFFD 检测器（必须证明告警会真的触发） ----------
                sb.AppendLine();
                sb.AppendLine("--- [6] C7 U+FFFD 检测器（告警必须真的触发，不能是死代码）---");
                {
                    string logPath = Path.Combine(root, "state", "capture.log");
                    int before = CountLines(logPath);

                    // 造一段"GBK 字节被当 UTF-8 解"的乱码：必然含 U+FFFD
                    string garbled;
                    try
                    {
                        Encoding gbk = Encoding.GetEncoding(936);
                        garbled = Encoding.UTF8.GetString(gbk.GetBytes(fixtureFile));
                    }
                    catch
                    {
                        garbled = fixtureFile + "\uFFFD";
                    }
                    string gList = Path.Combine(Path.GetTempPath(),
                        "rve_capbad_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                    File.WriteAllLines(gList, new[] { garbled }, new UTF8Encoding(false));

                    string stateDir = Path.Combine(root, "state");
                    if (!Directory.Exists(stateDir)) Directory.CreateDirectory(stateDir);
                    string capOut2 = Path.Combine(stateDir, "cap_selftest_" + DateTime.Now.Ticks + ".json");
                    string runner2 = RunnerExe(root);
                    if (runner2 == null)
                    {
                        sb.AppendLine("  [SKIP] 未找到编译版采集器 ⇒ 检测器用例无法跑（缺口见 [4]）");
                    }
                    else
                    {
                        string l2;
                        int c2 = RunHiddenExe(runner2,
                            "\"" + capOut2 + "\" --clipfiles-from \"" + gList + "\"", 5000, out l2);
                        int after = CountLines(logPath);
                        chk(c2 == 0 && after > before && LogContains(logPath, "U+FFFD"),
                            "U+FFFD 检测器 ✓（state\\capture.log 新增告警行）",
                            "exit=" + c2 + " 行数 " + before + " → " + after + " 日志=" + logPath);
                    }
                    try { if (File.Exists(gList)) File.Delete(gList); } catch { }
                    try { if (File.Exists(capOut2)) File.Delete(capOut2); } catch { }
                }

                // ---------- [7] C6 负对照（INFO，不计 pass/fail） ----------
                sb.AppendLine();
                sb.AppendLine("--- [7] C6 负对照：旧写法（cmd 重定向）是否复现乱码 ---");
                bool negReproduced = false;
                {
                    string ps1 = Path.Combine(Path.GetTempPath(),
                        "rve_capneg_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".ps1");
                    string negOut = Path.Combine(Path.GetTempPath(),
                        "rve_capnegout_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".txt");
                    // 旧写法：脚本正文里嵌路径（PS 自己按 UTF-8 读脚本没问题）+ `cmd >` 重定向落盘
                    string ps = "Write-Output \"" + fixtureFile + "\"\r\n";
                    File.WriteAllText(ps1, ps, new UTF8Encoding(true));   // 旧 AHK 用 "UTF-8"（带 BOM）
                    // 逐字复刻旧写法（AHK 旧代码）：
                    //   ps1 正文里嵌路径 + `cmd /c powershell -File <ps1> > <out>` 重定向
                    //   （powershell 走 PATH 裸名，与原代码一致 —— 给程序路径加引号会被
                    //     cmd /c 的引号剥离规则吃掉，反而跑不起来）
                    string cmdLine = "/c powershell -NoProfile -ExecutionPolicy Bypass -File \""
                        + ps1 + "\" > \"" + negOut + "\"";
                    string nl;
                    RunHiddenExe(ComSpec(), cmdLine, 15000, out nl);
                    if (File.Exists(negOut))
                    {
                        string txt = File.ReadAllText(negOut, Encoding.UTF8).TrimEnd('\r', '\n');
                        negReproduced = string.CompareOrdinal(txt, fixtureFile) != 0;
                        sb.AppendLine("  [INFO] 旧写法是否复现乱码 = " + (negReproduced ? "是" : "否"));
                        if (!negReproduced)
                            sb.AppendLine("  [INFO] 本机检测力不足（旧写法未复现乱码）：结论依赖机器代码页，"
                                + "本行不计 pass/fail，但必须写明");
                    }
                    else
                    {
                        sb.AppendLine("  [INFO] 旧写法未产出文件（exit 输出：" + nl + "）");
                    }
                    sb.AppendLine("  [INFO] 负对照 = " + (negReproduced ? "已复现旧缺陷" : "未复现旧缺陷（检测力不足）"));
                    try { if (File.Exists(ps1)) File.Delete(ps1); } catch { }
                    try { if (File.Exists(negOut)) File.Delete(negOut); } catch { }
                }

                // ---------- [8] C8 复原 ----------
                sb.AppendLine();
                sb.AppendLine("--- [8] C8 剪贴板与夹具复原 ---");
                bool restored = false;
                if (clipoSaved) restored = SetClipboardHdrop(clipoHdrop);
                else if (clipoTextSaved) restored = SetClipboardText(clipoText);
                else restored = SetClipboardText("");
                ok(restored, "C8 剪贴板已尽力复原（原 CF_HDROP / 原文本 / 空文本）");

                try { if (Directory.Exists(fixtureDir)) Directory.Delete(fixtureDir, true); } catch { }
                ok(!Directory.Exists(fixtureDir), "C8 夹具目录已删除（无残留）");
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message + "\n" + ex.StackTrace);
                rc = 1;
            }
            finally
            {
                try { if (fixtureDir != null && Directory.Exists(fixtureDir)) Directory.Delete(fixtureDir, true); } catch { }
                try
                {
                    if (clipoSaved) SetClipboardHdrop(clipoHdrop);
                    else if (clipoTextSaved) SetClipboardText(clipoText);
                }
                catch { }
            }

            sb.AppendLine();
            sb.AppendLine("结果：PASS=" + pass + " FAIL=" + fail + (fail == 0 ? " 全部通过" : " 有失败"));
            // ★ 尾行契约：run-selftests.ps1 取报告最后一个非空行作 tail
            //   （历次 gate 报告引用的就是这一行）⇒ 保留为**最后一行**。
            sb.AppendLine(fail == 0 && rc == 0
                ? "结果: 链路可用 ✓"
                : "结果: 链路不可用 ✗（见上方 FAIL 行）");

            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return (fail == 0 && rc == 0) ? 0 : 1;
        }

        // ============================================================
        // 进程 / 路径
        // ============================================================
        /// <summary>编译版采集器（与 CaptureClient.cs:41-47 同一查找顺序）。</summary>
        private static string RunnerExe(string root)
        {
            string d = Path.Combine(root, "dist", "CaptureRunner.exe");
            if (File.Exists(d)) return d;
            string r = Path.Combine(root, "CaptureRunner.exe");
            if (File.Exists(r)) return r;
            return null;
        }

        /// <summary>
        /// **无控制台**跑一个进程（逐字复刻 CaptureClient.cs:108-110）。
        /// 返回 exit 码；-1 = 超时（已杀）。
        /// </summary>
        private static int RunHiddenExe(string exe, string arguments, int timeoutMs, out string log)
        {
            log = "";
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = exe;
                psi.Arguments = arguments;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                var p = Process.Start(psi);
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    log = "超时（" + timeoutMs + "ms），已杀进程";
                    return -1;
                }
                log = "exit=" + p.ExitCode;
                return p.ExitCode;
            }
            catch (Exception ex)
            {
                log = "启动失败：" + ex.Message;
                return -2;
            }
        }

        private static string PowerShellExe()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(p) ? p : "powershell.exe";
        }

        private static string ComSpec()
        {
            string c = Environment.GetEnvironmentVariable("ComSpec");
            return string.IsNullOrEmpty(c) ? "cmd.exe" : c;
        }

        // ============================================================
        // JSON / 日志小工具
        // ============================================================
        private static List<string> ReadFilesFromJson(string json)
        {
            var list = new List<string>();
            var m = Regex.Match(json, "\"files\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!m.Success) return list;

            string inner = m.Groups[1].Value.Trim();
            if (inner.Length == 0) return list;

            foreach (Match e in Regex.Matches(inner, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                // 复用 CaptureClient 的反转义（唯一实现，禁第二套）
                string v = CaptureClient.JsonUnescape(e.Groups[1].Value);
                if (v.Length > 0) list.Add(v);
            }
            return list;
        }

        private static int CountLines(string path)
        {
            try
            {
                if (!File.Exists(path)) return 0;
                return File.ReadAllLines(path, Encoding.UTF8).Length;
            }
            catch { return 0; }
        }

        private static bool LogContains(string path, string needle)
        {
            try
            {
                if (!File.Exists(path)) return false;
                return File.ReadAllText(path, Encoding.UTF8).IndexOf(needle, StringComparison.Ordinal) >= 0;
            }
            catch { return false; }
        }

        private static string FormatPath(string root)
        {
            string d = Path.Combine(root, "dist", "CaptureRunner.exe");
            string r = Path.Combine(root, "CaptureRunner.exe");
            string src = Path.Combine(root, "src", "CaptureRunner.ahk");
            if (File.Exists(d)) return d + " (编译版)";
            if (File.Exists(r)) return r + " (编译版)";
            if (File.Exists(src)) return src + " (源码+运行时)";
            return "(未找到)";
        }

        // ============================================================
        // 剪贴板 CF_HDROP（就地 P/Invoke —— 不动 cs\Native\*，也不需要 STA）
        // ============================================================
        private const uint CF_HDROP = 15;
        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;
        private const uint GMEM_ZEROINIT = 0x0040;

        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint uFormat);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder lpszFile, uint cch);

        private static bool OpenClipboardRetry()
        {
            for (int i = 0; i < 3; i++)
            {
                if (OpenClipboard(IntPtr.Zero)) return true;
                System.Threading.Thread.Sleep(200);
            }
            return false;
        }

        /// <summary>把一组路径写成 CF_HDROP 放进剪贴板（DROPFILES 结构，Unicode）。</summary>
        public static bool SetClipboardHdrop(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return false;
            var text = new StringBuilder();
            foreach (var p in paths) text.Append(p).Append('\0');
            text.Append('\0');                                   // 双 null 结尾

            byte[] payload = Encoding.Unicode.GetBytes(text.ToString());
            byte[] buf = new byte[20 + payload.Length];          // DROPFILES = 20 字节
            BitConverter.GetBytes(20).CopyTo(buf, 0);            // pFiles = 偏移 20
            BitConverter.GetBytes(1).CopyTo(buf, 16);            // fWide = TRUE
            payload.CopyTo(buf, 20);                             // ★ 宽字符文件清单必须真的拷进去

            IntPtr h = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)buf.Length);
            if (h == IntPtr.Zero) return false;
            IntPtr pLock = GlobalLock(h);
            if (pLock == IntPtr.Zero) return false;
            Marshal.Copy(buf, 0, pLock, buf.Length);
            GlobalUnlock(h);

            if (!OpenClipboardRetry()) return false;
            bool okSet = false;
            try
            {
                if (!EmptyClipboard()) return false;
                // SetClipboardData 成功后所有权归系统 ⇒ 不能再 GlobalFree
                if (SetClipboardData(CF_HDROP, h) != IntPtr.Zero) okSet = true;
            }
            finally { CloseClipboard(); }
            return okSet;
        }

        /// <summary>读回剪贴板里的 CF_HDROP 路径列表；没有该格式 ⇒ null。</summary>
        public static List<string> GetClipboardHdrop()
        {
            if (!OpenClipboardRetry()) return null;
            try
            {
                IntPtr h = GetClipboardData(CF_HDROP);
                if (h == IntPtr.Zero) return null;
                uint n = DragQueryFile(h, 0xFFFFFFFF, null, 0);
                var list = new List<string>();
                for (uint i = 0; i < n; i++)
                {
                    var sbb = new StringBuilder(1024);
                    DragQueryFile(h, i, sbb, 1024);
                    list.Add(sbb.ToString());
                }
                return list;
            }
            catch { return null; }
            finally { CloseClipboard(); }
        }

        /// <summary>读剪贴板文本（CF_UNICODETEXT）；无 ⇒ null。</summary>
        public static string GetClipboardText()
        {
            if (!OpenClipboardRetry()) return null;
            try
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(h); }
            }
            catch { return null; }
            finally { CloseClipboard(); }
        }

        /// <summary>把文本放进剪贴板（CF_UNICODETEXT）。</summary>
        public static bool SetClipboardText(string text)
        {
            byte[] payload = Encoding.Unicode.GetBytes((text ?? "") + "\0");
            IntPtr h = GlobalAlloc(GMEM_MOVEABLE | GMEM_ZEROINIT, (UIntPtr)payload.Length);
            if (h == IntPtr.Zero) return false;
            IntPtr pLock = GlobalLock(h);
            if (pLock == IntPtr.Zero) return false;
            Marshal.Copy(payload, 0, pLock, payload.Length);
            GlobalUnlock(h);

            if (!OpenClipboardRetry()) return false;
            bool okSet = false;
            try
            {
                if (!EmptyClipboard()) return false;
                if (SetClipboardData(CF_UNICODETEXT, h) != IntPtr.Zero) okSet = true;
            }
            finally { CloseClipboard(); }
            return okSet;
        }
    }
}
