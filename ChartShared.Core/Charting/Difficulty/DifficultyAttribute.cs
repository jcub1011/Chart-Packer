namespace ChartShared.Core.Charting.Difficulty;

/// <summary>One of the five things a chart's difficulty is measured by (docs/difficulty-model.md §4).</summary>
public enum DifficultyAttribute
{
    /// <summary>The strumming hand: how fast, alternate strumming, strummed chords, and switching from HOPOs (§4.1).</summary>
    Strum,

    /// <summary>The fretting hand: moving between lane states, chord shapes, opens, HOPOs and held lanes (§4.2).</summary>
    Note,

    /// <summary>How hard the timing is to read: changing intervals, subdivision changes and syncopation (§4.3).</summary>
    Rhythm,

    /// <summary>How unpredictable the notes are: local variety and phrases not seen lately (§4.5).</summary>
    Pattern,

    /// <summary>How long the hands have worked hard, strumming and fretting, without enough rest (§4.4).</summary>
    Endurance,
}

/// <summary>A value for each <see cref="DifficultyAttribute"/>, on the normalized scale where 100 is its reference.</summary>
public readonly record struct DifficultyAttributes(double Strum, double Note, double Rhythm, double Pattern, double Endurance)
{
    /// <summary>The attributes, in the order <see cref="DifficultyAttribute"/> lists them.</summary>
    public static IReadOnlyList<DifficultyAttribute> All { get; } = Enum.GetValues<DifficultyAttribute>();

    public double this[DifficultyAttribute attribute] => attribute switch
    {
        DifficultyAttribute.Strum => Strum,
        DifficultyAttribute.Note => Note,
        DifficultyAttribute.Rhythm => Rhythm,
        DifficultyAttribute.Pattern => Pattern,
        DifficultyAttribute.Endurance => Endurance,
        _ => throw new ArgumentOutOfRangeException(nameof(attribute), attribute, null),
    };

    /// <summary>The largest of the five.</summary>
    public double Max => Math.Max(Math.Max(Math.Max(Strum, Note), Math.Max(Rhythm, Pattern)), Endurance);
}
