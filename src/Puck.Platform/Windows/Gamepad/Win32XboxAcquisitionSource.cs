using System.Diagnostics;
using Puck.Commands;
using Puck.Input.Devices;

namespace Puck.Platform.Windows.Gamepad;

/// <summary>
/// The Windows Xbox acquisition backend. A single shared poll thread services all four XInput user slots; each
/// connected slot becomes an <see cref="XInputGamepadConnection"/> registered with the platform-neutral
/// <c>GamepadManager</c>, so it flows through the same coalescer → command pipeline as the HID devices. Rumble is
/// driven through GameInput (which reaches wireless transports and the impulse-trigger motors) with an
/// <c>XInputSetState</c> fallback. The poll thread is the sole owner of the <see cref="GameInputHaptics"/> service.
/// </summary>
public sealed class Win32XboxAcquisitionSource : IGamepadAcquisitionSource {
    private const int XInputSlotCount = 4;
    private const int XInputPollHz = 250;

    private readonly CancellationTokenSource m_cancellation = new();
    private readonly Action<string>? m_diagnostics;
    private GameInputHaptics? m_gameInputHaptics;
    private IGamepadConnectionRegistry? m_registry;
    private int m_started;
    private Thread? m_thread;

    /// <summary>Initializes a new instance of the <see cref="Win32XboxAcquisitionSource"/> class.</summary>
    /// <param name="diagnostics">An optional sink for human-readable lifecycle/diagnostic messages.</param>
    public Win32XboxAcquisitionSource(Action<string>? diagnostics = null) {
        m_diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public void Start(IGamepadConnectionRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);

        // Idempotent: only the first Start spins the poll thread.
        if (uint.MinValue != Interlocked.CompareExchange(location1: ref m_started, value: 1, comparand: 0)) {
            return;
        }

        // XInput ships on Windows 8+; do nothing (no Xbox support) elsewhere.
        if (!OperatingSystem.IsWindowsVersionAtLeast(major: 6, minor: 2, build: 0)) {
            return;
        }

        m_registry = registry;
        m_thread = new Thread(start: () => RunXInputLoop(cancellationToken: m_cancellation.Token)) {
            IsBackground = true,
            Name = "Puck.Input XInput Poll",
        };
        m_thread.Start();
    }

    private void RunXInputLoop(CancellationToken cancellationToken) {
        try {
            _ = XInput.GetStateEx(userIndex: 0u, state: out _);
        } catch (DllNotFoundException) {
            m_diagnostics?.Invoke("[gamepad] XInput unavailable (xinput1_4.dll not found); Xbox controllers disabled");

            return;
        } catch (EntryPointNotFoundException) {
            m_diagnostics?.Invoke("[gamepad] XInput unavailable (entry point missing); Xbox controllers disabled");

            return;
        }

        // GameInput drives rumble that actuates over wireless (the Adapter/Bluetooth) and the trigger motors,
        // where legacy XInputSetState is silently dropped. If it's unavailable we fall back to XInput (USB only).
        var haptics = new GameInputHaptics(diagnostics: m_diagnostics);

        if (haptics.TryInitialize()) {
            m_gameInputHaptics = haptics;
            m_diagnostics?.Invoke("[gamepad] GameInput haptics ready (Xbox rumble incl. wireless)");
        } else {
            m_diagnostics?.Invoke("[gamepad] GameInput unavailable; Xbox rumble via XInput (USB only)");
        }

        var connections = new XInputGamepadConnection?[XInputSlotCount];
        var emptySlotRecheck = new long[XInputSlotCount];
        var pollPeriodTicks = (Stopwatch.Frequency / XInputPollHz);
        var recheckPeriodTicks = Stopwatch.Frequency;   // empty slots re-probed ~1/s (XInput stalls on empty polls)

        // Raise the OS timer resolution so the poll cadence is accurate; restored on exit.
        _ = XInput.TimeBeginPeriod(period: 1u);

        try {
            while (!cancellationToken.IsCancellationRequested) {
                var cycleStart = Stopwatch.GetTimestamp();

                for (var slot = 0; slot < XInputSlotCount; ++slot) {
                    var connection = connections[slot];

                    if ((connection is null) && (cycleStart < emptySlotRecheck[slot])) {
                        continue;
                    }

                    var status = XInput.GetStateEx(userIndex: ((uint)slot), state: out var state);

                    if (status == XInput.ErrorSuccess) {
                        if ((connection is null) || connection.IsFaulted) {
                            // Remove the prior (faulted) connection before re-adding, so the manager never briefly
                            // holds two entries with the same id (which would let TryGetOutput pick the dead one).
                            if (connection is not null) {
                                CloseSlot(connection: connection);
                            }

                            connection = OpenSlot(slot: ((uint)slot));
                            connections[slot] = connection;
                        }

                        connection.Apply(pad: in state.Gamepad);

                        // The connection owns its own output actuation (GameInput correlation + dual-path rumble).
                        connection.ServiceOutput();
                    } else {
                        if (connection is not null) {
                            CloseSlot(connection: connection);
                            connections[slot] = null;
                        }

                        emptySlotRecheck[slot] = (cycleStart + recheckPeriodTicks);
                    }
                }

                PaceTo(deadline: (cycleStart + pollPeriodTicks), cancellationToken: cancellationToken);
            }
        } catch (Exception exception) {
            // This runs on a dedicated thread, where an unhandled exception would terminate the process. Fail
            // soft: drop Xbox support for this session rather than take the app down.
            m_diagnostics?.Invoke($"[gamepad] XInput poll loop stopped: {exception.GetType().Name}: {exception.Message}");
        } finally {
            _ = XInput.TimeEndPeriod(period: 1u);

            // The poll thread is the sole owner of the haptics service, so dispose it here, keeping ownership off
            // any other thread. The connections still registered with the manager are disposed by the manager on
            // shutdown; their borrowed device handles survive haptics disposal (handles are never released).
            m_gameInputHaptics?.Dispose();
        }
    }

    // Atomically allocates a player slot and registers a new connection for it through the manager.
    private XInputGamepadConnection OpenSlot(uint slot) {
        return ((XInputGamepadConnection)m_registry!.Register(connectionFactory: playerIndex => new XInputGamepadConnection(
            deviceId: InputDeviceId.FromKey(key: $"xinput:{slot}"),
            haptics: m_gameInputHaptics,
            playerIndex: playerIndex,
            slot: slot
        )));
    }
    // Unregisters a connection from the manager and disposes it (silencing motors and freeing its GameInput bind).
    private void CloseSlot(XInputGamepadConnection connection) {
        m_registry!.Unregister(connection: connection);
        connection.Dispose();
    }
    private static void PaceTo(long deadline, CancellationToken cancellationToken) {
        while (!cancellationToken.IsCancellationRequested) {
            var remaining = (deadline - Stopwatch.GetTimestamp());

            if (remaining <= 0L) {
                break;
            }

            var remainingMs = ((remaining * 1000L) / Stopwatch.Frequency);

            if (remainingMs > 1L) {
                Thread.Sleep(millisecondsTimeout: ((int)(remainingMs - 1L)));
            } else {
                Thread.SpinWait(iterations: 64);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        m_cancellation.Cancel();

        // Join the poll thread, whose finally disposes the GameInput haptics it solely owns.
        _ = (m_thread?.Join(millisecondsTimeout: 500));

        m_cancellation.Dispose();
    }
}
