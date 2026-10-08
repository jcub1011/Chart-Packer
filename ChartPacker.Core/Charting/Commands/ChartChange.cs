using System.Globalization;
using System.Text;
using ChartPacker.Core.Charts;
using ChartPacker.Core.Validation;

namespace ChartPacker.Core.Charting.Commands;

/// <summary>What a change touched that isn't in one part: the song as a whole.</summary>
[Flags]
public enum SongChanges
{
    None = 0,

    /// <summary>
    /// The tempo map: a tempo or time-signature marker or an anchor, which moves every tick in time, so everything that
    /// depends on time must be looked at again.
    /// </summary>
    TempoMap = 1,

    /// <summary>The text events on <c>EVENTS</c>: sections, <c>[coda]</c>, <c>[end]</c> and the like.</summary>
    Events = 2,

    /// <summary>A part added or removed.</summary>
    Parts = 4,

    /// <summary>The song's drum variant.</summary>
    DrumsVariant = 8,
}

/// <summary>
/// What a command touched (charter-spec §4.1), for the work that follows an edit, such as chart lint: the regions of the
/// parts it changed, and what it changed of the song as a whole. Immutable.
/// </summary>
/// <remarks>
/// A region is a part, a difficulty (or none, for what every difficulty shares) and the ticks it covers, both ends
/// included. Regions of the same part and difficulty that meet or touch are joined into one, so a change of many notes
/// close together is one region.
/// </remarks>
public sealed class ChartChange
{
    private readonly ChartLocation[] _regions;

    private ChartChange(ChartLocation[] regions, SongChanges song)
    {
        _regions = regions;
        Song = song;
    }

    /// <summary>A change that touched nothing.</summary>
    public static ChartChange None { get; } = new([], SongChanges.None);

    /// <summary>The regions of the parts changed, in order of part, difficulty (every difficulty first), then tick.</summary>
    public IReadOnlyList<ChartLocation> Regions => _regions;

    /// <summary>What was changed of the song as a whole.</summary>
    public SongChanges Song { get; }

    /// <summary>Whether the tempo map changed, which moves everything in time.</summary>
    public bool TempoMapChanged => (Song & SongChanges.TempoMap) != 0;

    /// <summary>Whether nothing was touched.</summary>
    public bool IsEmpty => _regions.Length == 0 && Song == SongChanges.None;

    /// <summary>
    /// Whether every track's game view must be worked out again: a part added or removed, the drum variant or the tempo
    /// map changed, which moves or rereads everything.
    /// </summary>
    public bool RebuildsAllTracks => (Song & (SongChanges.Parts | SongChanges.DrumsVariant | SongChanges.TempoMap)) != 0;

    /// <summary>
    /// Whether the change touched <paramref name="track"/>'s game view: a region of its part on its difficulty or on every
    /// difficulty, or anywhere on the drums, whose difficulties share their markers; or a change that rebuilds every track.
    /// </summary>
    public bool Touches(ChartTrackKey track)
    {
        if (RebuildsAllTracks)
            return true;
        bool drums = ChartInstruments.KindOf(track.Instrument) == ChartInstrumentKind.Drums;
        foreach (var region in _regions)
        {
            if (region.Part == track.Instrument && (drums || region.Difficulty is null || region.Difficulty == track.Difficulty))
                return true;
        }

        return false;
    }

    /// <summary>A change to the song as a whole.</summary>
    public static ChartChange Of(SongChanges song) => song == SongChanges.None ? None : new([], song);

    /// <summary>A change to one region of a part.</summary>
    /// <param name="difficulty">The difficulty, or null for what every difficulty shares.</param>
    /// <param name="tick">The first tick changed.</param>
    /// <param name="endTick">The last tick changed, no earlier than <paramref name="tick"/>.</param>
    public static ChartChange Of(ChartInstrument part, ChartDifficulty? difficulty, long tick, long endTick) =>
        new([Region(part, difficulty, tick, endTick)], SongChanges.None);

    /// <summary>A change to <paramref name="regions"/>, and to <paramref name="song"/> of the song as a whole.</summary>
    public static ChartChange Of(IEnumerable<ChartLocation> regions, SongChanges song = SongChanges.None)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var joined = Join(regions);
        return joined.Length == 0 && song == SongChanges.None ? None : new(joined, song);
    }

    /// <summary>What this change and <paramref name="other"/> touched between them.</summary>
    public ChartChange Merge(ChartChange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.IsEmpty)
            return this;
        if (IsEmpty)
            return other;
        return Of(_regions.Concat(other._regions), Song | other.Song);
    }

    /// <summary>What <paramref name="changes"/> touched between them, as for a <see cref="CompositeCommand"/>.</summary>
    public static ChartChange Union(IEnumerable<ChartChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var regions = new List<ChartLocation>();
        var song = SongChanges.None;
        foreach (var change in changes)
        {
            regions.AddRange(change._regions);
            song |= change.Song;
        }

        return Of(regions, song);
    }

    /// <summary>The regions and song changes, in a few words, for logging.</summary>
    public override string ToString()
    {
        if (IsEmpty)
            return "nothing";
        var text = new StringBuilder();
        foreach (var region in _regions)
        {
            if (text.Length > 0)
                text.Append(", ");
            text.Append(CultureInfo.InvariantCulture, $"{region.Part}");
            if (region.Difficulty is { } difficulty)
                text.Append(CultureInfo.InvariantCulture, $" {difficulty}");
            text.Append(CultureInfo.InvariantCulture, $" {region.Tick}-{region.EndTick}");
        }

        if (Song != SongChanges.None)
            text.Append(text.Length > 0 ? ", " : "").Append(CultureInfo.InvariantCulture, $"song: {Song}");
        return text.ToString();
    }

    private static ChartLocation Region(ChartInstrument part, ChartDifficulty? difficulty, long tick, long endTick)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(endTick, tick);
        return new ChartLocation(part, difficulty, tick, endTick);
    }

    // The regions sorted, with those of one part and difficulty that meet or touch joined.
    private static ChartLocation[] Join(IEnumerable<ChartLocation> regions)
    {
        var sorted = regions.ToList();
        if (sorted.Count <= 1)
            return [.. sorted];
        sorted.Sort(static (a, b) =>
        {
            int c = a.Part.CompareTo(b.Part);
            if (c == 0)
                c = Nullable.Compare(a.Difficulty, b.Difficulty);
            if (c == 0)
                c = a.Tick.CompareTo(b.Tick);
            return c != 0 ? c : a.EndTick.CompareTo(b.EndTick);
        });

        var joined = new List<ChartLocation>(sorted.Count) { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var last = joined[^1];
            var next = sorted[i];
            if (next.Part == last.Part && next.Difficulty == last.Difficulty && next.Tick <= SaturatingNext(last.EndTick))
                joined[^1] = last with { EndTick = Math.Max(last.EndTick, next.EndTick) };
            else
                joined.Add(next);
        }

        return [.. joined];
    }

    private static long SaturatingNext(long tick) => tick == long.MaxValue ? tick : tick + 1;
}
