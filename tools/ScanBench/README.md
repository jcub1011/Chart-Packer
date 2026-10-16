# ScanBench

Times the library scan on a real library. Never shipped; files are only read.

```
dotnet run -c Release --project tools/ScanBench -- "<library>" --parallelism 1,4,16,64
dotnet run -c Release --project tools/ScanBench -- "<library>" --phases [--warm]
dotnet run -c Release --project tools/ScanBench -- "<library>" --charts
dotnet run -c Release --project tools/ScanBench -- "<library>" --validate
```

- **Default** runs `SongLibraryLoader.LoadAsync` once per parallelism, as the app's scan does, and prints the time
  and songs per second.
- **`--phases`** reads the songs one at a time on one thread and splits the time between the steps of a song's read:
  search, `.sng` index, chart read, chart parse, `song.ini`, audio length. It also prints what ATL reads to measure each
  stem for songs without a `song_length` (reads, KiB, jumps, how far into the stem). `--warm` reads everything once,
  untimed, first, so the times leave out the JIT and the disk.
- **`--charts`** times reading each song's chart for the library list, from memory, and lists the slowest.
- **`--validate`** times the Validator's check of every song, warm and without the lint cache: end to end on 1, 2, 4, 8
  and one-fewer-than-cores workers, with GC pauses and counts, then one song at a time on one thread, split between
  the steps of a song's check (metadata, listing, chart read, chart parse, audio length, lint rules) with what each allocates.
- `--depth N` sets the scan depth (default 2, as the app's).

## Cold runs

Only the first run after the file cache is emptied reads from the disk; later runs read from memory. To empty it
without rebooting, run Sysinternals RAMMap from an elevated prompt before each run:

```
RAMMap64.exe -Et
```

For a hard drive, compare parallelisms on cold runs only: warm runs measure the CPU, not the disk.
