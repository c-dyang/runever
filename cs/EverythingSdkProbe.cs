// ============================================================
// RunEver · EV 后端探针 CLI（S0）
//
// 用法：
//   RunEverCs.exe --ev-probe [root] [out.txt]
//   RunEverCs.exe --ev-probe --dll <Everything64.dll 路径> [out.txt]
//
// ★ 退出码恒为 0：探针失败 = 后端降级（正常路径），不是错误。
//   "EV 是否可用"只作为**信息**输出 —— 否则换一台没装 Everything 的机器全红。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Services;

namespace RunEverCs
{
    public static class EverythingSdkProbe
    {
        public static int Run(string[] args)
        {
            string root = null, dll = null, outFile = null;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--root" && i + 1 < args.Length) { root = args[++i]; continue; }
                if (args[i] == "--dll" && i + 1 < args.Length) { dll = args[++i]; continue; }
                if (root == null) { root = args[i]; continue; }
                if (outFile == null) { outFile = args[i]; continue; }
            }
            if (root == null) root = AppDomain.CurrentDomain.BaseDirectory;

            var sb = new StringBuilder();
            sb.AppendLine("=== EV (Everything SDK) 探针 ===");
            sb.AppendLine("进程位数 = " + (IntPtr.Size == 8 ? "x64" : "x86")
                + "，期望 DLL = " + EvDllLocator.ProcessDllName());
            sb.AppendLine("root = " + root);

            string autoConf = BackendResolver.AutoConfPath(root);
            string configured = dll != null ? dll : BackendResolver.ReadEvDll(root);
            sb.AppendLine("auto.conf evdll= [" + (BackendResolver.ReadEvDll(root) ?? "") + "]"
                + (dll != null ? "（命令行覆盖 → " + dll + "）" : ""));
            sb.AppendLine("auto.conf = " + autoConf + "（存在=" + File.Exists(autoConf) + "）");
            sb.AppendLine();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            BackendStatus st = EverythingProbe.ProbeOnce(configured);
            sw.Stop();
            sb.AppendLine("候选探测顺序：");
            foreach (string c in EvDllLocator.Candidates(configured))
                sb.AppendLine("  - " + c + "   [存在=" + File.Exists(c) + "]");
            sb.AppendLine();
            sb.AppendLine("结论 = " + st.Describe());
            sb.AppendLine("  Kind          = " + st.KindText);
            sb.AppendLine("  DllPath       = " + (st.DllPath.Length > 0 ? st.DllPath : "<无>"));
            sb.AppendLine("  EvVersion     = " + (st.EvVersion.Length > 0 ? st.EvVersion : "<未取到>"));
            sb.AppendLine("  ProbeHits     = " + st.ProbeHits + "  （判据：> 0）");
            sb.AppendLine("  LastError     = " + st.LastError + "  （判据：== 0；2 = Everything 未运行/IPC 不通）");
            sb.AppendLine("  DbLoaded      = " + (st.DbLoaded ? "true" : "false"));
            sb.AppendLine("  全路径读回校验 = " + (st.FullPathVerified ? "一致" : "不一致/未做"));
            sb.AppendLine("  Reason        = " + (st.Reason.Length > 0 ? st.Reason : "<无>"));
            sb.AppendLine("  总耗时        = " + sw.ElapsedMilliseconds + "ms（探针内部 " + st.ProbeMs + "ms）");

            // 降级链全貌（EV → es → none）
            var chain = BackendResolver.Resolve(root, true);
            string esProbe = PathCacheBuilder.ResolveEsPath(
                PathCacheBuilder.ReadEsPathFromConfig(autoConf));
            sb.AppendLine();
            sb.AppendLine("降级链解析 = " + chain.Describe());
            sb.AppendLine("  es.exe = " + (esProbe.Length > 0 ? esProbe : "<不可用>")
                + (esProbe.Length == 0 && !string.IsNullOrEmpty(chain.EsPath) ? "（已探测: " + chain.EsPath + "）" : ""));
            sb.AppendLine("  当前生效 = backend=" + chain.KindText
                + (chain.Kind == BackendKind.None ? "（只剩 PATH + App Paths，未命中项一律「待确认」）" : ""));
            sb.AppendLine("  降级链：EV → es.exe → PATH+App Paths；任一级失败都只是降级，不抛异常");

            // 额外：精确匹配验证（code.exe 不得命中 vscode.exe）
            if (st.Kind == BackendKind.Everything)
            {
                sb.AppendLine();
                sb.AppendLine("精确匹配验证（客户端按文件名全等过滤，等价 es 的 wfn:）：");
                var be = new EverythingBackend(configured, true);
                foreach (string probe in new[] { "code.exe", "vscode.exe", "7z.exe" })
                {
                    List<string> r = be.Search(probe, 30);
                    sb.AppendLine("  " + probe + " → " + r.Count + " 条");
                    foreach (string p in r)
                    {
                        bool exact = string.Equals(Path.GetFileName(p), probe, StringComparison.OrdinalIgnoreCase);
                        sb.AppendLine("      " + (exact ? "[exact] " : "[!!非全等!!] ") + p);
                    }
                }
            }

            string text = sb.ToString();
            try { Console.Out.Write(text); } catch { }
            if (!string.IsNullOrEmpty(outFile))
            {
                try
                {
                    string dir = Path.GetDirectoryName(outFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(outFile, text, new UTF8Encoding(false));
                }
                catch { }
            }
            return 0;
        }
    }
}
