from __future__ import annotations

import argparse
import importlib.util
import json
import os
import sys
import tempfile
import threading
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
            with self.assertRaisesRegex(sidecar.UserFacingError, "\u7248\u672c\u4e0d\u5339\u914d"):
                sidecar._validate_model_directory(model, "expected", "model")

            (model / sidecar.REVISION_MARKER).write_text("expected\n", encoding="utf-8")
            sidecar._validate_model_directory(model, "expected", "model")


class TimelineTests(unittest.TestCase):
    def test_aligned_words_stay_word_level_and_keep_chunk_offset(self) -> None:
        words = [
            {"text": "Hello", "start_time": 0.1, "end_time": 0.4},
            {"text": "world.", "start_time": 0.5, "end_time": 1.0},
            {"text": "\u4f60", "start_time": 1.2, "end_time": 1.4},
            {"text": "\u597d\uff01", "start_time": 1.4, "end_time": 2.1},
        ]
        segments = sidecar._aligned_word_segments(words, 5_000, 10_000)
        self.assertEqual(4, len(segments))
        self.assertEqual(["Hello", "world.", "\u4f60", "\u597d\uff01"], [item["text"] for item in segments])
        self.assertEqual((5_100, 5_400), (segments[0]["startMs"], segments[0]["endMs"]))
        self.assertEqual((5_500, 6_000), (segments[1]["startMs"], segments[1]["endMs"]))
        self.assertTrue(all(segment["speakerId"] == 0 for segment in segments))

    def test_aligner_failure_or_empty_result_never_emits_chunk_fallback(self) -> None:
        class FakeInputs(dict):
            def to(self, _device: str, _dtype: object) -> "FakeInputs":
                return self

        class FakeProcessor:
            decode_outcome: object = []

            @classmethod
            def from_pretrained(cls, *_args: object, **_kwargs: object) -> "FakeProcessor":
                return cls()

            def prepare_forced_aligner_inputs(self, **_kwargs: object) -> tuple[FakeInputs, list[str]]:
                return FakeInputs(input_ids=[[1]]), ["hello"]

            def decode_forced_alignment(self, **_kwargs: object) -> list[list[object]]:
                if isinstance(self.decode_outcome, BaseException):
                    raise self.decode_outcome
                return [self.decode_outcome]  # type: ignore[list-item]

        class FakeModel:
            config = types.SimpleNamespace(timestamp_token_id=1)

            @classmethod
            def from_pretrained(cls, *_args: object, **_kwargs: object) -> "FakeModel":
                return cls()

            def to(self, _device: str) -> "FakeModel":
                return self

            def eval(self) -> None:
                return None

            def __call__(self, **_kwargs: object) -> object:
                return types.SimpleNamespace(logits=[])

        class FakeInferenceMode:
            def __enter__(self) -> None:
                return None

            def __exit__(self, *_args: object) -> None:
                return None

        fake_torch = types.SimpleNamespace(
            inference_mode=lambda: FakeInferenceMode(),
            cuda=types.SimpleNamespace(is_available=lambda: False),
        )
        fake_transformers = types.ModuleType("transformers")
        fake_transformers.AutoProcessor = FakeProcessor
        fake_transformers.AutoModelForTokenClassification = FakeModel
        transcript = sidecar.TranscriptChunk(
            sidecar.AudioChunk(
                Path("chunk.wav"), sidecar.SAMPLE_RATE, 3 * sidecar.SAMPLE_RATE
            ),
            "hello",
            "hello",
            "English",
        )

        with mock.patch.dict(sys.modules, {"transformers": fake_transformers}):
            for label, outcome in (
                ("empty", []),
                ("decoder-error", RuntimeError("decoder broke")),
            ):
                with self.subTest(label=label):
                    FakeProcessor.decode_outcome = outcome
                    with self.assertRaisesRegex(
                        sidecar.UserFacingError,
                        r"第 1/1 段强制对齐失败（1000–3000 ms）.*未生成粗粒度",
                    ):
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
            self.assertEqual(65 * 16_000, boundaries[-1][1])
            self.assertTrue(all(left[1] == right[0] for left, right in zip(boundaries, boundaries[1:])))
            self.assertTrue(all(end - start <= 30 * 16_000 for start, end in boundaries))


class SpeakerTimelineTests(unittest.TestCase):
    def test_short_question_is_not_merged_into_long_answer(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            timeline = Path(directory) / "moss.json"
            timeline.write_text(
                json.dumps(
                    {
                        "durationMs": 6_000,
                        "segments": [
                            {"startMs": 0, "endMs": 250, "speakerId": 1, "text": ""},
                            {"startMs": 250, "endMs": 6_000, "speakerId": 2, "text": ""},
                        ],
                    }
                ),
                encoding="utf-8",
            )
            total_frames = 6 * sidecar.SAMPLE_RATE
            runs, warnings = sidecar._load_speaker_timeline(timeline, total_frames, 6_000)
            wav = array("f", [0.0]) * total_frames
            chunks = sidecar._split_sdk_chunks_at_speaker_turns(
                [(0, total_frames, wav)], wav, runs
            )

        self.assertEqual([], warnings)
        self.assertEqual([1, 2], [chunk.speaker_id for chunk in chunks])
        self.assertEqual(250, round(chunks[0].end_frame * 1000 / sidecar.SAMPLE_RATE))
        self.assertEqual(chunks[0].end_frame, chunks[1].start_frame)

    def test_vad_chunk_is_split_at_cross_speaker_boundary(self) -> None:
        total_frames = 2 * sidecar.SAMPLE_RATE
        wav = array("f", [0.0]) * total_frames
        runs = [
            sidecar.SpeakerTurn(0, sidecar.SAMPLE_RATE, 4),
            sidecar.SpeakerTurn(sidecar.SAMPLE_RATE, total_frames, 9),
        ]
        chunks = sidecar._split_sdk_chunks_at_speaker_turns(
            [
                (
                    sidecar.SAMPLE_RATE // 2,
                    sidecar.SAMPLE_RATE + sidecar.SAMPLE_RATE // 2,
                    wav[sidecar.SAMPLE_RATE // 2 : sidecar.SAMPLE_RATE + sidecar.SAMPLE_RATE // 2],
                )
            ],
            wav,
            runs,
        )
        self.assertEqual(
            [
                (sidecar.SAMPLE_RATE // 2, sidecar.SAMPLE_RATE, 4),
                (sidecar.SAMPLE_RATE, sidecar.SAMPLE_RATE + sidecar.SAMPLE_RATE // 2, 9),
            ],
            [(item.start_frame, item.end_frame, item.speaker_id) for item in chunks],
        )

    def test_large_different_speaker_overlap_is_split_with_warning(self) -> None:
        runs, warnings = sidecar._normalise_speaker_turns(
            [
                sidecar.SpeakerTurn(0, sidecar.SAMPLE_RATE, 1),
                sidecar.SpeakerTurn(sidecar.SAMPLE_RATE // 2, 2 * sidecar.SAMPLE_RATE, 2),
            ],
            2 * sidecar.SAMPLE_RATE,
        )

        self.assertEqual([1, 2], [run.speaker_id for run in runs])
        self.assertEqual(runs[0].end_frame, runs[1].start_frame)
        self.assertTrue(any("500 ms" in warning and "人工复核" in warning for warning in warnings))


class ProtocolTests(unittest.TestCase):
    def test_sdk_response_parses_official_shape(self) -> None:
        response = {
            "status_code": 200,
            "output": {
                "choices": [
                    {
                        "message": {
                            "content": [{"text": "\u4f60\u597d"}],
                            "annotations": [{"language": "zh"}],
                        }
                    }
                ]
            },
        }
        self.assertEqual(("Chinese", "\u4f60\u597d"), sidecar._sdk_response(response))

    def test_sdk_response_preserves_structured_error_metadata(self) -> None:
        response = {
            "status_code": 429,
            "code": "Throttling",
            "message": "slow down",
            "headers": {"Retry-After": "3"},
        }
        with self.assertRaises(sidecar.DashScopeResponseError) as raised:
            sidecar._sdk_response(response)
        self.assertEqual(429, raised.exception.status)
        self.assertEqual("Throttling", raised.exception.code)
        self.assertEqual(3.0, raised.exception.retry_after_seconds)

    def test_atomic_json_write_replaces_destination(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "result.json"
            path.write_text("old", encoding="utf-8")
            sidecar._atomic_write_json(path, {"fullText": "\u4e2d\u6587"})
            self.assertEqual({"fullText": "\u4e2d\u6587"}, json.loads(path.read_text(encoding="utf-8")))
            self.assertEqual([], list(path.parent.glob(f".{path.name}.*.tmp")))

    def test_sanitizer_redacts_environment_secrets(self) -> None:
        previous = os.environ.get("DASHSCOPE_API_KEY")
        os.environ["DASHSCOPE_API_KEY"] = "secret-value"
        try:
            self.assertNotIn("secret-value", sidecar._sanitize_error("failure: secret-value"))
        finally:
            if previous is None:
                os.environ.pop("DASHSCOPE_API_KEY", None)
            else:
                os.environ["DASHSCOPE_API_KEY"] = previous

    def test_temporary_audio_is_scoped_to_output_job_directory(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            job_directory = Path(directory) / "job"
            output = job_directory / "qwen-result.json"
            with sidecar._temporary_audio_directory(str(output), "sdk") as temporary:
                temporary_path = Path(temporary)
                self.assertEqual(job_directory.resolve(), temporary_path.parent)
                self.assertTrue(temporary_path.name.startswith(".qwen-audio-sdk-"))
                self.assertTrue(temporary_path.is_dir())
            self.assertFalse(temporary_path.exists())


class DashScopeRetryTests(unittest.TestCase):
    @staticmethod
    def success_response() -> dict[str, object]:
        return {
            "status_code": 200,
            "output": {
                "choices": [
                    {
                        "message": {
                            "content": [{"text": "recognized"}],
                            "annotations": [{"language": "en"}],
                        }
                    }
                ]
            },
        }

    @staticmethod
    def fake_dashscope(call: mock.Mock) -> types.ModuleType:
        module = types.ModuleType("dashscope")
        module.MultiModalConversation = types.SimpleNamespace(call=call)
        return module

    def call_chunk(self, call: mock.Mock, attempts: int = 2) -> tuple[str, str]:
        with mock.patch.dict(sys.modules, {"dashscope": self.fake_dashscope(call)}):
            return sidecar._call_dashscope_chunk(
                Path("chunk.wav"), "qwen3-asr-flash", "", attempts, lambda text: text
            )

    def test_only_allowlisted_http_statuses_are_retried(self) -> None:
        for status in (408, 429, 500, 502, 503, 504):
            with self.subTest(status=status):
                call = mock.Mock(
                    side_effect=[
                        {"status_code": status, "code": "Transient", "message": "retry"},
                        self.success_response(),
                    ]
                )
                with mock.patch.object(sidecar.time, "sleep") as sleep, mock.patch.object(
                    sidecar.random, "uniform", return_value=0.0
                ):
                    self.assertEqual(("English", "recognized"), self.call_chunk(call))
                self.assertEqual(2, call.call_count)
                sleep.assert_called_once_with(0.75)

    def test_retry_after_header_overrides_backoff(self) -> None:
        call = mock.Mock(
            side_effect=[
                {
                    "status_code": 429,
                    "code": "Throttling",
                    "message": "retry later",
                    "headers": {"retry-after": "4"},
                },
                self.success_response(),
            ]
        )
        with mock.patch.object(sidecar.time, "sleep") as sleep, mock.patch.object(
            sidecar.random, "uniform"
        ) as jitter:
            self.assertEqual(("English", "recognized"), self.call_chunk(call))
        sleep.assert_called_once_with(4.0)
        jitter.assert_not_called()

    def test_retry_after_is_capped(self) -> None:
        error = sidecar.DashScopeResponseError(429, "Throttling", "wait", 86_400.0)
        self.assertEqual(
            sidecar._MAX_DASHSCOPE_RETRY_DELAY_SECONDS,
            sidecar._dashscope_retry_delay(1, error),
        )

    def test_call_sets_a_finite_sdk_request_timeout(self) -> None:
        call = mock.Mock(return_value=self.success_response())
        self.assertEqual(("English", "recognized"), self.call_chunk(call, attempts=1))
        self.assertEqual(
            sidecar._DASHSCOPE_REQUEST_TIMEOUT_SECONDS,
            call.call_args.kwargs["request_timeout"],
        )

    def test_pre_cancelled_chunk_does_not_call_sdk(self) -> None:
        call = mock.Mock(return_value=self.success_response())
        stop_event = threading.Event()
        stop_event.set()
        with mock.patch.dict(sys.modules, {"dashscope": self.fake_dashscope(call)}):
            with self.assertRaisesRegex(sidecar.UserFacingError, "已停止后续请求"):
                sidecar._call_dashscope_chunk(
                    Path("chunk.wav"),
                    "qwen3-asr-flash",
                    "",
                    2,
                    lambda text: text,
                    stop_event,
                )
        call.assert_not_called()

    def test_terminal_worker_failure_signals_retrying_peers_immediately(self) -> None:
        call = mock.Mock(
            return_value={"status_code": 401, "code": "InvalidApiKey", "message": "stop"}
        )
        stop_event = threading.Event()
        with mock.patch.dict(sys.modules, {"dashscope": self.fake_dashscope(call)}):
            with self.assertRaises(sidecar.UserFacingError):
                sidecar._call_dashscope_chunk(
                    Path("chunk.wav"),
                    "qwen3-asr-flash",
                    "",
                    3,
                    lambda text: text,
                    stop_event,
                )
        self.assertTrue(stop_event.is_set())
        self.assertEqual(1, call.call_count)

    def test_data_inspection_and_other_client_errors_fail_fast(self) -> None:
        cases = (
            (429, "DataInspectionFailed"),
            (400, "InvalidParameter"),
            (401, "InvalidApiKey"),
            (403, "AccessDenied"),
            (404, "NotFound"),
            (409, "Conflict"),
            (422, "UnprocessableEntity"),
            (501, "NotImplemented"),
            (505, "HttpVersionNotSupported"),
        )
        for status, code in cases:
            with self.subTest(status=status, code=code):
                call = mock.Mock(
                    return_value={"status_code": status, "code": code, "message": "stop"}
                )
                with mock.patch.object(sidecar.time, "sleep") as sleep:
                    with self.assertRaises(sidecar.UserFacingError):
                        self.call_chunk(call, attempts=5)
                self.assertEqual(1, call.call_count)
                sleep.assert_not_called()

    def test_only_explicit_transport_exceptions_are_retried(self) -> None:
        class RequestTimeout(Exception):
            pass

        class RequestConnectionError(Exception):
            pass

        class RequestChunkedEncodingError(Exception):
            pass

        requests_module = types.ModuleType("requests")
        requests_module.exceptions = types.SimpleNamespace(
            Timeout=RequestTimeout,
            ConnectionError=RequestConnectionError,
            ChunkedEncodingError=RequestChunkedEncodingError,
        )

        errors = (
            TimeoutError("built-in timeout"),
            ConnectionError("built-in connection"),
            RequestTimeout("requests timeout"),
            RequestConnectionError("requests connection"),
            RequestChunkedEncodingError("truncated body"),
        )
        with mock.patch.dict(sys.modules, {"requests": requests_module}):
            for error in errors:
                with self.subTest(error_type=type(error).__name__):
                    call = mock.Mock(side_effect=[error, self.success_response()])
                    with mock.patch.object(sidecar.time, "sleep"), mock.patch.object(
                        sidecar.random, "uniform", return_value=0.0
                    ):
                        self.assertEqual(("English", "recognized"), self.call_chunk(call))
                    self.assertEqual(2, call.call_count)

    def test_protocol_parse_failure_is_not_retried(self) -> None:
        call = mock.Mock(return_value={"status_code": 200, "output": {"choices": []}})
        with mock.patch.object(sidecar.time, "sleep") as sleep:
            with self.assertRaisesRegex(sidecar.UserFacingError, "\u65e0\u6cd5\u89e3\u6790"):
                self.call_chunk(call, attempts=5)
        self.assertEqual(1, call.call_count)
        sleep.assert_not_called()

    def test_unknown_exception_fails_fast_without_leaking_key(self) -> None:
        secret = "sdk-secret-that-must-not-leak"
        call = mock.Mock(side_effect=RuntimeError(f"unexpected {secret}"))
        with mock.patch.dict(os.environ, {"DASHSCOPE_API_KEY": secret}), mock.patch.object(
            sidecar.time, "sleep"
        ) as sleep:
            with self.assertRaises(sidecar.UserFacingError) as raised:
                self.call_chunk(call, attempts=5)
        self.assertNotIn(secret, str(raised.exception))
        self.assertEqual(1, call.call_count)
        sleep.assert_not_called()


class DashScopeSchedulingTests(unittest.TestCase):
    def test_first_failure_stops_before_more_segments_are_submitted(self) -> None:
        started: list[int] = []
        started_lock = threading.Lock()
        initial_workers_started = threading.Event()

        def fake_call(
            path: Path,
            model_name: str,
            context: str,
            attempts: int,
            post_process: object,
            stop_event: threading.Event,
        ) -> tuple[str, str]:
            del model_name, context, attempts, post_process
            index = int(path.stem)
            with started_lock:
                started.append(index)
                if len(started) == 2:
                    initial_workers_started.set()
            if index == 0:
                if not initial_workers_started.wait(2):
                    raise AssertionError("second worker did not start")
                raise sidecar.UserFacingError("first segment failed")
            if not stop_event.wait(2):
                raise AssertionError("running peer did not receive stop signal")
            raise sidecar.UserFacingError("peer cancelled")

        paths = [Path(f"{index}.wav") for index in range(10)]
        with mock.patch.object(sidecar, "_call_dashscope_chunk", side_effect=fake_call):
            with self.assertRaisesRegex(sidecar.UserFacingError, "first segment failed"):
                sidecar._transcribe_sdk_paths(paths, "model", "", 3, str, 2)

        self.assertCountEqual([0, 1], started)

    def test_peer_stop_cannot_hide_originating_failure_or_refill_window(self) -> None:
        started: list[int] = []
        started_lock = threading.Lock()
        initial_workers_started = threading.Event()
        terminal_signalled = threading.Event()
        scheduler_processed_success = threading.Event()

        def fake_call(
            path: Path,
            model_name: str,
            context: str,
            attempts: int,
            post_process: object,
            stop_event: threading.Event,
        ) -> tuple[str, str]:
            del model_name, context, attempts, post_process
            index = int(path.stem)
            with started_lock:
                started.append(index)
                if len(started) == 3:
                    initial_workers_started.set()
            if not initial_workers_started.wait(2):
                raise AssertionError("initial workers did not all start")
            if index == 0:
                stop_event.set()
                terminal_signalled.set()
                if not scheduler_processed_success.wait(2):
                    raise AssertionError("scheduler did not process the successful peer")
                raise sidecar.UserFacingError("InvalidApiKey root cause")
            if not terminal_signalled.wait(2):
                raise AssertionError("terminal worker did not signal failure")
            if index == 1:
                return "English", "recognized"
            raise sidecar.DashScopePeerStoppedError("peer cancelled")

        paths = [Path(f"{index}.wav") for index in range(10)]
        with mock.patch.object(sidecar, "_call_dashscope_chunk", side_effect=fake_call), mock.patch.object(
            sidecar,
            "_progress",
            side_effect=lambda *args: scheduler_processed_success.set(),
        ):
            with self.assertRaisesRegex(sidecar.UserFacingError, "InvalidApiKey root cause"):
                sidecar._transcribe_sdk_paths(paths, "model", "", 3, str, 3)

        self.assertCountEqual([0, 1, 2], started)


class ArgumentTests(unittest.TestCase):
    def parse(self, *extra: str) -> argparse.Namespace:
        return sidecar._build_parser().parse_args(
            ["--mode", "sdk", "--audio", "input.wav", "--output", "output.json", "--language", "auto", *extra]
        )

    def test_sdk_defaults_to_fine_vad_timeline(self) -> None:
        args = self.parse()
        sidecar._validate_arguments(args)
        self.assertEqual(10, args.sdk_segment_seconds)
        self.assertEqual(2, args.sdk_workers)

    def test_parser_accepts_every_ui_language(self) -> None:
        for language in ("zh", "en", "yue", "ja", "ko", "fr", "de", "es", "pt", "ru", "it"):
            with self.subTest(language=language):
                args = sidecar._build_parser().parse_args(
                    [
                        "--mode",
                        "sdk",
                        "--audio",
                        "input.wav",
                        "--output",
                        "output.json",
                        "--language",
                        language,
                    ]
                )
                self.assertEqual(language, args.language)

    def test_explicit_zero_is_rejected_instead_of_defaulted(self) -> None:
        args = self.parse("--sdk-workers", "0")
        with self.assertRaisesRegex(sidecar.UserFacingError, "1\u20138"):
            sidecar._validate_arguments(args)

    def test_mode_specific_parameters_are_rejected(self) -> None:
        args = self.parse("--device", "cpu")
        with self.assertRaisesRegex(sidecar.UserFacingError, "sdk \u6a21\u5f0f"):
            sidecar._validate_arguments(args)

    def test_sdk_accepts_speaker_timeline(self) -> None:
        args = self.parse("--speaker-timeline", "moss.json")
        sidecar._validate_arguments(args)
        self.assertEqual("moss.json", args.speaker_timeline)

    def test_local_rejects_speaker_timeline(self) -> None:
        args = sidecar._build_parser().parse_args(
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
                "--speaker-timeline",
                "moss.json",
            ]
        )
        with self.assertRaisesRegex(sidecar.UserFacingError, "speaker-timeline"):
            sidecar._validate_arguments(args)


if __name__ == "__main__":
    unittest.main()
