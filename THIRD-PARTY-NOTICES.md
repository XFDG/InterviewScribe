# Third-party notices

MediaScribe 通过独立进程使用 FFmpeg，并随安装包分发 transcribe.cpp 的 Windows 原生运行库。模型权重不在安装包中；MOSS 仅在实际选择 MOSS 或说话人后处理时准备。精确版本、下载地址、文件大小和 SHA-256 见 `packaging/dependencies.lock.json`。

## FFmpeg 9.0.2

- 项目：FFmpeg
- 许可证：LGPL-2.1-or-later（本项目选用 BtbN 的 LGPL 构建）
- 源码：<https://github.com/FFmpeg/FFmpeg/tree/n9.0.2>
- 构建来源：<https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-09-19-13-11>
- 许可说明：<https://ffmpeg.org/legal.html>

FFmpeg 与 MediaScribe 分开运行，未对 FFmpeg 二进制文件做修改。获取脚本会将上游归档内的 LICENSE/COPYING/README 文件一并放入 `tools/ffmpeg/licenses`。

## transcribe.cpp 0.2.3

- 项目：handy-computer/transcribe.cpp
- 许可证：MIT
- 源码：<https://github.com/handy-computer/transcribe.cpp/tree/v0.2.3>

上游 Windows CPU/Vulkan 归档自带 `licenses` 目录，内含 transcribe.cpp、ggml 和 miniz 的许可文件，打包时原样保留。transcribe.cpp、ggml 和 miniz 使用 MIT 许可证。

## MOSS-Transcribe-Diarize-Q8_0

- 模型：handy-computer/moss-transcribe-diarize-gguf / `MOSS-Transcribe-Diarize-Q8_0.gguf`
- 锁定 revision：`bfa3d24438711391d8713c6ab0efd6264527757c`
- 许可证：Apache-2.0
- 模型卡：<https://huggingface.co/handy-computer/moss-transcribe-diarize-gguf/tree/bfa3d24438711391d8713c6ab0efd6264527757c>
- 上游模型：<https://huggingface.co/OpenMOSS-Team/MOSS-Transcribe-Diarize>

模型权重不随本软件安装包分发。只有选择 MOSS 本地模式或为 Whisper/Qwen 勾选说话人区分时，应用才会在首次使用时下载。下载后必须核对锁定的文件大小与 SHA-256，失败时不得加载。Apache License 2.0 全文：<https://www.apache.org/licenses/LICENSE-2.0>。

## Faster-Whisper / CTranslate2（可选快速模式组件）

- Faster-Whisper：<https://github.com/SYSTRAN/faster-whisper>，MIT License
- CTranslate2：<https://github.com/OpenNMT/CTranslate2>，MIT License
- 快速模式模型：`dropbox-dash/faster-whisper-large-v3-turbo`，模型文件与固定 revision、SHA-256 由依赖锁文件记录

Faster-Whisper、CTranslate2、NVIDIA CUDA 运行时 wheel 和快速模式模型均由用户主动运行组件安装流程后下载到本地，不随 MediaScribe 安装包再分发。相应 Python 包和 NVIDIA 组件继续适用其各自的上游许可条款。

## Qwen3-ASR 高精度组件（可选）

- 项目：QwenLM/Qwen3-ASR
- 代码许可证：Apache-2.0
- 源码：<https://github.com/QwenLM/Qwen3-ASR>
- ASR 模型：`Qwen/Qwen3-ASR-1.7B`
- ASR 锁定 revision：`d69410f1c275f2b0fa60cbb9960edfcdb0ae0aec`
- 对齐模型：`Qwen/Qwen3-ForcedAligner-0.6B`
- 对齐锁定 revision：`6f4d7c9606feb7adf282c9e4b139f28e8695d867`
- 模型许可证：Apache-2.0
- 模型卡：<https://www.modelscope.cn/models/Qwen/Qwen3-ASR-1.7B> 和 <https://www.modelscope.cn/models/Qwen/Qwen3-ForcedAligner-0.6B>

Qwen 模型权重、Python 运行环境及其包不随 MediaScribe 安装包分发。只有用户主动运行本地高精度组件安装流程时，才会通过官方 ModelScope 快照下载到用户的本地数据目录。安装的 Python 包适用其各自随包发布的许可条款。

## Microsoft .NET

自包含发布会携带 Microsoft .NET 运行时组件。相关许可与声明见：<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> 和 <https://dotnet.microsoft.com/platform/free>。

## PDFsharp-GDI 6.2.4

- 项目：PDFsharp
- 许可证：MIT
- 源码：<https://github.com/empira/PDFsharp>
- 文档：<https://docs.pdfsharp.net/>

PDF 导出使用 PDFsharp 的 Windows GDI 构建，并将转写中用到的 Unicode 字形子集嵌入生成的 PDF，以避免换机后出现乱码。

Copyright (c) 2001-2026 empira Software GmbH, Troisdorf (Cologne Area), Germany

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

---

本文件仅用于保留第三方归属与再分发信息，不改变任何上游项目的许可条款。
