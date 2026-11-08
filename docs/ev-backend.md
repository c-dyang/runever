# RunEver 搜索后端（EV / ES）说明

> 2026-10-03 定案并实测。**用户语境：EV = Everything SDK（`Everything64.dll`），ES = `es.exe` 命令行。**
> 单一源：本文件 + `config\auto.conf` 的 `evdll=` / `espath=` 注释 + `cs\Services\SearchBackends.cs`。

## 一、降级链（任何一级失败都只是"降级"，不是异常）

```
EV（Everything64.dll 直连 IPC）
  └─ 不可用 → es.exe（子进程命令行）
        └─ 不可用 → App Paths 注册表 + PATH 环境变量（无搜索）
              └─ 末级标记 backend=none
```

用途分工：
| 入口 | 后端 | 说明 |
|---|---|---|
| 托盘「刷新路径缓存」/ `--refresh-cache` | EV → es → none | 共用 `Services\PathCacheRefresh.cs`（唯一定义） |
| 托盘「全盘自检…」/ `--selfcheck` | 同上 | 顺带把解析到的真实路径写回 `cache\paths.txt` |
| **菜单渲染时的「缺件自动隐藏」** | **不用搜索** | 只查缓存 + PATH + App Paths —— 热键路径零外部 IO |
| `--selftest-pathcache` | 不注入后端（走原 es 路径） | 作为零回归基线，改造前后 diff 必须为空 |

## 二、DLL 探测顺序（`EverythingSdk.EvDllLocator.Candidates`）

按序取第一个**存在且能加载**的：`evdll=` 指定值（**指定即独占**，不再猜别的）→
`<程序目录>\Everything64.dll` → `%USERPROFILE%\WorkBuddy\Claw\claw-tools\quicknote\everything_sdk\dll\`
→ `%USERPROFILE%\WorkBuddy\Claw\tools\QuickNote\` → `%TEMP%\runany-src\`。

只认与进程位数匹配的文件名（x64 → `Everything64.dll`；32 位/ARM 名在 SDK 的 `dll\` 里并存，误选必 `BadImageFormat`）。

## 三、探针判据（"能加载 ≠ 能用"）

可用 = **`GetNumResults() > 0` 且 `Everything_GetLastError() == 0`**，并要求读回的全路径与查询名一致；
`LastError == 2` 表示 IPC 错误（Everything 未运行 / 版本不通）。

- 加载一律走 `LoadLibraryW` + `GetProcAddress` + 委托，**不用静态 `DllImport`**（DLL 缺失时静态导入会直接炸进程）。
- SDK 是进程级单例且**非线程安全** ⇒ 探针与搜索串行化在 `EvLock.Sync`。
- 探针结果缓存 120s（`EverythingProbe.CacheMs`）。
- 查询一律走 **W（Unicode）** 入口（中文程序名必须），并叠加**客户端文件名全等过滤** ——
  否则 `code.exe` 会命中 `vscode.exe`（es 的 `wfn:` 语义在 EV 侧要自己补）。

## 四、版本事实（本机实测 2026-10-03）

| 项 | 值 |
|---|---|
| Everything GUI | **1.4.1.1032**（`C:\Program Files\Everything\Everything.exe`）⇒ 配 1.4 SDK 的 `Everything64.dll`，IPC 兼容 |
| 1.5a 才需要 | `Everything3_x64.dll`（本机不适用） |
| 实测探针 | `backend=EV  ver=1.4.1.1032  hits=16  dbLoaded=1  lastError=0  probe=229ms` |
| 发布位置 | `dist\Everything64.dll`（90,280 B，与 SDK 原始副本同 SHA256） |

## 五、降级态纪律（最重要的一条）

**"搜不了" ≠ "没装"。** 后端降级时：
- 全盘自检把查不到的程序一律标 **「未找到（待确认）」**，不得出现「未安装」字样（有断言钉住）；
- 导出的 `.bat` 里**一条 active 安装行都没有**（全部 `REM`），并带 `UNCONFIRMED` 说明；
- 只有 `Status = Missing`（已确认缺失）的条目才会生成可执行的 `choco install <pkg> -y`。

## 六、相关配置键

```ini
# config\auto.conf
evdll=      ; 空 = 自动探测（见上）；指定 = 独占使用该文件
espath=     ; es.exe 路径；空 = 自动探测（常见位置 + PATH）
```
