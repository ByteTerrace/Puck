using Puck.Abstractions.Memory;
using System.Globalization;
using Puck.Abstractions.Collections;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One admitted input owner's epoch, source provenance, held origins and bounded history.</summary>
public sealed partial class WorldInputOwner {
    private readonly WorldInputCatalog m_catalog;
    private readonly WorldInputHistory m_policy;
    private readonly FixedQ4816 m_retention;
    private readonly FixedQ4816 m_lateness;
    private readonly FixedQ4816 m_calibrationLimit;
    private readonly WorldInputSourceState[] m_sources;
    private readonly WorldInputSourceState[] m_sourceScratch;
    private readonly bool[] m_seen;
    private readonly Dictionary<ushort, int> m_handles;
    private readonly HashSet<ulong> m_reboundActivations;
    private readonly HashSet<string> m_reboundSources;
    // The history and switch rings, and the step's event scratch, hold what the owner has written and grow by doubling
    // to their admitted capacities (m_historyCapacity, m_switchCapacity, m_scratchCapacity), so an owner that sees few
    // events (most of a world's producer-driven bodies) never holds its whole bound. Every capacity test reads the
    // capacity, never a storage length; a grown ring is laid out oldest first from slot zero, so its order is unchanged.
    // Record ids index the claim tables modulo the history capacity.
    private WorldInputRecord[] m_history;
    private WorldInputSwitch[] m_switches;
    private readonly int m_historyCapacity;
    private readonly int m_switchCapacity;
    private readonly int m_scratchCapacity;

    private int m_switchHead;
    private int m_switchCount;

    private WorldInputRecord[] m_eventScratch;
    private readonly (ushort Channel, long Press, long Release, int Direction)[] m_crossings;
    private readonly bool[] m_crossingHeld;
    private readonly bool[] m_crossingScratch;
    private readonly bool m_usesFacing;

    private readonly WorldInputHold[] m_holds = new WorldInputHold[ChannelLimits.MaxChannels];
    // The holds a call stages before it commits them to m_holds: per-call scratch, so one per thread serves every owner.
    // A switch or restore clears it before writing; an admission reads back only the addresses it wrote this call
    // (its holdsWritten mask), so nothing a call leaves behind is read by the next.
    [ThreadStatic]
    private static WorldInputHold[]? HoldScratchStorage;
    private static WorldInputHold[] HoldScratch => (HoldScratchStorage ??= new WorldInputHold[ChannelLimits.MaxChannels]);
    private readonly Int128[] m_sum = new Int128[ChannelLimits.MaxChannels];
    private readonly long[] m_maximum = new long[ChannelLimits.MaxChannels];

    private int m_head;
    private int m_count;
    private uint m_sequence;
    private ulong m_eventId;
    private WorldInputInterval m_lastCapture;
    private FixedQ4816 m_lastOffset;
    private ulong m_step;
    private int m_stepEvents;
    private bool m_steadyImageVerified;

    /// <summary>Admits an owner and allocates its bounded storage. Source handles are immutable until a reset/re-admission.</summary>
    public WorldInputOwner(WorldInputCatalog catalog, WorldInputMapping mapping, WorldInputHistory policy,
        IReadOnlyList<WorldInputSource> sources, uint revision = 1, IReadOnlyList<WorldInputPattern>? patterns = null) {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        m_catalog = catalog;
        Mapping = mapping;
        Revision = revision;
        m_policy = policy;
        m_retention = Parse(value: policy.RetentionSeconds);
        m_lateness = Parse(value: policy.MaximumLatenessSeconds);
        m_calibrationLimit = Parse(value: policy.CalibrationLimitSeconds);
        m_sources = catalog.CompileSources(sources: sources, handles: out m_handles);
        m_sourceScratch = new WorldInputSourceState[sources.Count];
        m_seen = new bool[sources.Count];
        m_reboundActivations = new HashSet<ulong>(capacity: sources.Count);
        m_reboundSources = new HashSet<string>(capacity: sources.Count, comparer: StringComparer.Ordinal);
        m_historyCapacity = catalog.HistoryCapacity(ingressCapacity: policy.EventCapacity);
        m_switchCapacity = policy.EventCapacity;
        m_scratchCapacity = policy.MaximumEventsPerStep;
        m_history = [];
        m_switches = [];
        m_eventScratch = [];
        m_patterns = CompiledWorldInputPattern.Compile(catalog: catalog, declarations: (patterns ?? []), policy: policy);
        var stages = ((m_patterns.Length == 0) ? 0 : m_patterns.Max(selector: static row => row.Stages.Length));

        m_matchWidth = ((m_patterns.Length == 0) ? 0 : m_patterns.Max(selector: static row => row.Width));
        var matchSize = checked(((stages * m_historyCapacity) * m_matchWidth));

        if (matchSize > (1024 * 1024)) { throw new ArgumentException(message: "inputPatterns matching scratch exceeds 4 MiB per owner"); }
        m_matchPaths = new int[matchSize];
        m_matchValid = new bool[(stages * m_historyCapacity)];
        m_matchNext = new int[(m_matchWidth * m_historyCapacity)];
        m_matchPrevious = new int[(m_matchWidth * m_historyCapacity)];
        m_matchCache = new WorldInputMatch[m_patterns.Length];
        m_matchCached = new bool[m_patterns.Length];
        m_claimed = new ulong[((m_patterns.Length == 0) ? 0 : (m_patterns.Max(selector: static row => row.Group) + 1))][];
        for (var group = 0; (group < m_claimed.Length); group++) { m_claimed[group] = new ulong[m_historyCapacity]; }
        m_crossings = new (ushort, long, long, int)[(policy.Crossings?.Count ?? 0)];
        m_crossingHeld = new bool[m_crossings.Length];
        m_crossingScratch = new bool[m_crossings.Length];
        var crossingNames = new HashSet<string>(comparer: StringComparer.Ordinal);

        for (var index = 0; (index < m_crossings.Length); index++) {
            var row = policy.Crossings![index];

            m_usesFacing |= (row.FacingState is not null);
            if (string.IsNullOrWhiteSpace(value: row.Name) || !crossingNames.Add(item: row.Name) ||
                !catalog.TryGetLogicalId(row.Channel, out var channel) ||
                (catalog.Channels[channel].Shape is not (ChannelShape.Bipolar or ChannelShape.Unipolar)) ||
                (row.Direction is not (1 or -1)) || ((row.Press * row.Direction) <= (row.Release * row.Direction)) ||
                (row.Press > 1) || (row.Release > 1) || (row.Press < ((catalog.Channels[channel].Shape == ChannelShape.Unipolar) ? 0 : -1)) ||
                (row.Release < ((catalog.Channels[channel].Shape == ChannelShape.Unipolar) ? 0 : -1))) {
                throw new ArgumentException(message: $"inputHistory crossing '{row.Name}' has invalid channel, thresholds or direction", paramName: nameof(policy));
            }
            m_crossings[index] = (channel, Parse(value: row.Press).Value, Parse(value: row.Release).Value, row.Direction);
        }
    }

    /// <summary>Installed tuple, chosen only by authoritative selectors.</summary>
    public WorldInputMapping Mapping { get; private set; }
    /// <summary>Installed definition's mapping revision.</summary>
    public uint Revision { get; }

    /// <summary>Current authority-issued input epoch.</summary>
    public ulong Epoch { get; private set; } = 1;

    /// <summary>Last admitted wire sequence, for resuming an authority-owned synthetic producer.</summary>
    public uint Sequence => m_sequence;
    /// <summary>Accepted final image.</summary>
    public PlayerIntent Image { get; private set; }
    /// <summary>Bounded signed calibration in fixed seconds.</summary>
    public FixedQ4816 Calibration { get; private set; }
    /// <summary>Latest committed barrier.</summary>
    public WorldInputSwitch LastSwitch { get; private set; }
    /// <summary>Current ring occupancy.</summary>
    public int Count => m_count;
    /// <summary>Retained commit boundaries, including intermediate switches within one simulation tick.</summary>
    public int SwitchCount => m_switchCount;

    /// <summary>Reads one retained boundary in commit order.</summary>
    public WorldInputSwitch SwitchRecord(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, m_switchCount);
        return m_switches[((m_switchHead + index) % m_switches.Length)];
    }

    /// <summary>Capacity outcomes, retained independently of ring eviction.</summary>
    public ulong RefusedBatches { get; private set; }
    /// <summary>Accepted source/rearm state, for publication and capture.</summary>
    public ReadOnlySpan<WorldInputSourceState> Sources => m_sources;

    /// <summary>Reads a retained record in oldest-first order.</summary>
    public WorldInputRecord Record(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, m_count);
        return m_history[((m_head + index) % m_history.Length)];
    }
    /// <summary>Reads the original held press without exposing it as a fresh edge.</summary>
    public bool TryHeld(ushort channel, out WorldInputHold hold) {
        var address = Mapping.Address(logicalId: channel);

        hold = ((address >= 0) ? m_holds[address] : default);
        return ((hold.Press != 0) && (Image[address] != FixedQ4816.Zero));
    }
    /// <summary>Returns an indexed newest matching event in the current epoch; missing time is never zero.</summary>
    public bool TryEvent(ushort channel, WorldInputEventKind kind, int occurrence, out WorldInputRecord record) {
        if (occurrence >= 0) {
            for (var index = (m_count - 1); (index >= 0); index--) {
                var candidate = Record(index: index);

                if ((candidate.Epoch == Epoch) && (candidate.Event.Channel == channel) && (candidate.Event.Kind == kind) && (occurrence-- == 0)) {
                    record = candidate;
                    return true;
                }
            }
        }
        record = default;
        return false;
    }
    // Makes room for `count` records in the history ring, at most its capacity, laying a grown ring out oldest first.
    private void ReserveHistory(int count) =>
        m_history = Rings.Regrown(capacity: m_historyCapacity, count: m_count, head: ref m_head, needed: count, ring: m_history);
    // Makes room for `count` boundaries in the switch ring, at most its capacity.
    private void ReserveSwitches(int count) =>
        m_switches = Rings.Regrown(capacity: m_switchCapacity, count: m_switchCount, head: ref m_switchHead, needed: count, ring: m_switches);
    /// <summary>Expires records in sequence order, retaining original held identity separately.</summary>
    public void Expire(WorldInputInterval now) {
        var time = now.Time(default);

        while ((m_switchCount != 0) && ((time - SwitchRecord(index: 0).Interval.Time(default)) > m_retention)) {
            TouchRetained();
            m_switches[m_switchHead] = default;
            m_switchHead = ((m_switchHead + 1) % m_switches.Length);
            m_switchCount--;
        }
        while ((m_count != 0) && ((time - Record(index: 0).Capture.Time(Record(index: 0).Event.Offset)) > m_retention)) {
            TouchRetained();
            m_history[m_head] = default;
            m_head = ((m_head + 1) % m_history.Length);
            m_count--;
            Array.Clear(array: m_matchCached);
        }
    }
    /// <summary>Returns whether a batch with no event that carries <paramref name="image"/> and the contributions last
    /// accepted would be admitted without changing what the owner holds: the steady case of
    /// <see cref="TryAdmit"/>.</summary>
    /// <param name="image">The final image such a batch would carry.</param>
    /// <returns><see langword="true"/> when the owner holds that image, verified against its sources.</returns>
    public bool HoldsSteady(in PlayerIntent image) => (m_steadyImageVerified && (Image == image));
    /// <summary>Gets how many events' capture times this owner moved to the tick that admitted them.</summary>
    public long RetimedEvents { get; private set; }

    /// <summary>Preflights every source, timestamp and transition before atomically installing levels and history.</summary>
    /// <param name="batch">The batch to admit.</param>
    /// <param name="admission">The interval that admits the batch.</param>
    /// <param name="captureIntervals">The earlier intervals an event may still claim.</param>
    /// <param name="reason">The named refusal, on failure.</param>
    /// <param name="facing">Each crossing's authority orientation.</param>
    /// <param name="retime">Whether an event whose claimed capture time cannot be honoured is moved to
    /// <paramref name="admission"/>, at or after the last event admitted, instead of refusing the batch. A batch from
    /// another process claims times its sender could only estimate, so its authority decides them; a batch stamped in
    /// this process is refused.</param>
    /// <returns><see langword="true"/> when the batch was installed.</returns>
    public bool TryAdmit(in WorldInputBatch batch, WorldInputInterval admission, ReadOnlySpan<WorldInputInterval> captureIntervals, out string reason,
        ReadOnlySpan<int> facing = default, bool retime = false) {
        reason = string.Empty;
        if ((batch.Revision != Revision) || (batch.Epoch != Epoch) || (batch.Mapping != Mapping.Id) || (batch.Tick != admission.Tick) || (batch.NumericCount != Mapping.NumericCount)) {
            reason = $"input.mapping: stale revision, epoch, mapping or assigned tick (the batch names revision {batch.Revision} epoch {batch.Epoch} mapping {batch.Mapping} numerics {batch.NumericCount}{((batch.Tick != admission.Tick) ? $" tick {batch.Tick}" : string.Empty)}; the owner holds revision {Revision} epoch {Epoch} mapping {Mapping.Id} numerics {Mapping.NumericCount}{((batch.Tick != admission.Tick) ? $" tick {admission.Tick}" : string.Empty)})";
            return false;
        }
        if (m_usesFacing && (facing.Length != m_crossings.Length)) { reason = "input.facing: authority orientation is missing"; return false; }
        foreach (var orientation in facing) {
            if (orientation is < -1 or > 1) { reason = "input.facing: invalid authority orientation"; return false; }
        }
        Expire(now: admission);
        if (batch.CaptureLost) {
            RefusedBatches++;
            Switch(Mapping, admission, reset: true);
            reason = "input.capture: capture loss reset the owner";
            return false;
        }
        var stepEvents = ((m_step == admission.Tick) ? m_stepEvents : 0);

        if ((batch.Events.Length > (m_policy.MaximumEventsPerStep - stepEvents)) || (batch.Events.Length > Math.Max(val1: 0, val2: (m_policy.EventCapacity - m_count)))) {
            RefusedBatches++;
            Array.Clear(array: m_history);
            m_head = 0;
            m_count = 0;
            Switch(Mapping, admission, reset: true);
            reason = "input.capacity: atomic batch exceeds step or retained history capacity; owner reset";
            return false;
        }
        if (batch.Contributions.Length > m_sources.Length) { reason = "input.sources: contribution count exceeds admitted profile"; return false; }
        if (m_steadyImageVerified && batch.Events.IsEmpty && (batch.Image == Image) && ContributionsMatchAccepted(contributions: batch.Contributions.Span)) {
            m_step = admission.Tick;
            m_stepEvents = stepEvents;
            return true;
        }
        TouchRetained();
        Array.Clear(array: m_seen);
        // The fold reads only the live addresses, so only theirs start the batch at zero; a contribution to any other
        // address accumulates where nothing reads it.
        foreach (var channel in Mapping.Live) {
            var address = Mapping.Address(logicalId: channel);

            if (address < 0) { continue; }
            m_sum[address] = 0;
            m_maximum[address] = 0;
        }
        for (var index = 0; (index < m_sources.Length); index++) {
            m_sourceScratch[index] = m_sources[index] with { RawValue = 0 };
        }
        foreach (var contribution in batch.Contributions.Span) {
            if (!m_handles.TryGetValue(key: contribution.Handle, value: out var index) || m_seen[index]) { reason = "input.sources: unknown or duplicate source handle"; return false; }
            m_seen[index] = true;
            var previous = m_sources[index];

            if ((contribution.Channel != previous.Source.Channel) || (contribution.Kind != previous.Source.Kind)) { reason = "input.sources: registered source contract differs"; return false; }
            var channel = contribution.Channel;
            var address = Mapping.Address(logicalId: channel);

            if ((address < 0) && (contribution.RawValue != 0)) { reason = "input.inactive: source targets an inactive action"; return false; }
            if (previous.AwaitingNeutral && (contribution.RawValue != 0) && !contribution.NeutralObserved) { reason = "input.neutral: released source must report neutral before re-actuation"; return false; }
            var raw = contribution.RawValue;
            var shape = m_catalog.Channels[channel].Shape;

            if ((shape != ChannelShape.Relative) && ((raw > FixedQ4816.One.Value) || (raw < ((shape == ChannelShape.Bipolar) ? -FixedQ4816.One.Value : 0)))) {
                reason = "input.range: source value is outside its channel shape";
                return false;
            }
            m_sourceScratch[index] = new(previous.Source, raw, contribution.Activation, false);
            if (address < 0) { continue; }
            if (shape is ChannelShape.Relative or ChannelShape.Bipolar or ChannelShape.Binary) { m_sum[address] += raw; } else { m_maximum[address] = Math.Max(val1: m_maximum[address], val2: raw); }
        }
        // The fold writes one bank in place: an intent rebuilt per channel copies the whole bank twice for each.
        var bank = default(ChannelValues);

        foreach (var channel in Mapping.Live) {
            var address = Mapping.Address(logicalId: channel);

            if (address < 0) { continue; }
            var shape = m_catalog.Channels[channel].Shape;

            if (shape == ChannelShape.Relative) { bank.SetRelative(ordinal: address, value: Mapping.Channels.ClampRelative(ordinal: address, raw: m_sum[address])); } else {
                var raw = ((shape == ChannelShape.Unipolar) ? m_maximum[address] :
                    (long)Int128.Clamp(m_sum[address], ((shape == ChannelShape.Bipolar) ? -FixedQ4816.One.Value : 0), FixedQ4816.One.Value));

                bank[address] = Mapping.Channels.Quantize(ordinal: address, value: FixedQ4816.FromRawBits(value: raw));
            }
        }
        var folded = new PlayerIntent(Channels: bank, SourceRay: batch.Image.SourceRay);

        if (folded != batch.Image) { reason = "input.image: complete source fold differs from final image"; return false; }
        if ((Epoch > 1) && (LastSwitch.Tick == admission.Tick)) {
            if (!batch.Events.IsEmpty || (batch.Image != Image)) {
                reason = "input.barrier: fresh input for the new epoch starts on the following tick";
                return false;
            }
            for (var index = 0; (index < m_sources.Length); index++) {
                if ((m_sourceScratch[index].RawValue != m_sources[index].RawValue) ||
                    (m_sourceScratch[index].Activation != m_sources[index].Activation)) {
                    reason = "input.barrier: source continuation cannot change during the switch tick";
                    return false;
                }
            }
        }
        var eventImage = Image;
        var hasEvents = !batch.Events.IsEmpty;

        // The batch's holds overlay the accepted ones: an address the batch wrote reads its scratch entry, every other
        // address the accepted hold, and the commit copies only the addresses written.
        var holdsWritten = UInt128.Zero;

        if (hasEvents) {
            m_crossingHeld.CopyTo(array: m_crossingScratch, index: 0);
        }
        var sequence = m_sequence;
        var lastCapture = m_lastCapture;
        var lastOffset = m_lastOffset;
        var eventCount = 0;
        var moved = 0;

        foreach (var claimed in batch.Events.Span) {
            var input = claimed;

            if ((input.Channel >= m_catalog.Channels.Length) || !Mapping.IsLive(logicalId: input.Channel)) { reason = "input.event: unknown or inactive logical action"; return false; }
            if ((input.Sequence <= sequence) || (input.Kind > WorldInputEventKind.Text)) { reason = "input.sequence: duplicate, exhausted or invalid event kind"; return false; }
            var capture = default(WorldInputInterval);
            var found = false;

            if (input.CaptureTick == admission.Tick) { capture = admission; found = true; } else {
                foreach (var interval in captureIntervals) {
                    if (interval.Tick == input.CaptureTick) { capture = interval; found = true; break; }
                }
            }
            if (retime && !(
                found && (capture.Tick <= admission.Tick) && capture.Contains(offset: input.Offset) &&
                ((sequence == 0) || (capture.EngineStart > lastCapture.EngineStart) || ((capture.EngineStart == lastCapture.EngineStart) && (input.Offset >= lastOffset))) &&
                ((capture.Tick == admission.Tick) || ((admission.Time(default) - capture.Time(input.Offset)) <= m_lateness))
            )) {
                // The claim is late, ahead or out of order, so the event happened when it was admitted: in this
                // interval, no earlier than the last event, which keeps the checks below true by construction.
                capture = admission;
                input = input with {
                    CaptureTick = admission.Tick,
                    Offset = (((sequence != 0) && (lastCapture.EngineStart == admission.EngineStart)) ? lastOffset : default),
                };
                found = true;
                moved++;
            }
            if (!found || (capture.Tick > admission.Tick) || !capture.Contains(offset: input.Offset)) {
                reason = $"input.time: unknown, future or out-of-interval capture (captured at tick {input.CaptureTick}, admitted at tick {admission.Tick}, {captureIntervals.Length} earlier intervals held)";
                return false;
            }
            if ((sequence != 0) && ((capture.EngineStart < lastCapture.EngineStart) || ((capture.EngineStart == lastCapture.EngineStart) && (input.Offset < lastOffset)))) {
                reason = "input.time: capture must be monotonic with sequence";
                return false;
            }
            if ((capture.Tick != admission.Tick) && ((admission.Time(default) - capture.Time(input.Offset)) > m_lateness)) { reason = "input.late: capture exceeds the admitted lateness horizon"; return false; }
            var shape = m_catalog.Channels[input.Channel].Shape;
            var address = Mapping.Address(logicalId: input.Channel);
            var holdPress = 0UL;
            var id = checked(((m_eventId + ((ulong)eventCount)) + 1));

            switch (input.Kind) {
                case WorldInputEventKind.Press:
                case WorldInputEventKind.Release:
                    var press = (input.Kind == WorldInputEventKind.Press);
                    if ((shape != ChannelShape.Binary) || (input.RawValue != (press ? FixedQ4816.One.Value : 0)) ||
                        ((eventImage[address] != FixedQ4816.Zero) == press)) { reason = "input.transition: impossible press/release sequence"; return false; }
                    holdPress = (press ? id : (((holdsWritten & (UInt128.One << address)) != 0) ? HoldScratch[address] : m_holds[address]).Press);
                    eventImage = eventImage.WithChannel(ordinal: address, value: (press ? FixedQ4816.One : FixedQ4816.Zero));
                    break;
                case WorldInputEventKind.AxisSample:
                    if ((shape is not (ChannelShape.Bipolar or ChannelShape.Unipolar)) || (input.RawValue > FixedQ4816.One.Value) ||
                        (input.RawValue < ((shape == ChannelShape.Bipolar) ? -FixedQ4816.One.Value : 0))) { reason = "input.event: invalid normalized axis sample"; return false; }
                    eventImage = eventImage.WithChannel(ordinal: address, value: FixedQ4816.FromRawBits(value: input.RawValue));
                    break;
                case WorldInputEventKind.Text:
                    if ((shape != ChannelShape.Text) || (input.RawValue < 0) || (input.RawValue > 0x10ffff) || (input.RawValue is >= 0xd800 and <= 0xdfff)) {
                        reason = "input.text: expected one Unicode scalar on a live text channel";
                        return false;
                    }
                    break;
            }
            var record = new WorldInputRecord(id, Epoch, input, capture, admission, holdPress, Calibration: Calibration);

            if (!TryStageEvent(admission: admission, count: ref eventCount, prior: stepEvents, reason: out reason, record: record)) { return false; }
            if (input.Kind == WorldInputEventKind.AxisSample) {
                for (var crossing = 0; (crossing < m_crossings.Length); crossing++) {
                    var predicate = m_crossings[crossing];

                    if (predicate.Channel != input.Channel) { continue; }
                    var held = m_crossingScratch[crossing];
                    var orientation = ((m_policy.Crossings![crossing].FacingState is null) ? 1 : facing[crossing]);
                    var value = ((input.RawValue * predicate.Direction) * orientation);
                    var next = ((orientation != 0) && (held ? (value > (predicate.Release * predicate.Direction)) : (value >= (predicate.Press * predicate.Direction))));

                    if (next == held) { continue; }
                    m_crossingScratch[crossing] = next;
                    var derived = record with {
                        Id = checked(((m_eventId + ((ulong)eventCount)) + 1)),
                        Crossing = crossing,
                        Event = input with { Kind = WorldInputEventKind.AxisCrossing, RawValue = (next ? FixedQ4816.One.Value : 0) },
                    };

                    if (!TryStageEvent(admission: admission, count: ref eventCount, prior: stepEvents, reason: out reason, record: derived)) { return false; }
                }
            }
            if (input.Kind == WorldInputEventKind.Press) {
                HoldScratch[address] = new(input.Channel, id, record, false);
                holdsWritten |= (UInt128.One << address);
            } else if (input.Kind == WorldInputEventKind.Release) {
                HoldScratch[address] = default;
                holdsWritten |= (UInt128.One << address);
            }
            sequence = input.Sequence;
            lastCapture = capture;
            lastOffset = input.Offset;
        }
        foreach (var channel in Mapping.Live) {
            var address = Mapping.Address(logicalId: channel);

            if ((m_catalog.Channels[channel].Shape is ChannelShape.Binary or ChannelShape.Bipolar or ChannelShape.Unipolar) && (eventImage[address] != folded[address])) {
                reason = "input.image: ordered transitions disagree with final image";
                return false;
            }
        }
        var imageChanged = (folded != Image);

        // A batch changes a source's value, activation and neutral latch, never its registration, so only a source whose
        // state moved is written back.
        for (var index = 0; (index < m_sources.Length); index++) {
            ref readonly var next = ref m_sourceScratch[index];
            ref var accepted = ref m_sources[index];

            if ((next.RawValue != accepted.RawValue) || (next.Activation != accepted.Activation) || (next.AwaitingNeutral != accepted.AwaitingNeutral)) {
                accepted = next;
            }
        }
        if (hasEvents) {
            for (var written = holdsWritten; (written != 0); written &= (written - 1)) {
                var address = (int)UInt128.TrailingZeroCount(value: written);

                m_holds[address] = HoldScratch[address];
            }
            m_crossingScratch.CopyTo(array: m_crossingHeld, index: 0);
            ReserveHistory(count: (m_count + eventCount));
            for (var index = 0; (index < eventCount); index++) { m_history[((m_head + m_count++) % m_history.Length)] = m_eventScratch[index]; }
            m_sequence = sequence;
            m_lastCapture = lastCapture;
            m_lastOffset = lastOffset;
            m_eventId += ((ulong)eventCount);
            RetimedEvents += moved;
        }
        m_step = admission.Tick;
        m_stepEvents = (stepEvents + eventCount);
        Image = folded;
        m_steadyImageVerified = true;
        if (hasEvents || imageChanged) {
            Array.Clear(array: m_matchCached);
        }
        return true;
    }

    private bool ContributionsMatchAccepted(ReadOnlySpan<WorldInputContribution> contributions) {
        var cursor = 0;

        for (var index = 0; (index < m_sources.Length); index++) {
            ref readonly var state = ref m_sources[index];

            if ((cursor < contributions.Length) && (contributions[cursor].Handle == state.Source.Handle)) {
                ref readonly var contribution = ref contributions[cursor];

                if (contribution.NeutralObserved || state.AwaitingNeutral ||
                    (contribution.Channel != state.Source.Channel) ||
                    (contribution.Kind != state.Source.Kind) ||
                    (contribution.RawValue != state.RawValue) ||
                    (contribution.Activation != state.Activation)) {
                    return false;
                }
                cursor++;
            } else if (state.RawValue != 0) {
                return false;
            }
        }
        return (cursor == contributions.Length);
    }

    private bool TryStageEvent(WorldInputRecord record, ref int count, int prior, WorldInputInterval admission, out string reason) {
        if ((count >= (m_scratchCapacity - prior)) || (count >= Math.Max(val1: 0, val2: (m_policy.EventCapacity - m_count)))) {
            RefusedBatches++;
            Array.Clear(array: m_history);
            m_head = 0;
            m_count = 0;
            Switch(Mapping, admission, reset: true);
            reason = "input.capacity: derived events exceed step or retained history capacity; owner reset";
            return false;
        }
        if (count >= m_eventScratch.Length) {
            Array.Resize(array: ref m_eventScratch, newSize: ArrayCapacity.Grown(ceiling: m_scratchCapacity, current: m_eventScratch.Length, minimum: 8, required: (count + 1)));
        }
        m_eventScratch[count++] = record;
        reason = string.Empty;
        return true;
    }

    /// <summary>Requires neutral observations from a replaced profile before any source may act in its new meaning.</summary>
    public void RequireNeutralSources(WorldInputOwner previous) {
        TouchRetained();
        for (var index = 0; (index < m_sources.Length); index++) {
            m_sources[index] = m_sources[index] with { RawValue = 0, AwaitingNeutral = true };
        }
        Image = default;
        m_steadyImageVerified = false;
        Array.Clear(array: m_holds);
        Epoch = checked((previous.Epoch + 1));
        m_eventId = previous.m_eventId;
    }
    /// <summary>Replaces a source profile under the same input contract, preserving only identical source meanings.</summary>
    public void InheritProfile(WorldInputOwner previous, WorldInputInterval interval) {
        var checkpoint = previous.Capture();
        var sources = new WorldInputSourceState[m_sources.Length];
        var rebound = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var old in previous.m_sources) {
            if ((old.Source.Kind == WorldInputContributionKind.Level) &&
                !m_sources.Any(predicate: current => SameMeaning(left: old.Source, right: current.Source))) { rebound.Add(item: old.Source.Name); }
        }
        foreach (var current in m_sources) {
            if ((current.Source.Kind == WorldInputContributionKind.Level) &&
                !previous.m_sources.Any(predicate: old => SameMeaning(left: old.Source, right: current.Source))) { rebound.Add(item: current.Source.Name); }
        }
        for (var index = 0; (index < sources.Length); index++) {
            var target = m_sources[index].Source;

            sources[index] = new(Activation: 0, AwaitingNeutral: true, RawValue: 0, Source: target);
            foreach (var prior in previous.m_sources) {
                if (SameMeaning(left: prior.Source, right: target) && ((target.Kind != WorldInputContributionKind.Level) || !rebound.Contains(item: target.Name))) {
                    sources[index] = prior with { Source = target };
                    break;
                }
            }
        }
        Restore(checkpoint: checkpoint with { Revision = Revision, Sources = sources });
        Switch(Mapping, interval);

        static bool SameMeaning(WorldInputSource left, WorldInputSource right) => ((left.Name == right.Name) &&
            (left.Channel == right.Channel) && (left.Kind == right.Kind) && (left.Binding == right.Binding));
    }
    /// <summary>Installs a committed mode/dispatch boundary; source provenance is filtered before refolding.</summary>
    public void Switch(WorldInputMapping next, WorldInputInterval interval, ulong consumedActivation = 0, bool reset = false,
        scoped ReadOnlySpan<ulong> consumedActivations = default) {
        ReadOnlySpan<ulong> single = new(reference: in consumedActivation);

        if (consumedActivations.IsEmpty && (consumedActivation != 0)) { consumedActivations = single; }
        if (consumedActivations.Length > WorldInputSwitch.MaximumConsumedActivations) { throw new ArgumentException(message: "input boundary exceeds its activation budget", paramName: nameof(consumedActivations)); }
        var previous = Mapping;

        TouchRetained();
        Expire(now: interval);
        if (m_switchCount == m_switchCapacity) {
            RefusedBatches++;
            reset = true;
            Array.Clear(array: m_switches);
            m_switchHead = 0;
            m_switchCount = 0;
        }
        var image = default(PlayerIntent);

        Array.Clear(array: HoldScratch);
        Array.Clear(array: m_sum);
        Array.Clear(array: m_maximum);
        m_reboundActivations.Clear();
        m_reboundSources.Clear();
        foreach (var source in m_sources) {
            if ((source.Source.Kind == WorldInputContributionKind.Level) &&
                (source.RawValue != 0) && (reset || !m_catalog.Keeps(previous, next, source.Source.Channel))) {
                if (source.Activation != 0) { m_reboundActivations.Add(item: source.Activation); }
                m_reboundSources.Add(item: source.Source.Name);
            }
        }
        for (var index = 0; (index < m_sources.Length); index++) {
            var source = m_sources[index];
            var channel = source.Source.Channel;
            var keep = (!reset && m_catalog.Keeps(from: previous, logicalId: channel, to: next) && (source.Source.Kind != WorldInputContributionKind.Relative) &&
                ((source.Source.Kind != WorldInputContributionKind.Level) ||
                    (!m_reboundSources.Contains(item: source.Source.Name) && !m_reboundActivations.Contains(item: source.Activation))));

            if (!keep) {
                m_sources[index] = source with {
                    RawValue = 0,
                    AwaitingNeutral = (source.AwaitingNeutral || (source.RawValue != 0) ||
                    ((source.Source.Kind == WorldInputContributionKind.Level) && m_reboundSources.Contains(item: source.Source.Name))),
                };
                continue;
            }
            var address = next.Address(logicalId: channel);
            var shape = m_catalog.Channels[channel].Shape;

            if (shape is ChannelShape.Bipolar or ChannelShape.Binary) { m_sum[address] += source.RawValue; } else { m_maximum[address] = Math.Max(val1: m_maximum[address], val2: source.RawValue); }
            var hold = m_holds[previous.Address(logicalId: channel)];

            if ((source.RawValue != 0) && (hold.Press != 0)) {
                HoldScratch[address] = hold with { Consumed = (hold.Consumed || consumedActivations.Contains(value: hold.Origin.Event.Activation)) };
            }
        }
        foreach (var channel in next.Live) {
            var address = next.Address(logicalId: channel);

            if ((address < 0) || (m_catalog.Channels[channel].Shape == ChannelShape.Relative)) { continue; }
            var shape = m_catalog.Channels[channel].Shape;
            var raw = ((shape == ChannelShape.Unipolar) ? m_maximum[address] :
                (long)Int128.Clamp(m_sum[address], ((shape == ChannelShape.Bipolar) ? -FixedQ4816.One.Value : 0), FixedQ4816.One.Value));

            image = image.WithChannel(ordinal: address, value: next.Channels.Quantize(ordinal: address, value: FixedQ4816.FromRawBits(value: raw)));
            if (image[address] == FixedQ4816.Zero) { HoldScratch[address] = default; }
        }
        var cancellations = 0;

        foreach (var hold in m_holds) {
            if ((hold.Press != 0) && ((next.Address(logicalId: hold.Channel) < 0) || (HoldScratch[next.Address(logicalId: hold.Channel)].Press != hold.Press))) { cancellations++; }
        }
        if ((m_count + cancellations) > m_historyCapacity) {
            // A terminal boundary cannot be refused. Overflow resets all continuation and uses the reserved cancellation slots.
            RefusedBatches++;
            Array.Clear(array: m_history);
            m_head = 0;
            m_count = 0;
            Array.Clear(array: HoldScratch);
            image = default;
            reset = true;
            for (var index = 0; (index < m_sources.Length); index++) {
                var source = m_sources[index];

                m_sources[index] = source with { RawValue = 0, AwaitingNeutral = (source.AwaitingNeutral || (source.RawValue != 0)) };
            }
        }
        var epoch = checked((Epoch + 1));

        ReserveHistory(count: (m_count + cancellations));
        foreach (var hold in m_holds) {
            if ((hold.Press == 0) || ((next.Address(logicalId: hold.Channel) >= 0) && (HoldScratch[next.Address(logicalId: hold.Channel)].Press == hold.Press))) { continue; }
            var cause = (consumedActivations.Contains(value: hold.Origin.Event.Activation) ? hold.Origin.Event.Activation : 0);
            var input = new WorldInputEvent(hold.Channel, WorldInputEventKind.Cancel, 0, interval.Tick, default, 0, cause);

            m_history[((m_head + m_count++) % m_history.Length)] = new(checked(++m_eventId), epoch, input, interval, interval, hold.Press, Calibration: Calibration);
        }
        var decisions = new List<WorldInputContinuation>();

        foreach (var channel in previous.Live) {
            var address = previous.Address(logicalId: channel);

            if ((address < 0) || (Image[address].Value == 0)) { continue; }
            var target = next.Address(logicalId: channel);
            var raw = ((target >= 0) ? image[target].Value : 0);
            var hold = m_holds[address];
            var reason = (reset ? WorldInputContinuationReason.Reset : ((raw != 0) ? WorldInputContinuationReason.Kept :
                ((target < 0) ? WorldInputContinuationReason.Inactive : (!m_catalog.Keeps(from: previous, logicalId: channel, to: next) ? WorldInputContinuationReason.Unlisted :
                WorldInputContinuationReason.Rebound))));

            decisions.Add(item: new(channel, Image[address].Value, raw, reason, hold.Press, ((hold.Press == 0) ? default : hold.Origin.Time(policy: WorldInputJudgement.Capture))));
        }
        HoldScratch.CopyTo(array: m_holds, index: 0);
        Mapping = next;
        Image = image;
        m_steadyImageVerified = false;
        for (var crossing = 0; (crossing < m_crossings.Length); crossing++) {
            if (reset || !m_catalog.Keeps(previous, next, m_crossings[crossing].Channel)) { m_crossingHeld[crossing] = false; }
        }
        Epoch = epoch;
        m_sequence = 0;
        m_lastCapture = default;
        m_lastOffset = default;
        LastSwitch = new(interval, Epoch, previous.Id, next.Id, consumedActivations.ToArray(), reset, decisions.ToArray());
        ReserveSwitches(count: (m_switchCount + 1));
        m_switches[((m_switchHead + m_switchCount++) % m_switches.Length)] = LastSwitch;
        foreach (var claims in m_claimed) { Array.Clear(array: claims); }
        Array.Clear(array: m_matchCached);
    }
    /// <summary>Changes calibration at an unconditional epoch boundary; historical records keep their admitted value.</summary>
    public bool TryCalibrate(FixedQ4816 value, WorldInputInterval interval) {
        if ((value < -m_calibrationLimit) || (value > m_calibrationLimit)) { return false; }
        if (value == Calibration) { return true; }
        Switch(Mapping, interval, reset: true);
        Calibration = value;
        return true;
    }

    private static FixedQ4816 Parse(decimal value) => FixedQ4816.Parse(value.ToString(provider: CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
