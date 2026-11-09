using System;
using System.Collections.Generic;

namespace RunEverCs.Models
{
    /// <summary>分组类型（对应 AHK ConfParser 的 6 种预设）。</summary>
    public enum GroupType
    {
        Folder,
        Ext,
        Proc,
        Text,
        Always,
        Fallback
    }

    /// <summary>动作类型（对应执行器的 7 种）。</summary>
    public enum ActionType
    {
        Group,
        Run,
        Open,
        Url,
        Action,
        Send,
        Keys
    }

    /// <summary>action 的显示/隐藏标记。</summary>
    public enum ActionFlag
    {
        /// <summary>未声明 —— 默认隐藏（存量行为）。</summary>
        None,
        Hide,
        Show
    }

    /// <summary>菜单条目（对应 AHK ParseItemLine 产出的 Map）。</summary>
    public class MenuItem
    {
        public string Title { get; set; } = "";
        public string Hotkey { get; set; } = "";
        public ActionType Type { get; set; } = ActionType.Group;
        public ActionFlag Flag { get; set; } = ActionFlag.None;

        /// <summary>
        /// `open sub &lt;目录&gt;` 的展开标记（2026-11-09 / U-B）。
        /// ★ 只是**渲染期**语义：子项当场从目录枚举，绝不写回 menus.conf；
        ///   目录不存在 / 无权限 / 为空 ⇒ 渲染层退化为普通 open（见 MenuBuilder）。
        /// 与 Flag 是两个正交的轴（hide/show 控制执行窗口，Sub 控制菜单形态）。
        /// </summary>
        public bool Sub { get; set; }

        public string Target { get; set; } = "";

        /// <summary>缩进层级（0 = 顶层）。</summary>
        public int Level { get; set; }

        /// <summary>源文件行号（1-based，用于回写定位与报错）。</summary>
        public int Line { get; set; }

        /// <summary>运行期解析出的图标源（不进配置文件）。</summary>
        public string IconSource { get; set; } = "";

        public bool IsGroupHeader => Type == ActionType.Group;

        public MenuItem Clone()
        {
            return new MenuItem
            {
                Title = Title,
                Hotkey = Hotkey,
                Type = Type,
                Flag = Flag,
                Sub = Sub,
                Target = Target,
                Level = Level,
                Line = Line,
                IconSource = IconSource
            };
        }
    }

    /// <summary>菜单分组（对应 AHK 的 Group Map）。</summary>
    public class MenuGroup
    {
        public string Name { get; set; } = "";
        public GroupType Type { get; set; } = GroupType.Always;

        /// <summary>关键词（ext 扩展名 / proc 进程名；其他类型为空）。</summary>
        public List<string> Keywords { get; set; } = new List<string>();

        public List<MenuItem> Items { get; set; } = new List<MenuItem>();

        /// <summary>源文件行号（分组行）。</summary>
        public int Line { get; set; }

        public override string ToString()
        {
            return Name + "  (" + TypeToString(Type) + ")";
        }

        public static string TypeToString(GroupType t)
        {
            switch (t)
            {
                case GroupType.Folder: return "folder";
                case GroupType.Ext: return "ext";
                case GroupType.Proc: return "proc";
                case GroupType.Text: return "text";
                case GroupType.Always: return "always";
                case GroupType.Fallback: return "fallback";
                default: return "always";
            }
        }

        public static bool TryParseType(string s, out GroupType t)
        {
            t = GroupType.Always;
            if (string.IsNullOrEmpty(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "folder": t = GroupType.Folder; return true;
                case "ext": t = GroupType.Ext; return true;
                case "proc": t = GroupType.Proc; return true;
                case "text": t = GroupType.Text; return true;
                case "always": t = GroupType.Always; return true;
                case "fallback": t = GroupType.Fallback; return true;
                default: return false;
            }
        }
    }
}
