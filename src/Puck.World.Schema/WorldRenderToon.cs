using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>Optional cel shading of opaque materials. Sky, emission and screen pictures retain their continuous colour.</summary>
/// <param name="Enabled">Whether the world boots with cel shading on; absent is off.</param>
/// <param name="Threshold">Diffuse band threshold in [0, 1].</param>
/// <param name="Softness">Full terminator width in [0.001, 0.5].</param>
/// <param name="ShadowTint">Shadow-band reflectance, three linear RGB values in [0, 1].</param>
/// <param name="OutlineWidth">Close outline radius in output pixels, in [0, 4]; zero disables outlines.</param>
/// <param name="OutlineDistance">Distance in metres at which the outline radius halves, in [0.1, 1000].</param>
/// <param name="OutlineNormal">Crease normal cosine in [0, 0.95].</param>
/// <param name="OutlineTint">Outline multiplier, three linear RGB values in [0, 1].</param>
/// <param name="SpecularSize">Highlight size as one minus the half-vector cosine, in [0.001, 0.25].</param>
/// <param name="Exposure">Reflected-light exposure compensation in stops, in [-4, 4].</param>
/// <param name="Shoulder">Hue-preserving highlight compression in [0, 2]; zero disables it.</param>
public sealed record WorldRenderToon(
    bool Enabled = false,
    float Threshold = .45f,
    float Softness = .08f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? ShadowTint = null,
    float OutlineWidth = 1.5f,
    float OutlineDistance = 12f,
    float OutlineNormal = .65f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? OutlineTint = null,
    float SpecularSize = .025f,
    float Exposure = .5f,
    float Shoulder = .65f
) {
    /// <summary>Resolves the document's look and an optional live enable override; neither affects simulation state.</summary>
    /// <param name="toon">The document settings, or null for the default look.</param>
    /// <param name="enabled">The session override, or null to follow the document.</param>
    /// <returns>The presentation parameters.</returns>
    public static SdfToon Resolve(WorldRenderToon? toon, bool? enabled = null) => (toon is null
        ? SdfToon.Default : new SdfToon {
            Enabled = toon.Enabled, Threshold = toon.Threshold, Softness = toon.Softness,
            ShadowTint = toon.ShadowTint ?? SdfToon.Default.ShadowTint,
            OutlineWidth = toon.OutlineWidth, OutlineDistance = toon.OutlineDistance, OutlineNormal = toon.OutlineNormal,
            OutlineTint = toon.OutlineTint ?? SdfToon.Default.OutlineTint,
            SpecularSize = toon.SpecularSize, Exposure = toon.Exposure, Shoulder = toon.Shoulder,
        }) with { Enabled = enabled ?? toon?.Enabled ?? false };
}
