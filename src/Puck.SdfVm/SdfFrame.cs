using System.Numerics;
using Puck.Cameras;
using Puck.Compositing;

namespace Puck.SdfVm;

/// <summary>Describes one camera and its normalized output region for an SDF frame.</summary>
/// <param name="Camera">The camera used to render the view.</param>
/// <param name="Region">The view's normalized output region.</param>
public readonly record struct SdfViewSnapshot(CameraSnapshot Camera, NormalizedRect Region) {
    /// <summary>The view's internal render scale in (0, 1]: Stage 1 renders the view at this fraction of its output
    /// region (an integer-derived extent — see the shader's <c>worldRenderDims</c>) and Stage 2 upsamples it back.
    /// 1 (the default) renders native through a bit-exact copy path, so an unset frame is byte-identical to a build
    /// without the lever. Presentation-only: hosts drop it during camera transitions / for mostly-hidden views.</summary>
    public float RenderScale { get; init; } = 1f;
    /// <summary>The reduced-resolution reconstruction blend in [0, 1]: 0 keeps the four-tap bilinear fast path; 1 uses
    /// clamped Catmull-Rom bicubic reconstruction; values between blend continuously. Ignored by the native exact-copy
    /// path. Presentation-only, quantized to one byte in Stage 2's push constants.</summary>
    public float UpscaleSharpness { get; init; }
}

/// <summary>One moving entity's rigid transform for a frame: a world position and an orientation. The renderer uploads
/// these into the per-frame dynamic-transform buffer the <c>SdfOp.TransformDynamic</c> opcode indexes by slot, so an
/// entity moves without rebuilding the scene program. The slot is the entity's index in <see cref="SdfFrame.DynamicTransforms"/>.
/// <para><paramref name="CastsSoftShadow"/> (default <see langword="true"/> = casts) rides the packed position row's spare
/// <c>.w</c> lane (see <c>SdfWorldEngine.PackDynamicTransforms</c>): <see langword="false"/> means THIS dynamic instance is
/// SKIPPED by the soft-shadow march only — the camera/AO marches are unaffected, so a suppressed avatar still renders and
/// self-occludes, it just stops casting/receiving through the sun-shadow enumeration. Per-frame data (avatars move every
/// frame); flipping it never rebuilds the program. Default casts is byte-identical to every prior frame's zero-pad upload.</para></summary>
public readonly record struct DynamicTransform(Vector3 Position, Quaternion Orientation, bool CastsSoftShadow = true);
/// <summary>Contains the scene program and presentation state consumed by one SDF render frame.</summary>
/// <param name="Program">The SDF program to render.</param>
/// <param name="ProgramChanged">Whether the renderer must upload <paramref name="Program"/> for this frame.</param>
/// <param name="Views">The camera views to render and composite.</param>
/// <param name="Time">The presentation time in seconds.</param>
/// <param name="WarpAmount">The presentation warp amount supplied to the compositor.</param>
public sealed record SdfFrame(
    SdfProgram Program,
    bool ProgramChanged,
    IReadOnlyList<SdfViewSnapshot> Views,
    float Time,
    float WarpAmount
) {
    /// <summary>Per-frame transforms for the scene's moving entities, indexed by dynamic-transform slot. Must supply
    /// at least the program's <see cref="SdfProgram.RequiredDynamicTransformCapacity"/> entries (the render frame
    /// throws otherwise — a dynamic slot silently rendering at identity is a bug, not a default); empty is therefore
    /// valid only for a program with no dynamic slots (the renderer then binds a single identity slot the program
    /// never references). Updating this list is how entities move — the program (binding 1) is uploaded once and left
    /// untouched.</summary>
    public IReadOnlyList<DynamicTransform> DynamicTransforms { get; init; } = [];
    /// <summary>A per-frame scale on the world path's AMBIENT term (default 1 = unchanged). Below 1 dims the room so
    /// the diegetic screen glow dominates — the overworld sets it low for mood; other scenes leave the default.</summary>
    public float AmbientScale { get; init; } = 1f;
    /// <summary>A per-frame scale on the world path's SUN (directional) term (default 1 = unchanged). Pairs with
    /// <see cref="AmbientScale"/> to darken the room for the overworld mood.</summary>
    public float SunScale { get; init; } = 1f;
    /// <summary>The SLICE debug view's plane selector: 0 (the default) = camera-locked (the plane through the world
    /// origin with normal = camera forward), 1/2/3 = a world-axis-aligned plane (X/Y/Z normal) at
    /// <see cref="DebugSliceOffset"/> along that axis. Rides the screen-light buffer's environment entry's two spare
    /// lanes (KEEP IN SYNC with sdf-world.hlsli's <c>sdfScreenLights</c> env decode and
    /// <c>SdfWorldEngine.PackScreenLights</c>) — no new upload plumbing. Read only by debug view mode 7 (slice);
    /// every other mode ignores it, so the default demo is byte-unchanged.</summary>
    public float DebugSliceAxis { get; init; }
    /// <summary>The axis-aligned slice plane's signed offset along the <see cref="DebugSliceAxis"/> axis (world
    /// units). Ignored while <see cref="DebugSliceAxis"/> is 0 (camera-locked).</summary>
    public float DebugSliceOffset { get; init; }
    /// <summary>Selects the four-tap finite-difference surface normal instead of the default analytic forward-mode
    /// gradient dual. The default <see langword="false"/> uses analytic normals (one dual field evaluation at
    /// the hit — exact through the transform chain, immune to finite-difference cancellation). Rides the screen-light
    /// buffer's grid-object-params row's reserved <c>.z</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldUseTapNormals</c>); a frame that never sets
    /// it uploads 0 and shades with analytic normals.</summary>
    public bool UseFiniteDifferenceNormals { get; init; }
    /// <summary>Disables the soft-shadow GRID CULL (default <see langword="false"/> = the cull is ON). With the cull ON
    /// the world lit path gathers each lit pixel's shadow-ray grid neighborhood and marches only those instances —
    /// bit-identical to the flat all-instances shadow but far cheaper on spread scenes. Setting this <see langword="true"/> forces
    /// the flat all-instances march: the ground-truth reference the Post <c>world-shadow-cull</c> gate matches and the
    /// A/B lever's OFF state (the <c>sdf.shadowcull</c> verb). Rides the screen-light buffer's grid-object-params row's
    /// reserved <c>.w</c> lane (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's
    /// <c>worldShadowCullEnabled</c>); an unset frame uploads 0 and the cull stays ON.</summary>
    public bool DisableShadowCull { get; init; }
    /// <summary>The grid-lock overlay flags (bit0 = draw the world floor grid, bit1 = draw the object grid). Rides
    /// the screen-light buffer's grid rows 9..12 (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c> and
    /// sdf-world.hlsli's <c>SdfGridWorld..SdfGridObjParams</c> decode). Default 0 = no overlay, so a frame that never
    /// sets it uploads the same zeros as before.</summary>
    public uint GridFlags { get; init; }
    /// <summary>The world floor grid's per-axis lattice pitch on X/Z (world units); 0 disables the grid on that axis.</summary>
    public Vector2 GridWorldPitch { get; init; }
    /// <summary>The floor plane height the world grid draws on (the overlay gates on the surface being near this Y).</summary>
    public float GridFloorY { get; init; }
    /// <summary>The object grid's reference frame origin (world space).</summary>
    public Vector3 GridObjectOrigin { get; init; }
    /// <summary>The object grid's reference frame orientation (the lattice renders in this frame's coordinates).</summary>
    public Quaternion GridObjectFrame { get; init; } = Quaternion.Identity;
    /// <summary>The object grid's per-axis in-plane pitch (reference-local X/Z).</summary>
    public Vector2 GridObjectPitch { get; init; }
    /// <summary>The object grid's finite-patch radius (reference-local units); 0 disables the object grid.</summary>
    public float GridObjectPatchRadius { get; init; }
    /// <summary>Engine-bench lever: skips the whole soft-shadow sun march (the sun goes UNSHADOWED; the ambient term is
    /// untouched, so shadowed regions read brighter). Default <see langword="false"/> = shadows ON. Isolates the single
    /// most expensive shading term for the <c>sdf.soft-shadows</c> bench toggle. Rides the bench-params screen-light
    /// row's <c>.x</c> lane (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's
    /// <c>worldSoftShadowsDisabled</c>); an unset frame uploads 0 and shadows stay ON.</summary>
    public bool DisableSoftShadows { get; init; }
    /// <summary>Engine-bench lever: skips <c>calcAO</c>'s normal-ladder ambient occlusion (occlusion is forced to 1, so
    /// creases read brighter). Default <see langword="false"/> = AO ON. Isolates the AO map() evals per lit pixel for
    /// the <c>sdf.ao</c> bench toggle. Rides the bench-params screen-light row's <c>.y</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldAoDisabled</c>); an unset frame uploads 0
    /// and AO stays ON.</summary>
    public bool DisableAmbientOcclusion { get; init; }
    /// <summary>Engine-bench lever: scales the soft-shadow reach (both the <c>sdfShadowGather</c> cull cone AND the
    /// march ceiling — ONE shared length, or the cull set would be unsound for the ray) for the <c>sdf.shadow-distance</c>
    /// bench toggle. <c>0</c> (the default) means the full 1.0 reach — an unset frame uploads 0 and behavior is
    /// unchanged; set 0.5/0.25 to shorten far shadows. Rides the bench-params screen-light row's <c>.z</c> lane (KEEP IN
    /// SYNC with <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldShadowDistanceScale</c>).</summary>
    public float ShadowDistanceScale { get; init; }
    /// <summary>Engine-bench lever: skips the per-screen area-light loop (the diegetic CRTs stop spilling colored light
    /// into the room). Default <see langword="false"/> = screen lights ON. Directly measures the lit CRTs' cost for the
    /// <c>sdf.screen-lights</c> bench toggle. Rides the bench-params screen-light row's <c>.w</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldScreenLightsDisabled</c>); an unset frame
    /// uploads 0 and screen lights stay ON.</summary>
    public bool DisableScreenLights { get; init; }
    /// <summary>Engine-bench lever (PATH B): when <see langword="true"/>, the soft-shadow march SKIPS Subtraction-family
    /// carve instances (host-flagged shadow-transparent) and marches the pre-carve union hull — the carve cavities stop
    /// letting sun through (a carved tunnel stays shadowed), collapsing the O(cluster) shadow re-march on dense-carve
    /// scenes to O(few). Default <see langword="false"/> = OFF (the full occluder set, byte-identical): shadows still
    /// evaluate every carve. Conservative when ON — a skipped carve can only make the field MORE solid, so shadows go
    /// darker, never light-leak. The <c>sdf.shadow-proxy</c> bench toggle. Rides a DEDICATED shadow-proxy screen-light
    /// row's <c>.x</c> lane (SdfBenchParams's four lanes are full — KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c>
    /// and sdf-world.hlsli's <c>worldShadowProxyEnabled</c> / <c>SdfShadowProxyParams</c>); an unset frame uploads 0 and
    /// the proxy stays OFF.</summary>
    public bool EnableShadowProxy { get; init; }
    /// <summary>Uses the already-computed camera-tile instance mask for soft-shadow rays instead of running the
    /// correctness-complete per-pixel shadow-grid gather. This is an explicit performance approximation for dense
    /// real-time crowds: it can omit an occluder outside the camera tile whose shadow reaches into the tile, but avoids
    /// paying a grid traversal for every sun-facing pixel. Default <see langword="false"/> keeps the exact gathered
    /// mask. Rides the shadow-proxy params row's reserved <c>.y</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldUseCameraTileShadowMask</c>).</summary>
    public bool UseCameraTileShadowMask { get; init; }
    /// <summary>Uses the bounded-cost soft-shadow marcher: fewer samples, wider open-space advances, and a sub-visible
    /// darkness early-out. This is an explicit presentation approximation for dense real-time scenes; the default
    /// <see langword="false"/> retains the exact 48-step quality path. Rides the shadow-proxy params row's reserved
    /// <c>.z</c> lane (KEEP IN SYNC with <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's
    /// <c>worldUseFastSoftShadowMarch</c>).</summary>
    public bool UseFastSoftShadowMarch { get; init; }
    /// <summary>Uses the one-sample contact-AO approximation instead of the three-rung quality ladder. This is an
    /// explicit presentation approximation for dense real-time scenes; the default <see langword="false"/> retains
    /// the quality path. Rides the shadow-proxy params row's reserved <c>.w</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldUseFastAmbientOcclusion</c>).</summary>
    public bool UseFastAmbientOcclusion { get; init; }
    /// <summary>A/B lever for the F1 beam-published per-tile FAR BOUND (perf plan Phase 5.1). Default
    /// <see langword="false"/> keeps the far bound ACTIVE — the shipped behavior: the fine march exits at
    /// <c>traveled &gt;= farBound</c> (plane 3), where the tile's cone provably cannot produce any footprint-accepted hit
    /// through MaxDistance, so the pixel is OUTPUT-IDENTICAL to a full march but pays fewer steps. Set
    /// <see langword="true"/> to push the far bound out of reach so the march runs to MaxDistance exactly as pre-F1 —
    /// the paired-run "off" side. Rides a DEDICATED far-field screen-light row's <c>.x</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldFarBoundDisabled</c> / <c>SdfFarFieldParams</c>);
    /// an unset frame uploads 0 and the far bound stays ON.</summary>
    public bool DisableFarBound { get; init; }
    /// <summary>A/B lever for the F2 SHADOW LIGHT-SIDE EARLY EXIT (perf plan Phase 5.1). Default
    /// <see langword="false"/> keeps the exit ACTIVE — the shipped behavior: <c>softShadow</c> returns its running
    /// result the moment the remaining reach provably cannot darken it further under the field's along-ray 1-Lipschitz
    /// bound (<c>ShadowSharpness·(clearanceTrue − remaining) &gt;= result·reach</c>), paying fewer shadow steps. Set
    /// <see langword="true"/> to run the full shadow step budget/reach exactly as pre-F2 — the paired-run "off" side.
    /// A MARCH-PATH change (solidity + parity families), not bit-identical: the Aaltonen parabola estimator can undershoot
    /// past the exit point (its worst case is unbounded below), so skipping it can leave a pixel brighter than the full
    /// march but never brighter than the true penumbra. Rides the far-field row's <c>.y</c> lane (KEEP IN SYNC with
    /// <c>SdfWorldEngine.PackScreenLights</c> and sdf-world.hlsli's <c>worldShadowFarExitDisabled</c> / <c>SdfFarFieldParams</c>);
    /// an unset frame uploads 0 and the exit stays ON.</summary>
    public bool DisableShadowFarExit { get; init; }
    /// <summary>Enables the CADENCE GATE (perf plan Phase 6.1): a presentation-only frame-graph optimization where a
    /// frame whose render-consumed inputs are byte-for-byte unchanged from the last rendered frame SKIPS the
    /// mask/beam/cull-args/views compute passes and re-composites from the retained views output — pixel-identical to a
    /// full re-render of the same inputs, at a fraction of the GPU cost. Built on change SIGNATURES (the packed
    /// per-frame byte spans the skipped passes consume, plus a program/decal revision), never wall-clock heuristics, so
    /// a camera ease — any input change at all — re-renders. The engine additionally forces a render whenever a live
    /// screen source is bound or a carve bake is in progress (their content changes without touching a packed span).
    /// Default <see langword="false"/> = the gate is OFF and every frame renders fully — BYTE-IDENTICAL to a build
    /// without the gate (the owner owes the shipped-default pick, matching render-scale/present-rate precedent).
    /// Presentation-only: never involves simulation state, and a skipped frame's simulation is unaffected.</summary>
    public bool EnableCadenceGate { get; init; }
}
