using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One entry of the scene's material palette. Packed as <see cref="SdfProgram.MaterialVectorsPerEntry"/> uint4
/// words, its paints in the program's paint table (see <see cref="SdfProgram"/>).</summary>
/// <param name="Albedo">The linear-RGB base color; every component must be finite and non-negative when admitted to
/// a builder or program. Also the metallic reflectance tint — see <see cref="Metal"/>.</param>
/// <param name="Emissive">The self-illumination, a luminance in nits (candela per square metre) the albedo tints:
/// <c>albedo * emissive</c> adds to the shaded luminance, so a white emissive surface shows exactly it and glows through
/// shadow and ambient falloff (an LED indicator about 300, a lit diffuser about 2,500). Must be finite and
/// non-negative; 0 = none.</param>
/// <param name="Specular">The finite, non-negative dielectric reflectance at normal incidence (GGX <c>f0</c> before
/// the <see cref="Metal"/> mix). The GGX term is skipped entirely when both <paramref name="Specular"/> and
/// <see cref="Metal"/> are zero, so a plain lambert material pays no specular cost.</param>
/// <param name="Roughness">The finite GGX roughness in [0, 1]; meaningful only when the GGX term is active (see
/// <see cref="Specular"/>/<see cref="Metal"/>). 0 = mirror-tight highlight, 1 = the broadest lobe this material model
/// reaches. The shader floors it before squaring — <c>rough = sqrt(roughness^2 + SdfRoughnessFloorSquared)</c>,
/// <c>alpha2 = rough * rough</c> — so a bare light direction never collapses the GGX lobe to a delta function
/// (<c>sdfMaterialShade</c> in <c>shade/sdf-material.hlsli</c> is the one decode/shade point). A document that still spells
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
/// <param name="Fill">The warm/cool fill tint added as <c>albedo * Fill * (1 - max(n·key, 0)) *
/// ambientOcclusion</c> — a restrained, art-directed fill on the side of a surface the key light does not reach
/// (the study's skin fill term, generalized to any material via an authored color instead of a hardcoded warm
/// constant). Black (the default) contributes exactly 0.</param>
/// <param name="Inset">Optional refractive radial paint layer.</param>
/// <param name="Bleed">The finite, nonnegative tint multiplying diffuse reflectance and emission sent into the
/// irradiance volume. Null means white; this does not change the directly viewed surface color.</param>
/// <param name="Receive">The finite, nonnegative gain applied to indirect irradiance received by the surface.</param>
/// <param name="Paints">The paints drawn over the surface, each over the one before; null or empty paints none.</param>
/// <param name="EmissiveLane">The render lane in [0, 3] that scales <see cref="Emissive"/> by <c>saturate(lane)</c>
/// at shading time, read from the shaded surface's own frame slot (an inhabited body's look lanes). A surface with
/// no frame slot, and the irradiance volume's trace of static draws, read the lane as zero. Null keeps the static
/// emissive.</param>
/// <param name="Glass">The glass response, or <see langword="null"/> for an opaque material. A glass material's
/// <paramref name="Albedo"/> is its tint, each channel in [0, 1], the colour light takes on through
/// <see cref="SdfGlass.Depth"/> of it, and its <paramref name="Roughness"/> is its frost. A shape carrying one bakes
/// into a group of its own and draws in the glass pass over the opaque image; inside an opaque group (a mixed dynamic
/// group) it shades opaque with the tint as albedo.</param>
public readonly record struct SdfMaterial(Vector3 Albedo, float Emissive = 0f, float Specular = 0f, float Roughness = SdfMaterial.DefaultRoughness, float Sheen = 0f, float Metal = 0f, float Coat = 0f, SdfWeathering? Weathering = null, float Wrap = 0f, float Soften = 0f, Vector3 Fill = default, SdfInset? Inset = null, Vector3? Bleed = null, float Receive = 1f, IReadOnlyList<SdfPaint>? Paints = null, int? EmissiveLane = null, SdfGlass? Glass = null) {
    /// <summary>Gets whether the material is glass: it carries a <see cref="Glass"/> response.</summary>
    public bool IsGlass => (Glass is not null);
    /// <summary>Gets whether the glass response, when present, is admissible with this material's albedo as its tint
    /// (<see cref="SdfGlass.IsValid"/>, <see cref="SdfGlass.IsTint"/>); an opaque material always is.</summary>
    public bool IsGlassValid => ((Glass is not { } glass) || (glass.IsValid && SdfGlass.IsTint(tint: Albedo)));

    /// <summary>The roughness whose GGX alpha (<c>sqrt(roughness^2 + SdfRoughnessFloorSquared)</c>, see
    /// <see cref="Roughness"/>) equals the Walter et al. (2007) Blinn-Phong-equivalent alpha of exponent 32,
    /// <c>sqrt(2 / (32 + 2))</c>. KEEP IN SYNC with <c>SdfRoughnessFloorSquared</c> in <c>shade/sdf-material.hlsli</c>
    /// (0.018) — the two constants together fix the default highlight width.</summary>
    public const float DefaultRoughness = 0.20204833f;
    /// <summary>The faintest emissive, in nits, an authoring door admits above zero: a fraction of a nit shows under no
    /// exposure a world renders at, so a value below it is a relative weight rather than a luminance.</summary>
    public const float MinimumVisibleEmissive = 1f;

    /// <summary>Returns whether an emissive lane is absent or names one of the four render lanes.</summary>
    /// <param name="lane">The lane, or <see langword="null"/> for a static emissive.</param>
    /// <returns><see langword="true"/> when <paramref name="lane"/> is null or in [0, 3].</returns>
    public static bool IsEmissiveLane(int? lane) => ((lane is not { } index) || (((uint)index) <= 3u));
}
