using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Assets.Documents;

/// <summary>
/// The ONE shared <see cref="JsonSerializerOptions"/> shape every document store serializes through (a creation
/// store, a world document store, an audio document store), instead of each copying its own instance.
/// <see cref="System.Numerics.Vector2"/>/<see cref="System.Numerics.Vector3"/>/<see cref="System.Numerics.Quaternion"/>
/// expose FIELDS, not properties, so they ride the dedicated array converters below rather than
/// <see cref="JsonSerializerOptions.IncludeFields"/> (which STJ would otherwise need to serialize them at all, and
/// which emits their raw field names as a wire object instead of the one array spelling every document uses).
/// </summary>
public static class DocumentJsonOptions {
    /// <summary>Indented, camel-case, case-insensitive-on-read, string-enum JSON options — the one shape every
    /// document store serializes through. An enum persists BY NAME, never ordinal, and a numeric enum token (the
    /// old, wire-incompatible shape) is REFUSED rather than silently accepted (<c>allowIntegerValues: false</c>): a
    /// reordered enum must never silently reinterpret a persisted value. <c>PropertyNamingPolicy</c> governs
    /// PROPERTY names only, so the enum's exact declared member name is the one wire spelling regardless.</summary>
    public static JsonSerializerOptions Shared { get; } = new() {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false), new Vector2JsonConverter(), new Vector3JsonConverter(), new QuaternionJsonConverter() },
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };
}
