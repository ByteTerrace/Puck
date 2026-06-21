using Puck.Hosting;

namespace Puck.Launcher;

/// <summary>The terminal's held capabilities, implemented by the one object that owns the window: the
/// <see cref="ITerminalControl"/> baton (lifecycle — the primary engine requests exit through it; the
/// window loop drains it via <see cref="TryConsumeExit"/>) and <see cref="IInputFocus"/> (the right to
/// receive the terminal's input; the loop routes input only while it is active). Exit is a one-shot latch;
/// input focus is a simple held/released flag.</summary>
public sealed class TerminalControl : ITerminalControl, IInputFocus {
    private readonly HashSet<Puck.Commands.InputDeviceId> m_releasedDevices = [];
    private volatile bool m_allReleased;
    private volatile bool m_exitRequested;

    /// <inheritdoc />
    public bool IsActiveFor(Puck.Commands.InputDeviceId deviceId) {
        lock (m_releasedDevices) {
            return !m_allReleased && !m_releasedDevices.Contains(deviceId);
        }
    }

    /// <inheritdoc />
    public void RequestExit() {
        m_exitRequested = true;
    }
    /// <summary>Consumes a pending exit request, returning whether one was set since the last call.</summary>
    public bool TryConsumeExit() {
        if (!m_exitRequested) {
            return false;
        }

        m_exitRequested = false;
        return true;
    }
    /// <inheritdoc />
    public void Release(Puck.Commands.InputDeviceId? deviceId = null) {
        lock (m_releasedDevices) {
            if (deviceId.HasValue) {
                m_releasedDevices.Add(deviceId.Value);
            } else {
                m_allReleased = true;
            }
        }
    }

    /// <inheritdoc />
    public void Claim(Puck.Commands.InputDeviceId? deviceId = null) {
        lock (m_releasedDevices) {
            if (deviceId.HasValue) {
                m_releasedDevices.Remove(deviceId.Value);
            } else {
                m_allReleased = false;
                m_releasedDevices.Clear();
            }
        }
    }
}
