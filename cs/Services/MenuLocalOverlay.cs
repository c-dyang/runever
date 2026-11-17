// ============================================================
// RunEver · menulocal.conf 本机覆盖层（卡 F · 2026-11-17b）
//
// 目的：`config\menus.conf` 作**基**（跨机共享 / WebDAV 同步），
//       `config\menulocal.conf` 作**本机增量**（换机 / 多机不同配置），
//       加载时在内存里叠加成"合并后的结果"，构建菜单与判定都看这一份。
//
// 三条硬约束（卡 F 定案）：
//   ① 文件不存在 = 完全等于今天的行为（零变化）：不碰 groups、不克隆、不校验；
//   ② 合并语义**可枚举且保守**：同 `Level` + 同 `标题` ⇒ 覆盖动作/目标/快捷键/修饰位；
//      否则按 `Level` 插到父项子树末尾；父项不存在（跳级）⇒ **整份覆盖层丢弃**；
//   ③ **覆盖层是只读输入**：绝不写回 menus.conf。
//
// ★ 为什么合并点在"解析之后、构建之前"，而**不**塞进 `ConfParser.Parse()`：
//   `MenuConfWriter`（保存）的窄写回以 `new ConfParser(menus.conf)` 作"磁盘现状"。
//   若 Parse 里就掺进覆盖层，编辑器保存时菜单模型里会多出"磁盘上没有"的条目，
//   窄写回必然退化成整份重写 ⇒ **把覆盖层内容写进基文件**，直接违反约束 ③。
//   ⇒ 唯一合并点 = `MenuLocalOverlay.Apply(parser, menus.conf, log)`，
//     由宿主（RunEverApp）在三处调用：Init / ReloadMenus / HotReloadCheck。
//
// ★ 层级不变式**不另写一套**：合并后直接跑 `MenuStructureValidator.Validate`
//   （与保存前那道闸同一套判据）。合并后问题数 > 基线问题数 ⇒ 整份丢弃 + 告警，
//   基配置照常工作（绝不产出坏树）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>一次覆盖层合并的结果（可枚举的数：覆盖 / 新增 / 新分组 + 丢弃原因）。</summary>
    public sealed class OverlayResult
    {
        /// <summary>menulocal.conf 是否存在（false = 与基线逐字节一致）。</summary>
        public bool Present;

        /// <summary>是否真的合并进了 groups（false + Present = 被丢弃，原因见 <see cref="Reason"/>）。</summary>
        public bool Applied;

        /// <summary>被覆盖的既有条目数（同 Level + 同标题）。</summary>
        public int Overridden;

        /// <summary>新增条目数（插到父项子树末尾）。</summary>
        public int Added;

        /// <summary>新增分组数（覆盖层里出现基配置没有的分组名）。</summary>
        public int NewGroups;

        /// <summary>覆盖层文件路径（Present 时非空）。</summary>
        public string Path = "";

        /// <summary>结论/原因（恒有值，日志与自检共用同一句话）。</summary>
        public string Reason = "";

        public string Describe()
        {
            if (!Present) return "无覆盖层（menulocal.conf 不存在）";
            string head = Applied ? "已合并" : "已丢弃";
            return head + "：" + Reason
                + "（覆盖 " + Overridden + " 条 / 新增 " + Added + " 条 / 新分组 " + NewGroups + " 个）";
        }
    }

    /// <summary>
    /// menulocal.conf 覆盖层的**唯一合并点**（解析之后、构建之前）。
    /// 语义见文件头；本类只读覆盖层，绝不写盘。
    /// </summary>
    public static class MenuLocalOverlay
    {
        /// <summary>覆盖层文件名（与 menus.conf 同目录）。</summary>
        public const string FileName = "menulocal.conf";

        /// <summary>menus.conf 路径 → 同目录的 menulocal.conf 路径（拿不到目录 ⇒ ""）。</summary>
        public static string OverlayPathFor(string baseConfPath)
        {
            if (string.IsNullOrEmpty(baseConfPath)) return "";
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(baseConfPath));
                if (string.IsNullOrEmpty(dir)) return "";
                return Path.Combine(dir, FileName);
            }
            catch { return ""; }
        }

        /// <summary>
        /// 把 <paramref name="baseConfPath"/> 同目录的 menulocal.conf 叠加到 <paramref name="parser"/> 上。
        /// 幂等（同一 parser 只合一次）；任何失败都**不抛**，只返回 Present/Applied + Reason。
        /// </summary>
        public static OverlayResult Apply(ConfParser parser, string baseConfPath, Action<string> log)
        {
            var r = new OverlayResult();
            if (parser == null) { r.Reason = "解析器为空，跳过"; return r; }

            if (parser.OverlayApplied)
            {
                r.Present = true; r.Applied = true; r.Reason = "已合并过（幂等跳过）";
                return r;
            }

            string ovPath = OverlayPathFor(baseConfPath);
            bool exists = false;
            try { exists = ovPath.Length > 0 && File.Exists(ovPath); }
            catch { exists = false; }

            if (!exists)
            {
                // ★ 零变化分支：文件不存在 ⇒ 一个字段都不动（调用方拿到的树 = 基线树）
                r.Reason = "menulocal.conf 不存在";
                return r;
            }

            r.Present = true;
            r.Path = ovPath;

            List<MenuGroup> overlay;
            try
            {
                // 只读解析：不做结构升格（合并后统一校验），也**不**再套覆盖层（防递归）
                overlay = ConfParser.ParseFileReadOnly(ovPath);
            }
            catch (Exception ex)
            {
                r.Reason = "覆盖层解析失败 ⇒ 整份丢弃：" + ex.Message;
                Warn(log, r.Reason + "（文件 " + ovPath + "）");
                return r;
            }

            List<MenuGroup> baseGroups = parser.Groups ?? new List<MenuGroup>();
            List<MenuGroup> merged = CloneGroups(baseGroups);      // 先克隆 ⇒ 失败即丢，基树一字节不受影响

            string why;
            if (!MergeInto(merged, overlay, r, out why))
            {
                r.Reason = why;
                Warn(log, "覆盖层已丢弃：" + why + "（基配置照常工作）");
                return r;
            }

            // ============================================================
            // 合并后重新校验层级不变式（唯一一套判据 = MenuStructureValidator）
            //   只有"合并**新增**了问题"才丢弃 —— 基配置自身的历史问题不算覆盖层的账。
            // ============================================================
            List<MenuStructureIssue> issues = MenuStructureValidator.Validate(merged);
            if (issues.Count > 0)
            {
                int baseIssues = MenuStructureValidator.Validate(baseGroups).Count;
                if (issues.Count > baseIssues)
                {
                    r.Reason = "合并后层级不变式被破坏（问题 " + issues.Count + " 处 > 基线 " + baseIssues
                        + " 处）⇒ 整份丢弃；首处：" + issues[0].Where;
                    Warn(log, "覆盖层已丢弃：" + r.Reason);
                    return r;
                }
            }

            parser.ReplaceGroups(merged);
            parser.OverlayApplied = true;

            r.Applied = true;
            r.Reason = "同 Level + 同标题覆盖 " + r.Overridden + " 条；新增 " + r.Added + " 条"
                + (r.NewGroups > 0 ? "；新分组 " + r.NewGroups + " 个" : "");
            Note(log, "menulocal.conf 覆盖层" + r.Describe());
            return r;
        }

        // ============================================================
        // 合并
        // ============================================================
        /// <summary>
        /// 逐分组合并：分组按**名**（大小写不敏感）对齐；分组不存在 ⇒ 追加一个新分组。
        /// 条目按 (Level, 标题) 对齐：命中 ⇒ 覆盖动作位；未命中 ⇒ 插到父项子树末尾。
        /// 返回 false = 结构上无法安全合并（调用方整份丢弃）。
        /// </summary>
        private static bool MergeInto(List<MenuGroup> merged, List<MenuGroup> overlay,
            OverlayResult r, out string why)
        {
            why = "";
            if (overlay == null) return true;

            foreach (MenuGroup og in overlay)
            {
                if (og == null) continue;
                string gname = (og.Name ?? "").Trim();
                if (gname.Length == 0)
                {
                    why = "覆盖层里有一个分组名为空的分组（写出去会把类型当成组名）";
                    return false;
                }

                MenuGroup target = null;
                foreach (MenuGroup g in merged)
                {
                    if (g == null) continue;
                    if (string.Equals((g.Name ?? "").Trim(), gname, StringComparison.OrdinalIgnoreCase))
                    { target = g; break; }
                }

                if (target == null)
                {
                    // 基配置没有这个分组 ⇒ 整组追加到末尾（覆盖层可"加组"，但不动既有组）
                    var ng = new MenuGroup
                    {
                        Name = gname,
                        Type = og.Type,
                        Keywords = new List<string>(og.Keywords ?? new List<string>()),
                        Line = 0
                    };
                    foreach (MenuItem oi in og.Items ?? new List<MenuItem>())
                        if (oi != null) ng.Items.Add(CopyItem(oi));
                    merged.Add(ng);
                    r.NewGroups++;
                    continue;
                }

                foreach (MenuItem oi in og.Items ?? new List<MenuItem>())
                {
                    if (oi == null) continue;

                    if (oi.Level < 0)
                    {
                        why = "覆盖层条目「" + oi.Title + "」层级为负（" + oi.Level + "）";
                        return false;
                    }

                    int hit = FindItem(target.Items, oi.Level, oi.Title);
                    if (hit >= 0)
                    {
                        OverrideItem(target.Items[hit], oi);
                        r.Overridden++;
                        continue;
                    }

                    if (!InsertIntoSubtree(target.Items, oi, out why)) return false;
                    r.Added++;
                }
            }
            return true;
        }

        /// <summary>同 Level + 同标题（严格大小写）⇒ 第一条命中；没有 ⇒ -1。</summary>
        private static int FindItem(List<MenuItem> items, int level, string title)
        {
            if (items == null) return -1;
            string t = (title ?? "").Trim();
            for (int i = 0; i < items.Count; i++)
            {
                MenuItem it = items[i];
                if (it == null) continue;
                if (it.Level != level) continue;
                if (string.Equals((it.Title ?? "").Trim(), t, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        /// <summary>
        /// 新增条目插入点 = **父项子树末尾**。
        ///
        /// ★ "顶层"是**相对口径**：本项目的 menus.conf 里顶级条目写在 2 空格缩进下 ⇒
        ///   Level = 1（见 `MenuConfWriter` 的 Level*2+1 写回），所以不能把"必须有 lv-1 父项"
        ///   当成绝对规则。取该分组**首条目的 Level** 作基准（baseLevel）：
        ///     · Level == baseLevel ⇒ 顶层条目，插到分组末尾；
        ///     · Level &gt;  baseLevel ⇒ 找它之前最近的 lv-1 父项，插到该父项子树末尾；
        ///       找不到 ⇒ 跳级，整份丢弃；
        ///     · Level &lt;  baseLevel ⇒ 比该组顶层还浅，同样算破坏结构，整份丢弃。
        /// </summary>
        private static bool InsertIntoSubtree(List<MenuItem> items, MenuItem oi, out string why)
        {
            why = "";

            int baseLevel = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null) continue;
                baseLevel = items[i].Level;
                break;
            }

            if (oi.Level < baseLevel)
            {
                why = "覆盖层条目「" + oi.Title + "」是 lv" + oi.Level
                    + "，比该分组的顶层（lv" + baseLevel + "）还浅 —— 会插到既有条目之外";
                return false;
            }

            if (oi.Level == baseLevel) { items.Add(CopyItem(oi)); return true; }

            int parentIdx = -1;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                MenuItem it = items[i];
                if (it == null) continue;
                if (it.Level == oi.Level - 1) { parentIdx = i; break; }
                if (it.Level < oi.Level - 1) break;      // 已越过父层：不存在
            }
            if (parentIdx < 0)
            {
                why = "覆盖层条目「" + oi.Title + "」是 lv" + oi.Level
                    + "，但基配置里找不到 lv" + (oi.Level - 1) + " 的父条目（跳级）";
                return false;
            }
            if (items[parentIdx].Type != ActionType.Group)
            {
                why = "覆盖层条目「" + oi.Title + "」的父条目「" + items[parentIdx].Title
                    + "」不是分组标题（group）—— 有子项却不是 group 会破坏层级不变式";
                return false;
            }

            int at = parentIdx + 1;
            while (at < items.Count && items[at] != null && items[at].Level > oi.Level - 1) at++;
            items.Insert(at, CopyItem(oi));
            return true;
        }

        /// <summary>覆盖动作位：保留基条目的 标题 / Level / 行号（身份不变，只换行为）。</summary>
        private static void OverrideItem(MenuItem dst, MenuItem src)
        {
            dst.Hotkey = src.Hotkey ?? "";
            dst.Type = src.Type;
            dst.Flag = src.Flag;
            dst.Sub = src.Sub;
            dst.Target = src.Target ?? "";
        }

        private static MenuItem CopyItem(MenuItem src)
        {
            var it = new MenuItem
            {
                Title = src.Title ?? "",
                Hotkey = src.Hotkey ?? "",
                Type = src.Type,
                Flag = src.Flag,
                Sub = src.Sub,
                Target = src.Target ?? "",
                Level = src.Level,
                Line = 0,                    // 覆盖层条目不对应基文件任何一行
                IconSource = ""              // 图标在渲染期重新解析
            };
            return it;
        }

        private static List<MenuGroup> CloneGroups(List<MenuGroup> groups)
        {
            var list = new List<MenuGroup>(groups == null ? 0 : groups.Count);
            if (groups == null) return list;
            foreach (MenuGroup g in groups)
            {
                if (g == null) { list.Add(null); continue; }
                var ng = new MenuGroup
                {
                    Name = g.Name,
                    Type = g.Type,
                    Keywords = new List<string>(g.Keywords ?? new List<string>()),
                    Line = g.Line
                };
                foreach (MenuItem it in g.Items ?? new List<MenuItem>())
                    ng.Items.Add(it == null ? null : it.Clone());
                list.Add(ng);
            }
            return list;
        }

        private static void Note(Action<string> log, string msg)
        {
            if (log == null) return;
            try { log(msg); } catch { }
        }

        private static void Warn(Action<string> log, string msg)
        {
            Note(log, "⚠ " + msg);
        }
    }
}
