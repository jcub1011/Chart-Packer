using System.Text.Json.Serialization;
using ChartPacker.Core.Charting.Layout;

namespace ChartBuilder.Services
{
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(BuilderSettings))]
    [JsonSerializable(typeof(LaneDockState))]
    internal sealed partial class BuilderSettingsJsonContext : JsonSerializerContext
    {
    }
}
