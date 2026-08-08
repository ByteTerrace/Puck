using System.Numerics;
using Puck.Abstractions.Cameras;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm.Debug;
using Puck.SdfVm.Views;

namespace Puck.SdfVm.Bench;

/// <summary>
/// The <see cref="ISdfFrameSource"/> that drives <see cref="SdfBenchScene"/>'s <c>DynamicMatrix</c> ladder to
/// completion, then requests the host terminal to exit. Owns no scene of its own — every frame IS the active bench
/// configuration's workload (<see cref="SdfDebugRenderer.EmitBench"/>), framed by the bench's own fixed deterministic
/// camera pose (<see cref="SdfBenchScene.CameraFrame"/>). Presentation-only measurement plumbing: this is a bench
/// harness, not a game frame source.
/// </summary>
internal sealed class DynamicMatrixBenchFrameSource : ISdfFrameSource {
    // Matches SdfBenchScene's own (private) FieldOfViewRadians — the bench's ComputeDistance sizes the camera pose
    // assuming this exact vertical FOV, so the rendered camera must use the same value or the framing margin drifts
    // from what the bench measured its distance against.
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));

    private readonly SdfBenchScene m_bench = new();
    private readonly SdfDebugRenderer m_renderer = new();
    private readonly bool m_backendIsDirectX;
    private SdfProgram? m_program;
    private int m_builtRevision = -1;
    private float m_elapsedSeconds;
    private bool m_exitRequested;
    // The most recently packed per-frame dynamic transforms — cached so the POST-FINISH frames (returned once
    // SdfBenchScene.Running goes false, while the host drains its exit request) keep supplying however many slots the
    // LAST uploaded program requires. TryPackStormTransforms returns false once Running is false, so re-calling it
    // post-finish would hand back an empty array — which THROWS in SdfWorldEngine.PrepareFrame whenever the final
    // config was a MOVING rung (it requires exactly its instance count of dynamic slots).
    private IReadOnlyList<DynamicTransform> m_lastTransforms = [];

    /// <summary>Initializes the harness and immediately starts either the FULL DynamicMatrix ladder or, when
    /// <paramref name="singleRung"/> is supplied, ONE bisection-point rung (so the very first captured frame already
    /// has a valid active configuration either way).</summary>
    /// <param name="warmFrames">The per-configuration warm-up frame count (a fresh pipeline's first dispatch pays a
    /// compile stall — see <see cref="SdfBenchScene"/>'s remarks).</param>
    /// <param name="sampleFrames">The per-configuration measured-sample frame count.</param>
    /// <param name="backendIsDirectX">Whether this run hosts on Direct3D 12 (Vulkan otherwise) — echoed into the
    /// printed report header.</param>
    /// <param name="singleRung">When supplied, runs ONLY this one (placement, moving, count) rung instead of the
    /// whole ladder — the fast bisection-point path a harness invocation uses to pin a budget-crossing knee without
    /// re-running the whole (expensive) battery.</param>
    public DynamicMatrixBenchFrameSource(int warmFrames, int sampleFrames, bool backendIsDirectX, (SdfBenchPlacement Placement, bool Moving, int Count)? singleRung = null) {
        m_backendIsDirectX = backendIsDirectX;

        _ = m_bench.SetWarmFrames(frames: warmFrames);
        _ = m_bench.SetSampleFrames(frames: sampleFrames);
        _ = ((singleRung is { } rung)
            ? m_bench.StartDynamicMatrixRung(placement: rung.Placement, moving: rung.Moving, count: rung.Count)
            : m_bench.StartDynamicMatrix());
    }

    /// <summary>The live engine node — set by the composition root right after <c>SdfWorldRenderBuilder.Build</c>
    /// returns (a settable slot, mirroring Puck.World's own <c>probe.Node = render.Producer</c> pattern), so this
    /// source can read back the PREVIOUS produced frame's GPU pass timings and CPU rebuild cost from inside its own
    /// <see cref="CaptureFrame"/> — exactly where <see cref="SdfBenchScene.Advance"/> documents it should be fed.</summary>
    public SdfEngineNode? Node { get; set; }

    /// <summary>The host terminal control — set by the composition root once the host is built. Requested to exit the
    /// moment the bench run finishes (<see cref="SdfBenchScene.Running"/> goes false).</summary>
    public ITerminalControl? Terminal { get; set; }

    /// <summary>Whether the run has finished and an exit was requested — the composition root's own sanity check that
    /// the process is exiting because the matrix completed, not because the window was closed externally.</summary>
    public bool ExitRequested => m_exitRequested;

    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        m_elapsedSeconds += deltaSeconds;

        // Feed the PREVIOUS produced frame's GPU pass timings + CPU instance-grid-rebuild cost into the bench state
        // machine BEFORE building THIS frame's program — the documented order (SdfBenchScene.Advance's remarks).
        // Node/its engine are null on frame 1 (not yet constructed) and TryReadPassTimings then returns false, which
        // Advance handles gracefully (no-op sample).
        Span<double> passMilliseconds = stackalloc double[SdfEngineNode.PassTimingCount];
        var passCount = 0;
        var frameMs = 0.0;
        var hasTimings = ((Node is { } node) && node.TryReadPassTimings(passMilliseconds: passMilliseconds, passCount: out passCount, frame: out frameMs));
        var beam = (hasTimings ? SdfEngineNode.PassMilliseconds(passMilliseconds: passMilliseconds, passCount: passCount, label: "beam") : 0.0);
        var views = (hasTimings ? SdfEngineNode.PassMilliseconds(passMilliseconds: passMilliseconds, passCount: passCount, label: "views") : 0.0);
        var composite = (hasTimings ? SdfEngineNode.PassMilliseconds(passMilliseconds: passMilliseconds, passCount: passCount, label: "composite") : 0.0);

        m_bench.Advance(
            hasTimings: hasTimings,
            beam: beam,
            views: views,
            composite: composite,
            frame: frameMs,
            width: width,
            height: height,
            backendIsDirectX: m_backendIsDirectX,
            instanceGridRebuildMs: Node?.LastInstanceGridRebuildMilliseconds
        );

        var view = BuildView(width: width, height: height);

        if (!m_bench.Running) {
            // The run finished (SdfBenchScene.Finish already printed the table to stdout). Request the terminal exit
            // exactly once, and keep reusing the LAST program/transforms verbatim (never re-pack — TryPackStormTransforms
            // returns false once Running is false, which would hand back an empty array and THROW in
            // SdfWorldEngine.PrepareFrame whenever the final config was a MOVING rung).
            if (!m_exitRequested) {
                m_exitRequested = true;
                Terminal?.RequestExit();
            }

            return new SdfFrame(Program: m_program!, ProgramChanged: false, Views: [view], Time: m_elapsedSeconds, WarpAmount: 0f) {
                DynamicTransforms = m_lastTransforms,
            };
        }

        var programChanged = (m_builtRevision != m_bench.Revision);

        if (programChanged) {
            var builder = new SdfProgramBuilder();

            m_renderer.EmitBench(builder: builder, config: m_bench.ActiveConfig);
            m_program = builder.Build();
            m_builtRevision = m_bench.Revision;
        }

        m_lastTransforms = (m_bench.TryPackStormTransforms(transforms: out var packed) ? packed : []);

        return new SdfFrame(Program: m_program!, ProgramChanged: programChanged, Views: [view], Time: m_elapsedSeconds, WarpAmount: 0f) {
            DynamicTransforms = m_lastTransforms,
        };
    }

    private SdfViewSnapshot BuildView(uint width, uint height) {
        var pose = (m_bench.CameraFrame ?? (Vector3.Zero, 0f, 0f, 10f, false));
        var eye = (pose.Target + OrbitRig.Offset(yaw: pose.Yaw, pitch: pose.Pitch, distance: pose.Distance));
        var camera = CameraSnapshot.LookAt(
            position: eye,
            target: pose.Target,
            fieldOfViewRadians: FieldOfViewRadians,
            viewportWidth: width,
            viewportHeight: height
        );

        return new SdfViewSnapshot(Camera: camera, Region: new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f));
    }
}
