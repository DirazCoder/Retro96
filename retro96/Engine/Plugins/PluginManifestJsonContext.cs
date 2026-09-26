using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retro96.Plugins;

/// <summary>Source-generated JSON metadata for the public plugin manifest contract.
/// This keeps plugin.json loading AOT/trimming-safe on .NET 11 without reflection-based
/// System.Text.Json serialization.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PluginManifest))]
internal partial class PluginManifestJsonContext : JsonSerializerContext
{
}
