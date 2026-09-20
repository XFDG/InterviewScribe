#!/usr/bin/env python3
"""Offline Faster-Whisper sidecar used by MediaScribe's fast mode.

The parent process hands this script an already-normalized 16 kHz mono WAV.
It never resolves a model ID at run time: a fully downloaded CTranslate2 model
directory is required, so after installation this path remains offline.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
import wave
from importlib import metadata
from pathlib import Path
from typing import Any, Iterable, Sequence


SAMPLE_RATE = 16_000
CHANNELS = 1
SAMPLE_WIDTH = 2
_DLL_DIRECTORY_HANDLES: list[Any] = []


class UserFacingError(RuntimeError):
    """An expected failure safe to surface in the desktop UI."""


class JsonArgumentParser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        raise UserFacingError(f"参数错误：{message}")


def _emit(event_type: str, **values: Any) -> None:
    sys.stderr.write(json.dumps({"type": event_type, **values}, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stderr.flush()


def _progress(message: str, fraction: float) -> None:
    _emit("progress", message=message, fraction=max(0.0, min(1.0, fraction)))


def _safe_message(value: str) -> str:
    return re.sub(r"\s+", " ", value.strip() or "未知错误")[:1_500]


def _package_version(name: str) -> str:
    try:
        return metadata.version(name)
    except metadata.PackageNotFoundError:
        return "unknown"


def _configure_windows_cuda_libraries() -> list[str]:
    """Make CUDA 12 + cuDNN 9 wheels visible to CTranslate2 on Windows.

    NVIDIA's pip runtime wheels install DLLs below site-packages/nvidia rather
    than on PATH.  CTranslate2 resolves them when it is imported, so this must
    run before importing faster_whisper/ctranslate2.
    """

    if os.name != "nt" or not hasattr(os, "add_dll_directory"):
        return []

    try:
        import site

        roots = [Path(path) for path in site.getsitepackages()]
    except (ImportError, AttributeError):
        roots = []
    added: list[str] = []
    seen: set[str] = set()
    for root in roots:
        nvidia_root = root / "nvidia"
        if not nvidia_root.is_dir():
            continue
        # CUDA wheels may add supporting DLL families (for example NVRTC) in
        # addition to CUDART/CUBLAS/cuDNN.  Discover every immediate wheel
        # bin directory rather than assuming one particular package layout.
        for package_root in sorted(nvidia_root.iterdir(), key=lambda item: item.name.lower()):
            directory = package_root / "bin"
            if not directory.is_dir():
                continue
            normalized = str(directory.resolve())
            if normalized.casefold() in seen:
                continue
            try:
                _DLL_DIRECTORY_HANDLES.append(os.add_dll_directory(normalized))
                added.append(normalized)
                seen.add(normalized.casefold())
            except OSError:
                # Continue: the actual CTranslate2 exception contains the more
                # actionable missing-DLL detail if this directory cannot load.
                continue
    if added:
        # Some delay-loaded CUDA dependencies are resolved by native code using
        # the legacy PATH search rather than Python's AddDllDirectory search.
        # Keep both mechanisms: this makes the isolated venv work on Windows
        # 10/11 without altering the user's system-wide PATH.
        existing_path = os.environ.get("PATH", "")
        os.environ["PATH"] = os.pathsep.join([*added, existing_path])
    return added


def _validate_wav(path: Path) -> int:
    if not path.is_file():
        raise UserFacingError(f"找不到输入音频：{path}")
    try:
        with wave.open(str(path), "rb") as reader:
            channels = reader.getnchannels()
            width = reader.getsampwidth()
            rate = reader.getframerate()
            compression = reader.getcomptype()
            frames = reader.getnframes()
    except (wave.Error, OSError) as exc:
        raise UserFacingError(f"无法读取 WAV 音频：{exc}") from exc
    if (channels, width, rate, compression) != (CHANNELS, SAMPLE_WIDTH, SAMPLE_RATE, "NONE"):
        raise UserFacingError(
            "快速模式仅接受 16 kHz、单声道、16-bit PCM WAV；"
            f"实际为 {rate} Hz、{channels} 声道、{width * 8}-bit、{compression}。"
        )
    if frames <= 0:
        raise UserFacingError("输入 WAV 不包含音频帧。")
    return round(frames * 1_000 / SAMPLE_RATE)


def _validate_model(model_dir: Path) -> None:
    if not model_dir.is_dir():
        raise UserFacingError(f"Faster-Whisper 模型目录不存在：{model_dir}")
    required = ("model.bin", "config.json", "tokenizer.json")
    missing = [name for name in required if not (model_dir / name).is_file()]
    if missing:
        raise UserFacingError(
            "Faster-Whisper 模型不完整，缺少：" + "、".join(missing) + "。请重新安装快速模式组件。"
        )
    if (model_dir / "model.bin").stat().st_size <= 0:
        raise UserFacingError("Faster-Whisper 模型权重为空。请重新安装快速模式组件。")


def _atomic_write_json(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=str(path.parent))
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False, separators=(",", ":"))
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    except BaseException:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
        raise


def _coalesce_text(values: Iterable[str]) -> str:
    result = ""
    for value in values:
        part = value.strip()
        if not part:
            continue
        if result and result[-1].isascii() and result[-1].isalnum() and part[0].isascii() and part[0].isalnum():
            result += " "
        result += part
    return result


def _run(args: argparse.Namespace, audio: Path, duration_ms: int) -> dict[str, Any]:
    _validate_model(Path(args.model_dir))
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["DO_NOT_TRACK"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"

    dll_paths = _configure_windows_cuda_libraries()
    try:
        from faster_whisper import BatchedInferencePipeline, WhisperModel
    except Exception as exc:
        raise UserFacingError(
            "无法加载 Faster-Whisper 运行组件。请点击“安装快速模式组件”；"
            f"详细信息：{_safe_message(str(exc))}"
        ) from exc

    device = args.device
    compute_type = args.compute_type
    _progress("正在加载 Whisper large-v3-turbo…", 0.04)
    try:
        model = WhisperModel(str(Path(args.model_dir)), device=device, compute_type=compute_type)
    except Exception as exc:
        detail = _safe_message(str(exc))
        if device == "cuda":
            raise UserFacingError(
                "Faster-Whisper 无法启动 Windows CUDA。请确认 NVIDIA 驱动可用，并重新安装快速模式组件。"
                f"详细信息：{detail}"
            ) from exc
        raise UserFacingError(f"无法加载 Faster-Whisper 模型：{detail}") from exc

    backend_label = "Windows CUDA GPU" if device == "cuda" else "CPU"
    _progress(f"已加载 Whisper；正在使用 VAD 和批量 {backend_label} 推理…", 0.10)
    pipeline = BatchedInferencePipeline(model)
    language = None if args.language == "auto" else args.language
    try:
        segments, info = pipeline.transcribe(
            str(audio),
            language=language,
            task="transcribe",
            beam_size=5,
            best_of=5,
            vad_filter=True,
            word_timestamps=True,
            condition_on_previous_text=True,
            batch_size=args.batch_size,
        )
        result_segments: list[dict[str, Any]] = []
        text_parts: list[str] = []
        last_end = 0
        for segment in segments:
            start_ms = max(0, round(float(segment.start) * 1_000))
            end_ms = min(duration_ms, max(start_ms + 1, round(float(segment.end) * 1_000)))
            text = str(segment.text or "").strip()
            if not text:
                continue
            # Faster-Whisper may return overlapping segments around VAD joins.
            # Preserve the original time data but keep progress monotonic.
            last_end = max(last_end, end_ms)
            result_segments.append(
                {"startMs": start_ms, "endMs": end_ms, "speakerId": 0, "text": text}
            )
            text_parts.append(text)
            _progress("Whisper 正在识别语音…", 0.12 + 0.82 * min(1.0, last_end / max(1, duration_ms)))
    except UserFacingError:
        raise
    except Exception as exc:
        raise UserFacingError(f"Whisper 识别失败：{_safe_message(str(exc))}") from exc
    finally:
        # Explicitly release the model before a separately requested MOSS
        # speaker pass starts in the parent pipeline.
        try:
            del pipeline, model
        except UnboundLocalError:
            pass

    if not result_segments:
        raise UserFacingError("Whisper 未识别到可输出的语音文字。")

    detected_language = str(getattr(info, "language", "") or args.language)
    warnings: list[str] = ["Whisper large-v3-turbo 通过 Faster-Whisper/CTranslate2 本地识别。"]
    if args.device == "cuda":
        warnings.append("已请求 Windows 原生 CUDA 推理（int8_float16）。")
    else:
        warnings.append("正在使用 CPU int8 推理。")
    if dll_paths:
        warnings.append("CUDA 运行库从应用隔离的 NVIDIA pip 包加载。")

    text = _coalesce_text(text_parts)
    return {
        "sourceFileName": audio.name,
        "durationMs": duration_ms,
        "model": "Whisper large-v3-turbo (CTranslate2)",
        "engineVersion": f"faster-whisper {_package_version('faster-whisper')}; ctranslate2 {_package_version('ctranslate2')}",
        "backend": f"faster-whisper-{args.device}-{args.compute_type}",
        "language": detected_language,
        "fullText": text,
        "rawText": text,
        "segments": result_segments,
        "warnings": warnings,
        "isPartial": False,
    }


def _build_parser() -> argparse.ArgumentParser:
    parser = JsonArgumentParser(description="MediaScribe Faster-Whisper sidecar", allow_abbrev=False)
    parser.add_argument("--audio", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--model-dir", required=True)
    parser.add_argument("--language", required=True, choices=("auto", "zh", "en", "yue", "ja", "ko", "fr", "de", "es", "pt", "ru", "it"))
    parser.add_argument("--device", required=True, choices=("cuda", "cpu"))
    parser.add_argument("--compute-type", required=True, choices=("int8_float16", "int8"))
    parser.add_argument("--batch-size", required=True, type=int)
    return parser


def _validate_arguments(args: argparse.Namespace) -> None:
    if args.device == "cuda" and args.compute_type != "int8_float16":
        raise UserFacingError("CUDA 快速模式必须使用 int8_float16。")
    if args.device == "cpu" and args.compute_type != "int8":
        raise UserFacingError("CPU 快速模式必须使用 int8。")
    if not 1 <= args.batch_size <= 32:
        raise UserFacingError("--batch-size 必须在 1–32 之间。")


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = _build_parser().parse_args(argv)
        _validate_arguments(args)
        audio = Path(args.audio).expanduser().resolve()
        output = Path(args.output).expanduser().resolve()
        if audio == output:
            raise UserFacingError("--output 不能覆盖输入 WAV。")
        duration_ms = _validate_wav(audio)
        _progress("已验证 16 kHz 单声道 PCM WAV", 0.01)
        result = _run(args, audio, duration_ms)
        _progress("正在原子写入识别结果…", 0.97)
        _atomic_write_json(output, result)
        _progress("识别完成", 1.0)
        return 0
    except KeyboardInterrupt:
        _emit("error", message="操作已取消。")
        return 130
    except UserFacingError as exc:
        _emit("error", message=_safe_message(str(exc)))
        return 2
    except Exception as exc:
        _emit("error", message=_safe_message(str(exc)))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
