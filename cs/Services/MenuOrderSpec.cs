using System;
using System.Collections.Generic;
using System.Text;

namespace RunEverCs.Services
{
    /// <summary>
    /// 菜单**三段**的显示顺序（auto.conf 的 `menuOrder` 键）。
    ///
    /// 三段 = 动态组（`dynamic`：选中文件/文本时命中的 ext·folder·text 组；**都没命中时由兜底组顶上同一格**）／
    ///        进程（`process`：按前台进程匹配的 proc 组）／固定（`always`：每次弹出都在）。
    /// ★ 兜底与动态**是同一格**（判定不变量 I-1：一次只命中其一，且都落在 `MenuDecision.Dynamic` 里）
    ///   ⇒ 顺序表里**不给兜底单列一格**（旧版四段口径已废弃：`fallback` 词仍可读，按 `dynamic` 处理）。
    /// 容错口径：未知词忽略、重复词只留第一个、表里缺的段按 <see cref="Default"/> 补到末尾
    ///   ⇒ 手改配置写错不会把某一段吞掉（最坏情况退回默认顺序）。
    /// </summary>
    public static class MenuOrderSpec
    {
        public const string Dynamic = "dynamic";
        public const string Process = "process";
        public const string Always = "always";

        /// <summary>旧口径的第四段词：读到时并入 `dynamic`（同一格），写回时不再出现。</summary>
        public const string LegacyFallback = "fallback";

        /// <summary>默认顺序 = 未配置 `menuOrder` 时的历史行为（动态 → 进程 → 固定）。</summary>
        public static readonly string[] Default = { Dynamic, Process, Always };

        /// <summary>解析配置串为**三段的完整排列**（去重 / 去未知 / `fallback` 并入 dynamic / 缺的补末尾）。</summary>
        public static string[] Parse(string raw)
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(raw))
            {
                string[] parts = raw.Split(new[] { ',', ';', '|', '/', ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (string p in parts)
                {
                    string t = Normalize(p);
                    if (t.Length == 0 || list.Contains(t)) continue;
                    list.Add(t);
                }
            }
            foreach (string t in Default)
                if (!list.Contains(t)) list.Add(t);
            return list.ToArray();
        }

        /// <summary>把单个词归一成三段之一；不认识返回空串。</summary>
        public static string Normalize(string token)
        {
            string t = (token ?? "").Trim().ToLowerInvariant();
            switch (t)
            {
                case Dynamic:
                    return Dynamic;
                case LegacyFallback:      // 旧口径：兜底与动态同格
                    return Dynamic;
                case Process:
                    return Process;
                case Always:
                    return Always;
                default:
                    return "";
            }
        }

        /// <summary>写回配置的规范串（如 `dynamic,process,always`）。</summary>
        public static string ToConf(string[] order)
        {
            string[] arr = Parse(order == null ? null : string.Join(",", order));
            var sb = new StringBuilder();
            foreach (string t in arr)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(t);
            }
            return sb.ToString();
        }

        /// <summary>设置页显示的段名。</summary>
        public static string Label(string token)
        {
            switch (token)
            {
                case Dynamic: return "动态菜单（含兜底）";
                case Process: return "进程菜单";
                case Always: return "固定菜单";
                default: return token;
            }
        }

        /// <summary>设置页显示的段说明。</summary>
        public static string Desc(string token)
        {
            switch (token)
            {
                case Dynamic: return "选中文件 / 文本时命中的分组；都没命中时由兜底分组顶上（同一格，不会并列出现）";
                case Process: return "按前台进程匹配的分组（proc）";
                case Always: return "每次弹出都会出现的分组（always）";
                default: return "";
            }
        }
    }
}
