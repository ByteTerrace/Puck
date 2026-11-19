using System.Diagnostics;
using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Input.Output;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>Snapshot of one local seat's live haptic output state for <c>haptics.state</c> read-back.</summary>
/// <param name="Seat">The 1-based local seat number.</param>
/// <param name="Joined">Whether the seat is currently joined.</param>
/// <param name="RowCount">How many authored <c>haptics</c> rows target this seat in its routed definition.</param>
/// <param name="HasGamepad">Whether a gamepad is currently assigned to this seat.</param>
/// <param name="HasOutput">Whether an <see cref="IGamepadOutput"/> handle resolved for that gamepad.</param>
/// <param name="PulsesSent">Total pulses or level transitions submitted for this seat since boot (monotone).</param>
/// <param name="LastRowId">The id of the last row that drove output for this seat, or <see langword="null"/>.</param>
/// <param name="LastLow">The last submitted low-frequency motor intensity.</param>
/// <param name="LastHigh">The last submitted high-frequency motor intensity.</param>
/// <param name="LastDurationMilliseconds">The last submitted duration, in milliseconds.</param>
/// <param name="LastTick">The simulation tick at which the last output was submitted.</param>
/// <param name="LastAccepted">Whether the pad's <see cref="IGamepadOutput.Rumble"/> accepted the last submission.</param>
public readonly record struct SeatHapticStatus(
    int Seat,
    bool Joined,
    int RowCount,
    bool HasGamepad,
    bool HasOutput,
    ulong PulsesSent,
    string? LastRowId,
    float LastLow,
    float LastHigh,
    uint LastDurationMilliseconds,
    ulong LastTick,
    bool LastAccepted
);
/// <summary>
/// Presentation-side haptic director: evaluates each world's authored <c>haptics</c> rows per local seat against that
/// seat's routed <see cref="WorldStateMirror"/> (supporting <c>$body</c> per-seat/body keys via
/// <see cref="WorldStateLease"/>) and drives the seat's assigned pad through <see cref="IGamepadOutput.Rumble"/>.
/// Runs on the pump thread once per fixed step; reads state only and never feeds simulation.
/// </summary>
public sealed class WorldHapticDirector {
    private const float LevelChangeEpsilon = 1e-4f;

    private readonly WorldValueDomainGuard m_domains;
    private readonly Func<InputDeviceId, IGamepadOutput?>? m_outputResolver;
    private readonly WorldPerceptionAnchor? m_perception;
    private readonly PlayerRoster m_roster;
    private readonly WorldSeatBindings m_seatBindings;
    private readonly WorldSeatAuthorityRouter m_seatRouter;
    private readonly SeatSlot[] m_seats;
    private readonly Func<long> m_timestamp;

    /// <summary>Initializes the director over the seat roster, routed state bindings, and optional pad output resolver.</summary>
    /// <param name="roster">The local seat roster, resolving each seat to its gamepad device.</param>
    /// <param name="seatBindings">The routed definition and state mirror for each seat.</param>
    /// <param name="seatRouter">The seat authority router, supplying each seat's routed entity index.</param>
    /// <param name="perception">Optional perception anchor for boot-authority body possession.</param>
    /// <param name="outputResolver">Resolves a gamepad device id to its <see cref="IGamepadOutput"/>, or
    /// <see langword="null"/> when physical devices are disabled (headless or <c>--devices none</c>).</param>
    /// <param name="domains">Optional shared domain guard for reporting out-of-range bound scalars.</param>
    /// <param name="timestamp">Optional monotonic timestamp source (<see cref="Stopwatch.Frequency"/> ticks) for tests.</param>
    public WorldHapticDirector(
        PlayerRoster roster,
        WorldSeatBindings seatBindings,
        WorldSeatAuthorityRouter seatRouter,
        WorldPerceptionAnchor? perception = null,
        Func<InputDeviceId, IGamepadOutput?>? outputResolver = null,
        WorldValueDomainGuard? domains = null,
        Func<long>? timestamp = null
    ) {
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: seatBindings);
        ArgumentNullException.ThrowIfNull(argument: seatRouter);

        m_roster = roster;
        m_seatBindings = seatBindings;
        m_seatRouter = seatRouter;
        m_perception = perception;
        m_outputResolver = outputResolver;
        m_domains = (domains ?? new WorldValueDomainGuard());
        m_timestamp = (timestamp ?? Stopwatch.GetTimestamp);
        m_seats = new SeatSlot[PlayerRoster.MaxSlots];

        for (var slot = 0; (slot < m_seats.Length); slot++) {
            m_seats[slot] = new SeatSlot();
        }
    }

    /// <summary>Evaluates authored <c>haptics</c> rows for every local seat at completed tick <paramref name="tick"/>
    /// and submits any resulting <see cref="RumbleEffect"/> to each seat's pad.</summary>
    /// <param name="tick">The completed host/simulation tick.</param>
    public void Tick(ulong tick) {
        var now = m_timestamp();

        for (var slot = 0; (slot < m_seats.Length); slot++) {
            TickSeat(
                now: now,
                seat: m_seats[slot],
                slot: slot,
                tick: tick
            );
        }
    }
    /// <summary>Returns the current read-back snapshot for local seat <paramref name="slot"/> (0-based).</summary>
    public SeatHapticStatus Status(int slot) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            other: m_seats.Length,
            value: slot
        );

        var seat = m_seats[slot];

        return new SeatHapticStatus(
            HasGamepad: seat.HasGamepad,
            HasOutput: seat.HasOutput,
            Joined: m_roster.IsJoined(slot: slot),
            LastAccepted: seat.LastAccepted,
            LastDurationMilliseconds: seat.LastDurationMilliseconds,
            LastHigh: seat.LastHigh,
            LastLow: seat.LastLow,
            LastRowId: seat.LastRowId,
            LastTick: seat.LastTick,
            PulsesSent: seat.PulsesSent,
            RowCount: seat.RowCount,
            Seat: PlayerRoster.DisplayNumber(slot: slot)
        );
    }
    /// <summary>Formats the <c>haptics.state</c> read-back across all joined local seats.</summary>
    public string Describe() {
        var builder = new StringBuilder(value: "[haptics.state:");
        var any = false;

        for (var slot = 0; (slot < m_seats.Length); slot++) {
            var status = Status(slot: slot);

            if (!status.Joined && (status.PulsesSent == 0UL) && (status.RowCount == 0)) {
                continue;
            }

            if (any) {
                _ = builder.Append(value: " |");
            }

            any = true;

            var pad = (status.HasOutput
                ? "ready"
                : (status.HasGamepad
                    ? "no-output"
                    : "none"));
            var last = (status.LastRowId is { } id
                ? string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"{id}@t{status.LastTick}({status.LastLow:0.##},{status.LastHigh:0.##},{status.LastDurationMilliseconds}ms,accepted={status.LastAccepted.ToString().ToLowerInvariant()})"
                )
                : "none");

            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" seat{status.Seat}:rows={status.RowCount},pad={pad},pulses={status.PulsesSent},last={last}"
            );
        }

        if (!any) {
            _ = builder.Append(value: " no active seats");
        }

        _ = builder.Append(value: ']');

        return builder.ToString();
    }

    private void TickSeat(int slot, SeatSlot seat, ulong tick, long now) {
        if (!m_roster.IsJoined(slot: slot)) {
            StopActiveDevice(
                now: now,
                seat: seat
            );
            seat.ResetTrackers();
            seat.RowCount = 0;
            seat.HasGamepad = false;
            seat.HasOutput = false;

            return;
        }

        var hasGamepad = m_roster.TryGetSeatDevice(
            device: out var deviceId,
            kind: InputDeviceKind.Gamepad,
            slot: slot
        );

        if (
            seat.HasActiveDevice &&
            (!hasGamepad || (seat.ActiveDevice != deviceId))
        ) {
            StopActiveDevice(
                now: now,
                seat: seat
            );
        }

        var output = (hasGamepad
            ? m_outputResolver?.Invoke(arg: deviceId)
            : null);

        seat.HasGamepad = hasGamepad;
        seat.HasOutput = (output is not null);
        if (hasGamepad) {
            seat.ActiveDevice = deviceId;
            seat.HasActiveDevice = true;
        } else {
            seat.HasActiveDevice = false;
        }

        m_seatBindings.GetRoutedState(
            definition: out var definition,
            slot: slot,
            state: out var mirror
        );

        if (
            (mirror is null) ||
            (definition.Haptics.Count == 0)
        ) {
            if (seat.LevelActive && (now >= seat.PulseExpiryTimestamp)) {
                StopOutput(
                    output: output,
                    seat: seat
                );
            }

            seat.ResetTrackers();
            seat.RowCount = 0;

            return;
        }

        var bodyIndex = ResolveBodyIndex(slot: slot);

        seat.Lease.Bind(
            bodyIndex: bodyIndex,
            mirror: mirror
        );

        if (
            (seat.SeenLifetime != mirror.Lifetime) ||
            (seat.SeenInstalls != mirror.Installs)
        ) {
            seat.SeenLifetime = mirror.Lifetime;
            seat.SeenInstalls = mirror.Installs;
            seat.ResetTrackers();
        }

        var seatNumber = PlayerRoster.DisplayNumber(slot: slot);
        var rows = definition.Haptics;
        var matchingRows = 0;

        var pulseFired = false;
        var pulseLow = 0f;
        var pulseHigh = 0f;
        var pulseDurationMs = 0u;
        string? pulseRowId = null;

        var levelActive = false;
        var levelLow = 0f;
        var levelHigh = 0f;
        var levelDurationMs = 0u;
        string? levelRowId = null;

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            if (!TargetsSeat(row: row, seatNumber: seatNumber)) {
                continue;
            }

            matchingRows++;

            var state = seat.GetOrAddRowState(id: row.Id);

            state.SeenSerial = seat.Serial;

            if (row.Mode == WorldHapticMode.Level) {
                var low = ReadScalar(
                    fallback: 0f,
                    field: WorldValueFields.HapticLow,
                    mirror: mirror,
                    row: row,
                    rowIndex: index,
                    scalar: row.Low,
                    seat: seat
                );
                var high = ReadScalar(
                    fallback: 0f,
                    field: WorldValueFields.HapticHigh,
                    mirror: mirror,
                    row: row,
                    rowIndex: index,
                    scalar: row.High,
                    seat: seat
                );

                if ((low > 0f) || (high > 0f)) {
                    var durationMs = ToDurationMilliseconds(seconds: row.DurationSeconds);

                    levelActive = true;
                    levelLow = MathF.Max(x: levelLow, y: low);
                    levelHigh = MathF.Max(x: levelHigh, y: high);
                    levelDurationMs = Math.Max(val1: levelDurationMs, val2: durationMs);
                    levelRowId ??= row.Id;
                }

                continue;
            }

            if (row.Signal is not { } signalScalar) {
                continue;
            }

            var signal = ReadScalar(
                fallback: 0f,
                field: WorldValueFields.HapticSignal,
                mirror: mirror,
                row: row,
                rowIndex: index,
                scalar: in signalScalar,
                seat: seat
            );

            var triggered = false;

            if (!state.Seeded) {
                state.Seeded = true;
                state.LastSignal = signal;
            } else if (row.Mode == WorldHapticMode.Rise) {
                if ((state.LastSignal <= 0f) && (signal > 0f)) {
                    triggered = true;
                }

                state.LastSignal = signal;
            } else if (row.Mode == WorldHapticMode.Change) {
                if (signal != state.LastSignal) {
                    triggered = true;
                    state.LastSignal = signal;
                }
            }

            if (!triggered) {
                continue;
            }

            var rowLow = ReadScalar(
                fallback: 0f,
                field: WorldValueFields.HapticLow,
                mirror: mirror,
                row: row,
                rowIndex: index,
                scalar: row.Low,
                seat: seat
            );
            var rowHigh = ReadScalar(
                fallback: 0f,
                field: WorldValueFields.HapticHigh,
                mirror: mirror,
                row: row,
                rowIndex: index,
                scalar: row.High,
                seat: seat
            );

            if ((rowLow <= 0f) && (rowHigh <= 0f)) {
                continue;
            }

            var rowDurationMs = ToDurationMilliseconds(seconds: row.DurationSeconds);

            pulseFired = true;
            pulseLow = MathF.Max(x: pulseLow, y: rowLow);
            pulseHigh = MathF.Max(x: pulseHigh, y: rowHigh);
            pulseDurationMs = Math.Max(val1: pulseDurationMs, val2: rowDurationMs);
            pulseRowId ??= row.Id;
        }

        seat.RowCount = matchingRows;
        seat.PruneUnseenRows();
        seat.Serial++;

        if (pulseFired) {
            var low = MathF.Max(x: pulseLow, y: levelLow);
            var high = MathF.Max(x: pulseHigh, y: levelHigh);
            var durationMs = Math.Max(val1: pulseDurationMs, val2: levelDurationMs);

            seat.PulseExpiryTimestamp = TimestampAfter(
                milliseconds: durationMs,
                now: now
            );
            seat.LevelActive = levelActive;

            if (levelActive) {
                seat.LevelLow = levelLow;
                seat.LevelHigh = levelHigh;
                seat.LevelRefreshTimestamp = TimestampAfter(
                    milliseconds: Math.Max(val1: 1u, val2: (levelDurationMs / 2u)),
                    now: now
                );
            }

            SendRumble(
                durationMs: durationMs,
                high: high,
                low: low,
                output: output,
                rowId: pulseRowId!,
                seat: seat,
                tick: tick
            );

            return;
        }

        if (levelActive) {
            var changed = (
                !seat.LevelActive ||
                (MathF.Abs(x: (levelLow - seat.LevelLow)) > LevelChangeEpsilon) ||
                (MathF.Abs(x: (levelHigh - seat.LevelHigh)) > LevelChangeEpsilon)
            );
            var refreshDue = (now >= seat.LevelRefreshTimestamp);

            if (changed || refreshDue) {
                seat.LevelActive = true;
                seat.LevelLow = levelLow;
                seat.LevelHigh = levelHigh;
                seat.LevelRefreshTimestamp = TimestampAfter(
                    milliseconds: Math.Max(val1: 1u, val2: (levelDurationMs / 2u)),
                    now: now
                );

                SendRumble(
                    durationMs: levelDurationMs,
                    high: levelHigh,
                    low: levelLow,
                    output: output,
                    rowId: levelRowId!,
                    seat: seat,
                    tick: tick
                );
            }

            return;
        }

        if (seat.LevelActive && (now >= seat.PulseExpiryTimestamp)) {
            StopOutput(
                output: output,
                seat: seat
            );
        }
    }
    private int ResolveBodyIndex(int slot) {
        if (
            (m_seatRouter.TryRoute(slot: slot) is { } route) &&
            (route.Entity.Index != slot)
        ) {
            return route.Entity.Index;
        }

        return (m_perception?.PerceivedBody(slot: slot) ?? slot);
    }
    private float ReadScalar(
        WorldStateMirror mirror,
        SeatSlot seat,
        WorldHapticRow row,
        in BindableScalar scalar,
        WorldValueField field,
        int rowIndex,
        float fallback
    ) {
        float raw;

        if (scalar.State is { Key: StateBinding.BodyKey } binding) {
            var slot = seat.Lease.Slot(
                reference: scalar.Binding!,
                truth: binding.Target
            );

            if (!seat.Lease.TryDeliveredNumber(slot: slot, value: out raw)) {
                raw = fallback;
            }
        } else {
            raw = mirror.Scalar(
                delivered: true,
                fallback: fallback,
                scalar: in scalar
            );
        }

        var site = new WorldValueSite(
            Index: rowIndex,
            Inner: null,
            Section: "haptics"
        );

        return m_domains.Resolve(
            fallback: fallback,
            field: field,
            mirror: mirror,
            scalar: in scalar,
            site: in site,
            value: raw
        );
    }
    private static bool TargetsSeat(WorldHapticRow row, int seatNumber) => (
        (row.Seat is null) ||
        (
            PrincipalTokens.TryParseSeat(
                number: out var targetSeat,
                token: row.Seat
            ) &&
            (targetSeat == seatNumber)
        )
    );
    private static uint ToDurationMilliseconds(float seconds) => ((uint)Math.Clamp(
        max: ((long)(WorldHapticCapacity.MaxDurationSeconds * 1000f)),
        min: 1L,
        value: ((long)MathF.Round(x: (seconds * 1000f)))
    ));
    private static long TimestampAfter(long now, uint milliseconds) =>
        (now + ((long)((milliseconds / 1000d) * Stopwatch.Frequency)));
    private void SendRumble(
        SeatSlot seat,
        IGamepadOutput? output,
        string rowId,
        float low,
        float high,
        uint durationMs,
        ulong tick
    ) {
        var accepted = ((output is not null) && output.Rumble(effect: new RumbleEffect(
            DurationMilliseconds: durationMs,
            HighFrequency: high,
            LowFrequency: low
        )));

        seat.PulsesSent++;
        seat.LastRowId = rowId;
        seat.LastLow = low;
        seat.LastHigh = high;
        seat.LastDurationMilliseconds = durationMs;
        seat.LastTick = tick;
        seat.LastAccepted = accepted;
    }
    private static void StopOutput(SeatSlot seat, IGamepadOutput? output) {
        if (output is not null) {
            _ = output.Rumble(effect: RumbleEffect.Off);
        }

        seat.LevelActive = false;
        seat.LevelLow = 0f;
        seat.LevelHigh = 0f;
    }
    private void StopActiveDevice(SeatSlot seat, long now) {
        if (
            seat.HasActiveDevice &&
            (seat.LevelActive || (now < seat.PulseExpiryTimestamp)) &&
            (m_outputResolver?.Invoke(arg: seat.ActiveDevice) is { } previousOutput)
        ) {
            _ = previousOutput.Rumble(effect: RumbleEffect.Off);
        }

        seat.HasActiveDevice = false;
        seat.ActiveDevice = default;
        seat.LevelActive = false;
        seat.LevelLow = 0f;
        seat.LevelHigh = 0f;
        seat.PulseExpiryTimestamp = 0L;
    }

    private sealed class RowState {
        public float LastSignal;
        public int SeenSerial;
        public bool Seeded;
    }

    private sealed class SeatSlot {
        public readonly WorldStateLease Lease = new();
        public readonly Dictionary<string, RowState> Rows = new(comparer: StringComparer.Ordinal);
        public readonly List<string> PruneScratch = [];

        public InputDeviceId ActiveDevice;
        public bool HasActiveDevice;
        public bool HasGamepad;
        public bool HasOutput;
        public bool LastAccepted;
        public uint LastDurationMilliseconds;
        public float LastHigh;
        public float LastLow;
        public string? LastRowId;
        public ulong LastTick;
        public bool LevelActive;
        public float LevelHigh;
        public float LevelLow;
        public long LevelRefreshTimestamp;
        public long PulseExpiryTimestamp;
        public ulong PulsesSent;
        public int RowCount;
        public int SeenInstalls = -1;
        public int SeenLifetime = -1;
        public int Serial = 1;

        public RowState GetOrAddRowState(string id) {
            if (!Rows.TryGetValue(key: id, value: out var state)) {
                state = new RowState();
                Rows[id] = state;
            }

            return state;
        }
        public void PruneUnseenRows() {
            if (Rows.Count == 0) {
                return;
            }

            PruneScratch.Clear();

            foreach (var (id, state) in Rows) {
                if (state.SeenSerial != Serial) {
                    PruneScratch.Add(item: id);
                }
            }

            foreach (var id in PruneScratch) {
                _ = Rows.Remove(key: id);
            }
        }
        public void ResetTrackers() {
            Rows.Clear();
        }
    }
}
