# Quicker 条目 → menus.conf 转换报告

- 源：`docs/条目结构解析.txt`（145 行）
- 产物：`config/menus.quicker.conf`（分组 17，条目 129）
- 丢弃合计：0 条；**Quicker 内置动作已救回 37 条**（转成 `run QuickerStarter.exe runaction:<动作ID>`，见下节）

## 分组一览

| 分组 | 类型 | 关键词 | 条目数 |
|---|---|---|---|
| 文件夹 | folder | - | 4 |
| 通用菜单 | fallback | - | 49 |
| 文本菜单 | text | - | 23 |
| 永久显示 | always | - | 12 |
| 音乐 | ext | mp3 wav flac wma ogg | 1 |
| 图片 | ext | ai avif b64 bmp bpg cur cut dib emf eps exif exr fax fits gif hdr heic heif ico jfif jp2 jpe jpeg jpg jxl mjpeg pbm pcx pgm png ppm psd qoi svg tga tif tiff viff vx webp wmf wpg | 5 |
| 纯文本 | ext | txt ini md log ahk | 5 |
| 压缩包 | ext | zip rar 7z 001 lzh | 2 |
| 视频 | ext | mp4 avi rm flv f4v mov 3gp mkv rm wmv mpg mpeg webm qsv m4v | 4 |
| 程序 | ext | msi exe | 2 |
| 浏览器 | proc | msedge vivaldi firefox chrome zen | 7 |
| 聊天 | proc | qq wechat | 2 |
| PDF | ext | pdf | 3 |
| Office | ext | doc docx xls xlsx ppt pptx | 2 |
| Wps(_W)|ksolaunch.exe | proc | /prometheus /fromksolaunch /from | 0 |
| 个人工具箱 | always | - | 5 |
| 游戏 | always | - | 2 |
| 模拟器 | proc | virtualboxvm vmware | 1 |

## 跨组重复条目（同一程序出现在多个分组；按你的习惯决定是否删）

- `SumatraPDF` → `SumatraPDF.exe`（出现在：文件夹、PDF）
- `NSudoLG` → `NSudoLG.exe -U:S -P:E`（出现在：通用菜单、程序）
- `EmEditor` → `EmEditor.exe`（出现在：通用菜单、纯文本）
- `Notepad4` → `Notepad4.exe`（出现在：通用菜单、纯文本）
- `MusicPlayer2` → `MusicPlayer2.exe`（出现在：通用菜单、音乐）
- `切换浏览器（动作）` → `QuickerStarter.exe runaction:997d7f2e-ab94-423d-90d7-3e78264ccd34`（出现在：文本菜单、浏览器）
- `全文搜索（动作）` → `QuickerStarter.exe runaction:6fc71b89-aac8-4cd6-9366-282afa5c5f94`（出现在：文本菜单、永久显示）
- `超级工具箱（动作）` → `QuickerStarter.exe runaction:f6ef3fc4-6abf-46b1-acb6-83215d201f80`（出现在：永久显示、图片、个人工具箱）

## Quicker 内置动作（已转成外部启动）

> 形式：`run QuickerStarter.exe runaction:<动作ID>`。**存裸程序名**（换机免重配，由目标机解析链去找）。
> ⚠️ **需要目标机装了 Quicker**（`QuickerStarter.exe` + `quicker:` 协议），否则执行器会报 `run-missing`。
> 需要把选中内容传给动作时，在目标后加 `?%text%` 或 `?%file%`（见 Quicker 文档的 `runaction:标识?参数`）。

| 分组 | 标题 | 动作ID |
|---|---|---|
| 文本菜单 | Skrbt搜索（动作） | `4eb5fd7a-4d66-432d-95a4-3c578385142f` |
| 文本菜单 | 切换浏览器（动作） | `997d7f2e-ab94-423d-90d7-3e78264ccd34` |
| 文本菜单 | 繁体转简体（动作） | `f821ce71-c7a6-459c-99a3-933f7aa4e88c` |
| 文本菜单 | 文本快速记录（动作） | `baf06e7d-10d5-4314-be58-0741585ef3b4` |
| 文本菜单 | QQ全局搜索（动作） | `5901f07a-fe85-4a15-bfaa-9e75cd9ed519` |
| 文本菜单 | Translator（动作） | `609ee632-4ae3-4719-8778-5443b29a5a20` |
| 文本菜单 | 全文搜索（动作） | `6fc71b89-aac8-4cd6-9366-282afa5c5f94` |
| 文本菜单 | 文本转图片（动作） | `325939bc-063e-4b8a-97ad-50c912c3ade6` |
| 永久显示 | 最新文件（动作） | `46eb8752-e5e8-471b-bb04-81a58204846c` |
| 永久显示 | 发送到文件夹（动作） | `6417712b-cbb1-4e6f-ac18-ebe5172d18f2` |
| 永久显示 | 文件预览（动作） | `e6e88966-10bb-4a19-b0cf-bf312df949f9` |
| 永久显示 | OneTab（动作） | `6c136ea1-7edc-4c5d-bc60-c4b52dc35a9c` |
| 永久显示 | 超级工具箱（动作） | `f6ef3fc4-6abf-46b1-acb6-83215d201f80` |
| 永久显示 | 全文搜索（动作） | `6fc71b89-aac8-4cd6-9366-282afa5c5f94` |
| 永久显示 | 程序便签（动作） | `c1a61908-01a4-4d4d-a011-b298d89388c2` |
| 永久显示 | 音频设备管理（动作） | `799a8417-3e68-43d9-80ba-965719ec07fc` |
| 永久显示 | SmartZip自动解压（动作） | `db1f4201-3625-4d17-a20a-7d863ead73a5` |
| 图片 | 图片压缩（动作） | `6a115363-0da7-4a34-9544-09c8678905db` |
| 图片 | FTP图床上传（动作） | `440c2741-217e-413c-a5ae-dc921c75d468` |
| 图片 | 超级工具箱（动作） | `f6ef3fc4-6abf-46b1-acb6-83215d201f80` |
| 纯文本 | 程序笔记（动作） | `c1a61908-01a4-4d4d-a011-b298d89388c2` |
| 压缩包 | 智能解压（动作） | `36f5c4fc-c1b3-4ed5-82fe-869810905492` |
| 浏览器 | 网页视频下载（动作） | `77c2a931-815b-40d7-a2c5-5379655e131d` |
| 浏览器 | YT-DLP下载视频（动作） | `02816303-3e1f-492b-beaa-5546e16b9cdd` |
| 浏览器 | 网页可视化编辑（动作） | `aee3e2dd-a37f-4a65-8460-15fdd9b1d92f` |
| 浏览器 | 查看网页Cookie（动作） | `79af15f1-3c4d-45bf-a7c9-cbb19d7c00ab` |
| 浏览器 | 当前链接（动作） | `9b5daae8-19ad-4044-9808-2ba06af002e1` |
| 浏览器 | 切换浏览器（动作） | `997d7f2e-ab94-423d-90d7-3e78264ccd34` |
| 浏览器 | 关闭当前域名的所有标签（动作） | `6f0d219d-0aff-49cc-83ab-6c98213d0f5e` |
| 聊天 | 表情包选择器（动作） | `3348124b-f462-4a78-89a1-5bfc3849065a` |
| 聊天 | 腾讯全局搜索（动作） | `5901f07a-fe85-4a15-bfaa-9e75cd9ed519` |
| 个人工具箱 | 文件名启动（动作） | `85ed5039-fad8-4330-886b-db55cd9b34be` |
| 个人工具箱 | 超级工具箱（动作） | `f6ef3fc4-6abf-46b1-acb6-83215d201f80` |
| 个人工具箱 | 取色者（动作） | `9ba4e72b-adff-4af9-be8f-1cca887e8a6c` |
| 个人工具箱 | 文件服务器（动作） | `2df205d8-ef59-42ea-8e77-de0b2da1da98` |
| 个人工具箱 | 关闭标签页（动作） | `8a0cce24-0cee-4cff-bb94-5d555693137b` |
| 模拟器 | 返回主系统 | `2138ce83-9618-42fd-983d-d783532d06f1` |

## 丢弃项

- （无）
