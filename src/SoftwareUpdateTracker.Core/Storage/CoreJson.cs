using System.Text.Json;
using System.Text.Json.Serialization;

namespace SoftwareUpdateTracker.Core.Storage;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectNullableAnnotations = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(SettingsFile))]
internal sealed partial class CoreJson : JsonSerializerContext
{
}
