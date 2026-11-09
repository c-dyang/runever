using System;

namespace RunEverCs.Services
{
    /// <summary>
    /// 占位符展开 —— **唯一源**（契约 §6：占位符与判定层同步维护）。
    ///
    /// `Executor.ReplaceVars` 只能委托到这里，**禁止两份替换逻辑**。
    /// 判定层与执行层共用同一个 `MatchContext`（值来源唯一）。
    ///
    /// 语义（逐字承接现役 `Executor.cs:248-263` 口径，不改）：
    ///   %text% 选中文本        %file% 首个选中文件（全路径）
    ///   %clip% 剪贴板          %win%  前台窗口标题
    ///   %proc% 前台进程名      %s     搜索词：优先选中文本，无则剪贴板
    ///
    /// ★ 大小写**敏感**（现役行为，逐字保留）：`%TEXT%` 不会被替换。
    /// ★ `%s` 最后替换（否则文本内容里恰好含 `%clip%` 之类的字面量会被二次替换）。
    /// ★ 空值替换为空串（不是原样保留占位符）——与现役一致。
    /// </summary>
    public static class PlaceholderExpander
    {
        public static string Expand(string template, MatchContext ctx)
        {
            // currentFile = null ⇒ 回落 ctx.FirstFilePath（既有单文件语义，逐字不变）
            return Expand(template, ctx, null);
        }

        /// <summary>
        /// 带「当前文件」的展开（2026-11-09 U-D 批量执行）：`%file%` 换成 <paramref name="currentFile"/>
        /// —— 同一模板 × N 个选中文件时，每个文件展开一次。
        ///
        /// ★ `currentFile == null` ⇒ 等价于 <see cref="Expand(string, MatchContext)"/>（不改既有语义）；
        ///   `currentFile == ""` ⇒ 明确按空串替换（"当前文件是空"，不是"没有当前文件"）。
        /// ★ 其余占位符仍全部取自 ctx（批量只改 `%file%` 的口径）。
        /// </summary>
        public static string Expand(string template, MatchContext ctx, string currentFile)
        {
            if (string.IsNullOrEmpty(template)) return "";
            if (ctx == null) return template;

            string s = template;
            s = s.Replace("%text%", ctx.Text ?? "");
            s = s.Replace("%file%", currentFile != null ? currentFile : (ctx.FirstFilePath ?? ""));
            s = s.Replace("%clip%", ctx.Clipboard ?? "");
            s = s.Replace("%win%", ctx.WinTitle ?? "");
            s = s.Replace("%proc%", ctx.ProcessName ?? "");

            // %s = 选中文本优先，无则剪贴板（现役口径 Executor.cs:259-261）
            string sVal = ctx.Text;
            if (string.IsNullOrEmpty(sVal)) sVal = ctx.Clipboard ?? "";
            s = s.Replace("%s", sVal);

            return s;
        }

        /// <summary>模板里是否含任一受支持占位符（自检/排障用）。</summary>
        public static bool HasPlaceholder(string template)
        {
            if (string.IsNullOrEmpty(template)) return false;
            return template.IndexOf('%') >= 0;
        }

        /// <summary>
        /// 受支持的占位符 token（**唯一集合**，与 <see cref="Expand"/> 同源）。
        /// 大小写敏感 —— 与现役口径一致（`%TEXT%` 不是占位符）。
        /// </summary>
        public static readonly string[] Tokens =
        {
            "%text%", "%file%", "%clip%", "%win%", "%proc%", "%s"
        };

        /// <summary>
        /// 模板里是否含**受支持的占位符 token**（精确匹配）—— 契约 §6 自动追加规则的判据。
        ///
        /// ★ 与 `HasPlaceholder` 的区别（**不要把两者合并**）：
        ///   `HasPlaceholder` = 含任意 `%`（宽口径，`SelfCheck` 的静态检查在用），
        ///   拿它判自动追加会把 `C:\100%\out` 误判成"含占位符"因而不追加；
        ///   本方法只认 6 个 token ⇒ 上述路径正常走自动追加。
        /// </summary>
        public static bool ContainsPlaceholder(string template)
        {
            if (string.IsNullOrEmpty(template)) return false;
            for (int i = 0; i < Tokens.Length; i++)
                if (template.IndexOf(Tokens[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }
    }
}
