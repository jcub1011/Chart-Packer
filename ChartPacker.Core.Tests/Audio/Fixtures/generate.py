"""Regenerates the audio test fixtures in this folder; see README.md.

Needs Python 3 with the soundfile (libsndfile >= 1.2) and numpy packages:
    python -m pip install soundfile numpy
    python generate.py
"""
import os
import numpy as np
import soundfile as sf

HERE = os.path.dirname(os.path.abspath(__file__))
SECONDS = 1.0
LOW, HIGH = 200.0, 2000.0


def chirp(rate, rising):
    """A half-amplitude sweep between LOW and HIGH Hz, up or down, over SECONDS."""
    t = np.arange(int(rate * SECONDS)) / rate
    k = (HIGH - LOW) / SECONDS
    phase = LOW * t + k * t * t / 2 if rising else HIGH * t - k * t * t / 2
    return 0.5 * np.sin(2 * np.pi * phase)


def write(name, rate, channels, fmt, subtype):
    left = chirp(rate, rising=True)
    data = left if channels == 1 else np.stack([left, chirp(rate, rising=False)], axis=1)
    sf.write(os.path.join(HERE, name), data.astype(np.float32), rate, format=fmt, subtype=subtype)


write("chirp-stereo-44100.ogg", 44100, 2, "OGG", "VORBIS")
write("chirp-stereo-48000.opus", 48000, 2, "OGG", "OPUS")
write("chirp-mono-48000.opus", 48000, 1, "OGG", "OPUS")
write("chirp-stereo-44100.mp3", 44100, 2, "MP3", "MPEG_LAYER_III")
