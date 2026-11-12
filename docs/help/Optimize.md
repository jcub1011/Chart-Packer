# Optimize Library

Makes charts smaller. It recompresses audio, video and images, optionally mixes stems down to one, and leaves out metadata no game needs. The window lists every chart with what optimizing it would save, so you can see the result before anything is changed.

**Open it from:** Chart Packer, **Tools**, then **Optimize Library…**, or right-click selected songs in the library. With no songs selected it works on the whole library.

## Back Up First

Under **Write to**, the window starts on **A separate folder**. Your library is left as it is, and only the charts that change are written to the folder, in the same layout. The folder is not a full copy of the library. Review the results, then copy the charts you want back over the originals.

**Over the originals** rewrites the charts where they are, and **that can't be undone**. Back up your charts before you use it. Chart Packer asks you to confirm first.

## What's In It

On the left, what to do:

- **Charts:** the selected songs or the whole library.
- **Shrink:** each way of making charts smaller, with what it saves across the charts beside it. Its settings appear once it is switched on.
  - **Compress audio to Opus:** each stem at the bitrate you pick, 128 kbps by default or the 80 kbps "smaller" preset. Stems that are already Opus are left alone, including Opus in an `.ogg` file.
  - **Mix stems down to one:** replaces a chart's stems with a single song stem. You can no longer hear the stems apart.
  - **Recompress background video:** re-encodes as VP8 in WebM, fitted inside the size you pick (480p, 720p, Full HD, 1440p or Ultra HD) and never scaled up, at the CQ you choose (lower is better quality and a larger file). The result has no audio track.
    - **Convert unsupported codecs for compatibility** also converts VP9, AV1 and HEVC videos, which some games can't play. This is for compatibility, not size, so it is done whatever it saves, and the result may be larger. It is off by default.
  - **Recompress album art** and **background images:** as JPEG at the quality you pick, fitted inside the size limit and never scaled up.
  - **Clean up metadata:** drops `song.ini` keys and chart events no game needs. It is done whatever it saves.
- **Skip small savings:** a file that would shrink by less than this is left as it is. Metadata cleanup ignores it.
- **Write to:** a separate folder, or over the originals.
- **Reset** puts every setting back to the shipped one. The window remembers its settings and the output folder by itself.

On the right, what it comes to:

- **The summary:** the charts' size before and after, the saving, and a bar of the same. Under it, how many charts have been measured.
- **The chart list:** every chart with its size, a bar, and what it saves. Search it, sort it by saving, size, name or problems first, and filter it with the chips.
- **Optimize**, **Stop** and **Close**. The Optimize button says how many charts it will change.

## How the Savings Are Worked Out

Nothing is changed until you press Optimize.

1. The window reads each chart's files.
2. It measures a few files of each type first: Vorbis audio, MP3 audio, each video codec, PNG and JPEG images, and so on. A type with only a few files has all of them measured. From these, every other chart gets a predicted saving at once. A predicted figure starts with **~**.
3. It then measures the rest of the charts in the background. As each is measured, its own figure replaces the prediction. Select a chart to have it measured next.

A measured figure is exactly what a run will do, with every rule applied: a file under the margin, one that would get larger, and one that is already small enough all count as saving nothing. Select a chart to see each of its files and what happens to it. A chart nothing applies to says **Nothing done** under its name.

## Comparing Before and After

Select a chart whose images or video would be recompressed, and its breakdown shows each one as it is beside as it would be:

- **Album art and background images** are shown whole, with the size of the file and of the picture under each.
- **The background video** is shown a frame at a time. The arrows step through five frames spread across the video; each is made when you first step to it, which takes a moment.

What you see is made by the same encoders with the same settings as a run, so change a setting and select the chart again to compare. Nothing is written to the chart. The pictures aren't shown while a run is going.

Measuring uses most of the processor. **Pause measuring** holds it, and keeps what has been measured. You don't have to wait for it: Optimize works at any point, and measures the charts it hasn't reached as it gets to them.

What has been measured is remembered, so opening the window again on the same charts is quick, and changing one setting only measures again what that setting affects.

## After a Run

Each chart shows what it saved, with the estimate beside it. Select a chart to see every file's size before and after. The summary counts the charts optimized, left alone and failed. **Open output folder** shows the folder the changed charts were written to.

## Good to Know

- A way of shrinking whose backend is missing is shown disabled with the reason. Video needs a build of FFmpeg with the libvpx encoder.
- Charts with unsaved changes, charts another app has open, and charts that changed since they were read are left alone. The list says why, and the **Problems** chip shows only those.
- **Stop** cancels the run. Charts already finished keep their changes; the one being worked on is left as it was.
- Videos take far longer than anything else, to measure and to optimize. A video is measured from two long stretches of it (about 17 seconds each at 30 frames a second), not the whole of it; a video shorter than that is measured whole.
- A video's estimate is the least sure of the figures: expect the file a run writes to be within about 15% of it. A video the estimate says would save too little is left alone.
- When Optimize is pressed before every chart is measured, the run measures the rest as it reaches them, and the footer says so while it does.

[Back to Home](Home.md)
