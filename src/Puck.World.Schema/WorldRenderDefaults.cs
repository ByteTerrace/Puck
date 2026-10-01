using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Presentation;
using Puck.Assets.Documents;

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

            // By index: a per-frame caller reads this, and an interface enumerator would allocate.
            if (Anchors is { } anchors) {
                for (var index = 0; (index < anchors.Count); index++) {
                    if (anchors[index]?.Anchor is WorldAnchor.Seat) {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
public static class WorldApplicationDefaults {
    /// <summary>The built-in world ships with no bundled AGB cartridge — an asset-free default, never an owner-local
    /// absolute path or a copyrighted dump. Durable per-deployment cartridge/BIOS paths belong in the world data file
    /// (the "durable config lives in the data file" doctrine); the <c>puck.world.definition.v1</c> loader
    /// (<c>Puck.World.WorldDefinitionLoader</c>) reads one, but the checked-in default file authors an empty content
    /// path, so the native-AGB screen boots unconfigured (a graceful fault, never a crash) until a real deployment
    /// supplies a named <see cref="WorldScreenSource.Machine"/> output.</summary>
    public const string DefaultAgbCartridgePath = "";
    public const string WindowTitle = "Puck: World";
}
/// <summary>One graphics-quality preset — the bundle of render levers the <c>world.quality</c> verb writes for a named
/// tier (the individual <c>world.shadows</c>/<c>.ao</c>/<c>.render-scale</c> verbs still override afterward).</summary>
/// <param name="Shadows">The soft-shadow tier the preset selects.</param>
/// <param name="AmbientOcclusion">Whether the preset enables ambient occlusion.</param>
/// <param name="RenderScale">The render-scale tier the preset selects.</param>
/// <param name="Temporal">Whether the preset reconstructs the world's views over time (<c>world.temporal</c>).</param>
public readonly record struct WorldQualityPreset(
    ShadowTier Shadows,
    bool AmbientOcclusion,
    WorldRenderScaleTier RenderScale,
    bool Temporal = false
);
/// <summary>The world's render-lever defaults — the boot values <c>Puck.World.WorldRenderSettings</c> wakes on and the
/// <c>world.quality</c> preset table. Session state, not identity: these are engine-wide levers (shadows, AO, render
/// scale, the crowd radius), the graphics-menu defaults a server-pulled world would carry.</summary>
/// <param name="Shadows">The boot soft-shadow tier.</param>
/// <param name="ShadowCrowdRadius">The boot soft-shadow crowd radius (world units).</param>
/// <param name="AmbientOcclusion">Whether ambient occlusion boots on.</param>
/// <param name="RenderScale">The boot render-scale tier.</param>
/// <param name="UpscaleSharpness">The boot reconstruction sharpness: the spatial resolve's blend (0 bilinear .. 1
/// Catmull-Rom) and the strength of the sharpen a temporally resolved view gets at its rect's own extent.</param>
/// <param name="Temporal">Whether the world's own views boot reconstructing over time (<c>world.temporal</c>): each
/// jitters its samples and resolves them over its history, native or reduced. Camera and session views never do.</param>
/// <param name="LowRaw">The <c>world.quality low</c> preset.</param>
/// <param name="MediumRaw">The <c>world.quality medium</c> preset.</param>
/// <param name="HighRaw">The <c>world.quality high</c> preset.</param>
/// <param name="Lighting">The scene's directional sun and ambient term. Optional, and every field within it is
/// optional individually — an absent section, or an absent field within it, resolves to <c>SdfFrame</c>'s pinned
/// default for that field, so a world renders unchanged until it authors one.</param>
/// <param name="Sky">The procedural sky — a gradient, sun disc, star field, and distance fog. Optional; an absent
/// section renders the pinned two-stop gradient and 0.015 fog density bit-exactly, as before this section
/// existed.</param>
/// <param name="Environment">The analytic studio-reflection softboxes and horizon gradient a GGX specular lobe
/// reflects. Optional; absent (no softboxes, a black horizon) contributes nothing to the shaded color.</param>
/// <param name="Tonemap">The tonemap the root graph applies to the SDF scene: each view, as its place pass reconstructs
/// it. The letterbox color, every pane (display-referred) and the HUD are never tonemapped. Optional; absent is
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
    bool Temporal = false,
    [property: JsonPropertyName("low"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? LowRaw = null,
    [property: JsonPropertyName("medium"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? MediumRaw = null,
    [property: JsonPropertyName("high"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? HighRaw = null,
    WorldRenderLighting? Lighting = null,
    WorldRenderSky? Sky = null,
    WorldRenderEnvironment? Environment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldTonemap? Tonemap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? FarDistance = null
) {
    /// <summary>The largest <see cref="FarDistance"/> the validator admits: 8192 world units. The march advances a
    /// float depth against a 0.001-unit surface epsilon; 8192 is the largest power of two at which a float's spacing
    /// (2^13 · 2^-23 = 0.00098) still resolves that epsilon, so every sample along the whole ray can still land within
    /// it. Past this the accept floor is unrepresentable and the cone proofs would rest on rounding.</summary>
    public const float MaxFarDistance = 8192f;
    /// <summary>The smallest <see cref="FarDistance"/> the validator admits: one world unit. The beam's cone march
    /// starts at 0.02 units and the fine march accepts a hit within a 0.001-unit floor, so a far plane under one unit
    /// leaves no marchable depth a camera could frame a body in.</summary>
    public const float MinFarDistance = 1f;

    /// <summary>Gets the inert absence — shadows off, no crowd radius, no ambient occlusion, native scale, no
    /// authored presets, the engine's pinned far distance. The engine holds no render posture of its own: a world
    /// authors its boot levers and its preset table in its own <c>render</c> section, or inherits them from its
    /// basis or an import; the shipped worlds share one preset table, <c>Assets/worlds/quality.puck</c>.</summary>
    public static WorldRenderDefaults Absent { get; } = new WorldRenderDefaults();

    /// <summary>Returns the authored preset for a quality tier, or <see langword="null"/> when the world authors none
    /// for it, which the <c>world.quality</c> verb refuses by name. The tiers are the engine's one quality vocabulary
    /// (<see cref="QualityTiers"/>), the one a <c>views.graphs</c> row and a shader package's variants name.</summary>
    /// <param name="tier">The quality tier.</param>
    /// <returns>The matching authored preset, or <see langword="null"/>.</returns>
    public WorldQualityPreset? Preset(QualityTier tier) {
        return (tier switch {
            QualityTier.Low => LowRaw,
            QualityTier.Medium => MediumRaw,
            QualityTier.High => HighRaw,
            _ => null,
        });
    }
}
/// <summary>The lit path's lights and stylization as world data. Absent renders the pinned sun and hemisphere an
/// unauthored world always had; present, the list IS the lights — an authored list without a hemisphere has no
/// ambient. Every field of every light is optional individually and resolves to the engine's pinned default for its
/// kind. Every value a light or the curvature carries may be keyed on a clock on its own; the section may instead be
/// keyed whole (<paramref name="Clock"/>, <paramref name="Keys"/>), each key a partial record addressing lights by
/// name.</summary>
/// <param name="Lights">The lights, at most <c>SdfLights.MaxLights</c>, in slot order. At most one directional
/// may shadow: the soft-shadow march runs once per lit pixel.</param>
/// <param name="Curvature">The stylized curvature enrichment — cavity darkening, curvature rim light, and an ink
/// outline. Optional; absent (and all-zero) shades exactly as a world that declares none.</param>
/// <param name="Clock">The clock the section's keys read, by name in the <c>timeline</c> section. Required with
/// <paramref name="Keys"/> and refused without them.</param>
/// <param name="Keys">The section's keys, ascending in time. A field a key states is keyed on
/// <paramref name="Clock"/> through the keys that state it, blended by the field's type and eased by each key's ease;
/// a field no key states keeps its authored value.</param>
public sealed record WorldRenderLighting(
    IReadOnlyList<WorldRenderLight>? Lights = null,
    WorldRenderCurvature? Curvature = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Clock = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldRenderLightingKey>? Keys = null
) {
    /// <summary>The topology an absent <c>render.lighting</c> resolves to — the pinned shadowing sun and the pinned
    /// hemisphere.</summary>
    public static WorldRenderLighting Pinned { get; } = new(Lights: [
        new WorldRenderLight.Directional(Shadows: true),
        new WorldRenderLight.Hemisphere(),
    ]);
}
/// <summary>One key of <see cref="WorldRenderLighting.Keys"/>: a partial record of the section at one time.</summary>
/// <param name="At">Where on the section's clock the key sits, in the clock's span units, in <c>[0, span)</c>.</param>
/// <param name="Ease">How time eases from this key to the next key that states each field. Absent is
/// <see cref="WorldEase.Linear"/>.</param>
/// <param name="Lights">The lights this key moves, by name: each the same kind as the light of that name, stating only
/// the fields it moves. A light's name and shadowing are structure, which a key never states.</param>
/// <param name="Curvature">The curvature fields this key moves.</param>
public sealed record WorldRenderLightingKey(
    double At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEase? Ease = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorldRenderLight>? Lights = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderCurvature? Curvature = null
);
/// <summary>One light. The <c>$type</c> string is the JSON discriminator; a new kind is a new derived record, its
/// <see cref="JsonDerivedTypeAttribute"/> line, and its kind in <c>SdfLightKind</c>.</summary>
[JsonDerivedType(typeof(WorldRenderLight.Directional), typeDiscriminator: "directional")]
[JsonDerivedType(typeof(WorldRenderLight.Hemisphere), typeDiscriminator: "hemisphere")]
[JsonDerivedType(typeof(WorldRenderLight.Rim), typeDiscriminator: "rim")]
[JsonDerivedType(typeof(WorldRenderLight.Point), typeDiscriminator: "point")]
[JsonDerivedType(typeof(WorldRenderLight.Occluder), typeDiscriminator: "occluder")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldRenderLight {
    private WorldRenderLight() {
    }

    /// <summary>Gets the light's name, which a section key addresses it by, or <see langword="null"/> for an unnamed
    /// light no key can address.</summary>
    [JsonIgnore]
    public abstract string? LightName { get; }

    /// <summary>A Lambert directional light.</summary>
    /// <param name="Direction">The direction from a lit surface toward the light, any nonzero length (normalized
    /// host-side before upload). Absent is the pinned sun direction.</param>
    /// <param name="Color">The light's colour.</param>
    /// <param name="Weight">The diffuse weight. Absent is the pinned sun weight.</param>
    /// <param name="AngularRadius">The light's angular radius, in <c>[0, atan 0.3]</c> radians: the penumbra
    /// half-slope is its tangent, so 0 casts a hard shadow. Read only when the light shadows. Absent is the pinned
    /// penumbra.</param>
    /// <param name="Shadows">Whether this light drives the soft-shadow march (at most one light per world). Absent is
    /// <see langword="false"/>. An unshadowed directional is scaled by ambient occlusion instead.</param>
    /// <param name="Name">The name a section key addresses the light by, unique among the lights.</param>
    public sealed record Directional(
        BindableDirection? Direction = null,
        BindableColor? Color = null,
        BindableScalar? Weight = null,
        BindableAngle? AngularRadius = null,
        bool? Shadows = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderLight {
        /// <inheritdoc/>
        public override string? LightName => Name;
    }
    /// <summary>A hemisphere ambient: a floor plus a gradient on the surface normal's Y (sky above, darker below),
    /// scaled by ambient occlusion.</summary>
    /// <param name="Color">The ambient's colour.</param>
    /// <param name="Base">The floor. Absent is the pinned ambient floor.</param>
    /// <param name="Gradient">The hemisphere gradient. Absent is the pinned gradient.</param>
    /// <param name="Name">The name a section key addresses the light by, unique among the lights.</param>
    public sealed record Hemisphere(
        BindableColor? Color = null,
        BindableScalar? Base = null,
        BindableScalar? Gradient = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderLight {
        /// <inheritdoc/>
        public override string? LightName => Name;
    }
    /// <summary>A view-dependent silhouette brighten: <c>weight · color · pow(1 − saturate(dot(normal,
    /// −rayDirection)), power)</c>, added after the material shade.</summary>
    /// <param name="Color">The rim's colour.</param>
    /// <param name="Weight">The strength. Absent is zero, which adds nothing.</param>
    /// <param name="Power">The falloff exponent — larger confines the highlight nearer the silhouette. Absent is the
    /// engine default.</param>
    /// <param name="Name">The name a section key addresses the light by, unique among the lights.</param>
    public sealed record Rim(
        BindableColor? Color = null,
        BindableScalar? Weight = null,
        BindableScalar? Power = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderLight {
        /// <inheritdoc/>
        public override string? LightName => Name;
    }
    /// <summary>A point light with inverse-square falloff and a soft core:
    /// <c>intensity = weight / (1 + (distance / radius)^2)</c>. No shadow march in v1 — a point light never occludes
    /// and is never occluded.</summary>
    /// <param name="Position">The world-space position for a static (unanchored) light. Absent is the world origin.
    /// An offset in the anchor frame when an anchor is authored.</param>
    /// <param name="Radius">The falloff radius. Absent is the engine default.</param>
    /// <param name="Anchor">An entity, entity part, or placement frame. A missing live target disables the light.</param>
    /// <param name="Color">The light's colour.</param>
    /// <param name="Weight">The strength. Absent is the engine default.</param>
    /// <param name="Name">The name a section key addresses the light by, unique among the lights.</param>
    public sealed record Point(
        BindableVector3? Position = null,
        BindableScalar? Radius = null,
        WorldAnchor? Anchor = null,
        BindableColor? Color = null,
        BindableScalar? Weight = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderLight {
        /// <inheritdoc/>
        public override string? LightName => Name;
    }
    /// <summary>A smooth attenuation field. Position is world space, or an offset in an anchored entity/part/placement
    /// frame. Missing anchors disable it. Radius is positive; Weight is in [0, 1]. It shares the eight-light
    /// capacity.</summary>
    /// <param name="Position">The world-space position, or the offset in the anchor's frame.</param>
    /// <param name="Radius">The field's radius, positive.</param>
    /// <param name="Anchor">An entity, entity part, or placement frame.</param>
    /// <param name="Weight">The attenuation, in <c>[0, 1]</c>.</param>
    /// <param name="Name">The name a section key addresses the light by, unique among the lights.</param>
    public sealed record Occluder(
        BindableVector3? Position = null,
        BindableScalar? Radius = null,
        WorldAnchor? Anchor = null,
        BindableScalar? Weight = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderLight {
        /// <inheritdoc/>
        public override string? LightName => Name;
    }
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
public sealed record WorldRenderCurvature(
    BindableScalar? Cavity = null,
    BindableScalar? Rim = null,
    BindableScalar? Ink = null,
    BindableScalar? InkLow = null,
    BindableScalar? InkHigh = null,
    BindableColor? InkColor = null
);
/// <summary>The procedural sky as an ordered stack of layers. Absent is a hard gate: the world renders the pinned
/// two-stop gradient and fog density, as before this section existed. The layers composite in a fixed order —
/// gradient, stars, sun disc, clouds — whatever order they are authored in; fog is read every frame on its own. A
/// layer kind appears at most once. Every value a layer carries may be keyed on a clock on its own; the section may
/// instead be keyed whole (<paramref name="Clock"/>, <paramref name="Keys"/>), each key a partial record addressing
/// layers by name.</summary>
/// <param name="Layers">The layers.</param>
/// <param name="Clock">The clock the section's keys read, by name in the <c>timeline</c> section. Required with
/// <paramref name="Keys"/> and refused without them.</param>
/// <param name="Keys">The section's keys, ascending in time. A field a key states is keyed on
/// <paramref name="Clock"/> through the keys that state it; a field no key states keeps its authored value.</param>
public sealed record WorldRenderSky(
    IReadOnlyList<WorldRenderSkyLayer>? Layers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Clock = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldRenderSkyKey>? Keys = null
);
/// <summary>One key of <see cref="WorldRenderSky.Keys"/>: a partial record of the sky at one time.</summary>
/// <param name="At">Where on the section's clock the key sits, in the clock's span units, in <c>[0, span)</c>.</param>
/// <param name="Ease">How time eases from this key to the next key that states each field. Absent is
/// <see cref="WorldEase.Linear"/>.</param>
/// <param name="Layers">The layers this key moves, by name: each the same kind as the layer of that name, stating only
/// the fields it moves. A gradient states every stop the layer has, in order. Counts, seeds, a layer's name and the
/// sun disc's light are structure, which a key never states.</param>
public sealed record WorldRenderSkyKey(
    double At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEase? Ease = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorldRenderSkyLayer>? Layers = null
);
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

    /// <summary>Gets the layer's name, which a section key addresses it by, or <see langword="null"/> for an unnamed
    /// layer no key can address.</summary>
    [JsonIgnore]
    public abstract string? LayerName { get; }

    /// <summary>The colour gradient over elevation: piecewise-linear between stops, clamped to the end stops.</summary>
    /// <param name="Stops">Two to <c>SdfSky.MaxStops</c> stops, strictly ascending in elevation.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    public sealed record Gradient(
        IReadOnlyList<WorldRenderSkyStop>? Stops = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>The exponential distance fog fading toward the sky gradient.</summary>
    /// <param name="Density">The density per world unit. Absent is the pinned density.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    public sealed record Fog(
        BindableScalar? Density = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>The visible sun disc — an additive highlight about one directional light's direction.</summary>
    /// <param name="Light">The <see cref="WorldRenderLighting.Lights"/> slot of a directional light. Absent is the
    /// shadow light, or the first directional when none shadows.</param>
    /// <param name="Radius">The disc's angular half-radius, in <c>(0, π/2]</c> radians. Absent is the engine
    /// default.</param>
    /// <param name="Intensity">The peak additive brightness. Absent is zero, which draws nothing.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    public sealed record SunDisc(
        int? Light = null,
        BindableAngle? Radius = null,
        BindableScalar? Intensity = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>The procedural star field: a deterministic per-cell hash over an octahedral sky projection.</summary>
    /// <param name="Density">The star grid's cell count per octahedral axis. Absent is the engine default.</param>
    /// <param name="Brightness">The peak per-star brightness. Absent is zero, which draws nothing.</param>
    /// <param name="Seed">The hash seed folded into every cell.</param>
    /// <param name="Twinkle">Scintillation for a share of the stars. Optional; absent twinkles none.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    public sealed record Stars(
        float? Density = null,
        BindableScalar? Brightness = null,
        uint? Seed = null,
        WorldRenderSkyTwinkle? Twinkle = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>The procedural cloud layer: a deterministic hashed-lattice noise on a plane above the camera,
    /// thresholded by coverage, drawn over the gradient, stars and sun disc and fading into the horizon.</summary>
    /// <param name="Coverage">The fraction of the sky the layer covers, in <c>[0, 1]</c>. Absent is zero.</param>
    /// <param name="Softness">The width of a cloud's edge, in <c>(0, 1]</c>. Absent is the engine default.</param>
    /// <param name="Scale">The size of one cloud cell in layer units (the layer sits at unit height). Absent is the
    /// engine default.</param>
    /// <param name="Seed">The hash seed folded into the lattice.</param>
    /// <param name="Color"><see cref="BindableColor"/>'s grammar: the cloud colour. Absent is white.</param>
    /// <param name="Drift">The layer's wind, in layer units per second along world X and Z, integrated on the tick
    /// clock. A rate: it keys only on a tick clock, and binds no state row. Absent holds still.</param>
    /// <param name="Spin">The layer's rotation about the zenith in radians per second; positive is counter-clockwise
    /// seen from below. A rate, as <paramref name="Drift"/> is. Absent is none.</param>
    /// <param name="Curl">The Coriolis twist at 45° elevation, in radians, falling off toward the horizon and the
    /// zenith. Positive winds counter-clockwise. Absent is none.</param>
    /// <param name="Shear">The wind of the shaping field relative to the cloud field, in layer units per second. A
    /// rate, as <paramref name="Drift"/> is. Absent holds the shapes.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    public sealed record Clouds(
        BindableScalar? Coverage = null,
        BindableScalar? Softness = null,
        BindableScalar? Scale = null,
        uint? Seed = null,
        BindableColor? Color = null,
        BindableVector2? Drift = null,
        BindableScalar? Spin = null,
        BindableAngle? Curl = null,
        BindableVector2? Shear = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
}
/// <summary>One gradient stop.</summary>
/// <param name="Elevation">The direction's Y component this stop sits at, in <c>[−1, 1]</c>.</param>
/// <param name="Color"><see cref="BindableColor"/>'s grammar: the colour at this elevation. Absent is white.</param>
public sealed record WorldRenderSkyStop(BindableScalar? Elevation = null, BindableColor? Color = null);
/// <summary>Scintillation: a hash-chosen share of the stars dip and recover on the simulation clock, each at its own
/// harmonic and phase of one authored rate, so no two twinkle in step. Presentation-only, keyed on the tick.</summary>
/// <param name="Share">The fraction of stars that twinkle, in <c>[0, 1]</c>. Zero twinkles none.</param>
/// <param name="Depth">How far a twinkling star dips below its steady brightness, in <c>[0, 1]</c>.</param>
/// <param name="Rate">The fundamental scintillation rate in hertz. A rate: it keys only on a tick clock, and binds no
/// state row.</param>
public sealed record WorldRenderSkyTwinkle(BindableScalar? Share = null, BindableScalar? Depth = null, BindableScalar? Rate = null);
/// <summary>The tonemap the root graph applies to the frame before the HUD — see
/// <see cref="WorldRenderDefaults.Tonemap"/>.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldTonemap>))]
public enum WorldTonemap {
    /// <summary>No remap: the stylized shaded color.</summary>
    None = 0,
    /// <summary>A filmic (ACES-fit) curve over each view, applied by the root graph's place pass for the view.</summary>
    Filmic = 1,
}
/// <summary>The analytic studio reflections a GGX specular lobe reflects — see
/// <see cref="WorldRenderDefaults.Environment"/>.</summary>
/// <param name="Softboxes">The reflection softboxes, at most <c>SdfSky.MaxSoftboxes</c>. Absent or empty
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
