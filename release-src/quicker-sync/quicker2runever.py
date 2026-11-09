# -*- coding: utf-8 -*-
"""Quicker 右键菜单动作 config -> RunEver menus.conf 转换器（一次性工具，非交付脚本）。"""
import re, sys, io, os

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
    # ★ 目标为空 ⇒ 一律 group（Quicker 里就是「纯子菜单标题」，无动作）。
    #   判定放在「算出 target 之后」，口径只有一处，覆盖全部空目标形态：
    #     val==""     值空                 （如   PCTools|   ）
    #     val=="\t"   字面量转义            （Quicker 导出里的空动作写法）
    #     val=="^"    只有「程序^参数」的分隔符、程序为空（如   清理工具|^   ）
    #     val=="动作=" 动作 ID 为空
    #   早先只认前两种，`清理工具|^` 会落到 else 分支、target 为空串，
    #   产出「非 group 父条目 + 空目标 + 带子项」的空节点（F2 的成因）。
    if val == "\\t":
        val = ""

    if val.startswith("动作="):
        action = val[len("动作="):].strip()
        target = ("quicker:runaction:" + action) if action else ""
        act = "url"
        stat_key = "act"
    elif SCHEME_RE.match(val):
        target = val
        act = "url"
        stat_key = "url"
    else:
        if "^" in val:
            prog, args = val.split("^", 1)
            target = (prog.strip() + " " + args.strip()).strip()
        else:
            target = val.strip()
        act = "run"
        stat_key = None

    if target == "":
        out.append("%s%s  | group" % (indent, nm))
        stats["item"] += 1
        continue

    if stat_key:
        stats[stat_key] += 1

    key = ("  _%s  " % mn) if mn else "  "
    out.append("%s%s%s| %s %s" % (indent, nm, key, act, target))
    stats["item"] += 1

io.open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
print("wrote", OUT)
print("stats", stats)

# ---- 2) 可选：合并版（--merged）—— 把多余的 always 组收成首个 always 组下的子菜单 ----
# 忠实版保留 3 个 always 组会违反 RunEver 契约 I-3（预设四类各至多一个），
# 合并版把「个人工具箱」「游戏」降级成 `| group` 子菜单，分组数 17 -> 15。
if "--merged" in sys.argv:
    body = [ln for ln in out if not ln.startswith("#")]
    while body and body[0].strip() == "":
        body.pop(0)

    blocks = []
    for ln in body:
        if ln.startswith("!"):
            blocks.append((ln, []))
        elif ln.strip():
            blocks[-1][1].append(ln)

    always = [i for i, b in enumerate(blocks) if b[0].rstrip().endswith(" always")]
    host, extras = always[0], always[1:]

    merged = []
    for i, (head, items) in enumerate(blocks):
        if i in extras:
            continue
        merged.append(head)
        merged.extend(items)
        if i == host:
            for j in extras:
                merged.append("  %s  | group" % re.split(r"\s{2,}", blocks[j][0][1:].strip())[0])
                merged.extend("  " + ln for ln in blocks[j][1])
            merged.append("")          # 与既有合并版一致：折叠块后留一空行

    mout = os.path.join(os.path.dirname(OUT), "menus.quicker.merged.conf")
    io.open(mout, "w", encoding="utf-8", newline="\n").write("\n".join(merged) + "\n")
    print("wrote", mout, "(blocks=%d, always=%d)" % (len(blocks), len(always)))
