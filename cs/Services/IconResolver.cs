using System;
using System.IO;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 图标源解析 —— 对应 AHK RunEver.ahk 的 ResolveIconPath。
    ///
    /// 规则：
    ///   · group 类型无图标
    ///   · run 类型：取 target 首个 token 的程序全路径
    ///       - 已含路径 → 直接用
    ///       - 裸名 → 查路径缓存
    ///   · 其他类型优先用条目自带的 Icon 源，没有则返回空（渲染时走兜底图标）
    /// </summary>
    public class IconResolver
    {
        private readonly PathCache _cache;
        public IconResolver(PathCache cache) { _cache = cache; }

        public string Resolve(MenuItem item)
        {
            if (item == null) return "";
            if (item.Type == ActionType.Group) return "";

            // 条目显式指定了图标源 → 优先
            if (!string.IsNullOrEmpty(item.IconSource))
                return item.IconSource;

            if (item.Type == ActionType.Run)
            {
                string tgt = item.Target ?? "";
                var parts = ConfParser.SplitWhitespace(tgt);
                if (parts.Count < 1) return "";

                string prog = parts[0].Trim('"');
                if (prog.IndexOf('\\') >= 0 || prog.IndexOf('/') >= 0)
                    return File.Exists(prog) ? prog : "";

                if (_cache != null)
                {
                    string p = _cache.Get(prog);
                    if (!string.IsNullOrEmpty(p)) return p;
                }
                return "";
            }

            return "";
        }

        /// <summary>给一组分组里的所有条目解析图标源。</summary>
        public void ResolveAll(System.Collections.Generic.List<MenuGroup> groups)
        {
            // 第一轮：叶子条目按自身动作解析
            foreach (var g in groups)
                foreach (var it in g.Items)
                    it.IconSource = Resolve(it);

            // ★ group 标题不再在这里补图标（2026-11-08 用户口径）：
            //   子菜单行统一用**系统文件夹图标**，由 MenuBuilder 调
            //   `NativeMenu.SetFolderIconByPosition()` 直接设 —— 与"第一个子项"无关。
        }
    }
}
