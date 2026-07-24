# N_m3u8DL-RE Windows GUI

一个面向 Windows 10/11 的轻量图形界面，用于调用
[`nilaoda/N_m3u8DL-RE`](https://github.com/nilaoda/N_m3u8DL-RE) 下载 HLS/DASH，
并调用 [FFmpeg](https://ffmpeg.org/) 混流或无损重新封装媒体文件。

这是独立的非官方 GUI，不隶属于、也不代表 `nilaoda/N_m3u8DL-RE`。本项目没有复制上游源码；程序把用户提供的官方 `N_m3u8DL-RE.exe` 作为外部进程调用。详细来源和许可见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

![主窗口](docs/assets/main-window.png)

## 下载 Windows 版本

打开 [Releases](https://github.com/hysterianeko/N_m3u8DL-RE-Windows-GUI/releases/latest)，按需要下载：

- `M3U8-Video-Downloader-v1.4.0-win-x64.zip`：完整 GUI 包，包含主程序、网页捕获所需的 WebView2 DLL、快速开始和备用依赖安装脚本。解压后保持这些文件在同一目录。
- `SHA256SUMS.txt`：ZIP 的 SHA-256 校验值。

发布包不捆绑第三方可执行文件，因此仍然很小。GUI 会先自动查找现有工具；只有缺失且用户明确选择“自动下载”时，才使用当前网络直连固定的 GitHub Release，不读取 Windows 系统代理，并在下载后校验 SHA-256。通常下载 `N_m3u8DL-RE` 约 5 MB、FFmpeg Essentials 约 32 MB，安装到当前用户的 `%LOCALAPPDATA%\N_m3u8DL-RE-GUI\tools`，不需要管理员权限。自动下载失败时仍可浏览并指定已有 EXE。

运行要求：Windows 10/11 x64、.NET Framework 4.8。实际下载和混流仍需要 `N_m3u8DL-RE.exe` 与 `ffmpeg.exe`，可由 GUI 按需准备或手动指定。本地构建没有数字签名证书，Windows SmartScreen 可能显示“未知发布者”。

## 功能

- 输入在线 `.m3u8`、`.mpd` 或本地播放列表文件。
- 自动从 URL 推断名称；`index.m3u8` / `master.m3u8` 会使用有意义的父目录名。
- 自动选择最佳视频、音频和字幕轨道，并默认混流为 MP4。
- 自动查找 GUI 同目录、`tools`、`PATH`、WinGet，以及各固定磁盘根部的 `Downloads` / `Download` / `下载` 目录中的外部工具。
- 缺少工具时提供自动下载、手动浏览和暂不处理三种选择；自动下载固定版本并校验压缩包与最终 EXE。
- 正确显示下载器中文日志，实时提取分片百分比，并把高频终端重绘压缩为可读的进度里程碑。
- 主按钮按任务状态切换为“暂停”“继续”“重试下载”和“完成”；暂停或失败时保留分片缓存，继续时只补缺失或不完整分片。
- 关闭程序或重启 Windows 后仍可恢复未完成任务；启动时只恢复为暂停状态，不会自动联网，用户可选择“继续”或“清除缓存”。
- 单独的“取消/清除缓存”会终止整个进程树并清理该任务的专属下载分片临时目录。
- 任务运行时禁用并灰显“密钥...”和“转换文件...”，结束后恢复。
- “从网页捕获”可同时发现普通 m3u8 请求和页面创建的 Blob 播放列表；候选项可展开查看按原顺序排列的全部切片、KEY、MAP 和子播放列表地址。
- CDN 拒绝独立大小探测时，可用已捕获主列表的平均/峰值码率和媒体列表总时长回退估算大小。
- 捕获到 Blob 时自动导入完整 `#EXTM3U` 正文；主输入框仍拒绝无法由外部程序直接访问的裸 `blob:` 地址，并保留手动粘贴正文作为备用方式。
- 普通 HTTP m3u8 已由浏览器读取正文时也改用本地导入，避免 `N_m3u8DL-RE` 再次请求受保护的播放列表；选中主列表会自动解析到已捕获的实际媒体子列表。
- 播放时关联真实分片请求，复用 Referer、User-Agent、Cookie 和安全的站点自定义头；受 Cloudflare 客户端指纹保护的 Surrit/私有 Token 分片会自动经仅监听 `127.0.0.1` 的 Windows cURL 通道获取，再交由 `N_m3u8DL-RE` 缓存、排序和合并。
- 手动 HLS AES-128 KEY/IV：支持 32 位 HEX、16 字节 Base64 和密钥文件。
- 将现有 TS/M2TS/MKV/MOV/WebM/FLV 等文件无损重新封装为 MP4。
- 普通设置只保存工具路径和界面选项；未完成任务所需的 URL、播放列表、浏览器请求头、KEY/IV 和输出参数仅写入当前 Windows 用户可解密的 DPAPI 恢复清单，完成或清除任务后立即删除。

## 快速使用

1. 粘贴真实的 `https://...m3u8` 或 `https://...mpd` 地址。
2. 选择保存目录并检查自动生成的文件名；文件名可以修改，不需要输入扩展名。
3. 保持“混流为 MP4”勾选，点击“开始下载”。
4. 下载结束后以界面最终状态和成品完整播放情况为准。

下载期间点击主按钮会暂停并保留分片，再次点击“继续”会复用缓存。网络中断导致失败时点击“重试下载”；直接关闭程序会安全停止下载并保留任务，下次打开仍显示“继续 / 清除缓存”。未完成任务最多保留 3 天；成功、取消或清除缓存会立即删除。编辑链接不会暗中删除旧分片，确认开始不同任务时才会提示并清理旧缓存。

默认下载参数等价于：

```text
--auto-select --ffmpeg-binary-path <ffmpeg.exe> -M format=mp4
```

详细步骤见 [使用说明](docs/USAGE.zh-CN.md)，常见错误见 [故障排查](docs/TROUBLESHOOTING.zh-CN.md)。

## Blob 播放列表

`blob:...` 只存在于创建它的浏览器进程中，不能把这个临时 URL 直接交给桌面下载器。点击“从网页捕获”，在内嵌浏览器中打开视频页并开始播放；捕获窗口会在页面调用 `URL.createObjectURL` 时读取 Blob 中的完整 `#EXTM3U` 正文。普通网络 m3u8 与 Blob 都会出现在下方列表中。

列表根行显示总大小和切片数。点击行首 `+` 可按播放列表原始顺序展开每一个切片，同时显示 KEY、初始化片段、附加媒体和子播放列表等关联资源的完整地址；如果网页实际请求为同路径但带额外查询参数，展开行还会显示实际地址。让视频继续播放到窗口提示“已捕获真实分片请求”，再选择 Blob 并点击“导入此 Blob 并下载”。程序会把正文交给与手动粘贴相同的导入流程，再由 `N_m3u8DL-RE` 下载和合并。

捕获脚本会在网页具有 HTTP(S) Base URL 时自动补全相对引用。如果网页没有可用的网络 Base URL，播放列表中的引用必须已经是完整地址，否则仍会拒绝导入。无法在内嵌浏览器中复现的扩展页面可继续使用备用方式：复制从 `#EXTM3U` 开始的完整“原始m3u8”正文，再回到本程序点击“粘贴”。

HLS 文本中的分片、KEY、MAP 和附加媒体地址必须是完整 URL。MPD 可以使用完整 URL，也可以在作用域内提供绝对 `<BaseURL>` 后使用相对 SegmentTemplate。无法确定 Base URL 的相对引用会被拒绝，避免被错误解析到本机临时目录。

## AES-128 密钥

标准 HLS 通常会从 `#EXT-X-KEY:URI=...` 自动取得密钥，不需要手填。只有自动获取失败且你合法持有密钥时，才点击“密钥...”输入：

- 32 位 HEX，例如 `00112233445566778899aabbccddeeff`
- 解码后正好 16 字节的 Base64，例如 `ABEiM0RVZneImaq7zN3u/w==`
- 包含 16 字节原始数据、HEX 文本或 Base64 文本的文件

IV 同样必须是 16 字节；没有明确值时留空。运行时 KEY/IV 会被转换为仅当前 Windows 用户和 `SYSTEM` 可访问的随机临时文件，命令行只传临时路径；未完成任务的恢复副本使用当前用户 DPAPI 加密，任务完成或清除后删除。不要在 Issue、截图或日志中公开真实 KEY、Cookie、Authorization、签名 URL 或 token。

已经合并且仍加密的媒体文件通常不能只靠补一个 KEY 可靠修复，应从保留分片边界的原播放列表重新下载。Widevine、PlayReady 等 DRM 许可证不等同于普通 HLS AES-128 KEY，本项目不提供 DRM 绕过功能。

## 转换已有文件

当 TS 可以播放但总时长或进度条不正确时，点击“转换文件...”，选择源文件和 MP4 输出路径。转换使用 `-c copy`，不会重新编码或损失画质。程序先写入同目录随机 `.partial.mp4`，只有 FFmpeg 成功退出才原子替换最终文件；取消或失败不会破坏已有目标。

## 从源码构建

项目目标为 .NET Framework 4.8 / C# 5，不依赖 .NET SDK 或 NuGet CLI。首次构建且本地缺少 WebView2 SDK 文件时，会从 NuGet 官方源下载固定的 `Microsoft.Web.WebView2 1.0.2957.106` 包，校验包和三个目标 DLL 的 SHA-256 后再使用；缓存完整时可以离线重建。可以直接打开 `M3u8DownloaderGui.sln`，也可以使用 Windows 自带编译器：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

只生成项目内产物：

```powershell
.\build.ps1 -SkipDesktopCopy
```

生成包含全部运行依赖的 ZIP 和校验文件：

```powershell
.\package.ps1 -Version 1.4.0
```

构建脚本会先编译并运行 `SelfTests.exe`，再生成 DPI 感知的 WinForms EXE。开发细节见 [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md)。

## 安全与合法使用

请只下载、转换你有权访问和保存的内容，并遵守内容提供方条款、版权规定和所在地区法律。安全问题与敏感信息处理见 [SECURITY.md](SECURITY.md)。

本 GUI 源码采用 [MIT License](LICENSE)。`N_m3u8DL-RE`、FFmpeg 及其发行包使用各自上游许可证；参见 [第三方声明](THIRD_PARTY_NOTICES.md)。
