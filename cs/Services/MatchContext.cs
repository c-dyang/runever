using System;
using System.Collections.Generic;

namespace RunEverCs.Services
{
    /// <summary>选中形态（R1–R6 的输入分类，见架构契约 §5）。</summary>
    public enum SelectionKind
    {
        /// <summary>无选中（R6）。</summary>
        None = 0,

        /// <summary>选中文本（R1）。</summary>
        Text,

        /// <summary>只选中文件（R2/R3）。</summary>
        File,

        /// <summary>只选中文件夹（R4）。</summary>
        Folder,

        /// <summary>文件 + 文件夹同时选中（R5）。</summary>
        Mixed
    }

    /// <summary>
    /// 判定层上下文 —— **纯数据**。
    ///
    /// ★ 架构契约 §2 硬边界：判定层零 IO。本文件**不许**读盘 / 查 EV / 读缓存 /
    ///   解析注册表；所有"需要在盘上问一句"的值都由解析层（PathResolvePolicy）
    ///   与分类器（SelectionClassifier）先算好，以**值**的形式注入：
    ///     · FileCount / FolderCount —— 选中项分类的计数（不接收路径数组）
    ///     · ExtensionsLower         —— 已提好的小写扩展名（不含点）
    ///     · EntryResolved[]         —— 缺件注入位（true = 该下标条目可用）
    ///     · MissingItemIndex        —— 被判定为缺件的**全局条目下标**
    ///   这样判定层可纯函数化、可全量单测（`--selftest-matcher` 不创建任何文件）。
    ///
    /// ★ 索引口径：`EntryResolved[i] / MissingItemIndex` 里的 `i` = 按传入分组列表
    ///   顺序**展平后的条目序号**（分组序 → 组内条目序），由
    ///   `PathResolvePolicy.InjectEntryResolved` 与 `MatchEngine.Decide` 共用同一份列表得出。
    /// </summary>
    public class MatchContext
    {
        /// <summary>选中形态（分类器产出；判定层只读）。</summary>
        public SelectionKind Kind = SelectionKind.None;

        /// <summary>选中文本（R1 的判据，也是 %text% / %s 的来源）。</summary>
        public string Text = "";

        /// <summary>剪贴板文本（%clip% / %s 兜底）。</summary>
        public string Clipboard = "";

        /// <summary>前台窗口标题（%win%）。</summary>
        public string WinTitle = "";

        /// <summary>前台进程名（原样，含 .exe；%proc%）。</summary>
        public string ProcessName = "";

        /// <summary>首个选中文件的全路径（%file%）。</summary>
        public string FirstFilePath = "";

        /// <summary>已归一化（去点 + 小写）的选中文件扩展名，可含重复。</summary>
        public List<string> ExtensionsLower = new List<string>();

        /// <summary>选中项里的文件数（分类器计数，判定层不自己数）。</summary>
        public int FileCount;

        /// <summary>选中项里的文件夹数。</summary>
        public int FolderCount;

        /// <summary>
        /// 缺件注入位：下标 = 展平条目序号，true = 该条目本机可用（保留）。
        /// 长度与分组列表条目数不一致时判定层按"全部可用"处理（安全默认，不误杀）。
        /// </summary>
        public bool[] EntryResolved = new bool[0];

        /// <summary>被判为缺件的条目下标集合（抑制显示的唯一数据来源）。</summary>
        public HashSet<int> MissingItemIndex = new HashSet<int>();

        /// <summary>有选中文本（trim 后非空）。</summary>
        public bool HasText
        {
            get { return !string.IsNullOrEmpty(Text) && Text.Trim().Length > 0; }
        }

        /// <summary>仅选中文件（R2/R3 前置）。</summary>
        public bool FileOnly { get { return FileCount > 0 && FolderCount == 0; } }

        /// <summary>仅选中文件夹（R4 前置）。</summary>
        public bool FolderOnly { get { return FolderCount > 0 && FileCount == 0; } }

        /// <summary>文件 + 文件夹同时选中（R5 前置）。</summary>
        public bool MixedFilesAndFolders { get { return FileCount > 0 && FolderCount > 0; } }

        /// <summary>前台进程名小写（R7 比对用）。</summary>
        public string ProcessNameLower
        {
            get { return (ProcessName ?? "").Trim().ToLowerInvariant(); }
        }

        /// <summary>该下标条目是否可用（缺件注入位的安全读取：越界 = 可用）。</summary>
        public bool IsEntryResolved(int index)
        {
            if (EntryResolved == null) return true;
            if (index < 0 || index >= EntryResolved.Length) return true;
            return EntryResolved[index];
        }

        /// <summary>该下标条目是否被判缺件。</summary>
        public bool IsEntryMissing(int index)
        {
            return MissingItemIndex != null && MissingItemIndex.Contains(index);
        }
    }
}
