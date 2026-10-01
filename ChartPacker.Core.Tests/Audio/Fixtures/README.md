# Audio fixtures

Each file is one second of a sine tone at half amplitude, 440 Hz on the left channel and 660 Hz
on the right (440 Hz for mono), written by `generate.py` with libsndfile 1.2.2:

| File | Format |
|---|---|
| `sine-stereo-44100.ogg` | Ogg Vorbis, stereo, 44.1 kHz |
| `sine-stereo-48000.opus` | Ogg Opus, stereo, 48 kHz |
| `sine-mono-48000.opus` | Ogg Opus, mono, 48 kHz |
| `sine-stereo-44100.mp3` | MPEG-1 Layer III, stereo, 44.1 kHz |

The tone is generated, so the files carry no one's recording. To regenerate them:

```
python -m pip install soundfile numpy
python generate.py
```

WAV tests build their files in code instead.
