// ============================================================
// RunEver · 全盘自检 CLI（托盘「全盘自检…」的命令行等价物）
//
// 用法：
//   RunEverCs.exe --selfcheck <root> [out.txt] [--no-writeback]
//
//   out.txt        文本报告落点（省略 = 只打印到 stderr）
//   --no-writeback 不写回 cache\paths.txt（只检查、不落盘）
//
// ★ 不做一键安装：本 CLI 只**生成** choco 命令清单 + 同名 .bat（`<out.txt>.bat`），
//   绝不执行 choco、绝不提权。
// ★ 退出码：0 = 跑完（哪怕有缺失项）；1 = 引擎异常。**不把"有缺失项"当失败**
//   —— 缺件是业务结果，不是程序错误。
// ============================================================

using System;
using System.IO;
using System.Text;
using RunEverCs.Services;

namespace RunEverCs
{
    public static class SelfCheckCli
    {
        public static int Run(string[] args)
        {
            string root = null, outFile = null;
            bool writeBack = true;
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--root" && i + 1 < args.Length) { root = args[++i]; continue; }
                if (args[i] == "--out" && i + 1 < args.Length) { outFile = args[++i]; continue; }
                if (args[i] == "--no-writeback") { writeBack = false; continue; }
                if (root == null) { root = args[i]; continue; }
                if (outFile == null) { outFile = args[i]; continue; }
            }
            if (root == null) root = AppDomain.CurrentDomain.BaseDirectory;

            try
            {
                var rep = SelfCheck.Run(root, s => Console.Error.WriteLine("[自检] " + s),
                    null, null, writeBack);

                string report = rep.Render();
                string bat = rep.BuildBat();

                Console.Error.WriteLine(rep.SummaryLine());
                Console.Error.WriteLine("写回缓存：" + (rep.CacheWriteBackNote.Length > 0 ? rep.CacheWriteBackNote : "(未写回)"));

                if (!string.IsNullOrEmpty(outFile))
                {
                    string dir = Path.GetDirectoryName(outFile);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(outFile, report, new UTF8Encoding(false));

                    string batFile = outFile + ".bat";
                    File.WriteAllText(batFile, bat, new UTF8Encoding(false));
                    Console.Error.WriteLine("报告 → " + outFile);
                    Console.Error.WriteLine("命令清单 → " + batFile + "（未执行，交用户自行审阅）");
                }
                else
                {
                    Console.Error.WriteLine(report);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("全盘自检失败: " + ex);
                return 1;
            }
        }
    }
}
