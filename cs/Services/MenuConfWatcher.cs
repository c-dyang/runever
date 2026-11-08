using System;
using System.Collections.Generic;
using System.IO;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 段 1.7：`menus.conf` 的**版本戳检查点**（热键路径专用）。
    ///
    /// 设计（对照任务表 §0「关键设计决定」+ §2 难点②）：
    ///   · 判据与 `AutoMatcher.RefreshConfIfChanged`（`:212-236`）**逐字同口径**：
    ///     长度 + `LastWriteTimeUtc.Ticks`（**不做哈希** —— 哈希必须读全文件，破 I-5）；
    ///   · 未变 ⇒ **0 次解析、0 次读盘**（只付一次 `FileInfo` 构造 + 两个属性，µs 级）；
    ///   · 变了 ⇒ `new ConfParser` + `Parse()`（读一个文本文件 + 建对象图），**不扫插件、
    ///     不重建 AutoMatcher / IconResolver / Executor、不碰 PathCache**；
    ///   · **整体换代**：解析成功才把新实例交出去（`out fresh`），调用方 `_parser = fresh`。
    ///     禁止就地 `Groups.Clear()/Add()` —— 那会让持旧引用的链看到"半成品列表"。
    ///
    /// ★ 失败一律**保留旧表**（难点③，与 `auto.conf` "回落默认值"是**不同的取舍**）：
    ///   `menus.conf` 没有默认值语义 —— 清空 = 菜单变空 = 用户看到"菜单没了"，
    ///   比"还是旧菜单"体验差得多。故下列**三种**情况 ⇒ 返回 false、不切换、不清空：
    ///     ① **文件暂缺**（写盘替换窗口 / 被删）；
    ///     ② **`Parse()` 抛异常**（半截截断到分组行 / 未知分组类型 / 语法错）；
    ///     ③ **`Parse()` 成功但解出 0 组** —— 判据 `Groups.Count == 0`：
    ///        · `len > 0` ⇒ **内容退化**（典型：被截断到只剩注释行。它**不抛异常**，
    ///          若不拦就会把菜单换成空表，正是本设计要避免的"菜单没了"）；
    ///        · `len == 0` ⇒ **0 字节文件**（"写盘被打断"的典型形态）。同样保留旧表：
    ///          本设计要避免的结果是"菜单没了"，0 字节放行恰好产生该结果；用户真要清空菜单
    ///          走显式通道（见下方边界），热键路径不替用户执行清空。
    ///        · 全空白文件（`len > 0` 但 trim 后为空）走 `len > 0` 分支 ⇒ **同样保留旧表**。
    ///        判据只用两个**已在函数内捕获的廉价量**：`Groups.Count`（已建好的对象图属性）
    ///        与 `len`（`:126` 那次 `FileInfo.Length`）—— **不额外读盘** ⇒ 不破 I-5。
    ///
    /// ★★ 边界（I-7 契约口径，勿混）——"保留旧表"**只覆盖热键路径的自动重载**：
    ///   **用户显式通道不受此守卫限制**。托盘「重载配置」（`TrayApp.ReloadConfig`
    ///   → `new RunEverApp(_root).Init()`，不经本类）与设置窗保存后的重读
    ///   （`RunEverApp.ReloadMenus`，`new ConfParser` + `Parse` 直读，不经本类）
    ///   都**尊重文件里的实际内容，含空表** —— 用户显式说"就用这份文件"时，
    ///   按文件呈现（哪怕它把菜单清空）才是正确语义。
    /// </summary>
    public class MenuConfWatcher
    {
        private readonly string _path;
        private readonly Action<string> _log;

        // 版本戳（-1 = 尚未建立基线）
        private long _stampTicks = -1;
        private long _stampLen = -1;

        private int _parseCount;
        private ConfParser _current;

        public MenuConfWatcher(string path, Action<string> log = null)
        {
            _path = path;
            _log = log;
        }

        public string ConfPath { get { return _path; } }

        /// <summary>累计"整份重解析 menus.conf"的次数（自检证据：未变时必须为 0 增量）。</summary>
        public int ParseCount { get { return _parseCount; } }

        /// <summary>最后一次成功解析的实例（失败路径下**保持不动**）。</summary>
        public ConfParser Current { get { return _current; } }

        /// <summary>当前版本戳是否已建立（自检用：证明 stat 真的执行过）。</summary>
        public bool HasStamp { get { return _stampTicks != -1 || _stampLen != -1; } }

        public long StampTicks { get { return _stampTicks; } }
        public long StampLen { get { return _stampLen; } }

        private void L(string s)
        {
            if (_log != null) _log(s);
        }

        /// <summary>
        /// 把"本进程刚解析成功的那一份"登记为基线（同步版本戳）。
        /// 调用点：`RunEverApp.Init`（启动解析后）与 `RunEverApp.ReloadMenus`（保存后重读）。
        /// 不登记 ⇒ 下一次热键会白解析一次（正确但浪费）。
        /// </summary>
        public void AcceptBaseline(ConfParser current)
        {
            _current = current;
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists)
                {
                    _stampTicks = fi.LastWriteTimeUtc.Ticks;
                    _stampLen = fi.Length;
                }
                else
                {
                    _stampTicks = -1;
                    _stampLen = -1;
                }
            }
            catch (Exception ex)
            {
                // 戳建不起来 ⇒ 下一次热键会重解析一次（正确性不受影响，只多付一次 Parse）
                L("menus.conf 版本戳建立失败（下次热键会重解析一次）: " + ex.Message);
            }
        }

        /// <summary>
        /// 变了才重解析；返回 true 时 `fresh` = **全新实例**（调用方必须整体换引用）。
        /// 返回 false = 未变 / 读不到 / 解析失败 / 解析成功但解出 0 组（内容退化或 0 字节文件）
        /// —— 四种情况都**不切换**当前表（详见类注释的不变量）。
        /// </summary>
        public bool TryReloadIfChanged(out ConfParser fresh)
        {
            fresh = null;
            try
            {
                var fi = new FileInfo(_path);
                if (!fi.Exists)
                {
                    // ★ 难点③：写盘竞态（tmp → delete → move 的"文件暂缺"窗口）或文件被删。
                    //   menus.conf 无默认值语义 ⇒ 保留旧表（不切换、不清空、不抛）。
                    if (_stampTicks == -1 && _stampLen == -1) return false;
                    _stampTicks = -1;
                    _stampLen = -1;
                    L("menus.conf 暂缺（写盘替换窗口内 / 被删）⇒ 保留上一份分组表，不切换、不清空");
                    return false;
                }

                long ticks = fi.LastWriteTimeUtc.Ticks;
                long len = fi.Length;
                if (ticks == _stampTicks && len == _stampLen) return false;   // 快路径：0 解析、0 读盘

                // ★ 先落戳、再解析：若 Parse 抛（半截 / 语法错），后续热键不会对**同一份坏文件**
                //   反复读全量（I-5）；文件被修好时 mtime/长度必再变 ⇒ 自动自愈。
                _stampTicks = ticks;
                _stampLen = len;

                var p = new ConfParser(_path);
                p.Parse();
                _parseCount++;

                // ★ 守卫（G-2）："解析成功" ≠ "文件是好的"。
                //   截断到只剩注释 ⇒ ConfParser 不抛、Groups.Count==0，若无此守卫会**整体换成空表**
                //   ⇒ 用户下一轮热键看到"菜单没了"（正是难点③ 要避免的结果）。
                //   判据 = 已建好的对象图属性 `Groups.Count` + :126 那次 FileInfo 已捕到的 `len`
                //   ⇒ **本分支零额外读盘**（I-5）；0/全空白文件属"写盘被打断"形态，一并保留旧表。
                //   保守取舍：判为"语义退化"（len>0）或"文件被清空/写盘中断"（len==0），**保留旧表**。
                if (p.Groups.Count == 0)
                {
                    L("menus.conf 解析成功但**语义退化**（0 分组，len=" + len
                      + (len > 0 ? "：文件非空却解不出任何分组" : "：0 字节文件")
                      + "）⇒ 保留上一份分组表（不切换、不清空）");
                    return false;
                }

                _current = p;      // 成功才更新"最后一份好表"（失败路径下保持不动 ⇒ 自检可断言引用相等）
                L("menus.conf 已变 ⇒ 重解析成功（分组数=" + p.Groups.Count + "），准备整体换代");
                fresh = p;
                return true;
            }
            catch (Exception ex)
            {
                L("menus.conf 重解析失败（保留旧分组表，不清空）: " + ex.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// 热键路径"缺件注入 + 判定"的产物（含**本次流程用了哪一份快照**的证据字段）。
    /// </summary>
    public class HotPathDecision
    {
        /// <summary>本次流程的快照（= 调用方传进来的那一份，RunEverApp 的 A1/A2/A3 共用它）。</summary>
        public IList<MenuGroup> Snapshot;

        /// <summary>证据：实际喂给 `PathResolvePolicy.InjectEntryResolved` 的实例（A1）。</summary>
        public IList<MenuGroup> InjectedWith;

        /// <summary>证据：实际喂给 `MatchEngine.Decide` 的实例（A3）。</summary>
        public IList<MenuGroup> DecidedWith;

        public MenuDecision Decision;
        public bool HideMissing;

        /// <summary>`ctx.MissingItemIndex.Count`（与快照同源的下标集）。</summary>
        public int HiddenCount;

        /// <summary>快照里的展平条目数（与下标口径同一个遍历序）。</summary>
        public int ItemTotal;
    }

    /// <summary>
    /// 段 1.7：热键路径「缺件注入（A1）→ 判定（A3）」的**唯一收口**。
    ///
    /// 为什么收成一个方法：任务表难点①的核心是 A1/A3 必须拿到**同一实例、同一次快照**
    /// （下标是"相对传入列表"的相对量，跨列表即错位且不抛异常，表现为随机隐藏错条目 +
    ///  `MenuBuilder.IdMap` 派发出"不在当前菜单上"的条目）。收成单一入参后，
    /// **编译期就不可能分叉**（两处都只能用它）；`InjectedWith/DecidedWith` 是给自检留的
    /// 行为级证据（H2 断言引用相等）。
    /// </summary>
    public static class MenuDecisionPipeline
    {
        public static HotPathDecision InjectAndDecide(MatchContext ctx, IList<MenuGroup> snapshot,
            PathCache cache, bool hideMissing, Action<string> log)
        {
            PathResolvePolicy.InjectEntryResolved(ctx, snapshot, cache, hideMissing, log);

            var r = new HotPathDecision();
            r.Snapshot = snapshot;
            r.InjectedWith = snapshot;
            r.HideMissing = hideMissing;
            r.HiddenCount = (ctx == null || ctx.MissingItemIndex == null) ? 0 : ctx.MissingItemIndex.Count;
            r.ItemTotal = CountItems(snapshot);

            // ★ 同一快照 → 判定（A3）。下标口径 = 分组序 → 组内条目序（与上面注入完全一致）。
            r.Decision = MatchEngine.Decide(ctx, snapshot);
            r.DecidedWith = snapshot;
            return r;
        }

        private static int CountItems(IList<MenuGroup> groups)
        {
            int n = 0;
            if (groups != null)
                foreach (var g in groups)
                    if (g != null && g.Items != null) n += g.Items.Count;
            return n;
        }
    }
}
