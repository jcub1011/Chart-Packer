using System.Collections.Generic;
using System.Text.Json.Serialization;
using ChartShared.Core.Charting.Authoring;
using ChartShared.Core.Charting.Layout;
using ChartShared.Core.Jobs;
using ChartShared.Core.Launching;

namespace ChartBuilder.Services
{
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(BuilderSettings))]
    [JsonSerializable(typeof(LaneDockState))]
    [JsonSerializable(typeof(List<RecentSong>))]
    [JsonSerializable(typeof(ReductionDefaults))]
    [JsonSerializable(typeof(OverwritePolicy))]
    internal sealed partial class BuilderSettingsJsonContext : JsonSerializerContext
    {
    }
}
