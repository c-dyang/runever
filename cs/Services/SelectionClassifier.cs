using System;
using System.Collections.Generic;
using System.IO;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 选中项分类器 —— R5 的数据前置（**App 侧，允许 IO**）。
    ///
    /// 为什么必须单独有一个：采集链只给出 `files` 数组 + 聚合 `HasFolder`，
    /// 无法区分"只有文件夹"与"文件 + 文件夹混合" ⇒ R5 判不出来。
    /// 采集链（CaptureClient / CaptureRunner.exe）冻结不许改，所以分类放这里，
    /// 复用现役 `MakeCtx` 已有的那次逐条遍历，只把它改成**分别计数**。
    ///
    /// 同时承担"扩展名提取"——原实现在判定层里 `Path.GetExtension`（= 判定层 IO 泄漏），
    /// 按契约 §2 搬到 IO 允许的这一层。
    /// </summary>
    public static class SelectionClassifier
    {
        /// <summary>逐条分类（文件 / 文件夹 / 都不存在）并计数。</summary>
        public static SelectionKind Classify(IList<string> paths, out int fileCount, out int folderCount)
        {
            fileCount = 0;
            folderCount = 0;
            if (paths != null)
            {
                foreach (var raw in paths)
                {
                    if (string.IsNullOrEmpty(raw)) continue;
                    string p = raw.Trim().Trim('"');
                    if (p.Length == 0) continue;
                    bool isDir = false, isFile = false;
                    try { isDir = Directory.Exists(p); } catch { }
                    if (isDir) { folderCount++; continue; }
                    try { isFile = File.Exists(p); } catch { }
                    if (isFile) { fileCount++; continue; }
                    // 都不存在（选中项已被删/路径非法）：按"文件"计入，避免把形态判成 None
                    fileCount++;
                }
            }
            return Shape(fileCount, folderCount);
        }

        /// <summary>由计数推形态（分类之外的第二入口，自检直接用）。</summary>
        public static SelectionKind Shape(int fileCount, int folderCount)
        {
            if (fileCount > 0 && folderCount > 0) return SelectionKind.Mixed;
            if (fileCount > 0) return SelectionKind.File;
            if (folderCount > 0) return SelectionKind.Folder;
            return SelectionKind.None;
        }

        /// <summary>取选中项的扩展名（小写、去点、可重复）——仅对**文件**取。</summary>
        public static List<string> ExtensionsLower(IList<string> paths)
        {
            var exts = new List<string>();
            if (paths == null) return exts;
            foreach (var raw in paths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string p = raw.Trim().Trim('"');
                if (p.Length == 0) continue;
                try
                {
                    if (Directory.Exists(p)) continue;          // 文件夹不贡献扩展名（R2/R3 只论文件）
                    string e = Path.GetExtension(p);
                    if (!string.IsNullOrEmpty(e))
                        exts.Add(e.TrimStart('.').ToLowerInvariant());
                }
                catch { }
            }
            return exts;
        }

        /// <summary>首个选中文件的全路径（%file% 用；找不到返回空串）。</summary>
        public static string FirstFilePath(IList<string> paths)
        {
            if (paths == null) return "";
            foreach (var raw in paths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string p = raw.Trim().Trim('"');
                if (p.Length == 0) continue;
                try { if (Directory.Exists(p)) continue; } catch { }
                return p;
            }
            return "";
        }

        /// <summary>
        /// 采集结果 → 判定上下文（唯一入口；RunEverApp 与自检共用）。
        /// ★ 不做缺件注入 —— 那是解析层的活（PathResolvePolicy.InjectEntryResolved）。
        /// </summary>
        public static MatchContext Build(string text, IList<string> paths, string procName,
            string clipboard, string winTitle)
        {
            int fc, dc;
            var kind = Classify(paths, out fc, out dc);
            var ctx = new MatchContext
            {
                Kind = kind,
                Text = text ?? "",
                Clipboard = clipboard ?? "",
                WinTitle = winTitle ?? "",
                ProcessName = procName ?? "",
                FirstFilePath = FirstFilePath(paths),
                ExtensionsLower = ExtensionsLower(paths),
                FileCount = fc,
                FolderCount = dc
            };
            return ctx;
        }

        /// <summary>把现役采集结果（Context）转成判定上下文 —— 一处转换，避免两套口径。</summary>
        public static MatchContext FromCaptured(Context captured)
        {
            if (captured == null) return new MatchContext();
            return Build(captured.Text, captured.Files, captured.Proc,
                captured.Clipboard, captured.WinTitle);
        }
    }
}
