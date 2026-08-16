using Puck.Commands;

namespace Puck.Input.Output;

/// <summary>
/// The <see cref="IGamepadOutput"/> facade for one device. Every method validates the capability and the
/// device's liveness, then enqueues a <see cref="GamepadOutputCommand"/> for the device's I/O loop to write —
/// it never touches the native handle directly, so callers on any thread are safe.
/// </summary>
public sealed class GamepadOutput : IGamepadOutput {
    private const int Accepting = 1;
    private const int Killed = 2;
    private const int Suspended = 0;

    private readonly Lock m_gate = new();
    private readonly GamepadOutputQueue m_queue;

    private int m_state;

    /// <summary>Initializes output control for one connected gamepad.</summary>
    /// <param name="deviceId">The device that receives output commands.</param>
    /// <param name="capabilities">The output effects supported by the device.</param>
    /// <param name="queue">The device I/O loop's bounded command queue.</param>
    /// <param name="accepting">Whether the output begins ready to accept requests.</param>
    /// <exception cref="ArgumentNullException"><paramref name="queue"/> is <see langword="null"/>.</exception>
    public GamepadOutput(
        InputDeviceId deviceId,
        GamepadOutputCapabilities capabilities,
        GamepadOutputQueue queue,
        bool accepting = true
    ) {
        ArgumentNullException.ThrowIfNull(queue);

        Capabilities = capabilities;
        DeviceId = deviceId;
        m_queue = queue;
        m_state = (accepting
            ? Accepting
            : Suspended
        );
    }

    /// <inheritdoc />
    public GamepadOutputCapabilities Capabilities { get; }
    /// <inheritdoc />
    public InputDeviceId DeviceId { get; }

    /// <summary>Resumes output after a wireless receiver slot begins streaming again.</summary>
    internal void Resume() {
        lock (m_gate) {
            if (m_state == Suspended) {
                m_state = Accepting;
            }
        }
    }
    /// <summary>Temporarily rejects and clears output while a wireless receiver slot is empty.</summary>
    internal void Suspend() {
        lock (m_gate) {
            if (m_state == Accepting) {
                m_state = Suspended;
            }

            m_queue.Clear();
        }
    }

    private bool TryEnqueue(GamepadOutputCapabilities required, in GamepadOutputCommand command) {
        lock (m_gate) {
            if (
                (m_state != Accepting) ||
                !Capabilities.HasFlag(flag: required)
            ) {
                return false;
            }

            return m_queue.TryEnqueue(command: in command);
        }
    }

    /// <summary>Marks the handle dead after the device disconnects; further requests are rejected.</summary>
    public void Kill() {
        lock (m_gate) {
            m_state = Killed;
            m_queue.Clear();
        }
    }
    /// <inheritdoc />
    public bool Rumble(in RumbleEffect effect) {
        return TryEnqueue(
            command: new GamepadOutputCommand(
                Kind: GamepadOutputKind.Rumble,
                Led: default,
                Raw: null,
                Rumble: effect,
                TriggerRumble: default
            ),
            required: GamepadOutputCapabilities.Rumble
        );
    }
    /// <inheritdoc />
    public bool RumbleTriggers(in TriggerRumbleEffect effect) {
        return TryEnqueue(
            command: new GamepadOutputCommand(
                Kind: GamepadOutputKind.TriggerRumble,
                Led: default,
                Raw: null,
                Rumble: default,
                TriggerRumble: effect
            ),
            required: GamepadOutputCapabilities.TriggerRumble
        );
    }
    /// <inheritdoc />
    public bool SendEffect(ReadOnlySpan<byte> data) {
        return TryEnqueue(
            command: new GamepadOutputCommand(
                Kind: GamepadOutputKind.Raw,
                Led: default,
                Raw: data.ToArray(),
                Rumble: default,
                TriggerRumble: default
            ),
            required: GamepadOutputCapabilities.RawEffect
        );
    }
    /// <inheritdoc />
    public bool SetLed(in LedColor color) {
        return TryEnqueue(
            command: new GamepadOutputCommand(
                Kind: GamepadOutputKind.Led,
                Led: color,
                Raw: null,
                Rumble: default,
                TriggerRumble: default
            ),
            required: GamepadOutputCapabilities.Led
        );
    }
    /// <inheritdoc />
    public bool SetTriggerEffect(in TriggerEffectSpec left, in TriggerEffectSpec right) {
        return TryEnqueue(
            command: new GamepadOutputCommand(
                Kind: GamepadOutputKind.TriggerEffect,
                Led: default,
                Raw: null,
                Rumble: default,
                TriggerRumble: default,
                TriggerEffectLeft: left,
                TriggerEffectRight: right
            ),
            required: GamepadOutputCapabilities.TriggerEffect
        );
    }
    /// <inheritdoc />
    public bool SetTriggerEffectAt(in TriggerEffectSpec left, in TriggerEffectSpec right, ulong fireAtTick) {
        return TryEnqueue(
            command: new GamepadOutputCommand(
                Kind: GamepadOutputKind.TriggerEffect,
                Led: default,
                Raw: null,
                Rumble: default,
                ScheduleTick: fireAtTick,
                TriggerEffectLeft: left,
                TriggerEffectRight: right,
                TriggerRumble: default
            ),
            required: GamepadOutputCapabilities.TriggerEffect
        );
    }
}
