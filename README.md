# RunEver

**按一下热键，弹出一份只跟"此刻"有关的菜单。**

RunEver 是一个 Windows 右键菜单启动器：它看你当前选中了什么（文本 / 文件 / 文件夹）、前台是什么程序，
从你的条目库里筛出这一瞬该出现的那一小撮，拼成一棵**原生菜单**，点完即走。

- 不常驻窗口、不做搜索面板、不接管文件管理 —— 它只回答一个问题：**「对这个东西、这个场景，我能做什么」**
- 菜单是 Win32 原生 `TrackPopupMenu`：系统级图标、`&` 助记键、真子菜单，不自己画控件
- 宿主 .NET Framework 4.8（`LangVersion 7.3`），**零第三方运行时依赖**；采集器是编译好的 AHK exe

> 血统：从 [RunAny](https://github.com/hui-Zz/RunAny) 的思路出发重写 —— RunAny 用 AHK 单进程同时做采集/判定/菜单，
> 采集中途卡死会连带整条热键链；RunEver 把**采集**留在 AHK（进程隔离），判定、执行、设置界面全部搬进 C#。

---

## 下载即用

到 [Releases](https://github.com/c-dyang/runever/releases) 拿 `RunEver-20261108-full.zip`：

1. 解压到任意目录（**别放在需要管理员权限的路径**，否则写不了 `state\`）
2. 双击 `RunEverCs.exe` → 常驻托盘，默认热键 <kbd>`</kbd>
3. 选一个文件 / 一段文字，按热键

包里自带 `config\menus.conf`（示例配置 + RunAny 内置搜索条目已合并）与 `配置说明.txt`。

**系统要求**：Windows 10 / 11 + .NET Framework 4.8（系统自带）。Everything 可选（有则路径解析更快）。

---

## 它怎么工作

```
热键
 └─ 采集（CaptureRunner.exe，AHK 常驻管道）
      → 选中文本 / 选中文件(夹) / 前台进程
        └─ 判定（MatchEngine）
             · 逐条比对分组规则 → 命中的组
             · 合成三个槽位：动态（含兜底） / 进程 / 固定
               └─ 渲染（Win32 TrackPopupMenu，图标 + 助记 + 子菜单）
                    └─ 点击 → 执行（run / open / url / send / cmd / http / group）
```

**三个槽位的语义**（这是 RunEver 的核心模型）：

| 槽位 | 出现条件 | 典型内容 |
|---|---|---|
| 动态 | 选中了文件/文本时命中 `folder` / `ext` / `text` 分组；什么都没选中时由 `fallback` 兜底 | 「压缩包 → 解压到当前目录」「选中文本 → 百度/谷歌」 |
| 进程 | 前台进程命中 `proc` 分组 | 「浏览器 → 用无痕窗口打开」 |
| 固定 | `always` 分组，每次弹出都出现 | 「工具 → 计算器 / 终端」 |

顺序由 `auto.conf` 的 `menuOrder` 决定（默认 `dynamic,process,always`）。**某槽位当次为空就整块不出现** ——
没选中东西又没配 `fallback` 时，「进程」自然排到第一位，这不是顺序失效。

---

## 配置

全部在 `config\` 下，纯文本，改完托盘右键「重载配置」即刻生效（不用重启）。

### `menus.conf` —— 菜单定义

```
! <分组名>  <类型>  [关键词…]
  <标题>  _<快捷键>  |  <动作>  <目标>
    <子条目 …>
```

- `!` 开头是分组；**两个空格一级缩进**；父分组后面必须紧跟它的子项（不能跳级）
- 条目 = `标题 | 动作 目标`，`_X` 是可选的快捷键提示
- 配置里**只写程序名**（如 `7zFM.exe`），全路径由路径缓存解析 ⇒ 换机不用改路径

**分组类型**

| 类型 | 命中条件 |
|---|---|
| `fallback` | 兜底：什么都没选中时出现（全局最多一个） |
| `folder` | 选中的是文件夹 |
| `ext` | 选中的文件扩展名在关键词里（如 `ext pdf`） |
| `text` | 选中了文本 |
| `proc` | 前台进程名在关键词里（如 `proc chrome msedge`） |
| `always` | 每次弹出都出现 |

**动作类型**：`run`（启动程序）· `open`（用默认程序打开）· `url`（`%s` = 选中文本）·
`send`（发送按键）· `cmd` / `http`（调用外部）· `group`（子分组）

### `auto.conf` —— 运行参数

首次运行从 `templates\auto.conf` 生成；样例见 `config\auto.conf.example`。

| 键 | 说明 |
|---|---|
| `hotkey` | 弹出热键，如 `` ` `` / `^!r` / `F9` |
| `enabled` / `minLen` / `cacheSeconds` | 选中文本自动执行：开关 / 最短长度 / 缓存秒数 |
| `hideMissing` | 隐藏本机解析不到的 `run/open` 条目（换机第一件事就是打开它） |
| `showByDefault` / `waitWhenShown` | 是否显示执行窗口 / 窗口是否等待命令结束 |
| `menuOrder` | 三槽位顺序，如 `dynamic,process,always` |
| `memTrimIdle` | 空闲多少秒后整理内存（默认 20） |
| `espath` / `evdll` | Everything 的 es.exe / SDK dll 路径（留空 = 自动探测） |
| `webdavUrl/User/Pass/Dir` / `syncOnStart` | WebDAV 配置同步（留空 = 不用） |

> 旧版曾有 `uiBackend`（win32/wpf）与 `captureBackend`（ahk/native）两个可切后端，**2026-11-08 起取消**：
> 采集固定走 AHK 管道、菜单固定走 Win32。旧配置里留着这两行会被忽略，不报错。

---

## 命令行

```powershell
RunEverCs.exe                     # 常驻托盘（默认）
RunEverCs.exe --settings          # 打开设置窗口
RunEverCs.exe --show-menu         # 弹一次菜单（不常驻）
RunEverCs.exe --trigger           # 走完整链路（采集 → 判定 → 菜单）
RunEverCs.exe --refresh-cache     # 重建路径缓存
RunEverCs.exe --selfcheck         # 全盘自检：配置里的程序本机有没有
RunEverCs.exe --sync              # WebDAV 同步一次
RunEverCs.exe --dump-autostart    # 看开机自启项（注册表值 + 解析结果 + 是否指向自己）
```

调试用：`--dump-parser`（解析结果）· `--dump-order`（判定后真实组序，可配 `--ctx=text|file:pdf|folder|proc:QQ.exe`）·
`--dump-menutext`（菜单文本与助记）· `--ev-probe`（Everything 探针）。

---

## 从源码构建

```powershell
git clone https://github.com/c-dyang/runever.git
cd runever
dotnet build cs\RunEverCs.csproj -c Release
.\cs\bin\Release\RunEverCs.exe --settings
```

不需要 Visual Studio，`dotnet build` 即可（net48 走系统自带的 .NET Framework 编译器链）。
运行时依赖的 DLL（`Everything64.dll` / `ControlzEx.dll` / `MahApps.Metro.dll` / `Microsoft.Xaml.Behaviors.dll`）
与采集器 `CaptureRunner.exe` 在 Releases 包里；源码树里的 `dist\` 不入库。

---

## 自检

19 套无人值守自检，一键跑：

```powershell
pwsh -File .\run-selftests.ps1 -Exe "cs\bin\Release\RunEverCs.exe" -OutDir state\selftest
```

单套：`RunEverCs.exe --selftest <root> <out.txt>`、`--selftest-menueditor`、`--selftest-menu` …

覆盖：解析器 / 写回器 / 模板 / 插件编辑 / 路径缓存 / 判定匹配 / 隐藏规则 / 自动执行 / 执行器 / 采集链 /
WebDAV / 配置写回 / 热重载 / 目录选择 / 菜单编辑页（结构操作、拖放、搜索） / 菜单渲染 / 自检自身。

每次改完必须跑；**19 套全绿才算没坏**（`bootstrap`、`selfcheck` 两条曾长期红，是断言口径问题，已修正）。

---

## 边界（它有意不做的事）

- **不做搜索**：找文件用 Everything / Listary；RunEver 是"对这个东西能做什么"的动词层
- **不做多选、不做常驻面板、不做 Dock**
- **单一后端**：菜单只有 Win32 一份实现（WPF 只用于设置窗口），采集只有 AHK 一份
- **不擅自改你的系统**：开机自启只在你点菜单时才写注册表，且只写 `HKCU\...\Run` 里自己那一项

---

## 目录

| 路径 | 内容 |
|---|---|
| `cs/` | C# 主体：判定、Win32 菜单、执行器、设置窗口、自检 |
| `src/` | AHK 原版（历史对照，不是主链） |
| `config/` | `menus.conf`（菜单）、`auto.conf`（参数）、`functions/`（插件脚本） |
| `templates/` | 首次运行生成配置用的模板 |
| `docs/` | 架构契约（菜单语义 / 不变量 / 配置字段）、EV 后端、parity 审计 |
| `release-src/` | 发版辅助：RunAny / Quicker 配置转换 |
| `run-selftests.ps1` | 19 套自检一键跑 |

---

## 说明

个人自用工具，按自己的使用习惯长出来；示例条目也只是示例。已在 Windows 10 / 11 + .NET Framework 4.8 验证，
Linux / macOS 不适用（Win32 菜单与注册表强依赖）。

问题或想法欢迎开 Issue。
