#!/usr/bin/env python3
"""MediaScribe local, offline Qwen transcription sidecar."""

from __future__ import annotations

import argparse
import gc
import json
import math
import os
import re
import sys
import tempfile
import unicodedata
import wave
from array import array
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence


SAMPLE_RATE = 16_000
SAMPLE_WIDTH = 2
CHANNELS = 1
ASR_REPOSITORY = "Qwen/Qwen3-ASR-1.7B"
ASR_REVISION = "d69410f1c275f2b0fa60cbb9960edfcdb0ae0aec"
ALIGNER_REPOSITORY = "Qwen/Qwen3-ForcedAligner-0.6B"
ALIGNER_REVISION = "6f4d7c9606feb7adf282c9e4b139f28e8695d867"
REVISION_MARKER = ".interviewscribe-revision"

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

# Qwen3-ASR recognizes 30 languages, whereas Qwen3-ForcedAligner-0.6B only
# supports these 11.  Do not force an unsupported auto-detected language into
# Chinese merely to fabricate a fine-grained timeline.
_ALIGNER_LANGUAGE_NAMES = {
    "zh": "Chinese",
    "en": "English",
    "yue": "Cantonese",
    "fr": "French",
    "de": "German",
    "it": "Italian",
    "ja": "Japanese",
    "ko": "Korean",
    "pt": "Portuguese",
    "ru": "Russian",
    "es": "Spanish",
}


class UserFacingError(RuntimeError):
    """An expected failure whose message is safe to show in the GUI."""


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


def _emit(event_type: str, **values: Any) -> None:
    event = {"type": event_type, **values}
    sys.stderr.write(json.dumps(event, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stderr.flush()


def _progress(message: str, fraction: float) -> None:
    _emit("progress", message=message, fraction=max(0.0, min(1.0, fraction)))


def _sanitize_error(message: str) -> str:
    result = message.strip() or "未知错误"
    # Avoid dumping an enormous native exception into the GUI diagnostics.
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
    from qwen_asr import Qwen3ASRModel

    model: Any = None
    results: list[TranscriptChunk] = []
    try:
        _progress("正在加载 Qwen3-ASR-1.7B（严格离线）…", 0.08)
        # Use Qwen's maintained Transformers wrapper rather than the legacy
        # raw-AutoModel calls.  The model directories are already validated and
        # are passed as paths, while local_files_only plus offline env flags make
        # a network fallback impossible at inference time.
        model = Qwen3ASRModel.from_pretrained(
            str(model_dir),
            local_files_only=True,
            dtype=dtype,
            device_map="cuda:0" if device == "cuda" else "cpu",
            max_inference_batch_size=1,
            max_new_tokens=max_new_tokens,
        )

        for index, chunk in enumerate(chunks):
            try:
                forced_language = None if language == "auto" else _LANGUAGE_NAMES[language]
                result = model.transcribe(
                    audio=str(chunk.path),
                    context=context,
                    language=forced_language,
                    return_time_stamps=False,
                )
                if len(result) != 1:
                    raise ValueError(f"Qwen 返回了 {len(result)} 个结果，期望 1 个。")
                text = str(result[0].text or "").strip()
                raw = text
                detected_language = str(
                    result[0].language or _LANGUAGE_NAMES.get(language, language)
                ).strip()
            except (KeyboardInterrupt, SystemExit):
                raise
            except Exception as exc:
                raise UserFacingError(
                    f"第 {index + 1}/{len(chunks)} 段 Qwen 转录失败：{_sanitize_error(str(exc))}"
                ) from exc
            results.append(TranscriptChunk(chunk, text, raw, detected_language))
            fraction = 0.13 + 0.42 * ((index + 1) / len(chunks))
            _progress(f"Qwen 转录 {index + 1}/{len(chunks)}", fraction)
        return results
    finally:
        # This function must return only plain Python data.  Dropping every model
        # reference before loading the aligner is essential on an 8 GB GPU.
        del model
        _release_cuda(torch_module)


def _alignment_language(detected: str, requested: str) -> str | None:
    """Return a truthful ForcedAligner language, or None for a coarse fallback.

    Qwen can report a comma-separated language list for code-switching.  A
    detected unsupported language must *not* be silently aligned as Chinese:
    doing so would make invented timestamps look authoritative.
    """

    candidates = [item.strip().lower() for item in re.split(r"[,;/]", detected) if item.strip()]
    resolved: list[str] = []
    for candidate in candidates:
        canonical: str | None = None
        for code, name in _LANGUAGE_NAMES.items():
            if candidate in {code, name.lower()}:
                canonical = name
                break
        if canonical is None and ("chinese" in candidate or "mandarin" in candidate):
            canonical = "Chinese"
        if canonical is None:
            # Do not guess whether an unknown label is supported.
            return None
        if canonical not in _ALIGNER_LANGUAGE_NAMES.values():
            return None
        resolved.append(canonical)

    if resolved:
        return resolved[0]
    # All selectable explicit GUI languages are in this allow-list.  This
    # fallback is only used when auto LID reports an empty label.
    return _ALIGNER_LANGUAGE_NAMES.get(requested)


def _alignment_text_key(value: str) -> str:
    """Normalize transcript text like the official aligner, retaining offsets."""

    result: list[str] = []
    for character in unicodedata.normalize("NFKC", value).casefold():
        if character in {"'", "\u2018", "\u2019"}:
            result.append("'")
        elif character.isalnum():
            result.append(character)
    return "".join(result)


def _project_original_text_onto_alignment(
    words: Sequence[Any], transcript_text: str
) -> list[str] | None:
    """Attach original punctuation/spacing to the following aligned token.

    Qwen3-ForcedAligner deliberately removes punctuation while tokenizing.  The
    app exports its timeline segments, not only ``fullText``, so using the
    aligner's tokens directly would silently strip punctuation from TXT/MD/
    Word/PDF.  This ordered projection retains the ASR text verbatim whenever
    its normalized aligned tokens can be located safely.
    """

    if not transcript_text:
        return None

    source_key_parts: list[str] = []
    source_offsets: list[int] = []
    for source_index, character in enumerate(transcript_text):
        normalized = _alignment_text_key(character)
        source_key_parts.append(normalized)
        source_offsets.extend([source_index] * len(normalized))
    source_key = "".join(source_key_parts)
    if not source_key:
        return None

    end_positions: list[int] = []
    key_cursor = 0
    for word in words:
        raw_token = word.get("text") if isinstance(word, dict) else getattr(word, "text", None)
        token_key = _alignment_text_key(str(raw_token or ""))
        if not token_key:
            return None
        # The aligner must account for the next normalized source token exactly.
        # Searching ahead would make a dropped word inherit the following word's
        # timestamp, which is worse than returning the existing coarse fallback.
        match_index = source_key.find(token_key, key_cursor)
        if match_index != key_cursor:
            return None
        end_positions.append(source_offsets[match_index + len(token_key) - 1] + 1)
        key_cursor = match_index + len(token_key)

    if not end_positions:
        return None

    display_texts: list[str] = []
    for index, end in enumerate(end_positions):
        # Prefix punctuation/whitespace belongs to the token on its right.  The
        # C# reading merger trims individual tokens, so this preserves `Hello,
        # world!` rather than turning it into `Hello,world!`.
        segment_start = 0 if index == 0 else end_positions[index - 1]
        if end <= segment_start:
            return None
        display_texts.append(transcript_text[segment_start:end])
    # The last token owns source suffix punctuation/whitespace, which has no
    # subsequent aligned token to receive it.
    display_texts[-1] += transcript_text[end_positions[-1] :]
    return display_texts


def _aligned_word_segments(
    words: Sequence[Any],
    chunk_start_ms: int,
    chunk_end_ms: int,
    display_texts: Sequence[str] | None = None,
) -> list[dict[str, Any]]:
    """Keep the forced aligner's native word/token granularity.

    Speaker turns can be much shorter than a sentence.  Combining timestamps
    here would force the C# merger to guess how a sentence's characters should
    be divided at a speaker boundary.  Returning each aligned unit lets it use
    real overlap and coalesce only after speaker assignment.
    """

    words = list(words)
    if display_texts is not None and len(display_texts) != len(words):
        raise UserFacingError("对齐文本投影数量与时间轴 token 数量不一致。")

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

        aligned_token = str(token_value or "").strip()
        if not aligned_token:
            raise UserFacingError(f"对齐器第 {index + 1} 个词/token 缺少文本。")
        token = str(display_texts[index]) if display_texts is not None else aligned_token
        if not token:
            raise UserFacingError(f"对齐器第 {index + 1} 个词/token 投影后为空。")
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
    from qwen_asr import Qwen3ForcedAligner

    model: Any = None
    segments: list[dict[str, Any]] = []
    warnings: list[str] = []
    alignment_plan = [
        (index, item, _alignment_language(item.language, requested_language))
        for index, item in enumerate(transcripts)
        if item.text
    ]
    try:
        if any(language is not None for _, _, language in alignment_plan):
            _progress("已释放 ASR；正在加载 Qwen3 Forced Aligner…", 0.61)
            model = Qwen3ForcedAligner.from_pretrained(
                str(aligner_dir),
                local_files_only=True,
                dtype=dtype,
                device_map="cuda:0" if device == "cuda" else "cpu",
            )

        for aligned_index, (index, item, language) in enumerate(alignment_plan):
            if language is None:
                # Qwen ASR can recognize more languages than ForcedAligner.
                # Preserve the original transcript, but label the timestamp as
                # chunk-level rather than pretending it is word-level.
                segments.append(
                    {
                        "startMs": item.audio.start_ms,
                        "endMs": item.audio.end_ms,
                        "speakerId": 0,
                        "text": item.raw_text or item.text,
                    }
                )
                warnings.append(
                    f"第 {index + 1}/{len(transcripts)} 段检测为 “{item.language or '未知语言'}”，"
                    "不在 Qwen3-ForcedAligner 的 11 种支持语言内；已保留原文，"
                    "该段时间轴为音频分段级而非逐词级。"
                )
                fraction = 0.66 + 0.29 * ((aligned_index + 1) / len(alignment_plan))
                _progress(f"时间轴对齐 {aligned_index + 1}/{len(alignment_plan)}（分段级）", fraction)
                continue
            try:
                if model is None:
                    raise RuntimeError("ForcedAligner 未加载。")
                results = model.align(
                    audio=str(item.audio.path),
                    text=item.text,
                    language=language,
                )
                if len(results) != 1:
                    raise ValueError(f"对齐器返回了 {len(results)} 个结果，期望 1 个。")
                timestamps = results[0]
                # qwen-asr's public API returns a ForcedAlignResult object.
                # Its word/token sequence lives in `.items`; older test doubles
                # and earlier wrappers returned the sequence directly.  Accept
                # both shapes, but never iterate the result object itself (it is
                # not a Sequence in the official package).
                if isinstance(timestamps, dict):
                    aligned_items = timestamps.get("items", timestamps)
                elif isinstance(timestamps, (list, tuple)):
                    aligned_items = timestamps
                else:
                    aligned_items = getattr(timestamps, "items", timestamps)
                if callable(aligned_items):
                    raise ValueError("对齐器结果不包含可迭代的 items。")
                aligned_items = list(aligned_items)
                display_texts = _project_original_text_onto_alignment(
                    aligned_items, item.raw_text or item.text
                )
                if display_texts is None:
                    # Do not let a tokenization difference erase punctuation or
                    # words from rendered exports.  This explicit coarse fallback
                    # is safer than claiming per-word timestamps for altered text.
                    segments.append(
                        {
                            "startMs": item.audio.start_ms,
                            "endMs": item.audio.end_ms,
                            "speakerId": 0,
                            "text": item.raw_text or item.text,
                        }
                    )
                    warnings.append(
                        f"第 {index + 1}/{len(transcripts)} 段的对齐 token 无法与 Qwen 原文安全映射；"
                        "已保留原始标点文字，该段时间轴为音频分段级而非逐词级。"
                    )
                    fraction = 0.66 + 0.29 * ((aligned_index + 1) / len(alignment_plan))
                    _progress(f"时间轴对齐 {aligned_index + 1}/{len(alignment_plan)}（分段级）", fraction)
                    continue
                aligned_words = _aligned_word_segments(
                    aligned_items,
                    item.audio.start_ms,
                    item.audio.end_ms,
                    display_texts,
                )
                if not aligned_words:
                    raise ValueError("对齐器未返回有效时间戳")
                segments.extend(aligned_words)
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
            fraction = 0.66 + 0.29 * ((aligned_index + 1) / len(alignment_plan))
            _progress(f"时间轴对齐 {aligned_index + 1}/{len(alignment_plan)}", fraction)
        return segments, warnings
    finally:
        del model
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
    _validate_model_directory(model_dir, ASR_REVISION, "Qwen3-ASR-1.7B")
    _validate_model_directory(aligner_dir, ALIGNER_REVISION, "Qwen3-ForcedAligner-0.6B")
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
    parser = JsonArgumentParser(description="MediaScribe local Qwen sidecar", allow_abbrev=False)
    parser.add_argument("--mode", required=True, choices=("local",))
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
    return parser


def _validate_arguments(args: argparse.Namespace) -> None:
    if not args.model_dir or not args.aligner_dir:
        raise UserFacingError("local 模式必须提供 --model-dir 和 --aligner-dir。")
    args.device = args.device if args.device is not None else "auto"
    # The official ForcedAligner supports up to five minutes in its combined
    # wrapper, but our sequential low-VRAM path loads it directly.  Keep each
    # input below 180 seconds and default conservatively for 8 GB GPUs.
    args.chunk_seconds = args.chunk_seconds if args.chunk_seconds is not None else 150
    # The official wrapper does not expose whether generation stopped at its
    # token cap. Use a deliberately high ceiling so a rapid 180-second
    # recording is not silently marked complete after truncation. Dynamic cache
    # allocation grows with actual output, rather than reserving this ceiling.
    args.max_new_tokens = args.max_new_tokens if args.max_new_tokens is not None else 8192
    if not 30 <= args.chunk_seconds <= 180:
        raise UserFacingError("--chunk-seconds 必须在 30–180 之间。")
    if not 128 <= args.max_new_tokens <= 8192:
        raise UserFacingError("--max-new-tokens 必须在 128–8192 之间。")


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = _build_parser().parse_args(argv)
        _validate_arguments(args)
        audio = Path(args.audio).expanduser().resolve()
        output = Path(args.output).expanduser().resolve()
        if audio == output:
            raise UserFacingError("--output 不能覆盖输入 WAV。")
        info = _validate_wav(audio)
        _progress("已验证 16 kHz 单声道 PCM WAV", 0.01)
        result = _run_local(args, audio, info)
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
