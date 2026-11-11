# Optimize Library

Makes charts smaller. It recompresses audio, video and images, optionally mixes stems down to one, and leaves out metadata no game needs. You choose what to do, estimate the savings first, then run it on the selected songs or on the whole library.

**Open it from:** Chart Packer, **Tools**, then **Optimize Library…**, or right-click selected songs in the library. With no songs selected it works on the whole library, which is scanned again first.

## Back Up First

Optimizing in place rewrites the charts where they are, and **that can't be undone**. Before you run it, either:

- **back up your charts**, or
- tick **Write changed charts to a folder**. Your library is left as it is, and only the charts that change are written there, in the same layout. The folder is not a full copy of the library. Review the results, then copy the charts you want back over the originals.

Chart Packer asks you to confirm before it changes charts in place.

## What's In It

- **Charts:** the selected songs or the whole library.
- **Clean up metadata:** drops `song.ini` keys and chart events no game needs. It is done whatever it saves.
- **Compress audio to Opus:** each stem at the bitrate you pick, 128 kbps by default or the 80 kbps "smaller" preset. Stems that are already Opus are left alone.
- **Mix stems down to one:** replaces a chart's stems with a single song stem. You can no longer hear the stems apart.
- **Recompress background video:** re-encodes as VP8 in WebM at Full HD or Ultra HD, at the CQ you choose (lower is better quality and a larger file). The result has no audio track.
  - **Convert unsupported codecs for compatibility** also converts VP9, AV1 and HEVC videos, which some games can't play. This is for compatibility, not size, so it is done whatever it saves, and the result may be larger. It is off by default.
- **Recompress album art** and **background images:** as JPEG at the quality you pick, fitted inside the size limit and never scaled up.
- **Savings margin:** a file that would shrink by less than this is left alone. Metadata cleanup ignores it.
- **Estimate from:** one chart (video and audio only), a sample of charts, or all charts. A sample is the same every time. Fewer charts is faster; more is more accurate.
- **Estimate, Optimize, Stop,** and **Close**.
- **Reset** puts every setting back to the shipped one. **Save as defaults** keeps the settings as the ones the window starts from next time.

## The Estimate

**Estimate** doesn't change anything. For each strategy that is on, it lists the savings by codec: how many files were estimated, what the codec saves across the library, and what it saves per chart. A codec that would grow shows as a cost. Compatibility conversions are marked, and what they cost is shown separately.

## Good to Know

- A strategy whose backend is missing is shown disabled with the reason. Video needs a build of FFmpeg with the libvpx encoder.
- Charts with unsaved changes, charts another app has open, and charts that changed since they were read are skipped. The results list says why.
- **Stop** cancels the run. Charts already finished keep their changes; the one being worked on is left as it was.
- Videos take far longer than anything else. A chart with nothing to do is not listed in the results.

[Back to Home](Home.md)
