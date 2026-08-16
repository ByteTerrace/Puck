
namespace Puck.World;

/// <summary>The soft-shadow candidate-mask policy. <see cref="Auto"/> keeps exact gathers for small sessions and uses
/// the camera-tile approximation at the declared fleet tiers; the other values are live profiling/authoring overrides.</summary>
internal enum ShadowMaskMode {
    Auto,
    ExactGather,
    CameraTile,
}
/// <summary>The soft-shadow march quality policy. Auto keeps the exact engine path for small sessions and selects the
/// bounded-cost approximation at fleet scale; the other values are live profiling/authoring overrides.</summary>
internal enum ShadowMarchMode {
    Auto,
    Exact,
    Fast,
}
/// <summary>The ambient-occlusion sampling policy. Auto keeps the quality ladder for small sessions and selects the
/// calibrated one-sample contact approximation at fleet scale; the other values are live profiling overrides.</summary>
internal enum AmbientOcclusionMode {
    Auto,
    Exact,
    Fast,
}
/// <summary>
/// The world's live render settings — the engine-wide levers (shadows, ambient occlusion, render scale) mutated by
/// console verbs in real time and read by <see cref="Client.WorldFrameSource"/> every captured frame. Session state, not
/// identity: per-player preferences belong on the profile.
/// </summary>
internal sealed class WorldRenderSettings {
    private bool m_ambientOcclusion;
    private AmbientOcclusionMode m_ambientOcclusionQuality;
    private bool m_farBound;
    private float m_renderScale;
    private int m_revision;
    private bool m_shadowAccumulation;
    private float m_shadowCrowdRadius;
    private bool m_shadowFarExit;
    private ShadowMarchMode m_shadowMarch;
    private ShadowMaskMode m_shadowMask;
    private float m_shadowReach;
    private float m_upscaleSharpness;

    /// <summary>Initializes a new instance of the <see cref="WorldRenderSettings"/> class from the world definition's
    /// render-lever boot defaults (<see cref="WorldRenderDefaults"/>), copied into the live, mutable settings the
    /// console verbs move from here.</summary>
    /// <param name="defaults">The render-lever boot defaults to wake on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    public WorldRenderSettings(WorldRenderDefaults defaults) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        ShadowReach = ShadowTiers.Scale(tier: defaults.Shadows);
        ShadowCrowdRadius = defaults.ShadowCrowdRadius;
        ShadowMask = ShadowMaskMode.Auto;
        ShadowMarch = ShadowMarchMode.Auto;
        AmbientOcclusionQuality = AmbientOcclusionMode.Auto;
        AmbientOcclusion = defaults.AmbientOcclusion;
        RenderScale = WorldRenderScaleTiers.Scale(tier: defaults.RenderScale);
        UpscaleSharpness = defaults.UpscaleSharpness;
        FarBound = true;
        ShadowFarExit = true;
        ShadowAccumulation = true;
    }

    /// <summary>Whether ambient occlusion is on. Boots at the definition's default (<see langword="false"/> in the built-in
    /// world); the <c>world.ao</c> verb toggles it live (it rides the per-frame
    /// <see cref="Puck.SdfVm.SdfFrame.DisableAmbientOcclusion"/> lane, so no rebuild).</summary>
    public bool AmbientOcclusion { get => m_ambientOcclusion; set { m_ambientOcclusion = value; m_revision++; } }
    /// <summary>The live ambient-occlusion sampling policy. Auto selects the one-sample contact approximation at 16 or
    /// more simulated stand-ins; exact and fast are explicit visual/performance A/B overrides.</summary>
    public AmbientOcclusionMode AmbientOcclusionQuality { get => m_ambientOcclusionQuality; set { m_ambientOcclusionQuality = value; m_revision++; } }
    /// <summary>Whether the per-tile far-field bound is active (default <see langword="true"/>). Set
    /// <see langword="false"/> (via <c>world.far-field bound off</c>) to march far-field sky rays to
    /// MaxDistance exactly — a pure performance isolator (output-identical when on), so it is session state, never
    /// durable config. Rides the per-frame <see cref="Puck.SdfVm.SdfFrame.DisableFarBound"/> lane
    /// <see cref="Client.WorldFrameSource"/> inverts each frame, so no rebuild.</summary>
    public bool FarBound { get => m_farBound; set { m_farBound = value; m_revision++; } }
    /// <summary>The engine-wide internal render-scale fraction, applied to every player view's
    /// <see cref="Puck.SdfVm.SdfViewSnapshot.RenderScale"/> each frame. Named tiers initialize it, while
    /// <c>world.render-scale</c> also accepts a live numeric fraction/percentage for performance sweeps. Native 1.0 is
    /// the bit-exact fast path; lower values use the compositor reconstruction selected by
    /// <see cref="UpscaleSharpness"/>.</summary>
    public float RenderScale { get => m_renderScale; set { m_renderScale = value; m_revision++; } }
    /// <summary>A monotonic counter advanced by every lever write — the cheap watch the editor HUD keys its
    /// live-session-act tag and drift refresh on (no per-frame drift recompute).</summary>
    public int Revision => m_revision;
    /// <summary>Whether the area-light shadow estimator's TEMPORAL ACCUMULATION is active (default
    /// <see langword="true"/>). Set <see langword="false"/> (via <c>world.shadow.accumulate off</c>) to shade each
    /// frame's raw two-sample stochastic estimate directly — deliberately noisy, an A/B isolator rather than a quality
    /// tier. Session state, never durable config. Rides the per-frame
    /// <see cref="Puck.SdfVm.SdfFrame.DisableShadowAccumulation"/> lane <see cref="Client.WorldFrameSource"/> inverts
    /// each frame, so no rebuild.</summary>
    public bool ShadowAccumulation { get => m_shadowAccumulation; set { m_shadowAccumulation = value; m_revision++; } }
    /// <summary>The soft-shadow crowd radius (world units): an avatar within this distance of any joined local seat casts
    /// soft shadows; beyond it, it is suppressed from the soft-shadow march only (still rendered, still self-lit). Boots
    /// at the definition's default; the <c>world.shadows</c> verb's optional second arg moves it live (it rides the
    /// per-instance <see cref="Puck.SignedDistance.DynamicTransform.CastsSoftShadow"/> lane <see cref="Client.WorldSceneEmitter"/> computes
    /// per frame, so no rebuild). 0 = only the local seats cast; a value ≥ the world's diameter = everyone casts. Bounding
    /// who casts is how the population scales, since soft shadows dominate the GPU cost.</summary>
    public float ShadowCrowdRadius { get => m_shadowCrowdRadius; set { m_shadowCrowdRadius = value; m_revision++; } }
    /// <summary>Whether the soft-shadow light-side early exit is active (default <see langword="true"/>). Set
    /// <see langword="false"/> (via <c>world.far-field shadow off</c>) to run the full shadow step budget/reach — a
    /// march-path change, session state, not durable config. Rides the per-frame
    /// <see cref="Puck.SdfVm.SdfFrame.DisableShadowEscapeExit"/> lane <see cref="Client.WorldFrameSource"/> inverts each frame.</summary>
    public bool ShadowFarExit { get => m_shadowFarExit; set { m_shadowFarExit = value; m_revision++; } }
    /// <summary>The live soft-shadow march policy. Auto selects the bounded-cost path at 16 or more simulated stand-ins;
    /// exact and fast are explicit A/B overrides.</summary>
    public ShadowMarchMode ShadowMarch { get => m_shadowMarch; set { m_shadowMarch = value; m_revision++; } }
    /// <summary>The live shadow candidate-mask policy. Auto selects the camera-tile approximation at 16 or more
    /// simulated stand-ins; exact and camera-tile are explicit A/B overrides.</summary>
    public ShadowMaskMode ShadowMask { get => m_shadowMask; set { m_shadowMask = value; m_revision++; } }
    /// <summary>The engine-wide soft-shadow reach fraction from 0 (off) through 1 (full reach). Named tiers are facades
    /// over this continuous value. The <c>world.shadows</c> verb moves it live through the per-frame
    /// <see cref="Puck.SdfVm.SdfFrame.DisableSoftShadows"/> / <see cref="Puck.SdfVm.SdfFrame.ShadowDistanceScale"/> lanes,
    /// so no rebuild).</summary>
    public float ShadowReach { get => m_shadowReach; set { m_shadowReach = value; m_revision++; } }
    /// <summary>The continuous reduced-resolution reconstruction blend: 0 is bilinear, 1 is clamped Catmull-Rom, and
    /// intermediate values blend between them. Native render scale ignores it.</summary>
    public float UpscaleSharpness { get => m_upscaleSharpness; set { m_upscaleSharpness = value; m_revision++; } }
}
