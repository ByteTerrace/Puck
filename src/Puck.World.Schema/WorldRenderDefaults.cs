using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Assets.Documents;
using System.Text.Json;

namespace Puck.World;

/// <summary>One ranked anchor candidate of a <see cref="WorldCamera"/>: the anchor the camera rides while
/// <paramref name="When"/> holds. Candidates are walked in authored order every frame and the first holding one wins;
/// a <see langword="null"/> predicate always holds, so the last row is the default.</summary>
/// <param name="Anchor">What the camera rides while this candidate wins.</param>
/// <param name="When">The condition, evaluated for the seat the view is resolved for.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldCameraAnchorCandidate(WorldAnchor Anchor, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OverlayPredicate? When = null);
/// <summary>One placeable camera composed from a reference frame, local motion, framing policy, lens, and render target.</summary>
/// <param name="Name">The camera's stable name — the handle a View screen / layout slot samples by.</param>
/// <param name="Anchor">What the camera rides, or <see langword="null"/> for the world reference frame (or for
/// <paramref name="Anchors"/> to decide).</param>
/// <param name="Rig">The independent local motion, aim, and lens axes.</param>
/// <param name="RenderWidth">The offscreen render width in pixels.</param>
/// <param name="RenderHeight">The offscreen render height in pixels.</param>
/// <param name="Anchors">Ranked anchor candidates, first holding wins each frame — a portrait camera that rides the
/// speaking character while they speak and the seat's own avatar otherwise. Refused beside <paramref name="Anchor"/>;
/// a single unconditional anchor is <paramref name="Anchor"/>.</param>
public sealed record WorldCamera(
    string Name,
    WorldCameraProgram Rig,
    uint RenderWidth,
    uint RenderHeight,
    // OPTIONAL, and carried beside its own XOR partner deliberately: `Anchor` and `Anchors` are alternatives, so a
    // camera authoring neither is an unanchored camera rather than an incomplete one. Before this pair agreed, the
    // singular sat among the required parameters and every unanchored camera had to spell `"anchor": null` to load.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldAnchor? Anchor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldCameraAnchorCandidate>? Anchors = null
) {
    /// <summary>Gets a value indicating whether any candidate or the anchor is seat-relative, so the view must be
    /// resolved per seat.</summary>
    [JsonIgnore]
    public bool IsSeatRelative {
        get {
            if (Anchor is WorldAnchor.Seat) {
                return true;
            }

            foreach (var candidate in (Anchors ?? [])) {
                if (candidate?.Anchor is WorldAnchor.Seat) {
                    return true;
                }
            }

            return false;
        }
    }
}
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
/// <param name="Environment">The analytic studio-reflection softboxes and horizon gradient a GGX specular lobe
/// reflects. Optional; absent (no softboxes, a black horizon) contributes nothing to the shaded color.</param>
/// <param name="Tonemap">The tonemap applied to the frame's final color. Optional; absent is
/// <see cref="WorldTonemap.None"/> — the stylized shaded color, unchanged.</param>
/// <param name="FarDistance">The far distance in world units: the depth at which every camera march ends — the far
/// plane the renderer's fine march exits at, the reach of the beam's cone proofs, and the depth the fog and depth
/// ramps are measured against. Geometry beyond it is never marched, so an infinite plane ends on a visible horizon
/// curve at this depth unless the sky fog has absorbed it (<c>render.sky.fogDensity</c>). Optional; absent
/// resolves to the engine's pinned 40 — exactly the value every world marched to before this field existed. Must
/// lie within [<see cref="MinFarDistance"/>, <see cref="MaxFarDistance"/>]. Re-read on every definition revision
/// (a <c>world.row.set render</c> lands on the next frame); <c>world.budget</c> echoes it with its derived
/// costs.</param>
public sealed record WorldRenderDefaults(
    ShadowTier Shadows = ShadowTier.Off,
    float ShadowCrowdRadius = 0f,
    bool AmbientOcclusion = false,
    WorldRenderScaleTier RenderScale = WorldRenderScaleTier.Native,
    float UpscaleSharpness = 0f,
    [property: JsonPropertyName("low"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? LowRaw = null,
    [property: JsonPropertyName("medium"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? MediumRaw = null,
    [property: JsonPropertyName("high"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? HighRaw = null,
    IReadOnlyList<WorldRenderExtensionEntry>? Extensions = null,
    WorldRenderLighting? Lighting = null,
    WorldRenderSky? Sky = null,
    WorldRenderCycle? Cycle = null,
    WorldRenderEnvironment? Environment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldTonemap? Tonemap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? FarDistance = null
) {
    /// <summary>The smallest <see cref="FarDistance"/> the validator admits: one world unit. The beam's cone march
    /// starts at 0.02 units and the fine march accepts a hit within a 0.001-unit floor, so a far plane under one unit
    /// leaves no marchable depth a camera could frame a body in.</summary>
    public const float MinFarDistance = 1f;
    /// <summary>The largest <see cref="FarDistance"/> the validator admits: 8192 world units. The march advances a
    /// float depth against a 0.001-unit surface epsilon; 8192 is the largest power of two at which a float's spacing
    /// (2^13 · 2^-23 = 0.00098) still resolves that epsilon, so every sample along the whole ray can still land within
    /// it. Past this the accept floor is unrepresentable and the cone proofs would rest on rounding.</summary>
    public const float MaxFarDistance = 8192f;

    /// <summary>Gets the inert absence — shadows off, no crowd radius, no ambient occlusion, native scale, no
    /// authored presets, the engine's pinned far distance. The engine holds no render posture of its own: the
    /// standard boot levers and preset table are AUTHORED, in <c>Assets/worlds/standard.world.json</c>, and a world
    /// inherits them by naming that document as its basis.</summary>
    public static WorldRenderDefaults Absent { get; } = new WorldRenderDefaults();

    /// <summary>Returns the authored preset for a quality tier keyword (case-insensitive
    /// <c>low</c>/<c>medium</c>/<c>high</c>), or <see langword="null"/> when the token names none or the world
    /// authors no such preset — the <c>world.quality</c> verb refuses by name either way.</summary>
    /// <param name="name">The quality tier keyword.</param>
    /// <returns>The matching authored preset, or <see langword="null"/>.</returns>
    public WorldQualityPreset? Preset(string name) {
        return (name.ToUpperInvariant() switch {
            "LOW" => LowRaw,
            "MEDIUM" => MediumRaw,
            "HIGH" => HighRaw,
            _ => null,
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
/// <summary>The lit path's lights and stylization as world data. Absent renders the pinned sun and hemisphere an
/// unauthored world always had; present, the list IS the lights — an authored list without a hemisphere has no
/// ambient. Every field of every light is optional individually and resolves to the engine's pinned default for its
/// kind.</summary>
/// <param name="Lights">The lights, at most <c>SdfEnvironment.MaxLights</c>, in slot order (a <c>render.cycle</c> key
/// moves a light by its slot). At most one directional may shadow: the soft-shadow march runs once per lit
/// pixel.</param>
/// <param name="Curvature">The stylized curvature enrichment — cavity darkening, curvature rim light, and an ink
/// outline. Optional; absent (and all-zero) shades exactly as a world that declares none.</param>
public sealed record WorldRenderLighting(IReadOnlyList<WorldRenderLight>? Lights = null, WorldRenderCurvature? Curvature = null) {
    /// <summary>The topology an absent <c>render.lighting</c> resolves to — the pinned shadowing sun and the pinned
    /// hemisphere — which a <c>render.cycle</c> key over unauthored lighting is validated against.</summary>
    public static WorldRenderLighting Pinned { get; } = new(Lights: [
        new WorldRenderLight.Directional(Shadows: true),
        new WorldRenderLight.Hemisphere(),
    ]);
}
/// <summary>One light. The <c>$type</c> string is the JSON discriminator; a new kind is a new derived record, its
/// <see cref="JsonDerivedTypeAttribute"/> line, and its lane semantics in <c>SdfEnvironment</c>.</summary>
[JsonDerivedType(typeof(WorldRenderLight.Directional), typeDiscriminator: "directional")]
[JsonDerivedType(typeof(WorldRenderLight.Hemisphere), typeDiscriminator: "hemisphere")]
[JsonDerivedType(typeof(WorldRenderLight.Rim), typeDiscriminator: "rim")]
[JsonDerivedType(typeof(WorldRenderLight.Point), typeDiscriminator: "point")]
[JsonDerivedType(typeof(WorldRenderLight.Occluder), typeDiscriminator: "occluder")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldRenderLight {
    private WorldRenderLight() {
    }

    /// <summary>A Lambert directional light.</summary>
    /// <param name="Direction">The direction from a lit surface toward the light, any nonzero length (normalized
    /// host-side before upload). Absent is the pinned sun direction.</param>
    /// <param name="Color">The light's linear colour.</param>
    /// <param name="Weight">The diffuse weight. Absent is the pinned sun weight.</param>
    /// <param name="AngularRadius">The light's angular radius in radians, in <c>[0, atan 0.3]</c>: the penumbra
    /// half-slope is its tangent, so 0 casts a hard shadow. Read only when the light shadows. Absent is the pinned
    /// penumbra.</param>
    /// <param name="Shadows">Whether this light drives the soft-shadow march (at most one light per world). Absent is
    /// <see langword="false"/>. An unshadowed directional is scaled by ambient occlusion instead.</param>
    public sealed record Directional(
        DocumentVector3? Direction = null,
        BindableColor? Color = null,
        float? Weight = null,
        float? AngularRadius = null,
        bool? Shadows = null
    ) : WorldRenderLight;
    /// <summary>A hemisphere ambient: a floor plus a gradient on the surface normal's Y (sky above, darker below),
    /// scaled by ambient occlusion.</summary>
    /// <param name="Color">The ambient's linear colour.</param>
    /// <param name="Base">The floor. Absent is the pinned ambient floor.</param>
    /// <param name="Gradient">The hemisphere gradient. Absent is the pinned gradient.</param>
    public sealed record Hemisphere(
        BindableColor? Color = null,
        float? Base = null,
        float? Gradient = null
    ) : WorldRenderLight;
    /// <summary>A view-dependent silhouette brighten: <c>weight · color · pow(1 − saturate(dot(normal,
    /// −rayDirection)), power)</c>, added after the material shade.</summary>
    /// <param name="Color">The rim's linear colour.</param>
    /// <param name="Weight">The strength. Absent is zero, which adds nothing.</param>
    /// <param name="Power">The falloff exponent — larger confines the highlight nearer the silhouette. Absent is the
    /// engine default.</param>
    public sealed record Rim(
        BindableColor? Color = null,
        float? Weight = null,
        float? Power = null
    ) : WorldRenderLight;
    /// <summary>A point light with inverse-square falloff and a soft core:
    /// <c>intensity = weight / (1 + (distance / radius)^2)</c>. No shadow march in v1 — a point light never occludes
    /// and is never occluded.</summary>
    /// <param name="Position">The world-space position for a static (unanchored) light. Absent is the world origin.
    /// An offset in the anchor frame when an anchor is authored.</param>
    /// <param name="Radius">The falloff radius. Absent is the engine default.</param>
    /// <param name="Anchor">An entity, entity part, or placement frame. A missing live target disables the light.</param>
    /// <param name="Color">The light's linear colour.</param>
    /// <param name="Weight">The strength. Absent is the engine default.</param>
    public sealed record Point(
        DocumentVector3? Position = null,
        float? Radius = null,
        WorldAnchor? Anchor = null,
        BindableColor? Color = null,
        float? Weight = null
    ) : WorldRenderLight;
    /// <summary>A smooth attenuation field. Position is world space, or an offset in an anchored entity/part/placement
    /// frame. Missing anchors disable it. Radius is positive; Weight is in [0, 1]. It shares the eight-light capacity.</summary>
    public sealed record Occluder(DocumentVector3? Position = null, float? Radius = null, WorldAnchor? Anchor = null, float? Weight = null) : WorldRenderLight;
}
/// <summary>The stylized curvature enrichment, keyed on the level-set mean curvature the lit path already measures
/// at each hit. Every field is optional individually — absent resolves to the engine's pinned default. The three
/// gains share one runtime gate: while all of them are zero the renderer skips the extra field tap the curvature
/// normal needs, so an unauthored world pays nothing.</summary>
/// <param name="Cavity">How far a concave crease darkens, in [0, 1] at a cavity whose curvature reaches
/// <paramref name="InkLow"/>. Zero darkens none.</param>
/// <param name="Rim">How far a convex ridge brightens, in [0, 1] at a ridge whose curvature reaches
/// <paramref name="InkLow"/>. Zero brightens none.</param>
/// <param name="Ink">The ink outline's strength where the curvature magnitude spikes. Zero draws none.</param>
/// <param name="InkLow">The curvature magnitude (1 / fillet radius, in world units) at which the outline starts and
/// the ridge and cavity terms saturate.</param>
/// <param name="InkHigh">The curvature magnitude at which the outline saturates.</param>
/// <param name="InkColor"><see cref="BindableColor"/>'s grammar: the outline colour.</param>
public sealed record WorldRenderCurvature(float? Cavity = null, float? Rim = null, float? Ink = null, float? InkLow = null, float? InkHigh = null, BindableColor? InkColor = null);
/// <summary>The procedural sky as an ordered stack of layers. Absent is a hard gate: the world renders the pinned
/// two-stop gradient and fog density, as before this section existed. The layers composite in a fixed order —
/// gradient, stars, sun disc, clouds — whatever order they are authored in; fog is read every frame on its own. A
/// layer kind appears at most once.</summary>
/// <param name="Layers">The layers.</param>
public sealed record WorldRenderSky(IReadOnlyList<WorldRenderSkyLayer>? Layers = null) {
    /// <summary>The topology an absent <c>render.sky</c> resolves to — the pinned two-stop gradient and the pinned
    /// fog — which a <c>render.cycle</c> key over an unauthored sky is validated against.</summary>
    public static WorldRenderSky Pinned { get; } = new(Layers: [
        new WorldRenderSkyLayer.Gradient(Stops: [
            new WorldRenderSkyStop(Elevation: -1f),
            new WorldRenderSkyStop(Elevation: 1f),
        ]),
        new WorldRenderSkyLayer.Fog(),
    ]);
}
/// <summary>One sky layer. The <c>$type</c> string is the JSON discriminator.</summary>
[JsonDerivedType(typeof(WorldRenderSkyLayer.Gradient), typeDiscriminator: "gradient")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Fog), typeDiscriminator: "fog")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.SunDisc), typeDiscriminator: "sunDisc")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Stars), typeDiscriminator: "stars")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Clouds), typeDiscriminator: "clouds")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldRenderSkyLayer {
    private WorldRenderSkyLayer() {
    }

    /// <summary>The colour gradient over elevation: piecewise-linear between stops, clamped to the end stops.</summary>
    /// <param name="Stops">Two to <c>SdfEnvironment.MaxSkyStops</c> stops, strictly ascending in elevation. A
    /// <c>render.cycle</c> key moves a stop by its index and may not add or remove one.</param>
    public sealed record Gradient(IReadOnlyList<WorldRenderSkyStop>? Stops = null) : WorldRenderSkyLayer;
    /// <summary>The exponential distance fog fading toward the sky gradient.</summary>
    /// <param name="Density">The density per world unit. Absent is the pinned density.</param>
    public sealed record Fog(float? Density = null) : WorldRenderSkyLayer;
    /// <summary>The visible sun disc — an additive highlight about one directional light's direction.</summary>
    /// <param name="Light">The <see cref="WorldRenderLighting.Lights"/> slot of a directional light. Absent is the
    /// shadow light, or the first directional when none shadows.</param>
    /// <param name="Radius">The disc's angular half-radius in radians, in <c>(0, π/2]</c>. Absent is the engine
    /// default.</param>
    /// <param name="Intensity">The peak additive brightness. Absent is zero, which draws nothing.</param>
    public sealed record SunDisc(int? Light = null, float? Radius = null, float? Intensity = null) : WorldRenderSkyLayer;
    /// <summary>The procedural star field: a deterministic per-cell hash over an octahedral sky projection.</summary>
    /// <param name="Density">The star grid's cell count per octahedral axis. Absent is the engine default.</param>
    /// <param name="Brightness">The peak per-star brightness. Absent is zero, which draws nothing.</param>
    /// <param name="Seed">The hash seed folded into every cell.</param>
    /// <param name="Twinkle">Scintillation for a share of the stars. Optional; absent twinkles none.</param>
    public sealed record Stars(float? Density = null, float? Brightness = null, uint? Seed = null, WorldRenderSkyTwinkle? Twinkle = null) : WorldRenderSkyLayer;
    /// <summary>The procedural cloud layer: a deterministic hashed-lattice noise on a plane above the camera,
    /// thresholded by coverage, drawn over the gradient, stars and sun disc and fading into the horizon.</summary>
    /// <param name="Coverage">The fraction of the sky the layer covers, in <c>[0, 1]</c>. Absent is zero.</param>
    /// <param name="Softness">The width of a cloud's edge, in <c>(0, 1]</c>. Absent is the engine default.</param>
    /// <param name="Scale">The size of one cloud cell in layer units (the layer sits at unit height). Absent is the
    /// engine default.</param>
    /// <param name="Seed">The hash seed folded into the lattice.</param>
    /// <param name="Color"><see cref="BindableColor"/>'s grammar: the cloud colour. Absent is white.</param>
    /// <param name="Drift">The layer's wind, in layer units per second along world X and Z, integrated on the tick
    /// clock. Absent holds still.</param>
    /// <param name="Spin">The layer's rotation about the zenith in radians per second; positive is counter-clockwise
    /// seen from below. Absent is none.</param>
    /// <param name="Curl">The Coriolis twist in radians at 45° elevation, falling off toward the horizon and the
    /// zenith. Positive winds counter-clockwise. Absent is none.</param>
    /// <param name="Shear">The wind of the shaping field relative to the cloud field, in layer units per second.
    /// Absent holds the shapes.</param>
    public sealed record Clouds(
        float? Coverage = null,
        float? Softness = null,
        float? Scale = null,
        uint? Seed = null,
        BindableColor? Color = null,
        DocumentVector2? Drift = null,
        float? Spin = null,
        float? Curl = null,
        DocumentVector2? Shear = null
    ) : WorldRenderSkyLayer;
}
/// <summary>One gradient stop.</summary>
/// <param name="Elevation">The direction's Y component this stop sits at, in <c>[−1, 1]</c>.</param>
/// <param name="Color"><see cref="BindableColor"/>'s grammar: the colour at this elevation. Absent (in a cycle key)
/// keeps the previous key's colour.</param>
public sealed record WorldRenderSkyStop(float? Elevation = null, BindableColor? Color = null);
/// <summary>Scintillation: a hash-chosen share of the stars dip and recover on the simulation clock, each at its own
/// harmonic and phase of one authored rate, so no two twinkle in step. Presentation-only, keyed on the tick.</summary>
/// <param name="Share">The fraction of stars that twinkle, in <c>[0, 1]</c>. Zero twinkles none.</param>
/// <param name="Depth">How far a twinkling star dips below its steady brightness, in <c>[0, 1]</c>.</param>
/// <param name="Rate">The fundamental scintillation rate in hertz.</param>
public sealed record WorldRenderSkyTwinkle(float? Share = null, float? Depth = null, float? Rate = null);
/// <summary>Lighting and sky as a function of a state row: presentation reads the row's live value each frame, takes
/// its fractional part (an advancing clock wraps once per unit), and interpolates between the two keys that bracket
/// it — every environment lane by its own kind: colours and scalars linearly, directions along the arc, counts, kinds,
/// seeds and flags held from the earlier key. A key states only the fields it moves; every other field holds its
/// value from the previous key (the first key starts from the static <see cref="WorldRenderDefaults.Lighting"/>/
/// <see cref="WorldRenderDefaults.Sky"/>, and the last key wraps into the first). A key addresses a light by its
/// slot and a stop by its index, with the same kind the statics author there, and may not add or remove either.
/// Presentation-only: the row is simulation state, the interpolation is not.</summary>
/// <param name="State">The state row read (its slot cell; <c>Fixed</c> or <c>Int</c>).</param>
/// <param name="Keys">At least two keys, strictly ascending <see cref="WorldRenderCycleKey.At"/> in <c>[0, 1)</c>.</param>
public sealed record WorldRenderCycle(string State, IReadOnlyList<WorldRenderCycleKey> Keys);
/// <summary>One point on a <see cref="WorldRenderCycle"/>.</summary>
/// <param name="At">The row-value fraction this key sits at, in <c>[0, 1)</c>.</param>
/// <param name="Lighting">The lighting fields this key moves, or <see langword="null"/>.</param>
/// <param name="Sky">The sky fields this key moves, or <see langword="null"/>.</param>
public sealed record WorldRenderCycleKey(float At, WorldRenderLighting? Lighting = null, WorldRenderSky? Sky = null);
/// <summary>The tonemap applied to the frame's final color — see <see cref="WorldRenderDefaults.Tonemap"/>.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldTonemap>))]
public enum WorldTonemap {
    /// <summary>No remap: the stylized shaded color, as every world rendered before this field existed.</summary>
    None = 0,
    /// <summary>A filmic (ACES-fit) curve on the frame's final color.</summary>
    Filmic = 1,
}
/// <summary>The analytic studio reflections a GGX specular lobe reflects — see
/// <see cref="WorldRenderDefaults.Environment"/>.</summary>
/// <param name="Softboxes">The reflection softboxes, at most <c>SdfEnvironment.MaxSoftboxes</c>. Absent or empty
/// contributes nothing.</param>
/// <param name="Horizon">The reflection horizon gradient. Absent is black — contributes nothing.</param>
public sealed record WorldRenderEnvironment(IReadOnlyList<WorldRenderSoftbox>? Softboxes = null, WorldRenderHorizon? Horizon = null);
/// <summary>One analytic studio-reflection softbox: a soft, angularly-extended highlight a GGX lobe catches in its
/// mirror direction, widened by the surface's own roughness.</summary>
/// <param name="Direction">From a reflecting surface toward the softbox, any nonzero length (normalized before
/// upload).</param>
/// <param name="Size">The angular half-extent (width, height) the falloff widens by, both strictly positive.</param>
/// <param name="Color"><see cref="BindableColor"/>'s grammar: the softbox's linear colour. Absent is white.</param>
/// <param name="Weight">The strength. Absent is 1.</param>
/// <param name="Blur">Additional falloff softening, in the same units as <paramref name="Size"/>. Absent is
/// 0.</param>
public sealed record WorldRenderSoftbox(DocumentVector3 Direction, DocumentVector2 Size, BindableColor? Color = null, float? Weight = null, float? Blur = null);
/// <summary>The studio reflection's horizon gradient — the reflection direction's Y interpolates between
/// <paramref name="Low"/> and <paramref name="High"/>.</summary>
/// <param name="Low">The ground-ward (direction.y = −1) colour. Absent is black.</param>
/// <param name="High">The sky-ward (direction.y = 1) colour. Absent is black.</param>
public sealed record WorldRenderHorizon(BindableColor? Low = null, BindableColor? High = null);
