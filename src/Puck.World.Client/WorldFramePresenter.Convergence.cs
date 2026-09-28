using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.World.Client;

public sealed partial class WorldFramePresenter {
    private FrameCaptureRequest? m_convergence;
    private SdfFrame? m_convergedFrame;

    private readonly List<(object Source, long Revision, long Cut)> m_viewCuts = [];
    private readonly object?[] m_seatCameraRigs = new object?[PlayerRoster.MaxSlots];

    private long ViewCut(int index, object source, long revision) {
        if (index == m_viewCuts.Count) {
            m_viewCuts.Add(item: (source, revision, 0));
        }
        var previous = m_viewCuts[index];

        if (!ReferenceEquals(objA: previous.Source, objB: source) || (previous.Revision != revision)) {
            previous = (source, revision, (previous.Cut + 1));
            m_viewCuts[index] = previous;
        }
        return previous.Cut;
    }

    /// <inheritdoc/>
    public void BeginConvergence(FrameCaptureRequest request) {
        ArgumentNullException.ThrowIfNull(argument: request);
        if (!ReferenceEquals(objA: m_convergence, objB: request)) {
            m_convergence = request;
            m_convergedFrame = null;
        }
    }
    /// <inheritdoc/>
    public SdfFrame CaptureFrame(uint width, uint height, float deltaSeconds, float interpolationAlpha) {
        // deltaSeconds is the launcher's clamped presentation interval, distinct from its whole-step simulation delta.
        // It may drive visual-only animation and the FPS witness, but never feeds authoritative world state.
        if (m_convergence is { Completion.IsCompleted: false }) {
            if (m_convergedFrame is { } frozen) {
                return frozen with { ProgramChanged = false };
            }
            deltaSeconds = 0f;
        } else {
            m_convergedFrame = null;
        }
        m_elapsedSeconds += deltaSeconds;
        m_frameRate.Sample(deltaSeconds: deltaSeconds);

        if (m_displayExtentSupplied) {
            width = m_displayWidth;
            height = m_displayHeight;
        } else {
            m_displayWidth = width;
            m_displayHeight = height;
        }

        // Simulation has already advanced on the launcher's exact fixed ticks; the client view holds the two latest
        // snapshot poses. Each active entry's render pose is Lerp(previous tick → current, alpha) plus any eased
        // server-correction offset, so above the fixed-step rate the crowd glides instead of stepping; a frame that banked zero
        // sub-steps holds a stable lerp (previous == current), no snap-back. Presentation only: every body.where
        // still reads the authoritative sim pose server-side.
        m_client.UpdateRenderPoses(alpha: interpolationAlpha);
        // Bound state presents at this frame's position between the last two ticks before anything reads it: the
        // program build, the transform pack (look lanes, drivers, poses, effectors, body scale), and the cameras,
        // markers and HUD the dress resolves.
        m_client.StateMirror.Apply(fraction: (PinsStateFraction
            ? 1f
            : interpolationAlpha));

        // Advance the animated-placement replay cursors on the render clock (hold-style — transforms move; the
        // program itself never rebuilds for a timeline step), and latch the same delta for the scene's own
        // catalog-avatar root followers (WorldSceneEmitter.PackDynamicTransforms consumes it once).
        m_animator.Tick(deltaSeconds: deltaSeconds);
        m_emitter.Tick(deltaSeconds: deltaSeconds);

        // A no-op after PrepareGraph reconciled this frame's delivery; a capture no graph prepares reconciles here.
        ReconcileDelivery();
        m_bakes?.Pump(definition: m_client.Definition);

        m_continuum.BeginFrame();
        try {
            var frame = m_composed.CaptureFrame(
                deltaSeconds: deltaSeconds,
                height: height,
                interpolationAlpha: interpolationAlpha,
                width: width
            );

            // The camera views film this frame, with the transforms and route choices its dress reads.
            m_binder.PresentFrame(
                authoritativeTick: m_simulation.Tick,
                transforms: m_transforms
            );

            if (m_convergence is { Completion.IsCompleted: false }) {
                m_convergedFrame = frame;
            }
            return frame;
        } finally {
            m_continuum.EndFrame();
        }
    }

    private FrameContext FrozenContext(in FrameContext context) =>
        ((m_convergence is { Completion.IsCompleted: false })
            ? (context with { FrameDeltaTicks = 0 })
            : context);
}
