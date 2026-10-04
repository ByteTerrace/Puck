using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.World;

/// <summary>The soft-shadow candidate-mask policy. <see cref="Auto"/> keeps exact gathers for small sessions and uses
/// the camera-tile approximation at the declared fleet tiers; the other values are live profiling/authoring overrides.</summary>
public enum ShadowMaskMode {
    Auto,
    ExactGather,
    CameraTile,
}
/// <summary>The soft-shadow march quality policy. Auto keeps the exact engine path for small sessions and selects the
/// bounded-cost approximation at fleet scale; the other values are live profiling/authoring overrides.</summary>
public enum ShadowMarchMode {
    Auto,
    Exact,
    Fast,
}
/// <summary>The ambient-occlusion sampling policy. Auto keeps the quality ladder for small sessions and selects the
/// calibrated one-sample contact approximation at fleet scale; the other values are live profiling overrides.</summary>
public enum AmbientOcclusionMode {
    Auto,
    Exact,
    Fast,
}
/// <summary>
/// The world's live render settings — the engine-wide levers (shadows, ambient occlusion, render scale) mutated by
/// console verbs in real time and read by <c>WorldFramePresenter</c> every captured frame. Session state, not
/// identity: per-player preferences belong on the profile.
/// </summary>
public sealed partial class WorldRenderSettings {
    /// <summary>The sky layer audition, never folded into a saved definition.</summary>
    public Client.WorldSkyAudition SkyLayers { get; } = new();

    private bool m_ambientOcclusion;
    private AmbientOcclusionMode m_ambientOcclusionQuality;
    private volatile int m_bakes;
    private bool m_cadenceGate;
    private bool m_dynamicResolution;
    private bool m_farBound;
    private SdfIndirectTier m_indirectTier;
    private float m_renderScale;
    private int m_revision;
    private float m_shadowCrowdRadius;
    private ShadowMarchMode m_shadowMarch;
    private ShadowMaskMode m_shadowMask;
    private float m_shadowReach;
    private Client.WorldShadowSettings m_shadowSlots;
    private bool m_temporal;
    private bool m_shadowAmortize;
    private float m_upscaleSharpness;
    private WorldSkyTier m_skyQuality;

    /// <summary>Initializes a new instance of the <see cref="WorldRenderSettings"/> class from the world definition's
    /// render-lever boot defaults (<see cref="WorldRenderDefaults"/>), copied into the live, mutable settings the
    /// console verbs move from here.</summary>
    /// <param name="defaults">The render-lever boot defaults to wake on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    public WorldRenderSettings(WorldRenderDefaults defaults) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        ShadowReach = ShadowTiers.Scale(tier: defaults.Shadows);
        ShadowSlots = Client.WorldShadowSettings.From(render: defaults);
        ShadowCrowdRadius = defaults.ShadowCrowdRadius;
        ShadowMask = ShadowMaskMode.Auto;
        ShadowMarch = ShadowMarchMode.Auto;
        AmbientOcclusionQuality = AmbientOcclusionMode.Auto;
        AmbientOcclusion = defaults.AmbientOcclusion;
        RenderScale = defaults.RenderScale;
        UpscaleSharpness = defaults.UpscaleSharpness;
        Temporal = defaults.Temporal;
        ShadowAmortize = defaults.ShadowAmortize;
        SkyQuality = defaults.SkyQuality;
        DynamicResolution = defaults.DynamicResolution;
        FarBound = true;
        CadenceGate = true;

    }

    /// <summary>Whether ambient occlusion is on. Boots at the definition's default (<see langword="false"/> in the built-in
    /// world); the <c>world.ao</c> verb toggles it live (it rides each view's
    /// <see cref="Puck.SdfVm.SdfViewQuality.DisableAmbientOcclusion"/> lane, so no rebuild).</summary>
    public bool AmbientOcclusion { get => m_ambientOcclusion; set { m_ambientOcclusion = value; m_revision++; } }
    /// <summary>The live ambient-occlusion sampling policy. Auto selects the one-sample contact approximation at 16 or
    /// more simulated stand-ins; exact and fast are explicit visual/performance A/B overrides.</summary>
    public AmbientOcclusionMode AmbientOcclusionQuality { get => m_ambientOcclusionQuality; set { m_ambientOcclusionQuality = value; m_revision++; } }
    /// <summary>Whether the per-tile far-field bound is active (default <see langword="true"/>). Set
    /// <see langword="false"/> (via <c>world.far-field bound off</c>) to march far-field sky rays to the far
    /// distance (<c>render.farDistance</c>) exactly — a pure performance isolator (output-identical when on), so it is session state, never
    /// durable config. Rides each view's <see cref="Puck.SdfVm.SdfViewQuality.DisableFarBound"/> lane,
    /// which <c>WorldFramePresenter</c> inverts each frame, so no rebuild.</summary>
    public bool FarBound { get => m_farBound; set { m_farBound = value; m_revision++; } }
    /// <summary>The residency's trace and partition cache tier. It starts off and remains session state;
    /// the cache does not yet apply lighting.</summary>
    public SdfIndirectTier IndirectTier {
        get => m_indirectTier;
        set {
            if (!Enum.IsDefined(value: value)) { throw new ArgumentOutOfRangeException(paramName: nameof(value)); }
            m_indirectTier = value;
            m_revision++;
        }
    }
    /// <summary>Whether a prototype whose bake is ready draws its baked mesh, textured, in place of its field
    /// (<c>world.bakes on|off</c>), or <see langword="null"/>, the default, for the world's own answer: its bakes draw
    /// when the loaded world carries them (a released or compiled tree's <c>BAKE</c> chunk, whose pack holds every bake
    /// before the first frame) and its fields draw otherwise, so no capture depends on a bake made on the device
    /// (<see cref="DrawsBakes"/>). Presentation only: the field still answers contact, casts shadows and occludes, so
    /// simulation state is the same either way. Session state, never durable config; a change rebuilds the static
    /// scene.</summary>
    public bool? Bakes {
        get => m_bakes switch { 1 => true, 2 => false, _ => null };
        set { m_bakes = value switch { true => 1, false => 2, null => 0 }; m_revision++; }
    }

    /// <summary>Returns whether the presentation draws its ready bakes: as <see cref="Bakes"/> says when it is set, and
    /// otherwise when the schedule's last reconcile found the loaded world's pack supplied every bake
    /// (<see cref="Client.WorldBakeSchedule.Ships"/>).</summary>
    /// <param name="schedule">The presentation's bake schedule, or <see langword="null"/> when it has none.</param>
    /// <returns><see langword="true"/> when ready bakes draw.</returns>
    public bool DrawsBakes(Client.WorldBakeSchedule? schedule) =>
        (Bakes ?? (schedule?.Ships ?? false));

    /// <summary>Whether a frame whose render inputs match the previous one re-composites the retained image instead of
    /// re-rendering (default <see langword="true"/>; pixel-identical either way). Set <see langword="false"/> (via
    /// <c>world.cadence off</c>) to render every frame, so <c>world.counters gpu</c> measures a still scene. Session state, never
    /// durable config; rides <see cref="Puck.SdfVm.SdfFrame.EnableCadenceGate"/>.</summary>
    public bool CadenceGate { get => m_cadenceGate; set { m_cadenceGate = value; m_revision++; } }
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
    /// <summary>The soft-shadow crowd radius (world units): an avatar within this distance of any joined local seat casts
    /// soft shadows; beyond it, it is suppressed from the soft-shadow march only (still rendered, still self-lit). Boots
    /// at the definition's default; the <c>world.shadows</c> verb's optional second arg moves it live (it rides the
    /// per-instance <see cref="Puck.SignedDistance.DynamicTransform.CastsSoftShadow"/> lane <c>WorldSceneEmitter</c> computes
    /// per frame, so no rebuild). 0 = only the local seats cast; a value ≥ the world's diameter = everyone casts. Bounding
    /// who casts is how the population scales, since soft shadows dominate the GPU cost.</summary>
    public float ShadowCrowdRadius { get => m_shadowCrowdRadius; set { m_shadowCrowdRadius = value; m_revision++; } }
    /// <summary>The live soft-shadow march policy. Auto selects the bounded-cost path at 16 or more simulated stand-ins;
    /// exact and fast are explicit A/B overrides.</summary>
    public ShadowMarchMode ShadowMarch { get => m_shadowMarch; set { m_shadowMarch = value; m_revision++; } }
    /// <summary>The live shadow candidate-mask policy. Auto selects the camera-tile approximation at 16 or more
    /// simulated stand-ins; exact and camera-tile are explicit A/B overrides.</summary>
    public ShadowMaskMode ShadowMask { get => m_shadowMask; set { m_shadowMask = value; m_revision++; } }
    /// <summary>The live shadow-slot policy, set by the world's quality row and persisted by save.</summary>
    public Client.WorldShadowSettings ShadowSlots { get => m_shadowSlots; set { m_shadowSlots = value; m_revision++; } }
    /// <summary>The engine-wide soft-shadow reach fraction from 0 (off) through 1 (full reach). Named tiers are facades
    /// over this continuous value. The <c>world.shadows</c> verb moves it live through each view's
    /// <see cref="Puck.SdfVm.SdfViewQuality.DisableSoftShadows"/> and <see cref="Puck.SdfVm.SdfViewQuality.ShadowDistanceScale"/>
    /// lanes, so no rebuild.</summary>
    public float ShadowReach { get => m_shadowReach; set { m_shadowReach = value; m_revision++; } }
    /// <summary>Whether the world's own views reconstruct over time (<c>world.temporal</c>): each jitters its samples
    /// and resolves them over its history into its output, at native or reduced render scale, through each view's
    /// <see cref="Puck.SdfVm.SdfViewQuality.Temporal"/> lane. A change rebuilds each view's graph beside the installed
    /// one. Camera and session views never ask for it.</summary>
    public bool Temporal { get => m_temporal; set { m_temporal = value; m_revision++; } }
    /// <summary>Whether secondary stable shadow slots reuse valid history when reconstruction is on. The
    /// <c>world.shadow-amortize</c> session lever changes it without rebuilding the graph.</summary>
    public bool ShadowAmortize { get => m_shadowAmortize; set { m_shadowAmortize = value; m_revision++; } }
    /// <summary>The sky's quality tier (<c>world.sky-quality</c>): a layer below it writes no entry and counts no work,
    /// and below <see cref="WorldSkyTier.High"/> each kind draws its reduced form. Boots at the definition's
    /// <c>render.skyQuality</c>; a quality preset's <c>sky</c> row sets it.</summary>
    public WorldSkyTier SkyQuality { get => m_skyQuality; set { m_skyQuality = value; m_revision++; } }
    /// <summary>The continuous reconstruction sharpness: the spatial resolve's blend from bilinear (0) to clamped
    /// Catmull-Rom (1), and the strength of the contrast-adaptive sharpen <c>place</c> applies to a temporally resolved
    /// view at its rect's own extent. A native view that does not reconstruct ignores it.</summary>
    public float UpscaleSharpness { get => m_upscaleSharpness; set { m_upscaleSharpness = value; m_revision++; } }
}
