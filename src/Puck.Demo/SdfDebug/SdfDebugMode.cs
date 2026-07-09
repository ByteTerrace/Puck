using System.Numerics;
using Puck.Input.Devices;
using Puck.SdfVm;

namespace Puck.Demo.SdfDebug;

/// <summary>
/// The composition FACADE for the fullscreen SDF-debug mode — owns the scene state, the pad orbit controller, and the
/// program emitter, and exposes the whole surface as ONE type. The <see cref="Puck.Demo.Overworld.OverworldFrameSource"/>
/// composes exactly this one type (it sits at its analyzer coupling ceiling and cannot name three), driving the mode
/// through these thin members; the <c>sdf.*</c> console module reaches <see cref="Scene"/> through it. Presentation
/// only — the deterministic simulation never sees the debug subject.
/// </summary>
public sealed class SdfDebugMode {
    private readonly SdfDebugScene m_scene = new();
    private readonly SdfBenchScene m_bench = new();
    private readonly SdfDebugRenderer m_renderer = new();
    private readonly SdfDebugController m_controller = new();
    private bool m_active;

    /// <summary>The debug scene the <c>sdf.*</c> verbs mutate (shape, op stack, floor, lift).</summary>
    public SdfDebugScene Scene => m_scene;

    /// <summary>The performance-bench runner the <c>sdf.bench.*</c> verbs drive (an async per-frame state machine).</summary>
    public SdfBenchScene Bench => m_bench;

    /// <summary>Whether the mode is active (the fullscreen debug subject — or a bench workload — replaces the room).</summary>
    public bool Active => m_active;

    /// <summary>Whether a bench run is in flight (the render node feeds it timings each frame; the mode renders the
    /// bench workload rather than the debug subject).</summary>
    public bool BenchRunning => m_bench.Running;

    /// <summary>The content revision the frame source rebuilds on — the debug scene's revision plus the bench's (so a
    /// bench config change forces a program rebuild exactly as a scene edit does).</summary>
    public int Revision => (m_scene.Revision + m_bench.Revision);

    /// <summary>The SLICE view's plane selector as a frame-channel float (0 = camera-locked, 1/2/3 = world X/Y/Z) —
    /// the frame source threads it into <see cref="SdfFrame.DebugSliceAxis"/> every frame.</summary>
    public float SliceAxis => m_scene.SliceAxis;

    /// <summary>The axis slice plane's signed offset (see <see cref="SdfFrame.DebugSliceOffset"/>).</summary>
    public float SliceOffset => m_scene.SliceOffset;

    /// <summary>The camera frame while active — the bench's FIXED deterministic pose when a run is in flight, otherwise
    /// the pad orbit (object-intent). Null when the mode is down.</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? CameraFrame =>
        (m_active ? (m_bench.CameraFrame ?? m_controller.CameraFrame) : null);

    /// <summary>Whether the camera pose must be applied VERBATIM (no easing): true while a bench run supplies the pose,
    /// so every configuration measures an identical, fully settled framing — an eased pose converges on the wall-clock
    /// delta, sampling fast configurations MID-EASE and making tables incomparable run-to-run.</summary>
    public bool CameraSnaps => (m_active && m_bench.Running);

    /// <summary>Poses the orbit camera directly (forwards to <see cref="SdfDebugController.SetPose"/>) — the scriptable
    /// lever the <c>sdf.cam</c> verb drives so a deterministic repro can pin a grazing pitch/framing. No effect on a
    /// bench run (the bench owns a fixed deterministic pose).</summary>
    /// <param name="pitch">The orbit pitch (radians), or null to keep it.</param>
    /// <param name="yaw">The orbit yaw (radians), or null to keep it.</param>
    /// <param name="distance">The orbit distance (world units), or null to keep it.</param>
    /// <param name="target">The orbit target (world units), or null to keep it.</param>
    public void PoseCamera(float? pitch, float? yaw, float? distance, Vector3? target) =>
        m_controller.SetPose(pitch: pitch, yaw: yaw, distance: distance, target: target);

    /// <summary>Enters or leaves the mode; resets the orbit controller's edge tracking on either transition (a held
    /// button never fires a stale edge into the other mode).</summary>
    /// <param name="active">Whether the mode should be active.</param>
    public void SetActive(bool active) {
        if (m_active == active) {
            return;
        }

        m_active = active;
        m_controller.Reset();
    }

    /// <summary>Forwards the creating slot's pad state to the orbit controller (only while active) and drains a pending
    /// pad-chord carve — appending it to the scene and echoing it to stdout. Draining HERE (the per-frame call the
    /// render node already makes) keeps the carve as pure data without new render-node plumbing: a pad carve appends the
    /// exact same <see cref="SdfCarve"/> a scripted <c>sdf.carve</c> does, and the same revision bump rebuilds the program.</summary>
    /// <param name="raw">The pad state.</param>
    /// <param name="deltaSeconds">The render-clock delta.</param>
    public void AdvanceInput(in GamepadState raw, float deltaSeconds) {
        if (!m_active) {
            return;
        }

        m_controller.Advance(raw: in raw, deltaSeconds: deltaSeconds);

        if (m_controller.ConsumeCarveRequest() is { } center) {
            // A pad carve is a hard subtraction at the default radius (the chord carries no size/smooth args — the verb
            // is the way to author those); the SmoothK rides along for a uniform record but is unused while Smooth=false.
            var carve = new SdfCarve(Center: center, Radius: SdfDebugScene.DefaultCarveRadius, Smooth: false, SmoothK: SdfDebugScene.DefaultCarveSmoothK);

            Console.Out.WriteLine(value: m_scene.AddCarve(carve: carve)
                ? $"[sdf.carve (pad) {SdfDebugScene.FormatCarve(carve: carve)}] carves={m_scene.Carves.Count}"
                : $"[sdf.carve (pad): pool full — MaxCarves={SdfDebugScene.MaxCarves} reached (sdf.carve.clear to reset)]");
        }
    }

    /// <summary>Whether the orbit controller's EXIT (North) fired since the last consume (clears it).</summary>
    public bool ConsumeExitRequest() =>
        m_controller.ConsumeExitRequest();

    /// <summary>Emits the LIVE takeover program: the current BENCH workload while a run is in flight, otherwise the
    /// debug subject (+ optional floor).</summary>
    /// <param name="builder">The program builder.</param>
    public void Emit(SdfProgramBuilder builder) {
        if (m_bench.Running) {
            m_renderer.EmitBench(builder: builder, config: m_bench.ActiveConfig);

            return;
        }

        m_renderer.Emit(builder: builder, scene: m_scene);
    }

    /// <summary>Emits the WORST-CASE debug subject for the frame source's capacity probe (never rendered). The BENCH
    /// worst case is a SEPARATE probe (<see cref="EmitBenchProbe"/>) — the mode's takeover is either the debug subject
    /// OR a bench workload, never both, so the envelope is their MAX (not their sum), and 4096 bench instances must not
    /// pile onto the room's own instances in one program (which would exceed the instance cap).</summary>
    /// <param name="builder">The probe builder.</param>
    public void EmitProbe(SdfProgramBuilder builder) =>
        m_renderer.EmitProbe(builder: builder);

    /// <summary>Emits the bench WORST CASE (4096 instances of the wordiest shape) into its OWN probe builder — measured
    /// separately from the room/debug probe and folded as a MAX into the frozen envelope. Never rendered.</summary>
    /// <param name="builder">A fresh probe builder holding only the bench worst case.</param>
    public void EmitBenchProbe(SdfProgramBuilder builder) =>
        m_renderer.EmitBenchProbe(builder: builder);

    /// <summary>Advances an in-flight bench run one produced frame (called from the render node with the previous
    /// frame's per-pass GPU ms + the render info the report header names). No-op when no run is active.</summary>
    public void AdvanceBench(bool hasTimings, double beam, double views, double composite, double frame, uint width, uint height, bool backendIsDirectX) =>
        m_bench.Advance(hasTimings: hasTimings, beam: beam, views: views, composite: composite, frame: frame, width: width, height: height, backendIsDirectX: backendIsDirectX);

    /// <summary>Builds the current debug subject as a standalone program and measures it (word/instance count + the
    /// baked Lipschitz <see cref="SdfProgram.StepScale"/>) — the facts <c>sdf.info</c> reports. Never rendered.</summary>
    /// <returns>The word count, instance count, and step scale.</returns>
    public (int Words, int Instances, float StepScale) Measure() {
        var builder = new SdfProgramBuilder();

        m_renderer.Emit(builder: builder, scene: m_scene);

        var program = builder.Build();

        return (program.Words.Length, program.Instances.Count, program.StepScale);
    }
}
