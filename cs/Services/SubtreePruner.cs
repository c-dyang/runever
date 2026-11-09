using System;
using System.Collections.Generic;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 空子树剪枝 —— 缺件过滤之后的收尾一刀。
    ///
    /// 语义（唯一口径）：**子树可见条目数 = 0 的节点，整棵子树一并丢弃**。
    ///   · 组标题（IsGroupHeader）没有可见子项 ⇒ 丢（含"本来就没有子项"的空壳）
    ///   · 带子项的普通条目，子项全被剪光 ⇒ 自己也不留（否则点了没内容）
    ///   · 任意嵌套层一致 —— 逐层递归，一次自底向上判定即到不动点（剪完不会再生出新空壳）
    ///
    /// 不变量（与卡片一致）：
    ///   · 只删**整棵子树**，保留节点的 Level 原样不动 ⇒ 父条目后仍紧跟其全部子条目，不产生层级断裂
    ///   · 纯函数、零 IO：只在传入的扁平条目列表上建临时层级树，不改动传入对象
    ///
    /// 统计口径（供日志 / 诊断页）：
    ///   Count  = 被剪掉的**空子树个数**（只数最外层被剪的节点，不重复数其子孙）
    ///   Titles = 这些被剪节点的标题（按出现顺序）
    /// </summary>
    public static class SubtreePruner
    {
        /// <summary>一次剪枝的统计（局部累计，调用方自行归并）。</summary>
        public class PruneStat
        {
            public int Count;
            public List<string> Titles = new List<string>();
        }

        /// <summary>最近一次判定/构建里累计剪掉的空子树数（探针，日志与诊断页同源）。</summary>
        public static int LastPrunedTotal;

        /// <summary>最近一次判定/构建里剪掉的空子树标题（日志用）。</summary>
        public static List<string> LastPrunedTitles = new List<string>();

        /// <summary>重置探针（每次弹菜单/每次自检的前置，避免跨轮累加）。</summary>
        public static void ResetLast()
        {
            LastPrunedTotal = 0;
            LastPrunedTitles = new List<string>();
        }

        /// <summary>把一次局部统计并入探针。</summary>
        public static void Accumulate(PruneStat stat)
        {
            if (stat == null || stat.Count == 0) return;
            LastPrunedTotal += stat.Count;
            if (stat.Titles != null) LastPrunedTitles.AddRange(stat.Titles);
        }

        /// <summary>
        /// 剪枝：keep = 逐条过滤后仍保留的条目（缺件叶条目不在其中；组标题通常都在）。
        /// 返回新列表；传入的 src / keep 不被修改。
        /// </summary>
        public static List<MenuItem> Prune(IList<MenuItem> src, HashSet<MenuItem> keep, PruneStat stat)
        {
            var outList = new List<MenuItem>();
            if (src == null || src.Count == 0) return outList;

            var roots = BuildTree(src);
            foreach (var r in roots) Mark(r, keep);
            foreach (var r in roots) Emit(r, outList, true, stat);
            return outList;
        }

        // ============================================================
        // 内部：临时层级树
        // ============================================================

        private sealed class Node
        {
            public MenuItem Item;
            public List<Node> Children = new List<Node>();
            public bool SelfKept;      // 自身没被缺件过滤丢掉
            public bool Kept;          // 自身 + 子树仍有可见内容
        }

        /// <summary>
        /// 扁平 Level 列表 → 树（按 Level 栈判定父子；与 MenuBuilder 的层级栈同一口径）。
        /// 跳级配置也能建出树：更深的层一律挂到最近的更浅节点下。
        /// </summary>
        private static List<Node> BuildTree(IList<MenuItem> items)
        {
            var roots = new List<Node>();
            var stack = new List<Node>();

            foreach (var it in items)
            {
                if (it == null) continue;
                var node = new Node { Item = it };

                while (stack.Count > 0 && stack[stack.Count - 1].Item.Level >= it.Level)
                    stack.RemoveAt(stack.Count - 1);

                if (stack.Count == 0) roots.Add(node);
                else stack[stack.Count - 1].Children.Add(node);

                stack.Add(node);
            }
            return roots;
        }

        /// <summary>
        /// 自底向上标记：叶子普通条目可见；组标题/带子项条目要看子项。
        /// 自身已被缺件过滤丢掉的节点 ⇒ 整棵子树都不保留（不留层级断裂）。
        /// </summary>
        private static bool Mark(Node n, HashSet<MenuItem> keep)
        {
            n.SelfKept = (keep == null) || keep.Contains(n.Item);

            bool anyChild = false;
            foreach (var c in n.Children)
                if (Mark(c, keep)) anyChild = true;

            if (!n.SelfKept) { n.Kept = false; return false; }

            if (n.Children.Count == 0)
                n.Kept = !n.Item.IsGroupHeader;      // 无子项的空壳组标题 ⇒ 丢
            else
                n.Kept = anyChild;                   // 子项全无 ⇒ 自己也丢

            return n.Kept;
        }

        /// <summary>
        /// 按原顺序摊平保留节点。丢节点时若其父仍在（= 这是一棵被剪空子树的最外层），
        /// 计入统计；子孙不再重复计数。
        /// </summary>
        private static void Emit(Node n, List<MenuItem> outList, bool parentKept, PruneStat stat)
        {
            if (!n.Kept)
            {
                // 只数"自身本来保留、却因子树为空被剪"的最外层节点；
                // 缺件叶条目由调用方自己统计，不在这里重复计数。
                if (parentKept && n.SelfKept && stat != null)
                {
                    stat.Count++;
                    stat.Titles.Add(n.Item.Title);
                }
                return;
            }

            outList.Add(n.Item);
            foreach (var c in n.Children) Emit(c, outList, true, stat);
        }
    }
}
