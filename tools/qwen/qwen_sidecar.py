#!/usr/bin/env python3
"""InterviewScribe Qwen transcription sidecar.

The local backend is deliberately offline-only.  The SDK backend is deliberately
online-only and uploads short, VAD-aligned audio chunks to DashScope.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import gc
import json
import math
import os
import random
import re
import sys
import tempfile
import threading
import time
import wave
from array import array
from collections import Counter
from dataclasses import dataclass
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from importlib import metadata
from pathlib import Path
from typing import Any, Iterable, Sequence


SAMPLE_RATE = 16_000
SAMPLE_WIDTH = 2
CHANNELS = 1
ASR_REPOSITORY = "Qwen/Qwen3-ASR-1.7B-hf"
ASR_REVISION = "bcd2b5b7f32b480ab5790554cfa8347f246a14f3"
ALIGNER_REPOSITORY = "Qwen/Qwen3-ForcedAligner-0.6B-hf"
ALIGNER_REVISION = "c07281df297b9905d24a508279258cccf987a064"
REVISION_MARKER = ".interviewscribe-revision"
_DASHSCOPE_REQUEST_TIMEOUT_SECONDS = 120
_MAX_DASHSCOPE_RETRY_DELAY_SECONDS = 60.0

_LANGUAGE_NAMES = {
    "ar": "Arabic",
    "zh": "Chinese",
    "en": "English",
    "fr": "French",
    "de": "German",
    "it": "Italian",
    "ja": "Japanese",
    "ko": "Korean",
    "pt": "Portuguese",
    "ru": "Russian",
    "es": "Spanish",
    "yue": "Cantonese",
}


class UserFacingError(RuntimeError):
    """An expected failure whose message is safe to show in the GUI."""


class DashScopeResponseError(UserFacingError):
    """A non-success DashScope response with machine-readable retry metadata."""

    def __init__(
        self,
        status: int | None,
        code: str,
        message: str,
        retry_after_seconds: float | None,
    ) -> None:
        self.status = status
        self.code = code
        self.retry_after_seconds = retry_after_seconds
        status_label = str(status) if status is not None else "unknown"
        detail = " ".join(part for part in (code, message) if part).strip()
        safe_detail = _sanitize_error(detail) if detail else ""
        super().__init__(
            f"DashScope 返回 HTTP {status_label}"
            f"{': ' + safe_detail if safe_detail else ''}"
        )


class DashScopePeerStoppedError(UserFacingError):
    """A cooperative cancellation caused by another worker's real failure."""


class JsonArgumentParser(argparse.ArgumentParser):
    def error(self, message: str) -> None:
        raise UserFacingError(f"参数错误：{message}")


@dataclass(frozen=True)
class WavInfo:
    frames: int
    duration_ms: int


@dataclass(frozen=True)
class AudioChunk:
    path: Path
    start_frame: int
    end_frame: int

    @property
    def start_ms(self) -> int:
        return round(self.start_frame * 1000 / SAMPLE_RATE)

    @property
    def end_ms(self) -> int:
        return round(self.end_frame * 1000 / SAMPLE_RATE)


@dataclass(frozen=True)
class TranscriptChunk:
    audio: AudioChunk
    text: str
    raw_text: str
    language: str


@dataclass(frozen=True)
class SpeakerTurn:
    start_frame: int
    end_frame: int
    speaker_id: int


@dataclass(frozen=True)
class SdkAudioChunk:
    start_frame: int
    end_frame: int
    samples: Any
    speaker_id: int = 0


_MAXIMUM_TOLERATED_SPEAKER_OVERLAP_FRAMES = round(0.40 * SAMPLE_RATE)


def _emit(event_type: str, **values: Any) -> None:
    event = {"type": event_type, **values}
    sys.stderr.write(json.dumps(event, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stderr.flush()


def _progress(message: str, fraction: float) -> None:
    _emit("progress", message=message, fraction=max(0.0, min(1.0, fraction)))


def _package_version(name: str) -> str:
    try:
        return metadata.version(name)
    except metadata.PackageNotFoundError:
        return "unknown"


def _sanitize_error(message: str) -> str:
    result = message.strip() or "未知错误"
    api_key = os.environ.get("DASHSCOPE_API_KEY", "")
    hf_token = os.environ.get("HF_TOKEN", "")
    for secret in (api_key, hf_token):
        if secret:
            result = result.replace(secret, "***")
    # Avoid dumping an entire remote response or an enormous native exception.
    result = re.sub(r"\s+", " ", result)
    return result[:1500]


def _validate_wav(path: Path) -> WavInfo:
    if not path.is_file():
        raise UserFacingError(f"找不到输入音频：{path}")
    try:
        with wave.open(str(path), "rb") as reader:
            channels = reader.getnchannels()
            sample_width = reader.getsampwidth()
            sample_rate = reader.getframerate()
            compression = reader.getcomptype()
            frames = reader.getnframes()
    except (wave.Error, OSError) as exc:
        raise UserFacingError(f"无法读取 WAV 音频：{exc}") from exc

    if channels != CHANNELS or sample_width != SAMPLE_WIDTH or sample_rate != SAMPLE_RATE or compression != "NONE":
        raise UserFacingError(
            "Qwen sidecar 仅接受 16 kHz、单声道、16-bit PCM WAV；"
            f"实际为 {sample_rate} Hz、{channels} 声道、{sample_width * 8}-bit、{compression}。"
        )
    if frames <= 0:
        raise UserFacingError("输入 WAV 不包含音频帧。")
    return WavInfo(frames=frames, duration_ms=round(frames * 1000 / sample_rate))


def _read_revision_marker(model_dir: Path) -> str:
    marker = model_dir / REVISION_MARKER
    try:
        return marker.read_text(encoding="utf-8-sig").strip()
    except OSError as exc:
        raise UserFacingError(f"模型版本标记不可读：{marker}（{exc}）") from exc


def _validate_model_directory(model_dir: Path, expected_revision: str, display_name: str) -> None:
    if not model_dir.is_dir():
        raise UserFacingError(f"{display_name} 目录不存在：{model_dir}")
    actual_revision = _read_revision_marker(model_dir)
    if actual_revision != expected_revision:
        raise UserFacingError(
            f"{display_name} 版本不匹配：期望 {expected_revision}，实际 "
            f"{actual_revision or '<empty>'}。请重新运行 Qwen 运行时安装脚本。"
        )
    required = model_dir / "config.json"
    if not required.is_file() or not any(model_dir.glob("*.safetensors")):
        raise UserFacingError(f"{display_name} 快照不完整：{model_dir}")


def _set_strict_offline_environment() -> None:
    # Set these before importing transformers/huggingface_hub.  local_files_only is
    # also passed to every from_pretrained call as a second line of defence.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_DATASETS_OFFLINE"] = "1"
    os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"
    os.environ["DO_NOT_TRACK"] = "1"
    os.environ["TOKENIZERS_PARALLELISM"] = "false"


def _samples_from_bytes(data: bytes) -> array:
    values = array("h")
    values.frombytes(data)
    if sys.byteorder != "little":
        values.byteswap()
    return values


def _quiet_boundary(reader: wave.Wave_read, target: int, radius_frames: int, total_frames: int) -> int:
    window_frames = max(1, SAMPLE_RATE // 10)
    search_start = max(0, target - radius_frames)
    search_end = min(total_frames, target + radius_frames)
    reader.setpos(search_start)
    samples = _samples_from_bytes(reader.readframes(search_end - search_start))
    if not samples:
        return target

    best_frame = target
    best_energy: float | None = None
    for offset in range(0, len(samples), window_frames):
        block = samples[offset : offset + window_frames]
        if not block:
            continue
        energy = sum(abs(value) for value in block) / len(block)
        candidate = search_start + offset + len(block) // 2
        # Prefer silence; for equal energy prefer the candidate closest to target.
        score = (energy, abs(candidate - target))
        best_score = (best_energy, abs(best_frame - target)) if best_energy is not None else None
        if best_score is None or score < best_score:
            best_energy = energy
            best_frame = candidate
    return min(total_frames, max(0, best_frame))


def _chunk_boundaries(path: Path, total_frames: int, chunk_seconds: int) -> list[tuple[int, int]]:
    maximum = chunk_seconds * SAMPLE_RATE
    if total_frames <= maximum:
        return [(0, total_frames)]

    boundaries = [0]
    with wave.open(str(path), "rb") as reader:
        while total_frames - boundaries[-1] > maximum:
            target = boundaries[-1] + maximum
            boundary = _quiet_boundary(reader, target, 5 * SAMPLE_RATE, total_frames)
            minimum = boundaries[-1] + 30 * SAMPLE_RATE
            maximum_boundary = min(total_frames, boundaries[-1] + maximum)
            boundary = min(maximum_boundary, max(minimum, boundary))
            if boundary <= boundaries[-1]:
                boundary = maximum_boundary
            boundaries.append(boundary)
    boundaries.append(total_frames)
    return list(zip(boundaries, boundaries[1:]))


def _write_wav_chunk(source: Path, destination: Path, start_frame: int, end_frame: int) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(source), "rb") as reader, wave.open(str(destination), "wb") as writer:
        writer.setnchannels(CHANNELS)
        writer.setsampwidth(SAMPLE_WIDTH)
        writer.setframerate(SAMPLE_RATE)
        writer.setcomptype("NONE", "not compressed")
        reader.setpos(start_frame)
        remaining = end_frame - start_frame
        while remaining > 0:
            count = min(remaining, SAMPLE_RATE * 30)
            data = reader.readframes(count)
            if not data:
                raise UserFacingError("拆分 WAV 时提前读到文件结尾。")
            frames_read = len(data) // SAMPLE_WIDTH
            if frames_read <= 0 or frames_read > remaining:
                raise UserFacingError("拆分 WAV 时读取到无效的 PCM 帧数。")
            writer.writeframesraw(data)
            remaining -= frames_read
        writer.writeframes(b"")


def _prepare_local_chunks(audio: Path, info: WavInfo, directory: Path, chunk_seconds: int) -> list[AudioChunk]:
    boundaries = _chunk_boundaries(audio, info.frames, chunk_seconds)
    if len(boundaries) == 1:
        return [AudioChunk(audio, 0, info.frames)]
    chunks: list[AudioChunk] = []
    for index, (start, end) in enumerate(boundaries):
        chunk_path = directory / f"chunk-{index:05d}.wav"
        _write_wav_chunk(audio, chunk_path, start, end)
        chunks.append(AudioChunk(chunk_path, start, end))
    return chunks


def _select_device(torch_module: Any, requested: str) -> tuple[str, Any]:
    if requested == "cuda" and not torch_module.cuda.is_available():
        raise UserFacingError("已要求使用 CUDA，但 PyTorch 未检测到可用的 NVIDIA GPU。")
    device = "cuda" if requested == "cuda" or (requested == "auto" and torch_module.cuda.is_available()) else "cpu"
    if device == "cpu":
        return device, torch_module.float32
    try:
        supports_bfloat16 = torch_module.cuda.is_bf16_supported()
    except Exception:
        supports_bfloat16 = False
    return device, torch_module.bfloat16 if supports_bfloat16 else torch_module.float16


def _release_cuda(torch_module: Any) -> None:
    gc.collect()
    if torch_module.cuda.is_available():
        torch_module.cuda.empty_cache()
        try:
            torch_module.cuda.ipc_collect()
        except (RuntimeError, AttributeError):
            pass


def _transcribe_local_chunks(
    chunks: Sequence[AudioChunk],
    model_dir: Path,
    device: str,
    dtype: Any,
    language: str,
    context: str,
    max_new_tokens: int,
    torch_module: Any,
) -> list[TranscriptChunk]:
    from transformers import AutoModelForMultimodalLM, AutoProcessor

    processor: Any = None
    model: Any = None
    results: list[TranscriptChunk] = []
    try:
        _progress("正在加载 Qwen3-ASR-1.7B（严格离线）…", 0.08)
        processor = AutoProcessor.from_pretrained(
            str(model_dir), local_files_only=True, trust_remote_code=False
        )
        model = AutoModelForMultimodalLM.from_pretrained(
            str(model_dir), local_files_only=True, trust_remote_code=False, dtype=dtype
        )
        model.to(device)
        model.eval()

        for index, chunk in enumerate(chunks):
            request: dict[str, Any] = {"audio": str(chunk.path)}
            if language != "auto":
                request["language"] = language
            if context:
                request["prompt"] = context
            inputs = processor.apply_transcription_request(**request).to(device, dtype)
            with torch_module.inference_mode():
                output_ids = model.generate(**inputs, max_new_tokens=max_new_tokens)
            prompt_length = inputs["input_ids"].shape[1]
            generated_ids = output_ids[:, prompt_length:]
            generated_count = int(generated_ids.shape[1])
            parsed = processor.decode(generated_ids, return_format="parsed")[0]
            raw = str(processor.decode(generated_ids)[0])
            text = str(parsed.get("transcription") or "").strip()
            detected_language = str(parsed.get("language") or _LANGUAGE_NAMES.get(language, language)).strip()
            if generated_count >= max_new_tokens:
                raise UserFacingError(
                    f"第 {index + 1} 段转录达到 max_new_tokens={max_new_tokens}，"
                    "为避免生成不完整 TXT，已停止。请减小 --chunk-seconds 或增大 --max-new-tokens。"
                )
            results.append(TranscriptChunk(chunk, text, raw, detected_language))
            del inputs, output_ids, generated_ids
            fraction = 0.13 + 0.42 * ((index + 1) / len(chunks))
            _progress(f"Qwen 转录 {index + 1}/{len(chunks)}", fraction)
        return results
    finally:
        # This function must return only plain Python data.  Dropping every model
        # reference before loading the aligner is essential on an 8 GB GPU.
        del model, processor
        _release_cuda(torch_module)


def _alignment_language(detected: str, requested: str) -> str:
    normalized = detected.strip().lower()
    for code, name in _LANGUAGE_NAMES.items():
        if normalized in {code, name.lower()}:
            return name
    if "chinese" in normalized or "mandarin" in normalized:
        return "Chinese"
    if "english" in normalized:
        return "English"
    return _LANGUAGE_NAMES.get(requested, detected or "Chinese")


def _aligned_word_segments(
    words: Sequence[Any], chunk_start_ms: int, chunk_end_ms: int
) -> list[dict[str, Any]]:
    """Keep the forced aligner's native word/token granularity.

    Speaker turns can be much shorter than a sentence.  Combining timestamps
    here would force the C# merger to guess how a sentence's characters should
    be divided at a speaker boundary.  Returning each aligned unit lets it use
    real overlap and coalesce only after speaker assignment.
    """

    segments: list[dict[str, Any]] = []
    if chunk_end_ms <= chunk_start_ms:
        raise UserFacingError("强制对齐的音频分段时间范围无效。")
    chunk_duration_ms = chunk_end_ms - chunk_start_ms

    for index, word in enumerate(words):
        if isinstance(word, dict):
            token_value = word.get("text")
            start_value = word.get("start_time")
            end_value = word.get("end_time")
        else:
            token_value = getattr(word, "text", None)
            start_value = getattr(word, "start_time", None)
            end_value = getattr(word, "end_time", None)

        token = str(token_value or "").strip()
        if not token:
            raise UserFacingError(f"对齐器第 {index + 1} 个词/token 缺少文本。")
        if isinstance(start_value, bool) or isinstance(end_value, bool):
            raise UserFacingError(f"对齐器第 {index + 1} 个词/token 的时间戳类型无效。")
        try:
            word_start = float(start_value)
            word_end = float(end_value)
        except (TypeError, ValueError) as exc:
            raise UserFacingError(
                f"对齐器第 {index + 1} 个词/token 缺少或包含非数字时间戳。"
            ) from exc
        if not math.isfinite(word_start) or not math.isfinite(word_end):
            raise UserFacingError(f"对齐器第 {index + 1} 个词/token 的时间戳不是有限数。")
        if word_start < 0 or word_end <= word_start:
            raise UserFacingError(f"对齐器第 {index + 1} 个词/token 的时间范围无效。")

        start_offset_ms = round(word_start * 1000)
        end_offset_ms = round(word_end * 1000)
        if (
            start_offset_ms < 0
            or start_offset_ms >= chunk_duration_ms
            or end_offset_ms <= start_offset_ms
            or end_offset_ms > chunk_duration_ms + 2
        ):
            raise UserFacingError(
                f"对齐器第 {index + 1} 个词/token 的时间戳 "
                f"{start_offset_ms}–{end_offset_ms} ms 超出当前音频分段 "
                f"0–{chunk_duration_ms} ms。"
            )
        start_ms = chunk_start_ms + start_offset_ms
        end_ms = chunk_start_ms + min(chunk_duration_ms, end_offset_ms)
        segments.append(
            {
                "startMs": start_ms,
                "endMs": end_ms,
                "speakerId": 0,
                "text": token,
            }
        )
    return segments


def _align_local_chunks(
    transcripts: Sequence[TranscriptChunk],
    aligner_dir: Path,
    device: str,
    dtype: Any,
    requested_language: str,
    torch_module: Any,
) -> tuple[list[dict[str, Any]], list[str]]:
    from transformers import AutoModelForTokenClassification, AutoProcessor

    processor: Any = None
    model: Any = None
    segments: list[dict[str, Any]] = []
    warnings: list[str] = []
    try:
        _progress("已释放 ASR；正在加载 Qwen3 Forced Aligner…", 0.61)
        processor = AutoProcessor.from_pretrained(
            str(aligner_dir), local_files_only=True, trust_remote_code=False
        )
        model = AutoModelForTokenClassification.from_pretrained(
            str(aligner_dir), local_files_only=True, trust_remote_code=False, dtype=dtype
        )
        model.to(device)
        model.eval()

        for index, item in enumerate(transcripts):
            if not item.text:
                continue
            try:
                language = _alignment_language(item.language, requested_language)
                inputs, word_lists = processor.prepare_forced_aligner_inputs(
                    audio=str(item.audio.path), transcript=item.text, language=language
                )
                inputs = inputs.to(device, dtype)
                with torch_module.inference_mode():
                    outputs = model(**inputs)
                timestamps = processor.decode_forced_alignment(
                    logits=outputs.logits,
                    input_ids=inputs["input_ids"],
                    word_lists=word_lists,
                    timestamp_token_id=model.config.timestamp_token_id,
                )[0]
                aligned_words = _aligned_word_segments(
                    timestamps, item.audio.start_ms, item.audio.end_ms
                )
                if not aligned_words:
                    raise ValueError("对齐器未返回有效时间戳")
                segments.extend(aligned_words)
                del inputs, outputs
            except (KeyboardInterrupt, SystemExit):
                raise
            except Exception as exc:
                # A chunk-wide fallback (often several minutes) cannot be assigned
                # to speakers honestly. High-accuracy mode fails the job instead
                # of emitting a plausible-looking but false timeline.
                detail = _sanitize_error(str(exc))
                raise UserFacingError(
                    f"第 {index + 1}/{len(transcripts)} 段强制对齐失败"
                    f"（{item.audio.start_ms}–{item.audio.end_ms} ms）：{detail}。"
                    "未生成粗粒度回退时间轴，请检查显存/内存或重新安装对齐模型。"
                ) from exc
            fraction = 0.66 + 0.29 * ((index + 1) / len(transcripts))
            _progress(f"时间轴对齐 {index + 1}/{len(transcripts)}", fraction)
        return segments, warnings
    finally:
        del model, processor
        _release_cuda(torch_module)


def _dominant_language(values: Iterable[str], fallback: str) -> str:
    cleaned = [value.strip() for value in values if value and value.strip()]
    if not cleaned:
        return fallback
    return Counter(cleaned).most_common(1)[0][0]


def _temporary_audio_directory(output_path: str, mode: str) -> tempfile.TemporaryDirectory[str]:
    """Keep derived audio inside the parent application's managed job directory."""

    output_parent = Path(output_path).expanduser().resolve().parent
    output_parent.mkdir(parents=True, exist_ok=True)
    return tempfile.TemporaryDirectory(
        prefix=f".qwen-audio-{mode}-",
        dir=str(output_parent),
    )


def _run_local(args: argparse.Namespace, audio: Path, info: WavInfo) -> dict[str, Any]:
    model_dir = Path(args.model_dir).expanduser().resolve()
    aligner_dir = Path(args.aligner_dir).expanduser().resolve()
    _validate_model_directory(model_dir, ASR_REVISION, "Qwen3-ASR-1.7B-hf")
    _validate_model_directory(aligner_dir, ALIGNER_REVISION, "Qwen3-ForcedAligner-0.6B-hf")
    _set_strict_offline_environment()

    try:
        import torch
        import transformers
    except ImportError as exc:
        raise UserFacingError(f"Qwen 本地运行时不完整：{exc}") from exc

    device, dtype = _select_device(torch, args.device)
    device_label = device.upper()
    if device == "cuda":
        try:
            device_label = f"CUDA · {torch.cuda.get_device_name(0)}"
        except (RuntimeError, AssertionError, AttributeError):
            device_label = "CUDA GPU"
    _progress(f"本地运行设备：{device_label}（{str(dtype).replace('torch.', '')}）", 0.03)
    with _temporary_audio_directory(args.output, "local") as temporary:
        chunks = _prepare_local_chunks(audio, info, Path(temporary), args.chunk_seconds)
        _progress(f"音频已分为 {len(chunks)} 段", 0.06)
        transcripts = _transcribe_local_chunks(
            chunks,
            model_dir,
            device,
            dtype,
            args.language,
            args.context,
            args.max_new_tokens,
            torch,
        )
        if not any(item.text for item in transcripts):
            raise UserFacingError("Qwen 未识别到可输出的语音文字。")
        # _transcribe_local_chunks has dropped the ASR model and cleared CUDA
        # before this call.  Never keep both models resident on an 8 GB GPU.
        segments, warnings = _align_local_chunks(
            transcripts, aligner_dir, device, dtype, args.language, torch
        )

    full_text = "\n".join(item.text for item in transcripts if item.text).strip()
    raw_text = "\n".join(item.raw_text for item in transcripts if item.raw_text).strip()
    language = _dominant_language((item.language for item in transcripts), args.language)
    return {
        "sourceFileName": audio.name,
        "durationMs": info.duration_ms,
        "model": ASR_REPOSITORY,
        "engineVersion": f"transformers {transformers.__version__}",
        "backend": f"local-{device}",
        "language": language,
        "fullText": full_text,
        "rawText": raw_text,
        "segments": segments,
        "warnings": list(dict.fromkeys(warnings)),
        "isPartial": False,
    }


def _mapping_get(value: Any, key: str, default: Any = None) -> Any:
    if isinstance(value, dict):
        return value.get(key, default)
    try:
        return value[key]
    except (KeyError, TypeError, IndexError):
        return getattr(value, key, default)


def _http_status(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    if isinstance(value, str) and re.fullmatch(r"[1-5][0-9]{2}", value.strip()):
        return int(value.strip())
    return None


def _retry_after_seconds(headers: Any, now: datetime | None = None) -> float | None:
    """Parse the two RFC 9110 Retry-After forms; reject ambiguous values."""

    if headers is None:
        return None
    try:
        items = headers.items()
    except AttributeError:
        return None
    raw: Any = None
    for key, value in items:
        if str(key).lower() == "retry-after":
            raw = value
            break
    if raw is None or isinstance(raw, bool):
        return None
    value = str(raw).strip()
    if re.fullmatch(r"[0-9]+", value):
        return float(int(value))
    try:
        retry_at = parsedate_to_datetime(value)
    except (TypeError, ValueError, OverflowError):
        return None
    if retry_at.tzinfo is None:
        return None
    reference = now or datetime.now(timezone.utc)
    if reference.tzinfo is None:
        reference = reference.replace(tzinfo=timezone.utc)
    return max(0.0, (retry_at - reference).total_seconds())


def _sdk_response(response: Any) -> tuple[str, str]:
    status = _http_status(_mapping_get(response, "status_code"))
    if status != 200:
        raw_code = _mapping_get(response, "code", "")
        raw_message = _mapping_get(response, "message", "")
        code = "" if raw_code is None else str(raw_code)
        message = "" if raw_message is None else str(raw_message)
        retry_after = _retry_after_seconds(_mapping_get(response, "headers"))
        raise DashScopeResponseError(status, code, message, retry_after)

    try:
        output = _mapping_get(response, "output")
        choice = _mapping_get(output, "choices")[0]
        message = _mapping_get(choice, "message")
        content = _mapping_get(message, "content", [])
        text = str(_mapping_get(content[0], "text", "")) if content else ""
        annotations = _mapping_get(message, "annotations", []) or []
        language_code = str(_mapping_get(annotations[0], "language", "")) if annotations else ""
    except (KeyError, TypeError, IndexError) as exc:
        raise UserFacingError("DashScope 返回了无法解析的 Qwen ASR 结果。") from exc
    return _LANGUAGE_NAMES.get(language_code.lower(), language_code or "auto"), text.strip()


_RETRYABLE_DASHSCOPE_STATUSES = frozenset({408, 429, 500, 502, 503, 504})


def _is_retryable_dashscope_response(error: DashScopeResponseError) -> bool:
    return error.code != "DataInspectionFailed" and error.status in _RETRYABLE_DASHSCOPE_STATUSES


def _is_retryable_transport_exception(error: BaseException) -> bool:
    request_types: tuple[type[BaseException], ...] = ()
    try:
        from requests import exceptions as requests_exceptions

        request_types = (
            requests_exceptions.Timeout,
            requests_exceptions.ConnectionError,
            requests_exceptions.ChunkedEncodingError,
        )
    except ImportError:
        # SDK mode normally has requests through dashscope. Built-in network
        # failures can still be classified safely if that dependency is broken.
        pass
    return isinstance(error, (TimeoutError, ConnectionError, *request_types))


def _dashscope_retry_delay(attempt: int, error: BaseException) -> float:
    if isinstance(error, DashScopeResponseError) and error.retry_after_seconds is not None:
        return min(_MAX_DASHSCOPE_RETRY_DELAY_SECONDS, error.retry_after_seconds)
    return min(8.0, 0.75 * (2 ** (attempt - 1))) + random.uniform(0.0, 0.25)


def _dashscope_failure(error: BaseException) -> UserFacingError:
    if isinstance(error, DashScopeResponseError):
        detail = _sanitize_error(str(error))
    else:
        detail = _sanitize_error(f"{type(error).__name__}: {error}")
    return UserFacingError(f"DashScope Qwen ASR 请求失败：{detail}")


def _call_dashscope_chunk(
    path: Path,
    model_name: str,
    context: str,
    attempts: int,
    post_process: Any,
    stop_event: threading.Event | None = None,
) -> tuple[str, str]:
    import dashscope

    messages = [
        {"role": "system", "content": [{"text": context}]},
        {"role": "user", "content": [{"audio": path.resolve().as_uri()}]},
    ]
    for attempt in range(1, attempts + 1):
        if stop_event is not None and stop_event.is_set():
            raise DashScopePeerStoppedError(
                "另一个 DashScope 分段失败，已停止后续请求。"
            )
        error: BaseException
        try:
            response = dashscope.MultiModalConversation.call(
                model=model_name,
                messages=messages,
                result_format="message",
                asr_options={"enable_lid": True, "enable_itn": False},
                request_timeout=_DASHSCOPE_REQUEST_TIMEOUT_SECONDS,
            )
            language, text = _sdk_response(response)
            return language, post_process(text)
        except DashScopeResponseError as exc:
            error = exc
            retryable = _is_retryable_dashscope_response(exc)
        except UserFacingError:
            # A successful HTTP response with an invalid result is a protocol
            # or parsing failure. Repeating it hides the real defect.
            if stop_event is not None:
                stop_event.set()
            raise
        except Exception as exc:
            error = exc
            retryable = _is_retryable_transport_exception(exc)

        if not retryable or attempt == attempts:
            # Signal directly in the worker before publishing the exception to
            # the scheduler.  A retrying peer therefore cannot start another
            # billable upload while the main thread is still observing this
            # future's failure.
            if stop_event is not None:
                stop_event.set()
            raise _dashscope_failure(error) from error
        delay = _dashscope_retry_delay(attempt, error)
        if stop_event is None:
            time.sleep(delay)
        elif stop_event.wait(delay):
            raise DashScopePeerStoppedError(
                "另一个 DashScope 分段失败，已停止后续请求。"
            )

    raise AssertionError("DashScope retry loop ended unexpectedly")


def _transcribe_sdk_paths(
    paths: Sequence[Path],
    model_name: str,
    context: str,
    attempts: int,
    post_process: Any,
    worker_count: int,
) -> list[tuple[str, str] | None]:
    """Transcribe with at most one in-flight request per worker.

    Keeping only a small sliding window prevents a failure in one segment from
    leaving the rest of a long recording queued for upload (and billing).
    Synchronous SDK calls cannot be interrupted mid-request, so every request
    also has a fixed timeout and workers observe ``stop_event`` before retries.
    """

    ordered: list[tuple[str, str] | None] = [None] * len(paths)
    if not paths:
        return ordered

    stop_event = threading.Event()
    maximum_workers = min(worker_count, len(paths))
    executor = concurrent.futures.ThreadPoolExecutor(max_workers=maximum_workers)
    pending: dict[concurrent.futures.Future[tuple[str, str]], int] = {}
    next_index = 0
    completed = 0
    succeeded = False

    def submit(index: int) -> None:
        pending[
            executor.submit(
                _call_dashscope_chunk,
                paths[index],
                model_name,
                context,
                attempts,
                post_process,
                stop_event,
            )
        ] = index

    try:
        while next_index < len(paths) and len(pending) < maximum_workers:
            submit(next_index)
            next_index += 1

        while pending:
            done, _ = concurrent.futures.wait(
                tuple(pending), return_when=concurrent.futures.FIRST_COMPLETED
            )

            # Resolve the whole completed batch before submitting replacements.
            # If any item failed, no additional audio is uploaded.
            batch: list[tuple[int, tuple[str, str]]] = []
            terminal_errors: list[tuple[int, BaseException]] = []
            for future in sorted(done, key=lambda item: pending[item]):
                index = pending.pop(future)
                try:
                    batch.append((index, future.result()))
                except DashScopePeerStoppedError:
                    # The worker that set stop_event owns the useful root cause.
                    # Keep waiting for it instead of replacing (for example) an
                    # InvalidApiKey response with a generic peer-stop message.
                    continue
                except BaseException as exc:
                    terminal_errors.append((index, exc))

            if terminal_errors:
                raise min(terminal_errors, key=lambda item: item[0])[1]

            for index, result in batch:
                ordered[index] = result
            completed += len(batch)
            if batch:
                _progress(
                    f"DashScope 转录 {completed}/{len(paths)}",
                    0.12 + 0.80 * completed / len(paths),
                )

            # A terminal worker sets this before its Future becomes observable.
            # Do not refill the sliding window in that interval; wait for the
            # originating Future so its precise error reaches the user.
            if stop_event.is_set():
                if not pending:
                    raise UserFacingError(
                        "DashScope 分段失败，已停止后续请求。"
                    )
                continue

            while next_index < len(paths) and len(pending) < maximum_workers:
                submit(next_index)
                next_index += 1

        succeeded = True
        return ordered
    finally:
        if not succeeded:
            stop_event.set()
            for future in pending:
                future.cancel()
        # At most ``maximum_workers`` calls can be running here. Their bounded
        # request timeout plus cooperative retry cancellation makes cleanup
        # finite and keeps the temporary WAV files alive until workers finish.
        executor.shutdown(wait=True, cancel_futures=True)


def _case_insensitive_field(value: dict[str, Any], field_name: str, default: Any = None) -> Any:
    """Read System.Text.Json camelCase/PascalCase fields without guessing schemas."""

    expected = re.sub(r"[^a-z0-9]", "", field_name.lower())
    for key, item in value.items():
        normalized = re.sub(r"[^a-z0-9]", "", str(key).lower())
        if normalized == expected:
            return item
    return default


def _timeline_integer(value: dict[str, Any], field_name: str, index: int) -> int:
    raw = _case_insensitive_field(value, field_name)
    if isinstance(raw, bool) or not isinstance(raw, int):
        raise UserFacingError(
            f"说话人时间轴第 {index + 1} 段的 {field_name} 必须是整数。"
        )
    return raw


def _normalise_speaker_turns(
    turns: Sequence[SpeakerTurn], total_frames: int
) -> tuple[list[SpeakerTurn], list[str]]:
    """Turn sparse MOSS segments into non-overlapping, full-duration speaker runs.

    Silence between two different speakers is divided at its midpoint. An
    overlap is likewise divided at its midpoint and reported as a warning.
    Fully nested turns remain ambiguous and are rejected: representing them
    safely would require source separation rather than a linear timeline.
    """

    if total_frames <= 0:
        raise UserFacingError("无法将说话人时间轴应用到空音频。")
    ordered = sorted(turns, key=lambda item: (item.start_frame, item.end_frame, item.speaker_id))
    if not ordered:
        raise UserFacingError("说话人时间轴不包含 speakerId > 0 的有效分段。")

    sequence: list[SpeakerTurn] = []
    warnings: list[str] = []
    for turn in ordered:
        if turn.speaker_id <= 0 or turn.end_frame <= turn.start_frame:
            continue
        if sequence and sequence[-1].speaker_id == turn.speaker_id:
            previous = sequence[-1]
            sequence[-1] = SpeakerTurn(
                min(previous.start_frame, turn.start_frame),
                max(previous.end_frame, turn.end_frame),
                turn.speaker_id,
            )
            continue
        if sequence and turn.start_frame < sequence[-1].end_frame:
            previous = sequence[-1]
            overlap = previous.end_frame - turn.start_frame
            if turn.end_frame <= previous.end_frame:
                raise UserFacingError(
                    "说话人时间轴含有不同说话人的嵌套分段，SDK 无法保证单一说话人切片。"
                )
            overlap_ms = round(overlap * 1000 / SAMPLE_RATE)
            if overlap > _MAXIMUM_TOLERATED_SPEAKER_OVERLAP_FRAMES:
                warnings.append(
                    f"说话人时间轴中不同说话人重叠 {overlap_ms} ms；"
                    "SDK 模式已在重叠区中点分割，该处说话人建议人工复核。"
                )
            else:
                warnings.append("说话人时间轴存在轻微重叠，已在重叠区中点分割。")
        sequence.append(turn)

    if not sequence:
        raise UserFacingError("说话人时间轴不包含 speakerId > 0 的有效分段。")
    if len(sequence) == 1:
        return [SpeakerTurn(0, total_frames, sequence[0].speaker_id)], warnings
    if total_frames < len(sequence):
        raise UserFacingError("说话人分段数量超过音频帧数，无法生成非空切片。")

    boundaries: list[int] = []
    for previous, current in zip(sequence, sequence[1:]):
        # This midpoint handles either a silent gap or a tolerated overlap.
        boundary = round((previous.end_frame + current.start_frame) / 2)
        boundary = max(1, min(total_frames - 1, boundary))
        if boundaries and boundary <= boundaries[-1]:
            raise UserFacingError("说话人时间轴分段过密或顺序矛盾，无法安全切分。")
        boundaries.append(boundary)

    result: list[SpeakerTurn] = []
    start = 0
    for index, turn in enumerate(sequence):
        end = boundaries[index] if index < len(boundaries) else total_frames
        if end <= start:
            raise UserFacingError("说话人时间轴产生了空分段，无法安全切分。")
        result.append(SpeakerTurn(start, end, turn.speaker_id))
        start = end
    return result, list(dict.fromkeys(warnings))


def _load_speaker_timeline(
    path: Path, total_frames: int, audio_duration_ms: int
) -> tuple[list[SpeakerTurn], list[str]]:
    if not path.is_file():
        raise UserFacingError(f"找不到说话人时间轴：{path}")
    try:
        if path.stat().st_size > 128 * 1024 * 1024:
            raise UserFacingError("说话人时间轴 JSON 超过 128 MiB 限制。")
        root = json.loads(path.read_text(encoding="utf-8-sig"))
    except UserFacingError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise UserFacingError(f"无法读取说话人时间轴 JSON：{exc}") from exc
    if not isinstance(root, dict):
        raise UserFacingError("说话人时间轴 JSON 根节点必须是对象。")

    declared_duration = _case_insensitive_field(root, "durationMs")
    if declared_duration is not None:
        if isinstance(declared_duration, bool) or not isinstance(declared_duration, int):
            raise UserFacingError("说话人时间轴 durationMs 必须是整数。")
        tolerance_ms = max(2_000, round(audio_duration_ms * 0.02))
        if abs(declared_duration - audio_duration_ms) > tolerance_ms:
            raise UserFacingError(
                f"说话人时间轴时长 {declared_duration} ms 与音频 {audio_duration_ms} ms 不匹配。"
            )

    items = _case_insensitive_field(root, "segments")
    if items is None:
        items = _case_insensitive_field(root, "turns")
    if not isinstance(items, list):
        raise UserFacingError("说话人时间轴 JSON 必须包含 segments 数组（或 turns 数组）。")

    parsed: list[SpeakerTurn] = []
    ignored_unknown = False
    for index, item in enumerate(items):
        if not isinstance(item, dict):
            raise UserFacingError(f"说话人时间轴第 {index + 1} 段必须是对象。")
        start_ms = _timeline_integer(item, "startMs", index)
        end_ms = _timeline_integer(item, "endMs", index)
        speaker_id = _timeline_integer(item, "speakerId", index)
        if start_ms < 0 or end_ms <= start_ms:
            raise UserFacingError(f"说话人时间轴第 {index + 1} 段时间范围无效。")
        if start_ms >= audio_duration_ms:
            raise UserFacingError(f"说话人时间轴第 {index + 1} 段起点超出音频时长。")
        if speaker_id <= 0:
            ignored_unknown = True
            continue
        start_frame = min(total_frames - 1, round(start_ms * SAMPLE_RATE / 1000))
        end_frame = min(total_frames, round(end_ms * SAMPLE_RATE / 1000))
        if end_frame <= start_frame:
            raise UserFacingError(f"说话人时间轴第 {index + 1} 段换算后为空。")
        parsed.append(SpeakerTurn(start_frame, end_frame, speaker_id))

    runs, warnings = _normalise_speaker_turns(parsed, total_frames)
    if ignored_unknown:
        warnings.append("speakerId <= 0 的 MOSS 分段已忽略，相邻已知说话人在空档中点分界。")
    return runs, warnings


def _speaker_for_interval(start_frame: int, end_frame: int, runs: Sequence[SpeakerTurn]) -> int:
    midpoint = start_frame + (end_frame - start_frame) // 2
    for turn in runs:
        if turn.start_frame <= midpoint < turn.end_frame:
            return turn.speaker_id
    raise UserFacingError("切片未能映射到说话人时间轴。")


def _split_sdk_chunks_at_speaker_turns(
    raw_chunks: Sequence[Any], wav: Any, runs: Sequence[SpeakerTurn]
) -> list[SdkAudioChunk]:
    result: list[SdkAudioChunk] = []
    total_frames = len(wav)
    speaker_boundaries = [turn.end_frame for turn in runs[:-1]]
    for index, raw in enumerate(raw_chunks):
        try:
            start_frame = int(raw[0])
            end_frame = int(raw[1])
            samples = raw[2]
        except (IndexError, KeyError, TypeError, ValueError) as exc:
            raise UserFacingError(f"Silero VAD 第 {index + 1} 个分段格式无效。") from exc
        if start_frame < 0 or end_frame <= start_frame or end_frame > total_frames:
            raise UserFacingError(f"Silero VAD 第 {index + 1} 个分段超出音频范围。")
        if not runs:
            result.append(SdkAudioChunk(start_frame, end_frame, samples, 0))
            continue

        boundaries = [start_frame]
        boundaries.extend(
            boundary for boundary in speaker_boundaries if start_frame < boundary < end_frame
        )
        boundaries.append(end_frame)
        for piece_start, piece_end in zip(boundaries, boundaries[1:]):
            if piece_end <= piece_start:
                continue
            # Slice from the original waveform, not VAD's derived buffer, so
            # speaker transitions remain sample-accurate.
            result.append(
                SdkAudioChunk(
                    piece_start,
                    piece_end,
                    wav[piece_start:piece_end],
                    _speaker_for_interval(piece_start, piece_end, runs),
                )
            )
    if not result:
        raise UserFacingError("Silero VAD 没有产生可上传的音频分段。")
    return result


def _run_sdk(args: argparse.Namespace, audio: Path, info: WavInfo) -> dict[str, Any]:
    api_key = os.environ.get("DASHSCOPE_API_KEY", "").strip()
    if not api_key:
        raise UserFacingError("SDK 模式需要环境变量 DASHSCOPE_API_KEY；密钥不接受命令行传入。")
    try:
        import dashscope
        from silero_vad import load_silero_vad
        from qwen3_asr_toolkit.audio_tools import load_audio, process_vad, save_audio_file
        from qwen3_asr_toolkit.qwen3asr import QwenASR
    except ImportError as exc:
        raise UserFacingError(f"Qwen SDK 运行时不完整：{exc}") from exc

    dashscope.api_key = api_key
    _progress("正在用官方 Silero VAD 切分长音频…", 0.05)
    wav = load_audio(str(audio))
    if abs(len(wav) - info.frames) > 1:
        raise UserFacingError(
            f"官方音频加载器返回 {len(wav)} 帧，但 WAV 包含 {info.frames} 帧；"
            "为避免时间轴错位，已停止。"
        )
    speaker_runs: list[SpeakerTurn] = []
    timeline_warnings: list[str] = []
    if args.speaker_timeline:
        timeline_path = Path(args.speaker_timeline).expanduser().resolve()
        speaker_runs, timeline_warnings = _load_speaker_timeline(
            timeline_path, len(wav), info.duration_ms
        )
        _progress(
            f"已读取 MOSS 说话人时间轴：{len(speaker_runs)} 个连续区间",
            0.07,
        )
    vad_model = load_silero_vad(onnx=True)
    # Keep segments deliberately short.  The API does not return word timestamps,
    # so these boundaries are also the finest honest timeline we can expose to
    # the diarization merger.  With the default this targets 10 s and hard-caps
    # uninterrupted speech at 15 s.
    maximum_seconds = min(180, args.sdk_segment_seconds + 5)
    raw_vad_chunks = process_vad(
        wav,
        vad_model,
        segment_threshold_s=args.sdk_segment_seconds,
        max_segment_threshold_s=maximum_seconds,
    )
    if not raw_vad_chunks:
        raise UserFacingError("Silero VAD 没有产生可上传的音频分段。")
    wav_chunks = _split_sdk_chunks_at_speaker_turns(raw_vad_chunks, wav, speaker_runs)
    post_process = QwenASR(model=args.sdk_model).post_text_process
    qualifier = "VAD/说话人" if speaker_runs else "VAD"
    _progress(f"音频已分为 {len(wav_chunks)} 个 {qualifier} 分段", 0.10)

    with _temporary_audio_directory(args.output, "sdk") as temporary:
        temp_dir = Path(temporary)
        paths: list[Path] = []
        for index, chunk in enumerate(wav_chunks):
            path = temp_dir / f"sdk-{index:05d}.wav"
            save_audio_file(chunk.samples, str(path))
            paths.append(path)

        ordered = _transcribe_sdk_paths(
            paths,
            args.sdk_model,
            args.context,
            args.sdk_retries,
            post_process,
            args.sdk_workers,
        )

    segments: list[dict[str, Any]] = []
    texts: list[str] = []
    languages: list[str] = []
    for index, (chunk, result) in enumerate(zip(wav_chunks, ordered)):
        if result is None:
            raise UserFacingError(f"DashScope 第 {index + 1} 段没有返回结果。")
        language, text = result
        languages.append(language)
        if not text:
            continue
        texts.append(text)
        segments.append(
            {
                "startMs": round(chunk.start_frame * 1000 / SAMPLE_RATE),
                "endMs": min(info.duration_ms, round(chunk.end_frame * 1000 / SAMPLE_RATE)),
                "speakerId": chunk.speaker_id,
                "text": text,
            }
        )
    if not texts:
        raise UserFacingError("DashScope Qwen 未识别到可输出的语音文字。")

    warnings = [
        "SDK 模式需要联网，并会将 VAD 分段音频上传到 DashScope。",
        f"SDK 时间轴是 Silero VAD 分块边界（目标 {args.sdk_segment_seconds} 秒，"
        f"硬上限 {maximum_seconds} 秒），不是逐词强制对齐；本地高精度模式的时间轴更细。",
    ]
    warnings.extend(timeline_warnings)
    if speaker_runs:
        warnings.append(
            "SDK 音频已在上传前按 MOSS 说话人转换边界进一步切开；"
            "每个上传分段仅对应一个归一化说话人区间。"
        )
    if args.language != "auto":
        warnings.append("Qwen3-ASR-Flash SDK 官方接口会自动识别语言；--language 会被记录作为回退值，不能强制服务端语言。")
    full_text = "\n".join(texts).strip()
    return {
        "sourceFileName": audio.name,
        "durationMs": info.duration_ms,
        "model": args.sdk_model,
        "engineVersion": (
            f"dashscope {_package_version('dashscope')}; "
            f"qwen3-asr-toolkit {_package_version('qwen3-asr-toolkit')}"
        ),
        "backend": "dashscope-sdk",
        "language": _dominant_language(languages, args.language),
        "fullText": full_text,
        "rawText": full_text,
        "segments": segments,
        "warnings": list(dict.fromkeys(warnings)),
        "isPartial": False,
    }


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


def _build_parser() -> argparse.ArgumentParser:
    parser = JsonArgumentParser(description="InterviewScribe Qwen sidecar", allow_abbrev=False)
    parser.add_argument("--mode", required=True, choices=("local", "sdk"))
    parser.add_argument("--audio", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument(
        "--language",
        required=True,
        choices=("auto", "zh", "en", "yue", "ja", "ko", "fr", "de", "es", "pt", "ru", "it"),
    )
    parser.add_argument("--context", default="")

    parser.add_argument("--model-dir")
    parser.add_argument("--aligner-dir")
    parser.add_argument("--device", choices=("auto", "cuda", "cpu"))
    parser.add_argument("--chunk-seconds", type=int)
    parser.add_argument("--max-new-tokens", type=int)

    parser.add_argument("--sdk-model")
    parser.add_argument("--sdk-workers", type=int)
    parser.add_argument("--sdk-segment-seconds", type=int)
    parser.add_argument("--sdk-retries", type=int)
    parser.add_argument(
        "--speaker-timeline",
        help="SDK only: MOSS EngineResult JSON whose segments define speaker boundaries",
    )
    return parser


def _validate_arguments(args: argparse.Namespace) -> None:
    local_names = ("model_dir", "aligner_dir", "device", "chunk_seconds", "max_new_tokens")
    sdk_names = (
        "sdk_model",
        "sdk_workers",
        "sdk_segment_seconds",
        "sdk_retries",
        "speaker_timeline",
    )
    if args.mode == "local":
        if not args.model_dir or not args.aligner_dir:
            raise UserFacingError("local 模式必须提供 --model-dir 和 --aligner-dir。")
        if args.speaker_timeline is not None:
            raise UserFacingError("--speaker-timeline 仅支持 sdk 模式。")
        if any(getattr(args, name) is not None for name in sdk_names[:-1]):
            raise UserFacingError("local 模式不接受 --sdk-* 参数。")
        args.device = args.device if args.device is not None else "auto"
        args.chunk_seconds = args.chunk_seconds if args.chunk_seconds is not None else 240
        args.max_new_tokens = args.max_new_tokens if args.max_new_tokens is not None else 4096
        if not 30 <= args.chunk_seconds <= 285:
            raise UserFacingError("--chunk-seconds 必须在 30–285 之间。")
        if not 128 <= args.max_new_tokens <= 8192:
            raise UserFacingError("--max-new-tokens 必须在 128–8192 之间。")
    else:
        if any(getattr(args, name) is not None for name in local_names):
            raise UserFacingError("sdk 模式不接受本地模型或 --device/--chunk-seconds 参数。")
        args.sdk_model = args.sdk_model if args.sdk_model is not None else "qwen3-asr-flash"
        args.sdk_workers = args.sdk_workers if args.sdk_workers is not None else 2
        args.sdk_segment_seconds = args.sdk_segment_seconds if args.sdk_segment_seconds is not None else 10
        args.sdk_retries = args.sdk_retries if args.sdk_retries is not None else 3
        if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,127}", args.sdk_model):
            raise UserFacingError("--sdk-model 格式无效。")
        if not 1 <= args.sdk_workers <= 8:
            raise UserFacingError("--sdk-workers 必须在 1–8 之间。")
        if not 10 <= args.sdk_segment_seconds <= 120:
            raise UserFacingError("--sdk-segment-seconds 必须在 10–120 之间。")
        if not 1 <= args.sdk_retries <= 5:
            raise UserFacingError("--sdk-retries 必须在 1–5 之间。")


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = _build_parser().parse_args(argv)
        _validate_arguments(args)
        audio = Path(args.audio).expanduser().resolve()
        output = Path(args.output).expanduser().resolve()
        if audio == output:
            raise UserFacingError("--output 不能覆盖输入 WAV。")
        if args.speaker_timeline and output == Path(args.speaker_timeline).expanduser().resolve():
            raise UserFacingError("--output 不能覆盖 --speaker-timeline JSON。")
        info = _validate_wav(audio)
        _progress("已验证 16 kHz 单声道 PCM WAV", 0.01)
        result = _run_local(args, audio, info) if args.mode == "local" else _run_sdk(args, audio, info)
        _progress("正在原子写入识别结果…", 0.98)
        _atomic_write_json(output, result)
        _progress("识别完成", 1.0)
        return 0
    except KeyboardInterrupt:
        _emit("error", message="操作已取消。")
        return 130
    except UserFacingError as exc:
        _emit("error", message=_sanitize_error(str(exc)))
        return 2
    except Exception as exc:
        message = _sanitize_error(str(exc))
        if "out of memory" in message.lower():
            message = (
                "CUDA 显存不足。ASR 和对齐器已按顺序加载，但当前分块仍过大；"
                "请关闭占用 GPU 的程序，或减小 --chunk-seconds。"
            )
        _emit("error", message=message)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
