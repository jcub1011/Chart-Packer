namespace ChartShared.Core.Charting.Authoring;

/// <summary>What a generator does with the phrases a part already has, as its window offers it.</summary>
public enum GenerationMode
{
    /// <summary>It replaces the phrases in the span: the phrases there are removed when the kept proposals are added.</summary>
    Replace,

    /// <summary>It adds its phrases alongside the phrases there, keeping clear of them.</summary>
    Add,
}
