using System;
using System.Collections.Generic;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>配置违规种类（不变量 I-3/I-4；本段只建数据位，告警文案留第 2 段）。</summary>
    public enum ViolationKind
    {
        /// <summary>预设四类（text/folder/fallback/always）各至多一个 —— 出现多个即违规。</summary>
        DuplicateSingleton = 0,

        /// <summary>多个 proc 组含同一进程名（D3 应由设置层拦住，手改配置会漏）。</summary>
        DuplicateProcName
    }

    /// <summary>一条配置违规记录（运行时取"配置顺序第一个"，不静默丢弃、不弹异常）。</summary>
    public class Violation
    {
        public ViolationKind Kind;
        public string Detail = "";

        /// <summary>相关分组在配置里的行号（0 = 未知）。</summary>
        public int Line;

        public override string ToString()
        {
            return Kind + "(" + Detail + (Line > 0 ? " @line " + Line : "") + ")";
        }
    }

    /// <summary>
    /// 多命中消解钩子 —— 段 2（AC-2 的 I-4）在此挂策略；本段默认实现 = 取配置顺序第一个。
    /// </summary>
    public interface IMultiHitResolver
    {
        /// <summary>从同一槽位的多个命中组里选一个（slot = text/ext 等槽位名）。</summary>
        MenuGroup Choose(string slot, IList<MenuGroup> hits);
    }

    /// <summary>默认消解策略：取**配置顺序第一个**（契约 §5 I-4）。</summary>
    public class ConfigOrderResolver : IMultiHitResolver
    {
        public MenuGroup Choose(string slot, IList<MenuGroup> hits)
        {
            if (hits == null || hits.Count == 0) return null;
            return hits[0];
        }
    }

    /// <summary>
    /// 判定产物 —— 三段槽位 + 违规记录（**纯数据**，无 IO）。
    ///
    /// R9 顺序：动态（R1–R6）→ 进程（R7）→ 固定（R8），**堆叠不去重**。
    /// </summary>
    public class MenuDecision
    {
        /// <summary>动态槽位（R1–R6，**互斥**：至多一个组，见 I-1）。</summary>
        public List<MenuGroup> Dynamic = new List<MenuGroup>();

        /// <summary>进程槽位（R7，至多一个组；按 D3 校验本应唯一）。</summary>
        public List<MenuGroup> Process = new List<MenuGroup>();

        /// <summary>固定槽位（R8，恒显示，含功能区）。</summary>
        public List<MenuGroup> Always = new List<MenuGroup>();

        /// <summary>配置违规（本段只记录数据，不产出告警文案）。</summary>
        public List<Violation> Violations = new List<Violation>();

        /// <summary>命中的规则标识（R1–R9；日志与自检断言用）。</summary>
        public string Rule = "";

        /// <summary>动态槽位是否走了兜底（R3/R5/R6 或"该形态无对应组"）。</summary>
        public bool FallbackShown;

        public int DynamicCount { get { return Dynamic.Count; } }
        public int ProcessCount { get { return Process.Count; } }
        public int AlwaysCount { get { return Always.Count; } }
        public int FallbackCount { get { return FallbackShown ? Fallback.Count : 0; } }

        /// <summary>兜底槽位内容（判定期暂存；对外只通过 Dynamic 暴露）。</summary>
        internal List<MenuGroup> Fallback = new List<MenuGroup>();

        /// <summary>
        /// 合成后的显示顺序（**默认序**）：动态 → 进程 → 固定（R9，堆叠不去重）。
        /// ★ 返回的是**新列表**，元素仍是原 MenuGroup 引用（不改配置对象）。
        /// </summary>
        public List<MenuGroup> Flat()
        {
            return Flat(null);
        }

        /// <summary>
        /// 按 `auto.conf` 的 `menuOrder` 合成显示顺序（三段：dynamic / process / always）。
        ///
        /// ★ 动态与兜底**是同一格**（I-1）：命中兜底时兜底组就在 `Dynamic` 里
        ///   ⇒ `dynamic` 段原样输出 `Dynamic`，不需要（也不能）再按兜底单独排位。
        /// ★ `order` 为 null / 空 ⇒ 走默认序（= Flat() 的历史行为）。
        /// </summary>
        public List<MenuGroup> Flat(string[] order)
        {
            if (order == null || order.Length == 0) order = MenuOrderSpec.Default;

            var r = new List<MenuGroup>();
            foreach (string token in order)
            {
                switch (MenuOrderSpec.Normalize(token))
                {
                    case MenuOrderSpec.Dynamic:
                        r.AddRange(Dynamic);
                        break;
                    case MenuOrderSpec.Process:
                        r.AddRange(Process);
                        break;
                    case MenuOrderSpec.Always:
                        r.AddRange(Always);
                        break;
                }
            }
            return r;
        }

        /// <summary>条目总数（三段合计，仅统计实际显示的组）。</summary>
        public int ItemCount()
        {
            int n = 0;
            var flat = Flat();
            foreach (var g in flat)
                if (g != null && g.Items != null) n += g.Items.Count;
            return n;
        }

        public override string ToString()
        {
            return "rule=" + Rule
                + " dynamic=" + DynamicCount
                + " process=" + ProcessCount
                + " always=" + AlwaysCount
                + " fallback=" + (FallbackShown ? 1 : 0)
                + " violations=" + Violations.Count;
        }
    }
}
