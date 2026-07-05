using System.Text.Json.Serialization;

namespace Puck.Scene;

/// <summary>
/// A scene material. The document's <c>scene.materials</c> array is positional — an object references a material by
/// its zero-based index in that array, which becomes the material id the <c>SdfProgramBuilder</c> assigns. Beyond the
/// albedo every channel is optional and additive: an older albedo-only document shades exactly as it always did.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Material {
    /// <summary>The linear-RGB albedo, authored as a 3-element <c>[r, g, b]</c> array.</summary>
    public IReadOnlyList<float> Albedo { get; init; } = [];
    /// <summary>The self-illumination strength: <c>albedo * emissive</c> adds to the shaded color, so the surface
    /// glows through shadow and ambient falloff. 0 (the default) = none.</summary>
    public float Emissive { get; init; }
    /// <summary>The Blinn-Phong specular strength in [0, 1]. 0 (the default) = matte.</summary>
    public float Specular { get; init; }
    /// <summary>The Blinn-Phong exponent (highlight tightness); meaningful only when <see cref="Specular"/> is
    /// non-zero. Omitted = the engine default (32). Nullable BECAUSE of a source-gen gotcha: System.Text.Json's
    /// source-generated deserializer does not run a record's property initializer for members absent from the JSON
    /// (they arrive as <c>default(T)</c>), so a non-zero default must be expressed as null-means-default and resolved
    /// at build time.</summary>
    public float? Shininess { get; init; }
}
