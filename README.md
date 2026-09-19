# InterviewScribe 面试转写助手

InterviewScribe 是一个面向 Windows 10/11 x64 的本地图形界面工具，用来把已有录屏或视频中的语音转成可交给 GPT 分析的文本。它优先解决中英文混合、说话人区分、标点和时间轴问题，并将转写留在本机。

## 它如何工作

1. 在界面中选择一个视频或音频文件。
2. 选择识别语言：默认同时选择“中文”和“English”，适合中英文混说；也可以只保留一种语言作为识别提示。
3. FFmpeg 在本地提取和规范化音频；如果录屏把麦克风和系统声音分成多条音轨，会自动合并，不会只识别第一条。
4. MOSS-Transcribe-Diarize Q8 在本地完成中英文识别、时间戳和说话人区分。
5. 每次都同时生成 TXT（便于交给 GPT）、SRT（标准字幕）和 JSON（结构化原始结果）。

界面中的两个导出开关用于控制可读文本：可以让 TXT 显示或隐藏时间轴，也可以让 TXT 和 SRT 显示或隐藏说话人。SRT 为保持标准字幕格式始终保留时间码；JSON 始终保留完整时间戳和说话人数据，方便以后重新导出。两个语言选项至少要选择一个；双选时使用模型原生的中英混合识别，单选时会向模型提供对应语言提示，而不是把另一种语言强行过滤掉。

安装包自带 FFmpeg/FFprobe、InterviewScribe EngineHost 命令行宿主和 transcribe.cpp Windows CPU/Vulkan 原生运行库，不自带模型。第一次开始转写时，应用会从 Hugging Face 的固定 revision 下载 `MOSS-Transcribe-Diarize-Q8_0.gguf`（约 987 MB）。只有文件大小和 SHA-256 都匹配发布锁文件中的值时才会加载；之后可以断网使用。

## 使用条件

- Windows 10 1809 或更新版本，64 位。
- 建议 16 GB 或更多内存；推荐支持 Vulkan 的 NVIDIA、AMD 或 Intel 显卡与较新驱动。如果原生引擎报告 Vulkan 推理失败，应用会自动用 CPU 重试。Q8 模型本身约 1 GB，实际运行还需要音频缓冲和上下文的显存/内存。
- 首次使用需能访问 Hugging Face；原生依赖已在安装包内。
- 请为长时间录屏预留足够的临时磁盘空间。
- 为避免超长 WAV 一次性读入造成内存耗尽，当前版本单个文件最长支持 2 小时；更长文件请先分段。

转写不会自动上传视频。不过，将导出的 TXT 提交给任何在线 GPT 服务前，请先检查并删除姓名、电话、公司机密等敏感信息。

卸载程序不会主动删除已下载的模型、失败任务日志和用户结果。如需彻底清理，请在卸载后手动删除 `%LOCALAPPDATA%\InterviewScribe`；用户自行选择的输出目录需单独处理。

## 从源码构建

需要：

- Windows 10/11 x64
- .NET 10 SDK x64
- PowerShell 5.1 或 PowerShell 7
- 如需生成安装包，安装 Inno Setup 6：`winget install --id JRSoftware.InnoSetup -e`

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 -Version 0.2.0
```

脚本会：

1. 按锁文件下载 FFmpeg 和 transcribe.cpp，同时核对文件大小与 SHA-256。
2. 还原 .NET 依赖、运行测试并发布 `win-x64` 自包含程序。
3. 将已验证的原生组件和第三方声明放入发布目录。
4. 用 Inno Setup 生成 `artifacts\release\InterviewScribe-Setup-x64.exe`，并在同一目录写入 `SHA256SUMS.txt`。

只准备原生依赖：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Fetch-NativeDependencies.ps1
```

只生成可便携运行目录，不调用 Inno Setup：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 -SkipInstaller
```

下载归档缓存在 `artifacts\downloads`，发布过程不会把任何模型、用户视频、临时音频或转写结果打进安装包。这些路径已由 `.gitignore` 排除。

## 安装与桌面快捷方式

双击安装包后，“在桌面创建快捷方式（推荐）”默认勾选，也可以在安装向导中取消。需要脚本化安装时，可显式控制这个选项：

```powershell
# 静默安装并创建桌面快捷方式
.\InterviewScribe-Setup-x64.exe /VERYSILENT /TASKS=desktopicon

# 静默安装但不创建桌面快捷方式
.\InterviewScribe-Setup-x64.exe /VERYSILENT /MERGETASKS=!desktopicon
```

卸载应用时，安装器创建的桌面和开始菜单快捷方式会一并移除。卸载不会删除模型缓存和用户转写结果。

## 依赖可复现性

仓库中的 `packaging/dependencies.lock.json`（安装后位于程序目录的 `dependencies.lock.json`）记录原生运行库和模型的锁定值：URL 不使用 `latest`，Hugging Face 使用完整 commit revision，所有大文件都锁定字节数和 SHA-256。如果升级任何依赖，必须同时审查许可证、更新锁文件和应用内的模型描述，并在干净环境重新校验。

请同时阅读 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 开发注意

transcribe.cpp v0.2.3 官方 Windows CPU/Vulkan release asset 只提供 `transcribe.dll`、`ggml-vulkan.dll`、CPU 后端及其依赖，**不包含** `transcribe-cli.exe`。InterviewScribe 自己构建并打包 `InterviewScribe.EngineHost.exe` 作为可控的命令行边界，它再通过原生 API 调用已锁定的 `transcribe.dll`。这个独立进程也能在原生库异常时保护图形界面主进程。打包脚本会同时校验 EngineHost 和 `transcribe.dll`，不会伪造或下载未锁定的执行文件。

## 许可与发布

InterviewScribe 自身代码采用 [MIT License](LICENSE)。第三方软件仍分别受其原许可证约束，不因本项目的许可证而改变；具体归属和许可见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

开发构建默认没有代码签名。在没有配置可信任的 Windows 代码签名证书时，对外分发的安装包可能会显示“未知发布者”；这不影响本地运行，但公开发布前应配置签名。
