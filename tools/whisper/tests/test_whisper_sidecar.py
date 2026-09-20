from __future__ import annotations

import importlib.util
import sys
import tempfile
import unittest
import wave
from pathlib import Path


SIDECAR_PATH = Path(__file__).resolve().parents[1] / "whisper_sidecar.py"
SPEC = importlib.util.spec_from_file_location("mediascribe_whisper_sidecar", SIDECAR_PATH)
assert SPEC is not None and SPEC.loader is not None
sidecar = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = sidecar
SPEC.loader.exec_module(sidecar)


def write_wav(
    path: Path,
    *,
    seconds: float = 0.2,
    sample_rate: int = 16_000,
    channels: int = 1,
    sample_width: int = 2,
) -> None:
    frames = max(1, round(seconds * sample_rate))
    with wave.open(str(path), "wb") as stream:
        stream.setnchannels(channels)
        stream.setsampwidth(sample_width)
        stream.setframerate(sample_rate)
        stream.writeframes(b"\0" * frames * channels * sample_width)


class WhisperSidecarTests(unittest.TestCase):
    def test_accepts_normalized_pcm_wav(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "audio.wav"
            write_wav(path, seconds=0.25)

            self.assertEqual(250, sidecar._validate_wav(path))

    def test_rejects_unexpected_wav_format(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "audio.wav"
            write_wav(path, sample_rate=8_000)

            with self.assertRaisesRegex(sidecar.UserFacingError, "16 kHz"):
                sidecar._validate_wav(path)

    def test_requires_a_complete_local_model(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            model = Path(directory)
            (model / "model.bin").write_bytes(b"weights")
            (model / "config.json").write_text("{}", encoding="utf-8")

            with self.assertRaisesRegex(sidecar.UserFacingError, "tokenizer.json"):
                sidecar._validate_model(model)

            (model / "tokenizer.json").write_text("{}", encoding="utf-8")
            sidecar._validate_model(model)

    def test_cuda_and_cpu_contracts_cannot_be_mixed(self) -> None:
        cuda = sidecar._build_parser().parse_args(
            [
                "--audio", "audio.wav", "--output", "result.json", "--model-dir", "model",
                "--language", "zh", "--device", "cuda", "--compute-type", "int8", "--batch-size", "1",
            ]
        )
        with self.assertRaisesRegex(sidecar.UserFacingError, "int8_float16"):
            sidecar._validate_arguments(cuda)

        cpu = sidecar._build_parser().parse_args(
            [
                "--audio", "audio.wav", "--output", "result.json", "--model-dir", "model",
                "--language", "en", "--device", "cpu", "--compute-type", "int8_float16", "--batch-size", "1",
            ]
        )
        with self.assertRaisesRegex(sidecar.UserFacingError, "CPU"):
            sidecar._validate_arguments(cpu)

    def test_text_coalescing_preserves_chinese_and_separates_english_words(self) -> None:
        self.assertEqual("hello world你好", sidecar._coalesce_text([" hello", "world ", "你", "好"]))


if __name__ == "__main__":
    unittest.main()
