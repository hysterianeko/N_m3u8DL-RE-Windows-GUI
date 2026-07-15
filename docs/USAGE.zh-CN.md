# 使用说明

## 1. 准备工具

程序需要两个外部可执行文件：

| 工具 | 用途 | 自动查找位置 |
| --- | --- | --- |
| `N_m3u8DL-RE.exe` | 解析和下载 HLS/DASH | GUI 同目录、`tools`、三类 `PATH`、各固定磁盘根部的下载目录 |
| `ffmpeg.exe` | 检测、混流和重新封装 | GUI 同目录、`tools`、三类 `PATH`、WinGet、各固定磁盘根部的下载目录 |

路径存在时，“工具路径”下方会显示“工具已就绪”。程序首次显示窗口时会自动检测；路径标红时会提示直连 GitHub Release 自动下载、浏览指定文件或暂不处理。点击“自动检测”、开始下载或转换文件时也会重新检查。

GUI 自动下载固定的 `N_m3u8DL-RE 0.6.0-beta` 和 Gyan FFmpeg `8.1.2 essentials`，使用不经过 Windows 系统代理的 HTTPS 连接直连 GitHub Release，校验下载长度、压缩包 SHA-256、解压长度和最终 EXE SHA-256 后才安装到 `%LOCALAPPDATA%\N_m3u8DL-RE-GUI\tools`。取消、失败或关闭窗口不会覆盖原有可用工具，并会清理本次临时文件。

Release ZIP 中的 `Setup-dependencies.cmd` 和源码目录中的 `setup-dependencies.ps1` 仍可作为备用安装方式。

使用 WinGet 安装 FFmpeg：

```powershell
winget install --id Gyan.FFmpeg -e --accept-source-agreements --accept-package-agreements
```

安装后重新打开程序即可自动检测。

## 2. 下载普通链接

在“视频链接”中输入真实播放列表地址，例如：

```text
https://media.example.com/series/episode-01/index.m3u8?token=REDACTED
```

程序直接启动目标进程，不经过 `cmd.exe` 或 PowerShell 解释，因此 URL 中的 `&`、路径空格和中文名称不会被拆成多个命令。

文件名默认从 URL 推断：

```text
/series/episode-01/index.m3u8  ->  episode-01
/movie/master.m3u8             ->  movie
/video/demo.mpd                ->  demo
```

文件名始终可以手动修改。不要输入扩展名；输出扩展名由下载结果决定。

默认参数相当于：

```text
--auto-select --ffmpeg-binary-path <ffmpeg.exe> -M format=mp4
```

任务运行时会锁定输入控件，“密钥...”和“转换文件...”会变成灰色且不可点击。点击“取消”会终止下载器及其 FFmpeg 子进程，并清理该任务独占临时目录中的下载分片。任务结束后控件会恢复。

## 3. 猫抓 Blob 播放列表

下面这种地址不能直接下载：

```text
blob:chrome-extension://extension-id/random-id
```

它只在创建它的 Chrome/扩展进程中有效。针对猫抓扩展：

1. 打开猫抓的 `M3U8解析器`。
2. 展开“查看所有切片和下载进度”。
3. 点击“原始m3u8”。
4. 在文本框中按 `Ctrl+A`、`Ctrl+C`。
5. 回到本程序点击“粘贴”，或在链接框中按 `Ctrl+V`。

程序识别到 `#EXTM3U` 后会创建临时本地播放列表。任务结束或程序关闭时会删除。

导入前会检查普通分片、`#EXT-X-KEY`、`#EXT-X-MAP`、`#EXT-X-MEDIA` 和 MPD XML 中的引用。HLS 中包含相对地址、根相对地址或 `//cdn...` 协议相对地址时会拒绝导入，因为本地文件无法知道原网页的 Base URL。MPD 如果在相应作用域提供绝对 `<BaseURL>`，其下的相对 `SegmentTemplate` 可以正常导入。

## 4. 手动 HLS AES-128 密钥

多数标准 HLS 播放列表会通过 `#EXT-X-KEY:URI=...` 让下载器自动取得密钥，不需要手填。仅在自动获取失败且你合法持有密钥时使用“密钥...”。

支持三种输入：

- 32 位十六进制 HEX，例如：`00112233445566778899aabbccddeeff`
- 16 字节 Base64，例如：`ABEiM0RVZneImaq7zN3u/w==`
- 包含 16 字节原始数据、HEX 文本或 Base64 文本的密钥文件

HEX 前缀 `0x` 或 `0X` 会自动移除。IV 也是 16 字节；播放列表没有要求自定义 IV 时留空。

点击“确定”后：

- 主窗口按钮显示“密钥已设置”。
- 主按钮显示“使用密钥下载”。
- KEY/IV 不保存到设置文件。
- KEY/IV 被转换为受限 ACL 的随机临时文件，命令行只传递文件路径。
- 已知 HEX、Base64 和文件内容会从 GUI 日志中脱敏。

清除密钥：再次打开“密钥...”，点击“清空”，然后“确定”。

如果旧输出文件已经存在，建议给重新处理的任务使用新名称，例如 `episode-01_decrypted`。验证新文件完整后再删除旧文件。

## 5. 转换已有媒体文件

“转换文件...”适用于：

- TS 内容完整但播放器只显示很短时长。
- 进度条无法拖动或总时长不正确。
- 想把已经可读取的 TS/M2TS/MKV/MOV/WebM/FLV 重新封装为 MP4。

操作步骤：

1. 点击“转换文件...”。
2. 选择源文件。
3. 选择 MP4 输出路径。
4. 等待“转换完成”。

核心参数为：

```text
-fflags +genpts -map 0:v:0? -map 0:a? -c copy -avoid_negative_ts make_zero -movflags +faststart
```

`-c copy` 表示不重新编码，因此速度快且不损失画质。此功能不能可靠解密已经丢失 HLS 分片边界的文件；真正缺 KEY 时应使用原播放列表和手动密钥重新运行下载。

## 6. 保存和日志

- “完成后打开目录”会在成功后打开实际输出目录。
- 下载器即使不输出换行，界面仍会从原始字符流实时提取当前轨道的分片数和百分比；运行日志每约 5% 保留一条简洁里程碑，不保存每次终端重绘。
- “复制日志”复制当前窗口日志；分享前仍应人工检查 URL token、Cookie 和站点标识。
- “清空”会同时清空界面和尚未刷新到界面的日志队列。
- 设置文件只包含工具路径、保存目录和完成选项，位于：

```text
%LOCALAPPDATA%\N_m3u8DL-RE-GUI\settings.xml
```

媒体 URL、GUI 日志、KEY 和 IV 不写入该设置文件。
