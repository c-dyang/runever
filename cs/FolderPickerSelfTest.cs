using System;
using System.IO;
using System.Text;
using RunEverCs.Native;

namespace RunEverCs
{
    /// <summary>
    /// 系统默认文件夹选择窗口（IFileOpenDialog）互操作自检。
    /// 用法：RunEverCs.exe --selftest-folderdlg &lt;probeDir&gt; &lt;out.txt&gt;
    ///   不弹窗 ⇒ 可无人值守；Show/GetResult 由实机点「浏览…」验收。
    /// </summary>
    public static class FolderPickerSelfTest
    {
        public static int Run(string[] args)
        {
            if (args.Length < 3) return 2;

            string probeDir = args[1];
            string outFile = args[2];

            string report;
            if (!Directory.Exists(probeDir))
            {
                report = "参数错误：探测目录不存在 → " + probeDir
                    + "\n用法：RunEverCs.exe --selftest-folderdlg <probeDir> <out.txt>";
                try { File.WriteAllText(outFile, report, new UTF8Encoding(false)); } catch { }
                return 2;
            }

            try
            {
                report = ModernFolderPicker.SelfTestReport(probeDir);
            }
            catch (Exception ex)
            {
                report = "异常: " + ex.GetType().Name + ": " + ex.Message
                    + Environment.NewLine + ex.StackTrace;
            }

            try { File.WriteAllText(outFile, report, new UTF8Encoding(false)); }
            catch { }

            return report.IndexOf("FAIL", StringComparison.Ordinal) >= 0 ? 1 : 0;
        }
    }
}
