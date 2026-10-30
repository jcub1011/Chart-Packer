# Validate Metadata

Checks the selected songs' `song.ini` metadata for values games may misread (such as a word where a number belongs), and lists problems found in the charts. It offers quick fixes.

**Open it from:** Chart Packer, **Tools**, then **Validate…**, or right-click selected songs in the library. If every song passes, a [message](Message-Dialog.md) says so and this window doesn't open.

## What's In It

- A list of the songs that fail, with a search box and filters (type of fix, issue type, metadata key).
- **Select all quick fixes**, and ticks per song and per issue, then **Fix selected**.
- The full song inspector on the right, to correct things by hand.
- **Save all changes** and **Close**. The inspector also has **Save** and **Discard** for the song being edited.

## Good to Know

- Fixes edit drafts only. Nothing is saved until you save.
- Saving songs that still fail asks you to confirm.

While the check runs, a [progress dialog](Validator-Progress.md) may appear.

[Back to Home](Home.md)
