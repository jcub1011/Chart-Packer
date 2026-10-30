# Getting Started

## Install

1. Download `ChartPacker-<version>-win-x64-setup.exe` (or the portable zip) from the [releases page](https://github.com/jcub1011/Chart-Packer/releases).
2. Run the installer.

All FFmpeg dependencies come bundled.

Releases aren't code-signed, so Windows SmartScreen shows "Windows protected your PC" the first time. Make sure you download the installer from the GitHub releases page and not from other sources.

When Chart Builder is installed, it becomes the default program for `.sng` files and adds an **Open with Chart Builder** option to the right-click menu of folders.

## Pack and Unpack Songs With Chart Packer

1. Open Chart Packer, choose **File**, then **Choose Songs Folder…**. Press F5 to scan if auto-scan is disabled.
2. Select songs in the library list. The right side shows each song's details.
3. Use the **Pack** menu to pack song folders into `.sng` files, or to unpack `.sng` files into folders.
4. Click the status bar at the bottom to see the results in [Job Progress](Job-Progress.md).

See [Chart Packer](Chart-Packer.md) for more information.

## Make a Chart With Chart Builder

1. Open Chart Builder. It starts with an empty "Untitled Chart".
2. Drop audio files onto the window to start a chart from them (each file name must say which stem it is, such as `song` or `guitar`), or use **File**, then **Open…** to edit an existing song.
3. Add tracks and fill in the metadata in the Chart Settings tab, then chart notes in the lanes.
4. **File**, then **Save** (Ctrl+S) saves the chart. A new chart asks where to put its folder ([First Save](First-Save.md)).
5. **File**, then **Pack…** exports a `.sng` ([Export Song](Export-SNG.md)).

See [Chart Builder](Chart-Builder.md) for more information.

## Getting Help

Every window has a **?** button in its title bar. Click it, or press **F1**, to open that window's page here.

[Back to Home](Home.md)
