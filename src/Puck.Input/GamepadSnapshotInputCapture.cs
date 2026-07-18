using Puck.Commands;

namespace Puck.Input;

/// <summary>The standard gamepad contribution for a launcher-driven snapshot loop: drains the shared arbiter once for
/// the host frame, then captures every device into the deterministic <see cref="InputRouter"/>.</summary>
public sealed class GamepadSnapshotInputCapture : ISnapshotInputCapture {
    private readonly IInputArbiter m_arbiter;
    private readonly GamepadCaptureSource m_capture;

    /// <summary>Initializes the contribution over the process's one gamepad arbiter and capture clock.</summary>
    public GamepadSnapshotInputCapture(IInputArbiter arbiter, InputRouter router, IInputClock clock, Func<InputDeviceId, bool>? isActiveFor = null) {
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(router);

        m_arbiter = arbiter;
        m_capture = new GamepadCaptureSource(router: router, clock: clock, isActiveFor: isActiveFor);
    }

    /// <inheritdoc/>
    public void CaptureFrame(ulong frameKey) {
        m_arbiter.DrainFrame(frameKey: frameKey);
        m_capture.Capture(drains: m_arbiter.DrainedDevices);
    }
}
