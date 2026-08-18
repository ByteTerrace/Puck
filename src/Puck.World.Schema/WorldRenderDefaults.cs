using System.Text.Json.Serialization;
using Puck.Assets.Documents;
using System.Text.Json;

namespace Puck.World;

/// <summary>One placeable camera composed from a reference frame, local motion, framing policy, lens, and render target.</summary>
/// <param name="Name">The camera's stable name — the handle a View screen / layout slot samples by.</param>
/// <param name="Anchor">What the camera rides, or <see langword="null"/> for the world reference frame.</param>
/// <param name="Rig">The independent local motion, aim, and lens axes.</param>
/// <param name="RenderWidth">The offscreen render width in pixels.</param>
/// <param name="RenderHeight">The offscreen render height in pixels.</param>
public sealed record WorldCamera(string Name, WorldAnchor? Anchor, WorldCameraRig Rig, uint RenderWidth, uint RenderHeight);
public static class WorldApplicationDefaults {
    /// <summary>The built-in world ships with no bundled AGB cartridge — an asset-free default, never an owner-local
    /// absolute path or a copyrighted dump. Durable per-deployment cartridge/BIOS paths belong in the world data file
    /// (the "durable config lives in the data file" doctrine); the <c>puck.world.def.v1</c> loader
    /// (<c>Puck.World.WorldDefinitionLoader</c>) reads one, but the checked-in default file authors an empty content
    /// path, so the native-AGB screen boots unconfigured (a graceful fault, never a crash) until a real deployment
    /// supplies <see cref="WorldScreenSource.Machine.ContentPath"/>.</summary>
    public const string DefaultAgbCartridgePath = "";
    public const string WindowTitle = "Puck: World";
}
/// <summary>One graphics-quality preset — the bundle of render levers the <c>world.quality</c> verb writes for a named
/// tier (the individual <c>world.shadows</c>/<c>.ao</c>/<c>.render-scale</c> verbs still override afterward).</summary>
/// <param name="Shadows">The soft-shadow tier the preset selects.</param>
/// <param name="AmbientOcclusion">Whether the preset enables ambient occlusion.</param>
/// <param name="RenderScale">The render-scale tier the preset selects.</param>
public readonly record struct WorldQualityPreset(
    ShadowTier Shadows,
    bool AmbientOcclusion,
    WorldRenderScaleTier RenderScale
);
/// <summary>The world's render-lever defaults — the boot values <c>Puck.World.WorldRenderSettings</c> wakes on and the
/// <c>world.quality</c> preset table. Session state, not identity: these are engine-wide levers (shadows, AO, render
/// scale, the crowd radius), the graphics-menu defaults a server-pulled world would carry.</summary>
/// <param name="Shadows">The boot soft-shadow tier.</param>
/// <param name="ShadowCrowdRadius">The boot soft-shadow crowd radius (world units).</param>
/// <param name="AmbientOcclusion">Whether ambient occlusion boots on.</param>
/// <param name="RenderScale">The boot render-scale tier.</param>
/// <param name="UpscaleSharpness">The boot reduced-resolution reconstruction blend (0 bilinear .. 1 Catmull-Rom).</param>
/// <param name="LowRaw">The <c>world.quality low</c> preset.</param>
/// <param name="MediumRaw">The <c>world.quality medium</c> preset.</param>
/// <param name="HighRaw">The <c>world.quality high</c> preset.</param>
/// <param name="Extensions">The post-render extension chain, composed over the world's rendered output in list
/// order — e.g. <c>[{ "id": "sdf-film-grain", "config": { "intensity": 0.08 } }]</c>. Optional; an absent or
/// empty list is the byte-identical default path (no extension composed). Every id must name a shipped shader
/// set — a <c>puck.shader.v1</c> manifest's file stem (checked at document load); each entry's own
/// <c>config</c> is validated against that manifest's declared config schema at boot and by <c>puck schema</c>.</param>
/// <param name="Lighting">The scene's directional sun and ambient term. Optional, and every field within it is
/// optional individually — an absent section, or an absent field within it, resolves to <c>SdfFrame</c>'s pinned
/// default for that field, so a world renders unchanged until it authors one.</param>
/// <param name="Sky">The procedural sky — a gradient, sun disc, star field, and distance fog. Optional; an absent
/// section renders the pinned two-stop gradient and 0.015 fog density bit-exactly, as before this section
/// existed.</param>
/// <param name="Cycle">Lighting and sky keyed over a state row's value (a day/night cycle when that row advances).
/// Optional; absent leaves <paramref name="Lighting"/>/<paramref name="Sky"/> static.</param>
public sealed record WorldRenderDefaults(
    ShadowTier Shadows = ShadowTier.Off,
    float ShadowCrowdRadius = 15f,
    bool AmbientOcclusion = false,
    WorldRenderScaleTier RenderScale = WorldRenderScaleTier.Half,
    float UpscaleSharpness = 0f,
    [property: JsonPropertyName("low"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? LowRaw = null,
    [property: JsonPropertyName("medium"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? MediumRaw = null,
    [property: JsonPropertyName("high"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? HighRaw = null,
    IReadOnlyList<WorldRenderExtensionEntry>? Extensions = null,
    WorldRenderLighting? Lighting = null,
    WorldRenderSky? Sky = null,
    WorldRenderCycle? Cycle = null
) {
    // Exact-128 is the built-in scene, so boot in the measured fleet posture that retains ample headroom above the
    // 60-FPS floor. High/native remains a live quality preset rather than silently changing the population.
    /// <summary>Gets the built-in default render levers — the boot values and preset table.</summary>
    public static WorldRenderDefaults Default { get; } = new WorldRenderDefaults();

    /// <summary>Gets the low quality preset (the authored one, or the built-in).</summary>
    [JsonIgnore]
    public WorldQualityPreset Low => (LowRaw ?? new WorldQualityPreset(
        AmbientOcclusion: false,
        RenderScale: WorldRenderScaleTier.Half,
        Shadows: ShadowTier.Off
    ));
    /// <summary>Gets the medium quality preset (the authored one, or the built-in).</summary>
    [JsonIgnore]
    public WorldQualityPreset Medium => (MediumRaw ?? new WorldQualityPreset(
        AmbientOcclusion: true,
        RenderScale: WorldRenderScaleTier.ThreeQuarter,
        Shadows: ShadowTier.Medium
    ));
    /// <summary>Gets the high quality preset (the authored one, or the built-in).</summary>
    [JsonIgnore]
    public WorldQualityPreset High => (HighRaw ?? new WorldQualityPreset(
        AmbientOcclusion: true,
        RenderScale: WorldRenderScaleTier.Native,
        Shadows: ShadowTier.High
    ));

    /// <summary>Returns the preset for a quality tier keyword (case-insensitive <c>low</c>/<c>medium</c>/<c>high</c>), or
    /// <see langword="null"/> when the token names none.</summary>
    /// <param name="name">The quality tier keyword.</param>
    /// <returns>The matching preset, or <see langword="null"/>.</returns>
    public WorldQualityPreset? Preset(string name) {
        return (name.ToUpperInvariant() switch {
            "LOW" => Low,
            "MEDIUM" => Medium,
            "HIGH" => High,
            _ => ((WorldQualityPreset?)null),
        });
    }
}
/// <summary>One entry in <see cref="WorldRenderDefaults.Extensions"/> — a shipped shader set's id plus the values
/// for its manifest-declared config fields.</summary>
/// <param name="Id">The shader set id (its <c>puck.shader.v1</c> manifest's file stem) — checked against the
/// shipped vocabulary at document load (<see cref="WorldExtensionVocabularyHook.IsRegisteredPostRenderExtension"/>),
/// never interpreted here.</param>
/// <param name="Config">The set's config values, or <see langword="null"/> when the manifest declares none or every
/// field has a default. Not validated at document load — the manifest's declared config schema validates it at
/// boot (matching <see cref="WorldScreenSource.Machine"/>'s <c>Options</c>, the identical shallow-then-deep
/// precedent), refusing boot with the set id and reason on a malformed value.</param>
public sealed record WorldRenderExtensionEntry(string Id, JsonElement? Config = null);
/// <summary>The scene's directional sun and ambient term — threads unchanged into the engine's existing per-frame
/// lighting fields (<c>SdfFrame.SunDirection</c>/<c>SunWeight</c>/<c>SunColor</c>/<c>AmbientBase</c>/
/// <c>AmbientHemisphere</c>/<c>AmbientColor</c>). Every field, at every level, is optional individually — an absent
/// one resolves to <c>SdfFrame</c>'s pinned default for that field, so an unauthored section, or a partially
/// authored one, renders exactly as a world that declares neither.</summary>
/// <param name="Sun">The directional sun.</param>
/// <param name="Ambient">The ambient (hemisphere) term.</param>
public sealed record WorldRenderLighting(WorldRenderSun? Sun = null, WorldRenderAmbient? Ambient = null);
/// <summary>One directional sun. Every field is optional individually — absent resolves to <c>SdfFrame</c>'s pinned
/// default for that field.</summary>
/// <param name="Direction">The direction from a lit surface toward the light, any nonzero length (normalized
/// host-side before upload).</param>
/// <param name="Weight">The sun's diffuse weight.</param>
/// <param name="Color">The sun's linear <c>#RRGGBB</c> color.</param>
public sealed record WorldRenderSun(DocumentVector3? Direction = null, float? Weight = null, string? Color = null);
/// <summary>The ambient (hemisphere) term. Every field is optional individually — absent resolves to
/// <c>SdfFrame</c>'s pinned default for that field.</summary>
/// <param name="Base">The ambient floor.</param>
/// <param name="Hemisphere">The hemisphere gradient, scaling surface normal Y.</param>
/// <param name="Color">The ambient linear <c>#RRGGBB</c> color.</param>
public sealed record WorldRenderAmbient(float? Base = null, float? Hemisphere = null, string? Color = null);
/// <summary>The procedural sky — a three-stop gradient, sun disc, star field, and distance fog, authored as world
/// data. Absent is a hard gate: every existing world renders the pinned two-stop gradient and 0.015 fog density
/// bit-exactly, as before this section existed, until it authors one.</summary>
/// <param name="Zenith">The straight-up sky color, as <c>#RRGGBB</c>. Optional; absent takes the pinned zenith.</param>
/// <param name="Horizon">The horizon-band color (the gradient's middle stop), as <c>#RRGGBB</c>. Optional; absent
/// takes the midpoint between the pinned ground and zenith.</param>
/// <param name="Ground">The straight-down (nadir) color, as <c>#RRGGBB</c>. Optional; absent takes the pinned
/// ground.</param>
/// <param name="FogDensity">The exponential distance-fog density fading toward the sky color. Optional; absent
/// takes the pinned 0.015 — the exact value the fog term used before this field existed.</param>
/// <param name="Sun">The visible sun disc, drawn about the lighting sun's direction. Optional; absent draws no
/// disc.</param>
/// <param name="Stars">The procedural star field. Optional; absent draws no stars.</param>
/// <param name="Clouds">The procedural cloud layer. Optional; absent draws no clouds.</param>
public sealed record WorldRenderSky(
    string? Zenith = null,
    string? Horizon = null,
    string? Ground = null,
    float? FogDensity = null,
    WorldRenderSkySun? Sun = null,
    WorldRenderSkyStars? Stars = null,
    WorldRenderSkyClouds? Clouds = null
);
/// <summary>Lighting and sky as a function of a state row: presentation reads the row's live value each frame,
/// takes its fractional part (an advancing clock wraps once per unit), and interpolates between the two keys that
/// bracket it — colours and scalars linearly, the sun direction along the arc, the star seed from the earlier key.
/// A key states only the fields it moves; every other field holds its value from the previous key (the first key
/// starts from the static <see cref="WorldRenderDefaults.Lighting"/>/<see cref="WorldRenderDefaults.Sky"/>, and the
/// last key wraps into the first). Presentation-only: the row is simulation state, the interpolation is not.</summary>
/// <param name="State">The state row read (its slot cell; <c>Fixed</c> or <c>Int</c>).</param>
/// <param name="Keys">At least two keys, strictly ascending <see cref="WorldRenderCycleKey.At"/> in <c>[0, 1)</c>.</param>
public sealed record WorldRenderCycle(string State, IReadOnlyList<WorldRenderCycleKey> Keys);
/// <summary>One point on a <see cref="WorldRenderCycle"/>.</summary>
/// <param name="At">The row-value fraction this key sits at, in <c>[0, 1)</c>.</param>
/// <param name="Lighting">The lighting fields this key moves, or <see langword="null"/>.</param>
/// <param name="Sky">The sky fields this key moves, or <see langword="null"/>.</param>
public sealed record WorldRenderCycleKey(float At, WorldRenderLighting? Lighting = null, WorldRenderSky? Sky = null);
/// <summary>The visible sun disc — an additive highlight about the lighting sun's direction.</summary>
/// <param name="DiscRadians">The disc's angular half-radius in radians, in <c>(0, π/2]</c>. Controls the falloff
/// sharpness: a smaller disc reads sharper.</param>
/// <param name="Intensity">The disc's peak additive brightness.</param>
public sealed record WorldRenderSkySun(float DiscRadians, float Intensity);
/// <summary>The procedural star field: a deterministic per-cell hash over an octahedral sky projection. No texture,
/// no session state — the identical density/seed always draws the identical field.</summary>
/// <param name="Density">The star grid's cell count per octahedral axis. A higher value packs more, smaller
/// stars.</param>
/// <param name="Brightness">The peak per-star brightness.</param>
/// <param name="Seed">The hash seed folded into every cell — a different seed reshuffles the field.</param>
/// <param name="Twinkle">Scintillation for a share of the stars. Optional; absent twinkles none.</param>
public sealed record WorldRenderSkyStars(float Density, float Brightness, uint Seed, WorldRenderSkyTwinkle? Twinkle = null);
/// <summary>Scintillation: a hash-chosen share of the stars dip and recover on the simulation clock, each at its own
/// harmonic and phase of one authored rate, so no two twinkle in step. Presentation-only, keyed on the tick.</summary>
/// <param name="Share">The fraction of stars that twinkle, in <c>[0, 1]</c>. Zero twinkles none.</param>
/// <param name="Depth">How far a twinkling star dips below its steady brightness, in <c>[0, 1]</c>: 0 holds steady, 1
/// dips to black.</param>
/// <param name="Rate">The fundamental scintillation rate in hertz; each star twinkles at a small harmonic of it.</param>
public sealed record WorldRenderSkyTwinkle(float Share, float Depth, float Rate);
/// <summary>The procedural cloud layer: a deterministic hashed-lattice noise (warped, four octaves) on a plane above
/// the camera, thresholded by coverage, drawn over the gradient, sun disc and stars and fading into the horizon. No
/// texture, no session state — the identical settings and seed always draw the identical layer at the identical
/// tick.</summary>
/// <param name="Coverage">The fraction of the sky the layer covers, in <c>[0, 1]</c>. Zero draws none.</param>
/// <param name="Softness">The width of a cloud's edge, in <c>(0, 1]</c>: small is hard-edged cumulus, large is a
/// diffuse haze.</param>
/// <param name="Scale">The size of one cloud cell in layer units (the layer sits at unit height, so a scale of 1
/// spans about 45° overhead). Larger is broader clouds.</param>
/// <param name="Seed">The hash seed folded into the lattice — a different seed reshapes the layer.</param>
/// <param name="Color">The cloud colour, as <c>#RRGGBB</c> or a <c>state.&lt;row&gt;.&lt;key&gt;</c> binding.
/// Optional; absent is white.</param>
/// <param name="Drift">The layer's wind, in layer units per second along world X and Z, integrated on the tick clock.
/// Optional; absent holds still.</param>
/// <param name="Spin">The layer's rotation about the zenith in radians per second — the planetary-scale turning a
/// rotating frame gives a broad flow; positive is counter-clockwise seen from below. Optional; absent is none.</param>
/// <param name="Curl">The Coriolis twist: how far, in radians, the layer is wound about the zenith at 45° elevation,
/// the winding falling off toward the horizon and the zenith so bands spiral in rather than shear apart. Positive
/// winds counter-clockwise. Optional; absent is none.</param>
/// <param name="Shear">The wind of the SHAPING field relative to the cloud field, in layer units per second: the
/// two slide past each other, so clouds boil and re-form as they drift rather than glide as a fixed picture.
/// Optional; absent holds their shapes.</param>
public sealed record WorldRenderSkyClouds(float Coverage, float Softness, float Scale, uint Seed, string? Color = null, DocumentVector2? Drift = null, float? Spin = null, float? Curl = null, DocumentVector2? Shear = null);
