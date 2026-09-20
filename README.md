# InterviewScribe 面试转写助手

InterviewScribe v0.4.0 是一个面向 Windows 10/11 x64 的图形界面工具，用来把已有录屏、视频或音频转成便于交给 GPT 分析的文字。界面支持 11 种语言、GPU 优先推理、说话人/时间轴开关、实时全流程百分比与预计剩余时间，以及 TXT、Markdown、Word、PDF、SRT 和 JSON 多格式导出。

## 三种识别模式

| 模式 | 文字与时间轴 | 说话人 | 运行位置 | 网络要求 |
| --- | --- | --- | --- | --- |
| **MOSS 本地快速** | MOSS-Transcribe-Diarize Q8 | MOSS | 本机；Vulkan 优先，失败时自动改用 CPU | 首次下载 MOSS 权重；之后可离线 |
| **Qwen3-ASR 1.7B 本地高精度** | Qwen3-ASR-1.7B-hf 生成文字，Qwen3-ForcedAligner-0.6B-hf 生成时间对齐 | 本地 MOSS | 本机；Qwen 自动选择 CUDA 或 CPU，MOSS 使用 Vulkan/CPU | 首次安装运行组件、MOSS 和两套 Qwen 权重；安装完成后严格离线 |
| **Qwen 官方 SDK 云端高精度** | DashScope `qwen3-asr-flash` 生成文字，本地 VAD 分段提供时间边界 | 本地 MOSS | Qwen 文字识别在云端，说话人轨道与结果合并在本机 | 每次识别都需联网、有效 API Key，并可能产生服务费用 |

两种高精度模式都会先运行一次 MOSS，取得说话人轨道，而且都不会用 MOSS 的文字替换 Qwen 文字。本地模式用 ForcedAligner 生成逐词时间戳，再按时间重叠合入 MOSS 说话人；SDK 模式先用本地 VAD 和 MOSS 说话人边界切分音频，再由云端逐段转写，因此提供的是分段时间轴，不是云端逐词时间戳。说话人标签是模型估计的“说话人 1、说话人 2”，不是身份识别，重叠说话或音质较差时仍应人工复核。

本地路径和 SDK 路径不是同一个模型的两种下载方式：本地路径运行固定 revision 的 `Qwen3-ASR-1.7B-hf`；SDK 路径调用由云服务管理的 `qwen3-asr-flash`，不需要在电脑上保存 Qwen 权重。

## 识别语言

界面可选中文、English、粤语、日语、韩语、法语、德语、西班牙语、葡萄牙语、俄语和意大利语。单选会给本地 Qwen 对应的语言提示；选择两种及以上或使用 SDK 路径时启用自动语言识别，而不是把模型限制在勾选集合中。

当前打包的 MOSS/transcribe.cpp 端口优先保障中文和英文。需要粤语、日语、韩语或欧洲语言的稳定文字与逐词时间轴时，应选择 Qwen 本地高精度模式。

## 当前工作流

1. 在界面中选择视频或音频，以及输出文件夹。
2. 选择一种或多种识别语言。本地路径单选会给模型语言提示，多选或 SDK 路径则启用自动识别。
3. 选择识别模式和一种或多种输出格式，并决定可读结果是否显示时间轴和说话人。
4. FFprobe 检查媒体时长和音轨；存在多条音轨时，FFmpeg 会把麦克风与系统声音合并成 16 kHz 单声道音频，原视频不会被修改。
5. MOSS 在本机生成基础文字、时间轴和说话人轨道。快速模式直接使用这份结果；高精度模式继续运行所选 Qwen 路径，并在本机合并说话人。
6. 程序以原子写入方式生成所选结果；成功后删除任务使用的临时 WAV。

当前版本单个文件最长支持 2 小时。更长的录屏请先分段，以免一次性处理占用过多内存和临时磁盘空间。

## 安装模型与运行组件

安装包包含 FFmpeg/FFprobe、InterviewScribe EngineHost、transcribe.cpp Windows CPU/Vulkan 运行库和 Qwen 辅助程序，但**不包含任何模型权重**。

### MOSS 本地快速

第一次开始识别时，应用会从 Hugging Face 的固定 revision 下载 `MOSS-Transcribe-Diarize-Q8_0.gguf`（约 987 MB）。程序会核对锁文件记录的字节数和 SHA-256；校验成功后保存在：

```text
%LOCALAPPDATA%\InterviewScribe\models\MOSS-Transcribe-Diarize-Q8_0.gguf
```

以后选择 MOSS 本地快速模式时可以断网运行。

### Qwen3-ASR 1.7B 本地高精度

选择该模式后，点击界面里的“安装高精度组件…”。电脑需要先安装 64 位 Python 3.11 或 3.12；打开的 PowerShell 窗口会自动识别 Python 官方安装和 `uv` 注册的解释器，基于它准备独立的 Python 虚拟环境，并下载、校验程序锁定的 MOSS 说话人模型及两套 Qwen 权重：

- `Qwen/Qwen3-ASR-1.7B-hf`
- `Qwen/Qwen3-ForcedAligner-0.6B-hf`

默认位置为：

```text
%LOCALAPPDATA%\InterviewScribe\qwen-runtime\.venv
%LOCALAPPDATA%\InterviewScribe\models\Qwen3-ASR-1.7B-hf
%LOCALAPPDATA%\InterviewScribe\models\Qwen3-ForcedAligner-0.6B-hf
```

安装器会校验 MOSS 的文件长度和 SHA-256，并校验两套 Qwen 模型的完整分片与 revision 标记。通过全部校验后，本地高精度识别会设置 Hugging Face/Transformers 离线环境，只从本地目录加载，不会静默回退到网络。

也可以在程序目录手动运行同一安装脚本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\qwen\Install-QwenRuntime.ps1
```

安装脚本默认自动检测 NVIDIA 显卡：检测到时安装锁定的 CUDA 版 PyTorch，否则安装 CPU 版。也可以手动指定 CPU：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\qwen\Install-QwenRuntime.ps1 -TorchBackend Cpu
```

### Qwen 官方 SDK 云端高精度

选择该模式后，先点击“安装 SDK 运行组件…”。此入口安装 SDK 所需的 Python 环境和本地 MOSS 说话人模型，但不下载 Qwen 本地权重。也可以手动执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\qwen\Install-QwenRuntime.ps1 -SkipLocalModels
```

随后在界面的密码框中输入 DashScope API Key，或事先设置 Windows 用户环境变量：

```powershell
[Environment]::SetEnvironmentVariable('DASHSCOPE_API_KEY', '你的_API_Key', 'User')
```

重新打开应用后环境变量才会生效。界面输入的 Key 只在当前运行内存和 Qwen 子进程环境中使用，不写入应用设置或日志。

DashScope SDK 默认连接中国内地（北京）区域，API Key 也必须与区域匹配。若你的百炼工作空间在其他区域，请同时配置该区域和工作空间 ID；例如新加坡区域：

```powershell
[Environment]::SetEnvironmentVariable('DASHSCOPE_API_REGION', 'ap-southeast-1', 'User')
[Environment]::SetEnvironmentVariable('DASHSCOPE_WORKSPACE_ID', '你的_WorkspaceId', 'User')
```

设置后重新打开应用。区域可用性、模型权限和计费以阿里云 Model Studio 控制台为准。

## 离线与隐私边界

- **MOSS 本地快速**：模型准备完成后，视频、提取音频和转写都留在本机。
- **Qwen 本地高精度**：MOSS、Qwen ASR 和强制对齐都在本机执行；模型准备完成后可断网运行。
- **Qwen 官方 SDK**：原视频仍留在本机，但提取出的音频片段会发送给 DashScope。云端数据处理、留存和计费以服务方条款为准。
- 无论选择哪种模式，应用都不会自动把最终 TXT 发给 GPT。手动提交前请检查并删除姓名、电话和公司机密等敏感信息。
- 卸载程序不会删除 `%LOCALAPPDATA%\InterviewScribe` 下的模型、运行环境、失败任务日志，也不会删除用户选择的输出目录；如需彻底清理，应在卸载后手动删除这些内容。

## 输出格式

每个任务可以多选以下格式，同一次导出使用相同的文件名主体：

- **TXT**：为阅读和交给 GPT 分析准备；可选择显示/隐藏时间轴和说话人。
- **Markdown (MD)**：使用标题和段落结构，方便在笔记工具或 GitHub 中阅读。
- **Word (DOCX)**：标准 Office Open XML 文档，转写文字、时间轴和说话人会按当前选项写入。
- **PDF**：生成 A4 分页文档，并嵌入实际用到的 Unicode 字形子集，便于换机阅读。
- **SRT**：标准字幕格式，始终保留时间码；可选择显示/隐藏说话人。
- **JSON**：保留模型、后端、完整分段、时间戳、说话人和警告等结构化数据，不受两个可读输出开关影响。

如果目标文件名已存在，程序会选择新的名称，不会覆盖已有结果。多格式导出会先生成全部临时文件；任一格式失败或任务取消时，本次导出不会留下半成品。

PDF 会优先使用 Windows 的 Microsoft YaHei UI（微软雅黑 UI）中文字体，也支持 Noto Sans SC、黑体、等线等备选字体。日文和韩文 PDF 会分别使用可用的 CJK 字体和 Malgun Gothic 等字体回退。如果精简的 Windows 缺少必要字体，程序会明确拒绝 PDF 导出并回滚整组文件，而不是生成乱码。

## 进度与运行诊断

界面显示单调不回退的全流程百分比、已用时间和预计剩余时间。模型下载、音频提取和 Qwen 分段会使用实际子任务进度；MOSS 核心推理在无法得到更细粒度样本时会保持当前百分比并显示“正在估算”，不用虚假动画冒充实际进度。

运行记录会实时显示当前百分比、ETA、阶段、实际使用的 CUDA/Vulkan/CPU 后端及自动回退信息。

## 性能与准确性说明

“本地快速”和“高精度”是产品中的工作模式，不是对所有录音都成立的基准结论。MOSS 快速模式只做一次本地模型推理；两种高精度模式会额外运行 Qwen，并继续使用 MOSS 生成说话人轨道，因此通常需要更多时间或资源。实际效果受口音、噪声、多人抢话、录音码率、显卡和驱动影响。

截至 v0.4.0，本次开发所用笔记本尚未完成本地 Qwen 权重的端到端实跑，因此仓库不提供未经实测的准确率、实时倍速或显存占用数字。发布前后的判断应以同一批真实面试录屏对三种模式进行对照，并人工核对关键姓名、公司名、技术术语、数字、说话人和时间轴。

## 使用条件

- Windows 10 1809 或更新版本，64 位。
- 使用任一 Qwen 模式前，需要安装 64 位 Python 3.11 或 3.12；安装按钮会识别 Python 官方安装及 `uv` 注册的解释器，创建应用专用虚拟环境，不会替换系统 Python。本版本不会误用尚未验证的 Python 3.13/3.14。
- 建议至少 16 GB 内存，并为本地 Qwen 权重、Python 环境和长录屏临时文件预留足够磁盘空间。
- MOSS 推荐使用支持 Vulkan 的 NVIDIA、AMD 或 Intel 显卡及较新驱动；Vulkan 推理失败时会自动用 CPU 重试。
- 对 60 分钟以内的 MOSS 任务，程序会在 Vulkan 尝试中使用 65,536-token 显存保护，以提高 8 GB 级显卡完成长录屏的机会。这只是上下文内存上限，不改变模型权重；如果输入或完整输出无法容纳，会明确失败并自动用 CPU 默认完整上下文重试，不会静默截断。
- 本地 Qwen 会优先使用可用的 CUDA GPU；没有可用 CUDA 时可以使用 CPU，但处理长录屏可能很慢。
- 首次准备本地模型，或每次使用 SDK 模式时，需要相应的网络连接。

## 安装与桌面快捷方式

双击安装包后，“在桌面创建快捷方式（推荐）”默认勾选，也可以在安装向导中取消。v0.4.0 已为 EXE、安装包、开始菜单和桌面快捷方式统一嵌入 InterviewScribe 图标。脚本化安装时可以显式控制：

```powershell
# 静默安装并创建桌面快捷方式
.\InterviewScribe-Setup-x64.exe /VERYSILENT /TASKS=desktopicon

# 静默安装但不创建桌面快捷方式
.\InterviewScribe-Setup-x64.exe /VERYSILENT /MERGETASKS=!desktopicon
```

卸载应用时，安装器创建的桌面和开始菜单快捷方式会一并移除。

## 从源码构建

需要：

- Windows 10/11 x64
- .NET 10 SDK x64
- PowerShell 5.1 或 PowerShell 7
- 如需生成安装包，安装 Inno Setup 6：`winget install --id JRSoftware.InnoSetup -e`

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 -Version 0.4.0
```

脚本会：

1. 按锁文件下载并校验 FFmpeg 和 transcribe.cpp 原生依赖。
2. 检查 Qwen 辅助脚本是否完整，但不会把 Qwen 权重下载进安装包。
3. 还原 .NET 依赖、运行测试，并发布 `win-x64` 自包含的 GUI 与隔离 EngineHost。
4. 打包原生组件、Qwen 安装/推理脚本、依赖锁文件和第三方声明。
5. 用 Inno Setup 生成 `artifacts\release\InterviewScribe-Setup-x64.exe`，并在同一目录写入 `SHA256SUMS.txt`。

只准备原生依赖：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Fetch-NativeDependencies.ps1
```

只生成可便携运行目录，不调用 Inno Setup：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 -SkipInstaller
```

下载归档缓存在 `artifacts\downloads`。发布过程不会把模型、用户视频、临时音频或转写结果打进安装包；这些路径由 `.gitignore` 排除。

## 依赖可复现性

`packaging/dependencies.lock.json`（安装后位于程序目录的 `dependencies.lock.json`）记录原生依赖与模型版本。FFmpeg、transcribe.cpp 归档和 MOSS GGUF 使用固定 URL/commit，并校验字节数和 SHA-256；两套 Qwen 模型使用完整 Hugging Face commit revision，安装后还会校验 InterviewScribe 写入的 revision 标记。升级依赖时必须同时审查许可证、更新锁文件与模型描述，并在干净环境重新验证。

请同时阅读 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 开发注意

transcribe.cpp v0.2.3 官方 Windows CPU/Vulkan 发布包只提供 `transcribe.dll`、`ggml-vulkan.dll`、CPU 后端及其依赖，不包含 `transcribe-cli.exe`。InterviewScribe 自己构建并打包 `InterviewScribe.EngineHost.exe`，由它通过原生 API 调用已锁定的 `transcribe.dll`；独立进程也可在原生库异常时保护 GUI 主进程。

本地 Qwen 由打包的 `qwen_sidecar.py` 在独立 Python 进程中运行。ASR 完成后先释放模型和 CUDA 缓存，再加载强制对齐模型，以降低两套模型同时驻留带来的显存压力；这仍不代表所有显卡都已通过实测。

## 许可与发布

InterviewScribe 自身代码采用 [MIT License](LICENSE)。第三方软件仍分别受各自许可证约束，具体归属见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

开发构建默认没有代码签名。未配置可信 Windows 代码签名证书时，安装包可能显示“未知发布者”；这不影响本地运行，但公开分发前应配置签名。
