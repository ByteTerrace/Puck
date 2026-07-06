using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Demo;

/// <summary>
/// The ONE shared <see cref="JsonSerializerOptions"/> shape every document store (<see cref="World.WorldDocumentStore"/>,
/// <see cref="Creator.CreationStore"/>, <see cref="Forge.AudioDocumentStore"/>) serializes through, instead of each
/// copying its own instance. <see cref="JsonSerializerOptions.IncludeFields"/> is ALWAYS <see langword="true"/> here —
/// CRITICAL, not cosmetic: <see cref="System.Numerics.Vector3"/>/<see cref="System.Numerics.Quaternion"/> expose
/// FIELDS, not properties, and <c>System.Text.Json</c> silently zeroes them into degenerate transforms if the option
/// is ever omitted on a document that carries one (a document with no geometry, like <see cref="Forge.AudioDocument"/>,
/// pays nothing extra for it — the option is a no-op where there is nothing to include).
/// </summary>
internal static class DocumentJsonOptions {
    /// <summary>Indented, camel-case, case-insensitive-on-read, string-enum JSON options with fields included — the
    /// one shape every document store serializes through.</summary>
    public static JsonSerializerOptions Shared { get; } = new() {
        Converters = { new JsonStringEnumConverter() },
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}
