using Puck.Hosting;
using Puck.SdfVm;
using Puck.SignedDistance;
using Puck.World.Authoring;

namespace Puck.World;

/// <summary>
/// The world's live render settings — the engine-wide levers (shadows, ambient occlusion, render scale) mutated by
/// console verbs in real time and read by <c>WorldFramePresenter</c> every captured frame. Session state, not
/// identity: per-player preferences belong on the profile.
/// </summary>
public sealed partial class WorldRenderSettings {
    /// <summary>The sky layer audition, never folded into a saved definition.</summary>
    public Client.WorldSkyAudition SkyLayers { get; } = new();

    private bool m_ambientOcclusion;
    private bool m_rayQueries;
    private WorldDistantShadows? m_distantShadows;
    private SdfViewsRate? m_viewsRate;
    /// <summary>The view shading rate selected by the active quality table.</summary>
    public SdfViewsRate? ViewsRate { get => m_viewsRate; set { m_viewsRate = value; m_revision++; } }
    private WorldFarShadows? m_farShadows;
    private WorldRenderIndirectBudget? m_indirectBudget;
    private bool m_cadenceGate;
    private SdfMeshCullLevel m_cull;
    private SdfMeshDetailLevel m_detail;
    private SdfUpdateRates m_updateRates;
    private bool m_dynamicResolution;
    private SdfIndirectTier m_indirectTier;
    private float? m_exposurePin;
    private float m_renderScale;
    private int m_revision;
    private ShadowTier m_shadows;
    private Client.WorldShadowSettings m_shadowSlots;
    private bool m_temporal;
    private bool? m_toonOverride;
    private bool m_shadePrograms = true;
    /// <summary>Gets or sets whether authored shading programs run (<c>world.shade programs</c>, the default) rather than
    /// the material layers alone (<c>world.shade layers</c>), the measurement lever until the layers migrate.</summary>
    public bool ShadePrograms { get => m_shadePrograms; set { m_shadePrograms = value; m_revision++; } }
    private float m_upscaleSharpness;
    private WorldSkyTier m_skyQuality;

    /// <summary>Initializes a new instance of the <see cref="WorldRenderSettings"/> class from the world definition's
    /// render-lever boot defaults (<see cref="WorldRenderDefaults"/>), copied into the live, mutable settings the
    /// console verbs move from here.</summary>
    /// <param name="defaults">The render-lever boot defaults to wake on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    public WorldRenderSettings(WorldRenderDefaults defaults) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        Shadows = defaults.Shadows;
        ShadowSlots = Client.WorldShadowSettings.From(render: defaults);
        AmbientOcclusion = defaults.AmbientOcclusion;
        RenderScale = defaults.RenderScale;
        UpscaleSharpness = defaults.UpscaleSharpness;
        Temporal = defaults.Temporal;
        SkyQuality = defaults.SkyQuality;
        SkyFieldScale = defaults.SkyFieldScale;
        IndirectTier = (defaults.Indirect?.Tier ?? SdfIndirectTier.Medium);
        RayQueries = defaults.RayQueries;
        DistantShadows = defaults.DistantShadows;
        ViewsRate = defaults.ViewsRate;
        FarShadows = defaults.FarShadows;
        DynamicResolution = defaults.DynamicResolution;
        CadenceGate = true;
        Cull = SdfMeshCullLevel.Occlusion;
        UpdateRates = SdfUpdateRates.All;
    }

    /// <summary>Whether ambient occlusion is on. Boots at the definition's default (<see langword="false"/> in the built-in
    /// world); the <c>world.ao</c> verb toggles it live (it rides each view's
    /// <see cref="Puck.SdfVm.SdfViewQuality.DisableAmbientOcclusion"/> lane, so no rebuild).</summary>
    public bool AmbientOcclusion { get => m_ambientOcclusion; set { m_ambientOcclusion = value; m_revision++; } }
    /// <summary>Whether the indirect volume's background rays take the device's ray hardware where it has it
    /// (<see cref="Puck.SdfVm.SdfFrame.RayQueries"/>), for every residency the session presents, routed worlds' included
    /// (<see cref="Client.WorldSessionSceneEmitter"/>). Boots at <c>render.rayQueries</c>; the <c>world.rays</c> verb and a
    /// quality preset's row move it live.</summary>
    public bool RayQueries { get => m_rayQueries; set { m_rayQueries = value; m_revision++; } }
    /// <summary>The key light's distant shadow map (<see cref="Puck.SdfVm.SdfFrame.DistantShadows"/>) for every residency
    /// the session presents, routed worlds' included: its reach, texels and the light turn a held map tolerates, or
    /// <see langword="null"/> for none. Boots at <c>render.distantShadows</c>; a quality preset's row (the device table)
    /// moves it live.</summary>
    public WorldDistantShadows? DistantShadows { get => m_distantShadows; set { m_distantShadows = value; m_revision++; } }
    /// <summary>The key light's far cascade rate (<see cref="Puck.SdfVm.SdfFrame.FarShadows"/>) for every residency the
    /// session presents, routed worlds' included: its refreshes a second and the light turn a held cascade tolerates, or
    /// <see langword="null"/> to refresh it every frame. Boots at <c>render.farShadows</c>; a quality preset's row (the
    /// device table) moves it live.</summary>
    public WorldFarShadows? FarShadows { get => m_farShadows; set { m_farShadows = value; m_revision++; } }
    /// <summary>The texels a side of a full-resolution shadow cascade (<see cref="Puck.SdfVm.SdfFrame.ShadowTexels"/>) for
    /// every residency the session presents, routed worlds' included, or <see langword="null"/> for the engine's
    /// <see cref="Puck.SdfVm.SdfShadowResolutions.DefaultTexels"/>. A quality preset's row (the device table) moves it
    /// live.</summary>
    public int? ShadowTexels { get => m_shadowTexels; set { m_shadowTexels = value; m_revision++; } }
    private int? m_shadowTexels;
    /// <summary>Returns the texels a side a frame's full-resolution cascades take.</summary>
    /// <param name="row">The presenting session's device row, or <see langword="null"/> for none.</param>
    /// <returns>The row's texels, else <see cref="Puck.SdfVm.SdfShadowResolutions.DefaultTexels"/>.</returns>
    public static int ResolveShadowTexels(int? row) => (row ?? Puck.SdfVm.SdfShadowResolutions.DefaultTexels);
    /// <summary>The irradiance work each presented frame grants every residency's volume the session presents, routed
    /// worlds' included (<see cref="Puck.SdfVm.SdfFrame.IndirectBudget"/>): the device table's row, or
    /// <see langword="null"/> to run each world's own <c>render.indirect.budget</c> (<see cref="ResolveIndirectBudget"/>).
    /// A quality preset's row moves it live.</summary>
    public WorldRenderIndirectBudget? IndirectBudget { get => m_indirectBudget; set { m_indirectBudget = value; m_revision++; } }
    /// <summary>Returns the budget a frame grants the volume: a device table's row replaces a world's own whole, since
    /// what a frame may spend on lighting is the device's cost, never a painting world's.</summary>
    /// <param name="authored">The world's own budget.</param>
    /// <param name="row">The presenting session's device row, or <see langword="null"/> for none.</param>
    /// <returns>The budget the frame carries.</returns>
    public static SdfIndirectBudget ResolveIndirectBudget(SdfIndirectBudget authored, WorldRenderIndirectBudget? row) => ((row is { } chosen)
        ? new SdfIndirectBudget(RelightPerFrame: (chosen.RelightPerFrame ?? 0), TracePerFrame: (chosen.TracePerFrame ?? 0), Age: (chosen.Age ?? 0f))
        : authored);
    /// <summary>The exposure pin (<c>world.exposure pin</c>): an <c>EV100</c> every view the session presents renders
    /// manual at, with no local adaptation (<see cref="SdfExposure.HeldAt"/>), so look comparisons hold one exposure;
    /// <see langword="null"/> (the default) leaves each world's authored and held exposure exactly as it is. Presentation
    /// only: it never reaches a document or simulation state.</summary>
    public float? ExposurePin {
        get => m_exposurePin;
        set {
            if (value is { } ev100 && (!float.IsFinite(f: ev100) || (ev100 < SdfPhotometry.MinimumEv100) || (ev100 > SdfPhotometry.MaximumEv100))) {
                throw new ArgumentOutOfRangeException(paramName: nameof(value));
            }
            m_exposurePin = value;
            m_revision++;
        }
    }
    /// <summary>Returns a world's resolved exposure under the session's pin: held at the pin while one is set, unchanged
    /// otherwise.</summary>
    /// <param name="exposure">The world's resolved exposure.</param>
    /// <param name="pin">The pin, or <see langword="null"/> for none.</param>
    /// <returns>The exposure its views render with.</returns>
    public static SdfExposure Pinned(SdfExposure exposure, float? pin) => ((pin is { } ev100) ? exposure.HeldAt(ev100: ev100) : exposure);
    /// <summary>The indirect-light tier, which sizes the irradiance volume. It boots from <c>render.indirect.tier</c>,
    /// Medium when absent, and follows the live session lever or quality preset thereafter.</summary>
    public SdfIndirectTier IndirectTier {
        get => m_indirectTier;
        set {
            if (!Enum.IsDefined(value: value)) { throw new ArgumentOutOfRangeException(paramName: nameof(value)); }
            m_indirectTier = value;
            m_revision++;
        }
    }
    /// <summary>Whether a frame whose render inputs match the previous one re-composites the retained image instead of
    /// re-rendering (default <see langword="true"/>; pixel-identical either way). Set <see langword="false"/> (via
    /// <c>world.cadence off</c>) to render every frame, so <c>world.counters gpu</c> measures a still scene. Session state, never
    /// durable config; rides <see cref="Puck.SdfVm.SdfFrame.EnableCadenceGate"/>.</summary>
    public bool CadenceGate { get => m_cadenceGate; set { m_cadenceGate = value; m_revision++; } }
    /// <summary>How much of the mesh draws each raster pass skips (default <see cref="SdfMeshCullLevel.Occlusion"/>;
    /// pixel-identical at every level). <c>world.cull frustum</c> keeps the cull by box alone and <c>world.cull off</c>
    /// draws every draw in every view and cascade, which measure each level's saving. Session state, never durable
    /// config; rides <see cref="Puck.SdfVm.SdfFrame.MeshCull"/>.</summary>
    public SdfMeshCullLevel Cull { get => m_cull; set { m_cull = value; m_revision++; } }
    /// <summary>Which levels of detail the raster passes draw (default <see cref="SdfMeshDetailLevel.Auto"/>, each pass's
    /// choice by the error it can show). <c>world.detail 0</c>, <c>1</c> or <c>2</c> draws one level everywhere, which
    /// shows what a level changes. Session state, never durable config; rides
    /// <see cref="Puck.SdfVm.SdfFrame.MeshDetail"/>.</summary>
    public SdfMeshDetailLevel Detail { get => m_detail; set { m_detail = value; m_revision++; } }
    /// <summary>Which work updates less often when it is far or small on screen (<c>world.rates</c>; default
    /// <see cref="SdfUpdateRates.All"/>): bodies' poses, nested views, screens' uploads and the far shadow cascade, each
    /// only where the drop is barely perceptible (<see cref="SdfUpdateRateTiers"/>). Session state, never durable config;
    /// rides <see cref="Puck.SdfVm.SdfFrame.UpdateRates"/>.</summary>
    public SdfUpdateRates UpdateRates { get => m_updateRates; set { m_updateRates = value; m_revision++; } }
    /// <summary>Gets the counters every frame's rated work counts into (<see cref="SdfUpdateRateCounts"/>), which
    /// <c>world.counters sdf.rates</c> reads.</summary>
    public Puck.Abstractions.Counting.WorkCounterSet RateCounts { get; } = SdfUpdateRateCounts.Counters();
    /// <summary>Gets the window every frame's rated work records its achieved intervals between updates into
    /// (<see cref="SdfUpdateRateIntervals"/>), which <c>world.rates</c> prints against each tier's period.</summary>
    public SdfUpdateRateIntervals RateIntervals { get; } = new();
    /// <summary>Gets the items each lever rated on the latest frame and the tier each took (<see cref="SdfUpdateRateReadings"/>),
    /// which <c>world.rates</c> lists.</summary>
    public SdfUpdateRateReadings RateReadings { get; } = new();
    /// <summary>Gets or sets whether each frame carries its rated items' tiers (<see cref="SdfFrame.SlotRateCodes"/>,
    /// <see cref="SdfFrame.ScreenRateCodes"/>), which a tier view shows; <c>world.debug-view</c> sets it while one is
    /// selected. Presentation only.</summary>
    public bool RateTints { get; set; }
    /// <summary>Whether dynamic resolution moves the world's own views' render grid each frame
    /// (<c>world.render-scale auto</c>; <see cref="Client.WorldDynamicResolution"/>), between
    /// each view's quality floor and <see cref="RenderCeiling"/>. The grid moves inside the allocation, so a
    /// frame never rebuilds; turning it on or off rebuilds a native view once, since it moves its ceiling.</summary>
    public bool DynamicResolution { get => m_dynamicResolution; set { m_dynamicResolution = value; m_revision++; } }
    /// <summary>Gets the render-scale ceiling each of the world's own views is allocated at: <see cref="RenderScale"/>,
    /// except that a scale quantized to native is lowered to the three-quarter tier while
    /// <see cref="DynamicResolution"/> is on, since a view at a native ceiling reconstructs nothing and has no grid to move.</summary>
    public float RenderCeiling => ((m_dynamicResolution && (RenderGraphExtent.Quantize(fraction: m_renderScale) >= 1d))
        ? WorldRenderScaleTiers.Scale(tier: WorldRenderScaleTier.ThreeQuarter)
        : m_renderScale);
    /// <summary>The engine-wide internal render-scale fraction, applied to every player view's
    /// <see cref="Puck.SdfVm.SdfViewSnapshot.RenderScale"/> each frame. Named tiers initialize it, while
    /// <c>world.render-scale</c> also accepts a live numeric fraction/percentage for performance sweeps. Native 1.0 is
    /// the bit-exact fast path; lower values use the compositor reconstruction selected by
    /// <see cref="UpscaleSharpness"/>.</summary>
    public float RenderScale { get => m_renderScale; set { m_renderScale = value; m_revision++; } }
    /// <summary>A monotonic counter advanced by every lever write — the cheap watch the editor HUD keys its
    /// live-session-act tag and drift refresh on (no per-frame drift recompute).</summary>
    public int Revision => unchecked((m_revision + SkyLayers.Revision));
    /// <summary>The live shadow-slot policy, set by the world's quality row and persisted by save.</summary>
    public Client.WorldShadowSettings ShadowSlots { get => m_shadowSlots; set { m_shadowSlots = value; m_revision++; } }
    /// <summary>The shadow tier: boots at the definition's <c>render.shadows</c>, and the <c>world.shadows</c> verb and a
    /// quality preset move it live. <see cref="ShadowTier.Off"/> renders no cascade
    /// (<see cref="Puck.SdfVm.SdfViewQuality.DisableSoftShadows"/>); every other tier chooses the cascades' resolution
    /// (<see cref="Puck.SdfVm.SdfViewQuality.ShadowResolution"/>, <see cref="Client.WorldFramePresenter.ShadowResolutionOf"/>),
    /// and a change between tiers rebuilds each view's graph once at the new size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value set is not a defined tier.</exception>
    public ShadowTier Shadows {
        get => m_shadows;
        set {
            if (!Enum.IsDefined(value: value)) { throw new ArgumentOutOfRangeException(paramName: nameof(value)); }
            m_shadows = value;
            m_revision++;
        }
    }
    /// <summary>Whether the world's own views reconstruct over time (<c>world.temporal</c>): each jitters its samples
    /// and resolves them over its history into its output, at native or reduced render scale, through each view's
    /// <see cref="Puck.SdfVm.SdfViewQuality.Temporal"/> lane. A change rebuilds each view's graph beside the installed
    /// one. The world's own views include every layout slot that names a camera; the camera views screens film and session
    /// views never ask for it.</summary>
    public bool Temporal { get => m_temporal; set { m_temporal = value; m_revision++; } }
    /// <summary>Gets or sets whether every cel style shows as authored (<c>world.toon</c>): false suppresses every style,
    /// a material's own and the world's default alike, so every styled material shades continuously and uninked and one
    /// view compares continuous against cel. True until written.</summary>
    public bool Toon { get => m_toonOverride ?? true; set { m_toonOverride = value; m_revision++; } }
    /// <summary>Gets the session's lever for nested worlds; <see langword="null"/> shows each world's styles as
    /// authored.</summary>
    public bool? ToonOverride => m_toonOverride;
    /// <summary>Resolves a world's default cel style under the live lever.</summary>
    /// <param name="render">The current world render settings.</param>
    /// <returns>The world's default style, suppressed while <see cref="Toon"/> is false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="render"/> is <see langword="null"/>.</exception>
    public SdfToon ResolveToon(WorldRenderDefaults render) {
        ArgumentNullException.ThrowIfNull(render);
        return ToonStyleDocument.ResolveDefault(toon: render.Toon, shown: m_toonOverride);
    }
    /// <summary>Returns a world's display transform (<c>render.tone</c>), or the neutral one when it authors none.</summary>
    /// <param name="render">The world's render settings.</param>
    /// <returns>The display transform.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="render"/> is <see langword="null"/>.</exception>
    public static SdfToneCurve ToneOf(WorldRenderDefaults render) {
        ArgumentNullException.ThrowIfNull(render);

        return ((render.Tone is { IsValid: true } tone)
            ? new SdfToneCurve(Contrast: tone.Contrast, LinearLength: tone.LinearLength, LinearStart: tone.LinearStart, Peak: tone.Peak, Toe: tone.Toe)
            : SdfToneCurve.None);
    }
    /// <summary>The sky's quality tier (<c>world.sky-quality</c>): a layer below it writes no entry and counts no work,
    /// and below <see cref="WorldSkyTier.High"/> each kind draws its reduced form. Boots at the definition's
    /// <c>render.skyQuality</c>; a quality preset's <c>sky</c> row sets it.</summary>
    public WorldSkyTier SkyQuality { get => m_skyQuality; set { m_skyQuality = value; m_revision++; } }
    /// <summary>The continuous reconstruction sharpness: the spatial resolve's blend from bilinear (0) to clamped
    /// Catmull-Rom (1), and the strength of the contrast-adaptive sharpen <c>place</c> applies to a temporally resolved
    /// view at its rect's own extent. A native view that does not reconstruct ignores it.</summary>
    public float UpscaleSharpness { get => m_upscaleSharpness; set { m_upscaleSharpness = value; m_revision++; } }
}
