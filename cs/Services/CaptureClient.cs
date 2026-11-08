using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RunEverCs.Services
{
    /// <summary>
    /// 采集客户端 —— 起 AHK 采集器进程，读回 JSON。
    ///
    /// ★ 架构选择（用户 2026-10-03 定案）：
    ///   采集层【保留 AHK】—— 因为 C# 重写 SendInput + ClipWait + ClipboardAll
    ///   这套组合风险高、收益低。AHK 编译/解释运行的独立进程负责采集，
    ///   C# 只做进程调用 + JSON 解析。
    ///
    /// ★ 通信方式：输出文件（不走 stdout）
    ///   实测教训：AHK 无终端时 stdout 不可用（exit=0 但输出空）。
    ///
    /// ★ 超时保护：采集器可能卡住（目标程序不响应 Ctrl+C），
    ///   超时即杀进程并返回空上下文（菜单仍可显示 always/fallback）。
    /// </summary>
    public class CaptureClient
    {
        private readonly string _runnerPath;   // CaptureRunner.ahk 或 .exe
        private readonly string _ahkExe;       // AutoHotkey64.exe（解释运行时）
        private readonly string _tempDir;
        private readonly int _timeoutMs;

        public CaptureClient(string root, string ahkExe = null, int timeoutMs = 3000)
        {
            _ahkExe = string.IsNullOrEmpty(ahkExe)
                ? @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe"
                : ahkExe;
            _timeoutMs = timeoutMs;
            _tempDir = Path.Combine(root, "state");

            // 优先用编译后的 exe（无解释器依赖），否则回退源码 + 运行时
            // 查找顺序：<root>\dist\CaptureRunner.exe → <root>\CaptureRunner.exe → src\*.ahk
            string exeInDist = Path.Combine(root, "dist", "CaptureRunner.exe");
            string exeInRoot = Path.Combine(root, "CaptureRunner.exe");
            string srcRunner = Path.Combine(root, "src", "CaptureRunner.ahk");

            if (File.Exists(exeInDist)) _runnerPath = exeInDist;
            else if (File.Exists(exeInRoot)) _runnerPath = exeInRoot;
            else _runnerPath = srcRunner;
        }

        public bool IsAvailable()
        {
            return File.Exists(_runnerPath);
        }

        /// <summary>
        /// AHK 运行时（解释器）全路径 —— 采集入口的唯一定义。
        /// ★ 公开出来是给 PluginRunner 用的：插件的形态 B 驱动脚本也跑在同一个运行时上，
        ///   否则会出现"采集能用、插件不能用"的分裂（本机装了两个版本 AHK 的经典症状）。
        /// </summary>
        public string AhkExe { get { return _ahkExe; } }

        /// <summary>采集器脚本/exe 路径（排障用）。</summary>
        public string RunnerPath { get { return _runnerPath; } }

        /// <summary>
        /// 累计"真正起了采集进程"的次数 —— 供自检断言使用。
        /// 用途：常驻模式下若热键每次都复用陈旧上下文，这个计数就不会涨
        /// （回归防线，见 RunEverApp.WaitCapture 的一次性语义）。
        /// </summary>
        public int CaptureCount { get; private set; }

        /// <summary>采集一次；任何失败都返回空 Context（绝不抛异常）。</summary>
        public Context Capture(Action<string> log = null)
        {
            CaptureCount++;
            var ctx = new Context();
            if (!IsAvailable())
            {
                if (log != null) log("采集器不存在: " + _runnerPath);
                return ctx;
            }

            try
            {
                if (!Directory.Exists(_tempDir)) Directory.CreateDirectory(_tempDir);
            }
            catch { }

            string outFile = Path.Combine(_tempDir, "cap_" + DateTime.Now.Ticks + ".json");
            string tmpFile = outFile + ".tmp";
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            try { if (File.Exists(tmpFile)) File.Delete(tmpFile); } catch { }

            Process proc = null;
            try
            {
                var psi = new ProcessStartInfo();
                if (_runnerPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = _runnerPath;
                    psi.Arguments = "\"" + outFile + "\"";
                }
                else
                {
                    psi.FileName = _ahkExe;
                    psi.Arguments = "\"" + _runnerPath + "\" \"" + outFile + "\"";
                }
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                var sw = Stopwatch.StartNew();
                proc = Process.Start(psi);
                bool exited = proc.WaitForExit(_timeoutMs);
                sw.Stop();

                if (!exited)
                {
                    try { proc.Kill(); } catch { }
                    if (log != null) log("采集超时（" + _timeoutMs + "ms），已杀进程");
                    return ctx;
                }
                if (log != null) log("采集耗时 " + sw.ElapsedMilliseconds + "ms exit=" + proc.ExitCode);

                if (!File.Exists(outFile))
                {
                    if (log != null) log("采集器未产出文件");
                    return ctx;
                }

                string json = File.ReadAllText(outFile, Encoding.UTF8);
                ParseInto(json, ctx);
            }
            catch (Exception ex)
            {
                if (log != null) log("采集异常: " + ex.Message);
            }
            finally
            {
                try { if (proc != null) proc.Dispose(); } catch { }
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            }

            return ctx;
        }

        /// <summary>解析采集器 JSON（极简提取，不引序列化库）。
        /// ★ internal：常驻客户端（ResidentCaptureClient）复用同一套解析，保证两条路解析口径一致。</summary>
        internal static void ParseInto(string json, Context ctx)
        {
            if (string.IsNullOrEmpty(json)) return;
            ctx.Text = GetStr(json, "text");
            ctx.WinTitle = GetStr(json, "winTitle");
            ctx.WinClass = GetStr(json, "winClass");
            ctx.Proc = GetStr(json, "proc");
            ctx.Clipboard = GetStr(json, "clipboard");
            ctx.Files = GetArr(json, "files");

            bool hasFolder = false;
            var m = Regex.Match(json, "\"hasFolder\"\\s*:\\s*(true|false)");
            if (m.Success) hasFolder = m.Groups[1].Value == "true";
            ctx.HasFolder = hasFolder;
        }

        private static string GetStr(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return "";
            return JsonUnescape(m.Groups[1].Value);
        }

        private static List<string> GetArr(string json, string key)
        {
            var list = new List<string>();
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
            if (!m.Success) return list;

            string inner = m.Groups[1].Value.Trim();
            if (inner.Length == 0) return list;

            foreach (Match e in Regex.Matches(inner, "\"((?:[^\"\\\\]|\\\\.)*)\""))
            {
                string v = JsonUnescape(e.Groups[1].Value);
                if (v.Length > 0) list.Add(v);
            }
            return list;
        }

        /// <summary>
        /// JSON 反转义 —— **必须单遍扫描**（不能写成若干次 `string.Replace`）。
        ///
        /// 为什么（采集链路径无损的硬要求）：AHK `Js()` 先做 `\` → `\\`，
        /// 所以路径 `C:\Temp\rve_cap\x` 在 JSON 里是 `C:\\Temp\\rve_cap\\x`。
        /// 若先做 `Replace("\r"…)`，`\\r` 的第 2 个反斜杠 + `r` 会被当成转义 CR
        /// ⇒ `C:` + CR + `ve_cap` —— **路径被静默改写，非 ASCII 之外再丢一层**。
        /// （同理 `\t`est / `\n`otes 这类真实存在的路径。）
        /// 单遍扫描按 JSON 语义逐个吃转义序列，`\\r` 正确还原为 `\` + `r`。
        /// </summary>
        internal static string JsonUnescape(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s ?? "";
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'r': sb.Append('\r'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    // `\uXXXX`：AHK 侧不产出，但保持 JSON 语义完整
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int cp;
                            if (int.TryParse(s.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out cp))
                            {
                                sb.Append((char)cp);
                                i += 4;
                                break;
                            }
                        }
                        sb.Append('\\').Append(n);
                        break;
                    default: sb.Append('\\').Append(n); break;
                }
            }
            return sb.ToString();
        }
    }
}
