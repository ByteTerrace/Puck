using System.Diagnostics;
using Puck.Commands;
using Puck.Input.Devices;

namespace Puck.Platform.Windows.Gamepad;

/// <summary>
/// The Windows Xbox acquisition backend. A single shared poll thread services all four XInput user slots; each
/// connected slot becomes an <see cref="XInputGamepadConnection"/> registered with the platform-neutral
/// <c>GamepadManager</c>, so it flows through the same coalescer and command pipeline as the HID devices. Rumble
/// reaches the impulse triggers through GameInput once a slot is bound to its GameInput device. The poll thread is
/// the sole owner of the <see cref="GameInputHaptics"/> service.
/// </summary>
/// <remarks>
/// A slot's family comes from its USB identity (the ordinal-108 capabilities). Steam Input's virtual XInput pad
/// is skipped: it mirrors a physical controller this engine already reads directly. Battery is read about every
/// two seconds, since <c>XInputGetBatteryInformation</c> costs a driver round trip; the headset report rides the
/// same poll. A cycle sweeps every slot first and only then services output, so the sole-connection decision never
/// rests on a half-discovered topology, and it waits out a settle window after the last slot or GameInput device
/// change before it lets a lone slot bind a lone device.
/// </remarks>
public sealed class Win32XboxAcquisitionSource : IGamepadAcquisitionSource {
    private const int BatteryPollSeconds = 2;
    private const int DisposeJoinMilliseconds = 2000;   // covers GameInput's one-second callback unregistration
    private const ushort SteamVirtualProductId = 0x11FF;
    private const ushort SteamVirtualVendorId = 0x28DE;
    private const int XInputPollHz = 250;
    private const int XInputSlotCount = 4;

    private readonly CancellationTokenSource m_cancellation = new();
    private readonly IInputClock m_clock;
    private readonly Action<string>? m_diagnostics;

    private GameInputHaptics? m_gameInputHaptics;
    private IGamepadConnectionRegistry? m_registry;
    private int m_disposed;
    private int m_started;
    private Thread? m_thread;

    /// <summary>Initializes a new instance of the <see cref="Win32XboxAcquisitionSource"/> class.</summary>
    /// <param name="clock">The capture clock that stamps each polled state's arrival.</param>
    /// <param name="diagnostics">The optional sink for lifecycle and diagnostic messages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is <see langword="null"/>.</exception>
    public Win32XboxAcquisitionSource(IInputClock clock, Action<string>? diagnostics = null) {
        ArgumentNullException.ThrowIfNull(clock);

        m_clock = clock;
        m_diagnostics = diagnostics;
    }

    private void CloseSlot(XInputGamepadConnection connection) {
        m_registry!.Unregister(connection: connection);
        connection.Dispose();
    }
    // Opens a slot unless it is Steam Input's virtual pad, returning null for a skipped slot.
    private XInputGamepadConnection? OpenSlot(uint slot) {
        var type = GamepadType.XboxOne;

        if (XInput.ErrorSuccess == XInput.GetCapabilitiesEx(
            capabilities: out var capabilities,
            flags: 0u,
            one: 1u,
            userIndex: slot
        )) {
            if (
                (SteamVirtualVendorId == capabilities.VendorId) &&
                (SteamVirtualProductId == capabilities.ProductId)
            ) {
                return null;
            }

            type = XInputGamepadConnection.ClassifyType(
                productId: capabilities.ProductId,
                vendorId: capabilities.VendorId
            );
        }

        return ((XInputGamepadConnection)m_registry!.Register(connectionFactory: playerIndex => new XInputGamepadConnection(
            clock: m_clock,
            deviceId: InputDeviceId.FromConnectionKey(key: $"xinput:{slot}"),
            haptics: m_gameInputHaptics,
            playerIndex: playerIndex,
            slot: slot,
            type: type
        )));
    }
    private void RunXInputLoop(CancellationToken cancellationToken) {
        try {
            _ = XInput.GetStateEx(
                state: out _,
                userIndex: 0u
            );
        } catch (DllNotFoundException) {
            m_diagnostics?.Invoke("[gamepad] XInput unavailable (xinput1_4.dll not found); Xbox controllers disabled");

            return;
        } catch (EntryPointNotFoundException) {
            m_diagnostics?.Invoke("[gamepad] XInput unavailable (entry point missing); Xbox controllers disabled");

            return;
        }

        var haptics = new GameInputHaptics(diagnostics: m_diagnostics);

        if (haptics.TryInitialize()) {
            m_gameInputHaptics = haptics;
            m_diagnostics?.Invoke("[gamepad] GameInput haptics ready (Xbox impulse triggers)");
        } else {
            m_diagnostics?.Invoke("[gamepad] GameInput unavailable; Xbox rumble via XInput (no impulse triggers)");
        }

        var connections = new XInputGamepadConnection?[XInputSlotCount];
        var skipped = new bool[XInputSlotCount];
        var emptySlotRecheck = new long[XInputSlotCount];
        var pollPeriod = TimeSpan.FromSeconds(value: (1.0 / XInputPollHz));
        var pollPeriodTicks = (Stopwatch.Frequency / XInputPollHz);
        var recheckPeriodTicks = Stopwatch.Frequency;   // empty slots are re-probed about once a second
        var batteryPeriodTicks = (BatteryPollSeconds * Stopwatch.Frequency);
        var settleTicks = ((long)(GameInputHaptics.SoleSettleMilliseconds * (Stopwatch.Frequency / 1000.0)));
        var lastSlotChange = Stopwatch.GetTimestamp();  // the last time a slot opened or closed
        var nextBatteryPoll = 0L;
        using var timer = Win32HighResolutionWaitableTimer.TryCreate();

        try {
            while (!cancellationToken.IsCancellationRequested) {
                var cycleStart = Stopwatch.GetTimestamp();
                var pollBattery = (cycleStart >= nextBatteryPoll);

                if (pollBattery) {
                    nextBatteryPoll = (cycleStart + batteryPeriodTicks);
                }

                // Sweep every slot first: open, close, and feed. Output is serviced only after the sweep, once the
                // cycle knows how many slots are connected.
                for (var slot = 0; (slot < XInputSlotCount); ++slot) {
                    var connection = connections[slot];

                    if (
                        (connection is null) &&
                        (cycleStart < emptySlotRecheck[slot])
                    ) {
                        continue;
                    }

                    var status = XInput.GetStateEx(
                        state: out var state,
                        userIndex: ((uint)slot)
                    );

                    // A native call can outlast a dispose; never touch a connection or the registry after one.
                    if (cancellationToken.IsCancellationRequested) {
                        return;
                    }

                    if (status != XInput.ErrorSuccess) {
                        if (connection is not null) {
                            CloseSlot(connection: connection);
                            connections[slot] = null;
                            lastSlotChange = cycleStart;
                        }

                        skipped[slot] = false;
                        emptySlotRecheck[slot] = (cycleStart + recheckPeriodTicks);

                        continue;
                    }

                    if (
                        (connection is null) ||
                        connection.IsFaulted
                    ) {
                        if (skipped[slot]) {
                            emptySlotRecheck[slot] = (cycleStart + recheckPeriodTicks);

                            continue;
                        }

                        // Remove the prior (faulted) connection before re-adding, so the manager never briefly holds
                        // two entries with the same id.
                        if (connection is not null) {
                            CloseSlot(connection: connection);
                        }

                        connection = OpenSlot(slot: ((uint)slot));
                        connections[slot] = connection;
                        lastSlotChange = cycleStart;

                        if (connection is null) {
                            skipped[slot] = true;
                            m_diagnostics?.Invoke($"[gamepad] XInput slot {slot} is Steam Input's virtual pad; skipped");

                            continue;
                        }

                        pollBattery = true;
                    }

                    if (pollBattery) {
                        if (XInput.ErrorSuccess == XInput.GetBatteryInformation(
                            deviceType: XInput.DeviceTypeGamepad,
                            information: out var battery,
                            userIndex: ((uint)slot)
                        )) {
                            connection.ApplyBattery(information: in battery);
                        }

                        if (XInput.ErrorSuccess == XInput.GetBatteryInformation(
                            deviceType: XInput.DeviceTypeHeadset,
                            information: out var headset,
                            userIndex: ((uint)slot)
                        )) {
                            connection.ApplyHeadset(information: in headset);
                        }

                        if (cancellationToken.IsCancellationRequested) {
                            return;
                        }
                    }

                    connection.Apply(state: in state);
                }

                var connected = 0;

                foreach (var connection in connections) {
                    connected += ((connection is null)
                        ? 0
                        : 1
                    );
                }

                // One slot with one device is only a sole pair once the topology has held still; until then a
                // partially discovered second pad (or its not-yet-reported GameInput device) could be mistaken
                // for absence.
                var soleConnection = (
                    (1 == connected) &&
                    ((cycleStart - lastSlotChange) >= settleTicks)
                );

                foreach (var connection in connections) {
                    connection?.ServiceOutput(soleConnection: soleConnection);
                }

                var remaining = ((cycleStart + pollPeriodTicks) - Stopwatch.GetTimestamp());

                if (remaining > 0L) {
                    var due = TimeSpan.FromSeconds(value: (remaining / ((double)Stopwatch.Frequency)));

                    if (timer is not null) {
                        _ = timer.WaitOne(
                            cancellationWaitHandle: cancellationToken.WaitHandle,
                            dueTime: due
                        );
                    } else {
                        _ = cancellationToken.WaitHandle.WaitOne(timeout: pollPeriod);
                    }
                }
            }
        } catch (ObjectDisposedException) {
            // The manager (or this source) was disposed while the loop ran; there is nobody left to feed.
            m_diagnostics?.Invoke("[gamepad] XInput poll loop stopped: the connection registry was disposed");
        } catch (Exception exception) {
            // This runs on a dedicated thread, where an unhandled exception would terminate the process. Fail
            // soft: drop Xbox support for this session rather than take the app down.
            m_diagnostics?.Invoke($"[gamepad] XInput poll loop stopped: {exception.GetType().Name}: {exception.Message}");
        } finally {
            // The poll thread solely owns the haptics service. Connections still registered are disposed by the
            // manager; their borrowed device handles survive haptics disposal (handles are never released).
            m_gameInputHaptics?.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        if (0 != Interlocked.Exchange(
            location1: ref m_disposed,
            value: 1
        )) {
            return;
        }

        m_cancellation.Cancel();

        // Join the poll thread, whose finally disposes the GameInput haptics it solely owns. A thread that does not
        // stop in time may still be inside a native call and read the token afterwards, so its cancellation source is
        // left to finalization rather than disposed under it.
        if (m_thread?.Join(millisecondsTimeout: DisposeJoinMilliseconds) == false) {
            m_diagnostics?.Invoke("[gamepad] XInput poll thread did not stop within its teardown bound; continuing shutdown");

            return;
        }

        m_cancellation.Dispose();
    }
    /// <inheritdoc />
    public void Start(IGamepadConnectionRegistry registry) {
        ArgumentNullException.ThrowIfNull(registry);

        if (0 != Interlocked.CompareExchange(
            comparand: 0,
            location1: ref m_started,
            value: 1
        )) {
            return;
        }

        // XInput 1.4 ships with Windows 8 and later.
        if (!OperatingSystem.IsWindowsVersionAtLeast(
            major: 6,
            minor: 2,
            build: 0
        )) {
            return;
        }

        m_registry = registry;
        m_thread = new Thread(start: () => RunXInputLoop(cancellationToken: m_cancellation.Token)) {
            IsBackground = true,
            Name = "Puck.Input XInput Poll",
        };
        m_thread.Start();
    }
}
