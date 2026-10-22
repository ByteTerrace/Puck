using Puck.Abstractions.Cameras;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Describes one viewport slot of an SDF frame: an SDF camera render into the view's own output image, and the
/// normalized display region a host places that output in.</summary>
/// <param name="Camera">The camera used to render the view.</param>
/// <param name="Region">The view's normalized display region, which sizes its output when no host asks for an
/// extent (the render graph's scheduled extent).</param>
public readonly record struct SdfViewSnapshot(CameraSnapshot Camera, NormalizedRect Region) {
    /// <summary>The infinity layers fitted to this consumer's camera, identified by their authored names.
    /// Their producer names identify existing render-graph outputs; the sky recorder binds them under the frame's
    /// graph-read leases. Empty means no fitted infinity image. The residency's authored sky remains shared.</summary>
    public IReadOnlyList<SdfSkyViewBinding> SkyViews { get; init; } = [];

    /// <summary>The camera's cut revision, moved when its framing is reseeded or a layout slot changes its source.</summary>
    public long CutRevision { get; init; }

    /// <summary>The render-scale ceiling in (0, 1], quantized by <see cref="RenderGraphExtent"/> into
    /// <see cref="RenderCeiling"/>. Scratch is allocated at that fraction of the view's output extent; its published
    /// output keeps its full extent. Zero or less reads as native.</summary>
    public float RenderScale { get; init; } = 1f;

    /// <summary>This frame's render scale inside the ceiling, quantized by <see cref="RenderGraphExtent"/> and bounded by
    /// <see cref="RenderCeiling"/> into <see cref="RenderGrid"/>. Zero uses the ceiling. It moves the render grid and
    /// nothing else: no allocation, fragment or graph follows it, so it can change every frame (a layout transition's
    /// dip). A view whose ceiling is native reconstructs nothing, so it renders its output grid and ignores this.</summary>
    public float ResolvedRenderScale { get; init; }
    /// <summary>The spatial reconstruction sharpness, from zero for bilinear to one for clamped Catmull-Rom.</summary>
    public float UpscaleSharpness { get; init; }
    /// <summary>Gets the allocation ceiling as a fraction of the output on each axis: <see cref="RenderScale"/>
    /// quantized, one for a scale of zero or less. It alone chooses whether the view reconstructs
    /// (<see cref="Reconstructs"/>) and sizes its scratch.</summary>
    public double RenderCeiling => RenderGraphExtent.Quantize(fraction: ((RenderScale > 0f) ? RenderScale : 1f));
    /// <summary>Gets whether the view renders a grid below its output and reconstructs it: exactly when
    /// <see cref="RenderCeiling"/> is below one.</summary>
    public bool Reconstructs => (RenderCeiling < 1d);
    /// <summary>Gets this frame's render grid as a fraction of the output on each axis: one for a view that does not
    /// reconstruct, otherwise <see cref="ResolvedRenderScale"/> quantized and bounded by <see cref="RenderCeiling"/>, or
    /// the ceiling when that is zero or less.</summary>
    public double RenderGrid => (!Reconstructs
        ? 1d
        : ((ResolvedRenderScale > 0f)
            ? Math.Min(val1: RenderGraphExtent.Quantize(fraction: ResolvedRenderScale), val2: RenderCeiling)
            : RenderCeiling));

    /// <summary>The editor grid the view draws, which its pass block carries (<see cref="SdfFrameBlock"/>); a view that
    /// draws none carries <see cref="GridOverlayState.Hidden"/>.</summary>
    public GridOverlayState Grid { get; init; } = GridOverlayState.Hidden;

    /// <summary>The quality the view renders at. Each view's pass block carries its own, so views of one frame, and of
    /// one residency, render the same scene at different cost. The default is full quality.</summary>
    public SdfViewQuality Quality { get; init; }

    /// <summary>The view's screen fidelity, from 0 to 1, which its pass block carries
    /// (<c>SdfWorldPackage.ScreenFidelity</c>): the share of a screen's own pixels that reaches the view untouched, past
    /// the CRT glass's grille, scanlines, bloom, vignette, glint and bezel mask, and past the world's atmosphere. A seat
    /// framing the screen it engaged carries its ease's weight; the default, 0, is the in-world look.</summary>
    public float ScreenFidelity { get; init; }
}
/// <summary>One infinity layer's fitted sampling parameters and the render-graph image this consumer reads.</summary>
/// <param name="Layer">The infinity layer's unique authored name.</param>
/// <param name="Parameters">The sampling basis and rectangle fitted to this consumer. The sky retains the
/// authored fallback, intensity and coverage.</param>
/// <param name="Producer">The instance whose completed output supplies the image, or null while the layer draws its fallback.</param>
public readonly record struct SdfSkyViewBinding(string Layer, SdfSkyView Parameters, string? Producer);
/// <summary>The quality levers one view renders with: each trades a shading term's cost against its fidelity. The
/// default is full quality, every term on, and no temporal reconstruction. The pass block carries each shading lever
/// under its own name (<see cref="SdfFrameBlock"/>), and the view's cadence signature folds it, so a change renders the
/// view again; <see cref="Temporal"/> chooses the view's fragment instead.</summary>
public readonly record struct SdfViewQuality {
    /// <summary>The sky field's grid fraction: one or one half; zero selects the full default.
    /// It changes neither the scene render grid nor retained field allocation capacity.</summary>
    public float SkyFieldScale { get; init; }
    /// <summary>Gets the validated sky field fraction, with the default zero reading as one.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The field scale is neither zero, one nor one half.</exception>
    public float SkyFieldFraction => SkyFieldScale switch {
        0f or 1f => 1f,
        .5f => .5f,
        _ => throw new ArgumentOutOfRangeException(paramName: nameof(SkyFieldScale)),
    };
    /// <summary>Publishes only geometry, with premultiplied color and its coverage in alpha, for a far sky layer.
    /// The composite omits background sky and air on uncovered rays. Restrictions retain the consumer's output form.</summary>
    public bool GeometryOnly { get; init; }
    /// <summary>Gets whether the view skips ambient occlusion: the surface pass writes an occlusion of one, so creases
    /// read brighter. The pass block carries it as <c>disableAmbientOcclusion</c>.</summary>
    public bool DisableAmbientOcclusion { get; init; }
    /// <summary>Gets whether the view skips shadows: the shadow cascades do not render and every light goes unshadowed.
    /// The pass block carries it as <c>disableSoftShadows</c>.</summary>
    public bool DisableSoftShadows { get; init; }
    /// <summary>Gets the texels along each side of the view's shadow cascades while it renders shadows: its fragment
    /// allocates cascades of that size, so a change rebuilds the view's graph. The default is
    /// <see cref="SdfShadowResolution.Full"/>.</summary>
    public SdfShadowResolution ShadowResolution { get; init; }
    /// <summary>Gets whether the view asks for temporal reconstruction: it runs the temporal fragment
    /// (<c>SdfWorldPackage.TemporalFragment</c>), jitters its samples and resolves them over its history into its
    /// output, at native or reduced render scale. Its costs are the resolve's dispatch and the history's bytes and
    /// barriers. The default asks for none.</summary>
    public bool Temporal { get; init; }

    /// <summary>Returns this quality with another's restrictions added: a term either skips stays skipped, the cascades
    /// take the coarser of the two resolutions, and the view reconstructs over time only when both ask. Neither can lift a
    /// restriction the other set.</summary>
    /// <param name="other">The restrictions to add.</param>
    /// <returns>The restricted quality.</returns>
    public SdfViewQuality Restrict(in SdfViewQuality other) => new() {
        SkyFieldScale = MathF.Min(x: SkyFieldFraction, y: other.SkyFieldFraction),
        GeometryOnly = GeometryOnly,
        DisableAmbientOcclusion = (DisableAmbientOcclusion || other.DisableAmbientOcclusion),
        DisableSoftShadows = (DisableSoftShadows || other.DisableSoftShadows),
        ShadowResolution = ((SdfShadowResolution)Math.Max(val1: ((int)ShadowResolution), val2: ((int)other.ShadowResolution))),
        Temporal = (Temporal && other.Temporal),
    };
}
/// <summary>Contains the scene program and presentation state consumed by one SDF render frame.</summary>
/// <param name="Program">The SDF program to render.</param>
/// <param name="ProgramChanged">Whether the renderer must upload <paramref name="Program"/> for this frame.</param>
/// <param name="Views">The camera views to render, each into its own output.</param>
/// <param name="Time">The presentation time in seconds.</param>
public sealed record SdfFrame(
    SdfProgram Program,
    bool ProgramChanged,
    IReadOnlyList<SdfViewSnapshot> Views,
    float Time
) {
    /// <summary>Gets the immutable host identity table for the composed frame. Composition fills it before publishing
    /// the frame; it is CPU-only presentation data and is never uploaded to the frame block.</summary>
    public ISdfPickMap? PickMap { get; internal set; }
    /// <summary>Gets the words <see cref="Program"/> differs in from the program built before it, which tables holding
    /// that earlier program upload alone, or <see langword="null"/> when its builder measured none.</summary>
    public SdfProgramDelta? ProgramDelta { get; internal set; }

    /// <summary>Gets the slot ranges drawn on both sides of a portal this frame (<see cref="SdfSlotClip"/>): the frame's
    /// producer fills it from the stamp pools whose slots the frame carries (a composition from every emitter's
    /// <see cref="ISdfSceneEmitter.SlotClips"/>), and the mesh bake's dressing clips each draw in a range and adds its
    /// carried copy (<see cref="SdfSlotClip.Apply"/>). Empty when no body straddles a portal.</summary>
    public IReadOnlyList<SdfSlotClip> SlotClips { get; set; } = [];
    /// <summary>Per-frame transforms for the scene's moving entities, indexed by dynamic-transform slot. Must supply
    /// at least the program's <see cref="SdfProgram.RequiredDynamicTransformCapacity"/> entries (the render frame
    /// throws otherwise — a dynamic slot silently rendering at identity is a bug, not a default); empty is therefore
    /// valid only for a program with no dynamic slots (the renderer then binds a single identity slot the program
    /// never references). Updating this list is how entities move — the program (binding 1) is uploaded once and left
    /// untouched.</summary>
    public IReadOnlyList<DynamicTransform> DynamicTransforms { get; init; } = [];

    /// <summary>The moved set of <see cref="DynamicTransforms"/>: which slots changed in each recent frame of the
    /// table's producer. An engine stages only the rows moved since the frame it last consumed from this producer,
    /// and every row when it has none of this producer's history. <see langword="null"/>, the default, declares the
    /// table static: an engine stages it whole the first time it sees that table instance and never again, so a
    /// producer whose transforms move must supply its moved set.</summary>
    public SdfMovedTransforms? MovedTransforms { get; init; }

    /// <summary>The frame's bounded flow and cloud volumes, at most
    /// <see cref="SdfWorldTables.MaxVolumes"/>; submission refuses a list beyond that capacity.
    /// Packed into its own structured buffer (never <c>sdfScreenLights</c>) and shaded by <c>shade-volumes.hlsli</c>'s
    /// one call site at the end of the views stage (<c>sdfViewsStage</c>), after the surface color is final. Empty (the default) uploads an
    /// all-zero table whose first bound ends the shader's scan.</summary>
    public IReadOnlyList<SdfVolume> Volumes { get; init; } = [];
    /// <summary>The frame's opaque triangle meshes: every placement and stamp of a prototype that carries a mesh. Empty by
    /// default; rasterized per view before primary traversal. A residency's tables repack them when the list is a different one or
    /// <see cref="MeshDrawsRevision"/> moved, so a producer that reuses one list rewrites it in place and moves the
    /// revision.</summary>
    public IReadOnlyList<SdfMeshDraw> MeshDraws { get; init; } = [];

    /// <summary>The revision of <see cref="MeshDraws"/>' content, which a producer that rewrites one list in place moves
    /// whenever it does; 0 for a producer that supplies a new list instead.</summary>
    public long MeshDrawsRevision { get; init; }

    /// <summary>The lit path's lights table, its shadow slots and its curvature shading. The default is the pinned sun
    /// an unauthored world renders.</summary>
    public SdfLights Lights { get; init; } = SdfLights.Default();
    /// <summary>The sky: its open layer stack, fog and environment gains.
    /// The default is the default look an unauthored world renders: the two-stop gradient and the default fog, as
    /// data.</summary>
    public SdfSky Sky { get; init; } = new();
    /// <summary>The far distance, in world units: the depth past which a view draws nothing, which bounds its surfaces,
    /// its far shadow cascade and the depth debug ramp. Authored as world data (<c>render.farDistance</c>). Must be
    /// finite and positive — the render frame throws otherwise (a document validator already refuses it by name
    /// upstream). Every pass block
    /// carries it as <c>farDistance</c> (<see cref="SdfFrameBlock"/>), which the cadence signature folds, so a change
    /// re-renders.</summary>
    public float FarDistance { get; init; } = DefaultFarDistance;

    /// <summary>Engine-bench lever: skips the per-screen area-light loop (the diegetic CRTs stop spilling colored light
    /// into the room). Default <see langword="false"/> = screen lights on. Directly measures the lit CRTs' cost for the
    /// <c>sdf.screen-lights</c> bench toggle. The pass block carries it as <c>disableScreenLights</c>
    /// (<see cref="SdfFrameBlock"/>). An unset frame uploads 0 and screen lights stay on.</summary>
    public bool DisableScreenLights { get; init; }
    /// <summary>Engine-bench lever: how much of the mesh draws the raster passes skip. The default,
    /// <see cref="SdfMeshCullLevel.Occlusion"/>, skips what the box cull (<see cref="SdfMeshCull"/>) and the mesh pass's
    /// occlusion test (<see cref="SdfMeshOcclusion"/>) find no pixel of; every level changes no pixel, so the lever
    /// measures each one's saving and checks it shows the same image. The cadence signature folds it, so a change
    /// re-renders.</summary>
    public SdfMeshCullLevel MeshCull { get; init; }
    /// <summary>Which work the frame's views update less often when it is far or small on screen
    /// (<see cref="SdfUpdateRates"/>, <c>world.rates</c>); the default, <see cref="SdfUpdateRates.None"/>, updates
    /// everything every frame. The residency reads <see cref="SdfUpdateRates.ShadowFar"/>; the host reads the rest
    /// before it composes the frame.</summary>
    public SdfUpdateRates UpdateRates { get; init; }
    /// <summary>The counters the frame's rated work counts into (<see cref="SdfUpdateRateCounts"/>), or
    /// <see langword="null"/> to count nothing.</summary>
    public WorkCounterSet? RateCounts { get; init; }
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

    /// <summary>The independent origins the irradiance volume admits; the default admits all five categories.</summary>
    public SdfIndirectSources IndirectSources { get; init; } = SdfIndirectSources.All;
    /// <summary>Gets the source gains the irradiance volume traces under. Zero also disables that category in authored frames.</summary>
    public SdfIndirectGains IndirectGains { get; init; } = SdfIndirectGains.One;

    /// <summary>Gets requested feedback sweeps after direct, capped by the tier. Null uses its layout limit.</summary>
    public int? IndirectBounces { get; init; }

    /// <summary>Gets receiver-only application controls, independent of the traced lighting.</summary>
    public SdfIndirectApplication IndirectApply { get; init; } = SdfIndirectApplication.Default;

    /// <summary>Gets the presented engine tick the sky and the bounded media animate on: the star-twinkle phase, the
    /// cloud layer's drift, shear and spin, and each medium's advection and pulse, all reduced on the host (the sky
    /// block, <see cref="SdfSkyBlock"/>, and the volume table), so no pass reads a raw tick. It comes from the
    /// state mirror of the world the frame draws, never from <see cref="Time"/>, which advances by wall-clock
    /// deltas, so a frame at a given tick and fraction draws the same sky and media on every run.</summary>
    public PresentedTick Clock { get; init; }

    /// <summary>The default far distance, 40 world units, which an unauthored <c>render.farDistance</c> resolves to.</summary>
    public const float DefaultFarDistance = 40f;
}
