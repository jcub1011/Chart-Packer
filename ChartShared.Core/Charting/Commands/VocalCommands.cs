using ChartShared.Core.Charting.Layout;

namespace ChartShared.Core.Charting.Commands;

/// <summary>
/// Changes a vocal part's notes, lyrics or phrases (charter-spec C3 item 7): its track is replaced by
/// <paramref name="to"/> written out, and put back as it was when the change is undone.
/// </summary>
/// <param name="to">
/// The part as it's to be, worked out from the track the chart has now, its <see cref="VocalTrackContent.Source"/>.
/// </param>
public sealed class ChangeVocals(VocalTrack track, VocalTrackContent to) : ChartCommand
{
    private PassthroughTrack? _written;
    private PassthroughTrack? _previous;
    private int _index;

    public VocalTrack Track { get; } = track;

    public VocalTrackContent To { get; } = to ?? throw new ArgumentNullException(nameof(to));

    private protected override ChartChange Do(EditableChart chart)
    {
        int index = VocalTracks.IndexIn(chart, Track);
        if (index < 0)
            throw new InvalidOperationException($"The chart has no {VocalTracks.NameOf(Track)} track.");
        if (!ReferenceEquals(chart.Passthrough[index], To.Source))
            throw new InvalidOperationException($"The {VocalTracks.NameOf(Track)} track has changed since the edit was worked out.");
        if (!To.IsChanged)
            throw new ArgumentException("There's nothing to change.", nameof(to));

        _written ??= To.Write();
        _index = index;
        _previous = chart.SetPassthrough(index, _written);
        return ChartChange.Of(SongChanges.Vocals);
    }

    private protected override void Undo(EditableChart chart)
    {
        chart.SetPassthrough(_index, _previous!);
        _previous = null;
    }
}

/// <summary>
/// Adds a vocal part the chart hasn't (charter-spec C3 item 7): an empty track of its name, after every other track.
/// </summary>
public sealed class AddVocalPart(VocalTrack track) : ChartCommand
{
    private PassthroughTrack? _added;

    public VocalTrack Track { get; } = track;

    private protected override ChartChange Do(EditableChart chart)
    {
        if (VocalTracks.IndexIn(chart, Track) >= 0)
            throw new InvalidOperationException($"The chart already has a {VocalTracks.NameOf(Track)} track.");
        _added ??= VocalTrackContent.Empty(VocalTracks.NameOf(Track)).Write();
        chart.AddPassthrough(_added);
        return ChartChange.Of(SongChanges.Vocals);
    }

    private protected override void Undo(EditableChart chart) => chart.RemovePassthroughAt(chart.Passthrough.Count - 1);
}
