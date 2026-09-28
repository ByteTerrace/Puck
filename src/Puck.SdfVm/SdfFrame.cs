using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Describes one viewport slot of an SDF frame: an SDF camera render into the view's own output image, and the
/// normalized display region a host places that output in.</summary>
/// <param name="Camera">The camera used to render the view.</param>
/// <param name="Region">The view's normalized display region, which sizes its output when no host asks for an
/// extent (the render graph's scheduled extent).</param>
public readonly record struct SdfViewSnapshot(CameraSnapshot Camera, NormalizedRect Region) {
    /// <summary>The view's render scale in (0, 1]: the fraction of its region's extent its output renders at when no
    /// host asks for an extent (the render graph's scheduled extent); a host placing the output reconstructs
    /// it into the region. 1 (the default) renders native. Presentation-only: hosts drop it during camera transitions
    /// and for mostly-hidden views.</summary>
    public float RenderScale { get; init; } = 1f;
    /// <summary>The editor grid the view draws, which its pass block carries (<see cref="SdfFrameBlock"/>); a view that
    /// draws none carries <see cref="GridOverlayState.Hidden"/>.</summary>
    public GridOverlayState Grid { get; init; } = GridOverlayState.Hidden;
    /// <summary>The quality the view renders at. Each view's pass block carries its own, so views of one frame, and of
    /// one residency, render the same scene at different cost. The default is full quality.</summary>
    public SdfViewQuality Quality { get; init; }
}
/// <summary>The quality levers one view renders with: each trades a shading term's cost against its fidelity. The
/// default is full quality, every term on at full reach with its exact path. The pass block carries each under its own
/// name (<see cref="SdfFrameBlock"/>), and the view's cadence signature folds it, so a change renders the view
/// again.</summary>
public readonly record struct SdfViewQuality {
    /// <summary>Gets whether the view skips ambient occlusion: occlusion reads 1, so creases read brighter, and the
    /// ambient pass does not run. The pass block carries it as <c>disableAmbientOcclusion</c>.</summary>
    public bool DisableAmbientOcclusion { get; init; }
    /// <summary>Gets whether the view pushes the beam-published per-tile far bound out of reach, so the fine march runs
    /// to <see cref="SdfFrame.FarDistance"/>. With the bound active a tile whose cone provably cannot produce a
    /// footprint-accepted hit exits early, output-identical to a full march. The pass block carries it as
    /// <c>disableFarBound</c>.</summary>
    public bool DisableFarBound { get; init; }
    /// <summary>Gets whether the view skips the soft-shadow sun march: the sun goes unshadowed and the shadow pass does not
    /// run. The pass block carries it as <c>disableSoftShadows</c>.</summary>
    public bool DisableSoftShadows { get; init; }
    /// <summary>Gets the scale on the soft-shadow reach, shared by the <c>sdfShadowGather</c> cull cone and the march
    /// ceiling (one length, or the cull set would be unsound for the ray). 0 means the full 1.0 reach. The pass block
    /// carries it as <c>shadowDistanceScale</c>.</summary>
    public float ShadowDistanceScale { get; init; }
    /// <summary>Gets whether soft-shadow rays use the camera-tile instance mask instead of the per-pixel shadow-grid
    /// gather. It can omit an occluder outside the camera tile whose shadow reaches into it. The pass block carries it
    /// as <c>cameraTileShadowMask</c>.</summary>
    public bool UseCameraTileShadowMask { get; init; }
    /// <summary>Gets whether the view uses the one-sample contact ambient occlusion instead of the three-rung ladder.
    /// The pass block carries it as <c>fastAmbientOcclusion</c>.</summary>
    public bool UseFastAmbientOcclusion { get; init; }
    /// <summary>Gets whether the view uses the bounded-cost soft-shadow marcher: fewer samples, wider open-space advances
    /// and a sub-visible darkness early-out, instead of the exact 48-step path. The pass block carries it as
    /// <c>fastSoftShadowMarch</c>.</summary>
    public bool UseFastSoftShadowMarch { get; init; }

    /// <summary>Returns this quality with another's restrictions added: a term either skips stays skipped, an
    /// approximation either takes stays taken, and the shorter shadow reach holds. Neither can lift a restriction the
    /// other set.</summary>
    /// <param name="other">The restrictions to add.</param>
    /// <returns>The restricted quality.</returns>
    public SdfViewQuality Restrict(in SdfViewQuality other) => new() {
        DisableAmbientOcclusion = (DisableAmbientOcclusion || other.DisableAmbientOcclusion),
        DisableFarBound = (DisableFarBound || other.DisableFarBound),
        DisableSoftShadows = (DisableSoftShadows || other.DisableSoftShadows),
        ShadowDistanceScale = MathF.Min(
            x: ((ShadowDistanceScale > 0f) ? ShadowDistanceScale : 1f),
            y: ((other.ShadowDistanceScale > 0f) ? other.ShadowDistanceScale : 1f)
        ),
        UseCameraTileShadowMask = (UseCameraTileShadowMask || other.UseCameraTileShadowMask),
        UseFastAmbientOcclusion = (UseFastAmbientOcclusion || other.UseFastAmbientOcclusion),
        UseFastSoftShadowMarch = (UseFastSoftShadowMarch || other.UseFastSoftShadowMarch),
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

    /// <summary>A per-frame scale on the world path's ambient term (default 1 = unchanged). Below 1 dims the room so
    /// the diegetic screen glow dominates — the overworld sets it low for mood; other scenes leave the default.</summary>
    public float AmbientScale { get; init; } = 1f;
    /// <summary>A per-frame scale on the world path's sun (directional) term (default 1 = unchanged). Pairs with
    /// <see cref="AmbientScale"/> to darken the room for the overworld mood.</summary>
    public float SunScale { get; init; } = 1f;
    /// <summary>The lit path's lights, stylization gains, and sky as one lane table. The default is the pinned sun
    /// and hemisphere ambient an unauthored world renders.</summary>
    public SdfEnvironment Environment { get; init; } = SdfEnvironment.Default();
    /// <summary>The far distance, in world units: the depth at which every camera march ends — the fine march's far
    /// exit, the beam's cone proofs (tile entry, the four-bound gap search, the F1 far bound) and every "nothing
    /// proven" tile-plane sentinel, and the depth/overshoot debug ramps. Authored as world data
    /// (<c>render.farDistance</c>); the default is the exact value the shaders pinned as <c>MaxDistance</c> before it
    /// became per-frame data, so a frame that never sets it renders bit-identically. Must be finite and positive — the
    /// render frame throws otherwise (a document validator already refuses it by name upstream). Every pass block
    /// carries it as <c>farDistance</c> (<see cref="SdfFrameBlock"/>), which the cadence signature folds, so a change
    /// re-renders.</summary>
    public float FarDistance { get; init; } = DefaultFarDistance;

    /// <summary>The slice debug view's plane selector: 0 (the default) = camera-locked (the plane through the world
    /// origin with normal = camera forward), 1/2/3 = a world-axis-aligned plane (X/Y/Z normal) at
    /// <see cref="DebugSliceOffset"/> along that axis. The pass block carries it as <c>debugSliceAxis</c>. Read only by
    /// debug view mode 7 (slice).</summary>
    public float DebugSliceAxis { get; init; }
    /// <summary>The axis-aligned slice plane's signed offset along the <see cref="DebugSliceAxis"/> axis (world
    /// units). Ignored while <see cref="DebugSliceAxis"/> is 0 (camera-locked).</summary>
    public float DebugSliceOffset { get; init; }
    /// <summary>Engine-bench lever: skips the per-screen area-light loop (the diegetic CRTs stop spilling colored light
    /// into the room). Default <see langword="false"/> = screen lights on. Directly measures the lit CRTs' cost for the
    /// <c>sdf.screen-lights</c> bench toggle. The pass block carries it as <c>disableScreenLights</c>
    /// (<see cref="SdfFrameBlock"/>). An unset frame uploads 0 and screen lights stay on.</summary>
    public bool DisableScreenLights { get; init; }
    /// <summary>Disables the soft-shadow grid cull (default <see langword="false"/> = the cull is on). With the cull on
    /// the world lit path gathers each lit pixel's shadow-ray grid neighborhood and marches only those instances —
    /// bit-identical to the flat all-instances shadow but far cheaper on spread scenes. Setting this
    /// <see langword="true"/> forces the flat all-instances march: the ground-truth reference for the cull, and the A/B
    /// lever's off state (the <c>sdf.shadowcull</c> verb) — cull-equals-flat parity is checked by flipping the verb.
    /// The pass block carries it as <c>disableShadowCull</c> (<see cref="SdfFrameBlock"/>). An unset frame uploads 0
    /// and the cull stays on.</summary>
    public bool DisableShadowCull { get; init; }
    /// <summary>Enables the cadence gate: a presentation-only frame-graph optimization where a
    /// frame whose render-consumed inputs are byte-for-byte unchanged from the last rendered frame skips the
    /// mask/beam/cull-args/views compute passes and re-composites from the retained views output — pixel-identical to a
    /// full re-render of the same inputs, at a fraction of the GPU cost. Built on change signatures (the packed
    /// per-frame byte spans the skipped passes consume, plus a program/decal revision), never wall-clock heuristics, so
    /// a camera ease — any input change at all — re-renders. The engine additionally forces a render whenever a live
    /// screen source is bound or a carve bake is in progress (their content changes without touching a packed span).
    /// Default <see langword="false"/> = the gate is off and every frame renders fully — byte-identical to a build
    /// without the gate. Presentation-only: never involves simulation state, and a skipped frame's simulation is
    /// unaffected.</summary>
    public bool EnableCadenceGate { get; init; }
    /// <summary>Engine-bench lever (PATH B): when <see langword="true"/>, the soft-shadow march skips
    /// Subtraction-family carve instances (host-flagged shadow-transparent) and marches the pre-carve union hull — the
    /// carve cavities stop letting sun through (a carved tunnel stays shadowed), collapsing the O(cluster) shadow
    /// re-march on dense-carve scenes to O(few). Default <see langword="false"/> = off (the full occluder set,
    /// byte-identical): shadows still evaluate every carve. Conservative when on — a skipped carve can only make the
    /// field more solid, so shadows go darker, never light-leak. The <c>sdf.shadow-proxy</c> bench toggle. The pass
    /// block carries it as <c>enableShadowProxy</c> (<see cref="SdfFrameBlock"/>). An unset frame uploads 0 and the
    /// proxy stays off.</summary>
    public bool EnableShadowProxy { get; init; }
    /// <summary>Gets the deterministic tick clock the sky reads: the star-twinkle phase and the integrated cloud offsets.</summary>
    /// <remarks>
    /// <para>
    /// This must be fed from the deterministic tick clock — <c>WorldSimulation.ElapsedTicks</c> — and never from
    /// <see cref="Time"/>, which is a presentation-clock accumulation that advances by wall-clock deltas, so a replay
    /// at tick N renders the identical sky.
    /// </para>
    /// <para>
    /// When the sky has visible twinkle it rides the composite push constant, which the engine's frame signature
    /// folds in, so the cadence gate never skips a frame whose tick moved; a sky without visible twinkle pushes 0 and a
    /// static frame stays skippable.
    /// </para>
    /// </remarks>
    public uint SampleIndex { get; init; }
    /// <summary>Selects the four-tap finite-difference surface normal instead of the default analytic forward-mode
    /// gradient dual. The default <see langword="false"/> uses analytic normals (one dual field evaluation at the hit —
    /// exact through the transform chain, immune to finite-difference cancellation). The pass block carries it as
    /// <c>finiteDifferenceNormals</c> (<see cref="SdfFrameBlock"/>). A frame that never sets it uploads 0 and shades
    /// with analytic normals.</summary>
    public bool UseFiniteDifferenceNormals { get; init; }

    /// <summary>The pinned default far distance — the shaders' retired <c>MaxDistance</c> constant (40 world
    /// units). An unauthored <c>render.farDistance</c> resolves to exactly this, so such a world renders unchanged.</summary>
    public const float DefaultFarDistance = 40f;
}
