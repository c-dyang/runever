# Quicker 菜单动作 → RunEver 配置映射

> 输入：用户 PC 上 Quicker「菜单」类动作的配置（`quicker-menu-source.txt`，144 行）
> 输出：`menus.quicker-sync.conf`（RunEver 格式，17 分组 / 128 条目 / 9,154 B）
> 转换器：`quicker2runever.py`（一次性工具，改完源文件重跑即可）

## 一、两种格式对照

| 概念 | Quicker 菜单动作 config | RunEver `menus.conf` |
|---|---|---|
| 分组 | `分组名^类型=` 或 `分组名^类型=[` … `]` | `! 分组名  <类型>` |
| 分组类型 | `folder` / `.pdf` 后缀列表 / `(进程)^a b c` / `pubic` / `search` / `tips` / 空 | `folder` / `ext …` / `proc …` / `always` / `text` / `fallback` |
| 条目 | `[图标]名称(_X)\|目标^参数` | `  名称  _x  \| <动作>  目标` |
| 层级 | 前导 `\t` 的个数 | 行首 2 空格 × 层级 |
| 图标 | `[url:...]` / `[fa:...]` 前缀 | 丢弃（RunEver 自己按 exe 解析图标） |
| 选中文本占位 | `%s` | `%s`（语义一致，原样保留） |
| 助记符 | 名称里的 `(_X)` / `（_X）` | 名称后的 `_x` |
| 分割线 | `----\|` | RunEver 无配置语法（界面里是 `AddSeparator`），转换时丢弃 |
| 纯子菜单标题 | `名称\|` 或 `名称\|\t`（目标为空） | `名称  \| group` |

## 二、动作判定

| Quicker 目标 | RunEver |
|---|---|
| `动作=<guid>` | `url quicker:runaction:<guid>` |
| `动作=<guid>?<参数>` | `url quicker:runaction:<guid>?<参数>` |
| `http(s)://…` 或其它协议 | `url <原样>` |
| `程序.exe^参数` | `run 程序.exe 参数`（**不写绝对路径**，由路径缓存解析） |
| 空 / `\t` | `group` |

**Quicker 内部动作的启动方式依据官方文档**：<https://getquicker.net/kc/manual/doc/quicker-starter>

- 命令行：`"C:\Program Files\Quicker\QuickerStarter.exe" runaction:<动作标识>`
- URI：`quicker:runaction:<动作标识>`
- 参数分隔：`runaction:<标识> <参数>` 或 1.8.0+ 的 `runaction:<标识>?<参数>`
- **两个硬前提**：① 从外部启动动作是 **Quicker 专业版功能**；② Quicker **不能以管理员身份运行**，否则 quicker 协议不可用。

这里选 URI 而不是 `QuickerStarter.exe`，是因为 RunEver 的 `run` 动作要靠路径缓存解析程序名，
而 `QuickerStarter.exe` 不在 PATH / App Paths / Everything 常规命中范围内；`url` 动作走
`ShellExecute`，协议由系统注册表直接转交，无路径依赖。

## 三、转换统计（本次）

| 项 | 数量 |
|---|---|
| 分组 | 17 |
| 条目 | 128 |
| 其中 Quicker 动作（转 quicker:runaction） | 37 |
| 其中网址条目 | 17 |
| 丢弃的分割线 `----\|` | 1 |
| 无法解析而丢弃的条目 | 0 |

层级分布：lv1=96 / lv2=26 / lv3=6（与源文件 `\t` 深度一致，无跳级）。

## 四、源配置里需要留意的几处

1. **多行 URL**：源文件里 `京东`/`百度`/`谷歌`/`知乎` 的 URL 被折成两行（值里含真实换行）。
   转换器把 `\n键=值` 这种续行接成 `&键=值`，否则 URL 会断。若以后新增类似折行，这条规则仍适用。
2. **`Wps`** 的目标含 `=`：`ksolaunch.exe^/prometheus /fromksolaunch /from=startmenu`。
   早期版本会把这类行误判成「分组头」，已加守卫（分组头的 `=` 左侧不得出现 `|`）。
3. **`pubic`** 是源文件里的原拼写（Quicker 的公共后缀菜单），按 `always` 处理。
4. **`1Bandicam_v5.4.3.1923_x64（_1）`** 的助记符是数字 `1`，转换后为 `_1`；
   RunEver 界面能显示，但 Alt+1 在有些输入法下会先被拦截，实际可用性需在 PC 上点一次确认。

## 五、复现

```
python quicker2runever.py
```
脚本内 `SRC` / `OUT` 两个常量指向源文件与输出路径，改完重跑即可。
生成的 conf 已直接部署到 PC 的 `RunEver-20261107\config\menus.conf`
（原件备份为 `menus.conf.bak-20261107-quicker`）。
