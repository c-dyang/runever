using System.Collections.Generic;
using System.Windows;
using RunEverCs.Models;
using MItem = RunEverCs.Models.MenuItem;

namespace RunEverCs.Views
{
    public partial class MenuEditorPage
    {
        // ============================================================
        // 列表行包装（DataGrid 绑定用）
        // ============================================================
        private class GroupRow
        {
            /// <summary>
            /// ★ 必须是**属性**，不能是 public 字段：WPF 绑定只认 public 属性，
            ///   绑到字段时**静默取空**（无异常，错误只出现在 trace 里）——
            ///   模板里 `Binding("Group.Name")` 曾因此让左栏第一行整片空白（B1-1）。
            ///   MS Learn: https://learn.microsoft.com/en-us/answers/a/1168021
            /// </summary>
            public MenuGroup Group { get; private set; }

            public GroupRow(MenuGroup g) { Group = g; }

            /// <summary>分组依据显示：类型 + 关键词（否则"显示不全"）。</summary>
            public string Basis
            {
                get
                {
                    string t = MenuGroup.TypeToString(Group.Type);
                    if ((Group.Type == GroupType.Ext || Group.Type == GroupType.Proc)
                        && Group.Keywords.Count > 0)
                        return t + ": " + string.Join(" ", Group.Keywords);
                    return t;
                }
            }

            public string CountText
            {
                get { return Group.Items.Count + " 项"; }
            }

            public override string ToString()
            {
                return Group.Name + "   (" + Basis + ")";
            }
        }

        /// <summary>
        /// ★ v9 树节点（TreeView 绑定用）：包一个 `MItem` + 子节点集合。
        /// 父子结构 = 菜单层级；`Children` 是 `HierarchicalDataTemplate.ItemsSource` 的来源。
        /// （原 `ItemRow` 是 DataGrid 的扁平行 —— 缩进只是视觉效果，已废弃。）
        /// </summary>
        internal class NodeRow : System.ComponentModel.INotifyPropertyChanged
        {
            private readonly MenuEditorPage _page;
            public readonly MItem Item;
            private readonly List<NodeRow> _children = new List<NodeRow>();

            /// <summary>
            /// 子节点集合（`HierarchicalDataTemplate.ItemsSource` 的来源）。
            /// ★ 必须是**属性**：WPF 数据绑定**不认字段**（只认属性）——
            ///   踩过：写成 `public readonly List&lt;NodeRow&gt; Children;` 字段时，
            ///   树**只渲染根节点**、子项静默丢失（无异常、无提示，绑定错误只在 trace 里）。
            /// </summary>
            public List<NodeRow> Children { get { return _children; } }

            /// <summary>父节点（null = 根层）；构建/搬家时维护，用于展开祖先与子树判定。</summary>
            public NodeRow Parent;

            /// <summary>
            /// 层级引导符（2026-11-07 树形可见性）：WPF 原生 TreeViewItem 只靠左移 19px
            /// 表达层级、没有连线，用户反馈「树形显示实际也不是特别明显」。
            /// 子项加 `└ ` 前缀 + 原生缩进 ⇒ 一眼能分层。
            /// ★ 必须是属性（WPF 绑定不认字段）；拖拽重建树时会重新求值，无需通知。
            /// </summary>
            public string Prefix
            {
                get { return Parent == null ? "" : "└ "; }
            }

            public NodeRow(MenuEditorPage page, MItem it)
            {
                _page = page;
                Item = it;
            }

            /// <summary>
            /// 展开态（双向绑定到 `TreeViewItem.IsExpanded`）。
            /// 折叠账本按 `MItem` 记账 ⇒ 树重建后折叠状态不丢（纯视图态，不写进配置）。
            /// </summary>
            public bool IsExpanded
            {
                get { return !_page._collapsed.Contains(Item); }
                set
                {
                    if (value) _page._collapsed.Remove(Item);
                    else _page._collapsed.Add(Item);
                    Raise("IsExpanded");
                }
            }

            private bool _selected;

            /// <summary>
            /// 选中态。
            /// ★ 2026-11-07 换成 ItemsControl 后必须发通知：TreeView 靠容器的双向绑定回写，
            ///   ItemsControl 没有这层，DataTrigger 只有收到 PropertyChanged 才会重新求值。
            /// </summary>
            public bool IsSelected
            {
                get { return _selected; }
                set { if (_selected != value) { _selected = value; Raise("IsSelected"); } }
            }

            private bool _marked;

            /// <summary>
            /// ★ C2 多选标记（Ctrl / Shift 选中的那批，浅蓝底）。
            /// 必须与 <see cref="IsSelected"/> 分开：TreeView 的选中是**单选**的，
            /// 拿它表达多选会被控件强制互斥（点第二条第一条就灭了）。
            /// </summary>
            public bool IsMarked
            {
                get { return _marked; }
                set { if (_marked != value) { _marked = value; Raise("IsMarked"); } }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            private void Raise(string name)
            {
                var h = PropertyChanged;
                if (h != null) h(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }

            public string Title
            {
                get
                {
                    // 分割线没有标题：给一个可辨认的占位，否则树里是一行空白（点了也不知道点了啥）
                    if (Item.Type == ActionType.Separator) return "——— 分割线 ———";
                    return Item.Title;
                }
            }

            /// <summary>组头（`group` 类型 = 纯分组标题）加粗，与普通条目区分。</summary>
            public FontWeight TitleWeight
            {
                get { return Item.Type == ActionType.Group ? FontWeights.Bold : FontWeights.Normal; }
            }

            /// <summary>
            /// 标题后的灰色 meta：`_快捷键 · 动作 · 目标`。
            /// 树节点随深度整体右移 ⇒ 分列对齐本就无意义，故压成一行（信息不丢、更紧凑）。
            /// </summary>
            public string Meta
            {
                get
                {
                    var parts = new List<string>();
                    if (Item.Type == ActionType.Separator)
                    {
                        parts.Add("separator（menus.conf 里写作一行 ---）");
                        return "  " + string.Join("  ·  ", parts.ToArray());
                    }
                    if (!string.IsNullOrEmpty(Item.Hotkey)) parts.Add("_" + Item.Hotkey);
                    parts.Add(Item.Type == ActionType.Group ? "group" : TypeStr(Item.Type));
                    if (!string.IsNullOrEmpty(Item.Target)) parts.Add(Item.Target);
                    return "  " + string.Join("  ·  ", parts.ToArray());
                }
            }
        }

    }
}
