using Puck.Input.Devices;

namespace Puck.Input;

/// <summary>
/// The engine-owned single-drainer mechanism: every consumer of a gamepad's per-frame state reads the frame's drain
/// through here instead of calling <see cref="GamepadManager.Drain"/> itself, so the manager's destructive per-device
/// drain has exactly one caller per produced frame however many consumers read it. Which seat a device drives is not
/// decided here: the command router's slot resolver and the host's roster own that.
/// </summary>
public interface IInputArbiter {
    /// <summary>
    /// Performs the run's one destructive <see cref="GamepadManager.Drain"/> for this produced frame, if it has not
    /// already run for <paramref name="frameKey"/>. Idempotent: any number of consumers may call this with the same key
    /// in the same frame, and only the first call does real work.
    /// </summary>
    /// <param name="frameKey">
    /// A value that is the same for every call meant to observe one produced frame's input, and different from the
    /// previous frame's (a render-tick counter or a simulation tick number both work; the arbiter does not interpret it
    /// beyond equality).
    /// </param>
    void DrainFrame(ulong frameKey);
    /// <summary>Copies the current frame's full per-device drain into caller-owned reusable storage.</summary>
    /// <param name="destination">The list to clear and fill with the last frame passed to <see cref="DrainFrame"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    void CopyDrainedDevices(List<GamepadDrain> destination);
}
/// <summary>
/// The <see cref="IInputArbiter"/> implementation: owns exactly one <see cref="GamepadManager"/> and provides the
/// canonical single-drainer mechanism for all gamepad consumers.
/// </summary>
public sealed class InputArbiter : IInputArbiter {
    private readonly List<GamepadDrain> m_drainBuffer = [];
    private readonly Lock m_gate = new();

    private readonly GamepadManager m_manager;

    private ulong? m_drainedFrame;

    /// <summary>Initializes a new instance of the <see cref="InputArbiter"/> class.</summary>
    /// <param name="manager">The manager this arbiter is the sole drainer of.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <see langword="null"/>.</exception>
    public InputArbiter(GamepadManager manager) {
        ArgumentNullException.ThrowIfNull(manager);

        m_manager = manager;
    }

    /// <inheritdoc/>
    public void CopyDrainedDevices(List<GamepadDrain> destination) {
        ArgumentNullException.ThrowIfNull(destination);

        lock (m_gate) {
            destination.Clear();
            destination.AddRange(collection: m_drainBuffer);
        }
    }
    /// <inheritdoc/>
    public void DrainFrame(ulong frameKey) {
        lock (m_gate) {
            // A second call for the same produced frame is a no-op, not a re-drain: GamepadManager.Drain is
            // destructive per device, so draining twice would discard whatever arrived between the calls.
            if (m_drainedFrame == frameKey) {
                return;
            }

            m_drainedFrame = frameKey;
            m_manager.Drain(buffer: m_drainBuffer);
        }
    }
}
