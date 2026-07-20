using System.Collections.Concurrent;
using System.Diagnostics;
using Puck.Commands;
using Puck.Input.Hid;
using Puck.Input.Output;

namespace Puck.Input.Devices;

/// <summary>
/// One connected controller: its HID transport, its <see cref="IGamepadParser"/>, the coalescer the frame
/// thread drains, and the output queue its single I/O loop services. The loop owns the native handle, so all
/// reads and writes for a device are serialized on it; input flows handle → coalescer, output flows queue →
/// handle.
/// </summary>
internal sealed class GamepadDevice : IGamepadConnection {
    private const int ReportBufferSize = 64;            // fallback when the device reports no input length
    private const int StartupPollMilliseconds = 100;    // bound reads before streaming starts so the watchdog can fire

    private static readonly long StreamingDeadlineTicks = (5L * Stopwatch.Frequency);  // fault if never streaming
    private readonly bool m_activateOnStream;
    private readonly GamepadCoalescer m_coalescer = new();
    private readonly CancellationTokenSource m_cancellation = new();
    private readonly IInputClock m_clock;
    private readonly Action<string>? m_diagnostics;
    private readonly IHidDevice m_hid;
    private readonly GamepadOutput m_output;
    private readonly ConcurrentQueue<GamepadOutputCommand> m_outputQueue = new();
    private readonly IGamepadParser m_parser;
    private readonly byte[] m_readBuffer;
    private volatile bool m_faulted;
    private volatile bool m_hasStream;
    private bool m_rumbleActive;
    private long m_rumbleExpiry = long.MaxValue;
    private bool m_scheduledTriggerActive;
    private ulong m_scheduledTriggerFireTick;
    private TriggerEffectSpec m_scheduledTriggerLeft;
    private TriggerEffectSpec m_scheduledTriggerRight;
    private ulong m_sequence;
    private Task? m_loop;

    public GamepadDevice(
        IHidDevice hid,
        IGamepadParser parser,
        InputDeviceId deviceId,
        int playerIndex,
        IInputClock clock,
        Action<string>? diagnostics = null,
        bool activateOnStream = false
    ) {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hid);
        ArgumentNullException.ThrowIfNull(parser);

        var inputLength = hid.InputReportByteLength;

        DeviceId = deviceId;
        PlayerIndex = playerIndex;
        m_activateOnStream = activateOnStream;
        m_clock = clock;
        m_diagnostics = diagnostics;
        m_hid = hid;
        m_output = new GamepadOutput(
            capabilities: CapabilitiesFor(parser: parser),
            deviceId: deviceId,
            queue: m_outputQueue
        );
        m_parser = parser;
        // Size the read buffer from the device's declared input report length (Bluetooth reports exceed 64);
        // over-sizing is harmless since ReadAsync returns the actual byte count.
        m_readBuffer = new byte[((inputLength > 0) ? inputLength : ReportBufferSize)];
    }

    public GamepadCoalescer Coalescer => m_coalescer;
    public InputDeviceId DeviceId { get; }
    /// <summary>The device interface path, used to match against enumeration so a connected device isn't reopened.</summary>
    public string Key => m_hid.DevicePath;
    /// <summary>Whether the device's I/O loop has stopped due to a disconnect or I/O error (eligible for pruning).</summary>
    public bool IsFaulted => m_faulted;
    /// <summary>
    /// The zero-based player slot assigned to this device (drives its indicator LEDs), or <c>-1</c> while a
    /// deferred-activation device (an idle wireless-receiver slot) is dormant.
    /// </summary>
    public int PlayerIndex { get; private set; }
    /// <summary>
    /// Whether this device claims its player slot lazily — on first streamed state — rather than at open: a
    /// wireless-receiver slot may sit empty indefinitely, so it parks dormant (watchdog-exempt, invisible to
    /// consumers) instead of holding a player identity nothing is behind.
    /// </summary>
    internal bool ActivateOnStream => m_activateOnStream;
    /// <summary>
    /// Whether the device is currently streaming parsed state. Set on the I/O thread by the first parsed report;
    /// cleared when the receiver announces the controller left the slot. The manager reconciles deferred devices'
    /// player slots against this.
    /// </summary>
    internal bool HasStream => m_hasStream;

    public IGamepadOutput Output => m_output;
    public GamepadType Type => m_parser.Type;
    /// <summary>The optional input features this device provides, from its parser.</summary>
    public GamepadInputCapabilities InputCapabilities => m_parser.InputCapabilities;

    private static GamepadOutputCapabilities CapabilitiesFor(IGamepadParser parser) {
        // Derive capabilities from what the parser actually implements, so every advertised feature has a real
        // write path (no capability lies). The raw report channel exists for every HID parser — the escape hatch
        // for adaptive triggers, HD-rumble waveforms, and other device-specific effects.
        var capabilities = GamepadOutputCapabilities.RawEffect;

        if (parser is IRumbleParser) {
            capabilities |= GamepadOutputCapabilities.Rumble;
        }

        if (parser is ILedParser) {
            capabilities |= GamepadOutputCapabilities.Led;
        }

        if (parser is ITriggerEffectParser) {
            capabilities |= GamepadOutputCapabilities.TriggerEffect;
        }

        return capabilities;
    }

    /// <summary>Assigns the player slot (manager-owned, called under its gate) when a deferred-activation device
    /// begins streaming.</summary>
    /// <param name="playerIndex">The zero-based player slot to assign.</param>
    internal void AssignPlayerSlot(int playerIndex) {
        PlayerIndex = playerIndex;
    }
    /// <summary>Releases the player slot (manager-owned, called under its gate) when the controller leaves its
    /// receiver slot, returning the device to dormancy.</summary>
    internal void ReleasePlayerSlot() {
        PlayerIndex = -1;
    }

    /// <summary>Starts the device's I/O loop on a background task.</summary>
    public void Start() {
        m_loop = Task.Run(function: () => RunAsync(cancellationToken: m_cancellation.Token));
    }

    // A dormant receiver slot has no player identity yet, and the lifecycle diagnostics should say so instead of
    // printing a bogus "player 0".
    private string SlotLabel() {
        var playerIndex = PlayerIndex;

        return ((playerIndex >= 0) ? $"player {(playerIndex + 1)}" : "dormant receiver slot");
    }
    private async Task RunAsync(CancellationToken cancellationToken) {
        var firstParsed = false;
        var loggedUnparsed = false;

        try {
            m_diagnostics?.Invoke($"[gamepad] {Type} init starting ({SlotLabel()})");

            await m_parser.InitializeAsync(playerIndex: PlayerIndex, cancellationToken: cancellationToken);

            m_diagnostics?.Invoke($"[gamepad] {Type} init complete; awaiting reports");

            var buffer = m_readBuffer;
            var streamingDeadline = (Stopwatch.GetTimestamp() + StreamingDeadlineTicks);

            while (!cancellationToken.IsCancellationRequested) {
                await DrainOutputAsync(cancellationToken: cancellationToken);

                // Steady state is an unbounded awaited read: it parks in the driver until a report arrives
                // (lowest latency, zero per-read allocation). It is bounded only (a) before the first parseable
                // report, so the liveness watchdog below can fault a controller that streams nothing, and (b)
                // while a finite rumble is pending, so its expiry is serviced even if the report stream pauses.
                int read;

                if (!firstParsed) {
                    read = await m_hid.ReadAsync(
                        buffer: buffer,
                        cancellationToken: cancellationToken,
                        timeoutInMilliseconds: StartupPollMilliseconds
                    );
                } else if (m_rumbleActive && (m_rumbleExpiry != long.MaxValue)) {
                    read = await m_hid.ReadAsync(
                        buffer: buffer,
                        cancellationToken: cancellationToken,
                        timeoutInMilliseconds: RemainingMilliseconds(expiryTimestamp: m_rumbleExpiry)
                    );
                } else {
                    read = await m_hid.ReadAsync(
                        buffer: buffer,
                        cancellationToken: cancellationToken
                    );
                }

                if (0 < read) {
                    bool parsed;
                    GamepadState state;

                    try {
                        parsed = m_parser.TryParse(report: buffer.AsSpan(start: 0, length: read), state: out state);
                    } catch (Exception parseException) {
                        // A parser must never throw on hostile bytes — when one does, the report payload IS the
                        // bug report. Dump it before the fault machinery eats the evidence.
                        m_diagnostics?.Invoke($"[gamepad] {Type} parser THREW on report id=0x{buffer[0]:x2} len={read} head={Convert.ToHexString(buffer, 0, Math.Min(val1: read, val2: 24))}: {parseException}");
                        throw;
                    }

                    if (parsed) {
                        // Stamp the report's arrival on the I/O thread — the earliest accurate point — and a
                        // per-device sequence, so the coalescer can carry true sub-frame edge times forward and a
                        // drain can order what it folded. The parser stays pure; timing is layered on here.
                        state = state with {
                            ArrivalTicks = m_clock.NowTicks,
                            SequenceNumber = ++m_sequence,
                        };
                        m_coalescer.Update(state: in state);

                        if (!m_hasStream) {
                            m_hasStream = true;
                        }

                        if (!firstParsed) {
                            firstParsed = true;
                            m_diagnostics?.Invoke($"[gamepad] {Type} streaming (first parsed report, len={read})");
                        }
                    } else {
                        var slotEvent = ((m_parser is IWirelessSlotParser slotParser)
                            ? slotParser.ClassifySlotEvent(report: buffer.AsSpan(start: 0, length: read))
                            : WirelessSlotEvent.None);

                        if (WirelessSlotEvent.Connected == slotEvent) {
                            // A controller just paired into this slot. The open-time initialization addressed an
                            // empty slot, so run it again for the pad that's actually here; its first state report
                            // then claims a player slot (via HasStream, reconciled by the manager).
                            m_diagnostics?.Invoke($"[gamepad] {Type} receiver slot paired; initializing the controller");

                            await m_parser.InitializeAsync(playerIndex: PlayerIndex, cancellationToken: cancellationToken);
                        } else if (WirelessSlotEvent.Disconnected == slotEvent) {
                            // The controller left the slot: back to dormant. The manager releases the player slot
                            // when it sees HasStream drop; resetting firstParsed returns the loop to bounded reads
                            // (and the refreshed deadline keeps a non-deferred device's watchdog honest).
                            m_hasStream = false;
                            firstParsed = false;
                            streamingDeadline = (Stopwatch.GetTimestamp() + StreamingDeadlineTicks);
                            m_diagnostics?.Invoke($"[gamepad] {Type} receiver slot unpaired; parking");
                        } else if (!loggedUnparsed) {
                            // Data is arriving but not in the expected report shape — e.g. the report-mode
                            // subcommand didn't take and the controller is still sending its simple (0x3F) report.
                            loggedUnparsed = true;
                            m_diagnostics?.Invoke($"[gamepad] {Type} unparsed report id=0x{buffer[0]:x2} len={read}");
                        }
                    }
                }

                // A deferred-activation device is a receiver slot that may legitimately sit empty forever, so
                // stream silence is not a fault for it; everything else must prove liveness within the deadline.
                if (!firstParsed && !m_activateOnStream && (Stopwatch.GetTimestamp() > streamingDeadline)) {
                    // Initialized but never produced a parseable report (a stuck handshake, or a controller still
                    // on its simple 0x3F report). Fault so the manager prunes it and the rescan reopens and
                    // re-initializes it, instead of leaving a zombie that holds a player slot forever.
                    throw new TimeoutException(message: $"{Type} did not begin streaming within {(StreamingDeadlineTicks / Stopwatch.Frequency)}s of init");
                }
            }
        } catch (OperationCanceledException) {
            // Expected on disposal.
        } catch (Exception exception) when (!cancellationToken.IsCancellationRequested) {
            // The device disconnected, errored mid-I/O (e.g. ERROR_DEVICE_NOT_CONNECTED), or never streamed.
            // Mark it faulted so the manager prunes it instead of leaving a zombie that replays stale state.
            m_faulted = true;
            m_output.Kill();
            // A timeout is the routine empty-slot/asleep-pad case — keep it terse. Anything else is unexpected,
            // and an unexpected exception without its stack is a debugging session nobody can start.
            m_diagnostics?.Invoke(((exception is TimeoutException)
                ? $"[gamepad] {Type} read error: {exception.GetType().Name}: {exception.Message}"
                : $"[gamepad] {Type} read error: {exception}"));
        }
    }
    private static int RemainingMilliseconds(long expiryTimestamp) {
        var remaining = (expiryTimestamp - Stopwatch.GetTimestamp());

        if (remaining <= 0L) {
            return 1;
        }

        var milliseconds = ((remaining * 1000L) / Stopwatch.Frequency);

        return ((milliseconds < 1L) ? 1 : ((milliseconds > 1000L) ? 1000 : ((int)milliseconds)));
    }
    private async ValueTask DrainOutputAsync(CancellationToken cancellationToken) {
        while (m_outputQueue.TryDequeue(result: out var command)) {
            switch (command.Kind) {
                case GamepadOutputKind.Rumble:
                    await ApplyRumbleAsync(effect: command.Rumble, cancellationToken: cancellationToken);

                    break;
                case GamepadOutputKind.Led when (m_parser is ILedParser led):
                    await led.SetLedAsync(color: command.Led, cancellationToken: cancellationToken);

                    break;
                case GamepadOutputKind.TriggerEffect when (m_parser is ITriggerEffectParser triggerEffect):
                    // Apply immediately when unscheduled or its fire tick has already passed; otherwise hold it
                    // until the capture clock reaches that tick (serviced below). A new effect supersedes any
                    // still-pending schedule.
                    if ((command.ScheduleTick == 0UL) || (m_clock.NowTicks >= command.ScheduleTick)) {
                        m_scheduledTriggerActive = false;

                        await triggerEffect.SetTriggerEffectAsync(left: command.TriggerEffectLeft, right: command.TriggerEffectRight, cancellationToken: cancellationToken);
                    } else {
                        m_scheduledTriggerActive = true;
                        m_scheduledTriggerFireTick = command.ScheduleTick;
                        m_scheduledTriggerLeft = command.TriggerEffectLeft;
                        m_scheduledTriggerRight = command.TriggerEffectRight;
                    }

                    break;
                case GamepadOutputKind.Raw when (command.Raw is { } raw):
                    await m_hid.WriteAsync(buffer: raw, cancellationToken: cancellationToken);

                    break;
                default:
                    // TriggerRumble is Xbox-only (serviced by the XInput connection, not here); any other kind is
                    // unreachable given this device's advertised capabilities. Ignore defensively.
                    break;
            }
        }

        // Honor a finite rumble duration by returning the motors to rest once it elapses.
        if (m_rumbleActive && (Stopwatch.GetTimestamp() >= m_rumbleExpiry)) {
            await ApplyRumbleAsync(effect: RumbleEffect.Off, cancellationToken: cancellationToken);
        }

        // Fire a scheduled trigger effect once the capture clock reaches its tick. This runs each loop iteration
        // (≈ once per arriving report, so sub-frame on a streaming pad); the device clock is the timing authority.
        if (m_scheduledTriggerActive && (m_clock.NowTicks >= m_scheduledTriggerFireTick) && (m_parser is ITriggerEffectParser scheduledParser)) {
            m_scheduledTriggerActive = false;

            await scheduledParser.SetTriggerEffectAsync(left: m_scheduledTriggerLeft, right: m_scheduledTriggerRight, cancellationToken: cancellationToken);
        }
    }
    private async ValueTask ApplyRumbleAsync(RumbleEffect effect, CancellationToken cancellationToken) {
        if (m_parser is not IRumbleParser rumbleParser) {
            return;
        }

        await rumbleParser.SetRumbleAsync(
            cancellationToken: cancellationToken,
            highFrequency: effect.HighFrequency,
            lowFrequency: effect.LowFrequency
        );

        if ((0f >= effect.HighFrequency) && (0f >= effect.LowFrequency)) {
            m_rumbleActive = false;
            m_rumbleExpiry = long.MaxValue;
        } else {
            m_rumbleActive = true;
            m_rumbleExpiry = ((0u < effect.DurationMilliseconds)
                ? (Stopwatch.GetTimestamp() + ((long)(effect.DurationMilliseconds * (Stopwatch.Frequency / 1000.0))))
                : long.MaxValue);
        }
    }

    public void Dispose() {
        m_output.Kill();
        m_cancellation.Cancel();

        try {
            m_loop?.Wait(timeout: TimeSpan.FromMilliseconds(value: 250));
        } catch (AggregateException) {
            // The loop faulted or was canceled during teardown; nothing actionable here.
        }

        m_cancellation.Dispose();

        // Give a parser that holds device state a chance to restore it (e.g. a controller that was switched out of
        // its built-in keyboard/mouse emulation on open) while the handle is still open, before the transport is
        // torn down. The I/O loop has already stopped, so this cannot race a concurrent read/write.
        (m_parser as IDisposable)?.Dispose();

        m_hid.Dispose();
    }
}
