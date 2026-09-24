using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoftwareUpdateTracker.Core.Storage;

// Persisted records use set, not init: generated code would drop the defaults of missing init keys.
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(SettingsFile))]
[JsonSerializable(typeof(HistoryFile))]
internal sealed partial class CoreJson : JsonSerializerContext
{
}
