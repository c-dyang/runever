using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace RunEverCs.Services
{
    /// <summary>
    /// 形态 B 插件运行器 —— 等价 AHK `RunEver.ahk:684-762 CallPluginRun`。
    ///
    /// 契约：形态 B 插件（无 RVE_FN_Tpl）自带 `Run(text)`，由它产出目标字符串；
    ///   返回空串 ⇒ 调用方回落弹菜单（三层兜底的②）。
    ///
    /// ★ 实现方式（与 AHK 一致，不是 #Include 主程序）：
    ///   生成一个【临时驱动脚本】→ 它 #Include 插件文件 → 调 Run(text)
    ///   → 结果写输出文件 → 主程序读回。
    ///   为什么要另起进程：热键路径不能重载脚本（加载期行为），
    ///   且插件作者代码可能抛错/死循环，独立进程 + 超时才能兜住。
    ///
    /// ★ 超时必须设上限：脚本通常 &lt;300ms，超时即杀进程并返回空串。
    /// ★ 临时目录 state\autotmp 执行后清理（对照 AHK `:764 CleanAutoTmp`）。
    /// </summary>
    public class PluginRunner
    {
        private readonly string _root;
        private readonly string _ahkExe;
        private readonly string _tmpDir;
        private readonly Action<string> _log;

        /// <summary>有界等待上限（ms）—— 对照 AHK 的 3000ms。</summary>
        public int TimeoutMs { get; set; }

        public PluginRunner(string root, string ahkExe = null, int timeoutMs = 3000,
            Action<string> log = null)
        {
            _root = root;
            _ahkExe = ResolveAhkExe(root, ahkExe, log);
            _tmpDir = Path.Combine(root, "state", "autotmp");
            _log = log;
            TimeoutMs = timeoutMs;
        }

        public string AhkExe { get { return _ahkExe; } }
        public string TmpDir { get { return _tmpDir; } }

        private void L(string s)
        {
            if (_log != null) _log(s);
        }

        // ============================================================
        // AHK 运行时取值口径 —— ★ 唯一来源 = CaptureClient.AhkExe。
        //
        // 为什么必须取同一份：采集器与插件运行必须是【同一个运行时】，否则会出现
        //   "采集能用、插件不能用"的分裂（本机装了两个版本的 AHK 的经典症状）。
        //   （早期版本这里用反射读 CaptureClient 的 private 字段；现已由
        //     CaptureClient 公开 AhkExe 属性，改为直接调用。）
        // ============================================================
        private static string ResolveAhkExe(string root, string explicitExe, Action<string> log)
        {
            if (!string.IsNullOrEmpty(explicitExe)) return explicitExe;

            try
            {
                string v = new CaptureClient(root).AhkExe;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            catch (Exception ex)
            {
                if (log != null) log("  插件运行: 取 AHK 运行时口径失败（用默认值）: " + ex.Message);
            }
            return @"C:\Program Files\AutoHotkey\v2\AutoHotkey64.exe";
        }

        // ============================================================
        // 调插件 Run(text) 产出目标。
        // 返回：目标字符串；任何失败返回 ""（调用方回落弹菜单，绝不抛）
        // ============================================================
        public string Run(PluginInfo p, string text)
        {
            if (p == null || string.IsNullOrEmpty(p.Path)) return "";
            if (!File.Exists(p.Path))
            {
                L("  自动执行: 插件文件不存在 " + p.Path);
                return "";
            }

            string stamp = Environment.TickCount + "_" + new Random().Next(10000, 99999);
            string inFile = Path.Combine(_tmpDir, "in_" + stamp + ".txt");
            string outFile = Path.Combine(_tmpDir, "out_" + stamp + ".txt");
            string drvFile = Path.Combine(_tmpDir, "drv_" + stamp + ".ahk");

            try
            {
                if (!Directory.Exists(_tmpDir)) Directory.CreateDirectory(_tmpDir);
            }
            catch (Exception ex)
            {
                L("  自动执行: 建临时目录失败: " + ex.Message);
                return "";
            }

            try
            {
                // 写输入（选中文本独立成文件，免参数转义问题）
                try
                {
                    File.WriteAllText(inFile, text ?? "", new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    L("  自动执行: 写输入文件失败: " + ex.Message);
                    return "";
                }

                // 生成驱动脚本（模板与 AHK 版逐行同构）
                try
                {
                    File.WriteAllText(drvFile, BuildDriver(p.Path), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    L("  自动执行: 写驱动脚本失败: " + ex.Message);
                    return "";
                }

                return ExecDriver(drvFile, inFile, outFile);
            }
            finally
            {
                // ★ 无论成败都清理三个临时文件（对照 CleanAutoTmp）
                CleanAutoTmp(inFile, outFile, drvFile);
            }
        }

        /// <summary>
        /// 驱动脚本正文 —— 与 AHK `CallPluginRun` 生成的 vDrv 逐行等价。
        /// ★ #Include 的路径必须用双引号包裹（防空格路径断裂）。
        /// ★ 不用"判断 Run 是否存在"的写法（AHK v2 里 IsSet 对函数无效）：
        ///   直接用 try 包住调用，缺函数时抛错被吃掉 ⇒ 结果空串 ⇒ 回落弹菜单。
        /// </summary>
        private static string BuildDriver(string pluginPath)
        {
            const string Q = "\"";
            var sb = new StringBuilder();
            sb.Append("#Requires AutoHotkey v2.0\r\n");
            sb.Append("#SingleInstance Off\r\n");
            sb.Append("#NoTrayIcon\r\n");
            sb.Append("vIn  := A_Args[1]\r\n");
            sb.Append("vOut := A_Args[2]\r\n");
            sb.Append("vTxt := " + Q + Q + "\r\n");
            sb.Append("try vTxt := FileRead(vIn, " + Q + "UTF-8" + Q + ")\r\n");
            sb.Append("vResult := " + Q + Q + "\r\n");
            sb.Append("try {\r\n");
            sb.Append("  #Include " + Q + pluginPath + Q + "\r\n");
            sb.Append("  vResult := Run(vTxt)\r\n");
            sb.Append("} catch {\r\n");
            sb.Append("  vResult := " + Q + Q + "\r\n");
            sb.Append("}\r\n");
            sb.Append("try {\r\n");
            sb.Append("  if FileExist(vOut)\r\n");
            sb.Append("    FileDelete(vOut)\r\n");
            sb.Append("  FileAppend(vResult, vOut, " + Q + "UTF-8" + Q + ")\r\n");
            sb.Append("} catch {\r\n");
            sb.Append("}\r\n");
            sb.Append("ExitApp(0)\r\n");
            return sb.ToString();
        }

        /// <summary>
        /// 跑驱动脚本并读回输出。有界等待（TimeoutMs）后强杀。
        ///
        /// ★ 与 AHK 的差异（更严，不改变成功语义）：进程已退出仍无输出文件时
        ///   立即判失败，不再空等满超时（AHK 会白等 3 秒）。
        /// </summary>
        private string ExecDriver(string drvFile, string inFile, string outFile)
        {
            Process proc = null;
            try
            {
                var psi = new ProcessStartInfo();
                psi.FileName = _ahkExe;
                psi.Arguments = "\"" + drvFile + "\" \"" + inFile + "\" \"" + outFile + "\"";
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                var sw = Stopwatch.StartNew();
                proc = Process.Start(psi);

                while (sw.ElapsedMilliseconds < TimeoutMs)
                {
                    if (File.Exists(outFile)) break;
                    if (proc.HasExited) break;     // 进程没了 → 不会再有输出
                    Thread.Sleep(30);
                }

                if (!proc.HasExited)
                {
                    L("  自动执行: 插件进程超时（" + TimeoutMs + "ms），已杀");
                    try { proc.Kill(); } catch { }
                }

                if (!File.Exists(outFile))
                {
                    L("  自动执行: 插件未产出结果文件（exit="
                        + (proc.HasExited ? proc.ExitCode.ToString() : "?") + "）");
                    return "";
                }

                // AHK: Trim(FileRead(vOutFile, "UTF-8"), " `t`r`n")
                string result = File.ReadAllText(outFile, Encoding.UTF8).Trim(' ', '\t', '\r', '\n');
                L("  自动执行: 插件产出 " + result.Length + "B（耗时 " + sw.ElapsedMilliseconds + "ms）");
                return result;
            }
            catch (Exception ex)
            {
                L("  自动执行: 插件执行异常: " + ex.Message);
                return "";
            }
            finally
            {
                // 超时被杀的进程要收尸，避免句柄泄漏（常驻进程会累积）
                try { if (proc != null) proc.Dispose(); } catch { }
            }
        }

        /// <summary>清理临时文件（对照 AHK `CleanAutoTmp`）——每个都独立 try，互不牵连。</summary>
        public static void CleanAutoTmp(string a, string b, string c)
        {
            try { if (File.Exists(a)) File.Delete(a); } catch { }
            try { if (File.Exists(b)) File.Delete(b); } catch { }
            try { if (File.Exists(c)) File.Delete(c); } catch { }
        }
    }
}
