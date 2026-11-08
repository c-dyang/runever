using System;
using System.Collections.Generic;
using RunEverCs.Models;

namespace RunEverCs.Services
{
    /// <summary>
    /// 上下文 —— 采集结果（对应 AHK 的 Collector 实例）。
    /// </summary>
    public class Context
    {
        public string Text { get; set; } = "";
        public List<string> Files { get; set; } = new List<string>();
        public string WinTitle { get; set; } = "";
        public string WinClass { get; set; } = "";
        public string Proc { get; set; } = "";

        /// <summary>剪贴板文本（供 %clip% 占位符与执行器使用）。</summary>
        public string Clipboard { get; set; } = "";

        public bool HasFolder { get; set; }

        public List<string> GetExtensions()
        {
            var exts = new List<string>();
            foreach (var f in Files)
            {
                try
                {
                    string e = System.IO.Path.GetExtension(f);
                    if (!string.IsNullOrEmpty(e))
                        exts.Add(e.TrimStart('.').ToLowerInvariant());
                }
                catch { }
            }
            return exts;
        }
    }

    // ============================================================
    // 判定：由 MatchEngine（Services\MatchEngine.cs）承担 —— 契约 §5 的 R1–R9。
    //   本文件只剩**采集结果的数据结构** Context（采集链的产物形态，多处共用），
    //   不再承载任何判定逻辑（禁止两套判定并存）。
    // ============================================================
}
