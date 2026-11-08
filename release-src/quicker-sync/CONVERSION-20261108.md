# Quicker → RunEver 重新转换（2026-11-08）

- 源：Quicker 导出的动作定义 `1.txt`（16,898B，SHA256 `05331a6af0b46a75…f33b8541`）
  ＝ 本目录 `quicker-menu-source.txt`（同一文件，哈希已核）
- 转换器：`quicker2runever.py`（本次只改了 `group_type()`）
- 产物：
  - `menus.quicker-sync.conf`（忠实版，17 组 / 128 条 / 内置动作 37 条）
  - `menus.quicker-sync.merged.conf`（合并版，15 组 / always 合成 1 组）

## 修的是什么

旧 `group_type()` 把 `^pubic` 与 `^tips` 一起判成 `always`，导致：

- **通用菜单**（Quicker 的兜底分组，49 条）变成常驻 always；
- 配置里**没有任何 fallback** ⇒ 无选中时动态槽位恒空（运行日志 `dynamic=0`），
  渲染层把空块整块跳过 ⇒ **进程菜单永远排第一**，排序设置看起来"怎么改都不动"；
- always 组数变成 4（通用菜单/永久显示/个人工具箱/游戏）⇒ 违反契约 I-3（预设四类各至多一个），
  运行日志里 `违规=1`。

## 正确映射（用户 2026-11-08 口径）

| Quicker 分组后缀 | RunEver 类型 | 说明 |
|---|---|---|
| `^pubic`（通用菜单） | `fallback` | 兜底：没命中任何动态分组时出现 |
| `^tips`（永久显示） | `always` | 每次弹出都在 |
| `^search`（文本菜单） | `text` | 选中文本时出现 |
| `^folder` | `folder` | 文件夹 |
| `^.<扩展名>…` | `ext …` | 扩展名分组 |
| `^名称(进程)` | `proc …` | 进程分组 |
| `^`（空后缀） | `always` | Quicker 里就是无条件分组 |

## 验证（本机构造，权威源 = RunEver 自己的解析器/判定器）

```
RunEverCs.exe --dump-parser <conf> <out>      # 忠实版/合并版：17 组 / 15 组，均 0 错
RunEverCs.exe --dump-order  <root> <out> --conf=<conf> --ctx=proc:QQ.exe --ctx=text --ctx=file:pdf
```

| 配置 | R6（QQ 前台） | 违规 |
|---|---|---|
| 忠实版 | 1.兜底 通用菜单(49) → 2.**进程 聊天(2)** → 3.永久显示(12) → 4.个人工具箱(5) → 5.游戏(2) | 1（always×3） |
| 合并版 | 1.兜底 通用菜单(49) → 2.**进程 聊天(2)** → 3.永久显示(21) | **0** |

`ctx=text`：文本菜单(21) 在最前；`ctx=file:pdf`：PDF(3) 在最前 —— 动态段正常占位。

## 遗留

`个人工具箱^=` 与 `游戏^=` 在 Quicker 里没有条件 ⇒ 只能是 `always`。
忠实版因此保留 3 个 always 组（违反 I-3、菜单出现 3 个固定块）；
合并版把两者收成 `永久显示` 下的子菜单（`  XXX  | group`）才合规。
二选一，文件都已放到 PC 桌面 `RunEver-quicker2\`。
