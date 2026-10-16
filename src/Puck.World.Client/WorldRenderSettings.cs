using Puck.Hosting;
using Puck.SignedDistance;

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
    private bool m_cadenceGate;
    private bool m_dynamicResolution;
    private SdfIndirectTier m_indirectTier;
    private float m_renderScale;
    private int m_revision;
    private bool m_shadows;
    private Client.WorldShadowSettings m_shadowSlots;
    private bool m_temporal;
    private float m_upscaleSharpness;
    private WorldSkyTier m_skyQuality;

    /// <summary>Initializes a new instance of the <see cref="WorldRenderSettings"/> class from the world definition's
    /// render-lever boot defaults (<see cref="WorldRenderDefaults"/>), copied into the live, mutable settings the
    /// console verbs move from here.</summary>
    /// <param name="defaults">The render-lever boot defaults to wake on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="defaults"/> is <see langword="null"/>.</exception>
    public WorldRenderSettings(WorldRenderDefaults defaults) {
        ArgumentNullException.ThrowIfNull(argument: defaults);

        Shadows = (defaults.Shadows != ShadowTier.Off);
        ShadowSlots = Client.WorldShadowSettings.From(render: defaults);
        AmbientOcclusion = defaults.AmbientOcclusion;
        RenderScale = defaults.RenderScale;
        UpscaleSharpness = defaults.UpscaleSharpness;
        Temporal = defaults.Temporal;
        SkyQuality = defaults.SkyQuality;
        SkyFieldScale = defaults.SkyFieldScale;
        IndirectTier = (defaults.Indirect?.Tier ?? SdfIndirectTier.Medium);
        DynamicResolution = defaults.DynamicResolution;
        CadenceGate = true;
    }

    /// <summary>Whether ambient occlusion is on. Boots at the definition's default (<see langword="false"/> in the built-in
    /// world); the <c>world.ao</c> verb toggles it live (it rides each view's
    /// <see cref="Puck.SdfVm.SdfViewQuality.DisableAmbientOcclusion"/> lane, so no rebuild).</summary>
    public bool AmbientOcclusion { get => m_ambientOcclusion; set { m_ambientOcclusion = value; m_revision++; } }
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
    /// <summary>Whether the shadow cascades render: boots on unless the definition's shadow tier is
    /// <see cref="ShadowTier.Off"/>, and the <c>world.shadows</c> verb moves it live through each view's
    /// <see cref="Puck.SdfVm.SdfViewQuality.DisableSoftShadows"/> lane, so no rebuild.</summary>
    public bool Shadows { get => m_shadows; set { m_shadows = value; m_revision++; } }
    /// <summary>Whether the world's own views reconstruct over time (<c>world.temporal</c>): each jitters its samples
    /// and resolves them over its history into its output, at native or reduced render scale, through each view's
    /// <see cref="Puck.SdfVm.SdfViewQuality.Temporal"/> lane. A change rebuilds each view's graph beside the installed
    /// one. Camera and session views never ask for it.</summary>
    public bool Temporal { get => m_temporal; set { m_temporal = value; m_revision++; } }
    /// <summary>The sky's quality tier (<c>world.sky-quality</c>): a layer below it writes no entry and counts no work,
    /// and below <see cref="WorldSkyTier.High"/> each kind draws its reduced form. Boots at the definition's
    /// <c>render.skyQuality</c>; a quality preset's <c>sky</c> row sets it.</summary>
    public WorldSkyTier SkyQuality { get => m_skyQuality; set { m_skyQuality = value; m_revision++; } }
    /// <summary>The continuous reconstruction sharpness: the spatial resolve's blend from bilinear (0) to clamped
    /// Catmull-Rom (1), and the strength of the contrast-adaptive sharpen <c>place</c> applies to a temporally resolved
    /// view at its rect's own extent. A native view that does not reconstruct ignores it.</summary>
    public float UpscaleSharpness { get => m_upscaleSharpness; set { m_upscaleSharpness = value; m_revision++; } }
}
