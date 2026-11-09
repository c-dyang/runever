using System;
using System.Collections.Generic;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 菜单树的结构算法（**纯函数**）：层级 ↔ 树、增删移缩、子树克隆、层级校验。
    /// 零 WPF / 零 IO / 零确认框 —— 入参就是 `List&lt;MenuItem&gt;` + 显式参数，出参是结果。
    ///
    /// ★ 为什么单独一层：条目树的**数据变换**（靠 `Level` 表达父子）与**视图交互**
    ///   （TreeView 重建 / 折叠账本 / 右键菜单 / 确认框 / 字段面板）本来是焊在
    ///   `Views\MenuEditorPage.cs` 里的两族东西，页面因此涨到近 4000 行、成了唯一热点。
    ///   这里只收第一族：算法可无人值守驱动、可单测、可被 **保存闸** 与 **自检** 共用。
    ///
    /// 扁平模型的唯一约定（与 `menus.conf` 一致，别在这里发明新格式）：
    ///   · 一条 = 一个 `MenuItem`，`Level` = 深度；**父条目之后必须紧跟其全部子条目**；
    ///   · **不许跳级**（后一条 Level 最多比前一条大 1）—— 见 <see cref="LevelsWellFormed"/>；
    ///   · 层级基准（最外层 Level）由调用方给（`menus.conf` 惯例是 1，但配置里也可能是 0）：
    ///     本类**从不假设** 1，所有纵向位移都是「相对量」（±1）或「与基准取齐」。
    ///
    /// 所有方法都**就地改传入的列表**（除了查询类），并且只在列表末尾/中间做整块搬移，
    /// 不新建中间集合 —— 树/字段面板都按**对象身份**记账，换对象就会丢选中（踩过）。
    /// </summary>
    internal static class MenuTreeOps
    {
        /// <summary>
        /// 升降级的三种结局。<see cref="Moved"/> = 已就位；
        /// 另两个是**拒绝原因**（文案留给页面：这里只说事实，不弹窗、不写状态行）。
        /// </summary>
        internal enum IndentOutcome
        {
            /// <summary>已按 delta 升降级。</summary>
            Moved,
            /// <summary>⇥ 被拒：它是同级第一条，没有可挂靠的上一条。</summary>
            NoPrevSibling,
            /// <summary>⇤ 被拒：它已经在本组最外层（没有父项可退出）。</summary>
            AtTopLevel
        }

        // ============================================================
        // 一、层级基准 / 树 → 扁平
        // ============================================================

        /// <summary>
        /// 本组「最外层」的 Level（= 组内最小 Level；空组按 0）。
        /// 为什么不能硬编码 1：`menus.conf` 里既有 base=1 也有 base=0 的配置，
        /// 写死 1 会让结构操作把整份文件的层级整体挪一位（保存后 diff 波及每一行）。
        /// 负数一律收敛到 0（坏配置的自保，别把 -1 当基准继续算）。
        /// </summary>
        internal static int BaseLevelOf(List<MenuItem> items)
        {
            int baseLevel = 0;
            if (items != null && items.Count > 0)
            {
                baseLevel = items[0].Level;
                foreach (var it in items) if (it.Level < baseLevel) baseLevel = it.Level;
                if (baseLevel < 0) baseLevel = 0;
            }
            return baseLevel;
        }

        /// <summary>
        /// 分组「最外层」的 Level（= 该组既有的层级约定；空分组按 menus.conf 惯例取 1）。
        /// 与 <see cref="BaseLevelOf"/> 的差别：这里**不收敛**，只做「有没有」的判定 ——
        /// 跨组搬运要沿用目标组既有约定，用 0 会把搬来的条目整体压深一层。
        /// </summary>
        internal static int GroupBaseLevel(MenuGroup g)
        {
            if (g == null || g.Items == null || g.Items.Count == 0) return 1;
            int min = int.MaxValue;
            foreach (var it in g.Items)
                if (it != null && it.Level < min) min = it.Level;
            if (min == int.MaxValue) return 1;
            return min < 0 ? 1 : min;
        }

        /// <summary>
        /// 树 → 扁平的**递归体**：DFS 顺序 + `Level = 基准 + 深度`。
        /// 层级唯一的源是树 ⇒ 回写结果天然满足「父项后紧跟其全部子项 + 不跳级」。
        ///
        /// ★ 用泛型 + 两个取值委托，是为了**不认视图类型**：页面传 `NodeRow`，
        ///   自检/其它调用方可以传任何自带 `Item` 与子集合的包装（本层不认识 WPF）。
        /// </summary>
        internal static void FlattenTree<T>(
            IEnumerable<T> nodes,
            Func<T, MenuItem> itemOf,
            Func<T, IEnumerable<T>> childrenOf,
            int depth,
            int baseLevel,
            List<MenuItem> flat)
        {
            if (nodes == null || itemOf == null || flat == null) return;
            foreach (var n in nodes)
            {
                var it = itemOf(n);
                if (it == null) continue;
                it.Level = baseLevel + depth;
                flat.Add(it);
                if (childrenOf != null) FlattenTree(childrenOf(n), itemOf, childrenOf, depth + 1, baseLevel, flat);
            }
        }

        /// <summary>
        /// 层级不变式（**强制**）。运行期 `MatchEngine` / `MissingFilter` 用
        /// `while (j &lt; n &amp;&amp; src[j].Level &gt; it.Level) j++` —— 即**按"紧随其后"**扫描子条目。
        /// 因此只允许"逐级加深"：出现**跳级**（前一条 lv1、后一条 lv3）会让父子关系错乱、
        /// 子菜单建错。判据 = 除首条外，`Level ≤ 前一条.Level + 1`。
        ///
        /// ★ 唯一实现：保存闸（`MenuConfWriter.Validate` 之外的最后一道）与
        ///   `--selftest-menueditor` 都走这里 —— 判据一个字都不许改。
        /// </summary>
        internal static bool LevelsWellFormed(List<MenuItem> items, out string why)
        {
            why = "";
            for (int i = 1; i < items.Count; i++)
            {
                if (items[i].Level > items[i - 1].Level + 1)
                {
                    why = "第 " + (i + 1) + " 条跳级（前一条 lv" + items[i - 1].Level
                        + " → lv" + items[i].Level + "）";
                    return false;
                }
            }
            return true;
        }

        // ============================================================
        // 二、位置解析（全族结构操作共用的几何）
        // ============================================================

        /// <summary>某条目**整棵子树**的最后一个下标（含自身；越界/找不到 = 末位）。</summary>
        internal static int LastDescendantIndex(List<MenuItem> items, int index)
        {
            if (items == null || items.Count == 0) return -1;
            if (index < 0 || index >= items.Count) return items.Count - 1;

            int lv = items[index].Level;
            int j = index + 1;
            while (j < items.Count && items[j].Level > lv) j++;
            return j - 1;
        }

        /// <summary>子树条目数（含自身）。删除条数、确认框文案都按它算；下标非法 = 0。</summary>
        internal static int CountSubtree(List<MenuItem> items, int index)
        {
            if (items == null || index < 0 || index >= items.Count) return 0;
            int end = LastDescendantIndex(items, index);
            if (end < index) return 0;
            return end - index + 1;
        }

        /// <summary>直接子项个数（= 子树里 Level 恰好为「自身 + 1」的条目数）。</summary>
        internal static int DirectChildCount(List<MenuItem> items, int index)
        {
            if (items == null || index < 0 || index >= items.Count) return 0;
            int end = LastDescendantIndex(items, index);
            int childLevel = items[index].Level + 1;
            int c = 0;
            for (int i = index + 1; i <= end; i++) if (items[i].Level == childLevel) c++;
            return c;
        }

        /// <summary>父项下标（= 往前最近的 Level-1 条目）；最外层返回 -1。</summary>
        internal static int ParentIndex(List<MenuItem> items, int index)
        {
            if (items == null || index < 0 || index >= items.Count) return -1;
            int lv = items[index].Level;
            for (int i = index - 1; i >= 0; i--)
            {
                if (items[i].Level == lv - 1) return i;
                if (items[i].Level < lv - 1) return -1;      // 已经是本层第一条
            }
            return -1;
        }

        /// <summary>上一条**同层**条目的下标（= 前一个兄弟的子树起点）；没有返回 -1。</summary>
        internal static int PrevSiblingIndex(List<MenuItem> items, int index)
        {
            if (items == null || index < 0 || index >= items.Count) return -1;
            int lv = items[index].Level;
            for (int i = index - 1; i >= 0; i--)
            {
                if (items[i].Level == lv) return i;
                if (items[i].Level < lv) return -1;          // 走到了父项 ⇒ 上面没有兄弟
            }
            return -1;
        }

        /// <summary>下一条**同层**条目的下标（= 本子树之后的下一个兄弟起点）；没有返回 -1。</summary>
        internal static int NextSiblingIndex(List<MenuItem> items, int index)
        {
            if (items == null || index < 0 || index >= items.Count) return -1;
            int q = LastDescendantIndex(items, index) + 1;
            if (q >= items.Count || items[q].Level != items[index].Level) return -1;
            return q;
        }

        /// <summary>它在同层里排第几（0 基）；状态行的「同层第 N 位」= 本值 + 1。</summary>
        internal static int SiblingOrdinal(List<MenuItem> items, int index)
        {
            if (items == null || index < 0 || index >= items.Count) return -1;
            int lv = items[index].Level;
            int n = 0;
            for (int i = 0; i <= index; i++) if (items[i].Level == lv) n++;
            return n - 1;
        }

        // ============================================================
        // 三、增 / 删 / 复制 / 移 / 升降级
        //   全部「整棵子树一起动」：搬父条目时子条目自然跟随，不留孤儿、不留空壳。
        // ============================================================

        /// <summary>新条目的出厂值（「新条目」/ run notepad.exe）；`Level` 由调用方按落点定。</summary>
        internal static MenuItem NewItem()
        {
            return new MenuItem
            {
                Title = "新条目",
                Type = ActionType.Run,
                Target = "notepad.exe",
                Level = 0
            };
        }

        /// <summary>
        /// 新增一条（标题「新条目」/ run notepad.exe，与菜单惯例一致），并返回它。
        /// <para><paramref name="selIndex"/> = 当前选中条目的下标（-1 = 没有选中）：</para>
        /// · 没有选中 ⇒ 追加到**根层末尾**（Level = 基准）；
        /// · 选中的是组头 ⇒ 成为它的**子项**（Level + 1，落在子树末尾）；
        /// · 选中的是普通条目 ⇒ 成为它的**下一个兄弟**（同 Level，落在子树之后）。
        /// </summary>
        internal static MenuItem AddChild(List<MenuItem> items, int selIndex, int baseLevel)
        {
            var it = NewItem();

            int at;
            int lv;
            bool noSel = (items == null) || selIndex < 0 || selIndex >= items.Count;
            if (noSel)
            {
                if (items == null) return null;
                at = items.Count;
                lv = baseLevel;
            }
            else
            {
                at = LastDescendantIndex(items, selIndex) + 1;
                lv = items[selIndex].Type == ActionType.Group
                    ? items[selIndex].Level + 1
                    : items[selIndex].Level;
            }

            it.Level = lv;
            items.Insert(at, it);
            return it;
        }

        /// <summary>
        /// 删除条目 + 其**整棵子树**，返回删掉的条数。
        /// <paramref name="next"/> = 删完该选谁（同层后继 → 否则父项 → 都没有 = null），
        /// 与 "先算好再删" 是同一份判据（顺序换一下就选到别人身上了）。
        /// </summary>
        internal static int DeleteSubtree(List<MenuItem> items, int index, out MenuItem next)
        {
            next = null;
            if (items == null || index < 0 || index >= items.Count) return 0;

            int lv = items[index].Level;
            int count = CountSubtree(items, index);
            items.RemoveRange(index, count);

            if (index < items.Count && items[index].Level == lv)
            {
                next = items[index];                       // 同层后继
                return count;
            }

            int pi = ParentIndex(items, index);             // 没有后继 ⇒ 退回父项
            if (pi >= 0) next = items[pi];
            return count;
        }

        /// <summary>
        /// 复制条目 + 整棵子树，插到源条目**之后**（同层），返回副本的根条目。
        /// 语义（别改，`--selftest-menueditor` 与产品手感都按它）：
        /// · 副本是**新对象**（新身份 / 新 ID，深拷贝）；
        /// · 根标题加「副本」后缀，子项标题原样；
        /// · **快捷键一律清空**（同一个键挂两条，运行时只会命中一条，用户看不出哪条生效）。
        /// <paramref name="directChildren"/> = 副本的直接子项数（状态行文案用）。
        /// </summary>
        internal static MenuItem CloneSubtree(List<MenuItem> items, int index, out int directChildren)
        {
            directChildren = 0;
            if (items == null || index < 0 || index >= items.Count) return null;

            int end = LastDescendantIndex(items, index);
            int lv = items[index].Level;
            var copies = new List<MenuItem>(end - index + 1);
            for (int i = index; i <= end; i++)
            {
                var c = items[i].Clone();
                c.Hotkey = "";
                copies.Add(c);
                if (c.Level == lv + 1) directChildren++;
            }
            copies[0].Title = items[index].Title + " 副本";
            items.InsertRange(end + 1, copies);
            return copies[0];
        }

        /// <summary>
        /// 同层上移（dir &lt; 0）/ 下移（dir &gt; 0），**整棵子树**一起动。
        /// 返回 false = 已经在同层首/末位（调用方什么都不做，也不弹提示 —— 与原行为一致）。
        /// <paramref name="siblingPos"/> = 移动后的同层位次（1 基，状态行用）；
        /// <paramref name="directChildren"/> = 直接子项数（状态行「连同 N 个子项」用）。
        /// </summary>
        internal static bool Move(
            List<MenuItem> items, int index, int dir,
            out int siblingPos, out int directChildren)
        {
            siblingPos = -1;
            directChildren = 0;
            if (items == null || index < 0 || index >= items.Count || dir == 0) return false;

            int blockLen = CountSubtree(items, index);
            directChildren = DirectChildCount(items, index);
            int newIndex;                       // 移动后的扁平下标（只在本方法内用）

            if (dir < 0)
            {
                int p = PrevSiblingIndex(items, index);
                if (p < 0) return false;
                var block = items.GetRange(index, blockLen);
                items.RemoveRange(index, blockLen);
                items.InsertRange(p, block);
                newIndex = p;
            }
            else
            {
                int q = NextSiblingIndex(items, index);
                if (q < 0) return false;
                int qEnd = LastDescendantIndex(items, q);
                var block = items.GetRange(index, blockLen);
                items.RemoveRange(index, blockLen);
                newIndex = qEnd + 1 - blockLen;      // 摘掉前面的块 ⇒ 后继整体上移 blockLen
                items.InsertRange(newIndex, block);
            }

            siblingPos = SiblingOrdinal(items, newIndex) + 1;
            return true;
        }

        /// <summary>
        /// 升降级（<paramref name="delta"/> = +1 缩进 / -1 反缩进），整棵子树相对位移。
        /// · ⇥ = 成为**上一条同层条目**的子项（落在它子项末尾；位置不变，层级 +1）；
        /// · ⇤ = 升到**父项的同层后继位置**（紧跟父项那一条之后；层级 -1）。
        /// <paramref name="anchor"/> = 挂靠/退出所依据的那一条（⇥ 是上一条同层，⇤ 是父项），
        /// 供调用方写「已成为「X」的子项」这类文案与展开折叠。
        /// </summary>
        internal static IndentOutcome Indent(
            List<MenuItem> items, int index, int delta, out MenuItem anchor)
        {
            anchor = null;
            if (items == null || index < 0 || index >= items.Count || delta == 0) return IndentOutcome.Moved;

            int blockLen = CountSubtree(items, index);

            if (delta > 0)
            {
                int p = PrevSiblingIndex(items, index);
                if (p < 0) return IndentOutcome.NoPrevSibling;

                var block = items.GetRange(index, blockLen);
                items.RemoveRange(index, blockLen);
                int prevEnd = LastDescendantIndex(items, p);
                for (int i = 0; i < block.Count; i++) block[i].Level += 1;
                items.InsertRange(prevEnd + 1, block);
                anchor = items[p];
                return IndentOutcome.Moved;
            }
            else
            {
                int pi = ParentIndex(items, index);
                if (pi < 0) return IndentOutcome.AtTopLevel;

                var block = items.GetRange(index, blockLen);
                items.RemoveRange(index, blockLen);
                for (int i = 0; i < block.Count; i++) block[i].Level -= 1;
                items.InsertRange(pi + 1, block);
                anchor = items[pi];
                return IndentOutcome.Moved;
            }
        }

        // ============================================================
        // 四、层级快照（跨组搬运失败回滚用）
        //   ★ Level 必须一起快照：搬动时会按目标分组重算 Level（对象上的字段被改了），
        //     只还原列表成员不还原 Level ⇒ "回滚"后层级还是错的（踩过）。
        // ============================================================

        /// <summary>快照一组条目的 Level（与调用方的列表快照配对使用）。</summary>
        internal static List<int> SnapshotLevels(List<MenuItem> items)
        {
            var levels = new List<int>(items.Count);
            foreach (var it in items) levels.Add(it == null ? 0 : it.Level);
            return levels;
        }

        /// <summary>按快照还原 Level（<paramref name="items"/> 与 <paramref name="levels"/> 一一对应）。</summary>
        internal static void RestoreLevels(List<MenuItem> items, List<int> levels)
        {
            int n = Math.Min(items.Count, levels.Count);
            for (int i = 0; i < n; i++)
                if (items[i] != null) items[i].Level = levels[i];
        }

        // ============================================================
        // 五、跨组搬运（移动 / 复制）+ 选择集解析
        //   ★ C1 闸的长度：搬完立刻跑 MenuStructureValidator，比操作前**多出**结构问题
        //     ⇒ 两个分组整体回滚（连 Level 一起还原），一个字节都不写。
        // ============================================================

        /// <summary>跨组搬运的结局。**Code** = OK / REJECT / SKIP；页面只据此写状态行，不解析 Detail。</summary>
        internal sealed class MoveGroupOutcome
        {
            /// <summary>OK / REJECT / SKIP。</summary>
            public string Code = "SKIP";
            /// <summary>SKIP/REJECT 的原因；REJECT 时是 <see cref="MenuStructureValidator.Summarize"/> 的原文（含换行）。</summary>
            public string Detail = "";
            /// <summary>搬动的条目总数（含子项）。</summary>
            public int Count;
            /// <summary>搬动的子树棵数（= 选中顶层条目数）。</summary>
            public int Blocks;
            /// <summary>REJECT 时的结构问题条数（操作后）。</summary>
            public int Issues;
        }

        /// <summary>
        /// 把 <paramref name="src"/> 里 <paramref name="rootIndices"/> 指到的若干**顶层子树**
        /// 移动（<paramref name="copy"/>=false）或复制（=true）到 <paramref name="dst"/>。
        ///
        /// 语义：
        ///   · 整棵子树一起走：父条目搬走 ⇒ 全部子条目跟随，源分组不留孤儿 / 不留空壳；
        ///   · 落到目标后 **Level 重算**：顶层 = 目标组既有最外层 Level（空组按 menus.conf 惯例取 1）；
        ///   · 复制 = **新对象**（新身份 / 新 ID），标题原样（不自动加"副本"）；
        ///   · 之后跑校验闸：比操作前多出结构问题 ⇒ 两组整体回滚（列表 + Level）。
        ///
        /// <paramref name="rootIndices"/> 必须已「父子都选中只留最上层」且升序（见
        /// <see cref="SelectionRootIndices"/>）；本方法不做二次去重。
        /// </summary>
        internal static MoveGroupOutcome MoveToGroup(
            List<MenuGroup> groups, MenuGroup src, MenuGroup dst, List<int> rootIndices, bool copy)
        {
            var outcome = new MoveGroupOutcome();
            if (src == null || dst == null) { outcome.Detail = "没有源分组/目标分组"; return outcome; }
            if (ReferenceEquals(src, dst)) { outcome.Detail = "目标分组 = 当前分组"; return outcome; }
            if (rootIndices == null || rootIndices.Count == 0) { outcome.Detail = "没有选中条目"; return outcome; }

            // ---- 块 = 每个顶层条目 + 它整棵子树（在**未搬动**的列表上算，从后往前摘才不乱）----
            var blocks = new List<int[]>();
            int total = 0;
            int lastEnd = -1;
            foreach (var i in rootIndices)
            {
                if (i < 0 || i >= src.Items.Count || i <= lastEnd) continue;
                int end = LastDescendantIndex(src.Items, i);
                blocks.Add(new[] { i, end });
                total += end - i + 1;
                lastEnd = end;
            }
            if (blocks.Count == 0) { outcome.Detail = "没有选中条目"; return outcome; }
            outcome.Count = total;
            outcome.Blocks = blocks.Count;

            // ---- 快照（回滚用；引用级快照 ⇒ 回滚后对象身份不变，树/字段面板都还认得）----
            var srcSnap = new List<MenuItem>(src.Items);
            var srcLevels = SnapshotLevels(src.Items);
            var dstSnap = new List<MenuItem>(dst.Items);
            var dstLevels = SnapshotLevels(dst.Items);
            int issuesBefore = MenuConfWriter.Validate(groups).Count;

            int baseLv = GroupBaseLevel(dst);

            if (!copy)
            {
                // 先按 DFS 顺序收集（目标组要按这个顺序落），再从后往前摘
                var moving = new List<MenuItem>(total);
                var movingLevels = new List<int>(total);
                foreach (var b in blocks)
                {
                    int s = b[0], e = b[1];
                    int rootLevel = src.Items[s].Level;
                    for (int i = s; i <= e; i++)
                    {
                        moving.Add(src.Items[i]);
                        movingLevels.Add(baseLv + (src.Items[i].Level - rootLevel));
                    }
                }
                for (int b = blocks.Count - 1; b >= 0; b--)
                    src.Items.RemoveRange(blocks[b][0], blocks[b][1] - blocks[b][0] + 1);

                for (int i = 0; i < moving.Count; i++)
                {
                    moving[i].Level = movingLevels[i];
                    dst.Items.Add(moving[i]);
                }
            }
            else
            {
                foreach (var b in blocks)
                {
                    int s = b[0], e = b[1];
                    int rootLevel = src.Items[s].Level;
                    for (int i = s; i <= e; i++)
                    {
                        var it = src.Items[i].Clone();          // 复制 ⇒ 新对象 = 新身份（新 ID）
                        it.Level = baseLv + (src.Items[i].Level - rootLevel);
                        dst.Items.Add(it);
                    }
                }
            }

            // ---- ★ C1 闸：操作后立刻校验；多出结构问题 ⇒ 整体回滚（不写盘）----
            var after = MenuConfWriter.Validate(groups);
            if (after.Count > issuesBefore)
            {
                src.Items.Clear(); src.Items.AddRange(srcSnap); RestoreLevels(srcSnap, srcLevels);
                dst.Items.Clear(); dst.Items.AddRange(dstSnap); RestoreLevels(dstSnap, dstLevels);

                outcome.Code = "REJECT";
                outcome.Issues = after.Count;
                outcome.Detail = MenuStructureValidator.Summarize(after, 3);
                return outcome;
            }

            outcome.Code = "OK";
            return outcome;
        }

        /// <summary>
        /// 本次要搬动的**顶层**条目下标（升序 = DFS 顺序）= 标记集合 ∪ 主选中项，
        /// 父子都被选中时只保留祖先（搬父条目时子条目本来就会跟着走，重复搬会乱）。
        /// ★ 主选中项一定要算进来：TreeView 的键盘导航（↑↓ / 首字母）会绕过标记账本，
        ///   只信标记集合就会出现"看着选的是 B、搬走的却是 A"（静默搬错，最难查）。
        /// </summary>
        internal static List<int> SelectionRootIndices(
            List<MenuItem> items, ICollection<MenuItem> marked, MenuItem selected)
        {
            var res = new List<int>();
            if (items == null) return res;

            var set = new HashSet<MenuItem>();          // MenuItem 未重写 Equals ⇒ 引用相等，正合意
            for (int i = 0; i < items.Count; i++)
                if (marked != null && marked.Contains(items[i])) set.Add(items[i]);

            if (selected != null && !set.Contains(selected))
            {
                if (items.IndexOf(selected) >= 0) set.Add(selected);
            }
            if (set.Count == 0) return res;

            for (int i = 0; i < items.Count; i++)
            {
                if (!set.Contains(items[i])) continue;

                bool ancestorPicked = false;
                for (int p = i; ; )
                {
                    int pi = ParentIndex(items, p);
                    if (pi < 0) break;
                    if (set.Contains(items[pi])) { ancestorPicked = true; break; }
                    p = pi;
                }
                if (!ancestorPicked) res.Add(i);
            }
            return res;
        }

        // ============================================================
        // 六、纯模型检索 / 候选分组清单（无窗口环境也能跑）
        // ============================================================

        /// <summary>
        /// 定位式搜索的**纯模型层**：命中 标题 / 快捷键 / 目标（大小写不敏感，DFS = 扁平顺序）。
        /// 只回答"命中哪些"，选中与滚动是调用方的事。
        /// </summary>
        internal static List<MenuItem> Search(List<MenuItem> items, string q)
        {
            var hits = new List<MenuItem>();
            if (items == null || string.IsNullOrEmpty(q)) return hits;
            foreach (var it in items)
            {
                if (it == null) continue;
                if (ContainsNoCase(it.Title, q) || ContainsNoCase(it.Hotkey, q) || ContainsNoCase(it.Target, q))
                    hits.Add(it);
            }
            return hits;
        }

        private static bool ContainsNoCase(string s, string q)
        {
            return !string.IsNullOrEmpty(s) && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// 「移动到分组… / 复制到分组…」的候选清单（**排除当前分组**：组内调序用 ↑↓ / ⇥，
        /// 组内复制用「复制条目」）。labels = 给人看的「分组名 — 依据（N 项）」；indices = 对应下标。
        /// </summary>
        internal static List<string> PickerLabels(List<MenuGroup> groups, MenuGroup current, out List<int> indices)
        {
            indices = new List<int>();
            var labels = new List<string>();
            if (groups == null) return labels;

            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                if (g == null || ReferenceEquals(g, current)) continue;

                string basis = MenuGroup.TypeToString(g.Type);
                if ((g.Type == GroupType.Ext || g.Type == GroupType.Proc) && g.Keywords.Count > 0)
                    basis += ": " + string.Join(" ", g.Keywords.ToArray());
                labels.Add((g.Name ?? "").Trim() + "  —  " + basis + "（" + g.Items.Count + " 项）");
                indices.Add(i);
            }
            return labels;
        }
    }
}
