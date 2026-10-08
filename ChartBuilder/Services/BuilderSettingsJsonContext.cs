using System.Collections.Generic;
using System.Text.Json.Serialization;
using ChartPacker.Core.Charting.Authoring;
using ChartPacker.Core.Charting.Layout;
using ChartPacker.Core.Launching;

namespace ChartBuilder.Services
{
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(BuilderSettings))]
    [JsonSerializable(typeof(LaneDockState))]
    [JsonSerializable(typeof(List<RecentSong>))]
    [JsonSerializable(typeof(ReductionDefaults))]
    internal sealed partial class BuilderSettingsJsonContext : JsonSerializerContext
    {
    }
}
