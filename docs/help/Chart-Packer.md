# Chart Packer (main window)

The main window of Chart Packer. You point it at a folder of songs, and it lists the `.sng` files and song folders it finds. From there you pack, unpack, edit metadata, play songs, and run tools across many songs at once.

## Parts of the window

- **Title bar:** the menus, the songs folder and output folder (click either to change it), and the version, which opens the releases page.
- **Library** (left): a searchable, sortable, filterable list of songs. Select one or more.
- **Song details** (right): the selected song's album art, background, playback, tracks, chart, files and metadata. Edits are drafts until you save.
- **Status bar** (bottom): scan and job progress. Click it to open [Job Progress](Job-Progress.md).

## Menus

- **File:** choose the songs folder, rescan (F5), choose the output folder, open the logs folder, and [Settings](Settings.md).
- **Edit:** undo, redo, and [Keyboard Shortcuts](Keyboard-Shortcuts.md).
- **Pack:** pack and unpack the selected songs, choose what happens if the output already exists (skip, overwrite, keep both), and pack into one flat folder.
- **Tools:** Edit Chart (opens the song in Chart Builder), [Merge](Merge.md), [Validate](Validator.md), [Generate Lower Difficulties](Lower-Difficulties.md) and [Deduplicate](Dedup.md).

## Good to know

- Dropping a folder anywhere on the window makes it the songs folder and scans it.
- Closing with unsaved metadata edits asks what to do with them.
- Edit Chart needs Chart Builder installed.

*Details and screenshots are coming.*

[Back to Home](Home.md)
