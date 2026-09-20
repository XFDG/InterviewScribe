from __future__ import annotations

import importlib.util
import json
import sys
import tempfile
import types
import unittest
import wave
from array import array
from pathlib import Path
from unittest import mock


SIDECAR_PATH = Path(__file__).resolve().parents[1] / "qwen_sidecar.py"
SPEC = importlib.util.spec_from_file_location("interviewscribe_qwen_sidecar", SIDECAR_PATH)
assert SPEC is not None and SPEC.loader is not None
sidecar = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = sidecar
SPEC.loader.exec_module(sidecar)


def write_wav(path: Path, seconds: float = 0.1, sample_rate: int = 16_000, channels: int = 1) -> None:
    frame_count = max(1, round(seconds * sample_rate))
    with wave.open(str(path), "wb") as stream:
        stream.setnchannels(channels)
        stream.setsampwidth(2)
        stream.setframerate(sample_rate)
        stream.writeframes(b"\0\0" * frame_count * channels)


class WavValidationTests(unittest.TestCase):
    def test_accepts_required_pcm_format(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "valid.wav"
            write_wav(path, seconds=0.25)
            info = sidecar._validate_wav(path)
            self.assertEqual(4_000, info.frames)
            self.assertEqual(250, info.duration_ms)

    def test_rejects_wrong_sample_rate(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "invalid.wav"
            write_wav(path, sample_rate=8_000)
            with self.assertRaisesRegex(sidecar.UserFacingError, "16 kHz"):
                sidecar._validate_wav(path)

    def test_wav_chunk_preserves_every_requested_sample_across_read_blocks(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "source.wav"
            destination = Path(directory) / "chunk.wav"
            samples = array(
                "h",
                ((index % 60_001) - 30_000 for index in range(31 * sidecar.SAMPLE_RATE)),
            )
            with wave.open(str(source), "wb") as stream:
                stream.setnchannels(sidecar.CHANNELS)
                stream.setsampwidth(sidecar.SAMPLE_WIDTH)
                stream.setframerate(sidecar.SAMPLE_RATE)
                stream.writeframes(samples.tobytes())

            start_frame = 17
            end_frame = 30 * sidecar.SAMPLE_RATE + 53
            sidecar._write_wav_chunk(source, destination, start_frame, end_frame)

            with wave.open(str(destination), "rb") as stream:
                actual_frames = stream.getnframes()
                actual = stream.readframes(actual_frames)
            self.assertEqual(end_frame - start_frame, actual_frames)
            self.assertEqual(samples[start_frame:end_frame].tobytes(), actual)


class ModelValidationTests(unittest.TestCase):
    def test_requires_exact_revision_marker(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            model = Path(directory)
            (model / "config.json").write_text("{}", encoding="utf-8")
            (model / "model.safetensors").write_bytes(b"placeholder")
            (model / sidecar.REVISION_MARKER).write_text("wrong\n", encoding="utf-8")
            with self.assertRaisesRegex(sidecar.UserFacingError, "版本不匹配"):
                sidecar._validate_model_directory(model, "expected", "model")

            (model / sidecar.REVISION_MARKER).write_text("expected\n", encoding="utf-8")
            sidecar._validate_model_directory(model, "expected", "model")


class TimelineTests(unittest.TestCase):
    def test_aligned_words_stay_word_level_and_keep_chunk_offset(self) -> None:
        words = [
            {"text": "Hello", "start_time": 0.1, "end_time": 0.4},
            {"text": "world.", "start_time": 0.5, "end_time": 1.0},
            {"text": "你", "start_time": 1.2, "end_time": 1.4},
            {"text": "好！", "start_time": 1.4, "end_time": 2.1},
        ]
        segments = sidecar._aligned_word_segments(words, 5_000, 10_000)
        self.assertEqual(4, len(segments))
        self.assertEqual(["Hello", "world.", "你", "好！"], [item["text"] for item in segments])
        self.assertEqual((5_100, 5_400), (segments[0]["startMs"], segments[0]["endMs"]))
        self.assertEqual((5_500, 6_000), (segments[1]["startMs"], segments[1]["endMs"]))
        self.assertTrue(all(segment["speakerId"] == 0 for segment in segments))

    def test_projection_preserves_original_punctuation_and_english_spacing(self) -> None:
        words = [
            {"text": "你", "start_time": 0.1, "end_time": 0.2},
            {"text": "好", "start_time": 0.2, "end_time": 0.4},
            {"text": "world", "start_time": 0.5, "end_time": 0.9},
            {"text": "Hello", "start_time": 1.0, "end_time": 1.4},
        ]
        original = "你好，world! Hello?"
        display = sidecar._project_original_text_onto_alignment(words, original)
        self.assertEqual(["你", "好", "，world", "! Hello?"], display)
        segments = sidecar._aligned_word_segments(words, 0, 2_000, display)
        self.assertEqual(original, "".join(segment["text"] for segment in segments))

    def test_projection_refuses_to_drop_unmatched_original_text(self) -> None:
        words = [{"text": "different", "start_time": 0.1, "end_time": 0.5}]
        self.assertIsNone(sidecar._project_original_text_onto_alignment(words, "原始文字，保留标点。"))

    def test_projection_refuses_an_aligner_that_skips_a_source_word(self) -> None:
        words = [{"text": "world", "start_time": 0.1, "end_time": 0.5}]
        # Searching ahead would incorrectly place "hello" on the world token's
        # timestamp.  The caller must use its coarse, whole-chunk fallback.
        self.assertIsNone(sidecar._project_original_text_onto_alignment(words, "hello world"))

    def test_official_forced_aligner_result_uses_items_field_and_preserves_punctuation(self) -> None:
        class FakeAligner:
            @classmethod
            def from_pretrained(cls, *_args: object, **_kwargs: object) -> "FakeAligner":
                return cls()

            def align(self, **_kwargs: object) -> list[object]:
                return [
                    types.SimpleNamespace(
                        items=[
                            types.SimpleNamespace(text="hello", start_time=0.1, end_time=0.4),
                            types.SimpleNamespace(text="world", start_time=0.5, end_time=0.9),
                        ]
                    )
                ]

        fake_torch = types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda: False))
        fake_qwen_asr = types.ModuleType("qwen_asr")
        fake_qwen_asr.Qwen3ForcedAligner = FakeAligner
        transcript = sidecar.TranscriptChunk(
            sidecar.AudioChunk(Path("chunk.wav"), sidecar.SAMPLE_RATE, 3 * sidecar.SAMPLE_RATE),
            "hello, world!",
            "hello, world!",
            "English",
        )

        with mock.patch.dict(sys.modules, {"qwen_asr": fake_qwen_asr}):
            segments, warnings = sidecar._align_local_chunks(
                [transcript], Path("aligner"), "cpu", object(), "en", fake_torch
            )

        self.assertEqual([], warnings)
        self.assertEqual(["hello", ", world!"], [segment["text"] for segment in segments])
        self.assertEqual((1_100, 1_400), (segments[0]["startMs"], segments[0]["endMs"]))

    def test_unsupported_auto_detected_language_is_coarse_not_false_chinese_alignment(self) -> None:
        class FakeAligner:
            @classmethod
            def from_pretrained(cls, *_args: object, **_kwargs: object) -> "FakeAligner":
                raise AssertionError("unsupported language must not load the forced aligner")

        fake_torch = types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda: False))
        fake_qwen_asr = types.ModuleType("qwen_asr")
        fake_qwen_asr.Qwen3ForcedAligner = FakeAligner
        transcript = sidecar.TranscriptChunk(
            sidecar.AudioChunk(Path("chunk.wav"), sidecar.SAMPLE_RATE, 3 * sidecar.SAMPLE_RATE),
            "مرحبا، world!",
            "مرحبا، world!",
            "Arabic",
        )

        with mock.patch.dict(sys.modules, {"qwen_asr": fake_qwen_asr}):
            segments, warnings = sidecar._align_local_chunks(
                [transcript], Path("aligner"), "cpu", object(), "auto", fake_torch
            )

        self.assertEqual(["مرحبا، world!"], [segment["text"] for segment in segments])
        self.assertEqual((1_000, 3_000), (segments[0]["startMs"], segments[0]["endMs"]))
        self.assertTrue(any("分段级" in warning and "Arabic" in warning for warning in warnings))

    def test_decoder_failure_stops_without_fabricating_timestamps(self) -> None:
        class FakeAligner:
            @classmethod
            def from_pretrained(cls, *_args: object, **_kwargs: object) -> "FakeAligner":
                return cls()

            def align(self, **_kwargs: object) -> list[object]:
                raise RuntimeError("decoder broke")

        fake_torch = types.SimpleNamespace(cuda=types.SimpleNamespace(is_available=lambda: False))
        fake_qwen_asr = types.ModuleType("qwen_asr")
        fake_qwen_asr.Qwen3ForcedAligner = FakeAligner
        transcript = sidecar.TranscriptChunk(
            sidecar.AudioChunk(Path("chunk.wav"), sidecar.SAMPLE_RATE, 3 * sidecar.SAMPLE_RATE),
            "hello",
            "hello",
            "English",
        )
        with mock.patch.dict(sys.modules, {"qwen_asr": fake_qwen_asr}):
            with self.assertRaisesRegex(sidecar.UserFacingError, r"第 1/1 段强制对齐失败.*未生成粗粒度"):
                sidecar._align_local_chunks(
                    [transcript], Path("aligner"), "cpu", object(), "en", fake_torch
                )

    def test_invalid_aligner_word_is_not_silently_dropped(self) -> None:
        invalid_words = (
            {"text": "missing-end", "start_time": 0.1},
            {"text": "", "start_time": 0.1, "end_time": 0.2},
            {"text": "nan", "start_time": float("nan"), "end_time": 0.2},
            {"text": "reverse", "start_time": 0.2, "end_time": 0.1},
            {"text": "past-chunk", "start_time": 0.1, "end_time": 5.1},
        )
        for word in invalid_words:
            with self.subTest(word=word):
                with self.assertRaisesRegex(sidecar.UserFacingError, "第 1 个词/token"):
                    sidecar._aligned_word_segments([word], 5_000, 10_000)

    def test_audio_chunk_boundaries_are_contiguous(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "long.wav"
            write_wav(path, seconds=65)
            boundaries = sidecar._chunk_boundaries(path, 65 * 16_000, 30)
            self.assertEqual(0, boundaries[0][0])
            self.assertEqual(65 * sidecar.SAMPLE_RATE, boundaries[-1][1])
            self.assertTrue(all(left[1] == right[0] for left, right in zip(boundaries, boundaries[1:])))
            self.assertTrue(all(end - start <= 30 * sidecar.SAMPLE_RATE for start, end in boundaries))


class ProtocolTests(unittest.TestCase):
    def test_atomic_json_write_replaces_destination(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.json"
            path.write_text("old", encoding="utf-8")
            sidecar._atomic_write_json(path, {"fullText": "中文"})
            self.assertEqual({"fullText": "中文"}, json.loads(path.read_text(encoding="utf-8")))
            self.assertEqual([], list(path.parent.glob(f".{path.name}.*.tmp")))

    def test_temporary_audio_is_scoped_to_output_job_directory(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            job_directory = Path(directory) / "job"
            output = job_directory / "qwen-result.json"
            with sidecar._temporary_audio_directory(str(output), "local") as temporary:
                temporary_path = Path(temporary)
                self.assertEqual(job_directory.resolve(), temporary_path.parent)
                self.assertTrue(temporary_path.name.startswith(".qwen-audio-local-"))
                self.assertTrue(temporary_path.is_dir())
            self.assertFalse(temporary_path.exists())


class ArgumentTests(unittest.TestCase):
    @staticmethod
    def parse(*extra: str):
        return sidecar._build_parser().parse_args(
            [
                "--mode",
                "local",
                "--audio",
                "input.wav",
                "--output",
                "output.json",
                "--language",
                "auto",
                "--model-dir",
                "asr",
                "--aligner-dir",
                "aligner",
                *extra,
            ]
        )

    def test_local_defaults_are_conservative_and_nontruncating(self) -> None:
        args = self.parse()
        sidecar._validate_arguments(args)
        self.assertEqual(150, args.chunk_seconds)
        self.assertEqual(8192, args.max_new_tokens)

    def test_parser_accepts_every_forced_aligner_language(self) -> None:
        for language in ("zh", "en", "yue", "ja", "ko", "fr", "de", "es", "pt", "ru", "it"):
            with self.subTest(language=language):
                args = self.parse("--language", language)
                self.assertEqual(language, args.language)

    def test_explicit_zero_is_rejected_instead_of_defaulted(self) -> None:
        args = self.parse("--chunk-seconds", "0")
        with self.assertRaisesRegex(sidecar.UserFacingError, "30–180"):
            sidecar._validate_arguments(args)

    def test_legacy_sdk_arguments_are_rejected_by_the_parser(self) -> None:
        with self.assertRaisesRegex(sidecar.UserFacingError, "unrecognized arguments"):
            self.parse("--sdk-model", "qwen3-asr-flash")

    def test_sdk_mode_is_not_an_active_backend(self) -> None:
        with self.assertRaisesRegex(sidecar.UserFacingError, "invalid choice"):
            sidecar._build_parser().parse_args(
                ["--mode", "sdk", "--audio", "input.wav", "--output", "output.json", "--language", "auto"]
            )


if __name__ == "__main__":
    unittest.main()
