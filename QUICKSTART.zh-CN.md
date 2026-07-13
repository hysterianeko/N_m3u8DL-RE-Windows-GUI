# M3U8 视频下载器快速开始

## 第一次使用

1. 双击 `Setup-dependencies.cmd`。
2. 脚本会从 `nilaoda/N_m3u8DL-RE` 官方 GitHub Release 下载并校验 Windows x64 版本。
3. 如果电脑尚无 FFmpeg，脚本会通过 WinGet 安装 `Gyan.FFmpeg`。
4. 安装完成后会启动 `M3U8-Video-Downloader.exe`。

已经安装好两个工具时，可以直接双击 `M3U8-Video-Downloader.exe`。程序会依次检查同目录、`tools` 子目录、用户下载目录、`PATH` 和 WinGet 位置。路径也可以在界面中手动选择。

## 下载视频

1. 粘贴真实的 `https://...m3u8` 或 `https://...mpd` 地址。
2. 选择保存目录。
3. 检查自动生成的文件名，必要时修改。
4. 点击“开始下载”。

`blob:chrome-extension://...` 不是网络地址，不能直接下载。请在扩展的解析页面复制从 `#EXTM3U` 开始的完整播放列表，再点击本程序的“粘贴”。

多数 AES-128 播放列表会自动取得 KEY。只有自动获取失败并且你合法持有密钥时，才需要点击“密钥...”输入 32 位 HEX、16 字节 Base64 或密钥文件。IV 不明确时留空。

## 来源与许可

本程序是非官方 GUI，不隶属于 `nilaoda/N_m3u8DL-RE`。上游地址：
https://github.com/nilaoda/N_m3u8DL-RE

完整说明见仓库 README、`LICENSE` 和 `THIRD_PARTY_NOTICES.md`。请只下载你有权访问和保存的内容；本程序不提供 DRM 绕过功能。
