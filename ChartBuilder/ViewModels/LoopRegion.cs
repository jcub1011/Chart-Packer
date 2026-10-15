namespace ChartBuilder.ViewModels
{
    /// <summary>A loop region, in seconds of audio (charter-spec C10 item 2).</summary>
    public sealed record LoopRegion(double Start, double End);
}
