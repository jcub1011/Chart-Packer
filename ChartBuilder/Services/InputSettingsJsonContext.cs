using System.Text.Json.Serialization;
using ChartShared.Core.Input;

namespace ChartBuilder.Services
{
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(InputSettings))]
    internal sealed partial class InputSettingsJsonContext : JsonSerializerContext
    {
    }
}
