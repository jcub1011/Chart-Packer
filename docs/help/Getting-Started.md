# Getting Started

## Install

1. Download `ChartPacker-<version>-win-x64-setup.exe` (or the portable zip) from the [releases page](https://github.com/jcub1011/Chart-Packer/releases).
2. Run the installer. It installs for the current user without needing administrator rights, unless you choose all users.
3. Choose **Chart Packer and Chart Builder** (the default), or **Custom** to leave out Chart Builder.

FFmpeg is included, so there is nothing else to install.

Releases aren't code-signed, so Windows SmartScreen shows "Windows protected your PC" the first time. Choose **More info**, then **Run anyway**.

The installer also makes `.sng` files open in Chart Builder, shows an `.sng`'s album art as its Explorer thumbnail, and adds **Open with Chart Builder** to folders (when Chart Builder is installed).

## Pack and unpack songs with Chart Packer

1. Open Chart Packer and choose **File**, then **Choose Songs Folder…** (or drag a folder onto the window). It scans for `.sng` files and song folders.
2. Select songs in the library list. The right side shows each song's details.
3. Use the **Pack** menu to pack song folders into `.sng` files, or to unpack `.sng` files into folders.
4. Click the status bar at the bottom to see the results in [Job Progress](Job-Progress.md).

See [Chart Packer](Chart-Packer.md) for the full tour.

## Make a chart with Chart Builder

1. Open Chart Builder. It starts with an empty "Untitled Chart".
2. Drop audio files onto the window to start a chart from them, or use **File**, then **Open…** to edit an existing song.
3. Add tracks and fill in the metadata in the Chart Settings tab, then chart notes in the lanes.
4. **File**, then **Save** (Ctrl+S) saves the chart. A new chart asks where to put its folder ([First Save](First-Save.md)).
5. **File**, then **Pack…** exports a `.sng` ([Export Song](Export-SNG.md)).

See [Chart Builder](Chart-Builder.md) for the full tour.

## Getting help

Every window has a **?** button in its title bar. Click it, or press **F1**, to open that window's page here.

[Back to Home](Home.md)
