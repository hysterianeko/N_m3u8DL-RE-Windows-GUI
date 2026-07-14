# 故障排查

## 双击 N_m3u8DL-RE.exe 没反应

`N_m3u8DL-RE.exe` 是命令行程序，没有图形界面。没有输入参数时窗口会立即退出。请启动本 GUI，或在 PowerShell 中传入播放列表地址。

## 工具路径标红

推荐把下载器放在 GUI 同目录、`tools` 子目录，或任一固定磁盘根部的 `Downloads` / `Download` / `下载` 目录（例如 `E:\Downloads`）。FFmpeg 可以安装到系统 `PATH` 或 WinGet 默认位置：

```text
<程序目录>\N_m3u8DL-RE.exe
<程序目录>\tools\N_m3u8DL-RE.exe
%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe
```

点击“自动检测”或“浏览...”重新选择。Release ZIP 用户也可以运行 `Setup-dependencies.cmd`。WinGet 安装 FFmpeg 后需要重新启动程序。

## Blob 地址无法下载

这是预期行为。`blob:` 是浏览器内存对象，不是网络 URL。按照 [使用说明中的猫抓流程](USAGE.zh-CN.md#3-猫抓-blob-播放列表) 复制“原始m3u8”的完整 `#EXTM3U` 内容。

## 播放列表包含相对地址

HLS 导入文本必须包含完整的 `https://` 分片、密钥、初始化文件和附加音轨地址。程序会拒绝：

```text
segments/001.ts
/video/001.ts
//cdn.example.com/video/001.ts
URI="key.key"
URI="init.mp4"
```

请返回猫抓复制已经补全 URL 的“原始m3u8”，或寻找真实的在线播放列表地址。

MPD 可以在作用域内提供绝对 `<BaseURL>https://cdn.example.com/video/</BaseURL>`，然后使用相对的 `media` / `initialization` 模板。没有绝对 BaseURL 的相对 MPD 引用仍会被拒绝。

## 日志提示“获取 KEY 失败”

单条 `ERROR` 不一定表示整个任务失败。先看最终退出状态和成品：

- 成品能完整播放、声音正常、可读到完整时长：通常不需要手动 KEY。
- 只有开头能播放、FFmpeg 报解密/数据错误：需要检查 KEY。
- 标准 AES-128 密钥 URL 可访问时，下载器通常会自动获取。

自动获取失败且你合法持有密钥时，点击“密钥...”，输入 32 位 HEX、16 字节 Base64 或选择密钥文件，然后从原播放列表重新处理。

## 下载显示 Done，但 TS 时长很短

TS 容器的时长探测可能只看到第一个时间戳周期。先使用“转换文件...”无损重新封装为 MP4。如果转换成功且输出时长完整，原始媒体内容是有效的，不需要密钥。

## 401、403 或连接失败

常见原因：

- 签名 URL 或 token 已过期。
- 服务器要求登录 Cookie、Referer 或特定 User-Agent。
- 密钥 URL 与媒体 URL使用不同权限。
- 代理、DNS 或防火墙阻止请求。

重新从浏览器获取最新播放列表地址。不要在公开 Issue 中粘贴 Cookie、Authorization 或完整签名 URL。当前 GUI 没有通用请求头编辑器；复杂认证场景可直接使用 `N_m3u8DL-RE` 的 `-H` 参数。

## 退出代码为 0，但没有识别到输出

程序只把本次任务名对应且确实发生变化的媒体文件视为结果，避免误报保存目录中其他程序生成的文件。点击“打开目录”检查是否存在未识别扩展名，并附上脱敏日志报告问题。

## Windows SmartScreen 提示未知发布者

本地构建 EXE 没有代码签名证书。确认文件来自你自己的构建或可信仓库后再运行。可以用 SHA-256 校验下载文件，并与 Release 附带的 `SHA256SUMS.txt` 对比：

```powershell
Get-FileHash .\M3U8视频下载器.exe -Algorithm SHA256
```

## 取消后仍有临时文件

从 1.2.2 起，每个下载任务都使用带所有权标记的独占临时目录。取消会通过 Windows Job Object 终止下载器和 FFmpeg 子进程，确认退出后只删除这个受管目录中的 `.ts`、`.m4s`、`.tmp` 等下载分片，同时清理 GUI 创建的播放列表和密钥临时文件。

如果取消恰好发生在最终合并或直接录制阶段，上游可能已经在保存目录写入部分媒体文件。GUI 不会根据文件名猜测并删除保存目录内容，以免误删用户原有文件；这种情况应人工确认后处理。

## 下载器中文日志显示乱码

1.2.2 已按照当前 Windows 系统 ANSI 代码页读取 `N_m3u8DL-RE` 输出，同时继续按 UTF-8 读取 FFmpeg。旧版本出现 `����` 时请升级；`--no-ansi-color` 只控制颜色，不能修复编码。
