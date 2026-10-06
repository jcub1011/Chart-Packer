using System.Text.Json.Serialization;

namespace ChartBuilder.Services
{
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(BuilderSettings))]
    internal sealed partial class BuilderSettingsJsonContext : JsonSerializerContext
    {
    }
}
