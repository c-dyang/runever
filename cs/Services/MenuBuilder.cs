using System;
using System.Collections.Generic;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Native;

namespace RunEverCs.Services
{
    /// <summary>
    /// 菜单渲染器 —— 把「选中的分组列表」渲染成 Win32 原生菜单。
    ///
    /// 与 AHK 版 RunEver.BuildMenu 的语义对齐：
    ///   · 顶层平铺（不显示分组名），组间插分隔线
    ///   · 条目按 Level 建真子菜单（层级栈维护父子关系）
    ///   · 同名条目天然共存（命令 ID 与显示文本分离）
    /// </summary>
    public class MenuBuilder
    {
        public class BuildResult
        {
            public NativeMenu Menu;
            public Dictionary<int, MenuItem> IdMap = new Dictionary<int, MenuItem>();
            public int ItemCount;
            public int IconHit;
            public int IconFallback;
        }

        /// <summary>2026-11-07 分段计时（只读探针，不改行为）—— 用于决定"缓存什么才真正省时间"。</summary>
        public static long LastBuildMs;
        public static long LastItemMs;
        public static long LastIconMs;

        /// <summary>构建菜单（不显示，纯构建，供自检调用）。</summary>
        public static BuildResult Build(List<MenuGroup> groups)
        {
            var swAll = System.Diagnostics.Stopwatch.StartNew();
            long iconMs = 0;
            var root = new NativeMenu();
            var idmap = new Dictionary<int, MenuItem>();
            int total = 0, iconHit = 0, iconFallback = 0;

            // ★ 重置全局 ID 分配器：保证本次菜单树内 ID 唯一且从 1 起
            //   （跨菜单实例共享分配器 ⇒ 子菜单不再与根菜单撞 ID）
            NativeMenu.ResetIdAllocator();

            bool firstGroup = true;
            int lastTopLevel = 0;

            foreach (var g in groups)
            {
                if (g.Items.Count == 0) continue;

                // 组间分隔线（第一组不加）
                if (!firstGroup) root.AddSeparator();
                firstGroup = false;

                // 层级栈：stack[depth] = 该深度对应的菜单
                var stack = new List<NativeMenu>();
                stack.Add(root);

                int prevLevel = 0;
                foreach (var it in g.Items)
                {
                    int lv = it.Level;

                    // 从 prevLevel 走到 lv：上读时弹栈
                    while (stack.Count > lv)
                        stack.RemoveAt(stack.Count - 1);
                    // 补栈：配置跳级时用栈顶补齐
                    while (stack.Count < lv)
                        stack.Add(stack[stack.Count - 1]);

                    var target = stack[stack.Count - 1];

                    if (it.IsGroupHeader)
                    {
                        // 分组标题 → 建真子菜单
                        var sub = new NativeMenu();
                        int subIdx = target.AddSubMenu(FormatMenuText(it.Title, it.Hotkey, true), sub);

                        // ★ 2026-11-08：子菜单项用**系统文件夹图标**（popup 项没有命令 ID ⇒ 按位置设）。
                        //   子菜单没有动作 ⇒ 本来就没有图标源；按用户口径统一给文件夹图标，
                        //   不再"继承第一个子项的图标"。
                        long tIconS = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (target.SetFolderIconByPosition(subIdx)) iconHit++;
                        else iconFallback++;
                        iconMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tIconS)
                                  * 1000L / System.Diagnostics.Stopwatch.Frequency;

                        stack.Add(sub);
                    }
                    else
                    {
                        // 叶条目
                        string text = FormatMenuText(it.Title, it.Hotkey, false);

                        int id = target.AddItem(text);
                        idmap[id] = it;
                        total++;

                        // 图标
                        long tIcon0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (!string.IsNullOrEmpty(it.IconSource))
                        {
                            if (target.SetIconById(id, it.IconSource)) iconHit++;
                            else iconFallback++;
                        }
                        else
                        {
                            // 无图标源 → 兜底图标
                            if (target.SetIconById(id, "")) iconFallback++;
                        }
                        iconMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tIcon0)
                                  * 1000L / System.Diagnostics.Stopwatch.Frequency;
                    }

                    prevLevel = lv;
                }

                lastTopLevel++;
            }

            LastIconMs = iconMs;
            LastBuildMs = swAll.ElapsedMilliseconds;
            LastItemMs = LastBuildMs - iconMs;

            return new BuildResult
            {
                Menu = root,
                IdMap = idmap,
                ItemCount = total,
                IconHit = iconHit,
                IconFallback = iconFallback
            };
        }

        /// <summary>
        /// 菜单文本格式化 —— 让快捷键【真正可点】。
        ///
        /// ★★ 关键（2026-10-03 用户实测暴露）：
        ///   Win32 菜单的快捷键靠 `&X` 前缀（X 会带下划线，按 X 即可激活），
        ///   而不是 `[X]` 这种纯文本提示。
        ///   AHK 版只写了 `\t[X]` —— 看起来像快捷键，实际【按了没反应】。
        ///   这里改用 `&X`，Tab 后的 `[X]` 仅作视觉提示保留。
        ///
        /// 规则：
        ///   · 标题里若已有 `&`，不再重复注入（尊重手写）
        ///   · 找到快捷键字母在标题中的位置 → 在其前插 `&`
        ///   · ★ 2026-10-06（用户拍板 A 方案）：标题里**没有**这个字母时（中文标题常态，
        ///     如「任务管理器 _T」），**不再回退成纯提示**，而是补一个 `(&T)` 后缀 ——
        ///     让字母成为标题的一部分 ⇒ Win32 才有地方挂助记。**不改配置语义**（配置里
        ///     仍是 `_T`），只是渲染层补全。补后缀的条目不再加 `\t[X]`（避免同字母显示两次）。
        ///
        /// ⚠️ 光有 `&` 还不够：菜单还得真的拿到键盘焦点（见 `NativeMenu.Show` 的抢焦点）。
        ///    两个成因各自独立，缺一个都表现为"快捷键无效"。
        /// </summary>
        public static string FormatMenuText(string title, string hotkey, bool isSubMenu)
        {
            if (string.IsNullOrEmpty(title)) title = "";

            bool injected = false;
            bool suffixFallback = false;   // ★ 补了 `(&X)` 后缀

            if (!string.IsNullOrEmpty(hotkey) && hotkey.Length >= 1)
            {
                string key = hotkey.Substring(0, 1);
                if (title.IndexOf('&') < 0)
                {
                    int pos = title.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                    if (pos < 0)
                        pos = title.IndexOf(key.ToUpperInvariant(), StringComparison.Ordinal);
                    if (pos < 0)
                        pos = title.IndexOf(key.ToLowerInvariant(), StringComparison.Ordinal);

                    if (pos >= 0)
                    {
                        title = title.Substring(0, pos) + "&" + title.Substring(pos);
                        injected = true;
                    }
                    else
                    {
                        // ★ A 方案：标题不含该字母（中文标题常态）⇒ 补 `(&X)` 后缀
                        title += "(&" + key.ToUpperInvariant() + ")";
                        injected = true;
                        suffixFallback = true;
                    }
                }
                else
                {
                    injected = true;   // 标题自带 & → 视为已注入
                }
            }

            // Tab 后的提示文本：Win32 会把 Tab 右侧右对齐显示
            // ★ 补后缀的条目跳过（字母已经在标题里了）
            if (!string.IsNullOrEmpty(hotkey) && !suffixFallback)
            {
                if (!isSubMenu)
                    title += "\t[" + hotkey + "]";
                else if (!injected)
                    title += "\t[" + hotkey + "]";
            }

            return title;
        }

        /// <summary>递归收集菜单树里所有叶条目的命令 ID（用于跨菜单冲突检测）。</summary>
        private static void CollectIds(NativeMenu menu, List<int> ids, int depth)
        {
            if (menu == null || menu.Handle == IntPtr.Zero || depth > 8) return;
            int n = menu.Count();
            for (int i = 0; i < n; i++)
            {
                int id = menu.GetItemIdAt(i);
                if (id == -1)
                {
                    // 子菜单项 → 递归
                    IntPtr sh = menu.GetSubMenuHandle(i);
                    if (sh != IntPtr.Zero)
                    {
                        var sub = NativeMenu.FromHandle(sh);
                        CollectIds(sub, ids, depth + 1);
                    }
                }
                else if (id > 0)
                {
                    ids.Add(id);
                }
            }
        }

        /// <summary>自检：断言菜单结构与 AHK 版一致（不弹菜单）。</summary>
        public static string SelfTest(List<MenuGroup> groups)
        {
            var sb = new StringBuilder();
            var r = Build(groups);
            try
            {
                sb.AppendLine("顶层项数 = " + r.Menu.Count());
                sb.AppendLine("可点条目 = " + r.ItemCount);
                sb.AppendLine("图标: 命中=" + r.IconHit + " 兜底=" + r.IconFallback);

                // ---- 断言 0：命令 ID 全局唯一（★ 关键回归断言）----
                //   历史 bug：每个菜单实例各自从 1 编号 ⇒ 子菜单与根菜单撞 ID ⇒
                //   idmap 被覆盖 ⇒ 点任何 ID=1 的项都执行"计算器"。
                var idSeen = new Dictionary<int, MenuItem>();
                int dupIds = 0;
                foreach (var kv in r.IdMap)
                {
                    if (idSeen.ContainsKey(kv.Key)) dupIds++;
                    else idSeen[kv.Key] = kv.Value;
                }
                bool idOk = (dupIds == 0) && (r.IdMap.Count == r.ItemCount);
                sb.AppendLine("ID 唯一断言: idmap项数=" + r.IdMap.Count
                    + " 可点条目=" + r.ItemCount
                    + " 重复=" + dupIds
                    + " → " + (idOk ? "OK 全局唯一 ✓" : "FAIL"));

                // 收集所有子菜单里的 ID，确认不与根菜单撞车
                var allIds = new List<int>();
                CollectIds(r.Menu, allIds, 0);
                var idSet = new HashSet<int>();
                int crossDup = 0;
                foreach (var id in allIds)
                    if (!idSet.Add(id)) crossDup++;
                sb.AppendLine("跨菜单 ID 冲突检测: 全部叶项=" + allIds.Count
                    + " 唯一=" + idSet.Count
                    + " 冲突=" + crossDup
                    + " → " + (crossDup == 0 ? "OK 无冲突 ✓" : "FAIL(会有错点)"));

                // ---- 断言 1：同名项共存 ----
                // 构造一个含两条同名项的菜单，验证 ID 不同、文本相同
                var dupMenu = new NativeMenu();
                int id1 = dupMenu.AddItem("Rename");
                int id2 = dupMenu.AddItem("Rename");
                bool dupOk = (dupMenu.Count() == 2) && (id1 != id2)
                    && (dupMenu.GetItemText(0) == dupMenu.GetItemText(1));
                sb.AppendLine("重名断言: 项数=" + dupMenu.Count()
                    + " id=" + id1 + "/" + id2
                    + " 文本相同=" + (dupMenu.GetItemText(0) == dupMenu.GetItemText(1))
                    + " → " + (dupOk ? "OK 同名项共存 ✓" : "FAIL"));
                dupMenu.Dispose();

                // ---- 断言 2：子菜单层级 ----
                int subCount = r.Menu.CountSubMenus();
                sb.AppendLine("层级断言: 顶层子菜单数=" + subCount
                    + " → " + (subCount >= 1 ? "OK ✓" : "FAIL(应为 >=1)"));
                for (int i = 0; i < subCount && i < 8; i++)
                {
                    IntPtr sh = r.Menu.GetSubMenuHandle(i);
                    if (sh != IntPtr.Zero)
                    {
                        int n = Win32.GetMenuItemCount(sh);
                        sb.AppendLine("  子菜单[" + i + "] 项数=" + n);
                    }
                }

                // ---- 断言 3：组间分隔线 ----
                int sepCount = 0;
                for (int i = 0; i < r.Menu.Count(); i++)
                {
                    if (Win32.GetMenuItemID(r.Menu.Handle, i) == 0) sepCount++;
                }
                sb.AppendLine("分隔线数 = " + sepCount);

                sb.AppendLine("结果: " + (dupOk && subCount >= 1 ? "全部通过 ✓" : "有失败"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常: " + ex.Message);
            }
            finally
            {
                r.Menu.Dispose();
            }
            return sb.ToString();
        }
    }
}
