# MediaScribe 媒体转写助手

MediaScribe 是一个面向 Windows 10/11 x64 的本地媒体转写 GUI。把已有录屏、会议视频或音频拖进窗口，选择语言和输出格式，程序会按队列逐个生成可直接交给 GPT、笔记工具或字幕编辑器使用的文字。

- 完全本地识别：模型安装完成后，识别过程不上传音频、不需要联网。
- 三种可选本地引擎：Whisper Turbo、MOSS 兼容模式、Qwen3-ASR + ForcedAligner。
- 11 种可选语言：中文、英语、粤语、日语、韩语、法语、德语、西班牙语、葡萄牙语、俄语、意大利语；可多选并自动识别混说。
- TXT、Markdown、DOCX、PDF、SRT、JSON 可多选导出。
- 可选时间轴和说话人标签；说话人能力由保留的 MOSS 独立后处理提供。
- 可一次选择多个文件，严格顺序处理，不会争抢同一块 GPU。
- 实时显示确定型百分比、已用时间、预计剩余时间和运行诊断。

> 名称从 InterviewScribe 更名为 MediaScribe；当前版本继续复用旧的数据目录 `%LOCALAPPDATA%\InterviewScribe`，避免升级时重复下载已有模型。

## 选择识别方案

| 模式 | 文字与时间轴 | 说话人 | 设备策略 | 适合什么情况 |
| --- | --- | --- | --- | --- |
| **Whisper Turbo 快速模式（默认推荐）** | `faster-whisper-large-v3-turbo` | 可选：再跑一次 MOSS | Windows 原生 CUDA 优先，`int8_float16`；失败时 CPU `int8` | 希望速度接近 Buzz，同时保留本地时间轴 |
| **MOSS 本地兼容模式** | `MOSS-Transcribe-Diarize-Q8` 一次生成 | 内建 | Vulkan GPU 优先；单段失败才回退 CPU | 希望沿用已有 MOSS 工作流或直接得到说话人轨道 |
| **Qwen3-ASR + ForcedAligner 高精度模式** | Qwen3-ASR 生成文字，ForcedAligner 生成逐词/逐 token 时间轴 | 可选：再跑一次 MOSS | 标准 PyTorch，Windows CUDA 优先；ASR 与对齐器顺序加载 | 更看重中英混说、文字质量与细粒度时间轴 |

“快速”和“高精度”是工作流定位，不是所有录音条件下的绝对基准。噪声、口音、多人抢话、术语、驱动和显卡都会影响速度及正确性；重要内容仍建议抽听核对。

## 当前工作流

1. 在 GUI 中添加一个或多个视频/音频文件，选择输出文件夹、语言、模式和格式。
2. 程序用 FFprobe 读取媒体，用 FFmpeg 合并音轨并提取为 16 kHz、单声道 PCM WAV；原文件不会被修改或删除。
3. 队列从上到下逐个处理。Whisper 与 Qwen 默认只做文字和时间轴；勾选“说话人区分”后，才额外运行 MOSS 并按时间重叠合入说话人标签。
4. Qwen 模式会先释放 ASR 与 CUDA 缓存，再加载 ForcedAligner，避免两套大模型同时占用显存。
5. 所有选中的格式先写入临时文件，全部成功后原子提交；取消或失败不会留下半成品。可重建的 WAV、分段音频和中间 JSON 会清理，原始媒体始终保留。

单个媒体文件目前上限为 8 小时。使用 MOSS 的长录音会自动拆成最长 25 分钟、相邻 30 秒重叠的窗口，合并全局时间轴和说话人标签；其他模式也会按显存配置把 Qwen 音频切为保守的小段。

## 显卡自适应，而非只为 RTX 5060 编写

启动识别时，程序尝试用 `nvidia-smi` 读取主 NVIDIA GPU 的显存。它据此保守设置 MOSS 上下文、Qwen 分段和 Whisper 批量大小；无法检测显存时使用更小的安全默认值。不会因为某一个显卡型号写死 token 上限。

| 可用显存（MiB） | MOSS 上下文上限 | Qwen 单段 | Whisper 批量 |
| --- | ---: | ---: | ---: |
| 未检测到 | 12,288 token | 60 秒 | 2 |
| < 4,096 | 8,192 token | 45 秒 | 1 |
| 4,096–6,143 | 12,288 token | 60 秒 | 2 |
| 6,144–8,191 | 16,384 token | 75 秒 | 3 |
| 8,192–16,383 | 20,480–32,768 token | 90–150 秒 | 4–6 |
| ≥ 16,384 | 49,152–65,536 token | 最多 180 秒 | 8–12 |

这是为桌面合成、驱动和模型工作区预留余量的策略，不把理论显存全部压满。可通过 `MEDIASCRIBE_GPU_MEMORY_MIB` 为诊断或特殊硬件临时覆盖检测值。

## 安装包与桌面快捷方式

下载 `MediaScribe-Setup-x64.exe` 后双击安装。向导默认勾选“在桌面创建快捷方式（推荐）”；也可以在静默安装时显式控制：

```powershell
# 安装并创建桌面快捷方式
.\MediaScribe-Setup-x64.exe /VERYSILENT /TASKS=desktopicon

# 安装但不创建桌面快捷方式
.\MediaScribe-Setup-x64.exe /VERYSILENT /MERGETASKS=!desktopicon
```

应用、安装器、开始菜单和桌面快捷方式使用同一枚 MediaScribe 霓虹波形图标。

## 首次安装模型

安装包包含 GUI、FFmpeg/FFprobe、MOSS 原生运行库和模型安装脚本，**不包含模型权重**。首次准备每一种模型时需联网；通过校验后，日常转写可完全离线。

### Whisper Turbo 快速模式

在 GUI 选择“Whisper Turbo 快速模式”，点击“安装快速模式组件…”。安装器会创建应用隔离的 Python 环境，安装 Faster-Whisper/CTranslate2 及其 Windows CUDA 12 依赖，并下载固定 revision 的 CTranslate2 `large-v3-turbo` 模型。

默认位置：

```text
%LOCALAPPDATA%\InterviewScribe\whisper-runtime\.venv
%LOCALAPPDATA%\InterviewScribe\models\Whisper-large-v3-turbo-ct2
```

该模型由 Hugging Face 快照安装器下载、按固定 revision、文件长度和 SHA-256 校验。若你的网络无法访问 Hugging Face，需要先让该下载阶段具备可访问的网络；完成后不再依赖网络或 Hugging Face Token。

也可从安装目录手动运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\whisper\Install-FasterWhisperRuntime.ps1
```

### Qwen3-ASR + ForcedAligner 高精度模式（ModelScope）

在 GUI 选择“Qwen3-ASR + ForcedAligner 高精度”，点击“安装高精度组件…”。需要 64 位 CPython 3.11 或 3.12；安装器建立独立虚拟环境，安装锁定的标准 PyTorch CUDA/CPU 运行时、`qwen-asr` 和 `modelscope`，再从官方 ModelScope 仓库下载并逐文件校验：

- `Qwen/Qwen3-ASR-1.7B`
- `Qwen/Qwen3-ForcedAligner-0.6B`

默认位置：

```text
%LOCALAPPDATA%\InterviewScribe\qwen-runtime\.venv
%LOCALAPPDATA%\InterviewScribe\ModelScope-Qwen3\Qwen3-ASR-1.7B
%LOCALAPPDATA%\InterviewScribe\ModelScope-Qwen3\Qwen3-ForcedAligner-0.6B
```

这条路径不需要 Hugging Face 权限或 Token。ModelScope 公共仓库能否直连取决于你当地网络；若先用官方 `modelscope download` 下载到上述目录，安装器会校验并接管完全匹配的本地快照，而不是重复下载。安装完成后，Qwen sidecar 同时设置离线环境变量和 `local_files_only=True`，权重不完整时会明确报错，绝不静默联网。

```powershell
# 自动优先选择 NVIDIA CUDA；没有 NVIDIA 时安装 CPU 运行时
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\qwen\Install-QwenRuntime.ps1

# 强制安装 CPU 版本
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\qwen\Install-QwenRuntime.ps1 -TorchBackend Cpu
```

高精度安装器不再预下载 MOSS；只有你实际勾选“说话人区分”或选择 MOSS 模式时，MOSS 才会单独准备，避免无故下载额外模型或把 ModelScope 安装绑到 Hugging Face。

### MOSS 本地兼容模式

首次使用 MOSS 时会从其锁定的 Hugging Face revision 下载 `MOSS-Transcribe-Diarize-Q8_0.gguf`（约 987 MB），并验证长度和 SHA-256：

```text
%LOCALAPPDATA%\InterviewScribe\models\MOSS-Transcribe-Diarize-Q8_0.gguf
```

之后 MOSS、Whisper 和 Qwen 都能在没有网络的情况下运行。卸载应用不会自动删除模型、运行环境、失败任务诊断或用户输出，以免误删数 GB 数据。

## 输出与隐私

| 格式 | 用途 |
| --- | --- |
| TXT | 直接交给 GPT、搜索或人工整理 |
| Markdown | 适合笔记和 GitHub 阅读 |
| DOCX | 可编辑 Word 文档 |
| PDF | 便于分享和归档的 A4 文档 |
| SRT | 字幕时间码；可选显示说话人 |
| JSON | 模型、后端、完整分段、时间戳、说话人和警告的结构化结果 |

时间轴与说话人开关会应用于 TXT、Markdown、DOCX 和 PDF；JSON 始终保留完整结构。应用不会自动把 TXT 上传给 GPT：如需 AI 分析，请在转写完成后由你自行选择和粘贴，并先检查个人信息、公司机密等敏感内容。

## 构建源码

要求：Windows 10/11 x64、.NET 10 SDK、PowerShell 5.1 或 7。要生成安装包，还需 Inno Setup 6：

```powershell
winget install --id JRSoftware.InnoSetup -e
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 -Version 0.5.0
```

发布脚本会校验原生依赖与模型描述、运行 C# 和两套 Python sidecar 测试、生成 `win-x64` 自包含 GUI、拷贝 Qwen 与 Whisper 安装脚本，并输出：

```text
artifacts\release\MediaScribe-Setup-x64.exe
artifacts\release\SHA256SUMS.txt
```

只生成可便携目录、跳过 Inno Setup：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Release.ps1 -Version 0.5.0 -SkipInstaller
```

## 开源与许可证

MediaScribe 自身代码采用 [MIT License](LICENSE)。模型、FFmpeg、transcribe.cpp、Faster-Whisper/CTranslate2、PyTorch、Qwen 和 ModelScope 仍各自受上游许可证约束，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与 [依赖锁文件](packaging/dependencies.lock.json)。

关联上游： [Qwen3-ASR](https://github.com/QwenLM/Qwen3-ASR)、[ModelScope](https://www.modelscope.cn/)、[Faster-Whisper](https://github.com/SYSTRAN/faster-whisper)、[CTranslate2](https://opennmt.net/CTranslate2/)。

安装包默认未进行商业代码签名，Windows 可能显示“未知发布者”；公开分发前建议配置可信代码签名证书。
