using System.Collections.Concurrent;
using Puck.Commands;

namespace Puck.Input.Output;

/// <summary>
/// The <see cref="IGamepadOutput"/> facade for one device. Every method validates the capability and the
/// device's liveness, then enqueues a <see cref="GamepadOutputCommand"/> for the device's I/O loop to write —
/// it never touches the native handle directly, so callers on any thread are safe.
/// </summary>
public sealed class GamepadOutput : IGamepadOutput {
    private readonly ConcurrentQueue<GamepadOutputCommand> m_queue;
    private volatile bool m_alive = true;

    /// <summary>Initializes output control for one connected gamepad.</summary>
    /// <param name="deviceId">The device that receives output commands.</param>
    /// <param name="capabilities">The output effects supported by the device.</param>
    /// <param name="queue">The device I/O loop's command queue.</param>
    /// <exception cref="ArgumentNullException"><paramref name="queue"/> is <see langword="null"/>.</exception>
    public GamepadOutput(
        InputDeviceId deviceId,
        GamepadOutputCapabilities capabilities,
        ConcurrentQueue<GamepadOutputCommand> queue
    ) {
        ArgumentNullException.ThrowIfNull(queue);

        Capabilities = capabilities;
        DeviceId = deviceId;
        m_queue = queue;
    }

    /// <inheritdoc />
    public GamepadOutputCapabilities Capabilities { get; }
    /// <inheritdoc />
    public InputDeviceId DeviceId { get; }

    /// <summary>Marks the handle dead after the device disconnects; further requests are rejected.</summary>
    public void Kill() {
        m_alive = false;
    }

    private bool TryEnqueue(GamepadOutputCapabilities required, in GamepadOutputCommand command) {
        if (!m_alive || !Capabilities.HasFlag(flag: required)) {
            return false;
        }

        m_queue.Enqueue(item: command);

        return true;
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
                TriggerRumble: default,
                TriggerEffectLeft: left,
                TriggerEffectRight: right,
                ScheduleTick: fireAtTick
            ),
            required: GamepadOutputCapabilities.TriggerEffect
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
}
