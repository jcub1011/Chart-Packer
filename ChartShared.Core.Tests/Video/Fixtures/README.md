# Video fixtures

| File | What it is |
|---|---|
| `tiny.mp4` | Two seconds of a 64×36 test pattern at 10 fps, MPEG-4, no audio |
| `tiny.webm` | The same picture as VP8, no audio |
| `tiny-audio.mkv` | `tiny.webm`'s picture with the one-second `chirp-stereo-44100.mp3` audio, Matroska |
| `tiny-audio.mp4` | `tiny.mp4`'s picture with that audio re-encoded as AAC, MP4 |

`tiny.mp4` and `tiny.webm` are used by the video decoder tests. The two audio-bearing fixtures cover reading a
video's audio and stripping it before packing; they carry the generated chirp, not anyone's recording.

With FFmpeg 9 on the path, they can be made again from this folder and `../../Audio/Fixtures`:

```sh
ffmpeg -y -i tiny.webm -i ../../Audio/Fixtures/chirp-stereo-44100.mp3 \
  -map 0:v:0 -map 1:a:0 -c copy -shortest tiny-audio.mkv
ffmpeg -y -i tiny.mp4 -i ../../Audio/Fixtures/chirp-stereo-44100.mp3 \
  -map 0:v:0 -map 1:a:0 -c:v copy -c:a aac -shortest tiny-audio.mp4
```
