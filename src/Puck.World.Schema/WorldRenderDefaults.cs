using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Presentation;
using Puck.Assets.Documents;
using Puck.SignedDistance;

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
/// tier (the individual <c>world.shadows</c>/<c>.ao</c>/<c>.render-scale</c> verbs still override afterward).
/// Omitted JSON members use these constructor defaults.</summary>
/// <param name="Shadows">The soft-shadow tier the preset selects.</param>
/// <param name="AmbientOcclusion">Whether the preset enables ambient occlusion.</param>
/// <param name="RenderScale">The scalar render-scale ceiling the preset selects.</param>
/// <param name="Temporal">Whether the preset reconstructs the world's views over time (<c>world.temporal</c>).</param>
/// <param name="ShadowAmortize">Whether secondary shadows reuse valid K history (<c>world.shadow-amortize</c>).</param>
/// <param name="DynamicResolution">Whether the preset moves each view's render extent with the load
/// (<c>world.render-scale auto</c>).</param>
/// <param name="RenderScaleFloor">The per-view floor tier the preset selects (Quarter by default).</param>
/// <param name="ShadowLights">The number of held shadow slots, from 0 through 4.</param>
/// <param name="ShadowFadeSlots">The number of additional shadow handoff slots, from 0 through 2.</param>
/// <param name="ShadowFadeTicks">The length of a shadow handoff in delivered ticks; zero selects instant changes.</param>
/// <param name="ShadowOverflow">How a crossing proceeds when its handoff capacity is occupied.</param>
/// <param name="Sky">The sky's quality tier the preset selects (<c>world.sky-quality</c>).</param>
/// <param name="Indirect">The indirect tier override. Absent selects Off for Low, Medium for Medium and High for High.</param>
/// <param name="SkyFieldScale">The independent sky field grid fraction, one or one half.</param>
[method: JsonConstructor]
public readonly record struct WorldQualityPreset(
    ShadowTier Shadows,
    bool AmbientOcclusion,
    float RenderScale,
    bool Temporal = false,
    bool ShadowAmortize = false,
    bool DynamicResolution = false,
    int ShadowLights = 0,
    int ShadowFadeSlots = 0,
    uint ShadowFadeTicks = 0,
    WorldShadowOverflow ShadowOverflow = WorldShadowOverflow.Instant,
    WorldRenderScaleTier RenderScaleFloor = WorldRenderScaleTier.Quarter,
    WorldSkyTier Sky = WorldSkyTier.High,
    [property: JsonConverter(typeof(StrictEnumConverter<SdfIndirectTier>)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SdfIndirectTier? Indirect = null,
    float SkyFieldScale = 1f
);
/// <summary>The world's render-lever defaults — the boot values <c>Puck.World.WorldRenderSettings</c> wakes on and the
/// <c>world.quality</c> preset table. Session state, not identity: these are engine-wide levers (shadows, AO, render
/// scale, the crowd radius), the graphics-menu defaults a server-pulled world would carry.</summary>
/// <param name="Shadows">The boot soft-shadow tier.</param>
/// <param name="ShadowCrowdRadius">The boot soft-shadow crowd radius (world units).</param>
/// <param name="AmbientOcclusion">Whether ambient occlusion boots on.</param>
/// <param name="RenderScale">The scalar boot render-scale ceiling in [0.125, 1].</param>
/// <param name="UpscaleSharpness">The boot reconstruction sharpness: the spatial resolve's blend (0 bilinear .. 1
/// Catmull-Rom) and the strength of the sharpen a temporally resolved view gets at its rect's own extent.</param>
/// <param name="Temporal">Whether the world's own views boot reconstructing over time (<c>world.temporal</c>): each
/// jitters its samples and resolves them over its history, native or reduced. Camera and session views never do.</param>
/// <param name="ShadowAmortize">Whether secondary shadows boot reusing valid K history (<c>world.shadow-amortize</c>).</param>
/// <param name="DynamicResolution">Whether views boot adapting their grids (<c>world.render-scale auto</c>).
/// Saved per-view quality and tier rows supply their floors. A native ceiling is lowered to three-quarter while
/// adaptation is enabled, since a native view reconstructs nothing.</param>
/// <param name="LowRaw">The <c>world.quality low</c> preset.</param>
/// <param name="MediumRaw">The <c>world.quality medium</c> preset.</param>
/// <param name="HighRaw">The <c>world.quality high</c> preset.</param>
/// <param name="Lighting">The scene's direct lights and curvature shading. Optional, and every field within it is
/// optional individually — an absent section, or an absent field within it, resolves to <c>SdfFrame</c>'s pinned
/// default for that field, so a world renders unchanged until it authors one.</param>
/// <param name="Sky">The repeatable sky-layer stack. Optional; an absent
/// section renders the default look, the two-stop gradient <c>SdfSky</c> starts from, as data the kernels
/// read like any authored sky.</param>
/// <param name="Atmosphere">Fog, height fog, haze and a medium between the camera and what it sees. An absent
/// section renders the default fog; an authored section contains exactly the kinds it states.</param>
/// <param name="Environment">The sky's bindable ambient and reflection gains, each defaulting to one.
/// Zero disables that lighting contribution.</param>
/// <param name="Tonemap">The tonemap the root graph applies to the SDF scene: each view, as its place pass reconstructs
/// it. The letterbox color, every pane (display-referred) and the HUD are never tonemapped. Optional; absent is
/// <see cref="WorldTonemap.None"/> — the stylized shaded color, unchanged.</param>
/// <param name="FarDistance">The far distance in world units: the depth at which every camera march ends — the far
/// plane the renderer's fine march exits at, the reach of the beam's cone proofs, and the depth the fog and depth
/// ramps are measured against. Geometry beyond it is never marched, so an infinite plane ends on a visible horizon
/// curve at this depth unless the atmosphere absorbs it (<c>render.atmosphere</c>). Optional; absent
/// resolves to the engine's pinned 40 — exactly the value every world marched to before this field existed. Must
/// lie within [<see cref="MinFarDistance"/>, <see cref="MaxFarDistance"/>]. Re-read on every definition revision
/// (a <c>world.row.set render</c> lands on the next frame); <c>world.budget</c> echoes it with its derived
/// costs.</param>
/// <param name="ShadowLights">The boot number of held shadow slots, from 0 through 4; one keeps the pinned sun selected.</param>
/// <param name="ShadowFadeSlots">The boot number of additional shadow handoff slots, from 0 through 2.</param>
/// <param name="ShadowFadeTicks">The boot length of a shadow handoff in delivered ticks; zero selects instant changes.</param>
/// <param name="ShadowOverflow">How a crossing proceeds when its handoff capacity is occupied.</param>
/// <param name="SkyQuality">The sky's boot quality tier (<c>world.sky-quality</c>): a layer below it writes no entry, and
/// below <see cref="WorldSkyTier.High"/> each kind draws its reduced form.</param>
/// <param name="Indirect">The world's diffuse indirect-light participation defaults.</param>
/// <param name="SkyFieldScale">The boot sky field grid fraction, one or one half, independent of view render scale.</param>
public sealed record WorldRenderDefaults(
    ShadowTier Shadows = ShadowTier.Off,
    float ShadowCrowdRadius = 0f,
    bool AmbientOcclusion = false,
    float RenderScale = 1f,
    float UpscaleSharpness = 0f,
    bool Temporal = false,
    bool ShadowAmortize = false,
    bool DynamicResolution = false,
    [property: JsonPropertyName("low"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? LowRaw = null,
    [property: JsonPropertyName("medium"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? MediumRaw = null,
    [property: JsonPropertyName("high"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? HighRaw = null,
    WorldRenderLighting? Lighting = null,
    WorldRenderSky? Sky = null,
    WorldRenderEnvironment? Environment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderAtmosphere? Atmosphere = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldTonemap? Tonemap = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? FarDistance = null,
    int ShadowLights = 1,
    int ShadowFadeSlots = 0,
    uint ShadowFadeTicks = 0,
    WorldShadowOverflow ShadowOverflow = WorldShadowOverflow.Instant,
    WorldSkyTier SkyQuality = WorldSkyTier.High,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderIndirect? Indirect = null,
    float SkyFieldScale = 1f
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

    /// <summary>Gets the absent defaults — shadows off, no crowd radius, no ambient occlusion, native scale,
    /// Medium indirect light, no authored presets and the engine's pinned far distance. A world
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
/// <summary>Authored sources, finite solve depth and receiver application for the residency's indirect light cache.</summary>
/// <param name="Bodies">Default makes moving bodies receive at medium and cast and receive at high. Receive omits
/// their casting, Cast enables both, and Off disables both. An explicit placement policy takes precedence.</param>
/// <param name="Sources">Bindable source gains in [0, 1]; absent gains are one.</param>
/// <param name="Bounces">Feedback sweeps after the direct sweep, capped by the selected tier. Absent uses that tier's limit.</param>
/// <param name="Apply">Bindable receiver-only controls. They do not change cached radiance.</param>
/// <param name="Tier">The boot cache tier, Medium by default. The live session lever and selected quality preset override it.</param>
public sealed record WorldRenderIndirect(
    [property: JsonConverter(typeof(StrictEnumConverter<SdfIndirectParticipation>))] SdfIndirectParticipation Bodies = SdfIndirectParticipation.Default,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderIndirectSources? Sources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Bounces = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderIndirectApply? Apply = null,
    [property: JsonConverter(typeof(StrictEnumConverter<SdfIndirectTier>))] SdfIndirectTier Tier = SdfIndirectTier.Medium);
/// <summary>Gains applied once where each source enters transport; continuation never applies them a second time.</summary>
/// <param name="Lights">Explicit diffuse light gain.</param>
/// <param name="Emission">Material emission gain.</param>
/// <param name="Screens">Acquired screen-face emission gain.</param>
/// <param name="Sky">Physical radiance at certified world exits.</param>
/// <param name="Feedback">Reflected preceding-sweep radiance gain, once per bounce.</param>
public sealed record WorldRenderIndirectSources(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Lights = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Emission = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Screens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Sky = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Feedback = null);
/// <summary>Receiver-only controls; gains and tint channels lie in [0, 1] and default to one.</summary>
/// <param name="Intensity">Final indirect diffuse gain.</param>
/// <param name="Tint">Final indirect diffuse tint.</param>
/// <param name="Contact">How much existing ambient occlusion dims indirect diffuse, zero disabling that attenuation.</param>
public sealed record WorldRenderIndirectApply(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Intensity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Tint = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Contact = null);
/// <summary>The lit path's lights and stylization as world data. Absent renders the pinned sun; present, the list
/// is exactly the authored direct lights. Ambient comes from the sky through the environment gain. Every field of every light is optional individually and resolves to the engine's pinned default for its
/// kind. Every value a light or the curvature carries may be keyed on a clock on its own; the section may instead be
/// keyed whole (<paramref name="Clock"/>, <paramref name="Keys"/>), each key a partial record addressing lights by
/// name.</summary>
/// <param name="Lights">The lights, at most <c>SdfLights.MaxLights</c>, in authored order. Shadow-capable directionals
/// compete for the quality row's shadow slots by mode and tick-resolved luminance.</param>
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
    /// <summary>The topology an absent <c>render.lighting</c> resolves to: the pinned shadowing sun.</summary>
    public static WorldRenderLighting Pinned { get; } = new(Lights: [
        new WorldRenderLight.Directional(Shadow: WorldShadowMode.Always, Name: "sun"),
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
[JsonDerivedType(typeof(WorldRenderLight.Rim), typeDiscriminator: "rim")]
[JsonDerivedType(typeof(WorldRenderLight.Point), typeDiscriminator: "point")]
[JsonDerivedType(typeof(WorldRenderLight.Occluder), typeDiscriminator: "occluder")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldRenderLight {
    private WorldRenderLight() {
    }

    /// <summary>Gets the finite nonnegative gain of this light's diffuse contribution to indirect transport.
    /// Absent is one; zero excludes it from the indirect solve while leaving direct shading unchanged. Rim and
    /// attenuation-only lights have no diffuse contribution. Literals, state bindings and keys use the same domain.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BindableScalar? Bounce { get; init; }
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
    /// <param name="Shadow">How this light competes for shadow slots. Absent is <see cref="WorldShadowMode.Never"/>.
    /// A directional without a presented shadow slot is scaled by ambient occlusion instead.</param>
    /// <param name="Name">The unique identity a section key and the shadow allocator address. Required when
    /// <paramref name="Shadow"/> is <see cref="WorldShadowMode.Always"/> or <see cref="WorldShadowMode.Auto"/>.</param>
    public sealed record Directional(
        BindableDirection? Direction = null,
        BindableColor? Color = null,
        BindableScalar? Weight = null,
        BindableAngle? AngularRadius = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldShadowMode? Shadow = null,
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
/// <summary>The procedural sky: a frame and an open, ordered stack of layers. Absent is the default look: the two-stop
/// gradient <c>SdfSky</c> starts from, which a layer drawn over an unauthored gradient draws over too. The layers
/// composite in the order they are authored, each by its blend, and a kind may appear more than once. The air before the
/// sky is <see cref="WorldRenderDefaults.Atmosphere"/>'s. At most <c>SdfSky.MaxLayers</c> layers draw, and the layers the
/// camera sees cut into at most <c>SdfSky.MaxUpperFieldRuns</c> field runs above the lowest run. Every value a layer
/// carries may be keyed on a clock on its own; the section may instead be keyed whole (<paramref name="Clock"/>,
/// <paramref name="Keys"/>), each key a partial record addressing layers by name.</summary>
/// <param name="Layers">The layers, lowest first.</param>
/// <param name="Clock">The clock the section's keys read, by name in the <c>timeline</c> section. Required with
/// <paramref name="Keys"/> and refused without them.</param>
/// <param name="Keys">The section's keys, ascending in time. A field a key states is keyed on
/// <paramref name="Clock"/> through the keys that state it; a field no key states keeps its authored value.</param>
/// <param name="Frame">Which way is up for the sky. Absent is world +y.</param>
public sealed record WorldRenderSky(
    IReadOnlyList<WorldRenderSkyLayer>? Layers = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Clock = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldRenderSkyKey>? Keys = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderSkyFrame? Frame = null
);
/// <summary>One key of <see cref="WorldRenderSky.Keys"/>: a partial record of the sky at one time.</summary>
/// <param name="At">Where on the section's clock the key sits, in the clock's span units, in <c>[0, span)</c>.</param>
/// <param name="Ease">How time eases from this key to the next key that states each field. Absent is
/// <see cref="WorldEase.Linear"/>.</param>
/// <param name="Layers">The layers this key moves, by name: each the same kind as the layer of that name, stating only
/// the fields it moves. A gradient states every stop the layer has, in order. Counts, seeds, kinds, a layer's name,
/// blend, mask, visibility, tier and clock and the sun disc's light are structure, which a key never states.</param>
public sealed record WorldRenderSkyKey(
    double At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldEase? Ease = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, WorldRenderSkyLayer>? Layers = null
);
/// <summary>One sky layer. The <c>$type</c> string is the JSON discriminator. Every layer also carries what any layer
/// carries: its blend, opacity, mask, transform, clock, visibility and the lowest quality tier it draws at.</summary>
[JsonDerivedType(typeof(WorldRenderSkyLayer.Gradient), typeDiscriminator: "gradient")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.SunDisc), typeDiscriminator: "sunDisc")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Stars), typeDiscriminator: "stars")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Clouds), typeDiscriminator: "clouds")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Aurora), typeDiscriminator: "aurora")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Noise), typeDiscriminator: "noise")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Pattern), typeDiscriminator: "pattern")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Panorama), typeDiscriminator: "panorama")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Panel), typeDiscriminator: "panel")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.View), typeDiscriminator: "view")]
[JsonDerivedType(typeof(WorldRenderSkyLayer.Far), typeDiscriminator: "far")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldRenderSkyLayer {
    /// <summary>A rectangular emitter at infinity, evaluated analytically in reflections. Its default visibility is
    /// lighting only and its default blend is add.</summary>
    /// <param name="Direction">The direction toward its centre in the layer frame. Absent is +y.</param>
    /// <param name="Size">The angular half extents in radians, both positive. Absent is (0.3, 0.3).</param>
    /// <param name="Color">The linear radiance. Absent is white.</param>
    /// <param name="Intensity">The nonnegative radiance gain. Absent is one.</param>
    /// <param name="Blur">The nonnegative angular edge softness. Absent is zero.</param>
    /// <param name="Name">The name used by section keys and counted detail rows.</param>
    public sealed record Panel(
        DocumentVector3? Direction = null,
        DocumentVector2? Size = null,
        BindableColor? Color = null,
        BindableScalar? Intensity = null,
        BindableScalar? Blur = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }

    private WorldRenderSkyLayer() {
    }

    /// <summary>Gets the layer's name, which a section key addresses it by and its counted rows are labelled with, or
    /// <see langword="null"/> for an unnamed layer no key can address, labelled by its kind.</summary>
    [JsonIgnore]
    public abstract string? LayerName { get; }
    /// <summary>Gets how the layer composes over the colour beneath it. Absent is its kind's: <c>over</c> for a gradient,
    /// clouds, noise, a pattern and a panorama, <c>add</c> for stars, a sun disc, an aurora and a panel.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldSkyBlend? Blend { get; init; }
    /// <summary>Gets the layer's opacity, in <c>[0, 1]</c>, which scales its alpha. Zero draws nothing and counts no work.
    /// Absent is one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BindableScalar? Opacity { get; init; }
    /// <summary>Gets where the layer draws. Absent is everywhere.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldRenderSkyMask? Mask { get; init; }
    /// <summary>Gets the layer's own transform about the sky frame. Absent is none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldRenderSkyTransform? Transform { get; init; }
    /// <summary>Gets the clock, by name in the <c>timeline</c> section, whose phase moves the layer's own motion: an
    /// aurora's curtains, a noise field's slide, a pattern's scroll. Absent holds them still.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Clock { get; init; }
    /// <summary>Gets who sees the layer. Absent is its kind's: the camera and the lighting for a gradient, the camera alone
    /// for every other kind.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldSkyVisibility? Visibility { get; init; }
    /// <summary>Gets the lowest quality tier the layer draws at; below it the layer writes no entry and counts no work.
    /// Absent is <see cref="WorldSkyTier.Low"/>, every tier.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldSkyTier? Tier { get; init; }

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
    /// <summary>The visible sun disc about one directional light's direction: an additive glow, or with
    /// <paramref name="Texture"/> the image a screen shows across the disc.</summary>
    /// <param name="Light">The <see cref="WorldRenderLighting.Lights"/> slot of a directional light. Absent is the
    /// shadow light, or the first directional when none shadows.</param>
    /// <param name="Radius">The disc's angular half-radius, in <c>(0, π/2]</c> radians. Absent is the engine
    /// default.</param>
    /// <param name="Intensity">The peak additive brightness. Absent is zero, which draws nothing.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    /// <param name="Color"><see cref="BindableColor"/>'s grammar: the disc's tint. Absent is white.</param>
    /// <param name="Texture">The <c>texture</c> body shape: the screen whose image the disc shows. Absent is the
    /// glow.</param>
    public sealed record SunDisc(
        int? Light = null,
        BindableAngle? Radius = null,
        BindableScalar? Intensity = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Color = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderSkyTexture? Texture = null
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
    /// <param name="Sparsity">The share of the cells that carry a star, in <c>(0, 1]</c>. Absent is the engine
    /// default.</param>
    /// <param name="Size">A star's angular radius as a share of one cell's angular pitch, in <c>(0, 0.5]</c>. Absent is
    /// the engine default.</param>
    public sealed record Stars(
        float? Density = null,
        BindableScalar? Brightness = null,
        uint? Seed = null,
        WorldRenderSkyTwinkle? Twinkle = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Sparsity = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Size = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>The procedural cloud layer: a deterministic hashed-lattice noise on a dome above the camera,
    /// thresholded by coverage and fading into the horizon.</summary>
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
    /// <param name="Octaves">The fractal octaves at the high tier, one to eight. Absent is four.</param>
    /// <param name="Warp">How far the first fractal sum bends the second's domain, in cells. Absent is 0.6.</param>
    /// <param name="Relief">The heightfield's rise per unit thickness, the steepness its lighting reads. Absent is
    /// 0.7.</param>
    /// <param name="Extinction">Beer's-law extinction per unit thickness: how quickly a cloud turns opaque. Absent is
    /// 3.5.</param>
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
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? Octaves = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Warp = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Relief = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Extinction = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>Aurora curtains: rays rising from a wavering base, fading upward from <paramref name="Color"/> to
    /// <paramref name="Top"/>, moving with the layer's clock.</summary>
    /// <param name="Intensity">The curtains' peak brightness. Absent is zero, which draws nothing.</param>
    /// <param name="Color"><see cref="BindableColor"/>'s grammar: the colour at a curtain's base. Absent is green.</param>
    /// <param name="Top"><see cref="BindableColor"/>'s grammar: the colour at a curtain's top. Absent is violet.</param>
    /// <param name="Base">The curtains' mean base elevation, in radians. Absent is about 17°.</param>
    /// <param name="Height">How far above its base a curtain rises, in radians. Absent is about 20°.</param>
    /// <param name="Fold">How far the base wavers, in radians. Absent is about 5°.</param>
    /// <param name="Rays">The rays per turn of azimuth. Absent is 96.</param>
    /// <param name="Waves">The base's waves per turn of azimuth. Absent is 5.</param>
    /// <param name="Seed">The hash seed.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    public sealed record Aurora(
        BindableScalar? Intensity = null,
        BindableColor? Color = null,
        BindableColor? Top = null,
        BindableAngle? Base = null,
        BindableAngle? Height = null,
        BindableAngle? Fold = null,
        float? Rays = null,
        float? Waves = null,
        uint? Seed = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>A fractal noise field over the sky, coloured from <paramref name="Low"/> to <paramref name="High"/> and
    /// covered where it rises past one less its coverage, sliding with the layer's clock.</summary>
    /// <param name="Low"><see cref="BindableColor"/>'s grammar: the colour where the noise is lowest. Absent is black.</param>
    /// <param name="High"><see cref="BindableColor"/>'s grammar: the colour where it is highest. Absent is white.</param>
    /// <param name="Coverage">The share of the sky the noise covers, in <c>[0, 1]</c>. Absent is one.</param>
    /// <param name="Softness">The covered edge's width as a share of the noise's range, in <c>(0, 1]</c>. Absent is
    /// 0.25.</param>
    /// <param name="Scale">The lattice cells per unit of direction. Absent is four.</param>
    /// <param name="Octaves">The fractal octaves at the high tier, one to eight. Absent is four.</param>
    /// <param name="Gain">Each octave's amplitude relative to the one before it, in <c>(0, 1)</c>. Absent is one
    /// half.</param>
    /// <param name="Seed">The hash seed.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    public sealed record Noise(
        BindableColor? Low = null,
        BindableColor? High = null,
        BindableScalar? Coverage = null,
        float? Softness = null,
        float? Scale = null,
        uint? Octaves = null,
        float? Gain = null,
        uint? Seed = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>A painted pattern over azimuth and elevation, scrolling one cell a cycle of the layer's clock.</summary>
    /// <param name="Shape">What it paints. Absent is a checker.</param>
    /// <param name="Colors">Its two colours, in <see cref="BindableColor"/>'s grammar. Absent is black and white.</param>
    /// <param name="Cells">The cells per turn of azimuth, a whole number. Absent is 24.</param>
    /// <param name="Line">A stripe's or grid line's width as a share of a cell, in <c>(0, 1)</c>. Absent is 0.1.</param>
    /// <param name="Softness">The edge's width as a share of a cell, in <c>[0, 0.5]</c>. Absent is 0.05.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    public sealed record Pattern(
        WorldSkyPatternShape? Shape = null,
        IReadOnlyList<BindableColor>? Colors = null,
        float? Cells = null,
        float? Line = null,
        float? Softness = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>The image a diegetic screen shows, sampled by direction: the screen's source, at infinity. Visibility
    /// defaults to the camera; lighting visibility projects the same acquired image into the residency's environment,
    /// following its completed publication and capture taint.</summary>
    /// <param name="Screen">The screen's surface index (<see cref="WorldScreen.Index"/>), a screen the world declares.
    /// Required.</param>
    /// <param name="Projection">How a direction maps to the image. Absent is equirectangular.</param>
    /// <param name="Intensity">The image's brightness scale. Absent is one.</param>
    /// <param name="Name">The name a section key addresses the layer by, unique among the layers.</param>
    public sealed record Panorama(
        int? Screen = null,
        WorldSkyProjection? Projection = null,
        BindableScalar? Intensity = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>Another world, seen at infinity: a second <c>sdf.world</c> instance (<c>sky$&lt;name&gt;</c>) rendering the
    /// destination's session from a fixed anchor in it, turned with the viewer's camera and never translated by it. It
    /// renders only while the viewer's previous frame showed it, and only the rectangle its mask covers (a cone, or the
    /// whole frustum without a mask). The camera alone sees it, since the environment map binds no screen. At most
    /// <c>SdfSky.MaxInfinityViews</c> view and far layers a world, nested worlds included; a view that cannot render
    /// draws its <paramref name="Fallback"/> colour.</summary>
    /// <param name="Name">The layer's name, required: the instance is <c>sky$name</c> and its counted rows carry the name, so
    /// it is one part free of <c>$</c> and <c>~</c>, unique among the layers.</param>
    /// <param name="Destination">The destination world, by the name a session screen's destination takes. Required.</param>
    /// <param name="Anchor">The point in the destination the camera sits at, whatever the viewer does. Absent is its
    /// origin.</param>
    /// <param name="Turn">The rotation about up that carries the viewer's frame into the destination's, in degrees. Absent
    /// is none: the destination's axes are the viewer's.</param>
    /// <param name="Scale">The instance's render scale against the viewer's pixel density, in <c>(0, 1]</c>; below the high
    /// sky tier it renders at half of it. Absent is one half.</param>
    /// <param name="Refresh">It renders at most once every this many frames, at least one. Absent is two.</param>
    /// <param name="FarDistance">The depth its march ends at, in world units. Absent is 1000.</param>
    /// <param name="Shadows">Whether the instance is dressed with the key light's soft shadow. Absent is off.</param>
    /// <param name="AmbientOcclusion">Whether the instance is dressed with ambient occlusion. Absent is off.</param>
    /// <param name="Fallback"><see cref="BindableColor"/>'s grammar: the colour drawn where the view cannot render, before
    /// its first image and past the nesting depth or the cap. Absent is black.</param>
    public sealed record View(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Destination = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Anchor = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Turn = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Scale = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Refresh = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? FarDistance = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Shadows = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AmbientOcclusion = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Fallback = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>Far geometry: the same mechanism as <see cref="View"/> over a residency that holds only the named
    /// prototypes of this world, a planet or a ring of monoliths or a city on the horizon, rendered by the one
    /// <c>sdf.world</c> engine sized to its angular bound, so its cost scales with the pixels it covers and is counted under
    /// its own instance. Its image alpha is its coverage: the rest of the sky shows around it.</summary>
    /// <param name="Name">The layer's name, required, as <see cref="View"/>'s.</param>
    /// <param name="Prototypes">The prototypes the residency holds, by their ids in <c>prototypes</c>. Required, at least
    /// one.</param>
    /// <param name="Anchor">The point the camera sits at, in this world. Absent is its origin.</param>
    /// <param name="Turn">The rotation about up applied to the viewer's frame, in degrees. Absent is none.</param>
    /// <param name="Scale">As <see cref="View.Scale"/>.</param>
    /// <param name="Refresh">As <see cref="View.Refresh"/>.</param>
    /// <param name="FarDistance">The depth its march ends at, in world units. Absent is 1000.</param>
    /// <param name="Shadows">As <see cref="View.Shadows"/>.</param>
    /// <param name="AmbientOcclusion">As <see cref="View.AmbientOcclusion"/>.</param>
    /// <param name="Fallback">As <see cref="View.Fallback"/>.</param>
    public sealed record Far(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Prototypes = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Anchor = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Turn = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Scale = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Refresh = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? FarDistance = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Shadows = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AmbientOcclusion = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Fallback = null
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
/// <summary>The air between the camera and what it sees: each kind it states, and nothing it does not. Every value may
/// be bound to state or keyed on a clock on its own; the kinds are structure, which no key states. An edit lands on the
/// next frame. The sky share of a pixel, the sky beyond every surface, passes through the haze and the medium; the fog
/// ends at the sky, which is its colour at infinity.</summary>
/// <param name="Fog">The fog: an exponential medium, level or thinning with height. Absent is no fog.</param>
/// <param name="Haze">The haze: aerial perspective that scatters the sky and the light of the bodies that cast light,
/// brightest toward them. Absent is no haze.</param>
/// <param name="Medium">The medium: water below a level surface, with its own extinction and colour. Absent is no
/// medium.</param>
public sealed record WorldRenderAtmosphere(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderFog? Fog = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderHaze? Haze = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderMedium? Medium = null
);
/// <summary>The fog: <paramref name="Density"/> per world unit, in-scattering the sky or <paramref name="Color"/>.</summary>
/// <param name="Density">The density per world unit, at the base when <paramref name="Height"/> is authored. Absent is the
/// default look's density.</param>
/// <param name="Color"><see cref="BindableColor"/>'s grammar: the colour the fog in-scatters. Absent is the sky in the
/// pixel's direction, so a distant surface fades into the horizon behind it.</param>
/// <param name="Height">The height fog's profile. Absent is a fog alike at every height.</param>
public sealed record WorldRenderFog(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Density = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Color = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderAirHeight? Height = null
);
/// <summary>An air kind's height profile: its density falls by a factor of e over each <paramref name="Falloff"/> world
/// units above <paramref name="Base"/>, and rises as much below it, so a ray's optical depth through it has a closed
/// form.</summary>
/// <param name="Base">The height at which the kind's density is its authored one. Absent is zero.</param>
/// <param name="Falloff">The rise over which the density thins by a factor of e, at least
/// <c>SdfAtmosphere.MinFalloff</c> world units. Absent is <c>SdfAtmosphere.DefaultFalloff</c>.</param>
public sealed record WorldRenderAirHeight(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Base = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Falloff = null
);
/// <summary>The haze: aerial perspective. It takes <paramref name="Amount"/> of the light along a level ray the far
/// distance (<see cref="WorldRenderDefaults.FarDistance"/>) long, and in-scatters the sky and the light of every
/// directional light, which is what a light-casting body binds, forward by <paramref name="Anisotropy"/>, so it is
/// brightest toward a low sun.</summary>
/// <param name="Amount">The share of the light the haze takes over the far distance, in <c>[0, 0.99]</c>. Absent is
/// zero, which draws none.</param>
/// <param name="Anisotropy">The Henyey-Greenstein anisotropy of its scattering toward the bodies, in <c>[0, 0.9]</c>:
/// zero scatters alike in every direction. Absent is <c>SdfAtmosphere.DefaultHazeAnisotropy</c>.</param>
/// <param name="Height">The haze's height profile, the amount taken at its base. Absent is a haze alike at every
/// height.</param>
public sealed record WorldRenderHaze(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Amount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Anisotropy = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderAirHeight? Height = null
);
/// <summary>The medium: water below a level surface, which every ray below it crosses with its own extinction and
/// in-scatter colour, a camera below it seeing the world and the sky through it.</summary>
/// <param name="Surface">The surface's height. Absent is zero.</param>
/// <param name="Extinction">The extinction per world unit below the surface. Absent is
/// <c>SdfAtmosphere.DefaultMediumExtinction</c>.</param>
/// <param name="Color"><see cref="BindableColor"/>'s grammar: the colour the medium in-scatters, which a deep view through
/// it reaches. Absent is <c>SdfAtmosphere.DefaultMediumColor</c>.</param>
public sealed record WorldRenderMedium(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Surface = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Extinction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Color = null
);
/// <summary>The tonemap the root graph applies to the frame before the HUD — see
/// <see cref="WorldRenderDefaults.Tonemap"/>.</summary>
[JsonConverter(typeof(StrictEnumConverter<WorldTonemap>))]
public enum WorldTonemap {
    /// <summary>No remap: the stylized shaded color.</summary>
    None = 0,
    /// <summary>A filmic (ACES-fit) curve over each view, applied by the root graph's place pass for the view.</summary>
    Filmic = 1,
}
/// <summary>The gains on lighting derived from the lighting-visible sky layers.</summary>
/// <param name="Ambient">The diffuse irradiance gain. Absent is one; zero disables harmonic lighting.</param>
/// <param name="Reflection">The reflection gain. Absent is one; zero disables reflection lookups.</param>
public sealed record WorldRenderEnvironment(BindableScalar? Ambient = null, BindableScalar? Reflection = null);
