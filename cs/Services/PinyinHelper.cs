/*
 * RunEver - PinyinHelper - 中文标题拼音转换（用于全库搜索的拼音匹配）
 *
 * 拷自 Switcheroo 中文版（`Switcheroo/PinyinHelper.cs`），实现保持不变：
 *   · GetFull      全拼（小写、无声调）       "哔哩哔哩" → "bilibili"
 *   · GetInitials  首字母（小写）             "哔哩哔哩" → "blbl"
 *   · 静态字符级缓存（同一汉字只转换一次）
 *   · 多音字取第一个读音（该库不处理多音字，常见场景可接受）
 *
 * 底层：Microsoft.International.Converters.PinYinConverter（微软官方，net20 兼容）
 *       随包依赖 = lib\ChnCharInfo.dll（见 RunEverCs.csproj 的 Reference）。
 *
 * ★ 只在「全库搜索」窗口打开时被调用；主菜单（Win32）与热键路径**不碰**拼音，
 *   所以这个依赖不进入按热键的耗时链路。
 * ★ 字符级缓存是进程级的：一次窗口打开后，之后每次按键的重建也不会真正做转换
 *   （窗口侧还会把整条标题的全拼/首字母预先算好，按键时只做字符串包含 —— 见
 *   Views\FullSearchWindow.cs 的 FullSearchData）。
 */

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.International.Converters.PinYinConverter;

namespace RunEverCs.Services
{
    public static class PinyinHelper
    {
        private static readonly Dictionary<char, string> Cache = new Dictionary<char, string>();
        private static readonly object LockObj = new object();

        /// <summary>已缓存的汉字数（自检/诊断用；只读）。</summary>
        public static int CachedCharCount
        {
            get { lock (LockObj) { return Cache.Count; } }
        }

        /// <summary>
        /// 全拼（小写，无声调）。非汉字：ASCII 字母数字保留，其他字符跳过。
        /// 例："哔哩哔哩" → "bilibili"
        /// </summary>
        public static string GetFull(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length * 2);
            foreach (char c in text)
            {
                sb.Append(GetCharPinyin(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 首字母（小写）。非汉字：ASCII 字母数字保留，其他字符跳过。
        /// 例："哔哩哔哩" → "blbl"
        /// </summary>
        public static string GetInitials(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                var py = GetCharPinyin(c);
                if (py.Length > 0)
                {
                    sb.Append(py[0]);
                }
                else if (char.IsLetterOrDigit(c))
                {
                    sb.Append(char.ToLowerInvariant(c));
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// 单个字符的拼音（小写，无声调）；非汉字返回空串或自身（ASCII 字母数字）。
        /// 注意：汉字（如"哔"）的 char.IsLetterOrDigit 为 true，必须先判汉字再判 ASCII，
        /// 否则汉字会被 ASCII 分支拦下返回空串、永远轮不到 PinYinConverter 转换。
        /// </summary>
        private static string GetCharPinyin(char c)
        {
            if (c >= 0x4e00 && c <= 0x9fff)
            {
                // 汉字：查拼音缓存
                lock (LockObj)
                {
                    if (Cache.TryGetValue(c, out var cached)) return cached;
                }

                string result = "";
                try
                {
                    var cc = new ChineseChar(c);
                    if (cc.Pinyins != null)
                    {
                        foreach (var p in cc.Pinyins)
                        {
                            if (!string.IsNullOrEmpty(p))
                            {
                                foreach (var ch in p)
                                {
                                    if (char.IsLetter(ch)) result += char.ToLowerInvariant(ch);
                                }
                                break; // 取第一个读音（多音字简化）
                            }
                        }
                    }
                }
                catch
                {
                    result = ""; // 转换失败按无拼音处理
                }

                lock (LockObj)
                {
                    if (!Cache.ContainsKey(c)) Cache[c] = result;
                }
                return result;
            }

            // 非汉字：ASCII 字母数字保留（小写），其他字符跳过
            if (char.IsLetterOrDigit(c) && c < 128)
            {
                return char.ToLowerInvariant(c).ToString();
            }
            return "";
        }
    }
}
