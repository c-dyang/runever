using System;
using System.Collections.Generic;
using System.IO;
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

            /// <summary>
            /// 固定项「全库搜索」的命令 ID（0 = 本次没有追加）。
            /// ★ 它**不在** <see cref="IdMap"/> 里 —— 派发方必须先判这个字段，再查 IdMap
            ///   （见 RunEverApp.ShowMenuFlow 的 MenuPick 分流）。
            /// </summary>
            public int FixedSearchId;

            /// <summary>
            /// 虚拟项「选中的文件」的命令 ID（0 = 本次没有选中文件 ⇒ 未插入）。
            /// ★ 同 <see cref="FixedSearchId"/>：不在 IdMap 里，派发方先判字段再查 IdMap。
            /// </summary>
            public int SelectedFileId;
        }

        // ============================================================
        // ★ S1（2026-11-10）：虚拟固定项 —— 「全库搜索」
        //
        // 用户定案：菜单**最后**恒有这一项，且**虚拟化**：
        //   · 不进 menus.conf（配置里根本没有这一行）
        //   · 不占条目 ID（用保留段 0x7E00–0x7EFF；条目 ID 由 NativeMenu.AllocId 从 1 递增）
        //   · 不可编辑/删除（不进 IdMap ⇒ 条目编辑器、回写、保存校验闸都看不见它）
        //   · 不参与剪枝/隐藏失效（**Build 之后**才追加，任何过滤都碰不到它）
        //   · 只在渲染层末尾追加（本方法就是唯一入口）
        //
        // ★ 为什么不塞进 Build()：`Build` 的语义是"分组 → 菜单"，固定项不属于配置；
        //   塞进去会让每个调用方（含全部既有自检的菜单计数）凭空多一条与配置无关的项。
        //   追加点收在 `RunEverApp.ShowMenuFlow` 弹菜单前一行。
        // ============================================================

        /// <summary>固定项显示文本。无快捷键、无 `&` 助记（它不该抢按键）。</summary>
        public const string FullSearchTitle = "全库搜索";

        /// <summary>固定项图标：shell32.dll 的放大镜（用户定案）。</summary>
        public const string FullSearchIcon = "SHELL32.dll,22";

        /// <summary>虚拟固定项的保留命令 ID 段起点（0x7E00–0x7EFF）。</summary>
        public const int FixedCommandIdBase = 0x7E00;

        /// <summary>「全库搜索」的命令 ID（IdMap 之外的保留 ID，与条目 ID 不重叠）。</summary>
        public const int FullSearchId = FixedCommandIdBase + 1;

        /// <summary>
        /// 在菜单**末尾**追加固定项「全库搜索」，返回它的命令 ID（0 = 追加失败）。
        /// 菜单非空时先补一条分隔线（与"组间分隔线"同一风格），避免与最后一条条目粘连。
        /// ★ 条目数（<see cref="BuildResult.ItemCount"/>）不因它变化 —— 它不是一个配置条目。
        /// </summary>
        public static int AppendFullSearch(BuildResult built)
        {
            if (built == null || built.Menu == null) return 0;
            var menu = built.Menu;

            // 先放分隔线：Count()>0 说明上面已经有内容（含"全空菜单"的禁用占位项）
            if (menu.Count() > 0) menu.AddSeparator();

            int id = FullSearchId;
            if (built.IdMap != null && built.IdMap.ContainsKey(id))
            {
                // 理论不可达（从 1 递增的分配器要产出 0x7E01 得有 32256 个条目）。
                // 真撞上就退化成"正常分配一个 ID" —— 宁可多占一个 ID，也不许点错派发。
                id = menu.AddItem(FullSearchTitle);
            }
            else
            {
                // 固定 ID 只能绕过 NativeMenu.AddItem（它强制分配新 ID）⇒ 直接走 Win32，
                // flags 与 AddItem 完全相同，只有 ID 由我们指定。
                if (!Win32.AppendMenuW(menu.Handle, Win32.MF_STRING, new IntPtr(id), FullSearchTitle))
                    return 0;
            }

            if (menu.SetIconById(id, FullSearchIcon)) built.IconHit++;
            else built.IconFallback++;

            built.FixedSearchId = id;
            return id;
        }

        // ============================================================
        // ★ S2（2026-11-11）：虚拟固定项 —— 「选中的文件」（置顶第一项）
        //
        // 用户定案：有选中文件时，菜单**最前**恒有这一项；点它 = 打开选中的文件。
        //   · 单选：显示文件名；多选：显示「打开选中的 N 个文件」
        //   · 深度复用执行器：Task{Type="open", Target="", Files=ctx.Files}
        //     ⇒ Executor 的 U-D 循环体逐文件追加引号路径（每个文件一次，天然并发）
        //   · 多选确认框在 RunEverApp.OpenSelectedFiles（渲染层不管交互）
        //   · 位置靠 InsertMenuW(0) 实现（AppendMenu 只能加末尾）
        // ============================================================

        /// <summary>虚拟项「选中的文件」的命令 ID。</summary>
        public const int SelectedFileId = FixedCommandIdBase + 2;

        /// <summary>置顶项的兜底图标（通用文件）。</summary>
        public const string SelectedFileIcon = "SHELL32.dll,0";

        /// <summary>
        /// 在菜单**最前**插入虚拟项「选中的文件」，返回命令 ID（0 = 没插）。
        /// ★ 不选中文件 ⇒ 返回 0，不占菜单位。
        /// </summary>
        public static int AppendSelectedFile(BuildResult built, IList<string> files)
        {
            if (built == null || built.Menu == null || files == null || files.Count == 0) return 0;

            string title;
            if (files.Count == 1)
            {
                string name;
                try { name = Path.GetFileName(files[0]); }
                catch { name = files[0]; }
                if (string.IsNullOrEmpty(name)) name = files[0];
                if (name.Length > 40) name = name.Substring(0, 37) + "…";
                title = "打开  " + name;
            }
            else
            {
                title = "打开选中的 " + files.Count + " 个文件";
            }

            int id = SelectedFileId;
            if (built.IdMap != null && built.IdMap.ContainsKey(id)) return 0;

            // 位置 0 + MF_BYPOSITION ⇒ 插到最前（这正是不用 AppendMenu 的原因）
            if (!Win32.InsertMenuW(built.Menu.Handle, 0,
                Win32.MF_BYPOSITION | Win32.MF_STRING, new IntPtr(id), title))
                return 0;

            // 图标：按第一个文件取；取不到退通用文件图标（不许因图标失败丢整项）
            string icon = SelectedFileIcon;
            try { if (File.Exists(files[0])) icon = files[0] + ",0"; } catch { }
            if (built.Menu.SetIconById(id, icon)) built.IconHit++;
            else if (icon != SelectedFileIcon && built.Menu.SetIconById(id, SelectedFileIcon)) built.IconHit++;
            else built.IconFallback++;

            // 用户要求（2026-11-11）：虚拟项后面补一条分隔线，与下方配置条目视觉分隔
            Win32.InsertMenuW(built.Menu.Handle, 1,
                Win32.MF_BYPOSITION | Win32.MF_SEPARATOR, IntPtr.Zero, null);

            built.SelectedFileId = id;
            return id;
        }

        /// <summary>2026-11-07 分段计时（只读探针，不改行为）—— 用于决定"缓存什么才真正省时间"。</summary>
        public static long LastBuildMs;
        public static long LastItemMs;
        public static long LastIconMs;

        /// <summary>本次构建里被**渲染护栏**挡掉的空壳子菜单数（正常为 0；与剪枝统计同源展示）。</summary>
        public static int LastPrunedSubtrees;

        /// <summary>本次构建里被挡掉的空壳子菜单标题（日志用）。</summary>
        public static List<string> LastPrunedTitles = new List<string>();

        // ============================================================
        // ★ 2026-11-09（U-B）：`open sub <目录>` —— 渲染期动态子菜单
        //   只在这条 open 落到**本次已命中**的段里才枚举（Win32 菜单无懒加载）；
        //   枚举结果只进 NativeMenu / idmap，**绝不写回 menus.conf**。
        // ============================================================

        /// <summary>本次构建真的建出来的目录子菜单数（含递归展开的层级）。</summary>
        public static int LastSubmenus;

        /// <summary>本次构建动态生成的子项数（= 可点条目里来自目录枚举的那部分）。</summary>
        public static int LastSubmenuItems;

        /// <summary>本次构建里标记了 sub、但目录不存在/无权限/为空 ⇒ 退化为普通 open 的条目数。</summary>
        public static int LastSubmenuFallbacks;

        /// <summary>本次构建里目录枚举命中 mtime 缓存的次数（性能证据：第二遍构建不重扫）。</summary>
        public static int LastDirCacheHits;

        /// <summary>构建菜单（不显示，纯构建，供自检调用）。conf = null ⇒ 不展开子菜单（历史行为）。</summary>
        public static BuildResult Build(List<MenuGroup> groups)
        {
            return Build(groups, null);
        }

        /// <summary>
        /// 构建菜单。<paramref name="conf"/> = auto.conf 的内存快照（AutoMatcher.Conf）。
        /// ★ 只有 <c>SubmenuEnabled=1</c> 才展开 `open sub`；其余键（limit/order/recursive/depth）
        ///   全来自这一份快照 —— 与 AutoMatcher 的解析同源，不在这里重读文件。
        /// </summary>
        public static BuildResult Build(List<MenuGroup> groups, AutoConf conf)
        {
            var swAll = System.Diagnostics.Stopwatch.StartNew();
            long iconMs = 0;
            var root = new NativeMenu();
            var idmap = new Dictionary<int, MenuItem>();
            int total = 0, iconHit = 0, iconFallback = 0;

            LastPrunedSubtrees = 0;
            LastPrunedTitles = new List<string>();
            LastSubmenus = 0;
            LastSubmenuItems = 0;
            LastSubmenuFallbacks = 0;
            LastDirCacheHits = 0;

            // ★ 重置全局 ID 分配器：保证本次菜单树内 ID 唯一且从 1 起
            //   （跨菜单实例共享分配器 ⇒ 子菜单不再与根菜单撞 ID）
            NativeMenu.ResetIdAllocator();

            bool firstGroup = true;
            int lastTopLevel = 0;

            foreach (var g in groups)
            {
                if (g.Items.Count == 0) continue;

                // ★ 渲染护栏：子树里没有任何可点条目 ⇒ 连子菜单都不建（永不生成 0 项子菜单）
                var emptyShells = FindEmptyShells(g.Items);
                if (emptyShells.Count >= g.Items.Count) continue;    // 整组都是空壳 ⇒ 不占一行/一条分隔线

                // 组间分隔线（第一组不加）
                if (!firstGroup) root.AddSeparator();
                firstGroup = false;

                // 层级栈：stack[depth] = 该深度对应的菜单
                var stack = new List<NativeMenu>();
                stack.Add(root);

                int prevLevel = 0;
                foreach (var it in g.Items)
                {
                    if (it == null || emptyShells.Contains(it)) continue;
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
                        // ★ U-B：`open sub <目录>` —— 目录可枚举就当场建子菜单，否则落到下面的普通叶分支
                        //   ★ 2026-11-10：语义拆成两层 ——
                        //     · 显式 sub（条目编辑页「以子菜单展示」勾选 / menus.conf 写 open sub）
                        //       ⇒ **总是**尝试展开，不受全局闸门限制（用户明确要这个条目展开）；
                        //     · 非显式 sub 的普通 open 目录条目 ⇒ 只有全局闸门
                        //       「文件夹条目展开为子菜单」打开时才自动展开。
                        if (it.Type == ActionType.Open && conf != null
                            && (it.Sub || (conf.SubmenuEnabled == 1 && IsDirectoryTarget(it))))
                        {
                            var sr = TryBuildDirectoryMenu(it.Target, conf, 1, idmap);
                            if (sr != null)
                            {
                                int subIdx = target.AddSubMenu(
                                    FormatMenuText(it.Title, it.Hotkey, true), sr.Menu);

                                // 子菜单项本身用系统文件夹图标（popup 项没有命令 ID ⇒ 按位置设）
                                long tIconS = System.Diagnostics.Stopwatch.GetTimestamp();
                                if (target.SetFolderIconByPosition(subIdx)) iconHit++;
                                else iconFallback++;
                                iconMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tIconS)
                                          * 1000L / System.Diagnostics.Stopwatch.Frequency;

                                total += sr.Items;
                                iconHit += sr.IconHit;
                                iconFallback += sr.IconFallback;
                                LastSubmenus += 1 + sr.Submenus;
                                LastSubmenuItems += sr.Items;

                                prevLevel = lv;
                                continue;
                            }

                            // 目录不存在/无权限/为空 ⇒ 退化普通 open（绝不建空子菜单）
                            LastSubmenuFallbacks++;
                        }

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
                        else if (it.Type == ActionType.Open && IsDirectoryTarget(it))
                        {
                            // ★ 2026-11-10：文件夹条目（open 指向目录）用**系统文件夹图标**，
                            //   不再走程序兜底图标（兜底是放大镜，对文件夹是错的）。
                            if (target.SetFolderIconById(id)) iconHit++;
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

        /// <summary>open 条目的目标是不是可枚举目录（无模板占位符 + 目录存在）。</summary>
        private static bool IsDirectoryTarget(MenuItem it)
        {
            if (it == null || it.Type != ActionType.Open) return false;
            string t = (it.Target ?? "").Trim(' ', '\t', '"');
            if (t.Length == 0 || t.IndexOf('%') >= 0) return false;
            try { return System.IO.Directory.Exists(t); }
            catch { return false; }
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

        /// <summary>
        /// 渲染护栏：找出"子树里没有任何可点条目"的组标题，连同其整棵子树一起返回（调用方跳过不渲染）。
        ///
        /// 为什么还要这一层：剪枝是数据层的收尾，但渲染是最后一道闸 —— 任何来源的分组列表
        /// （自检构造 / 未来新调用方）都不该让 Win32 建出 0 项 popup（点开一片空白）。
        /// 判据只看"这个标题跨度内有没有非标题条目"，与层级栈口径一致。
        /// </summary>
        private static HashSet<MenuItem> FindEmptyShells(IList<MenuItem> items)
        {
            var hide = new HashSet<MenuItem>();
            if (items == null) return hide;

            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                if (it == null || !it.IsGroupHeader) continue;

                int j = i + 1;
                int visible = 0;
                while (j < items.Count && items[j] != null && items[j].Level > it.Level)
                {
                    if (!items[j].IsGroupHeader) visible++;
                    j++;
                }

                if (visible > 0) continue;

                for (int m = i; m < j; m++) hide.Add(items[m]);
                LastPrunedSubtrees++;
                LastPrunedTitles.Add(it.Title);
                i = j - 1;
            }
            return hide;
        }

        // ============================================================
        // ★ U-B：目录子菜单（渲染期枚举，不写回配置）
        // ============================================================

        /// <summary>一个目录的枚举快照（按目录 mtime 缓存；只读）。</summary>
        private sealed class DirListing
        {
            public long Stamp;                                   // 目录 LastWriteTimeUtc.Ticks
            public readonly List<string> Dirs = new List<string>();
            public readonly List<string> Files = new List<string>();
        }

        /// <summary>一棵动态子菜单的构建产物（计数回传给 Build 汇总）。</summary>
        private sealed class SubMenuBuild
        {
            public NativeMenu Menu;
            public int Items;
            public int IconHit;
            public int IconFallback;
            public int Submenus;
        }

        /// <summary>目录枚举缓存：mtime 未变 ⇒ 0 次目录枚举（只 stat 一次）。</summary>
        private static readonly Dictionary<string, DirListing> DirCache =
            new Dictionary<string, DirListing>(StringComparer.OrdinalIgnoreCase);
        private static readonly object DirCacheLock = new object();

        /// <summary>缓存上限（超过就整体清空 —— 菜单只碰用户点过的那几个目录，64 足够）。</summary>
        private const int DirCacheLimit = 64;

        /// <summary>
        /// 把一条 `open sub` 的目录展开成一棵子菜单。
        /// 返回 null = **退化普通 open**（目录不存在 / 无权限 / 为空 / 枚举异常）。
        ///
        /// depth 从 1 起（第一层展开 = 1）；只有 `SubmenuRecursive=1` 且
        /// `depth &lt; SubmenuDepth` 时才把子目录再展开 —— 默认递归关，只展开一层。
        /// </summary>
        private static SubMenuBuild TryBuildDirectoryMenu(string target, AutoConf conf, int depth,
            Dictionary<int, MenuItem> idmap)
        {
            string dir = NormalizeDirectoryTarget(target);
            if (dir.Length == 0) return null;

            var dirs = new List<string>();
            var files = new List<string>();
            if (!ReadDirectory(dir, dirs, files)) return null;

            // ---- 排序：folder-first（目录在前）/ name（纯按名称）----
            var ordered = new List<KeyValuePair<string, bool>>();   // Key = 全路径，Value = 是否目录
            bool byName = string.Equals(conf.SubmenuOrder, "name", StringComparison.OrdinalIgnoreCase);
            if (byName)
            {
                foreach (var d in dirs) ordered.Add(new KeyValuePair<string, bool>(d, true));
                foreach (var f in files) ordered.Add(new KeyValuePair<string, bool>(f, false));
                ordered.Sort(delegate (KeyValuePair<string, bool> a, KeyValuePair<string, bool> b)
                {
                    return StringComparer.CurrentCultureIgnoreCase.Compare(
                        Path.GetFileName(a.Key), Path.GetFileName(b.Key));
                });
            }
            else
            {
                // ReadDirectory 已按名称排好；目录段在前，文件段在后
                foreach (var d in dirs) ordered.Add(new KeyValuePair<string, bool>(d, true));
                foreach (var f in files) ordered.Add(new KeyValuePair<string, bool>(f, false));
            }

            // ---- 上限截断（防大目录把菜单撑爆）----
            int limit = conf.SubmenuLimit;
            if (limit < 1) limit = 1;
            if (ordered.Count > limit) ordered.RemoveRange(limit, ordered.Count - limit);

            // 空目录 / 全被过滤 ⇒ 不建子菜单（与 F1 剪枝口径一致）
            if (ordered.Count == 0) return null;

            var menu = new NativeMenu();
            var result = new SubMenuBuild { Menu = menu };
            try
            {
                foreach (var e in ordered)
                {
                    string name = Path.GetFileName(e.Key);
                    if (name.Length == 0) name = e.Key;
                    // 文件名里的 & 在 Win32 菜单里是助记符前缀 ⇒ 双写才是字面量（"R&D.txt" 不能变成 R_D.txt）
                    string label = name.Replace("&", "&&");

                    // 递归：只有显式开启且还没到层数上限
                    if (e.Value && conf.SubmenuRecursive == 1 && depth < conf.SubmenuDepth)
                    {
                        var nested = TryBuildDirectoryMenu(e.Key, conf, depth + 1, idmap);
                        if (nested != null)
                        {
                            int idx = menu.AddSubMenu(label, nested.Menu);
                            if (menu.SetFolderIconByPosition(idx)) result.IconHit++;
                            else result.IconFallback++;

                            result.Items += nested.Items;
                            result.Submenus += 1 + nested.Submenus;
                            continue;
                        }
                    }

                    // 叶：点它 = open 这个文件/目录（与普通 open 条目同一条派发路径）
                    int id = menu.AddItem(label);
                    idmap[id] = new MenuItem
                    {
                        Title = name,
                        Type = ActionType.Open,
                        Target = e.Key,
                        Level = 0
                    };
                    result.Items++;

                    int pos = menu.Count() - 1;
                    bool iconOk = e.Value ? menu.SetFolderIconByPosition(pos)
                                          : menu.SetFileIconByPosition(pos);
                    if (iconOk) result.IconHit++;
                    else result.IconFallback++;
                }
            }
            catch
            {
                // 建菜单中途失败 ⇒ 整棵丢弃，调用方退化为普通 open
                menu.Dispose();
                return null;
            }

            return result;
        }

        /// <summary>
        /// 读一个目录（缓存优先）。返回 false = 不存在 / 无权限 / IO 错 ⇒ 调用方退化普通 open。
        /// ★ 缓存键 = 目录全路径；失效判据 = 目录 LastWriteTimeUtc（增删条目才会变）。
        ///   命中缓存时只花一次 stat，不枚举整棵树 —— 这是"每次弹菜单不重扫"的关键。
        /// </summary>
        private static bool ReadDirectory(string dir, List<string> outDirs, List<string> outFiles)
        {
            DirectoryInfo di;
            long stamp;
            try
            {
                di = new DirectoryInfo(dir);
                if (!di.Exists) return false;
                stamp = di.LastWriteTimeUtc.Ticks;
            }
            catch { return false; }

            lock (DirCacheLock)
            {
                DirListing hit;
                if (DirCache.TryGetValue(dir, out hit) && hit.Stamp == stamp)
                {
                    outDirs.AddRange(hit.Dirs);
                    outFiles.AddRange(hit.Files);
                    LastDirCacheHits++;
                    return true;
                }
            }

            var fresh = new DirListing { Stamp = stamp };
            try
            {
                foreach (var fsi in di.GetFileSystemInfos())
                {
                    try
                    {
                        FileAttributes a = fsi.Attributes;
                        // 隐藏/系统条目不进菜单（与资源管理器默认视图口径一致）
                        if ((a & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if ((a & FileAttributes.Directory) != 0) fresh.Dirs.Add(fsi.FullName);
                        else fresh.Files.Add(fsi.FullName);
                    }
                    catch { }   // 单个条目读属性失败 ⇒ 跳过，不影响整目录
                }
            }
            catch { return false; }

            fresh.Dirs.Sort(StringComparer.CurrentCultureIgnoreCase);
            fresh.Files.Sort(StringComparer.CurrentCultureIgnoreCase);

            outDirs.AddRange(fresh.Dirs);
            outFiles.AddRange(fresh.Files);

            lock (DirCacheLock)
            {
                if (DirCache.Count >= DirCacheLimit) DirCache.Clear();
                DirCache[dir] = fresh;
            }
            return true;
        }

        /// <summary>目标 → 目录路径：去首尾空白与包裹引号，展开环境变量。空串 = 不可用。</summary>
        private static string NormalizeDirectoryTarget(string target)
        {
            string t = (target ?? "").Trim();
            if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
                t = t.Substring(1, t.Length - 2).Trim();
            if (t.Length == 0) return "";
            try { t = Environment.ExpandEnvironmentVariables(t); }
            catch { }
            return t;
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
