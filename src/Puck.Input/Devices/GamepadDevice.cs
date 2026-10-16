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
public sealed class GamepadDevice : IGamepadConnection {
    private readonly GamepadCoalescer m_coalescer = new();
    private readonly CancellationTokenSource m_cancellation = new();
    private readonly Lock m_lifecycleGate = new();
    private readonly GamepadOutputQueue m_outputQueue = new();
    private long m_rumbleExpiry = long.MaxValue;
    private long m_rumbleLastWrite = long.MinValue;

    public const int DisposeJoinTimeoutMilliseconds = 250; // a wedged transport must not hang disposal
    public const int SteadyStatePollMilliseconds = 16;     // services output/schedules even when input reports pause

    private const int DisposeOutputMilliseconds = 100;  // the bound on the final neutral writes at disposal
    private const int MaximumOutputCommandsPerIteration = 32;
    private const int ReceiverSilenceTimeoutMilliseconds = 1000;
    private const int ReportBufferSize = 64;            // fallback when the device reports no input length
    private const int StartupPollMilliseconds = 100;    // bound reads before streaming starts so the watchdog can fire
    private const int StopRefreshCount = 3;             // a refreshed controller hears its stop this many more times
    private const int StreamingDeadlineSeconds = 5;     // fault if never streaming

    private readonly bool m_activateOnStream;
    private readonly IInputClock m_clock;
    private readonly Action<string>? m_diagnostics;
    private readonly IHidDevice m_hid;
    private readonly GamepadOutput m_output;
    private readonly IGamepadParser m_parser;
    private readonly byte[] m_readBuffer;
    private readonly long m_receiverSilenceTimeoutTicks;
    private readonly long m_rumbleRefreshTicks;
    private readonly long m_rumbleWriteIntervalTicks;
    private readonly long m_streamSilenceTicks;
    private readonly long m_streamingDeadlineTicks;
    private readonly TimeProvider m_time;

    private bool m_disposed;
    private volatile bool m_faulted;
    private volatile bool m_hasStream;
    private Task? m_loop;
    private bool m_rumbleActive;
    private bool m_rumblePending;
    private HdRumbleSide m_rumbleLeft;
    private HdRumbleSide m_rumbleRight;
    private HdRumbleSide m_rumbleSentLeft;
    private HdRumbleSide m_rumbleSentRight;
    private int m_rumbleStopRefreshes;
    private bool m_scheduledTriggerActive;
    private bool m_triggerEffectApplied;
    private ulong m_scheduledTriggerFireTick;
    private TriggerEffectSpec m_scheduledTriggerLeft;
    private TriggerEffectSpec m_scheduledTriggerRight;
    private ulong m_sequence;
    private bool m_started;

    /// <summary>Creates a device over an opened transport. The loop does not run until <see cref="Start"/>.</summary>
    /// <param name="hid">The opened transport; the device owns it and disposes it with itself.</param>
    /// <param name="parser">The family parser; the device owns it and disposes it once the loop has stopped.</param>
    /// <param name="deviceId">The device's stable identity.</param>
    /// <param name="playerIndex">The zero-based player slot, or <c>-1</c> for a deferred-activation device.</param>
    /// <param name="clock">The capture clock that stamps report arrival and times scheduled trigger effects.</param>
    /// <param name="diagnostics">The optional lifecycle diagnostic sink.</param>
    /// <param name="activateOnStream">Whether the device claims its slot on first streamed state.</param>
    /// <param name="receiverSilenceTimeoutMilliseconds">The parsed-stream silence, in milliseconds, after which a
    /// deferred-activation device parks its slot.</param>
    /// <param name="timeProvider">The clock for the device's own deadlines — receiver silence, rumble expiry, the
    /// streaming deadline, and the bounded join at disposal — or <see langword="null"/> for
    /// <see cref="TimeProvider.System"/>. Timed transport reads keep their own timeouts.</param>
    public GamepadDevice(
        IHidDevice hid,
        IGamepadParser parser,
        InputDeviceId deviceId,
        int playerIndex,
        IInputClock clock,
        Action<string>? diagnostics = null,
        bool activateOnStream = false,
        int receiverSilenceTimeoutMilliseconds = ReceiverSilenceTimeoutMilliseconds,
        TimeProvider? timeProvider = null
    ) {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(hid);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: receiverSilenceTimeoutMilliseconds);

        var inputLength = hid.InputReportByteLength;
        var time = (timeProvider ?? TimeProvider.System);

        DeviceId = deviceId;
        PlayerIndex = playerIndex;
        m_activateOnStream = activateOnStream;
        m_clock = clock;
        m_diagnostics = diagnostics;
        m_hid = hid;
        m_output = new GamepadOutput(
            capabilities: CapabilitiesFor(parser: parser),
            trackpadHaptics: ((parser as ITrackpadHapticParser)?.SupportedTrackpadHaptics ?? TrackpadHapticKinds.None),
            deviceId: deviceId,
            queue: m_outputQueue,
            accepting: !activateOnStream
        );
        m_parser = parser;
        m_receiverSilenceTimeoutTicks = ((receiverSilenceTimeoutMilliseconds * time.TimestampFrequency) / 1000L);

        m_streamSilenceTicks = ((parser is IStreamingParser streaming)
            ? ToTicks(
                frequency: time.TimestampFrequency,
                interval: streaming.StreamSilenceTimeout
            )
            : 0L
        );

        if (parser is IRumbleParser rumbleParser) {
            m_rumbleRefreshTicks = ToTicks(
                frequency: time.TimestampFrequency,
                interval: rumbleParser.RumbleRefreshInterval
            );
            m_rumbleWriteIntervalTicks = ToTicks(
                frequency: time.TimestampFrequency,
                interval: rumbleParser.RumbleWriteInterval
            );
        }
        m_streamingDeadlineTicks = (StreamingDeadlineSeconds * time.TimestampFrequency);
        m_time = time;
        // Size the read buffer from the device's declared input report length (Bluetooth reports exceed 64);
        // over-sizing is harmless since ReadAsync returns the actual byte count.
        m_readBuffer = new byte[Math.Max(
            val1: inputLength,
            val2: ReportBufferSize
        )];
    }

    /// <summary>
    /// Whether this device claims its player slot lazily — on first streamed state — rather than at open: a
    /// wireless-receiver slot may sit empty indefinitely, so it parks dormant (watchdog-exempt, invisible to
    /// consumers) instead of holding a player identity nothing is behind.
    /// </summary>
    internal bool ActivateOnStream => m_activateOnStream;

    /// <summary>
    /// Whether the device is currently streaming parsed state. Set on the I/O thread by the first parsed report;
    /// cleared when the receiver announces the controller left the slot. The manager reconciles deferred devices'
    /// player slots against this. Either transition publishes the flag after the output and stream state it
    /// announces, so a reader that observes it finds output accepting (<see langword="true"/>) or suspended and
    /// reset (<see langword="false"/>).
    /// </summary>
    public bool HasStream => m_hasStream;
    public GamepadCoalescer Coalescer => m_coalescer;
    public InputDeviceId DeviceId { get; }
    /// <summary>The optional input features this device provides, from its parser.</summary>
    public GamepadInputCapabilities InputCapabilities => m_parser.InputCapabilities;
    /// <summary>Whether the device's I/O loop has stopped due to a disconnect or I/O error (eligible for pruning).</summary>
    public bool IsFaulted => m_faulted;
    /// <summary>The controller's physical identity once its parser has read it, or <see langword="null"/>; see
    /// <see cref="IPhysicalIdentityParser"/>.</summary>
    public string? PhysicalId => (m_parser as IPhysicalIdentityParser)?.PhysicalId;
    /// <summary>The transport the device is connected over.</summary>
    public HidTransport Transport => m_hid.Transport;
    /// <summary>The device interface path, used to match against enumeration so a connected device isn't reopened.</summary>
    public string Key => m_hid.DevicePath;
    public IGamepadOutput Output => m_output;
    /// <summary>
    /// The zero-based player slot assigned to this device (drives its indicator LEDs), or <c>-1</c> while a
    /// deferred-activation device (an idle wireless-receiver slot) is dormant.
    /// </summary>
    public int PlayerIndex { get; private set; }
    public GamepadType Type => m_parser.Type;

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

    private bool RumbleSent => ((0f < m_rumbleSentLeft.PeakAmplitude) || (0f < m_rumbleSentRight.PeakAmplitude));

    private static HdRumbleSide Normalize(HdRumbleSide side) => new(
        High: (side.High with { Amplitude = NormalizeIntensity(intensity: side.High.Amplitude), }),
        Low: (side.Low with { Amplitude = NormalizeIntensity(intensity: side.Low.Amplitude), })
    );
    // Records a requested rumble: an ordinary one is both actuators at their default carriers. The write itself
    // happens in ServiceRumbleAsync, which owns the controller's cadence.
    private void RequestRumble(HdRumbleSide left, HdRumbleSide right, uint durationMilliseconds) {
        if (durationMilliseconds == 0u) {
            m_rumbleLeft = HdRumbleSide.Rest;
            m_rumbleRight = HdRumbleSide.Rest;
        } else {
            m_rumbleLeft = Normalize(side: left);
            m_rumbleRight = Normalize(side: right);
        }

        m_rumblePending = true;
        m_rumbleActive = ((0f < m_rumbleLeft.PeakAmplitude) || (0f < m_rumbleRight.PeakAmplitude));
        m_rumbleExpiry = (m_rumbleActive
            ? (m_time.GetTimestamp() + ((long)(durationMilliseconds * (m_time.TimestampFrequency / 1000.0))))
            : long.MaxValue
        );
    }
    private static HdRumbleSide RumbleSide(in RumbleEffect effect) => new(
        High: new HdRumbleBand(Amplitude: effect.HighFrequency, FrequencyHertz: 0f),
        Low: new HdRumbleBand(Amplitude: effect.LowFrequency, FrequencyHertz: 0f)
    );
    // Leaves a pad that stays connected at rest when the engine releases it: motors stopped and adaptive
    // triggers released. Best-effort and bounded, on the disposing thread once the I/O loop has stopped.
    private void NeutralizeOutput() {
        if (!m_hasStream) {
            return;
        }

        using var timeout = new CancellationTokenSource(millisecondsDelay: DisposeOutputMilliseconds);

        try {
            if (
                (m_parser is IRumbleParser rumbleParser) &&
                (m_rumbleActive || RumbleSent)
            ) {
                _ = WriteRumbleAsync(
                    cancellationToken: timeout.Token,
                    left: HdRumbleSide.Rest,
                    parser: rumbleParser,
                    right: HdRumbleSide.Rest,
                    timestamp: m_time.GetTimestamp()
                ).AsTask().Wait(millisecondsTimeout: DisposeOutputMilliseconds);
            }

            if (
                (m_parser is ITriggerEffectParser triggerParser) &&
                m_triggerEffectApplied
            ) {
                _ = triggerParser.SetTriggerEffectAsync(
                    cancellationToken: timeout.Token,
                    left: TriggerEffectSpec.Off,
                    right: TriggerEffectSpec.Off
                ).AsTask().Wait(millisecondsTimeout: DisposeOutputMilliseconds);
            }
        } catch (Exception exception) when (exception is AggregateException or IOException or OperationCanceledException or ObjectDisposedException) {
            // The pad may already be gone; there is nothing left to stop.
        }
    }
    private static float NormalizeIntensity(float intensity) =>
        (float.IsFinite(f: intensity)
            ? Math.Clamp(
                max: 1f,
                min: 0f,
                value: intensity
            )
            : 0f
        );
    // The earliest instant rumble work falls due: a duration's expiry, a held update's write, or a refresh.
    private long NextRumbleDeadline() {
        var deadline = (m_rumbleActive
            ? m_rumbleExpiry
            : long.MaxValue
        );

        if (m_rumblePending) {
            deadline = Math.Min(
                val1: deadline,
                val2: (m_rumbleLastWrite + m_rumbleWriteIntervalTicks)
            );
        }

        if (
            (m_rumbleRefreshTicks > 0L) &&
            (RumbleSent || (m_rumbleStopRefreshes > 0))
        ) {
            deadline = Math.Min(
                val1: deadline,
                val2: (m_rumbleLastWrite + m_rumbleRefreshTicks)
            );
        }

        return deadline;
    }
    // Ends an elapsed duration, writes a requested change once the controller's write spacing allows (a stop or a
    // stronger request goes at once), and rewrites a running rumble on a controller that drops its motors without
    // fresh writes. A refreshed controller hears its stop a few more times, so a lost stop cannot leave it running.
    private async ValueTask ServiceRumbleAsync(CancellationToken cancellationToken) {
        if (m_parser is not IRumbleParser rumbleParser) {
            return;
        }

        var now = m_time.GetTimestamp();

        if (
            m_rumbleActive &&
            (now >= m_rumbleExpiry)
        ) {
            m_rumbleActive = false;
            m_rumbleExpiry = long.MaxValue;
            m_rumbleLeft = HdRumbleSide.Rest;
            m_rumbleRight = HdRumbleSide.Rest;
            m_rumblePending = true;
        }

        var sinceLastWrite = ((m_rumbleLastWrite == long.MinValue)
            ? long.MaxValue
            : (now - m_rumbleLastWrite)
        );

        if (m_rumblePending) {
            var stop = !m_rumbleActive;
            var stronger = (MathF.Max(
                x: m_rumbleLeft.PeakAmplitude,
                y: m_rumbleRight.PeakAmplitude
            ) > MathF.Max(
                x: m_rumbleSentLeft.PeakAmplitude,
                y: m_rumbleSentRight.PeakAmplitude
            ));

            if (
                !stop &&
                !stronger &&
                (sinceLastWrite < m_rumbleWriteIntervalTicks)
            ) {
                return;
            }

            m_rumblePending = false;
            m_rumbleStopRefreshes = (stop
                ? StopRefreshCount
                : 0
            );

            await WriteRumbleAsync(
                cancellationToken: cancellationToken,
                left: m_rumbleLeft,
                parser: rumbleParser,
                right: m_rumbleRight,
                timestamp: now
            );

            return;
        }

        if (
            (m_rumbleRefreshTicks <= 0L) ||
            (sinceLastWrite < m_rumbleRefreshTicks)
        ) {
            return;
        }

        if (RumbleSent) {
            await WriteRumbleAsync(
                cancellationToken: cancellationToken,
                left: m_rumbleSentLeft,
                parser: rumbleParser,
                right: m_rumbleSentRight,
                timestamp: now
            );
        } else if (m_rumbleStopRefreshes > 0) {
            --m_rumbleStopRefreshes;

            await WriteRumbleAsync(
                cancellationToken: cancellationToken,
                left: HdRumbleSide.Rest,
                parser: rumbleParser,
                right: HdRumbleSide.Rest,
                timestamp: now
            );
        }
    }
    private static long ToTicks(TimeSpan interval, long frequency) =>
        ((interval <= TimeSpan.Zero)
            ? 0L
            : ((long)(interval.TotalSeconds * frequency))
        );
    // An HD-rumble controller receives both actuators' bands; any other gets each band's larger amplitude as its
    // low and high motor intensity.
    private async ValueTask WriteRumbleAsync(IRumbleParser parser, HdRumbleSide left, HdRumbleSide right, long timestamp, CancellationToken cancellationToken) {
        if (parser is IHdRumbleParser hdParser) {
            await hdParser.SetHdRumbleAsync(
                cancellationToken: cancellationToken,
                left: left,
                right: right
            );
        } else {
            await parser.SetRumbleAsync(
                cancellationToken: cancellationToken,
                highFrequency: MathF.Max(
                    x: left.High.Amplitude,
                    y: right.High.Amplitude
                ),
                lowFrequency: MathF.Max(
                    x: left.Low.Amplitude,
                    y: right.Low.Amplitude
                )
            );
        }

        m_rumbleLastWrite = timestamp;
        m_rumbleSentLeft = left;
        m_rumbleSentRight = right;
    }
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

        if (parser is IHdRumbleParser) {
            capabilities |= GamepadOutputCapabilities.HdRumble;
        }

        if (parser is IHomeLedPatternParser) {
            capabilities |= GamepadOutputCapabilities.HomeLedPattern;
        }

        if (parser is ITrackpadHapticParser) {
            capabilities |= GamepadOutputCapabilities.TrackpadHaptics;
        }

        return capabilities;
    }
    private async ValueTask DrainOutputAsync(CancellationToken cancellationToken) {
        for (var count = 0; ((count < MaximumOutputCommandsPerIteration) && m_outputQueue.TryDequeue(command: out var command)); ++count) {
            switch (command.Kind) {
                case GamepadOutputKind.Rumble:
                    RequestRumble(
                        durationMilliseconds: command.Rumble.DurationMilliseconds,
                        left: RumbleSide(effect: command.Rumble),
                        right: RumbleSide(effect: command.Rumble)
                    );

                    break;
                case GamepadOutputKind.HdRumble:
                    RequestRumble(
                        durationMilliseconds: command.HdRumble.DurationMilliseconds,
                        left: command.HdRumble.Left,
                        right: command.HdRumble.Right
                    );

                    break;
                case GamepadOutputKind.HomeLedPattern when (m_parser is IHomeLedPatternParser pattern):
                    await pattern.SetHomeLedPatternAsync(
                        cancellationToken: cancellationToken,
                        pattern: command.HomeLedPattern
                    );

                    break;
                case GamepadOutputKind.TrackpadHaptic when (m_parser is ITrackpadHapticParser haptic):
                    await haptic.PlayTrackpadHapticAsync(
                        cancellationToken: cancellationToken,
                        effect: command.TrackpadHaptic
                    );

                    break;
                case GamepadOutputKind.Led when (m_parser is ILedParser led):
                    await led.SetLedAsync(
                        cancellationToken: cancellationToken,
                        color: command.Led
                    );

                    break;
                case GamepadOutputKind.TriggerEffect when (m_parser is ITriggerEffectParser triggerEffect):
                    // Apply immediately when unscheduled or its fire tick has already passed; otherwise hold it
                    // until the capture clock reaches that tick (serviced below). A new effect supersedes any
                    // still-pending schedule.
                    if (
                        (command.ScheduleTick == 0UL) ||
                        (m_clock.NowTicks >= command.ScheduleTick)
                    ) {
                        m_scheduledTriggerActive = false;
                        m_triggerEffectApplied = true;

                        await triggerEffect.SetTriggerEffectAsync(
                            cancellationToken: cancellationToken,
                            left: command.TriggerEffectLeft,
                            right: command.TriggerEffectRight
                        );
                    } else {
                        m_scheduledTriggerActive = true;
                        m_scheduledTriggerFireTick = command.ScheduleTick;
                        m_scheduledTriggerLeft = command.TriggerEffectLeft;
                        m_scheduledTriggerRight = command.TriggerEffectRight;
                    }

                    break;
                case GamepadOutputKind.Raw when (command.Raw is { } raw):
                    await m_hid.WriteAsync(
                        buffer: raw,
                        cancellationToken: cancellationToken
                    );

                    break;
                default:
                    // TriggerRumble is Xbox-only (serviced by the XInput connection, not here); any other kind is
                    // unreachable given this device's advertised capabilities. Ignore defensively.
                    break;
            }
        }

        await ServiceRumbleAsync(cancellationToken: cancellationToken);

        // Fire a scheduled trigger effect once the capture clock reaches its tick. The steady-state timed read
        // bounds service latency even when reports pause; the device clock is the timing authority.
        if (
            m_scheduledTriggerActive &&
            (m_clock.NowTicks >= m_scheduledTriggerFireTick) &&
            (m_parser is ITriggerEffectParser scheduledParser)
        ) {
            m_scheduledTriggerActive = false;
            m_triggerEffectApplied = true;

            await scheduledParser.SetTriggerEffectAsync(
                cancellationToken: cancellationToken,
                left: m_scheduledTriggerLeft,
                right: m_scheduledTriggerRight
            );
        }
    }
    private async ValueTask ParkReceiverSlotAsync(CancellationToken cancellationToken) {
        // The pad may still be reachable with its motors running on the last written speed, and the tracked
        // expiry is about to be discarded — return the motors to rest first. When the pad is truly gone the
        // write lands on the receiver and is harmless.
        if (
            (m_parser is IRumbleParser rumbleParser) &&
            (m_rumbleActive || RumbleSent)
        ) {
            await WriteRumbleAsync(
                cancellationToken: cancellationToken,
                left: HdRumbleSide.Rest,
                parser: rumbleParser,
                right: HdRumbleSide.Rest,
                timestamp: m_time.GetTimestamp()
            );
        }

        m_sequence = 0UL;
        m_rumbleActive = false;
        m_rumbleExpiry = long.MaxValue;
        m_rumbleLeft = HdRumbleSide.Rest;
        m_rumbleLastWrite = long.MinValue;
        m_rumbleRight = HdRumbleSide.Rest;
        m_rumblePending = false;
        m_rumbleSentLeft = HdRumbleSide.Rest;
        m_rumbleSentRight = HdRumbleSide.Rest;
        m_rumbleStopRefreshes = 0;
        m_scheduledTriggerActive = false;
        m_output.Suspend();
        m_coalescer.Reset();

        if (m_parser is IGamepadStreamReset resettable) {
            resettable.ResetStreamState();
        }

        // Publish the flag last, mirroring the claim: an observer that sees the stream gone finds output already
        // suspended and the coalescer and parser already reset.
        m_hasStream = false;
    }
    private int RemainingMilliseconds(long expiryTimestamp) {
        var remaining = (expiryTimestamp - m_time.GetTimestamp());

        if (remaining <= 0L) {
            return 1;
        }

        var milliseconds = ((remaining * 1000L) / m_time.TimestampFrequency);

        return ((milliseconds < 1L)
            ? 1
            : ((milliseconds > 1000L)
                ? 1000
                : ((int)milliseconds)
        ));
    }
    private async Task RunAsync(CancellationToken cancellationToken) {
        var firstParsed = false;
        var loggedUnparsed = false;

        try {
            m_diagnostics?.Invoke($"[gamepad] {Type} init starting ({SlotLabel()})");

            await m_parser.InitializeAsync(
                cancellationToken: cancellationToken,
                playerIndex: PlayerIndex
            );

            m_diagnostics?.Invoke($"[gamepad] {Type} init complete; awaiting reports");

            var buffer = m_readBuffer;
            var streamingDeadline = (m_time.GetTimestamp() + m_streamingDeadlineTicks);
            var lastParsedTimestamp = m_time.GetTimestamp();

            while (!cancellationToken.IsCancellationRequested) {
                await DrainOutputAsync(cancellationToken: cancellationToken);

                if (m_parser is IServicedGamepadParser serviced) {
                    await serviced.ServiceAsync(
                        cancellationToken: cancellationToken,
                        timestamp: m_time.GetTimestamp(),
                        timestampFrequency: m_time.TimestampFrequency
                    );
                }

                // Poll while streaming so output, scheduled effects, rumble expiry, and receiver-silence detection
                // continue to make progress even when input reports pause. The Windows transport reuses its timeout
                // source across reads that complete before the deadline instead of creating one for every read.
                int read;

                if (!firstParsed) {
                    read = await m_hid.ReadAsync(
                        buffer: buffer,
                        cancellationToken: cancellationToken,
                        timeoutInMilliseconds: StartupPollMilliseconds
                    );
                } else {
                    var rumbleDeadline = NextRumbleDeadline();
                    var timeout = ((rumbleDeadline != long.MaxValue)
                        ? Math.Min(
                            val1: SteadyStatePollMilliseconds,
                            val2: RemainingMilliseconds(expiryTimestamp: rumbleDeadline)
                        )
                        : SteadyStatePollMilliseconds
                    );

                    read = await m_hid.ReadAsync(
                        buffer: buffer,
                        cancellationToken: cancellationToken,
                        timeoutInMilliseconds: timeout
                    );
                }

                if (0 < read) {
                    bool parsed;
                    GamepadState state;

                    try {
                        parsed = m_parser.TryParse(
                            report: buffer.AsSpan(
                                length: read,
                                start: 0
                            ),
                            state: out state
                        );
                    } catch (Exception parseException) {
                        // A parser must never throw on hostile bytes — when one does, the report payload IS the
                        // bug report. Dump it before the fault machinery eats the evidence.
                        m_diagnostics?.Invoke($"[gamepad] {Type} parser THREW on report id=0x{buffer[0]:x2} len={read} head={Convert.ToHexString(
                            buffer,
                            0,
                            Math.Min(
                                val1: read,
                                val2: 24
                            )
                        )}: {parseException}");
                        throw;
                    }

                    if (parsed) {
                        lastParsedTimestamp = m_time.GetTimestamp();
                        // Stamp the report's arrival on the I/O thread — the earliest accurate point — and a
                        // per-device sequence, so the coalescer can carry true sub-frame edge times forward and a
                        // drain can order what it folded. The parser stays pure; timing is layered on here.
                        state = state with {
                            ArrivalTicks = m_clock.NowTicks,
                            SequenceNumber = ++m_sequence,
                        };
                        m_coalescer.Update(state: in state);

                        if (!m_hasStream) {
                            // Publish the flag last: an observer that sees HasStream finds output accepting.
                            m_output.Resume();
                            m_hasStream = true;
                        }

                        if (!firstParsed) {
                            firstParsed = true;
                            m_diagnostics?.Invoke($"[gamepad] {Type} streaming (first parsed report, len={read})");
                        }
                    } else {
                        var slotEvent = ((m_parser is IWirelessSlotParser slotParser)
                            ? slotParser.ClassifySlotEvent(report: buffer.AsSpan(
                                length: read,
                                start: 0
                            ))
                            : WirelessSlotEvent.None
                        );

                        if (WirelessSlotEvent.Connected == slotEvent) {
                            // A controller just paired into this slot. The open-time initialization addressed an
                            // empty slot, so run it again for the pad that's actually here; its first state report
                            // then claims a player slot (via HasStream, reconciled by the manager).
                            m_diagnostics?.Invoke($"[gamepad] {Type} receiver slot paired; initializing the controller");

                            await ParkReceiverSlotAsync(cancellationToken: cancellationToken);
                            firstParsed = false;
                            streamingDeadline = (m_time.GetTimestamp() + m_streamingDeadlineTicks);

                            await m_parser.InitializeAsync(
                                cancellationToken: cancellationToken,
                                playerIndex: PlayerIndex
                            );
                        } else if (WirelessSlotEvent.Disconnected == slotEvent) {
                            // The controller left the slot: back to dormant. The manager releases the player slot
                            // when it sees HasStream drop; resetting firstParsed returns the loop to bounded reads
                            // (and the refreshed deadline keeps a non-deferred device's watchdog honest).
                            await ParkReceiverSlotAsync(cancellationToken: cancellationToken);
                            firstParsed = false;
                            streamingDeadline = (m_time.GetTimestamp() + m_streamingDeadlineTicks);
                            m_diagnostics?.Invoke($"[gamepad] {Type} receiver slot unpaired; parking");
                        } else if (!loggedUnparsed) {
                            // Data is arriving but not in the expected report shape — e.g. the report-mode
                            // subcommand didn't take and the controller is still sending its simple (0x3F) report.
                            loggedUnparsed = true;
                            m_diagnostics?.Invoke($"[gamepad] {Type} unparsed report id=0x{buffer[0]:x2} len={read}");
                        }
                    }
                }

                if (
                    firstParsed &&
                    m_activateOnStream &&
                    ((m_time.GetTimestamp() - lastParsedTimestamp) >= m_receiverSilenceTimeoutTicks)
                ) {
                    await ParkReceiverSlotAsync(cancellationToken: cancellationToken);
                    firstParsed = false;
                    streamingDeadline = (m_time.GetTimestamp() + m_streamingDeadlineTicks);
                    m_diagnostics?.Invoke($"[gamepad] {Type} receiver stream silent; releasing slot and parking");
                }

                // A continuously streaming controller that falls silent has lost its link even when the transport
                // reports no removal; faulting releases its held input and lets the manager reopen it.
                if (
                    firstParsed &&
                    !m_activateOnStream &&
                    (m_streamSilenceTicks > 0L) &&
                    ((m_time.GetTimestamp() - lastParsedTimestamp) >= m_streamSilenceTicks)
                ) {
                    throw new TimeoutException(message: $"{Type} stopped streaming for {(m_streamSilenceTicks / (double)m_time.TimestampFrequency):F1}s");
                }

                // A deferred-activation device is a receiver slot that may legitimately sit empty forever, so
                // stream silence is not a fault for it; everything else must prove liveness within the deadline.
                if (
                    !firstParsed &&
                    !m_activateOnStream &&
                    (m_time.GetTimestamp() > streamingDeadline)
                ) {
                    // Initialized but never produced a parseable report (a stuck handshake, or a controller still
                    // on its simple 0x3F report). Fault so the manager prunes it and the rescan reopens and
                    // re-initializes it, instead of leaving a zombie that holds a player slot forever.
                    throw new TimeoutException(message: $"{Type} did not begin streaming within {StreamingDeadlineSeconds}s of init");
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
    // A dormant receiver slot has no player identity yet, and the lifecycle diagnostics should say so instead of
    // printing a bogus "player 0".
    private string SlotLabel() {
        var playerIndex = PlayerIndex;

        return ((playerIndex >= 0)
            ? $"player {(playerIndex + 1)}"
            : "dormant receiver slot"
        );
    }
    // A canceled or faulted loop is as terminal as a completed one, so teardown may continue after any of them.
    private bool TryJoinLoop(Task? loop) {
        if (loop is null) {
            return true;
        }

        using var joined = new CancellationTokenSource();
        var deadline = Task.Delay(
            cancellationToken: joined.Token,
            delay: TimeSpan.FromMilliseconds(value: DisposeJoinTimeoutMilliseconds),
            timeProvider: m_time
        );

        _ = Task.WaitAny(tasks: [deadline, loop]);
        joined.Cancel();

        return loop.IsCompleted;
    }

    public void Dispose() {
        Task? loop;

        lock (m_lifecycleGate) {
            if (m_disposed) {
                return;
            }

            m_disposed = true;
            m_output.Kill();
            m_cancellation.Cancel();
            loop = m_loop;
        }

        var loopCompleted = TryJoinLoop(loop: loop);
        var hidForceClosed = false;

        if (!loopCompleted) {
            // Cancellation normally completes the timed read. If the underlying HID IRP is wedged, closing its
            // handle is the remaining cancellation mechanism. Do that before touching parser or CTS state, then
            // give the loop one final bounded opportunity to observe the close and exit.
            m_diagnostics?.Invoke($"[gamepad] {Type} I/O loop did not stop after cancellation; forcing HID close");
            m_hid.Dispose();
            hidForceClosed = true;
            loopCompleted = TryJoinLoop(loop: loop);
        }

        if (!loopCompleted) {
            // The handle is closed, but an uncooperative transport still owns the loop's parser/CTS references.
            // Leave those objects intact rather than reintroducing dispose-under-use; process teardown can reclaim
            // them, and the diagnostic makes the exceptional leak observable.
            m_diagnostics?.Invoke($"[gamepad] {Type} I/O loop remained live after forced HID close; parser and cancellation resources retained");

            return;
        }

        if (!hidForceClosed) {
            NeutralizeOutput();

            try {
                // Give a parser that holds device state a chance to restore it (e.g. a controller switched out of
                // its built-in keyboard/mouse emulation) while the live handle is no longer owned by the loop.
                (m_parser as IDisposable)?.Dispose();
            } finally {
                try {
                    m_hid.Dispose();
                } finally {
                    m_cancellation.Dispose();
                }
            }
        } else {
            m_cancellation.Dispose();
        }
    }
    /// <summary>Starts the device's I/O loop on a background task.</summary>
    public void Start() {
        lock (m_lifecycleGate) {
            if (
                m_started ||
                m_disposed
            ) {
                return;
            }

            m_started = true;
            m_loop = Task.Run(function: () => RunAsync(cancellationToken: m_cancellation.Token));
        }
    }
}
