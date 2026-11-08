#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
quicker2menus.py —— 把 Quicker 动作导出的条目结构 dump 转成 RunEver 的 menus.conf

输入：docs\条目结构解析.txt（每行形如 ` 12 ITEM  ind=0 | [url:...]标题(_X)|目标^参数`）
输出：config\menus.<名字>.conf（可直接改名成 menus.conf 使用；**不覆盖任何现役文件**）
      docs\quicker-转换报告.md（丢弃项 / 需人工确认项清单）

转换规则
  · 分组行：`名称^<标记>=` → `! 名称  <类型>  [关键词...]`
      标记 folder / pubic|public / text / 含 .exe 或名字带(进程) → proc / 以 . 开头的后缀串 → ext / 空 → always
  · 条目行：`[icon]标题(_X)|目标^参数` → `  <缩进><标题>  _X  | <动作>  <目标 参数>`
      目标 http(s) → url；目标是路径（含 \\ 或盘符）→ 存在则 open、否则 run；
      其它（裸程序名）→ run；目标为空 → group（子菜单标题）
  · 丢弃：图标标签 [url:...] / [fa:...]、`动作=UUID`（Quicker 内置动作，不可移植）
  · 缩进：条目字符串里前导 \\t 的个数 n → 层级 n+1 → 缩进 2*(n+1) 个空格
"""
import os
import re
import sys
import io

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))   # ...\runever
SRC = os.path.join(ROOT, "docs", "条目结构解析.txt")
OUT = os.path.join(ROOT, "config", "menus.quicker.conf")
REP = os.path.join(ROOT, "docs", "quicker-转换报告.md")

ICON = re.compile(r"\[(?:url|fa):[^\]]*\]")
NUM = re.compile(r"^\s*\d+\s+")
LINE = re.compile(r"^(GROUP|ITEM)\s+ind=(\d+)\s*\|\s*(.*)$")
HOTKEY = re.compile(r"[（(]\s*(_[^）)]+?)\s*[）)]")
ACTION_UUID = re.compile(r"^动作=")
PATHISH = re.compile(r"(^[A-Za-z]:[\\/])|(^\\\\)|(^\.{1,2}[\\/])|(\\[^\\]+$)")


def unescape(s: str) -> str:
    """dump 里是 JSON 转义（\\t \\n \\" \\\\）—— 必须先还原，否则缩进与目标都是字面量。"""
    out = []
    i = 0
    while i < len(s):
        c = s[i]
        if c == "\\" and i + 1 < len(s):
            n = s[i + 1]
            if n == "t":
                out.append("\t"); i += 2; continue
            if n == "n":
                out.append("\n"); i += 2; continue
            if n == "r":
                out.append(""); i += 2; continue
            if n in ('"', "\\", "/"):
                out.append(n); i += 2; continue
        out.append(c); i += 1
    return "".join(out)


def strip_icon(s: str) -> str:
    return ICON.sub("", s)


def take_hotkey(s: str):
    m = HOTKEY.search(s)
    if not m:
        return s.strip(), ""
    hot = m.group(1).strip()
    s = s[:m.start()] + s[m.end():]
    return s.strip(), hot


def norm_entry(raw: str):
    """条目字符串 → (层级, 标题, 快捷键, 目标, 参数)"""
    s = raw.strip()
    if s.startswith('"') and (s.endswith('"') or s.endswith('",')):
        s = s[1:]
        if s.endswith('",'):
            s = s[:-2]
        elif s.endswith('"'):
            s = s[:-1]
    s = unescape(s).rstrip(",")
    tabs = len(s) - len(s.lstrip("\t"))
    s = s.lstrip("\t").rstrip("\t")          # 结尾的 \t 是 Quicker 的层级标记残留
    s = strip_icon(s).strip().strip('"').strip()
    if "|" in s:
        title_part, target_part = s.split("|", 1)
    else:
        title_part, target_part = s, ""
    title, hot = take_hotkey(title_part)
    title = title.strip().strip('"').strip()
    target_part = target_part.strip().rstrip("\t").strip()
    args = ""
    if "^" in target_part:
        target, args = target_part.split("^", 1)
        target, args = target.strip(), args.strip()
    else:
        target = target_part
    return tabs + 1, title, hot, target, args


def group_type(name: str, marker: str):
    """Quicker 分组标记 → (我们的类型, 关键词列表)
       实测标记形态：folder / pubic / search(文本菜单) / tips(永久显示)
                     .mp3.wav.flac（点号分隔后缀）/ msedge vivaldi chrome（空格分隔进程名）
    """
    m = marker.strip()
    low = m.lower()
    if low == "folder":
        return "folder", []
    if low in ("pubic", "public"):
        return "fallback", []
    if low in ("search", "text"):
        return "text", []
    if low == "tips":
        return "always", []
    if m == "":
        return "always", []
    if m.startswith("."):
        kws = [t.lower() for t in m.split(".") if t.strip()]
        return "ext", kws
    toks = [t for t in re.split(r"[\s,]+", m) if t]
    # 空格分隔：长 token 或分组名含"进程" ⇒ 进程菜单；否则按后缀列表处理
    if "进程" in name or any(len(t) >= 4 for t in toks):
        return "proc", [t.lower() for t in toks]
    kws = [t.lower().lstrip(".") for t in toks for t in t.split(".") if t.strip(".")]
    return ("ext", kws) if kws else ("always", [])


def action_for(target: str, args: str):
    t = target.strip()
    if t == "":
        return "group", ""
    if re.match(r"^https?://", t, re.I):
        return "url", t
    if PATHISH.search(t):
        try:
            exists = os.path.exists(t)
        except Exception:
            exists = False
        if exists:
            return "open", t
        return "run", join_cmd(t, args)
    # 裸程序名 / 其它
    return "run", join_cmd(t, args)


def join_cmd(t: str, args: str) -> str:
    """裸程序名含空格 ⇒ 加引号（执行器按 SplitWhitespace 切首个 token，不加引号会被截断）。"""
    head = t
    if " " in t and not t.startswith('"') and ("\\" not in t):
        head = '"' + t + '"'
    return (head + " " + args).strip()


def main():
    if not os.path.exists(SRC):
        print("找不到输入: " + SRC)
        return 2
    groups = []          # [(name, type, kws, [entries])]
    dropped = []         # 丢弃项
    quickrun = []        # Quicker 外部启动条目（动作=UUID → QuickerStarter.exe runaction:<id>）
    seen = set()
    cur = None

    with open(SRC, "r", encoding="utf-8") as f:
        lines = f.read().splitlines()

    pending_first_item = None
    pending = None          # 断行拼接缓冲（dump 会把长 URL 折成两行，且丢掉分隔的 &）
    for raw in lines:
        s = NUM.sub("", raw).strip()
        if not s:
            continue
        m = LINE.match(s)
        if not m:
            continue
        kind, ind, payload = m.group(1), int(m.group(2)), m.group(3).strip()

        # ---- 断行拼接：前一行是未闭合的引号串 ⇒ 与本行合并 ----
        if pending is not None:
            payload = pending + "&" + payload
            pending = None
            kind = "ITEM"
        elif payload.startswith('"') and not payload.rstrip().endswith(('"', '",')):
            pending = payload
            continue

        if kind == "GROUP":
            payload = strip_icon(payload)
            # dump 残留：极少数 GROUP 行实际是条目（无 ^ 标记、含 |）⇒ 挂到上一个分组
            if "^" not in payload and "|" in payload:
                if cur is not None:
                    _add(cur, payload, dropped, quickrun)
                    dropped.append(("dump 残留：GROUP 行实为条目（已挂到上一分组）", payload))
                continue
            head, rest = (payload.split("=", 1) + [""])[:2] if "=" in payload else (payload, "")
            name_part, marker = (head.split("^", 1) + [""])[:2] if "^" in head else (head, "")
            name = name_part.strip()
            name = re.sub(r"[（(][^）)]*[）)]\s*$", "", name).strip()   # 去尾部括号说明
            gt, kws = group_type(name, marker)
            cur = {"name": name, "type": gt, "kws": kws, "items": [], "seen": set()}
            groups.append(cur)
            # 分组行后半段可能直接跟着第一条目
            tail = rest.strip().lstrip("[").strip()
            pending_first_item = tail if ("|" in tail) else None
            if pending_first_item:
                _add(cur, pending_first_item, dropped, quickrun)
                pending_first_item = None
            continue

        if kind == "ITEM":
            if cur is None:
                dropped.append(("无归属行", payload))
                continue
            _add(cur, payload, dropped, quickrun)

    # ---- 输出 ----
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    out = []
    out.append("# RunEver 菜单配置（由 Quicker 动作条目 dump 转换）")
    out.append("# 生成脚本：tools/quicker2menus.py  源：docs/条目结构解析.txt")
    out.append("# 语法: `! 名称 类型 [关键词...]` 分组 / `标题  _快捷键  | 动作  目标` 条目（缩进 2 空格 = 层级）")
    out.append("# 动作: run 启动程序 / open 打开文件(夹) / url 网址 / action cmd / send 文本 / keys 按键 / group 子菜单")
    out.append("")
    n_items = 0
    for g in groups:
        if not g["items"]:
            continue
        kw = ("  " + " ".join(g["kws"])) if g["kws"] else ""
        out.append("! %s  %s%s" % (g["name"], g["type"], kw))
        for e in g["items"]:
            lv, title, hot, act, tgt = e
            pad = " " * (2 * lv)
            hk = ("  " + hot) if hot else ""
            out.append("%s%s%s  | %s  %s" % (pad, title, hk, act, tgt))
            n_items += 1
        out.append("")
    with open(OUT, "w", encoding="utf-8", newline="\r\n") as f:
        f.write("\n".join(out) + "\n")

    # ---- 报告 ----
    rep = []
    rep.append("# Quicker 条目 → menus.conf 转换报告")
    rep.append("")
    rep.append("- 源：`docs/条目结构解析.txt`（%d 行）" % len(lines))
    rep.append("- 产物：`config/menus.quicker.conf`（分组 %d，条目 %d）" % (len([g for g in groups if g['items']]), n_items))
    rep.append("- 丢弃合计：%d 条；**Quicker 内置动作已救回 %d 条**（转成 `run QuickerStarter.exe runaction:<动作ID>`，见下节）"
               % (len(dropped), len(quickrun)))
    rep.append("")
    rep.append("## 分组一览")
    rep.append("")
    rep.append("| 分组 | 类型 | 关键词 | 条目数 |")
    rep.append("|---|---|---|---|")
    for g in groups:
        rep.append("| %s | %s | %s | %d |" % (g["name"], g["type"], " ".join(g["kws"]) or "-", len(g["items"])))
    rep.append("")
    rep.append("## 跨组重复条目（同一程序出现在多个分组；按你的习惯决定是否删）")
    rep.append("")
    seen_map = {}
    for g in groups:
        for e in g["items"]:
            seen_map.setdefault((e[1], e[4]), []).append(g["name"])
    cross = [(k, v) for k, v in seen_map.items() if len(v) > 1]
    if cross:
        for (title, tgt), gs in cross:
            rep.append("- `%s` → `%s`（出现在：%s）" % (title, tgt, "、".join(gs)))
    else:
        rep.append("- （无）")
    rep.append("")
    rep.append("## Quicker 内置动作（已转成外部启动）")
    rep.append("")
    rep.append("> 形式：`run QuickerStarter.exe runaction:<动作ID>`。**存裸程序名**（换机免重配，由目标机解析链去找）。")
    rep.append("> ⚠️ **需要目标机装了 Quicker**（`QuickerStarter.exe` + `quicker:` 协议），否则执行器会报 `run-missing`。")
    rep.append("> 需要把选中内容传给动作时，在目标后加 `?%text%` 或 `?%file%`（见 Quicker 文档的 `runaction:标识?参数`）。")
    rep.append("")
    if quickrun:
        rep.append("| 分组 | 标题 | 动作ID |")
        rep.append("|---|---|---|")
        for gname, title, aid in quickrun:
            rep.append("| %s | %s | `%s` |" % (gname, title, aid))
    else:
        rep.append("- （无）")
    rep.append("")
    rep.append("## 丢弃项")
    rep.append("")
    for why, payload in dropped:
        rep.append("- `%s` ← %s" % (why, strip_icon(payload)[:120]))
    if not dropped:
        rep.append("- （无）")
    with open(REP, "w", encoding="utf-8") as f:
        f.write("\n".join(rep) + "\n")

    print("OK  分组=%d 条目=%d 丢弃=%d" % (len([g for g in groups if g['items']]), n_items, len(dropped)))
    print("产物: " + OUT)
    print("报告: " + REP)
    return 0


def _add(cur, payload, dropped, quickrun):
    payload = payload.strip()
    if payload in ("]", ""):
        return
    lv, title, hot, target, args = norm_entry(payload)
    mq = ACTION_UUID.match(target)
    if mq:
        # Quicker 内置动作 → 用「外部启动」形式救回来：
        #   "QuickerStarter.exe" runaction:<动作ID>
        # 存**裸程序名**（本项目"只存程序名、换机免重配"的纪律），由目标机的解析链去找它。
        uuid = re.match(r"^动作=([0-9a-fA-F-]{36})", target)
        aid = uuid.group(1) if uuid else target.split("=", 1)[1].split("?")[0].strip()
        act, tgt = "run", "QuickerStarter.exe runaction:" + aid
        quickrun.append((cur["name"], title, aid))
    else:
        if not title:
            dropped.append(("无标题", payload))
            return
        act, tgt = action_for(target, args)
    key = (title, tgt)
    if key in cur["seen"]:
        dropped.append(("同组内重复条目", payload))
        return
    cur["seen"].add(key)
    cur["items"].append((lv, title, hot, act, tgt))


if __name__ == "__main__":
    sys.exit(main())
