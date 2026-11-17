using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RunEverCs.Models;
using RunEverCs.Native;

namespace RunEverCs.Services
{
    /// <summary>右键二级菜单上的三个动作（数值稳定：自检按数值断言，别随手改）。</summary>
    public enum MenuItemAction
    {
        None = 0,

        /// <summary>编辑：就地改**动作目标**（深编辑仍走设置页的条目编辑页）。</summary>
        EditTarget = 1,

        /// <summary>重命名：就地改**标题**。</summary>
        Rename = 2,

        /// <summary>删除：删该条目（分组标题则连整棵子树一起删，否则结构校验必拒）。</summary>
        Delete = 3
    }

    /// <summary>
    /// 一条右键动作的"定位凭证"——**不是**对象引用。
    ///
    /// 为什么不用 MenuItem 引用：菜单里看到的是**过滤/重排后**的快照
    /// （判定层 hideMissing + menuOrder + 目录子菜单临时项），拿它直接写盘
    /// 等于把"当前屏幕上这一份"当配置写回去 —— 会把没显示的条目全删掉。
    /// 所以写盘前必须**重新解析磁盘配置**，再用 (行号 + 标题 + 层级) 定位同一条。
    /// </summary>
    public sealed class MenuItemRef
    {
        public int Line;
        public int Level;
        public string Title = "";
        public string Target = "";
        public ActionType Type = ActionType.Group;

        public static MenuItemRef From(MenuItem it)
        {
            if (it == null) return null;
            return new MenuItemRef
            {
                Line = it.Line,
                Level = it.Level,
                Title = it.Title ?? "",
                Target = it.Target ?? "",
                Type = it.Type
            };
        }

        public override string ToString()
        {
            return "「" + Title + "」 第 " + Line + " 行 lv" + Level;
        }
    }

    /// <summary>一次右键动作的结果（成功与否 + 给人看的一句 + 落盘统计）。</summary>
    public sealed class MenuItemContextResult
    {
        public bool Ok;
        public string Message = "";
        public MenuItemAction Action = MenuItemAction.None;
        public string OldTitle = "";
        public string NewTitle = "";
        public int RemovedItems;

        public static MenuItemContextResult Fail(string why)
        {
            return new MenuItemContextResult { Ok = false, Message = why ?? "" };
        }

        public override string ToString()
        {
            return (Ok ? "OK " : "FAIL ") + Action + " " + Message;
        }
    }

    /// <summary>
    /// ★ 卡 D（2026-11-17b）：菜单项**右键上下文菜单**（编辑 / 删除 / 重命名单条目）。
    ///
    /// 链路（官方机制，逐条对应 HANDOFF 卡 D 的"官方机制"）：
    ///   ① 主菜单弹出时带 <c>TPM_RECURSE</c>（见 <see cref="NativeMenu.BuildPopupFlags"/>）；
    ///   ② 用户在某个条目上松开右键 ⇒ user32 把 <c>WM_MENURBUTTONUP</c> **sent** 给菜单 owner；
    ///   ③ <see cref="NativeMenu.Show"/> 在弹出期间临时子类化 owner，把该消息交给本类；
    ///   ④ 本类用 <c>GetMenuItemInfoW(hMenu, index, MF_BYPOSITION, MIIM_ID)</c> 取命令 ID，
    ///      再查 <c>BuildResult.IdMap[id]</c> 拿 <see cref="MenuItem"/>（**绝不按标题回查**）；
    ///   ⑤ 带 <c>TPM_RECURSE</c> 弹二级菜单（光标位置）；
    ///   ⑥ 选中动作后：**先 <c>EndMenu()</c> 关掉主菜单**，把动作存成"待执行"，
    ///      等 <see cref="NativeMenu.PopupClosed"/>（已退出菜单模态循环）再执行 ——
    ///      输入框/写盘绝不在菜单消息泵里同步做（那会把菜单卡死）。
    ///   ⑦ 写盘只走既有 <see cref="MenuConfWriter"/>（.bak + 原子写 + 窄写回），
    ///      **不新增任何写盘路径**；
    ///   ⑧ 失败只记日志 —— 主菜单不受影响，绝不抛到上层把菜单打掉。
    ///
    /// ⚠ 接线边界（如实声明，见 SUMMARY.md）：
    ///   本类只提供能力，**不自己去找配置路径/IdMap**。要在产品里生效，
    ///   `RunEverApp.ShowMenuFlow` 里 `built.Menu.ShowAtCursor(...)` 之前需要一行
    ///   <c>MenuItemContextMenu.Attach(built.Menu, built.IdMap, confPath, Log, prompt)</c>
    ///   （+ 可选输入框）。那一行在卡外文件（RunEverApp.cs），本卡不许改 ⇒ 未接线。
    /// </summary>
    public sealed class MenuItemContextMenu
    {
        // ---- 二级菜单的条目与命令（AddItem 返回的真实 ID 由 ShowSecondary 动态拿）----
        public const string LabelEdit = "编辑（改目标）…";
        public const string LabelRename = "重命名（改标题）…";
        public const string LabelDelete = "删除";

        /// <summary>「全库搜索 / 选中的文件」等虚拟固定项的保留 ID 段起点（与 MenuBuilder 同源）。</summary>
        private const int FixedCommandIdBase = 0x7E00;

        private readonly NativeMenu _menu;
        private readonly Dictionary<int, MenuItem> _idMap;
        private readonly string _confPath;
        private readonly Action<string> _log;
        private readonly Func<string, MenuItemAction, string> _prompt;

        private PendingAction _pending;
        private bool _inButtonUp;

        /// <summary>最近一次动作的结果（诊断/自检只读）。</summary>
        public MenuItemContextResult LastResult { get; private set; }

        /// <summary>二级菜单真的弹出了几次（诊断/自检只读；越界/分隔线不会 +1）。</summary>
        public int SecondaryPopupCount { get; private set; }

        /// <summary>输入回调被调用的次数（证明"输入只发生在菜单关闭之后"）。</summary>
        public int PromptCallCount { get; private set; }

        private sealed class PendingAction
        {
            public MenuItemRef Item;
            public MenuItemAction Action;
            public string Title;
        }

        private MenuItemContextMenu(NativeMenu menu, IDictionary<int, MenuItem> idMap,
            string confPath, Action<string> log, Func<string, MenuItemAction, string> prompt)
        {
            _menu = menu;
            _confPath = confPath ?? "";
            _log = log;
            _prompt = prompt;
            _idMap = new Dictionary<int, MenuItem>();
            if (idMap != null)
                foreach (var kv in idMap)
                    _idMap[kv.Key] = kv.Value;
        }

        /// <summary>
        /// 把右键上下文菜单挂到一张已构建好的菜单上（每个菜单实例挂一次；重建菜单后要重新 Attach）。
        /// </summary>
        /// <param name="prompt">输入回调：入参 = (当前值, 动作)，返回新文本；返回 null = 用户取消。
        ///   为 null ⇒ 编辑/重命名记日志后放弃（删除仍然可用）。**只在菜单关闭后调用**。</param>
        public static MenuItemContextMenu Attach(NativeMenu menu, IDictionary<int, MenuItem> idMap,
            string confPath, Action<string> log, Func<string, MenuItemAction, string> prompt)
        {
            if (menu == null) throw new ArgumentNullException("menu");

            var ctx = new MenuItemContextMenu(menu, idMap, confPath, log, prompt);
            menu.ContextLog = log;
            menu.MenuButtonUp = ctx.OnMenuButtonUp;
            menu.PopupClosed = ctx.OnPopupClosed;
            ctx.Log("右键菜单已挂载（条目映射 " + ctx._idMap.Count + " 条"
                + (prompt == null ? "；未提供输入框 ⇒ 编辑/重命名不可用" : "") + "）");
            return ctx;
        }

        /// <summary>摘掉（重建菜单前 / 换配置后）。</summary>
        public void Detach()
        {
            try
            {
                if (_menu != null)
                {
                    _menu.MenuButtonUp = null;
                    _menu.PopupClosed = null;
                }
            }
            catch { }
            _pending = null;
        }

        private void Log(string line)
        {
            if (_log == null) return;
            try { _log(line); } catch { }
        }

        // ============================================================
        // ① 索引 → MenuItem（纯查表；自检直接喂 hMenu + index）
        // ============================================================

        /// <summary>
        /// 零基索引 + HMENU → <see cref="MenuItem"/>。
        /// 失败（越界 / 分隔线 / 子菜单项 / 虚拟项 / IdMap 里没有）⇒ false + 可读原因，
        /// **绝不抛异常**（这是自检 ④ 的三条边界）。
        /// </summary>
        public static bool TryResolveItem(IntPtr hMenu, int index, IDictionary<int, MenuItem> idMap,
            out MenuItem item, out string why)
        {
            item = null;

            int cmdId;
            bool isSep, isSub;
            if (!NativeMenu.TryGetMenuItemInfo(hMenu, index, out cmdId, out isSep, out isSub, out why))
                return false;

            if (cmdId >= FixedCommandIdBase)
            {
                why = "虚拟固定项（命令 ID=" + cmdId + " 不在 menus.conf 里，不可编辑）";
                return false;
            }

            MenuItem found = null;
            if (idMap == null || !idMap.TryGetValue(cmdId, out found) || found == null)
            {
                why = "命令 ID=" + cmdId + " 不在 IdMap 里（虚拟项 / 旧菜单的失效 ID）";
                return false;
            }

            // 目录子菜单的临时项：只在渲染期存在，配置里根本没有这一行
            if (found.Line <= 0)
            {
                why = "目录子菜单的临时项（不在 menus.conf 里，不可编辑）";
                return false;
            }

            item = found;
            why = "";
            return true;
        }

        // ============================================================
        // ② owner 的 WM_MENURBUTTONUP
        // ============================================================

        private void OnMenuButtonUp(IntPtr owner, IntPtr hMenu, int index)
        {
            // 二级菜单自己也弹在同一个 owner 上 ⇒ 它上面的右键同样会发这条消息，
            // 不拦住就会无限套娃。
            if (_inButtonUp || _pending != null) return;

            _inButtonUp = true;
            try
            {
                MenuItem item;
                string why;
                if (!TryResolveItem(hMenu, index, _idMap, out item, out why))
                {
                    // 边界三种（越界 / 分隔线 / 子菜单）统一走这里：只记日志，什么都不弹
                    Log("右键：第 " + index + " 项不可编辑（" + why + "）");
                    return;
                }

                MenuItemAction action = ShowSecondaryMenu(owner, item);
                if (action == MenuItemAction.None)
                {
                    Log("右键：「" + item.Title + "」已取消（未改动配置）");
                    return;
                }

                _pending = new PendingAction
                {
                    Item = MenuItemRef.From(item),
                    Action = action,
                    Title = item.Title
                };
                Log("右键：「" + item.Title + "」→ " + ActionLabel(action)
                    + "（先关主菜单；动作在菜单关闭后执行）");

                // ★ 先关主菜单：当前 HMENU 是固定的，改完只能重建新菜单，
                //   不许在旧句柄上继续跑（旧 IdMap 会失效）。
                if (!NativeMenu.EndActiveMenu())
                    Log("⚠ 右键：EndMenu 返回 false（主菜单可能已关闭）");
            }
            catch (Exception ex)
            {
                Log("⚠ 右键菜单异常（主菜单不受影响）: " + ex.Message);
                _pending = null;
            }
            finally
            {
                _inButtonUp = false;
            }
        }

        /// <summary>主菜单关闭后（已退出菜单模态循环）才执行待办动作。</summary>
        private void OnPopupClosed()
        {
            var p = _pending;
            if (p == null) return;
            _pending = null;
            try
            {
                RunPending(p);
            }
            catch (Exception ex)
            {
                LastResult = MenuItemContextResult.Fail("执行异常：" + ex.Message);
                Log("⚠ 右键动作失败（配置未改动）: " + ex.Message);
            }
        }

        private void RunPending(PendingAction p)
        {
            string newText = null;

            if (p.Action == MenuItemAction.Rename || p.Action == MenuItemAction.EditTarget)
            {
                if (_prompt == null)
                {
                    LastResult = MenuItemContextResult.Fail("未提供输入框（编辑/重命名需要输入）");
                    Log("右键：" + ActionLabel(p.Action)
                        + " 需要输入框，但未接线 ⇒ 已放弃，配置未改动（见 SUMMARY.md）");
                    return;
                }

                string current = p.Action == MenuItemAction.Rename ? p.Item.Title : p.Item.Target;
                PromptCallCount++;
                newText = _prompt(current, p.Action);
                if (newText == null)
                {
                    LastResult = MenuItemContextResult.Fail("用户取消");
                    Log("右键：输入已取消，配置未改动");
                    return;
                }
            }

            var r = Apply(_confPath, p.Item, p.Action, newText);
            LastResult = r;
            Log("右键：" + ActionLabel(p.Action) + (r.Ok ? " 已完成" : " 失败（配置未改动）")
                + " —— " + (r.Message ?? "").Replace("\r", " ").Replace("\n", " "));
        }

        // ============================================================
        // ③ 二级菜单（TPM_RECURSE，光标位置）
        // ============================================================

        private MenuItemAction ShowSecondaryMenu(IntPtr owner, MenuItem item)
        {
            NativeMenu m = null;
            try
            {
                m = new NativeMenu();
                int idEdit = m.AddItem(LabelEdit);
                int idRename = m.AddItem(LabelRename);
                m.AddSeparator();
                int idDelete = m.AddItem(LabelDelete);

                Win32.POINT pt;
                Win32.GetCursorPos(out pt);

                int cmd = NativeMenu.TrackPopupRaw(m.Handle, NativeMenu.ContextPopupFlags, pt.X, pt.Y, owner);
                SecondaryPopupCount++;

                if (cmd == idEdit) return MenuItemAction.EditTarget;
                if (cmd == idRename) return MenuItemAction.Rename;
                if (cmd == idDelete) return MenuItemAction.Delete;
                return MenuItemAction.None;
            }
            catch (Exception ex)
            {
                // 弹不出来 ⇒ 只记日志，主菜单照常（HANDOFF「失败要能降级」）
                Log("⚠ 右键菜单弹出失败（主菜单不受影响）: " + ex.Message);
                return MenuItemAction.None;
            }
            finally
            {
                if (m != null) { try { m.Dispose(); } catch { } }
            }
        }

        // ============================================================
        // ④ 落盘（唯一写盘路径 = MenuConfWriter）
        // ============================================================

        /// <summary>
        /// 对**沙箱/真实** conf 执行一次动作：重新解析磁盘 → 定位 → 改内存模型 → 交给
        /// <see cref="MenuConfWriter.Save"/>（结构校验 + .bak + 原子写 + 窄写回 + 回读校验）。
        ///
        /// ★ 绝不拿"菜单看到的那份 groups"写盘：那是过滤/重排/枚举后的显示快照。
        /// ★ 定位不上（文件被别处改过）⇒ 拒绝，不猜。
        /// </summary>
        public static MenuItemContextResult Apply(string confPath, MenuItemRef target,
            MenuItemAction action, string newText)
        {
            var res = new MenuItemContextResult { Action = action };

            if (string.IsNullOrEmpty(confPath))
            {
                res.Message = "配置路径为空";
                return res;
            }
            if (target == null)
            {
                res.Message = "缺少条目定位信息";
                return res;
            }
            if (action == MenuItemAction.None)
            {
                res.Message = "没有动作";
                return res;
            }

            List<MenuGroup> groups;
            try
            {
                var p = new ConfParser(confPath);
                p.Parse();
                groups = p.Groups;
            }
            catch (Exception ex)
            {
                res.Message = "配置读不进来：" + ex.Message;
                return res;
            }

            MenuGroup g;
            int idx;
            string why;
            if (!TryLocate(groups, target, out g, out idx, out why))
            {
                res.Message = why;
                return res;
            }

            var it = g.Items[idx];
            res.OldTitle = it.Title ?? "";
            res.NewTitle = res.OldTitle;

            if (action == MenuItemAction.Rename)
            {
                string t = (newText ?? "").Trim();
                string bad = ValidateTitle(t);
                if (bad != null) { res.Message = bad; return res; }
                it.Title = t;
                res.NewTitle = t;
            }
            else if (action == MenuItemAction.EditTarget)
            {
                if (it.IsGroupHeader)
                {
                    res.Message = "「" + res.OldTitle + "」是分组标题（group），没有动作目标可改";
                    return res;
                }
                if (it.IsSeparator)
                {
                    res.Message = "分隔线没有动作目标";
                    return res;
                }
                string t = (newText ?? "").Trim();
                if (t.Length == 0) { res.Message = "目标不能为空（原名未改动）"; return res; }
                if (t.IndexOf('\r') >= 0 || t.IndexOf('\n') >= 0)
                {
                    res.Message = "目标不能含换行（原名未改动）";
                    return res;
                }
                it.Target = t;
                res.NewTitle = it.Title ?? "";
            }
            else if (action == MenuItemAction.Delete)
            {
                // 分组标题必须连整棵子树一起删：只删标题行会留下"孤儿子项"，
                // 结构校验（统一是 Level 口径）会判跳级/空壳 ⇒ 干脆在这里一次删干净。
                int end = idx + 1;
                while (end < g.Items.Count && g.Items[end] != null && g.Items[end].Level > it.Level) end++;
                res.RemovedItems = end - idx;
                g.Items.RemoveRange(idx, res.RemovedItems);
                res.NewTitle = "";
            }
            else
            {
                res.Message = "未知动作";
                return res;
            }

            string msg;
            var writer = new MenuConfWriter(confPath);
            bool ok = writer.Save(groups, out msg);
            res.Ok = ok;
            res.Message = msg;
            return res;
        }

        /// <summary>
        /// 标题落盘前的**最小**合法性（不是完整校验 —— 完整校验由 writer 的结构闸与回读闸负责）：
        /// 空标题写出来是一行只剩缩进，解析回来标题为空；`|` 会被解析器当动作分隔符 ⇒ 往返必坏。
        /// </summary>
        public static string ValidateTitle(string t)
        {
            if (string.IsNullOrEmpty(t) || t.Trim().Length == 0) return "标题不能为空（原名未改动）";
            if (t.IndexOf('|') >= 0) return "标题不能含 `|`（解析器用它分隔动作，往返会坏）";
            if (t.IndexOf('\r') >= 0 || t.IndexOf('\n') >= 0) return "标题不能含换行";
            if (t.IndexOf('\t') >= 0) return "标题不能含制表符（Win32 菜单用 Tab 右对齐快捷键提示）";
            return null;
        }

        /// <summary>
        /// 在**磁盘解析结果**里定位同一条：行号 + 层级 + 标题三者同时相符才算数。
        /// 只按行号会认错（文件被别处改过一行，行号全移），只按标题会认错（同名项合法共存）。
        /// </summary>
        private static bool TryLocate(List<MenuGroup> groups, MenuItemRef target,
            out MenuGroup group, out int index, out string why)
        {
            group = null;
            index = -1;
            why = "";

            if (groups != null)
            {
                foreach (var g in groups)
                {
                    if (g == null || g.Items == null) continue;
                    for (int i = 0; i < g.Items.Count; i++)
                    {
                        var it = g.Items[i];
                        if (it == null) continue;
                        if (it.Line != target.Line) continue;
                        if (it.Level != target.Level) continue;
                        if (!string.Equals(it.Title ?? "", target.Title ?? "", StringComparison.Ordinal)) continue;
                        group = g;
                        index = i;
                        return true;
                    }
                }
            }

            why = "配置已变化：第 " + target.Line + " 行找不到原条目「" + target.Title
                + "」（可能刚被别处改过 —— 请关掉菜单重开再试，本次未改动任何文件）";
            return false;
        }

        // ============================================================
        // ⑤ 重建（旧 HMENU / 旧 IdMap 一律作废）
        // ============================================================

        /// <summary>
        /// 动作之后重建菜单：重新解析磁盘 + 全新 <see cref="MenuBuilder.Build"/>（新 HMENU、新 IdMap）。
        /// ★ 绝不复用动作前的句柄 —— 旧 IdMap 的命令 ID 与旧菜单一一对应，改过配置后必然错位。
        /// </summary>
        public static MenuBuilder.BuildResult RebuildFromFile(string confPath)
        {
            var p = new ConfParser(confPath);
            p.Parse();
            return MenuBuilder.Build(p.Groups, null);
        }

        // ============================================================
        // ⑥ 测试缝：不弹任何菜单，直接走"已关菜单 → 输入 → 写盘"这一段
        //    （自检用它证明"输入回调一定发生在菜单关闭之后"这条结构约束）
        // ============================================================

        /// <summary>
        /// 模拟一次"右键 → 选动作 → 主菜单已关"的后续动作（不弹菜单）。
        /// 与真实链路走**同一份** <see cref="RunPending"/>，因此能覆盖输入回调 + 写盘 + 结果记录。
        /// </summary>
        public MenuItemContextResult ExecuteDeferred(MenuItem item, MenuItemAction action)
        {
            if (item == null) return MenuItemContextResult.Fail("条目为空");
            var pending = new PendingAction
            {
                Item = MenuItemRef.From(item),
                Action = action,
                Title = item.Title
            };
            RunPending(pending);
            return LastResult ?? MenuItemContextResult.Fail("没有结果");
        }

        /// <summary>动作 → 中文标签（日志与二级菜单文案同源）。</summary>
        public static string ActionLabel(MenuItemAction a)
        {
            switch (a)
            {
                case MenuItemAction.EditTarget: return "编辑目标";
                case MenuItemAction.Rename: return "重命名";
                case MenuItemAction.Delete: return "删除";
                default: return "无动作";
            }
        }

        /// <summary>把一份 groups 写进文本（自检造沙箱夹具用；与产品写盘无关）。</summary>
        internal static void WriteSandbox(string path, string text, bool withBom)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, text, new UTF8Encoding(withBom));
        }
    }
}
