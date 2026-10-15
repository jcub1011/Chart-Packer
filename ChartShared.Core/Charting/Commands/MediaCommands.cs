namespace ChartShared.Core.Charting.Commands;

/// <summary>
/// Puts a file in one of the open song's <see cref="MediaSlot"/>s, or empties it, as one undo step (charter-spec C1, Art
/// and Tracks): a pending change until the song is saved. The chart itself isn't touched.
/// </summary>
/// <remarks>
/// It keeps what the slot held as the next save would leave it, before and after, so undoing it still gives back the
/// song's own file once the song has been saved since: from the copy the session kept of it.
/// </remarks>
public sealed class MediaCommand : ChartCommand
{
    private readonly EditorSong _song;
    private readonly MediaRef _after;
    private MediaRef? _before;

    /// <param name="song">The song, whose chart the command is applied to.</param>
    /// <param name="slot">The slot to change.</param>
    /// <param name="after">What it holds afterwards.</param>
    public MediaCommand(EditorSong song, MediaSlot slot, MediaRef after)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(after);
        _song = song;
        Slot = slot;
        _after = after;
    }

    /// <summary>The slot it changes.</summary>
    public MediaSlot Slot { get; }

    private protected override ChartChange Do(EditableChart chart)
    {
        RequireSongChart(chart);
        _before ??= _song.EffectiveMedia(Slot);
        _song.SetEffectiveMedia(Slot, _after);
        return ChartChange.Of(SongChanges.Media);
    }

    private protected override void Undo(EditableChart chart)
    {
        RequireSongChart(chart);
        _song.SetEffectiveMedia(Slot, _before!);
    }

    private void RequireSongChart(EditableChart chart)
    {
        if (!ReferenceEquals(chart, _song.Chart))
            throw new InvalidOperationException("The command belongs to another song.");
    }
}

/// <summary>
/// The undoable file edits the Art tab and Chart settings' Tracks make (charter-spec C1): each a
/// <see cref="MediaCommand"/>, or null when it would change nothing.
/// </summary>
public static class MediaCommands
{
    /// <summary>Puts the file at <paramref name="sourcePath"/> in <paramref name="slot"/>, in place of what it holds.</summary>
    /// <exception cref="ArgumentException">The file can't be stored in the slot, being of the wrong kind.</exception>
    public static MediaCommand? Set(EditorSong song, MediaSlot slot, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentException.ThrowIfNullOrEmpty(sourcePath);
        if (slot.StoredNameFor(sourcePath) is null)
            throw new ArgumentException($"'{sourcePath}' can't be stored as {slot}.", nameof(sourcePath));
        return Make(song, slot, MediaRef.Source(sourcePath));
    }

    /// <summary>Empties <paramref name="slot"/>.</summary>
    public static MediaCommand? Remove(EditorSong song, MediaSlot slot) => Make(song, slot, MediaRef.None);

    /// <summary>Puts <paramref name="slot"/> back as the song has it saved, taking back every change to it.</summary>
    public static MediaCommand? RevertToSaved(EditorSong song, MediaSlot slot)
    {
        ArgumentNullException.ThrowIfNull(song);
        return song.Media.ContainsKey(slot) ? new MediaCommand(song, slot, MediaRef.Stored(song.SaveGeneration)) : null;
    }

    private static MediaCommand? Make(EditorSong song, MediaSlot slot, MediaRef after)
    {
        ArgumentNullException.ThrowIfNull(song);
        return song.EffectiveMedia(slot) == after ? null : new MediaCommand(song, slot, after);
    }
}
