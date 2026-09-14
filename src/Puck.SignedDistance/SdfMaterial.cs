using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One entry of the scene's material palette. Packed as twenty uint4 words (see <see cref="SdfProgram"/>).</summary>
/// <param name="Albedo">The linear-RGB base color; every component must be finite and non-negative when admitted to
/// a builder or program. Also the metallic reflectance tint — see <see cref="Metal"/>.</param>
/// <param name="Emissive">The self-illumination strength: <c>albedo * emissive</c> adds to the shaded color, so an
/// emissive surface glows through shadow and ambient falloff. Must be finite and non-negative; 0 = none.</param>
/// <param name="Specular">The finite, non-negative dielectric reflectance at normal incidence (GGX <c>f0</c> before
/// the <see cref="Metal"/> mix). The GGX term is skipped entirely when both <paramref name="Specular"/> and
/// <see cref="Metal"/> are zero, so a plain lambert material pays no specular cost.</param>
/// <param name="Roughness">The finite GGX roughness in [0, 1]; meaningful only when the GGX term is active (see
/// <see cref="Specular"/>/<see cref="Metal"/>). 0 = mirror-tight highlight, 1 = the broadest lobe this material model
/// reaches. The shader floors it before squaring — <c>rough = sqrt(roughness^2 + SdfRoughnessFloorSquared)</c>,
/// <c>alpha2 = rough * rough</c> — so a bare light direction never collapses the GGX lobe to a delta function
/// (<c>sdfMaterialShade</c> in <c>sdf-vm.hlsli</c> is the one decode/shade point). A document that still spells
/// <c>shininess</c> is refused by name.</param>
/// <param name="Sheen">The finite sheen strength in [0, 1]: a per-material fresnel edge lift,
/// <c>sheen * (1 - saturate(dot(normal, -rayDirection)))^SdfSheenFresnelExponent</c>, multiplied into the material's
/// own already-lit color (never an independent glow color) so it reads as a painted light catch along a bevel or rim
/// rather than a halo. 0 = none (the pre-sheen shading, unchanged).</param>
/// <param name="Metal">The finite metalness in [0, 1]. Mixes the GGX reflectance toward <see cref="Albedo"/>
/// (<c>f0 = lerp(specular, albedo, metal)</c>) and scales the lambert diffuse term down by <c>(1 - metal)</c>, so a
/// fully metallic surface (1) carries no diffuse response and a colored specular tinted by its own albedo. 0 = the
/// dielectric shading <see cref="Specular"/> alone describes.</param>
/// <param name="Coat">The finite clearcoat strength in [0, 1]: a second, narrower fixed-roughness (0.25) GGX lobe
/// riding the same light and normal, scaled by <c>coat * 0.04</c> — a thin lacquer/varnish catch layered over the
/// primary specular response, independent of <see cref="Roughness"/>/<see cref="Metal"/>. 0 = none.</param>
/// <param name="Weathering">Optional authored coverage and reveal surfaces.</param>
/// <param name="Wrap">The wrap-lighting share in [0, 1]: the shaded diffuse term becomes
/// <c>max((n·l + Wrap) / (1 + Wrap), 0)</c> in place of the plain <c>max(n·l, 0)</c> Lambert term, widening the
/// terminator so light appears to carry past grazing incidence — the skin/soft-surface look (ported from the
/// study's <c>directLight</c> <c>skin</c> mix). 0 (the default) reduces the formula to the plain Lambert term
/// exactly, so an unauthored material is byte-identical.</param>
/// <param name="Soften">The shading-normal broadening in [0, 1]: blends the hit's normal toward a wide-stencil
/// (0.05 creation-unit epsilon) field-gradient guide, smoothing fine surface detail (pores, panel seams, wear
/// noise) out of the lighting normal while leaving the silhouette and geometric AO normal untouched — the study's per-part guide
/// ellipsoid normal, generalized without an authored guide shape. 0 (the default) skips the extra probe
/// entirely.</param>
/// <param name="Bounce">The warm/cool bounce tint added as <c>albedo * Bounce * (1 - max(n·key, 0)) *
/// ambientOcclusion</c> — a restrained, art-directed fill on the side of a surface the key light does not reach
/// (the study's skin bounce term, generalized to any material via an authored color instead of a hardcoded warm
/// constant). Black (the default) contributes exactly 0.</param>
/// <param name="Inset">Optional refractive radial paint layer.</param>
public readonly record struct SdfMaterial(Vector3 Albedo, float Emissive = 0f, float Specular = 0f, float Roughness = SdfMaterial.DefaultRoughness, float Sheen = 0f, float Metal = 0f, float Coat = 0f, SdfWeathering? Weathering = null, float Wrap = 0f, float Soften = 0f, Vector3 Bounce = default, SdfInset? Inset = null) {
    /// <summary>The roughness whose GGX alpha (<c>sqrt(roughness^2 + SdfRoughnessFloorSquared)</c>, see
    /// <see cref="Roughness"/>) equals the Walter et al. (2007) Blinn-Phong-equivalent alpha of exponent 32,
    /// <c>sqrt(2 / (32 + 2))</c>. KEEP IN SYNC with <c>SdfRoughnessFloorSquared</c> in <c>sdf-vm.hlsli</c>
    /// (0.018) — the two constants together fix the default highlight width.</summary>
    public const float DefaultRoughness = 0.20204833f;
}
