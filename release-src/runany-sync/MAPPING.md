# RunAny → RunEver 配置映射

> 目的：把 RunAny 的配置口径同步成 RunEver 的 `menus.conf` 口径。
> 原文快照见同目录 `RunAny-builtin-search.ini`，转换结果见 `menus.runany-sync.conf`。
> 来源：<https://hui-zz.github.io/RunAny/>（抓取 2026-11-07）

## 一、两种格式对照

| 概念 | RunAny（`RunAny.ini`） | RunEver（`menus.conf`） |
|---|---|---|
| 分类 | `-视频(&V)`（一个 `-` 一级，`--` 二级） | `! 分组名  <类型>` |
| 分类的层级 | 名字前连字符个数 | 行首缩进 2 空格 = 层级 +1 |
| 分类出现条件 | 名字后 `\|` 接 `text` / `file` / `public` / `ext` 列表 / `.exe` | 分组类型：`folder` / `ext ...` / `proc ...` / `text` / `always` / `fallback` |
| 条目 | `名称\|目标` | `名称  [_助记符]  \| <动作>  目标` |
| 选中文本占位 | `%s`（不写则自动追加到末尾） | `%s`（前有选中文本用文本，否则用剪贴板）**语义一致** |
| 菜单助记符 | 名称里的 `&X` | 名称后的 `_x`（Alt+X） |
| 全局热键 | 名称后 Tab + `!b` 这类 AHK 写法 | 无对应物（RunEver 只有一个全局热键 `hotkey`） |
| 分割线 | 单独一行 `--` | `AddSeparator()`，配置文件里无语法 |
| 注释 | `;` 开头 | `#` 开头 |

## 二、逐字段转换规则

1. **分类 → 分组**：RunAny 里带 `|text` 或无后缀但只在选中文字时用的搜索类 → RunEver `text` 组；
   带 `|file` / `|public` → `folder`；带扩展名列表 → `ext <后缀…>`；其余 → `always`。
2. **条目 → 条目**：`名称|目标` → `  名称  | <动作>  目标`。
3. **动作判定**：
   - 目标是 `http://` / `https://` / 其它协议（`tencent://` 等） → `url`
   - 目标是 `.exe` 或可执行程序名 → `run`（走路径缓存解析，**不写绝对路径**，这是 RunEver 的核心差异）
   - 目标是文件/文件夹路径 → `open`
4. **`%s` 原样保留**：语义一一对应，不需要改写。
   原文里**没有** `%s` 的条目（RunAny 会把它追加到末尾），转换时补在末尾，例如
   `百度指数|…?words=` → `url …?words=%s`。
5. **`&X` → `_x`**：只取字母、转小写。同一分组内助记符重复时，后出现的丢弃助记符。
6. **协议占位符不转**：`聊天|tencent://message/?uin=QQ号…` 里的 `QQ号` 是用户自己的号，
   属个人数据，转换时**丢弃该条目**，不写进发布配置。

## 三、RunEver 侧动作词表（`ConfParser.TryParseActionType`）

`run` / `open` / `url` / `action` / `send` / `keys` / `group`

## 四、无法同步的部分（明确不支持，不要假装转了）

| RunAny 功能 | 为什么不能转 |
|---|---|
| 全局热键（名称后 Tab + `!b`） | RunEver 只有 `auto.conf` 里一个全局 `hotkey`，不支持给单个条目绑全局热键 |
| 热字符串（`:*X:#QQ\|QQ.exe`） | RunEver 无热字符串引擎 |
| 热键映射（`LShift & CapsLock\|{Delete}::`） | 同上，属于 AHK 层的输入重映射 |
| 短语输出（`…;` 结尾，含 `%A_YYYY%` 等 AHK 变量） | RunEver 的 `send` 只支持 `%s %text% %file% %clip% %win% %proc%`，没有日期时间变量 |
| 规则启动 / 正则一键直达 / 批量搜索 | 无对应动作类型 |
| 三键启动（`\` a w`） | RunEver 是右键菜单 + 单热键模型，没有「热键→分类字母→条目字母」两级定位 |

## 五、复现方式

1. 原文快照已在 `RunAny-builtin-search.ini`，可直接粘回 RunAny 使用，也可作为再转换的输入。
2. 转换结果 `menus.runany-sync.conf` 可直接整段并入 `config\menus.conf`；
   注意它只含 `! 选中文本  text` 一个分组，若现有配置已有同名 text 组，二选一合并。
3. RunAny 版本升级后内置库若有增删，按本文件第二节的规则重转即可。
