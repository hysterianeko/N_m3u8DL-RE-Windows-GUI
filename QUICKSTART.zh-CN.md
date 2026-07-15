# M3U8 视频下载器快速开始

## 第一次使用

1. 双击 `M3U8-Video-Downloader.exe`。
2. 程序会自动查找电脑中已有的 `N_m3u8DL-RE.exe` 和 `ffmpeg.exe`。
3. 缺少工具时选择“是”可直连 GitHub Release 自动下载（不使用 Windows 系统代理），选择“否”可浏览并指定本机文件，选择“取消”则暂不处理。
4. 自动下载会固定版本并校验 SHA-256，不需要管理员权限；通常总下载量约 38 MB。

已经安装好两个工具时不会重复下载。程序会优先检查已保存路径、GUI 同目录和便携 `tools` 子目录，再检查受管理工具目录、`PATH`、WinGet，以及各固定磁盘根部的 `Downloads` / `Download` / `下载` 目录。路径也可以在界面中手动选择。ZIP 中的 `Setup-dependencies.cmd` 仅作为备用安装方式。

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
