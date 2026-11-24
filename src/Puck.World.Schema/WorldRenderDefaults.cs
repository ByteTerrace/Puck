using System.Text.Json.Serialization;
using Puck.Abstractions;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Presentation;
using Puck.Assets.Documents;
using Puck.SignedDistance;
using Puck.World.Authoring;

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
    [property: DocumentName(WorldNameKind.Camera, WorldNameRole.Declares)] string Name,
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
/// Shadows, ambient occlusion and render scale are required; optional members use their constructor defaults.</summary>
/// <param name="Shadows">The soft-shadow tier the preset selects.</param>
/// <param name="AmbientOcclusion">Whether the preset enables ambient occlusion.</param>
/// <param name="RenderScale">The scalar render-scale ceiling the preset selects.</param>
/// <param name="Temporal">Whether the preset reconstructs the world's views over time (<c>world.temporal</c>).</param>
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
/// <param name="Bloom">The optical scatter cost gate.</param>
/// <param name="RayQueries">Whether the preset lets the indirect volume's background rays take the device's ray hardware
/// where it has it (<c>world.rays</c>); the software walk answers the same hits elsewhere.</param>
/// <param name="DistantShadows">The distant shadow map the device draws past the far cascade (its reach, texels and the
/// light turn a held map tolerates): far shadows by rasterization, which need no ray hardware. Absent draws none.</param>
/// <param name="FarShadows">How often the device refreshes the far shadow cascade and the light turn a held cascade
/// tolerates (<see cref="WorldFarShadows"/>). Absent refreshes it every frame.</param>
/// <param name="ViewsRate">The maximum shared shading footprint and classification tile size. Absent shades every pixel.</param>
/// <param name="Nested">The nested-view table the preset runs (<see cref="WorldNestedViewQuality"/>), in place of the
/// world's <c>render.nested</c>, so each device chooses what its windows, portals and filmed screens give up at each size.
/// Absent runs the world's table.</param>
/// <param name="IndirectBudget">The irradiance work each presented frame grants the volume on this device (<see cref="WorldRenderIndirectBudget"/>), in place of the world's <c>render.indirect.budget</c>, so each device chooses how much lighting work it affords a frame. Absent runs the world's budget.</param>
/// <param name="ShadowTexels">The texels along each side of a full-resolution shadow cascade (the near, far and
/// secondary entries) on this device, a power of two from <see cref="MinShadowTexels"/> through
/// <see cref="MaxShadowTexels"/>: a shadow tier below High and a nested view's row halve it as they halve the engine's.
/// Each entry's depth, exit and glass layers are sized by its square, so it sets what every world's shadows hold on the
/// device. Absent keeps the engine's 2048.</param>
[method: JsonConstructor]
public readonly record struct WorldQualityPreset(
    ShadowTier Shadows,
    bool AmbientOcclusion,
    float RenderScale,
    bool Temporal = false,
    bool DynamicResolution = false,
    int ShadowLights = 0,
    int ShadowFadeSlots = 0,
    uint ShadowFadeTicks = 0,
    WorldShadowOverflow ShadowOverflow = WorldShadowOverflow.Instant,
    WorldRenderScaleTier RenderScaleFloor = WorldRenderScaleTier.Quarter,
    WorldSkyTier Sky = WorldSkyTier.High,
    [property: JsonConverter(typeof(StrictEnumConverter<SdfIndirectTier>)), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SdfIndirectTier? Indirect = null,
    float SkyFieldScale = 1f,
    Puck.Hosting.BloomQuality Bloom = Puck.Hosting.BloomQuality.High,
    bool RayQueries = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDistantShadows? DistantShadows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFarShadows? FarShadows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SdfViewsRate? ViewsRate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldNestedViewQuality>? Nested = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderIndirectBudget? IndirectBudget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ShadowTexels = null
) {
    /// <summary>The fewest texels a side <see cref="ShadowTexels"/> takes.</summary>
    public const int MinShadowTexels = 256;
    /// <summary>The most texels a side <see cref="ShadowTexels"/> takes.</summary>
    public const int MaxShadowTexels = 8192;

    /// <summary>Returns why a cascade's texels a side cannot be authored, or <see langword="null"/> when they can.</summary>
    /// <param name="texels">The texels a side.</param>
    /// <param name="path">The document path the refusal names.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static string? ShadowTexelsRefusal(int texels, string path) =>
        (((texels >= MinShadowTexels) && (texels <= MaxShadowTexels) && System.Numerics.BitOperations.IsPow2(value: texels))
            ? null
            : $"{path} must be a power of two from {MinShadowTexels} through {MaxShadowTexels}.");
}
/// <summary>The world's render-lever defaults — the boot values <c>Puck.World.WorldRenderSettings</c> wakes on and the
/// <c>world.quality</c> preset table. Session state, not identity: these are engine-wide levers (shadows, AO, render
/// scale), the graphics-menu defaults a server-pulled world would carry.</summary>
/// <param name="Shadows">The boot soft-shadow tier.</param>
/// <param name="Shade">The default shading program inherited by materials without their own.</param>
/// <param name="AmbientOcclusion">Whether ambient occlusion boots on.</param>
/// <param name="RenderScale">The scalar boot render-scale ceiling in [0.125, 1].</param>
/// <param name="UpscaleSharpness">The boot reconstruction sharpness: the spatial resolve's blend (0 bilinear .. 1
/// Catmull-Rom) and the strength of the sharpen a temporally resolved view gets at its rect's own extent.</param>
/// <param name="Temporal">Whether the world's own views boot reconstructing over time (<c>world.temporal</c>): each
/// jitters its samples and resolves them over its history, native or reduced. The world's own views include every layout
/// slot that names a camera; the camera views screens film and session views never do.</param>
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
/// <param name="Tone">The display transform every view of the world puts its exposed scene light through before the
/// view's display pass encodes it for the display (<see cref="WorldToneCurve"/>). The letterbox color, every pane
/// (display-referred) and the HUD are never toned. Optional; absent applies no curve.</param>
/// <param name="FarDistance">The far distance in world units: the depth at which every view's geometry ends — the
/// mesh raster's far plane, the reach of the bake and the indirect volume, and the depth the fog and depth ramps are
/// measured against. Geometry beyond it is never drawn, so an infinite plane ends on a visible horizon curve at this
/// depth unless the atmosphere absorbs it (<c>render.atmosphere</c>). Optional; absent resolves to the engine's pinned
/// 40. Must lie within [<see cref="MinFarDistance"/>, <see cref="MaxFarDistance"/>]. Re-read on every definition
/// revision (a <c>world.row.set render</c> lands on the next frame); <c>world.budget</c> echoes it with its derived
/// costs.</param>
/// <param name="ShadowLights">The boot number of held shadow slots, from 0 through 4; one keeps the pinned sun selected.</param>
/// <param name="ShadowFadeSlots">The boot number of additional shadow handoff slots, from 0 through 2.</param>
/// <param name="ShadowFadeTicks">The boot length of a shadow handoff in delivered ticks; zero selects instant changes.</param>
/// <param name="ShadowOverflow">How a crossing proceeds when its handoff capacity is occupied.</param>
/// <param name="SkyQuality">The sky's boot quality tier (<c>world.sky-quality</c>): a layer below it writes no entry, and
/// below <see cref="WorldSkyTier.High"/> each kind draws its reduced form.</param>
/// <param name="Indirect">The world's diffuse indirect-light tier, sources and application.</param>
/// <param name="SkyFieldScale">The boot sky field grid fraction, one or one half, independent of view render scale.</param>
/// <param name="Exposure">The camera exposure each view maps the luminance it shades to the display by, before the
/// tone curve. Absent is a manual exposure at <c>SdfExposure.DefaultEv100</c>, a sunlit day's.</param>
/// <param name="Toon">The world's default cel style (<see cref="ToonStyleDocument"/>), which every material that authors
/// no style of its own takes: its ink, and the styled mark the live <c>world.toon off</c> suppresses with the
/// <see cref="Shade"/> default it inks. On unless it says <c>enabled: false</c>; absent is off.</param>
/// <param name="Bloom">Optional camera scatter. Absent is off.</param>
/// <param name="ShadowCoverage">How far the key light's shadow cascades reach (<see cref="WorldRenderShadowCoverage"/>).
/// Absent keeps the engine's 16 m near, 120 m far and 200 m caster reach; <c>world.budget</c> prints the coverage with
/// each cascade's texel.</param>
/// <param name="Devices">The world's device profiles (<see cref="WorldDeviceProfile"/>): for each graphics device, the
/// quality table it runs and the memory it may use. The engine selects one by the adapter it runs on and by name
/// (<c>world.device-profile</c>). Absent authors none, and no cap applies.</param>
/// <param name="Lanes">The world's render lanes: up to four bindable scalars (a literal, a <c>state.&lt;row&gt;</c>
/// binding, or keys on a clock), resolved at each frame's presented tick and read by every surface and volume as
/// render lanes 4 to 7 (<c>SdfLanes</c>), so a static placement sharing one baked mesh follows world state without a
/// slot. Absent or a missing entry reads zero; <c>world.lighting</c> echoes them as authored.</param>
/// <param name="Nested">What a nested view gives up at each presented size (<see cref="WorldNestedViewQuality"/>). Absent
/// gives up nothing.</param>
/// <param name="RayQueries">Whether the indirect volume's background rays (its probe trace, the relight's shadow rays
/// and the camera sample) boot taking the device's ray hardware where it has it, through the scene's acceleration
/// structures (<c>world.rays</c>). The software walk answers the same hits wherever it is off or the device has none.
/// Absent is off.</param>
/// <param name="DistantShadows">The distant shadow map the world boots drawing past the far cascade
/// (<see cref="WorldDistantShadows"/>); a quality preset's row replaces it. Absent draws none.</param>
/// <param name="FarShadows">How often the world boots refreshing the far shadow cascade (<see cref="WorldFarShadows"/>);
/// a quality preset's row replaces it. Absent refreshes it every frame.</param>
/// <param name="ViewsRate">The maximum shared shading footprint and classification tile size. Absent shades every pixel.</param>
/// <param name="FarProduct">What the world shows of itself to a far viewer (<see cref="WorldFarProduct"/>): the product a
/// sky view asking for it draws. Absent takes the product's defaults.</param>
public sealed record WorldRenderDefaults(
    ShadowTier Shadows = ShadowTier.Off,
    bool AmbientOcclusion = false,
    float RenderScale = 1f,
    float UpscaleSharpness = 0f,
    bool Temporal = false,
    bool DynamicResolution = false,
    [property: JsonPropertyName("low"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? LowRaw = null,
    [property: JsonPropertyName("medium"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? MediumRaw = null,
    [property: JsonPropertyName("high"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? HighRaw = null,
    WorldRenderLighting? Lighting = null,
    WorldRenderSky? Sky = null,
    WorldRenderEnvironment? Environment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderAtmosphere? Atmosphere = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldToneCurve? Tone = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? FarDistance = null,
    int ShadowLights = 1,
    int ShadowFadeSlots = 0,
    uint ShadowFadeTicks = 0,
    WorldShadowOverflow ShadowOverflow = WorldShadowOverflow.Instant,
    WorldSkyTier SkyQuality = WorldSkyTier.High,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderIndirect? Indirect = null,
    float SkyFieldScale = 1f,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderExposure? Exposure = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ToonStyleDocument? Toon = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Puck.Hosting.CameraBloom? Bloom = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderShadowCoverage? ShadowCoverage = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BindableScalar>? Lanes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Puck.World.Authoring.ShadeReference? Shade = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldDeviceProfile>? Devices = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<WorldNestedViewQuality>? Nested = null,
    bool RayQueries = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldDistantShadows? DistantShadows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFarShadows? FarShadows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] SdfViewsRate? ViewsRate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFarProduct? FarProduct = null
) {
    /// <summary>The most world lanes <see cref="Lanes"/> holds (<c>SdfLanes.WorldCount</c>).</summary>
    public const int MaxLanes = SdfLanes.WorldCount;

    /// <summary>The largest <see cref="FarDistance"/> the validator admits: 8192 world units, the largest power of two
    /// at which a float's spacing (2^13 · 2^-23 = 0.00098) still resolves a millimetre, so a depth anywhere in the view
    /// still places a surface to within one.</summary>
    public const float MaxFarDistance = 8192f;
    /// <summary>The smallest <see cref="FarDistance"/> the validator admits: one world unit, below which no camera could
    /// frame a body.</summary>
    public const float MinFarDistance = 1f;

    /// <summary>Gets the absent defaults — shadows off, no ambient occlusion, native scale,
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
/// <summary>How a camera exposes: the luminance <c>1.2 · 2^EV100</c> displays as white (before the tone curve), so each stop
/// up halves the image's brightness. A manual exposure holds <paramref name="Ev100"/>; an automatic one meters each
/// view's own rendered image (a log histogram between the <paramref name="Low"/> and <paramref name="High"/>
/// percentiles, weighted toward the centre by <paramref name="Center"/>), aims for that reading less
/// <paramref name="Compensation"/> within <paramref name="Minimum"/> and <paramref name="Maximum"/>, holds while the
/// reading stays within <paramref name="Band"/> stops of the one it settled on, and otherwise eases toward it over
/// <paramref name="Adaptation"/> seconds of presentation time. Presentation only: nothing in
/// simulation state reads it. <paramref name="Ev100"/>, the bounds and the compensation are bindable, so a world keys
/// them on its day clock as it keys the sun and the sky (a lower floor at night). The darkest exposure any of their
/// authored values reaches sizes each point and spot light's range, so a keyed floor never changes the lights.</summary>
/// <param name="Mode">Manual or automatic. Absent is manual.</param>
/// <param name="Ev100">The manual exposure value at ISO 100, in <c>[−6, 20]</c>: about 15 for a sunlit day, 7 for a lit
/// room, 3 for a dim one. An automatic exposure starts there and holds it while its meter has no reading. Absent is
/// <c>SdfExposure.DefaultEv100</c>, or with automatic exposure the middle of its bounds.</param>
/// <param name="Minimum">The lowest <c>EV100</c> an automatic exposure adapts to: the darkest scene it brightens. In
/// <c>[−6, 20]</c>; absent is −6.</param>
/// <param name="Maximum">The highest <c>EV100</c> an automatic exposure adapts to, at or above
/// <paramref name="Minimum"/>. In <c>[−6, 20]</c>; absent is 20.</param>
/// <param name="Compensation">The stops an automatic exposure brightens the metered image by, in <c>[−10, 10]</c>.
/// Absent is zero.</param>
/// <param name="Adaptation">The time constant an automatic exposure eases toward its meter with, in seconds, in
/// <c>[0, 60]</c>; zero follows the meter at once. Absent is <c>SdfExposure.DefaultAdaptation</c>.</param>
/// <param name="Local">The share of the local adaptation, in <c>[0, 1]</c>: how far each surface's exposure moves from
/// the view's toward what its own illumination would meter against the view's image meter, within six stops either way,
/// so a dim ceiling beside a sunlit window lifts and the sunlit ground seen through the window compresses, as the eye
/// adapts across a scene. Zero exposes the whole view alike; absent is zero. Read only with an automatic exposure.</param>
/// <param name="Hold">A bindable <c>EV100</c> that pins the exposure while it is above zero, in <c>[0, 20]</c>: every
/// view then renders manual at that value with no local adaptation, whatever <paramref name="Mode"/> and the bounds say,
/// and zero (the value of an unbound or released hold) leaves the authored exposure exactly as it was. The bounds still
/// size each point and spot light's range, so a hold never changes the lights the volume traces. Absent is no hold.</param>
/// <param name="Band">The automatic exposure's dead band, in stops, in <c>[0, 4]</c>: the exposure holds while the
/// meter reads within this far of the reading it last settled on, and follows the meter once a reading leaves the band.
/// Absent is zero, which follows every change.</param>
/// <param name="Low">The percentile, in <c>[0, 1)</c>, below which the image meter leaves the darkest pixels out.
/// Bindable, so a world keys it on a view clock of the viewer's height: a camera climbing into a black sky leaves the
/// void out of its meter and exposes what the sun lights. Absent is zero.</param>
/// <param name="High">The percentile, above <paramref name="Low"/> and at most one, above which the image meter leaves
/// the brightest pixels out. Absent is one.</param>
/// <param name="Center">How far the image meter weights the image toward its centre, in <c>[0, 1]</c>: a pixel counts
/// <c>1 − Center · r²</c>, <c>r</c> its distance from the centre over the half diagonal. Absent is zero, every pixel
/// alike.</param>
public sealed record WorldRenderExposure(
    SdfExposureMode Mode = SdfExposureMode.Manual,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Ev100 = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Minimum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Maximum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Compensation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Adaptation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Local = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Hold = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Band = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Low = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? High = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Center = null
) {
    /// <summary>The lowest an exposure's compensation admits, in stops.</summary>
    public const float MinCompensation = -10f;
    /// <summary>The highest an exposure's compensation admits, in stops.</summary>
    public const float MaxCompensation = 10f;
    /// <summary>The longest an exposure's adaptation admits, in seconds.</summary>
    public const float MaxAdaptation = 60f;
    /// <summary>The widest an exposure's dead band admits, in stops.</summary>
    public const float MaxBand = 4f;

    // A section's brightest exposure, held per section: a section is immutable and its authored values are its own, so
    // every resolve of the same section gives the same exposure.
    private sealed record BrightestExposure(SdfExposure Exposure);
    private static readonly CodeDerivedTable<WorldRenderExposure, BrightestExposure> BrightestBySection = new();

    /// <summary>Returns the brightest exposure the section's authored values reach, which sizes each point and spot
    /// light's range: an absent section's manual default, or this section with each bindable value at its brightest
    /// authored value (the lowest <see cref="Ev100"/> and <see cref="Minimum"/> literal or key, the highest
    /// <see cref="Maximum"/> and <see cref="Compensation"/>), a state binding's at the widest the domain admits.</summary>
    /// <param name="exposure">The authored section, or <see langword="null"/>.</param>
    /// <returns>The exposure.</returns>
    public static SdfExposure Resolve(WorldRenderExposure? exposure) {
        if (exposure is null) {
            return SdfExposure.Default;
        }

        return BrightestBySection.GetValue(
            key: exposure,
            derive: static section => new BrightestExposure(Exposure: ResolveSection(exposure: section, state: false, value: static (_, scalar, _, brightest) => Brightest(brightest: brightest, scalar: scalar)))
        ).Exposure;
    }
    /// <summary>Returns the exposure a frame's views map luminance by: each bindable value as the caller resolves it
    /// (a literal, a state cell or keys on a clock), the pinned manual exposure while <paramref name="hold"/> is above
    /// zero.</summary>
    /// <typeparam name="TState">What <paramref name="value"/> reads, passed to it so the delegate captures nothing.</typeparam>
    /// <param name="exposure">The authored section, or <see langword="null"/>.</param>
    /// <param name="state">What <paramref name="value"/> reads.</param>
    /// <param name="value">Resolves a bindable value, with the field whose domain bounds it, to its current number; called
    /// only for an authored one.</param>
    /// <param name="hold">The resolved <see cref="Hold"/>, in <c>EV100</c>; zero or less is no hold.</param>
    /// <returns>The exposure.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public static SdfExposure Held<TState>(WorldRenderExposure? exposure, TState state, Func<TState, BindableScalar, WorldValueField, float> value, float hold) {
        ArgumentNullException.ThrowIfNull(value);

        var authored = ((exposure is null)
            ? SdfExposure.Default
            : ResolveSection(exposure: exposure, state: (State: state, Value: value), value: static (held, scalar, field, _) => held.Value(held.State, scalar, field)));

        return ((hold > 0f) ? authored.HeldAt(ev100: hold) : authored);
    }
    // A bindable value at the brightest exposure it can reach: its lowest authored value where a lower one brightens,
    // its highest where a higher one does; a state binding's the domain's end.
    private static float Brightest(BindableScalar scalar, bool brightest) {
        var values = scalar.AuthoredValues().ToArray();

        if (values.Length == 0) { return (brightest ? SdfPhotometry.MinimumEv100 : SdfPhotometry.MaximumEv100); }
        return (brightest ? values.Min() : values.Max());
    }

    // The section with each authored bindable value through `value`, given its field and whether a lower value brightens.
    private static SdfExposure ResolveSection<TState>(WorldRenderExposure exposure, TState state, Func<TState, BindableScalar, WorldValueField, bool, float> value) {
        var automatic = (exposure.Mode == SdfExposureMode.Auto);
        float? ev100 = ((exposure.Ev100 is { } ev) ? value(state, ev, WorldValueFields.ExposureEv100, true) : null);
        var minimum = ((exposure.Minimum is { } floor) ? value(state, floor, WorldValueFields.ExposureMinimum, true) : (automatic ? SdfPhotometry.MinimumEv100 : (ev100 ?? SdfExposure.DefaultEv100)));
        var maximum = ((exposure.Maximum is { } high) ? value(state, high, WorldValueFields.ExposureMaximum, false) : (automatic ? SdfPhotometry.MaximumEv100 : (ev100 ?? SdfExposure.DefaultEv100)));

        return new SdfExposure(
            Adaptation: (exposure.Adaptation ?? SdfExposure.DefaultAdaptation),
            Band: (automatic ? (exposure.Band ?? 0f) : 0f),
            Center: (automatic ? (exposure.Center ?? 0f) : 0f),
            High: (automatic ? (exposure.High ?? 1f) : 1f),
            Low: ((automatic && (exposure.Low is { } low)) ? value(state, low, WorldValueFields.ExposureLow, false) : 0f),
            Compensation: ((exposure.Compensation is { } compensation) ? value(state, compensation, WorldValueFields.ExposureCompensation, false) : 0f),
            Ev100: (ev100 ?? (automatic ? ((minimum + maximum) / 2f) : SdfExposure.DefaultEv100)),
            Local: (automatic ? (exposure.Local ?? 0f) : 0f),
            Maximum: maximum,
            Minimum: minimum,
            Mode: exposure.Mode
        );
    }
}
/// <summary>The reach of the key light's shadow cascades, in metres. Each cascade spreads the same texels over the view's
/// slice it covers, so a longer reach costs precision rather than texels: a valley whose walls stand hundreds of metres
/// off needs a far reach past them, and its far cascade's texel grows in proportion. Every member is optional; an absent
/// one keeps the engine's value.</summary>
/// <param name="Near">The depth from the eye the near cascade covers, positive and below the far reach (16 when
/// absent).</param>
/// <param name="Far">The depth from the eye the far cascade covers, at most the far distance it is clipped to (120 when
/// absent).</param>
/// <param name="Casters">How far past a cascade toward the light a caster may stand and still shadow it (200 when
/// absent).</param>
public sealed record WorldRenderShadowCoverage(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Near = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Far = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Casters = null);
/// <summary>What a world shows of itself to a far viewer: its settled static scene, each mesh at the coarsest level it
/// carries, its vertices gathered into cells of <paramref name="Cell"/>, without the parts smaller than
/// <paramref name="MinimumSize"/>. A sky view of the world that asks for its far product draws it. Presentation
/// only.</summary>
/// <param name="Cell">The side, in metres, of the cells the product gathers its vertices into, so its finest level holds
/// about two triangles a cell of surface; its coarser levels, which a far view's draws choose between by the pixels they
/// cover, stay within multiples of it. Within [0.01, 1024]. Absent
/// is one metre.</param>
/// <param name="MinimumSize">The least size, in metres, a static part's longest side reaches to enter the product: what
/// the nearest far viewer resolves. Within [0, 8192]. Absent keeps every static part.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFarProduct(float Cell = 1f, float MinimumSize = 0f) {
    /// <summary>The finest cell a product may ask for, in metres.</summary>
    public const float MinCell = 0.01f;
    /// <summary>The coarsest cell a product may ask for, in metres.</summary>
    public const float MaxCell = 1024f;

    /// <summary>Returns why the product cannot be built, or <see langword="null"/> when it can.</summary>
    /// <param name="path">The document path the refusal names the product by.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? Refusal(string path) {
        if (!float.IsFinite(f: Cell) || (Cell < MinCell) || (Cell > MaxCell)) {
            return $"{path}.cell is {Cell}; it lies within [{MinCell}, {MaxCell}] metres.";
        }
        if (!float.IsFinite(f: MinimumSize) || (MinimumSize < 0f) || (MinimumSize > WorldRenderDefaults.MaxFarDistance)) {
            return $"{path}.minimumSize is {MinimumSize}; it lies within [0, {WorldRenderDefaults.MaxFarDistance}] metres.";
        }
        return null;
    }
}
/// <summary>The key light's distant shadow map: one more shadow map past the far cascade, with texels of its own, so ridges
/// shadow walls hundreds of metres off without costing the near cascades precision. It draws only what stands still and
/// holds what it drew while the light turns within <paramref name="TurnDegrees"/> and the eye stays within an eighth of
/// the reach of where it was centred, so it costs a frame nothing between those changes. A device's quality row chooses it
/// (<see cref="WorldQualityPreset.DistantShadows"/>); a view's shadow quality halves its texels as it does the cascades'.
/// </summary>
/// <param name="Reach">The distance, in metres from the eye in every direction, the map covers: past the far cascade's
/// reach and up to the far distance, within [1, 8192].</param>
/// <param name="Texels">The texels along each side of the map at full shadow quality: a power of two from 256 through
/// 4096. Its texel is about twice the reach over this.</param>
/// <param name="TurnDegrees">The light turn, in degrees, a held map tolerates before it is drawn again: the most its
/// shadows lag a moving sun by, within [0, 10]. Zero, the default, takes the engine's: the turn that moves the shadow of
/// a caster as tall as the reach by a quarter of the penumbra the surface pass draws for it and a texel besides.</param>
/// <param name="Frames">The fewest frames one redraw is spread over, from 1 through <see cref="MaxFrames"/>: each draws a
/// share of the casters, and the map the receivers read changes only once the last share lands. The world's shadows
/// spread a redraw over more frames where its measured work asks.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDistantShadows(float Reach, int Texels, float TurnDegrees = 0f, int Frames = 1) {
    /// <summary>The fewest texels a side a map may ask for: the render graph's least distant map
    /// (<c>SdfWorldPackage.MeshDistantShadowMinResolution</c>, which this project cannot reference).</summary>
    public const int MinTexels = 256;
    /// <summary>The most texels a side a map may ask for: the render graph's largest distant map
    /// (<c>SdfWorldPackage.MeshDistantShadowMaxResolution</c>).</summary>
    public const int MaxTexels = 4096;
    /// <summary>The widest light turn, in degrees, a held map may tolerate.</summary>
    public const float MaxTurnDegrees = 10f;
    /// <summary>The most frames one redraw may be spread over.</summary>
    public const int MaxFrames = 16;

    /// <summary>Returns why the map cannot be drawn, or <see langword="null"/> when it can.</summary>
    /// <param name="path">The document path the refusal names the map by.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? Refusal(string path) {
        if (!float.IsFinite(f: Reach) || (Reach < WorldRenderDefaults.MinFarDistance) || (Reach > WorldRenderDefaults.MaxFarDistance)) {
            return $"{path}.reach is {Reach}; it lies within [{WorldRenderDefaults.MinFarDistance}, {WorldRenderDefaults.MaxFarDistance}] metres.";
        }
        if ((Texels < MinTexels) || (Texels > MaxTexels) || !System.Numerics.BitOperations.IsPow2(value: Texels)) {
            return $"{path}.texels is {Texels}; it is a power of two from {MinTexels} through {MaxTexels}.";
        }
        if (!float.IsFinite(f: TurnDegrees) || (TurnDegrees < 0f) || (TurnDegrees > MaxTurnDegrees)) {
            return $"{path}.turnDegrees is {TurnDegrees}; it lies within [0, {MaxTurnDegrees}] degrees.";
        }
        if ((Frames < 1) || (Frames > MaxFrames)) {
            return $"{path}.frames is {Frames}; it is a whole number from 1 through {MaxFrames}.";
        }

        return null;
    }
}
/// <summary>How the key light's far shadow cascade keeps up with what moves: the casters that moved are drawn again
/// <paramref name="Hertz"/> times a second of presentation time, and the cascade is drawn whole again when the light turns
/// past <paramref name="TurnDegrees"/>, the eye crosses an eighth of its reach or a static caster changes, that redraw
/// spread over at least the frames of one period. A dynamic caster's shadow lags it by up to one period, and every shadow
/// lags a turning light by up to the turn. A device's quality row chooses it
/// (<see cref="WorldQualityPreset.FarShadows"/>).</summary>
/// <param name="Hertz">The refreshes a second, a whole number from 1 through <see cref="MaxHertz"/>, where
/// <see cref="MaxHertz"/> draws moved casters every frame.</param>
/// <param name="TurnDegrees">The light turn, in degrees, a held cascade tolerates, within [0, <see cref="MaxTurnDegrees"/>]:
/// past it the cascade is drawn again. Zero, the default, takes the engine's: the turn that moves the shadow of a caster
/// as tall as the reach by a quarter of the penumbra the surface pass draws for it and a texel besides.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFarShadows(int Hertz, float TurnDegrees = 0f) {
    /// <summary>The rate that draws the cascade every frame.</summary>
    public const int MaxHertz = 60;
    /// <summary>The widest light turn, in degrees, a held cascade may tolerate.</summary>
    public const float MaxTurnDegrees = 1f;

    /// <summary>Returns why the cascade's rate cannot be applied, or <see langword="null"/> when it can.</summary>
    /// <param name="path">The document path the refusal names the row by.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? Refusal(string path) {
        if ((Hertz < 1) || (Hertz > MaxHertz)) {
            return $"{path}.hertz is {Hertz}; it is a whole number from 1 through {MaxHertz}.";
        }
        if (!float.IsFinite(f: TurnDegrees) || (TurnDegrees < 0f) || (TurnDegrees > MaxTurnDegrees)) {
            return $"{path}.turnDegrees is {TurnDegrees}; it lies within [0, {MaxTurnDegrees}] degrees.";
        }

        return null;
    }
}
/// <summary>One row of the nested-view table (<c>render.nested</c>): what a view shown inside another (through a window or
/// portal, on a screen a camera films, or a session's screen) gives up while it is presented at or below a share of the
/// display's height. A nested view takes the first row, in ascending <paramref name="Share"/>, whose share it does not
/// exceed, and gives up nothing past the last row; a world with no table gives up nothing at any size, so a portal that
/// fills the screen is lit as the room behind it.</summary>
/// <param name="Share">The largest share of the display's height, in <c>(0, 1]</c>, at which the row applies; rows ascend
/// strictly.</param>
/// <param name="Shadows">The finest shadow tier the view keeps: <see cref="ShadowTier.Off"/> drops its cascades, so its
/// lights take the probes' answer. Absent keeps the world's.</param>
/// <param name="AmbientOcclusion">Whether the view keeps ambient occlusion: false drops it. Absent keeps the world's.</param>
/// <param name="IndirectLevels">How many of the viewed world's irradiance-volume levels the view reads, counted from the
/// coarsest, at least one: that world traces, relights and publishes those alone, so a world seen through a small glass
/// measures only its coarse light. Absent, or at least the volume's level count, reads every level.</param>
/// <param name="RenderScale">The render-scale ceiling the view renders at, in <c>[0.125, 1]</c> of its presented extent, below
/// the presentation's own (the world's or device's <c>renderScale</c>), which every nested view keeps as its ceiling in any
/// case. A nested view never reconstructs over time, so a reduced one is upscaled spatially. Absent keeps the
/// presentation's.</param>
public sealed record WorldNestedViewQuality(
    float Share,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ShadowTier? Shadows = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AmbientOcclusion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? IndirectLevels = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? RenderScale = null);
/// <summary>Authored tier, sources, bounce depth and receiver application for the baked-mesh path's irradiance volume.</summary>
/// <param name="Sources">Bindable source gains in [0, 1]; absent gains are one.</param>
/// <param name="Bounces">Feedback sweeps after the direct sweep, capped by the selected tier. Absent uses that tier's limit.</param>
/// <param name="Apply">Bindable receiver-only controls. They do not change the traced radiance.</param>
/// <param name="Tier">The boot tier, Medium by default. The live session lever and selected quality preset override it.</param>
/// <param name="Budget">The irradiance work each second grants the volume and the lighting age it reports past; absent
/// bounds nothing.</param>
/// <param name="LightingGrid">Fixed angular and radiometric grids for indirect captures; absent leaves them continuous.</param>
public sealed record WorldRenderIndirect(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderIndirectSources? Sources = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Bounces = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderIndirectApply? Apply = null,
    [property: JsonConverter(typeof(StrictEnumConverter<SdfIndirectTier>))] SdfIndirectTier Tier = SdfIndirectTier.Medium,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderIndirectBudget? Budget = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderIndirectLightingGrid? LightingGrid = null);
/// <summary>Fixed grids for indirect light; direct lights remain continuous. Each zero or absent step disables
/// that grid, and capture history never changes its origin or spacing.</summary>
/// <param name="DirectionDegrees">Directional azimuth and elevation step in degrees, from zero through 180.</param>
/// <param name="Radiance">Fractional radiance step per power-of-two range, from zero through one.</param>
public sealed record WorldRenderIndirectLightingGrid(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? DirectionDegrees = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Radiance = null);
/// <summary>The irradiance volume's budget: a light change costs a relight of the probes' cached hits and a geometry
/// change costs a trace, each granted a count for each presented frame (never a rate of wall time, so a slow frame owes no
/// more probe work than a fast one), and overload lengthens the lighting's age rather than the frame. A device's quality
/// row (<see cref="WorldQualityPreset.IndirectBudget"/>) chooses its own. Every member is optional; an absent one leaves
/// that part unbounded.</summary>
/// <param name="RelightPerFrame">The probes relit each presented frame, at least one.</param>
/// <param name="TracePerFrame">The probes whose hits are traced each presented frame, at least one.</param>
/// <param name="Age">The lighting age, in seconds, past which the volume reports it is behind; positive.</param>
public sealed record WorldRenderIndirectBudget(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RelightPerFrame = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TracePerFrame = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Age = null) {
    /// <summary>Returns why the budget cannot be run, or <see langword="null"/> when it can: a count under one probe or a
    /// non-positive or non-finite age. Judges an authored world's budget, and a device row's, by one rule.</summary>
    /// <param name="path">The authored path the refusal names.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public string? Refusal(string path) {
        if (RelightPerFrame is < 1) { return $"{path}.relightPerFrame must be at least one probe a frame; omit it to leave relighting unbounded."; }
        if (TracePerFrame is < 1) { return $"{path}.tracePerFrame must be at least one probe a frame; omit it to leave tracing unbounded."; }
        if ((Age is { } age) && (!float.IsFinite(f: age) || (age <= 0f))) { return $"{path}.age must be a positive number of seconds."; }

        return null;
    }
}
/// <summary>Gains applied once where each source enters the traced light.</summary>
/// <param name="Lights">Explicit diffuse light gain.</param>
/// <param name="Emission">Material emission gain.</param>
/// <param name="Screens">Acquired screen-face emission gain.</param>
/// <param name="Sky">Physical sky radiance at world exits.</param>
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
/// <param name="Lights">The lights, at most <c>SdfLights.MaxLights</c>, in authored order; each view shades only those
/// that can reach what it shows (<c>SdfLightSelection</c>). Shadow-capable directionals
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
    [property: DocumentName(WorldNameKind.Clock, WorldNameRole.Names)]
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
[JsonDerivedType(typeof(WorldRenderLight.Spot), typeDiscriminator: "spot")]
[JsonDerivedType(typeof(WorldRenderLight.Occluder), typeDiscriminator: "occluder")]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
public abstract record WorldRenderLight {
    private WorldRenderLight() {
    }

    /// <summary>Gets the finite nonnegative gain of this light's diffuse contribution to indirect transport.
    /// Absent is one; zero excludes it from the traced indirect light while leaving direct shading unchanged. Rim and
    /// attenuation-only lights have no diffuse contribution. Literals, state bindings and keys use the same domain.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BindableScalar? Bounce { get; init; }
    /// <summary>Gets the light's name, which a section key addresses it by, or <see langword="null"/> for an unnamed
    /// light no key can address.</summary>
    [JsonIgnore]
    public abstract string? LightName { get; }

    /// <summary>A Lambert directional light, such as the sun.</summary>
    /// <param name="Direction">The direction from a lit surface toward the light, any nonzero length (normalized
    /// host-side before upload). Absent is the pinned sun direction.</param>
    /// <param name="Color">The light's colour, scaled to unit luminance so the light gives exactly its illuminance.
    /// Refused beside <paramref name="Temperature"/>. Absent is white, or the temperature's.</param>
    /// <param name="Illuminance">The illuminance on a surface facing the light, in lux: a clear midday sun gives about
    /// 100,000, an overcast day about 10,000, the full moon about 0.3. Absent is the pinned sun's.</param>
    /// <param name="AngularRadius">The light's angular radius, in <c>[0, atan 0.3]</c> radians: the penumbra
    /// half-slope is its tangent, so 0 casts a hard shadow. Read only when the light shadows. Absent is 0, a point
    /// source: the engine chooses no softness, and the standard package authors the soft edge its own lights cast.</param>
    /// <param name="Shadow">How this light competes for shadow slots. Absent is <see cref="WorldShadowMode.Never"/>.
    /// A directional without a presented shadow slot is scaled by ambient occlusion instead.</param>
    /// <param name="Name">The unique identity a section key and the shadow allocator address. Required when
    /// <paramref name="Shadow"/> is <see cref="WorldShadowMode.Always"/> or <see cref="WorldShadowMode.Auto"/>.</param>
    /// <param name="Temperature">The light's colour as a black body's at this temperature, in kelvin, in
    /// <c>[1000, 40000]</c>: noon sunlight about 5,500, a sunset about 3,000. Refused beside
    /// <paramref name="Color"/>.</param>
    public sealed record Directional(
        BindableDirection? Direction = null,
        BindableColor? Color = null,
        BindableScalar? Illuminance = null,
        BindableAngle? AngularRadius = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldShadowMode? Shadow = null,
        [property: DocumentText("a light name a section key addresses")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Temperature = null
    ) : WorldRenderLight {
        /// <inheritdoc/>
        public override string? LightName => Name;
    }
    /// <summary>A view-dependent silhouette brighten: <c>luminance · color · pow(1 − saturate(dot(normal,
    /// −rayDirection)), power)</c>, added after the material shade.</summary>
    /// <param name="Color">The rim's colour.</param>
    /// <param name="Luminance">The luminance the brighten adds at a silhouette, in nits. Absent is zero, which adds
    /// nothing.</param>
    /// <param name="Power">The falloff exponent — larger confines the highlight nearer the silhouette. Absent is the
    /// engine default.</param>
    /// <param name="Name">The name a section key addresses the light by, unique among the lights.</param>
    public sealed record Rim(
        BindableColor? Color = null,
        BindableScalar? Luminance = null,
        BindableScalar? Power = null,
        [property: DocumentText("a light name a section key addresses")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderLight {
        /// <inheritdoc/>
        public override string? LightName => Name;
    }
    /// <summary>A point light, such as a bulb: its luminous intensity alike in every direction, falling off as the inverse
    /// square of the distance under a window that ends it smoothly and exactly at its range, the distance past which
    /// its light would show below <c>SdfPhotometry.RangeThreshold</c> of display white at the world's brightest
    /// exposure. An unanchored light is static, so the irradiance volume traces it: its light bounces off the static
    /// meshes, and each probe records whether it sees the light, so a surface the volume covers takes the light only
    /// where the probes about it see it, and a fixture lights nothing through the wall or slab between them. An
    /// anchored light moves with its anchor, lights through every wall and bounces nothing.</summary>
    /// <param name="Position">The world-space position for a static (unanchored) light. Absent is the world origin.
    /// An offset in the anchor frame when an anchor is authored.</param>
    /// <param name="Anchor">An entity, entity part, or placement frame. A missing live target disables the light.</param>
    /// <param name="Color">The light's colour, scaled to unit luminance. Refused beside
    /// <paramref name="Temperature"/>.</param>
    /// <param name="Intensity">The luminous intensity, in candela. Refused beside <paramref name="Power"/>; absent with
    /// it is the engine default.</param>
    /// <param name="Name">The name a section key addresses the light by, unique among the lights.</param>
    /// <param name="Power">The luminous power, in lumens, emitted alike in every direction: a 60 W incandescent bulb
    /// gives about 800. Refused beside <paramref name="Intensity"/>.</param>
    /// <param name="Temperature">The light's colour as a black body's, in kelvin, in <c>[1000, 40000]</c>: a warm
    /// bulb about 2,700. Refused beside <paramref name="Color"/>.</param>
    public sealed record Point(
        BindableVector3? Position = null,
        WorldAnchor? Anchor = null,
        BindableColor? Color = null,
        BindableScalar? Intensity = null,
        [property: DocumentText("a light name a section key addresses")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Power = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Temperature = null
    ) : WorldRenderLight {
        /// <inheritdoc/>
        public override string? LightName => Name;
    }
    /// <summary>A spot light, such as a downlight: a point light whose light leaves in a cone about its aim, at full
    /// strength inside the inner angle and smoothly to none at the outer, with the point light's windowed falloff,
    /// range, and tracing and occlusion by the irradiance volume while unanchored.</summary>
    /// <param name="Position">The world-space position, or the offset in the anchor's frame.</param>
    /// <param name="Direction">The direction the light shines along, any nonzero length; in the anchor's frame when an
    /// anchor is authored. Absent is straight down.</param>
    /// <param name="Anchor">An entity, entity part, or placement frame. A missing live target disables the light.</param>
    /// <param name="Angle">The cone's outer half angle, in <c>(0, π/2]</c> radians. Absent is 45 degrees.</param>
    /// <param name="InnerAngle">The half angle inside which the light is at full strength, in <c>[0, Angle]</c>
    /// radians. Absent is three quarters of <paramref name="Angle"/>.</param>
    /// <param name="Color">The light's colour, scaled to unit luminance. Refused beside
    /// <paramref name="Temperature"/>.</param>
    /// <param name="Intensity">The luminous intensity along the aim, in candela. Refused beside
    /// <paramref name="Power"/>; absent with it is the engine default.</param>
    /// <param name="Power">The luminous power, in lumens, emitted alike across the outer cone. Refused beside
    /// <paramref name="Intensity"/>.</param>
    /// <param name="Temperature">The light's colour as a black body's, in kelvin, in <c>[1000, 40000]</c>. Refused
    /// beside <paramref name="Color"/>.</param>
    /// <param name="Name">The name a section key addresses the light by, unique among the lights.</param>
    public sealed record Spot(
        BindableVector3? Position = null,
        BindableDirection? Direction = null,
        WorldAnchor? Anchor = null,
        BindableAngle? Angle = null,
        BindableAngle? InnerAngle = null,
        BindableColor? Color = null,
        BindableScalar? Intensity = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Power = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Temperature = null,
        [property: DocumentText("a light name a section key addresses")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderLight {
        /// <summary>The cone's outer half angle a spot that authors none takes, in radians: 45 degrees.</summary>
        public const float DefaultAngle = (MathF.PI / 4f);
        /// <summary>The share of the outer half angle a spot that authors no inner angle is at full strength within.</summary>
        public const float DefaultInnerShare = 0.75f;

        /// <inheritdoc/>
        public override string? LightName => Name;
    }
    /// <summary>A smooth attenuation field. Position is world space, or an offset in an anchored entity/part/placement
    /// frame. Missing anchors disable it. Radius is positive; Weight is in [0, 1]. It shares the lights table's
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
        [property: DocumentText("a light name a section key addresses")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderLight {
        /// <inheritdoc/>
        public override string? LightName => Name;
    }
}
/// <summary>The stylized curvature enrichment, keyed on mean curvature estimated from the baked triangle's
/// interpolated vertex normals in world units. Every field is optional individually — absent resolves to the
/// engine's default. The three gains share one shading gate: while all are zero their enrichment is skipped.
/// The surface pass supplies the same curvature for edge weathering without querying the field.</summary>
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
    [property: DocumentName(WorldNameKind.Clock, WorldNameRole.Names)]
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
        [property: DocumentText("a sky layer name a section key addresses")]
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
    /// <summary>Gets the luminance, in nits, a colour of one shows at in this layer: every colour and brightness the layer
    /// carries scales by it (a gradient's stops, a disc's, the stars', the clouds', an aurora's, a noise's, a pattern's,
    /// a panel's, a panorama's image, a view's fallback), so the sky is as bright beside its lights as the real one: a
    /// clear sky's horizon about 7,000, a night sky well under one. Absent is <c>SdfSky.DefaultLuminance</c>, a clear
    /// day's sky beside the pinned sun.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BindableScalar? Luminance { get; init; }
    /// <summary>Gets the layer's own transform about the sky frame. Absent is none.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorldRenderSkyTransform? Transform { get; init; }
    /// <summary>Gets the clock, by name in the <c>timeline</c> section, whose phase moves the layer's own motion: an
    /// aurora's curtains, a noise field's slide, a pattern's scroll. Absent holds them still.</summary>
    [DocumentName(WorldNameKind.Clock, WorldNameRole.Names)]
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
        [property: DocumentText("a sky layer name a section key addresses")]
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
        [property: DocumentText("a sky layer name a section key addresses")]
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
        [property: DocumentText("a sky layer name a section key addresses")]
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
        [property: DocumentText("a sky layer name a section key addresses")]
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
        [property: DocumentText("a sky layer name a section key addresses")]
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
        [property: DocumentText("a sky layer name a section key addresses")]
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
        [property: DocumentText("a sky layer name a section key addresses")]
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
        [property: DocumentText("a sky layer name a section key addresses")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>Another world in the sky: a second <c>sdf.world</c> instance (<c>sky$&lt;name&gt;</c>) rendering the
    /// destination's session from an anchor in it, turned with the viewer's camera. Without <paramref name="Follow"/> the
    /// camera stays at the anchor, so the destination is a backdrop at infinity; with it the camera moves as the viewer
    /// does, so the destination is a place at a distance that grows as the viewer nears it, and the two worlds share
    /// one frame. It renders only while the viewer's previous frame showed it, and only the rectangle its mask covers (a
    /// cone, the cone its <paramref name="Bound"/> subtends, or the whole frustum with neither). The camera alone sees
    /// it, since the environment map binds no screen. At most
    /// <c>SdfSky.MaxInfinityViews</c> view and far layers a world, nested worlds included; a view that cannot render
    /// draws its <paramref name="Fallback"/> colour.</summary>
    /// <param name="Name">The layer's name, required: the instance is <c>sky$name</c> and its counted rows carry the name, so
    /// it is one part free of <c>$</c> and <c>~</c>, unique among the layers.</param>
    /// <param name="Destination">The destination world, by the name a session screen's destination takes. Required.</param>
    /// <param name="Anchor">The point in the destination the camera sits at, whatever the viewer does; with
    /// <paramref name="Follow"/>, the point of the destination this world's origin stands at. Absent is its
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
    /// <param name="Follow">Whether the camera moves with the viewer's. Absent is not.</param>
    /// <param name="Bound">The sphere that bounds what the view shows, in the destination's coordinates. Absent is none.</param>
    /// <param name="Coverage">Whether the view draws only the destination's geometry, so this world's sky shows around
    /// it. Absent is not: the destination's own sky fills the view.</param>
    /// <param name="FarProduct">Whether the view draws the destination's far product (its settled static scene, gathered
    /// at the cell its own <c>render.farProduct</c> asks for) rather than a residency of the whole destination:
    /// no bake, none of its bodies, creations or screens, and only the product's triangles held. A view that asks for it
    /// and has no product yet draws the destination whole until one is built from that residency. Absent is not.</param>
    public sealed record View(
        [property: DocumentText("a sky layer name a section key addresses")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: DocumentText("a destination name")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Destination = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DocumentVector3? Anchor = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Turn = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? Scale = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Refresh = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] float? FarDistance = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Shadows = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AmbientOcclusion = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Fallback = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Follow = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldSkyViewBound? Bound = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Coverage = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? FarProduct = null
    ) : WorldRenderSkyLayer {
        /// <inheritdoc/>
        public override string? LayerName => Name;
    }
    /// <summary>The sphere that bounds what a <see cref="View"/> shows.</summary>
    /// <param name="Center">The sphere's centre, in the destination's coordinates.</param>
    /// <param name="Radius">The sphere's radius, in world units, positive.</param>
    public sealed record WorldSkyViewBound(DocumentVector3 Center, float Radius);
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
        [property: DocumentText("a sky layer name a section key addresses")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
        [property: DocumentName(WorldNameKind.Prototype, WorldNameRole.Names)]
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
/// <param name="Luminance">The luminance, in nits, an authored <paramref name="Color"/> of one in-scatters at. Absent is
/// <c>SdfSky.DefaultLuminance</c>; a fog that reads the sky takes the sky's own.</param>
public sealed record WorldRenderFog(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Density = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Color = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldRenderAirHeight? Height = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Luminance = null
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
/// <param name="Luminance">The luminance, in nits, a <paramref name="Color"/> of one in-scatters at. Absent is
/// <c>SdfSky.DefaultLuminance</c>.</param>
public sealed record WorldRenderMedium(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Surface = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Extinction = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableColor? Color = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindableScalar? Luminance = null
);
/// <summary>A world's display transform (<see cref="WorldRenderDefaults.Tone"/>): Uchimura's toe, linear section and
/// shoulder over exposed scene light, where one is SDR white. Every value is the author's; the standard package names
/// one (<c>worlds/standard/tones.puck</c>). Each must be finite, with <see cref="Peak"/>, <see cref="Contrast"/> and
/// <see cref="Toe"/> positive, <see cref="LinearStart"/> in (0, <see cref="Peak"/>) and <see cref="LinearLength"/> in
/// [0, 1).</summary>
/// <param name="Peak">The value the shoulder rolls into.</param>
/// <param name="Contrast">The linear section's slope.</param>
/// <param name="LinearStart">The exposed value where the toe gives way to the linear section.</param>
/// <param name="LinearLength">The share of the range above <paramref name="LinearStart"/> the linear section spans.</param>
/// <param name="Toe">The toe's power: above one it darkens the shadows, below one it lifts them.</param>
public sealed record WorldToneCurve(float Peak, float Contrast, float LinearStart, float LinearLength, float Toe) {
    /// <summary>Gets whether every value lies in its range.</summary>
    [JsonIgnore]
    public bool IsValid => float.IsFinite(f: Peak) && float.IsFinite(f: Contrast) && float.IsFinite(f: LinearStart) &&
        float.IsFinite(f: LinearLength) && float.IsFinite(f: Toe) && (Peak > 0f) && (Contrast > 0f) && (Toe > 0f) &&
        (LinearStart > 0f) && (LinearStart < Peak) && (LinearLength >= 0f) && (LinearLength < 1f);
}
/// <summary>The gains on lighting derived from the lighting-visible sky layers.</summary>
/// <param name="Ambient">The diffuse irradiance gain. Absent is one; zero disables harmonic lighting.</param>
/// <param name="Reflection">The reflection gain. Absent is one; zero disables reflection lookups.</param>
public sealed record WorldRenderEnvironment(BindableScalar? Ambient = null, BindableScalar? Reflection = null);
