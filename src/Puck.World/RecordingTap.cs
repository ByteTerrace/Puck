using Puck.Abstractions.Capture;
using Puck.Recording.Session;

namespace Puck.World;

/// <summary>
/// The swappable sink the world's <see cref="Puck.Hosting.CapturingRenderNode"/> is wired to for its whole lifetime.
/// The render node is built once (on the first frame); a recording session, by contrast, comes and goes with the
/// <c>capture.start</c>/<c>capture.stop</c> verbs. This relay bridges the two: it forwards captured frames to the armed
/// session (if any) and is otherwise inert. Its <see cref="WantsFrames"/> is the capture node's gate — while no session
/// is armed the node does zero work (no GPU readback, no consume), so leaving the tap permanently in the render tree
/// costs nothing until a capture begins.
/// </summary>
internal sealed class RecordingTap : ICaptureSink {
    private volatile RecordingSession? m_session;

    /// <summary>Gets the armed session, or <see langword="null"/> when idle.</summary>
    public RecordingSession? Current => m_session;

    /// <summary>Gets whether a session is armed — the capture node polls this each frame to decide whether to read back
    /// and consume the frame at all.</summary>
    public bool WantsFrames => (m_session is not null);

    /// <summary>Arms the relay with a session; frames now flow to it.</summary>
    /// <param name="session">The session to receive captured frames.</param>
    public void Arm(RecordingSession session) {
        ArgumentNullException.ThrowIfNull(argument: session);

        m_session = session;
    }

    /// <summary>Disarms the relay and returns the session that was armed (or <see langword="null"/>), so the caller
    /// finalizes it. Frames stop flowing immediately.</summary>
    /// <returns>The previously armed session, or <see langword="null"/>.</returns>
    public RecordingSession? Disarm() {
        var session = m_session;

        m_session = null;

        return session;
    }

    /// <inheritdoc/>
    public void Consume(in CaptureFrame frame) => m_session?.Consume(frame: in frame);

    /// <inheritdoc/>
    public void Dispose() => Disarm()?.Dispose();
}
