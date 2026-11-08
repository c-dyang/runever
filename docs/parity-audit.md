# RunEver AHK → C# 功能对照审计（只读审查）

审计人：审核子代理（只读 src\*.ahk + cs\**\*.cs；仅写本文件与 `task-table.md`）
证据格式：AHK 侧 `src\X.ahk:NN`，C# 侧 `cs\Y.cs:NN`。读不到 = 标「待核 + 原因」，不猜。
基线：AHK 侧 10 个非测试源（RunEver/AutoMatcher/MenuNative/PathCache/ConfParser/CaptureRunner/Collector/HotkeyTrigger/Matcher/**SettingsUI=已被 C# 取代**）；C# 侧 18 个功能源 + 6 个 *SelfTest/Dump 工具。

## 0. 结论速览

- **主链路缺失 2 条**：① **自动执行（匹配块）全链在 C# 侧不存在**（只移植了元数据解析，没有匹配/派发/登记/动态加载）；② **程序路径缓存构建器不存在**（无 es.exe/tier 加权/排除规则），连带托盘「刷新路径缓存」入口缺失。
- **用户点名的「text 动态菜单」判定规则双侧等价**（`Matcher.cs` 与 `Matcher.ahk` 逐行同构）——它不是独立能力，而依赖 **自动执行把匹配结果做成动态条目**；C# 侧缺的正是这一环，所以选中文本时用户「看不到动态菜单」。
- **C# 侧多出** AHK 没有的能力：托盘「开机自启」（注册表 Run 项）、`--trigger` 外部触发、插件新建向导（Generate/Write + 语法校验）、菜单可视化编辑（写入 menus.conf）。
- **明确无对应实现**：僵尸执行器清理（`CleanZombies`）、`plugins.json` 启动登记、AHK 热键触发器助手（HotkeyTrigger.ahk）——C# 已自带 RegisterHotKey，助手属可弃。

---

## 1. 功能对照总表

| # | 能力（对外可观察） | AHK 证据 | C# 状态 | C# 证据 | 分级 | 建议 |
|---|---|---|---|---|---|---|
| 1 | 全局热键 `` ` `` 弹菜单 | src\HotkeyTrigger.ahk:54 RegisterHotkey；src\HotkeyTrigger.ahk:87 OnHotkey | 已实现（原生 RegisterHotKey + 消息窗口） | cs\TrayApp.cs:80；cs\TrayApp.cs:305 WM_HOTKEY | — | 保留；AHK 助手可弃（C# 自带） |
| 2 | 托盘「显示菜单」 | src\RunEver.ahk:495 | 已实现 | cs\TrayApp.cs:119 | — | — |
| 3 | 托盘「设置…」 | src\RunEver.ahk:497 | 已实现（C# 设置窗取代 SettingsUI.ahk） | cs\TrayApp.cs:126-129；cs\Views\SettingsWindow.cs:13 | — | SettingsUI.ahk 标注已被取代 |
| 4 | 托盘「打开配置」 | src\RunEver.ahk:501 | 已实现 | cs\TrayApp.cs:136 | — | — |
| 5 | 托盘「刷新路径缓存」 | src\RunEver.ahk:499 → :1019 TriggerCacheBuild | **缺失**（无入口，且无构建器可调） | cs\TrayApp.cs:119-180 菜单项清单无此项 | 主链路 | 新建 `cs\Services\PathCacheBuilder.cs`，回接托盘项；工作量：中 |
| 6 | 托盘「刷新插件」 | src\RunEver.ahk:500 → :535 RefreshPlugins | **缺失**（托盘无入口；PluginScanner 仅在设置页内用） | cs\TrayApp.cs:119-180；cs\Views\PluginPage.cs:212 | 体验 | 托盘加项 → `PluginScanner.Scan()`；工作量：小 |
| 7 | 托盘「开机自启」 | **AHK 无** | C# 新增（HKCU Run 项，用户点击才写） | cs\TrayApp.cs:150-159；cs\TrayApp.cs:243 | 增强 | 保留 |
| 8 | 托盘「重载配置」/「打开插件目录」/「打开日志」/「状态…」/「退出」 | AHK 无对应或等价能力弱 | C# 新增 | cs\TrayApp.cs:130 / :140 / :144 / :174 / :178 | 增强 | 保留 |
| 9 | 常驻托盘 + 单实例 + 热键冲突提示 | src\RunEver.ahk:2 #SingleInstance Force；:490 SetupTray | 已实现 | cs\Program.cs:60（--tray）；cs\TrayApp.cs:83-87 冲突提示 | — | — |
| 10 | 启动自举：建目录 + 释放配置模板 | src\RunEver.ahk:1090-1111（含 auto.conf :1108-1111） | 部分实现（只建目录，不释放模板） | cs\RunEverApp.cs:107-114 EnsureLayout | 体验 | 版本升级时新用户拿不到模板；工作量：小 |
| 11 | 启动清理僵尸执行器 + 残留任务文件 | src\RunEver.ahk:1045-1083 CleanZombies（:177 启动调用） | **缺失** | cs\ 全域无 CleanZombies/僵尸 关键词 | 边缘 | C# 执行器为同进程/自管进程，风险低；可按需补；工作量：小 |
| 12 | 采集：选中文本 / 文件 / 前台窗口 / 扩展名集 / 是否文件夹 | src\Collector.ahk:20-152；src\CaptureRunner.ahk:29-178 | 已实现（改由 AHK 采集器子进程产出 JSON 回传） | cs\Services\CaptureClient.cs:63；cs\Services\Matcher.cs:10-38 Context | — | 跨进程契约变更，需回归 |
| 13 | 采集一次性语义（常驻模式不复用陈旧上下文） | **AHK 无此坑**（每次热键新建 Collector） | C# 修正实现（消费即失效 + 2s 过期） | cs\RunEverApp.cs:123-156；cs\RunEverApp.cs:105 | 增强 | 保留 |
| 14 | 配置解析：分组行 `! 名称 类型 关键词` | src\ConfParser.ahk:65 起 | 已实现 | cs\Services\ConfParser.cs:33 | — | — |
| 15 | 配置解析：条目行（缩进层级 / `_快捷键` / 动作 / 目标） | src\ConfParser.ahk:106-187；src\ConfParser.ahk:188 引号感知分列 | 已实现 | cs\Services\ConfParser.cs:126 快捷键正则 | — | 需逐字段回归 |
| 16 | 配置解析：收集 run 类型程序名（供路径缓存） | src\ConfParser.ahk:214 CollectPrograms | **缺失** | cs\Services\ConfParser.cs 无此方法（仅 Parse） | 主链路 | 随 #5 一起补；工作量：小 |
| 17 | 菜单判定：always/folder/ext/proc/text/fallback 六类 | src\Matcher.ahk:21-52 | 已实现（同构） | cs\Services\Matcher.cs:60-95 | — | 见 §3 逐条比对 |
| 18 | 判定合成：三层顺序 + 不去重 + 逐项堆叠 | src\Matcher.ahk:61-79 | 已实现 | cs\Services\Matcher.cs:97-105 | — | — |
| 19 | 判定统计（always/dynamic/fallback 计数，日志可观察） | src\Matcher.ahk:81-85 | 已实现 | cs\Services\Matcher.cs:107-113 | — | — |
| 20 | ext 匹配（关键词=扩展名，小写去点，任一等值） | src\Matcher.ahk:90-100 | 已实现 | cs\Services\Matcher.cs:116-123 | — | — |
| 21 | proc 匹配（`chrome` 与 `chrome.exe` 等价） | src\Matcher.ahk:103-116 | 已实现 | cs\Services\Matcher.cs:125-137 | — | — |
| 22 | 菜单渲染：Win32 原生菜单 + 第一层平铺（不显示分组名） | src\RunEver.ahk:786 BuildMenu；:770-784 平铺规则 | 已实现 | cs\Services\MenuBuilder.cs:29；cs\Native\NativeMenu.cs:89 | — | — |
| 23 | 同名项共存（ID 与文本分离，不静默替换） | src\RunEver.ahk:318-354 断言；src\Native 说明 :781-782 | 已实现 | cs\Native\NativeMenu.cs:89-93（AppendMenuW 带 id） | — | — |
| 24 | 组内缩进建真子菜单 + 组间分隔线 | src\RunEver.ahk:858 AddItemToMenu | 已实现 | cs\Services\MenuBuilder.cs:29 起 | — | — |
| 25 | 条目图标解析 + 兜底图标 + 位图去重缓存 | src\RunEver.ahk:987 ResolveIconPath；src\MenuNative.ahk:164-341 | 已实现 | cs\Services\IconResolver.cs:22；cs\Services\IconResolver.cs:53 | — | — |
| 26 | 条目显示文本格式（标题 + 快捷键 + 分组标题） | src\MenuNative.ahk:146 GetItemText 区 | 已实现 | cs\Services\MenuBuilder.cs:127 FormatMenuText | — | 有 `MenuTextDump` 可对拍 |
| 27 | 点击派发 → 执行器任务文件契约 | src\RunEver.ahk:887 OnItemClick；:944 WriteTaskFile | 已实现（改为进程内 Executor，不落任务文件） | cs\RunEverApp.cs:233 Dispatch；cs\Services\Executor.cs:45 | — | 契约变更：AHK 执行器 exe 不再必需 |
| 28 | 执行动作类型 run/open/url/action/send/keys | src\RunEverExec.ahk:134 DoAction；:160/:212/:232/:248/:279/:291 | 已实现 | cs\Services\Executor.cs:25（类型枚举）；:45 Execute | — | — |
| 29 | 占位符替换 %text%/%clip%/%file%/%s% | src\RunEverExec.ahk:305 ReplaceVars | 已实现 | cs\Services\Executor.cs:237 ReplaceVars | — | — |
| 30 | run 目标首 token 走路径缓存解析 | src\RunEverExec.ahk:174/:326 LookupPath | 部分实现（PATH + App Paths，无缓存/es 加权） | cs\Services\PathCache.cs:92 ResolveProgram | 影响体验 | 随 #5 补；工作量：小 |
| 31 | action 显示/隐藏窗口（show 开关）→ Run/RunWait | src\RunEverExec.ahk:248-277 DoCmd | 已实现 | cs\Services\Executor.cs:17 注释；:45 Execute | — | — |
| 32 | 执行失败重试 + 失败小菜单（重试/打开任务/打开日志） | src\RunEverExec.ahk:63 RunWithRetry；:97 ShowFailMenu | **缺失**（已核：`Execute` 全 6 分支只 return 成功/失败，无重试、无失败菜单；Executor.cs 全文 255 行无 ShowFailMenu/Retry 关键词） | cs\Services\Executor.cs:45-63（switch 直接 return）；cs\Services\Executor.cs:66-125 DoRun 失败仅 `t.Log` + return false | 边缘 | 失败即静默，用户无感知；建议加托盘气泡 + 日志链接；工作量：小 |
| 33 | 自检入口（配置/路径缓存/采集/判定/图标/菜单/重名/自动执行/执行器/点击模拟） | src\RunEver.ahk:193-485 SelfTest（自动执行段 :403-441） | 部分实现（配置+采集新鲜度；另有 6 个专项 selftest 工具） | cs\RunEverApp.cs:260 SelfTest；cs\Program.cs:33-51 各 --selftest-* | 体验 | **缺「自动执行」断言**，随 #40 补 |
| 34 | 运行日志（统一 Log 出口 + 级别开关） | src\RunEver.ahk:1121 Log；:1114 LogInit | 已实现（log/level 读 auto.conf） | cs\RunEverApp.cs:376 Log；:398 ReadLogSettings | — | — |
| 35 | 设置界面：插件列表 / 启用 / 显示窗口勾选 | src\SettingsUI.ahk:162-234（**已被 C# 取代**） | 已实现 + 增强（编辑对话框、向导、删除到回收站） | cs\Views\PluginPage.cs:212-305；cs\Views\PluginEditDialog.cs:20；cs\Views\PluginWizardDialog.cs:25 | — | AHK 侧不再审实现细节 |
| 36 | 设置界面：分组/条目编辑并写回 menus.conf（AHK 无） | src\SettingsUI.ahk:412 仅写 auto.conf | C# 新增（保留注释骨架） | cs\Views\MenuEditorPage.cs:46；cs\Services\MenuConfWriter.cs:118 | 增强 | 保留 |
| 37 | 设置界面：auto.conf 全字段 | src\SettingsUI.ahk:412 写 auto.conf | 已实现 | cs\Views\GeneralPage.cs:12-27；:248 | — | — |
| 38 | 插件元数据「原地替换」写回（保留注释，唯一落盘口径） | src\AutoMatcher.ahk:553-608 | 已实现 | cs\Views\PluginPage.cs:344-379；cs\Services\PluginTemplate.cs:123 | — | 有 PluginEditSelfTest 守护 |
| 39 | 插件元数据解析（11 字段 + BadPlugins） | src\AutoMatcher.ahk:209 ReadMeta；:280 BadPlugins | 已实现（含稳定排序 Prio↓/文件名↑） | cs\Services\PluginScanner.cs:77 Scan；:98-101 排序 | — | — |
| 40 | **自动执行（匹配块）：匹配 → 派发 → 跳过弹菜单** | src\RunEver.ahk:564-577（1.5 步）+ :629-673 TryAutoExec | **缺失** | cs\RunEverApp.cs:162-173 流程无自动执行步；cs\ 无 AutoMatcher/TryAutoExec | **主链路** | 见 §2；工作量：中；风险：误触发 |
| 41 | 插件匹配式编译（正则 / 前缀糖 `SF*`） | src\AutoMatcher.ahk:439-455 区 | **缺失** | cs\ 仅 PluginWizardDialog.cs:258 用于校验，无匹配实现 | 主链路 | 随 #40；工作量：小 |
| 42 | 整段匹配 whole / 优先级选唯一命中最优 | src\AutoMatcher.ahk:173-197 Less；:439 区 | **缺失** | cs\Services\PluginScanner.cs:18-19 仅存字段，无使用方 | 主链路 | 随 #40 |
| 43 | 形态 B：调插件 `Run(text)` 产出目标（临时驱动脚本回传） | src\RunEver.ahk:684-762 CallPluginRun | **缺失** | cs\ 无对应实现 | 主链路 | 随 #40；工作量：中；风险：隔离/超时 |
| 44 | minLen / cacheSeconds / showByDefault / waitWhenShown / enabled 语义 | src\AutoMatcher.ahk:106-172 读 auto.conf；config\auto.conf:25-29 | 部分实现（字段可读可写，但**无消费者**——enabled/minLen 不生效） | cs\Views\GeneralPage.cs:168-199 读写；cs\RunEverApp.cs:398 只读 log | 主链路 | 随 #40 接线 |
| 45 | 启动登记 plugins.json（供设置 UI 读） | src\AutoMatcher.ahk:26 职责④ + :403 自检断言 | **缺失**（改为设置页实时 Scan） | cs\Views\PluginPage.cs:212 | 边缘 | 可弃（架构替代），需在文档里明确等价物 |
| 46 | 动态加载插件文件（丢文件即生效） | src\AutoMatcher.ahk:23 职责① scan *.ahk | 部分实现（Scan 有，但热键路径不调用） | cs\Services\PluginScanner.cs:77；cs\Views\PluginPage.cs:30 | 影响体验 | 随 #40 |
| 47 | `RVE_FN_Menu` 同步进菜单（AHK 本轮仅预留） | src\AutoMatcher.ahk:42 注释「本轮预留」 | 已存字段（未实现消费） | cs\Services\PluginScanner.cs:23 | 边缘 | 双侧等价（都未实现），无需补 |

> 分级含义：**主链路** = 少了它用户核心流程不走（选中文本自动执行）；**影响体验/影响主链路** = 能力存在但残缺、可见入口点了没反应；**边缘** = 内部或可替代。

---

## 2. 特别核查 A：自动执行（匹配块）全链逐环比对

用户原话：「ahk的自动执行…你丢完了啊」——**核对结论：确实丢了，且是整链缺失，不是部分缺失。**

### 2.1 全链七环对照

| 环 | AHK 实现 | AHK 证据 | C# 对应 | 结论 |
|---|---|---|---|---|
| ① 触发位（热键路径 1.5 步，命中则 return 不弹菜单） | `ShowMenu` 采集后调 `TryAutoExec(ctx)`，true 即 return | src\RunEver.ahk:564-577 | **无** | RunEverApp.ShowMenuFlow 是 `采集→匹配→图标→渲染→显示→派发`（cs\RunEverApp.cs:168-217），采集与判定之间**没有自动执行步**；`RunEverApp` 字段清单（:28-34）也无 AutoMatcher 成员 |
| ② 元数据解析（11 字段 + 双形态判定） | `AutoMatcher.ReadMeta` | src\AutoMatcher.ahk:209-305 | **有** | cs\Services\PluginScanner.cs:77 Scan / :131 正则 / :148-152 字段 |
| ③ 匹配（正则或前缀糖 + whole + prio 选唯一） | `AutoMatcher.Match` | src\AutoMatcher.ahk:439-455 区 | **无** | PluginScanner 只有 Scan（扫描）与排序，**无任何 Match 方法**（grep 全域无匹配实现） |
| ④ 形态 B：插件 `Run(text)` 产出目标 | `CallPluginRun`（临时驱动脚本 + 文件回传） | src\RunEver.ahk:684-762 | **无** | cs\ 无对应实现；PluginInfo.IsFormB 仅作 UI 展示（cs\Views\PluginPage.cs:430） |
| ⑤ 派发（复用执行器契约 + show 开关 → Run/RunWait） | `OnItemClick` + `show` 字段 | src\RunEver.ahk:657-672 | **无（但派发本身有）** | 派发复用点存在（cs\RunEverApp.cs:233 Dispatch），缺的是「自动执行构造 Task 并调它」 |
| ⑥ 启动登记 plugins.json | 落盘插件清单供设置 UI | src\AutoMatcher.ahk:26 职责④ | **无（架构替代）** | 设置页改为实时 Scan（cs\Views\PluginPage.cs:212），功能等价物存在，但文件契约消失 |
| ⑦ 动态加载插件文件 | 扫 `config\functions\*.ahk`，丢文件即生效 | src\AutoMatcher.ahk:23 | **部分** | Scan 可扫（cs\Services\PluginScanner.cs:77），但只在设置页被调；热键路径从不扫 → 新增插件对自动执行（不存在）与菜单（不读插件）均无影响 |

### 2.2 失败兜底三层（AHK 定案）在 C# 侧的状态

AHK 三层兜底：① 无命中 → 落回弹菜单；② 形态 B 空串 → 落回弹菜单；③ 派发异常 → 落回弹菜单（src\RunEver.ahk:624-627、:638-651、:666-672）。
C# 侧**不适用**（无自动执行），但意味着 **C# 侧永远走弹菜单** —— 用户视角 = 「自动执行完全没了」，与其申诉一致。

---

## 3. 特别核查 B：text 动态菜单

用户原话：「还有选中没有自动执行的 text 动态菜单呢」——把两侧 `text` 语义逐条列出比对。

### 3.1 text 组的语义真相（读代码得出，非推测）

`text` 组**没有关键词**（menus.conf:67 `! 选中文本  text`，类型后无参数），条目是**配置里的静态条目**（menus.conf:68-70：搜索/百度搜索/复制并打开），命中条件只有一条：**选中文本非空**。所谓「动态」= 该组因判定结果进入/不进入菜单、且其目标里的 `%s`/`%text%` 在派发时被替换成选中文本（src\RunEverExec.ahk:305 ReplaceVars）。
⇒ **text 组本身不需要 C# 侧新增任何东西**；用户看到的「选中后没有动态菜单」，真正缺的是 **§2 的自动执行链**（以及 `%text%` 动态条目）。这一点是本轮最容易误判的地方，须写进交付说明。

### 3.2 判定规则逐条比对

| 规则 | AHK | C# | 等价 |
|---|---|---|---|
| text 组命中条件 | `hasText := (Trim(ctx.Text) != "")` → push selectGrps | `hasText = !IsNullOrEmpty(ctx.Text) && ctx.Text.Trim().Length > 0` → selectGrps.Add | ✔ 等价（含空白串都判 false） |
| text 组关键词 | 无（`g["kw"]` 未被 text 分支使用） | 无（GroupType.Text 分支不看 Keywords） | ✔ |
| 槽位 | 选中类动态槽（folder/ext/text 同槽，与 fallback 互斥） | 同 | ✔ |
| 顺序 | 选中类 → fallback（仅当选中类全空）→ proc → always | 同（result.AddRange 顺序一致） | ✔ |
| 去重 | 无（全部堆叠） | 无 | ✔ |
| 统计 | dynamic = select+proc 计数 | DynamicCount 同定义 | ✔ |
| 条目生成 | 来自 menus.conf 静态条目；`%s`/`%text%` 派发期替换 | 同（cs\Services\Executor.cs:237 ReplaceVars） | ✔ |
| **动态条目（自动执行命中项）** | 由 AutoMatcher 命中产出，不弹菜单直接执行；`RVE_FN_Menu=true` 时预留进菜单（src\AutoMatcher.ahk:42） | **不存在** | ✘ 唯一缺口 |

**判定结论**：`Matcher.cs` 与 `Matcher.ahk` **行为等价**（六类分组、三层合成、无去重、ext/proc 匹配细节全部对齐，逐行同构）；text 相关缺口 100% 落在「自动执行」一节。若终审要求「text 动态菜单」独立验收，判定标准应改为：**选中含匹配式的文本按热键 → 不弹菜单直接执行对应插件**。

---

## 4. 已知不一致清单（架构变更 / 等价物替代）

| # | 不一致点 | AHK 口径 | C# 口径 | 影响 |
|---|---|---|---|---|
| 1 | 执行模型 | 主程序写任务文件 → 独立 `RunEverExec.exe` 进程执行 | 进程内 Executor 直接执行 | 僵尸清理失去对象（见 #11）；失败重试语义可能变化（#32） |
| 2 | 采集模型 | 同进程 Collector/AHk 直采 | `CaptureClient` 起 AHK 采集器子进程 + JSON 回传 | 跨进程契约脆弱点；采集耗时 279ms（cs\RunEverApp.cs:62 注释） |
| 3 | 插件清单契约 | `state\plugins.json` 启动登记 | 设置页实时 Scan；无该文件 | 外部工具若依赖该文件会失效 |
| 4 | 配置模板自举 | `FileInstall` 释放 menus.conf + auto.conf | 只建目录，不释放模板 | 新机器/新目录首启无模板（#10） |
| 5 | 自动执行 | 一等公民（1.5 步） | 不存在（#40/#41/#42/#43） | **主链路缺口**；`auto.conf` 的 enabled/minLen/showByDefault 全成死配置（除 log） |
| 6 | 菜单分组标题 | 第一层平铺、不显示分组名（用户 2026-10-03 定案） | 同 | 一致，无需处理 |
| 7 | AHK 热键助手 | `HotkeyTrigger.ahk` 独立脚本注册热键 → 起 C# `--trigger` | C# 自带 RegisterHotKey（`` ` `` / VK_OEM_3） | 助手可弃；但 `--trigger` 保留给外部触发（HwndFile 契约，cs\TrayApp.cs:189） |

---

## 5. 缺失项统计与建议实现位置

**统计（按能力条口径）**：主链路 **4** 项（#40/#41/#42/#43，均属自动执行链）+ 主链路连带 **2** 项（#5/#16）+ 影响体验 **3** 项（#6/#30/#46）+ 边缘 **4** 项（#11/#32/#45/#47）+ 部分实现需回归 **4** 项（#10/#33/#44/#13）。合计 **17** 项，其中**确认缺失 11** 项、**部分实现 3** 项、**双侧同为预留/等价无需补 2** 项（#45/#47）、**仅需回归核对 1** 项（#13）。

**缺失项数量与分级（确认缺失 11 项）**
- 主链路 6：自动执行触发位(#40)、匹配式编译(#41)、whole/prio 选优(#42)、形态 B 执行(#43)、路径缓存构建器(#5)、CollectPrograms(#16)
- 影响体验 3：托盘「刷新插件」(#6)、run 目标缓存解析(#30)、热键路径动态加载插件(#46)
- 边缘 2：僵尸清理(#11)、失败重试与失败菜单(#32)


**Top3 主链路缺失（建议先修顺序）**
1. **自动执行派发链**（#40 + #41 + #42）：建议在 `cs\Services\` 新增 `AutoMatcher.cs`（Match/CompileMatch/Whole/Prio），在 `cs\RunEverApp.cs:168` 采集与判定之间插入 `TryAutoExec(ctx)` 早退分支；工作量 **中**；风险 = 误触发（须叠 minLen/enabled/whole 三重保护）。
2. **形态 B 插件执行**（#43）：建议 `cs\Services\PluginRunner.cs`（临时驱动脚本 + 输出文件回传，等价 `CallPluginRun`）；工作量 **中**；风险 = 子进程超时/输出路径转义/僵尸（需与 #11 一起考虑）。
3. **路径缓存构建器**（#5 + #16 + #30）：建议 `cs\Services\PathCacheBuilder.cs`（CollectPrograms + es.exe 搜索 + PreferPrefix 加权/ExcludeFrag 硬排除），回接托盘「刷新路径缓存」；工作量 **中**；风险 = es.exe 路径依赖外部工具（AHK 侧硬编码 `%USERPROFILE%\.workbuddy\binaries\es.exe`，src\PathCache.ahk:32），需配置化。

> 详细分步实施（含自检方式与重难点）见项目根 `task-table.md`。
