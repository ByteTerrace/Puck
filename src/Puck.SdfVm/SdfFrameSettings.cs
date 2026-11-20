using Puck.Abstractions.Counting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>What changes per document, program or setting in an <see cref="SdfFrame"/>, rather than per frame: the
/// world's far plane and shadow reach, its tone and cel style, its shading programs, the engine levers and the indirect
/// light's tier, gains and budget. A frame holds it by value beside its <see cref="SdfFrameContent"/>, so a producer that
/// keeps one value across frames carries it into every frame without allocating, and a consumer copying a frame copies it
/// whole. The default (<c>new()</c>) is what an unauthored world renders; <see langword="default"/> is not a valid set of
/// settings, since its far distance is zero.</summary>
public readonly record struct SdfFrameSettings {
    /// <summary>Initializes a new instance of the <see cref="SdfFrameSettings"/> struct with every setting at its
    /// unauthored default.</summary>
    public SdfFrameSettings() { }

    /// <summary>The host's immutable document version, retained through bake dressing for matching-state diagnostics.
    /// Compared by value: a delivered client document need not be the authority's object.</summary>
    public object? DiagnosticSource { get; init; }
    /// <summary>The far distance, in world units: the depth past which a view draws nothing, which bounds its surfaces,
    /// its far shadow cascade and the depth debug ramp. Authored as world data (<c>render.farDistance</c>). Must be
    /// finite and positive — the render frame throws otherwise (a document validator already refuses it by name
    /// upstream). Every pass block carries it as <c>farDistance</c> (<see cref="SdfFrameBlock"/>), which the cadence
    /// signature folds, so a change re-renders.</summary>
    public float FarDistance { get; init; } = SdfFrame.DefaultFarDistance;
    /// <summary>How far the key light's shadow cascades reach (<c>render.shadowCoverage</c>), which
    /// <see cref="SdfMeshShadowCascades.Fit"/> reads; the default is <see cref="SdfShadowCoverage.Default"/>.</summary>
    public SdfShadowCoverage ShadowCoverage { get; init; } = SdfShadowCoverage.Default;
    /// <summary>The key light's distant shadow map (<c>render.distantShadows</c> or the device table's row), which
    /// <see cref="SdfMeshShadowCascades.Fit"/> adds to every view's cascade set while it is on; the default is off.</summary>
    public SdfDistantShadows DistantShadows { get; init; }
    /// <summary>How the key light's far shadow cascade keeps up with what moves (<c>render.farShadows</c> or the device
    /// table's row) while <see cref="SdfUpdateRates.ShadowFar"/> is on: its refreshes a second and the light turn a held
    /// cascade tolerates; the default refreshes it every frame (<see cref="SdfFarShadows.EveryFrame"/>).</summary>
    public SdfFarShadows FarShadows { get; init; } = SdfFarShadows.EveryFrame;
    /// <summary>The display transform every view of the frame that converts for the display puts its exposed light
    /// through before the encode (<c>render.tone</c>, the display pass); the default applies no curve.</summary>
    public SdfToneCurve Tone { get; init; }
    /// <summary>The world's default cel style (its ink and styled mark), which every material without a style of its own
    /// takes, and the live suppression of every style (<see cref="SdfToon.Suppressed"/>); the default is off.</summary>
    public SdfToon Toon { get; init; }
    /// <summary>Measures authored programs beside the material layers, until their migration removes the lever.</summary>
    public bool ShadePrograms { get; init; }
    /// <summary>The authored default for materials that omit a program. Runtime overrides upload only presentation data.</summary>
    public SdfShadeProgram? ShadeDefault { get; init; }
    /// <summary>Gets every program the documents behind the frame declare, which the residency specializes together with
    /// the frame's active programs (<see cref="SdfFrame.ShadingPrograms"/>), so its specialized kernels do not change while
    /// the scene's composition does. The emitter passes one list for one set of documents, compared by reference; empty
    /// declares none.</summary>
    public IReadOnlyList<SdfShadeProgram> ShadeDeclared { get; init; } = [];
    /// <summary>Engine-bench lever: skips the per-screen area-light loop (the diegetic screens stop spilling colored light
    /// into the room). Default <see langword="false"/> = screen lights on. Directly measures the lit screens' cost for the
    /// <c>sdf.screen-lights</c> bench toggle. The pass block carries it as <c>disableScreenLights</c>
    /// (<see cref="SdfFrameBlock"/>). An unset frame uploads 0 and screen lights stay on.</summary>
    public bool DisableScreenLights { get; init; }
    /// <summary>Engine-bench lever: how much of the mesh draws the raster passes skip. The default,
    /// <see cref="SdfMeshCullLevel.Occlusion"/>, skips what the box cull (<see cref="SdfMeshCull"/>) and the mesh pass's
    /// occlusion test (<see cref="SdfMeshOcclusion"/>) find no pixel of, and far-cascade casters no current receiver can
    /// read or whose entry is wholly hidden by light-space depth; every level changes no pixel, so the lever
    /// measures each one's saving and checks it shows the same image. The cadence signature folds it, so a change
    /// re-renders.</summary>
    public SdfMeshCullLevel MeshCull { get; init; }
    /// <summary>Engine-bench lever: which levels of detail the raster passes draw. The default,
    /// <see cref="SdfMeshDetailLevel.Auto"/>, has each pass choose each draw's level by the error it can show
    /// (<see cref="SdfMeshDetail"/>); a fixed level draws it everywhere, so the lever shows what a level changes. The
    /// cadence signature folds it, so a change re-renders.</summary>
    public SdfMeshDetailLevel MeshDetail { get; init; }
    /// <summary>Which work the frame's views update less often when it is far or small on screen
    /// (<see cref="SdfUpdateRates"/>, <c>world.rates</c>); the default, <see cref="SdfUpdateRates.None"/>, updates
    /// everything every frame. The residency reads <see cref="SdfUpdateRates.ShadowFar"/>; the host reads the rest
    /// before it composes the frame.</summary>
    public SdfUpdateRates UpdateRates { get; init; }
    /// <summary>The counters the frame's rated work counts into (<see cref="SdfUpdateRateCounts"/>), or
    /// <see langword="null"/> to count nothing.</summary>
    public WorkCounterSet? RateCounts { get; init; }
    /// <summary>The window the frame's rated work records its intervals between updates into
    /// (<see cref="SdfUpdateRateIntervals"/>), or <see langword="null"/> to record nothing.</summary>
    public SdfUpdateRateIntervals? RateIntervals { get; init; }
    /// <summary>Enables the cadence gate: a presentation-only frame-graph optimization where a
    /// frame whose render-consumed inputs are byte-for-byte unchanged from the last rendered frame skips the view's
    /// passes and re-composites from the retained views output — pixel-identical to a
    /// full re-render of the same inputs, at a fraction of the GPU cost. Built on change signatures (the packed
    /// per-frame byte spans the skipped passes consume, plus a program/decal revision), never wall-clock heuristics, so
    /// a camera ease — any input change at all — re-renders. The engine additionally forces a render whenever a live
    /// screen source is bound or a carve bake is in progress (their content changes without touching a packed span).
    /// Default <see langword="false"/> = the gate is off and every frame renders fully — byte-identical to a build
    /// without the gate. Presentation-only: never involves simulation state, and a skipped frame's simulation is
    /// unaffected.</summary>
    public bool EnableCadenceGate { get; init; }
    /// <summary>The indirect-light tier the frame asks for, which sizes the irradiance volume
    /// (<see cref="SdfMeshIrradiance"/>). Off reads no volume.</summary>
    public SdfIndirectTier IndirectTier { get; init; }
    /// <summary>Whether the irradiance volume's background rays (its probe trace, the relight's shadow rays and the camera
    /// sample) take the device's ray hardware where it runs the probe kernels' ray-query variants, through the scene's
    /// acceleration structures; elsewhere, and when false, they take the software walk. Both answer the same hits within
    /// float tolerance. Default <see langword="false"/>.</summary>
    public bool RayQueries { get; init; }
    /// <summary>The independent origins the irradiance volume admits; the default admits all five categories.</summary>
    public SdfIndirectSources IndirectSources { get; init; } = SdfIndirectSources.All;
    /// <summary>Gets the source gains the irradiance volume traces under. Zero also disables that category in authored frames.</summary>
    public SdfIndirectGains IndirectGains { get; init; } = SdfIndirectGains.One;
    /// <summary>Gets requested feedback sweeps after direct, capped by the tier. Null uses its layout limit.</summary>
    public int? IndirectBounces { get; init; }
    /// <summary>Gets the irradiance work each frame grants the volume and the lighting age it reports past; the default
    /// bounds nothing.</summary>
    public SdfIndirectBudget IndirectBudget { get; init; }
    /// <summary>Gets how many of the irradiance volume's levels the frame's views read, counted from the coarsest
    /// (<see cref="SdfMeshIrradiance.LevelsOf"/>): the volume traces, relights and publishes those alone, so a world seen
    /// through a small glass measures only its coarse light. Zero, the default, reads every level.</summary>
    public int IndirectLevels { get; init; }
    /// <summary>Gets the share of the display's pixels the frame's views are presented over, in [0, 1]: one for a frame
    /// the presentation shows whole (the default), a glass's share for a world seen through it, zero for one no view
    /// showed on the latest measured frame. It weighs the residency's draw on the presentation's one background budget
    /// (<see cref="SdfGpuWork.Weigh"/>), and a frame no view shows owes no first light
    /// (<see cref="SdfMeshIrradiance.OwesFirstLight"/>). Presentation only.</summary>
    public float DisplayShare { get; init; } = 1f;
    /// <summary>The fixed angular and radiometric grids used by indirect captures. Zero leaves values continuous.</summary>
    public SdfIndirectLightingGrid IndirectLightingGrid { get; init; }
    /// <summary>Gets receiver-only application controls, independent of the traced lighting.</summary>
    public SdfIndirectApplication IndirectApply { get; init; } = SdfIndirectApplication.Default;
}
