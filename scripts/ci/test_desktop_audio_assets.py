import struct
import unittest
import wave
from pathlib import Path


ASSET_DIRECTORY = (
    Path(__file__).resolve().parents[2]
    / "src"
    / "Watcher.EdgeAgent.Desktop"
    / "Assets"
)


class DesktopAudioAssetTests(unittest.TestCase):
    def test_bundled_test_sounds_are_short_quiet_mono_pcm(self):
        for file_name in ("audio-test-tone.wav", "audio-test-chime.wav"):
            with self.subTest(file_name=file_name):
                path = ASSET_DIRECTORY / file_name
                with wave.open(str(path), "rb") as audio:
                    self.assertEqual(audio.getnchannels(), 1)
                    self.assertEqual(audio.getsampwidth(), 2)
                    self.assertEqual(audio.getframerate(), 24_000)
                    self.assertLessEqual(audio.getnframes() / audio.getframerate(), 1)

                    samples = struct.unpack(
                        f"<{audio.getnframes()}h", audio.readframes(audio.getnframes())
                    )
                    self.assertTrue(samples)
                    self.assertLessEqual(max(abs(sample) for sample in samples), 4_000)


if __name__ == "__main__":
    unittest.main()
