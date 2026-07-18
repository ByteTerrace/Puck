using System.Globalization;
using System.Numerics;

using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>
/// The REVEALED-ROOM fixed-camera perf-bench channel's frame-source surface — thin forwarders the render node and
/// <c>OverworldControlCommandModule</c>'s <c>room.bench</c> verb drive. The whole channel is composed behind ONE
/// facade (<see cref="RoomBenchScene"/>) so this source names a single type for it, mirroring the SDF-debug mode's
/// own <c>OverworldFrameSource.SdfDebug.cs</c> shape. Presentation only — the deterministic simulation never learns
/// the bench exists.
/// </summary>
public sealed partial class OverworldFrameSource {
    /// <inheritdoc/>
    public string RoomBench(string[] args) {
        ArgumentNullException.ThrowIfNull(argument: args);

        if ((args.Length > 0) && string.Equals(a: args[0], b: "abort", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return m_roomBench.Abort();
        }

        var frames = 0;

        if ((args.Length > 0) && !int.TryParse(s: args[0], style: NumberStyles.Integer, provider: CultureInfo.InvariantCulture, result: out frames)) {
            return "[room.bench: usage — room.bench [n|abort] (n = sample-frame count, default ~300)]";
        }

        return m_roomBench.Start(frames: frames);
    }

    // Asserts (or releases, on the falling edge) the room-bench's fixed camera pin onto the director's
    // ScenarioCameraPose seam — called once per composed frame, right before Compose, so the pin (or its release) is
    // in effect for the frame about to render. See RoomBenchScene.CameraFrame's remarks for why this seam.
    private void ApplyRoomBenchCameraPose() {
        if (m_roomBench.Running) {
            m_director.ScenarioCameraPose = m_roomBench.CameraFrame;
            m_roomBenchHeld = true;
        } else if (m_roomBenchHeld) {
            m_director.ScenarioCameraPose = null;
            m_roomBenchHeld = false;
        }
    }

    /// <summary>Whether a room-bench run is in flight — the render node feeds it per-pass GPU timings each produced
    /// frame (<see cref="AdvanceRoomBench"/>).</summary>
    public bool RoomBenchRunning => m_roomBench.Running;

    /// <summary>Advances an in-flight room-bench run one produced frame with the previous frame's per-pass GPU ms.
    /// The node reads the timings (it owns the producer) and passes them through; this source injects the LIVE
    /// render-scale tier name it already tracks (<c>m_renderScaleTierName</c>) so the eventual summary line always
    /// names the tier the sample window actually measured.</summary>
    /// <param name="hasTimings">Whether this frame's readback carried valid GPU timestamps.</param>
    /// <param name="passMilliseconds">Each render pass's milliseconds, in <c>SdfEngineNode.PassTimingLabels</c> order.</param>
    /// <param name="passCount">How many entries of <paramref name="passMilliseconds"/> are valid.</param>
    /// <param name="frame">The whole-frame milliseconds.</param>
    /// <param name="backendIsDirectX">Whether the host backend is Direct3D 12 (else Vulkan).</param>
    public void AdvanceRoomBench(bool hasTimings, ReadOnlySpan<double> passMilliseconds, int passCount, double frame, bool backendIsDirectX) =>
        m_roomBench.Advance(hasTimings: hasTimings, passMilliseconds: passMilliseconds, passCount: passCount, frame: frame, renderScaleTierName: m_renderScaleTierName, backendIsDirectX: backendIsDirectX);

    // ---- The engine-benchmark staging seam ---------------------------------------------------------------------------
    // The engine benchmark's world/feature scenes drive their per-frame CAMERA and (for the synthetic scenes) their
    // rendered PROGRAM through this source, exactly as room.bench pins its camera. The choreography is content-side
    // (Puck.Demo.Bench.*) and, unlike room.bench, is driven by the CONTENT-BLIND Puck.Bench harness — so the pins are
    // ONE-FRAME STICKY: a scene controller re-arms them every OnFrame, and a scene that stops driving (its teardown)
    // simply leaves them un-armed, so ApplyBenchStage auto-releases on the very next composed frame with no teardown
    // verb. Presentation only — the deterministic simulation never learns the bench exists.
    //
    // The workload PROGRAM is built content-side (SyntheticBenchWorkloads, which names SdfDebugRenderer/SdfBenchWorkloads)
    // and handed here already-constructed, so this source — at its exact analyzer coupling ceiling — takes on NO new type
    // (it holds only an SdfProgram, DynamicTransforms, a Vector3, and floats, all already in its coupling set).
    private SdfProgram? m_benchWorkloadProgram;
    private IReadOnlyList<DynamicTransform> m_benchWorkloadTransforms = [];
    private (Vector3 Target, float Yaw, float Pitch, float Distance)? m_benchStagePose;
    private bool m_benchStageRequested;
    private bool m_benchWorkloadRequested;
    private bool m_benchFullscreenRequested;
    private bool m_benchWorkloadActive;
    private bool m_benchCameraHeld;
    private bool m_benchFullscreenHeld;
    // The headless carve-bake advance hook: a synthetic sdf.carves scene
    // registers this so the engine's per-produced-frame AdvanceBricks drives its settle-0 planner with the live brick-
    // bake service — the ONLY seam through which the pool-owning engine reaches content. It runs every produced frame
    // while set (even during the harness's await-ready window, so the background bake completes before sampling), then
    // clears on the scene's teardown. Independent of the interactive SDF-debug mode's own planner (m_sdfDebug), which
    // advances alongside it. Presentation only.
    private Action<Puck.SdfVm.ISdfBrickBakeService>? m_benchBrickAdvance;

    // The four shader-level bench toggles' live demo-side state (SdfFrame.DisableSoftShadows / DisableAmbientOcclusion
    // / ShadowDistanceScale / DisableScreenLights). Dress reads these every frame; the
    // feature-switch registry's Set delegates (registered by BenchInstaller) write them. Default = every effect ON
    // (an unset frame uploads 0). m_benchShadowDistanceScale 0 = the full 1.0
    // reach; 0.5/0.25 shorten far shadows. These live HERE (not on the SdfVm-owned SdfDebugMode) because the four new
    // lanes are demo-driven bench levers with no debug-mode verb of their own.
    private bool m_benchDisableSoftShadows;
    private bool m_benchDisableAmbientOcclusion;
    private float m_benchShadowDistanceScale;
    private bool m_benchDisableScreenLights;
    // PATH B — the shadow-proxy lever's live demo-side state (SdfFrame.EnableShadowProxy). Default OFF (false) = the full
    // shadow occluder set (byte-identical); ON => shadow rays skip Subtraction-family carves and march the pre-carve
    // union hull. Written by the sdf.shadow-proxy feature switch (BenchInstaller); read by Dress every frame.
    private bool m_benchEnableShadowProxy;
    // CADENCE GATE (perf plan Phase 6.1) live demo-side state (SdfFrame.EnableCadenceGate). Default OFF (false) =
    // byte-identical (every frame renders fully). ON => the engine skips the mask/beam/cull-args/views passes and
    // re-composites when a frame's render-consumed inputs are unchanged. Written by the sdf.cadence-gate feature switch
    // (BenchInstaller); read by Dress every frame.
    private bool m_benchEnableCadenceGate;
    // The F1/F2 FAR-FIELD isolators' live demo-side state. Both ship ON (default true = the shipped behavior; an unset
    // frame uploads 0 to the disable lanes). m_benchFarBound false disables the F1 beam-published per-tile far bound
    // (SdfFrame.DisableFarBound — the fine march runs to MaxDistance, output-identical either way); m_benchShadowFarExit
    // false disables the F2 soft-shadow light-side early exit (SdfFrame.DisableShadowFarExit — a march-path change).
    // Written by the sdf.far-bound / sdf.shadow-far-exit feature switches (BenchInstaller); read by Dress every frame.
    private bool m_benchFarBound = true;
    private bool m_benchShadowFarExit = true;

    /// <summary>Whether a synthetic-workload takeover is rendering this frame (the bench's sdf.* scenes) — read by
    /// <see cref="CaptureFrame"/>'s takeover dispatch to render <see cref="BenchWorkloadProgram"/> in place of the room.</summary>
    internal bool BenchWorkloadActive => m_benchWorkloadActive;

    /// <summary>The pinned synthetic-workload program (valid only while <see cref="BenchWorkloadActive"/>).</summary>
    internal SdfProgram? BenchWorkloadProgram => m_benchWorkloadProgram;

    /// <summary>The pinned synthetic workload's per-frame dynamic transforms (empty for a program with no dynamic
    /// slots; a spread grid for the storm rung so the moving instances are framed and bounded).</summary>
    internal IReadOnlyList<DynamicTransform> BenchWorkloadTransforms => m_benchWorkloadTransforms;

    /// <summary>Arms the bench CAMERA pin for THIS frame (the flythrough dolly's per-sample pose, the active-room's
    /// fixed overview). One-frame sticky — a scene re-arms it every produced frame; when it stops, the pin releases on
    /// the next composed frame (no teardown verb). Asserts onto the director's verbatim <c>ScenarioCameraPose</c> seam.</summary>
    /// <param name="target">The look target (render-relative world units).</param>
    /// <param name="yaw">The orbit yaw (radians).</param>
    /// <param name="pitch">The orbit pitch (radians).</param>
    /// <param name="distance">The orbit distance (world units).</param>
    /// <param name="fullscreen">Whether to also force the room to fullscreen (hiding every game pane) — the active-room
    /// scene sets it so the four booted cabinets' lit diegetic CRT faces + their screen lights are measured in ONE
    /// fullscreen overview rather than a quad tiling. The flythrough leaves it false (a zero-cabinet room is already
    /// fullscreen).</param>
    public void ArmBenchCamera(Vector3 target, float yaw, float pitch, float distance, bool fullscreen = false) {
        m_benchStagePose = (target, yaw, pitch, distance);
        m_benchStageRequested = true;
        m_benchFullscreenRequested |= fullscreen;
    }

    /// <summary>Arms a synthetic-WORKLOAD takeover for THIS frame: the given already-built program renders fullscreen in
    /// place of the room, framed by the given pose. One-frame sticky like <see cref="ArmBenchCamera"/> — the same cached
    /// program instance is re-armed each frame (so <see cref="Dress"/>'s reference-diff rebuilds the views ONCE, on
    /// entry), and dropping it (teardown) reverts to the room next frame. The program + transforms come from the
    /// content-side <c>SyntheticBenchWorkloads</c> builder so this source names no bench-workload type.</summary>
    /// <param name="program">The pre-built workload program (cached by the caller — pass the SAME instance each frame).</param>
    /// <param name="transforms">The workload's per-frame dynamic transforms (must satisfy the program's required
    /// dynamic-transform capacity; empty for a static workload).</param>
    /// <param name="target">The framing look target.</param>
    /// <param name="yaw">The framing yaw (radians).</param>
    /// <param name="pitch">The framing pitch (radians).</param>
    /// <param name="distance">The framing distance (world units).</param>
    public void ArmBenchWorkload(SdfProgram program, IReadOnlyList<DynamicTransform> transforms, Vector3 target, float yaw, float pitch, float distance) {
        ArgumentNullException.ThrowIfNull(argument: program);
        ArgumentNullException.ThrowIfNull(argument: transforms);

        m_benchWorkloadProgram = program;
        m_benchWorkloadTransforms = transforms;
        m_benchStagePose = (target, yaw, pitch, distance);
        m_benchStageRequested = true;
        m_benchWorkloadRequested = true;
        m_benchFullscreenRequested = true;
    }

    // Consumes this frame's bench-stage requests (called once per CaptureFrame, right after ApplyRoomBenchCameraPose and
    // before the director composes / the takeover dispatch): assert or release the camera pin, latch the workload-active
    // state, then clear the per-frame request flags so a scene that stopped driving auto-releases next frame. The two
    // camera pins (room.bench + this) are mutually exclusive in practice — a room.bench run and a bench-harness run never
    // overlap — so whichever is active owns ScenarioCameraPose; when neither drives, its falling edge clears the seam.
    private void ApplyBenchStage() {
        if (m_benchStageRequested && (m_benchStagePose is { } pose)) {
            m_director.ScenarioCameraPose = (pose.Target, pose.Yaw, pose.Pitch, pose.Distance, false);
            m_benchCameraHeld = true;
        } else if (m_benchCameraHeld) {
            m_director.ScenarioCameraPose = null;
            m_benchCameraHeld = false;
        }

        m_benchWorkloadActive = m_benchWorkloadRequested;

        // Fullscreen forcing (CreatorView eases the room up and hides every game pane): a synthetic-workload scene needs
        // it so its fullscreen program isn't tiled into a quad by a still-booted cabinet (room.active leaves four booted;
        // there is no eject verb), and the active-room scene requests it so its four lit diegetic CRTs are framed in ONE
        // fullscreen overview. The flythrough leaves it off (a zero-cabinet room is already fullscreen). Restored on the
        // falling edge.
        if (m_benchFullscreenRequested) {
            m_director.CreatorView = true;
            m_benchFullscreenHeld = true;
        } else if (m_benchFullscreenHeld) {
            m_director.CreatorView = false;
            m_benchFullscreenHeld = false;
        }

        m_benchStageRequested = false;
        m_benchWorkloadRequested = false;
        m_benchFullscreenRequested = false;
    }

    /// <summary>The live soft-shadow bench lever (<see cref="SdfFrame.DisableSoftShadows"/>). <see langword="false"/>
    /// (default) = shadows ON. The <c>sdf.soft-shadows</c> feature switch's backing state.</summary>
    public bool BenchDisableSoftShadows { get => m_benchDisableSoftShadows; set => m_benchDisableSoftShadows = value; }

    /// <summary>The live AO bench lever (<see cref="SdfFrame.DisableAmbientOcclusion"/>). <see langword="false"/>
    /// (default) = AO ON. The <c>sdf.ao</c> feature switch's backing state.</summary>
    public bool BenchDisableAmbientOcclusion { get => m_benchDisableAmbientOcclusion; set => m_benchDisableAmbientOcclusion = value; }

    /// <summary>The live soft-shadow reach scale (<see cref="SdfFrame.ShadowDistanceScale"/>). <c>0</c> (default) = the
    /// full 1.0 reach; 0.5/0.25 shorten far shadows. The <c>sdf.shadow-distance</c> feature switch's backing state.</summary>
    public float BenchShadowDistanceScale { get => m_benchShadowDistanceScale; set => m_benchShadowDistanceScale = value; }

    /// <summary>The live screen-lights bench lever (<see cref="SdfFrame.DisableScreenLights"/>). <see langword="false"/>
    /// (default) = the diegetic CRTs spill light. The <c>sdf.screen-lights</c> feature switch's backing state.</summary>
    public bool BenchDisableScreenLights { get => m_benchDisableScreenLights; set => m_benchDisableScreenLights = value; }

    /// <summary>The live shadow-proxy bench lever (<see cref="SdfFrame.EnableShadowProxy"/>). <see langword="false"/>
    /// (default) = the full shadow occluder set; <see langword="true"/> = shadow rays skip Subtraction-family carves and
    /// march the pre-carve union hull. The <c>sdf.shadow-proxy</c> feature switch's backing state.</summary>
    public bool BenchEnableShadowProxy { get => m_benchEnableShadowProxy; set => m_benchEnableShadowProxy = value; }

    /// <summary>The live cadence-gate lever (<see cref="SdfFrame.EnableCadenceGate"/>). <see langword="false"/>
    /// (default) = the gate is OFF and every frame renders fully (byte-identical); <see langword="true"/> lets the
    /// engine skip the render passes and re-composite for an unchanged frame. The <c>sdf.cadence-gate</c> feature
    /// switch's backing state.</summary>
    public bool BenchEnableCadenceGate { get => m_benchEnableCadenceGate; set => m_benchEnableCadenceGate = value; }

    /// <summary>The live F1 far-bound isolator (<see cref="SdfFrame.DisableFarBound"/>, inverted). <see langword="true"/>
    /// (default) = the beam-published per-tile far bound is ACTIVE (output-identical, fewer empty-sky steps);
    /// <see langword="false"/> marches to MaxDistance exactly as pre-F1. The <c>sdf.far-bound</c> feature switch's backing
    /// state — the "off" side of the owner's far-field A/B.</summary>
    public bool BenchFarBound { get => m_benchFarBound; set => m_benchFarBound = value; }

    /// <summary>The live F2 shadow-far-exit isolator (<see cref="SdfFrame.DisableShadowFarExit"/>, inverted).
    /// <see langword="true"/> (default) = <c>softShadow</c>'s no-further-darkening early exit is ACTIVE (a march-path
    /// change); <see langword="false"/> runs the full shadow budget/reach exactly as pre-F2. The
    /// <c>sdf.shadow-far-exit</c> feature switch's backing state — the "off" side of the owner's shadow far-exit A/B.</summary>
    public bool BenchShadowFarExit { get => m_benchShadowFarExit; set => m_benchShadowFarExit = value; }

    /// <summary>Registers (or clears with <see langword="null"/>) the headless carve-bake advance hook (see
    /// <c>m_benchBrickAdvance</c>): while set, the engine's per-produced-frame <see cref="AdvanceBricks"/> drives it with
    /// the live brick-bake service so a synthetic <c>sdf.carves</c> scene settles its bake off the measured path. The
    /// interactive SDF-debug mode's own planner still advances independently.</summary>
    /// <param name="advance">The per-frame advance callback, or <see langword="null"/> to clear it.</param>
    public void SetBenchBrickAdvance(Action<Puck.SdfVm.ISdfBrickBakeService>? advance) => m_benchBrickAdvance = advance;

    /// <summary>The room's console count — the active-room bench scene reads it to know how many cabinets to boot/poll.</summary>
    public int BenchConsoleCount => m_room.Consoles.Count;

    /// <summary>How many consoles are currently booted (emulating) — the active-room bench scene's readiness poll waits
    /// for this to reach the count it booted.</summary>
    public int BenchBootedConsoleCount => BitOperations.PopCount(value: m_world.BootedMask);
}
