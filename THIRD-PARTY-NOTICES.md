# Third-party notices

InterviewScribe 通过独立进程使用 FFmpeg，并随安装包分发 transcribe.cpp 的 Windows 原生运行库。MOSS 模型不在安装包中，由用户首次使用时下载。精确版本、下载地址、文件大小和 SHA-256 见 `packaging/dependencies.lock.json`。

## FFmpeg 9.0.2

- 项目：FFmpeg
- 许可证：LGPL-2.1-or-later（本项目选用 BtbN 的 LGPL 构建）
- 源码：<https://github.com/FFmpeg/FFmpeg/tree/n9.0.2>
- 构建来源：<https://github.com/BtbN/FFmpeg-Builds/tree/autobuild-2026-09-19-13-11>
- 许可说明：<https://ffmpeg.org/legal.html>

FFmpeg 与 InterviewScribe 分开运行，未对 FFmpeg 二进制文件做修改。获取脚本会将上游归档内的 LICENSE/COPYING/README 文件一并放入 `tools/ffmpeg/licenses`。

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

模型权重不随本软件安装包分发。应用下载后必须核对锁定的文件大小与 SHA-256，失败时不得加载。Apache License 2.0 全文：<https://www.apache.org/licenses/LICENSE-2.0>。

## Microsoft .NET

自包含发布会携带 Microsoft .NET 运行时组件。相关许可与声明见：<https://github.com/dotnet/runtime/blob/main/LICENSE.TXT> 和 <https://dotnet.microsoft.com/platform/free>。

---

本文件仅用于保留第三方归属与再分发信息，不改变任何上游项目的许可条款。
