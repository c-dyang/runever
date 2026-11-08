# -*- coding: utf-8 -*-
"""Quicker 右键菜单动作 config -> RunEver menus.conf 转换器（一次性工具，非交付脚本）。"""
import re, sys, io

SRC = os.environ.get("QUICKER_SRC", "quicker-menu-source.txt")   # 原始导出（自备）
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "menus.quicker-sync.conf")

raw = io.open(SRC, "r", encoding="utf-8").read().replace("\r\n", "\n").replace("\r", "\n")
lines = raw.split("\n")

# ---- 1) 逻辑行合并：引号未闭合则续行 ----
logical = []
buf = None
for ln in lines:
    if buf is None:
        buf = ln
    else:
        buf += "\n" + ln
    if buf.count('"') % 2 == 0:
        logical.append(buf)
        buf = None
if buf is not None:
    logical.append(buf)

GROUP_RE = re.compile(r'^(?P<name>[^=]+?)\^(?P<suffix>[^=]*)=(?P<rest>.*)$')
MNEM_RE  = re.compile(r'[（(]_([A-Za-z0-9])[）)]')
ICON_RE  = re.compile(r'^\[[^\]]*\]')
SCHEME_RE= re.compile(r'^[A-Za-z][A-Za-z0-9+.\-]*://')

def norm_value(v):
    """值里的真实换行：URL 续行用 & 接（xxx=yyy 形式），其余直接去掉。"""
    if "\n" not in v:
        return v
    v = re.sub(r'\n(?=[A-Za-z0-9_.]+=)', "&", v)
    return v.replace("\n", "")

def group_type(name, suffix):
    s = suffix.strip()
    low = s.lower()
    if s == "":
        return "always"
    if low == "folder":
        return "folder"
    # ★ Quicker 的三个「默认分组」语义一一对应（用户 2026-11-08 口径）：
    #     ^pubic  通用菜单 = 兜底（没命中任何动态分组时出现）
    #     ^tips   永久显示 = always（每次都在）
    #     ^search 文本菜单 = text（选中文本时出现）
    #   早先把 pubic 也当 always 是错的 —— 那会让"兜底"变成常驻块，
    #   动态槽位在 R6 形态下永远是空的，进程菜单就会顶到第一位。
    if low in ("pubic", "public"):
        return "fallback"
    if low == "tips":
        return "always"
    if low == "search":
        return "text"            # 文本菜单 = 选中文本时出现
    if s.startswith("."):
        toks = [t for t in s.lstrip(".").split(".") if t]
        return "ext " + " ".join(toks)
    if "(进程)" in s:
        toks = s.split("(进程)", 1)[1].split()
        return "proc " + " ".join(toks)
    if any(ch.isspace() for ch in s):
        return "proc " + " ".join(s.split())
    return "always"

out = []
stats = {"group": 0, "item": 0, "act": 0, "url": 0, "sep": 0, "dropped": 0}
out.append("# ============================================================")
out.append("# RunEver 配置 · 由 Quicker 右键菜单动作配置转换而来")
out.append("# 编码: UTF-8 (无 BOM)")
out.append("#")
out.append("# 来源: 用户 PC 上 Quicker 的菜单动作配置（1.txt，144 行）")
out.append("# 转换: 分组/条目/层级/%s/助记符 一一对应；")
out.append("#       Quicker 内部动作（动作=<guid>）改走 QuickerStarter 的 URI: ")
out.append("#           url quicker:runaction:<guid>")
out.append("#       （依据 https://getquicker.net/kc/manual/doc/quicker-starter）")
out.append("#       ★ 需要 Quicker 专业版；且 Quicker 不能以管理员身份运行，否则协议不可用。")
out.append("# ============================================================")
out.append("")

cur = None   # 当前分组（用于缩进基准）
for blk in logical:
    s = blk.strip()
    if s == "" or s.startswith(";"):
        continue

    m = GROUP_RE.match(s)
    if m and ("=[" in s or s.endswith("=") or "=" in s):
        name = m.group("name").strip().strip('"')
        suffix = m.group("suffix")
        rest = m.group("rest").strip()
        # 只把「^xxx=」视为分组头：名字里不带 ^ 也不带 |（后者是「名称|目标」里目标含 = 的条目）
        if "^" not in name and "|" not in m.group("name") and "|" not in suffix:
            name = re.sub(r'[（(]默认分组禁止修改[）)]', '', name).strip()
            name = re.sub(r'[（(]进程[）)]$', '', name).strip()
            gtype = group_type(name, suffix)
            out.append("! %s  %s" % (name, gtype))
            stats["group"] += 1
            cur = name
            # `=[` 后面紧跟换行 = 数组开始；`=[url:...]名称|目标` = 单行分组（`[` 是图标括号，不能吃掉）
            if rest in ("", "["):
                continue
            s = rest   # 单行分组：把 = 后面的内容当条目继续处理
        else:
            pass

    if s in ("]",):
        continue

    body = s
    if body.endswith(","):
        body = body[:-1]
    body = body.strip()
    if body.startswith('"'):
        body = body[1:]
    if body.endswith('"'):
        body = body[:-1]
    body = body.strip()
    if body == "" or body == "]":
        continue

    level = 1
    while body.startswith("\\t"):
        level += 1
        body = body[2:].strip()
    while body.startswith("\t"):
        level += 1
        body = body[1:].strip()

    body = ICON_RE.sub("", body).strip()
    if body in ("", "]") or body == "----|" or body.startswith("----|"):
        stats["sep"] += 1
        continue

    if "|" not in body:
        stats["dropped"] += 1
        continue
    nm, val = body.split("|", 1)
    nm = nm.strip()
    val = norm_value(val).strip()

    mn = ""
    mm = MNEM_RE.search(nm)
    if mm:
        mn = mm.group(1).lower()
        nm = MNEM_RE.sub("", nm).strip()
    nm = re.sub(r'\s+', ' ', nm).strip()
    if nm == "":
        stats["dropped"] += 1
        continue

    indent = "  " * level
    # 值空 或 值为字面量 \t ⇒ Quicker 里就是「纯子菜单标题」，无动作
    if val == "" or val == "\\t":
        out.append("%s%s  | group" % (indent, nm))
        stats["item"] += 1
        continue

    if val.startswith("动作="):
        target = "quicker:runaction:" + val[len("动作="):].strip()
        act = "url"
        stats["act"] += 1
    elif SCHEME_RE.match(val):
        target = val
        act = "url"
        stats["url"] += 1
    else:
        if "^" in val:
            prog, args = val.split("^", 1)
            target = (prog.strip() + " " + args.strip()).strip()
        else:
            target = val.strip()
        act = "run"

    key = ("  _%s  " % mn) if mn else "  "
    out.append("%s%s%s| %s %s" % (indent, nm, key, act, target))
    stats["item"] += 1

io.open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
print("wrote", OUT)
print("stats", stats)
