using System.Collections.Generic;
using System.Text.Json.Serialization;
using ChartBuilder.ViewModels;
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
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, List<string>>))]
    [JsonSerializable(typeof(ReductionDefaults))]
    [JsonSerializable(typeof(OverwritePolicy))]
    [JsonSerializable(typeof(NeuralReduceMethodViewModel.NeuralMethodSettings))]
    internal sealed partial class BuilderSettingsJsonContext : JsonSerializerContext
    {
    }
}
