using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// A scene material. The document's <c>scene.materials</c> array is positional — an object references a material by
/// its zero-based index in that array, which becomes the material id the <c>SdfProgramBuilder</c> assigns. Today a
/// material is just a linear-RGB albedo; more channels can be added as additive fields without breaking older
/// documents.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Material {
    /// <summary>The linear-RGB albedo, authored as a 3-element <c>[r, g, b]</c> array.</summary>
    public IReadOnlyList<float> Albedo { get; init; } = [];
}
