using System;
using System.Collections.Generic;

namespace RunEverCs.Services
{
    /// <summary>
    /// 触发热键规格 —— 把 AHK 风格的写法解析成 Win32 RegisterHotKey 的参数。
    ///
    /// 存放位置：`config\auto.conf` 的 `hotkey=` 键（与 AHK 的全局 RVE_HOTKEY 同语义，
    /// 但 AHK 版写死在源码里、无法设置 —— C# 版做成配置项，设置界面可改）。
    ///
    /// 支持范围（够用即止，不追 AHK 全语法）：
    ///   · 单键      : ` - = [ ] \ ; ' , . / 0-9 A-Z
    ///   · 具名键    : Space Tab Enter Esc Backspace Insert Delete Home End PgUp PgDn Up Down Left Right
    ///   · 功能键    : F1 .. F24
    ///   · 修饰符前缀: ^ = Ctrl   ! = Alt   + = Shift   # = Win（可叠加，如 ^!x）
    ///
    /// ★ 有意拒绝「裸字母/裸数字」：那会把正常打字全吞掉（按 a 就弹菜单），
    ///   报错让用户加修饰符或改用 F 键/` 之类的非输入键。
    /// </summary>
    public class HotkeySpec
    {
        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000;

        /// <summary>修饰符掩码（不含 MOD_NOREPEAT）。</summary>
        public uint Modifiers { get; private set; }

        /// <summary>虚拟键码。</summary>
        public uint Vk { get; private set; }

        /// <summary>规范化后的显示文本（如 "^!A" / "`" / "F8"）。</summary>
        public string Text { get; private set; }

        /// <summary>默认热键 = 反引号（AHK 版 RVE_HOTKEY 的原值）。</summary>
        public static HotkeySpec CreateDefault()
        {
            HotkeySpec s;
            string err;
            TryParse("`", out s, out err);
            return s;
        }

        public static bool TryParse(string raw, out HotkeySpec spec, out string err)
        {
            spec = null;
            err = "";

            if (raw == null || raw.Trim().Length == 0)
            {
                err = "热键不能为空";
                return false;
            }

            string s = raw.Trim();
            uint mods = 0;
            int i = 0;

            // ---- 修饰符前缀 ----
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '^') mods |= MOD_CONTROL;
                else if (c == '!') mods |= MOD_ALT;
                else if (c == '+') mods |= MOD_SHIFT;
                else if (c == '#') mods |= MOD_WIN;
                else break;
                i++;
            }

            string key = s.Substring(i);
            if (key.Length == 0)
            {
                err = "只有修饰符、没有主键（例如 ^ 后面还要跟一个键）";
                return false;
            }

            uint vk;
            bool bareAlnum = false;

            if (key.Length == 1 && !NamedKeys.ContainsKey(key.ToUpperInvariant()))
            {
                char c = key[0];
                if (!TryMapSingleChar(c, out vk))
                {
                    err = "不支持的按键：" + key;
                    return false;
                }
                bareAlnum = (mods == 0) && IsAlnum(c);
            }
            else
            {
                string name = key.ToUpperInvariant();
                if (!NamedKeys.TryGetValue(name, out vk))
                {
                    err = "不认识的键名：" + key
                        + "（可用：F1-F24 / Space / Tab / Enter / Esc / Home / End / PgUp / PgDn / Up / Down / Left / Right / Insert / Delete）";
                    return false;
                }
            }

            if (bareAlnum)
            {
                err = "裸字母/数字会吞掉正常输入（按 " + key + " 就弹菜单）——请加修饰符（如 ^"
                    + key + "）或改用 F1-F24 / ` 这类非输入键";
                return false;
            }

            spec = new HotkeySpec
            {
                Modifiers = mods,
                Vk = vk,
                Text = Normalize(mods, key)
            };
            return true;
        }

        /// <summary>规范化显示：修饰符按 ^ ! + # 顺序，主键字母统一大写。</summary>
        private static string Normalize(uint mods, string key)
        {
            string head = "";
            if ((mods & MOD_CONTROL) != 0) head += "^";
            if ((mods & MOD_ALT) != 0) head += "!";
            if ((mods & MOD_SHIFT) != 0) head += "+";
            if ((mods & MOD_WIN) != 0) head += "#";

            string k = key;
            if (k.Length == 1 && char.IsLetter(k[0])) k = k.ToUpperInvariant();
            else if (k.Length > 1) k = k.ToUpperInvariant();

            return head + k;
        }

        private static bool IsAlnum(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        }

        /// <summary>单字符 → 虚拟键码（US 布局 OEM 键）。</summary>
        private static bool TryMapSingleChar(char c, out uint vk)
        {
            vk = 0;

            if (c >= 'a' && c <= 'z') { vk = (uint)(c - 32); return true; }
            if (c >= 'A' && c <= 'Z') { vk = (uint)c; return true; }
            if (c >= '0' && c <= '9') { vk = (uint)c; return true; }

            switch (c)
            {
                case '`': vk = 0xC0; return true;   // VK_OEM_3（反引号/波浪号）
                case '-': vk = 0xBD; return true;   // VK_OEM_MINUS
                case '=': vk = 0xBB; return true;   // VK_OEM_PLUS
                case '[': vk = 0xDB; return true;   // VK_OEM_4
                case ']': vk = 0xDD; return true;   // VK_OEM_6
                case '\\': vk = 0xDC; return true;  // VK_OEM_5
                case ';': vk = 0xBA; return true;   // VK_OEM_1
                case '\'': vk = 0xDE; return true;  // VK_OEM_7
                case ',': vk = 0xBC; return true;   // VK_OEM_COMMA
                case '.': vk = 0xBE; return true;   // VK_OEM_PERIOD
                case '/': vk = 0xBF; return true;   // VK_OEM_2
                default: return false;
            }
        }

        private static readonly Dictionary<string, uint> NamedKeys =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            {"SPACE", 0x20}, {"TAB", 0x09}, {"ENTER", 0x0D}, {"RETURN", 0x0D},
            {"ESC", 0x1B}, {"ESCAPE", 0x1B}, {"BACKSPACE", 0x08}, {"BS", 0x08},
            {"INSERT", 0x2D}, {"INS", 0x2D}, {"DELETE", 0x2E}, {"DEL", 0x2E},
            {"HOME", 0x24}, {"END", 0x23}, {"PGUP", 0x21}, {"PGDN", 0x22},
            {"UP", 0x26}, {"DOWN", 0x28}, {"LEFT", 0x25}, {"RIGHT", 0x27},
            {"F1", 0x70}, {"F2", 0x71}, {"F3", 0x72}, {"F4", 0x73},
            {"F5", 0x74}, {"F6", 0x75}, {"F7", 0x76}, {"F8", 0x77},
            {"F9", 0x78}, {"F10", 0x79}, {"F11", 0x7A}, {"F12", 0x7B},
            {"F13", 0x7C}, {"F14", 0x7D}, {"F15", 0x7E}, {"F16", 0x7F},
            {"F17", 0x80}, {"F18", 0x81}, {"F19", 0x82}, {"F20", 0x83},
            {"F21", 0x84}, {"F22", 0x85}, {"F23", 0x86}, {"F24", 0x87}
        };

        /// <summary>纯键名显示（设置页用）：Ctrl+Alt+A / 反引号 / 空格 / F8，不带调试尾巴。</summary>
        public string ToDisplay()
        {
            string m = "";
            if ((Modifiers & MOD_CONTROL) != 0) m += "Ctrl+";
            if ((Modifiers & MOD_ALT) != 0) m += "Alt+";
            if ((Modifiers & MOD_SHIFT) != 0) m += "Shift+";
            if ((Modifiers & MOD_WIN) != 0) m += "Win+";

            string k = Text;
            foreach (char c in "^!+#")
                if (k.StartsWith(c.ToString())) k = k.Substring(1);
            return m + HumanizeKey(k);
        }

        /// <summary>AHK 主键 → 人话（设置页显示用）。</summary>
        public static string HumanizeKey(string key)
        {
            if (key == "`") return "反引号";
            switch ((key ?? "").ToUpperInvariant())
            {
                case "SPACE": return "空格";
                case "TAB": return "Tab";
                case "ENTER": case "RETURN": return "回车";
                case "ESC": case "ESCAPE": return "Esc";
                case "BACKSPACE": case "BS": return "退格";
                case "INSERT": case "INS": return "Insert";
                case "DELETE": case "DEL": return "Delete";
                case "PGUP": return "PgUp";
                case "PGDN": return "PgDn";
                case "UP": return "↑";
                case "DOWN": return "↓";
                case "LEFT": return "←";
                case "RIGHT": return "→";
            }
            return key;
        }

        /// <summary>人话键名 → AHK 主键（HumanizeKey 的反函数）。</summary>
        public static string DehumanizeKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (key == "反引号") return "`";
            switch (key)
            {
                case "空格": return "Space";
                case "回车": return "Enter";
                case "退格": return "Backspace";
                case "↑": return "Up";
                case "↓": return "Down";
                case "←": return "Left";
                case "→": return "Right";
            }
            return key;
        }

        /// <summary>
        /// 设置页输入解析：先按 AHK 写法（^!a），再按键名写法（Ctrl+Alt+A / 反引号 / 空格）。
        /// 两条都失败则返回 false（调用方负责还原显示成当前生效值）。
        /// </summary>
        public static bool TryParseDisplay(string raw, out HotkeySpec spec, out string err)
        {
            spec = null;
            if (TryParse(raw, out spec, out err)) return true;

            string s = (raw ?? "").Trim();
            if (s.Length == 0) { err = "热键不能为空"; return false; }

            uint mods = 0;
            bool changed = true;
            while (changed)
            {
                changed = false;
                string[][] pre = {
                    new[] { "Ctrl+", "^" }, new[] { "Control+", "^" },
                    new[] { "Alt+", "!" }, new[] { "Shift+", "+" }, new[] { "Win+", "#" }
                };
                foreach (var p in pre)
                {
                    if (s.StartsWith(p[0], StringComparison.OrdinalIgnoreCase))
                    {
                        mods |= p[1] == "^" ? MOD_CONTROL
                              : p[1] == "!" ? MOD_ALT
                              : p[1] == "+" ? MOD_SHIFT : MOD_WIN;
                        s = s.Substring(p[0].Length);
                        changed = true;
                        break;
                    }
                }
            }

            string head = "";
            if ((mods & MOD_CONTROL) != 0) head += "^";
            if ((mods & MOD_ALT) != 0) head += "!";
            if ((mods & MOD_SHIFT) != 0) head += "+";
            if ((mods & MOD_WIN) != 0) head += "#";
            return TryParse(head + DehumanizeKey(s), out spec, out err);
        }

        /// <summary>人话描述，用于托盘「状态…」与日志。</summary>
        public string Describe()
        {
            string m = "";
            if ((Modifiers & MOD_CONTROL) != 0) m += "Ctrl+";
            if ((Modifiers & MOD_ALT) != 0) m += "Alt+";
            if ((Modifiers & MOD_SHIFT) != 0) m += "Shift+";
            if ((Modifiers & MOD_WIN) != 0) m += "Win+";

            string k = Text;
            foreach (char c in "^!+#")
                if (k.StartsWith(c.ToString())) k = k.Substring(1);

            return m + (k == "`" ? "` （反引号）" : k)
                + "  [VK=0x" + Vk.ToString("X2") + " 修饰符=0x" + Modifiers.ToString("X") + "]";
        }
    }
}
