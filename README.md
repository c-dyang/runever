# RunEver

Windows 右键菜单启动器：在**任意位置按一个热键**，按当前上下文（选中的文本 / 选中的文件 / 前台进程）弹出一份
**只跟这一刻有关**的原生菜单，点完即走。

- 菜单：Win32 `TrackPopupMenu`（系统原生，带图标、助记键、子菜单）
- 判定：配置驱动的「情境 / 进程 / 固定」三段槽位（`menuOrder` 可调）
- 采集：AHK 常驻管道（选中的文本 / 文件 / 前台窗口）
- 宿主：.NET Framework 4.8（`LangVersion 7.3`），设置窗口 WPF，**零第三方运行时依赖**

## 目录

| 路径 | 说明 |
|---|---|
| `cs/` | C# 主体（判定、菜单、执行、设置窗口、自检） |
| `src/` | AHK 原版（历史对照，非主链） |
| `config/` | `menus.conf`（菜单定义）、`auto.conf.example`（配置样例） |
| `templates/` | 首次运行的配置模板 |
| `docs/` | 架构契约（菜单语义、不变量、配置字段） |
| `release-src/` | 发版辅助：Quicker 配置转换等 |
| `run-selftests.ps1` | 19 套自检一键跑 |

## 构建

```powershell
dotnet build cs\RunEverCs.csproj -c Release
```

## 运行

```powershell
.\cs\bin\Release\RunEverCs.exe            # 常驻托盘（默认）
.\cs\bin\Release\RunEverCs.exe --settings # 打开设置窗口
```

首次运行会在程序目录旁生成 `config/`、`state/`、`cache/`。

## 自检

```powershell
pwsh -File .\run-selftests.ps1 -Exe "cs\bin\Release\RunEverCs.exe" -OutDir state\selftest
```

单套示例：

```powershell
.\cs\bin\Release\RunEverCs.exe --selftest            . state\selftest.txt
.\cs\bin\Release\RunEverCs.exe --selftest-menueditor . state\menueditor.txt
```

## 配置

- `config/menus.conf`：菜单定义。契约见 `docs/架构契约.md`（`! <分组> <类型> [关键词…]`、两空格一级缩进、父后紧跟子）。
- `config/auto.conf`：运行参数（热键、路径缓存、菜单顺序、内存整理等）。样例见 `config/auto.conf.example`。
  **此文件可能包含 WebDAV 凭据，已在 `.gitignore` 中排除。**

### 分组类型

| 类型 | 含义 |
|---|---|
| `fallback` | 兜底：没命中其它分组时出现的条目 |
| `folder` / `ext` | 按选中的文件（夹）/ 扩展名命中 |
| `text` | 按选中的文本命中 |
| `proc` | 按前台进程命中 |
| `always` | 每次弹出都出现 |

判定后合成三格：**动态（含兜底）/ 进程 / 固定**，顺序由 `auto.conf` 的 `menuOrder` 决定。

## 说明

本项目为个人自用工具，配置与路径按个人环境编写；`menus.conf` 里的条目示例可能包含本机路径，
换机使用时按自己的环境调整。
