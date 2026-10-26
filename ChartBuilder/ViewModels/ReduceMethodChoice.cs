namespace ChartBuilder.ViewModels
{
    /// <summary>A way of making lower difficulties the Reduce dialog offers, picked from its Method input.</summary>
    /// <param name="Id">The method's id, as <see cref="BuilderSettingsService"/> persists it.</param>
    /// <param name="Label">The method's name in the Method input.</param>
    public sealed record ReduceMethodChoice(string Id, string Label);
}
