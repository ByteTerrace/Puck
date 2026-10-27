using System.Globalization;
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
    private readonly WorldInputRecord[] m_history;
    private readonly WorldInputSwitch[] m_switches;
    private int m_switchHead;
    private int m_switchCount;
    private readonly WorldInputRecord[] m_eventScratch;
    private readonly (ushort Channel, long Press, long Release, int Direction)[] m_crossings;
    private readonly bool[] m_crossingHeld;
    private readonly bool[] m_crossingScratch;
    private readonly bool m_usesFacing;
    private readonly WorldInputHold[] m_holds = new WorldInputHold[ChannelLimits.MaxChannels];
    private readonly WorldInputHold[] m_holdScratch = new WorldInputHold[ChannelLimits.MaxChannels];
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

    /// <summary>Admits an owner and allocates its bounded storage. Source handles are immutable until a reset/re-admission.</summary>
    public WorldInputOwner(WorldInputCatalog catalog, WorldInputMapping mapping, WorldInputHistory policy,
        IReadOnlyList<WorldInputSource> sources, uint revision = 1, IReadOnlyList<WorldInputPattern>? patterns = null) {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (sources.Count > WorldInputCatalog.MaximumSources) { throw new ArgumentException($"input sources exceed {WorldInputCatalog.MaximumSources}", nameof(sources)); }
        m_catalog = catalog;
        Mapping = mapping;
        Revision = revision;
        m_policy = policy;
        m_retention = Parse(policy.RetentionSeconds);
        m_lateness = Parse(policy.MaximumLatenessSeconds);
        m_calibrationLimit = Parse(policy.CalibrationLimitSeconds);
        m_sources = new WorldInputSourceState[sources.Count];
        m_sourceScratch = new WorldInputSourceState[sources.Count];
        m_seen = new bool[sources.Count];
        m_handles = new Dictionary<ushort, int>(sources.Count);
        m_reboundActivations = new HashSet<ulong>(sources.Count);
        m_reboundSources = new HashSet<string>(sources.Count, StringComparer.Ordinal);
        var identities = new HashSet<(string, ushort)>();
        for (var index = 0; index < sources.Count; index++) {
            var source = sources[index];
            if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 256 || source.Binding is null || source.Binding.Length > 4096 ||
                !identities.Add((source.Name, source.Channel)) || source.Channel >= catalog.Channels.Length || !m_handles.TryAdd(source.Handle, index) ||
                !Enum.IsDefined(source.Kind) || catalog.Channels[source.Channel].Shape == ChannelShape.Text ||
                ((source.Kind == WorldInputContributionKind.Relative) != (catalog.Channels[source.Channel].Shape == ChannelShape.Relative))) {
                throw new ArgumentException($"input source {source.Handle} has an invalid channel, kind or duplicate handle", nameof(sources));
            }
            m_sources[index] = new(source, 0, 0, false);
        }
        m_history = new WorldInputRecord[catalog.HistoryCapacity(policy.EventCapacity)];
        m_switches = new WorldInputSwitch[policy.EventCapacity];
        m_eventScratch = new WorldInputRecord[policy.MaximumEventsPerStep];
        m_patterns = CompiledWorldInputPattern.Compile(catalog, policy, patterns ?? []);
        var stages = m_patterns.Length == 0 ? 0 : m_patterns.Max(static row => row.Stages.Length);
        m_matchWidth = m_patterns.Length == 0 ? 0 : m_patterns.Max(static row => row.Width);
        var matchSize = checked(stages * m_history.Length * m_matchWidth);
        if (matchSize > 1024 * 1024) { throw new ArgumentException("inputPatterns matching scratch exceeds 4 MiB per owner"); }
        m_matchPaths = new int[matchSize];
        m_matchValid = new bool[stages * m_history.Length];
        m_matchNext = new int[m_matchWidth * m_history.Length];
        m_matchPrevious = new int[m_matchWidth * m_history.Length];
        m_matchCache = new WorldInputMatch[m_patterns.Length];
        m_matchCached = new bool[m_patterns.Length];
        m_claimed = new ulong[m_patterns.Length == 0 ? 0 : m_patterns.Max(static row => row.Group) + 1][];
        for (var group = 0; group < m_claimed.Length; group++) { m_claimed[group] = new ulong[m_history.Length]; }
        m_crossings = new (ushort, long, long, int)[policy.Crossings?.Count ?? 0];
        m_crossingHeld = new bool[m_crossings.Length];
        m_crossingScratch = new bool[m_crossings.Length];
        var crossingNames = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < m_crossings.Length; index++) {
            var row = policy.Crossings![index];
            m_usesFacing |= row.FacingState is not null;
            if (string.IsNullOrWhiteSpace(row.Name) || !crossingNames.Add(row.Name) ||
                !catalog.TryGetLogicalId(row.Channel, out var channel) ||
                catalog.Channels[channel].Shape is not (ChannelShape.Bipolar or ChannelShape.Unipolar) ||
                row.Direction is not (1 or -1) || row.Press * row.Direction <= row.Release * row.Direction ||
                row.Press > 1 || row.Release > 1 || row.Press < (catalog.Channels[channel].Shape == ChannelShape.Unipolar ? 0 : -1) ||
                row.Release < (catalog.Channels[channel].Shape == ChannelShape.Unipolar ? 0 : -1)) {
                throw new ArgumentException($"inputHistory crossing '{row.Name}' has invalid channel, thresholds or direction", nameof(policy));
            }
            m_crossings[index] = (channel, Parse(row.Press).Value, Parse(row.Release).Value, row.Direction);
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
        return m_switches[(m_switchHead + index) % m_switches.Length];
    }
    /// <summary>Capacity outcomes, retained independently of ring eviction.</summary>
    public ulong RefusedBatches { get; private set; }
    /// <summary>Accepted source/rearm state, for publication and capture.</summary>
    public ReadOnlySpan<WorldInputSourceState> Sources => m_sources;
    /// <summary>Reads a retained record in oldest-first order.</summary>
    public WorldInputRecord Record(int index) {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, m_count);
        return m_history[(m_head + index) % m_history.Length];
    }
    /// <summary>Reads the original held press without exposing it as a fresh edge.</summary>
    public bool TryHeld(ushort channel, out WorldInputHold hold) {
        var address = Mapping.Address(channel);
        hold = address >= 0 ? m_holds[address] : default;
        return hold.Press != 0 && Image[address] != FixedQ4816.Zero;
    }
    /// <summary>Returns an indexed newest matching event in the current epoch; missing time is never zero.</summary>
    public bool TryEvent(ushort channel, WorldInputEventKind kind, int occurrence, out WorldInputRecord record) {
        if (occurrence >= 0) {
            for (var index = m_count - 1; index >= 0; index--) {
                var candidate = Record(index);
                if (candidate.Epoch == Epoch && candidate.Event.Channel == channel && candidate.Event.Kind == kind && occurrence-- == 0) {
                    record = candidate;
                    return true;
                }
            }
        }
        record = default;
        return false;
    }
    /// <summary>Expires records in sequence order, retaining original held identity separately.</summary>
    public void Expire(WorldInputInterval now) {
        var time = now.Time(default);
        while (m_switchCount != 0 && time - SwitchRecord(0).Interval.Time(default) > m_retention) {
            m_switches[m_switchHead] = default;
            m_switchHead = (m_switchHead + 1) % m_switches.Length;
            m_switchCount--;
        }
        while (m_count != 0 && time - Record(0).Capture.Time(Record(0).Event.Offset) > m_retention) {
            m_history[m_head] = default;
            m_head = (m_head + 1) % m_history.Length;
            m_count--;
            Array.Clear(m_matchCached);
        }
    }

    /// <summary>Preflights every source, timestamp and transition before atomically installing levels and history.</summary>
    public bool TryAdmit(in WorldInputBatch batch, WorldInputInterval admission, ReadOnlySpan<WorldInputInterval> captureIntervals, out string reason,
        ReadOnlySpan<int> facing = default) {
        reason = string.Empty;
        if (batch.Revision != Revision || batch.Epoch != Epoch || batch.Mapping != Mapping.Id || batch.Tick != admission.Tick || batch.NumericCount != Mapping.NumericCount) {
            reason = "input.mapping: stale revision, epoch, mapping or assigned tick";
            return false;
        }
        if (m_usesFacing && facing.Length != m_crossings.Length) { reason = "input.facing: authority orientation is missing"; return false; }
        foreach (var orientation in facing) {
            if (orientation is < -1 or > 1) { reason = "input.facing: invalid authority orientation"; return false; }
        }
        Expire(admission);
        if (batch.CaptureLost) {
            RefusedBatches++;
            Switch(Mapping, admission, reset: true);
            reason = "input.capture: capture loss reset the owner";
            return false;
        }
        var stepEvents = m_step == admission.Tick ? m_stepEvents : 0;
        if (batch.Events.Length > m_policy.MaximumEventsPerStep - stepEvents || batch.Events.Length > Math.Max(0, m_policy.EventCapacity - m_count)) {
            RefusedBatches++;
            Array.Clear(m_history);
            m_head = 0;
            m_count = 0;
            Switch(Mapping, admission, reset: true);
            reason = "input.capacity: atomic batch exceeds step or retained history capacity; owner reset";
            return false;
        }
        if (batch.Contributions.Length > m_sources.Length) { reason = "input.sources: contribution count exceeds admitted profile"; return false; }
        Array.Clear(m_seen);
        Array.Clear(m_sum);
        Array.Clear(m_maximum);
        for (var index = 0; index < m_sources.Length; index++) {
            m_sourceScratch[index] = m_sources[index] with { RawValue = 0 };
        }
        foreach (var contribution in batch.Contributions.Span) {
            if (!m_handles.TryGetValue(contribution.Handle, out var index) || m_seen[index]) { reason = "input.sources: unknown or duplicate source handle"; return false; }
            m_seen[index] = true;
            var previous = m_sources[index];
            if (contribution.Channel != previous.Source.Channel || contribution.Kind != previous.Source.Kind) { reason = "input.sources: registered source contract differs"; return false; }
            var channel = contribution.Channel;
            var address = Mapping.Address(channel);
            if (address < 0 && contribution.RawValue != 0) { reason = "input.inactive: source targets an inactive action"; return false; }
            if (previous.AwaitingNeutral && contribution.RawValue != 0 && !contribution.NeutralObserved) { reason = "input.neutral: released source must report neutral before re-actuation"; return false; }
            var raw = contribution.RawValue;
            var shape = m_catalog.Channels[channel].Shape;
            if (shape != ChannelShape.Relative && (raw > FixedQ4816.One.Value || raw < (shape == ChannelShape.Bipolar ? -FixedQ4816.One.Value : 0))) {
                reason = "input.range: source value is outside its channel shape";
                return false;
            }
            m_sourceScratch[index] = new(previous.Source, raw, contribution.Activation, false);
            if (address < 0) { continue; }
            if (shape is ChannelShape.Relative or ChannelShape.Bipolar or ChannelShape.Binary) { m_sum[address] += raw; }
            else { m_maximum[address] = Math.Max(m_maximum[address], raw); }
        }
        var folded = new PlayerIntent(default, batch.Image.SourceRay);
        foreach (var channel in Mapping.Live) {
            var address = Mapping.Address(channel);
            if (address < 0) { continue; }
            var shape = m_catalog.Channels[channel].Shape;
            if (shape == ChannelShape.Relative) { folded = folded.WithRelative(address, Mapping.Channels.ClampRelative(address, m_sum[address])); }
            else {
                var raw = shape == ChannelShape.Unipolar ? m_maximum[address] :
                    (long)Int128.Clamp(m_sum[address], shape == ChannelShape.Bipolar ? -FixedQ4816.One.Value : 0, FixedQ4816.One.Value);
                folded = folded.WithChannel(address, Mapping.Channels.Quantize(address, FixedQ4816.FromRawBits(raw)));
            }
        }
        if (folded != batch.Image) { reason = "input.image: complete source fold differs from final image"; return false; }
        var eventImage = Image;
        m_holds.CopyTo(m_holdScratch, 0);
        m_crossingHeld.CopyTo(m_crossingScratch, 0);
        var sequence = m_sequence;
        var lastCapture = m_lastCapture;
        var lastOffset = m_lastOffset;
        var eventCount = 0;
        foreach (var input in batch.Events.Span) {
            if (input.Channel >= m_catalog.Channels.Length || !Mapping.IsLive(input.Channel)) { reason = "input.event: unknown or inactive logical action"; return false; }
            if (input.Sequence <= sequence || input.Kind > WorldInputEventKind.Text) { reason = "input.sequence: duplicate, exhausted or invalid event kind"; return false; }
            var capture = default(WorldInputInterval);
            var found = false;
            if (input.CaptureTick == admission.Tick) { capture = admission; found = true; }
            else {
                foreach (var interval in captureIntervals) {
                    if (interval.Tick == input.CaptureTick) { capture = interval; found = true; break; }
                }
            }
            if (!found || capture.Tick > admission.Tick || !capture.Contains(input.Offset)) { reason = "input.time: unknown, future or out-of-interval capture"; return false; }
            if (sequence != 0 && (capture.EngineStart < lastCapture.EngineStart || (capture.EngineStart == lastCapture.EngineStart && input.Offset < lastOffset))) {
                reason = "input.time: capture must be monotonic with sequence";
                return false;
            }
            if (capture.Tick != admission.Tick && admission.Time(default) - capture.Time(input.Offset) > m_lateness) { reason = "input.late: capture exceeds the admitted lateness horizon"; return false; }
            var shape = m_catalog.Channels[input.Channel].Shape;
            var address = Mapping.Address(input.Channel);
            var holdPress = 0UL;
            var id = checked(m_eventId + (ulong)eventCount + 1);
            switch (input.Kind) {
                case WorldInputEventKind.Press:
                case WorldInputEventKind.Release:
                    var press = input.Kind == WorldInputEventKind.Press;
                    if (shape != ChannelShape.Binary || input.RawValue != (press ? FixedQ4816.One.Value : 0) ||
                        (eventImage[address] != FixedQ4816.Zero) == press) { reason = "input.transition: impossible press/release sequence"; return false; }
                    holdPress = press ? id : m_holdScratch[address].Press;
                    eventImage = eventImage.WithChannel(address, press ? FixedQ4816.One : FixedQ4816.Zero);
                    break;
                case WorldInputEventKind.AxisSample:
                    if (shape is not (ChannelShape.Bipolar or ChannelShape.Unipolar) || input.RawValue > FixedQ4816.One.Value ||
                        input.RawValue < (shape == ChannelShape.Bipolar ? -FixedQ4816.One.Value : 0)) { reason = "input.event: invalid normalized axis sample"; return false; }
                    eventImage = eventImage.WithChannel(address, FixedQ4816.FromRawBits(input.RawValue));
                    break;
                case WorldInputEventKind.Text:
                    if (shape != ChannelShape.Text || input.RawValue < 0 || input.RawValue > 0x10ffff || input.RawValue is >= 0xd800 and <= 0xdfff) {
                        reason = "input.text: expected one Unicode scalar on a live text channel";
                        return false;
                    }
                    break;
            }
            var record = new WorldInputRecord(id, Epoch, input, capture, admission, holdPress, Calibration: Calibration);
            if (!TryStageEvent(record, ref eventCount, stepEvents, admission, out reason)) { return false; }
            if (input.Kind == WorldInputEventKind.AxisSample) {
                for (var crossing = 0; crossing < m_crossings.Length; crossing++) {
                    var predicate = m_crossings[crossing];
                    if (predicate.Channel != input.Channel) { continue; }
                    var held = m_crossingScratch[crossing];
                    var orientation = m_policy.Crossings![crossing].FacingState is null ? 1 : facing[crossing];
                    var value = input.RawValue * predicate.Direction * orientation;
                    var next = orientation != 0 && (held ? value > predicate.Release * predicate.Direction : value >= predicate.Press * predicate.Direction);
                    if (next == held) { continue; }
                    m_crossingScratch[crossing] = next;
                    var derived = record with { Id = checked(m_eventId + (ulong)eventCount + 1), Crossing = crossing,
                        Event = input with { Kind = WorldInputEventKind.AxisCrossing, RawValue = next ? FixedQ4816.One.Value : 0 } };
                    if (!TryStageEvent(derived, ref eventCount, stepEvents, admission, out reason)) { return false; }
                }
            }
            if (input.Kind == WorldInputEventKind.Press) { m_holdScratch[address] = new(input.Channel, id, record, false); }
            else if (input.Kind == WorldInputEventKind.Release) { m_holdScratch[address] = default; }
            sequence = input.Sequence;
            lastCapture = capture;
            lastOffset = input.Offset;
        }
        foreach (var channel in Mapping.Live) {
            var address = Mapping.Address(channel);
            if (m_catalog.Channels[channel].Shape is ChannelShape.Binary or ChannelShape.Bipolar or ChannelShape.Unipolar && eventImage[address] != folded[address]) {
                reason = "input.image: ordered transitions disagree with final image";
                return false;
            }
        }
        m_sourceScratch.CopyTo(m_sources, 0);
        m_holdScratch.CopyTo(m_holds, 0);
        m_crossingScratch.CopyTo(m_crossingHeld, 0);
        for (var index = 0; index < eventCount; index++) { m_history[(m_head + m_count++) % m_history.Length] = m_eventScratch[index]; }
        m_sequence = sequence;
        m_lastCapture = lastCapture;
        m_lastOffset = lastOffset;
        m_eventId += (ulong)eventCount;
        m_step = admission.Tick;
        m_stepEvents = stepEvents + eventCount;
        Image = folded;
        Array.Clear(m_matchCached);
        return true;
    }

    private bool TryStageEvent(WorldInputRecord record, ref int count, int prior, WorldInputInterval admission, out string reason) {
        if (count >= m_eventScratch.Length - prior || count >= Math.Max(0, m_policy.EventCapacity - m_count)) {
            RefusedBatches++;
            Array.Clear(m_history);
            m_head = 0;
            m_count = 0;
            Switch(Mapping, admission, reset: true);
            reason = "input.capacity: derived events exceed step or retained history capacity; owner reset";
            return false;
        }
        m_eventScratch[count++] = record;
        reason = string.Empty;
        return true;
    }

    /// <summary>Requires neutral observations from a replaced profile before any source may act in its new meaning.</summary>
    public void RequireNeutralSources(WorldInputOwner previous) {
        for (var index = 0; index < m_sources.Length; index++) {
            m_sources[index] = m_sources[index] with { RawValue = 0, AwaitingNeutral = true };
        }
        Image = default;
        Array.Clear(m_holds);
        Epoch = checked(previous.Epoch + 1);
        m_eventId = previous.m_eventId;
    }

    /// <summary>Replaces a source profile under the same input contract, preserving only identical source meanings.</summary>
    public void InheritProfile(WorldInputOwner previous, WorldInputInterval interval) {
        var checkpoint = previous.Capture();
        var sources = new WorldInputSourceState[m_sources.Length];
        var rebound = new HashSet<string>(StringComparer.Ordinal);
        foreach (var old in previous.m_sources) {
            if (old.Source.Kind == WorldInputContributionKind.Level &&
                !m_sources.Any(current => SameMeaning(old.Source, current.Source))) { rebound.Add(old.Source.Name); }
        }
        foreach (var current in m_sources) {
            if (current.Source.Kind == WorldInputContributionKind.Level &&
                !previous.m_sources.Any(old => SameMeaning(old.Source, current.Source))) { rebound.Add(current.Source.Name); }
        }
        for (var index = 0; index < sources.Length; index++) {
            var target = m_sources[index].Source;
            sources[index] = new(target, 0, 0, true);
            foreach (var prior in previous.m_sources) {
                if (SameMeaning(prior.Source, target) && (target.Kind != WorldInputContributionKind.Level || !rebound.Contains(target.Name))) {
                    sources[index] = prior with { Source = target };
                    break;
                }
            }
        }
        Restore(checkpoint with { Revision = Revision, Sources = sources });
        Switch(Mapping, interval);

        static bool SameMeaning(WorldInputSource left, WorldInputSource right) => left.Name == right.Name &&
            left.Channel == right.Channel && left.Kind == right.Kind && left.Binding == right.Binding;
    }

    /// <summary>Installs a committed mode/dispatch boundary; source provenance is filtered before refolding.</summary>
    public void Switch(WorldInputMapping next, WorldInputInterval interval, ulong consumedActivation = 0, bool reset = false,
        scoped ReadOnlySpan<ulong> consumedActivations = default) {
        ReadOnlySpan<ulong> single = new(in consumedActivation);
        if (consumedActivations.IsEmpty && consumedActivation != 0) { consumedActivations = single; }
        if (consumedActivations.Length > WorldInputSwitch.MaximumConsumedActivations) { throw new ArgumentException("input boundary exceeds its activation budget", nameof(consumedActivations)); }
        var previous = Mapping;
        Expire(interval);
        if (m_switchCount == m_switches.Length) {
            RefusedBatches++;
            reset = true;
            Array.Clear(m_switches);
            m_switchHead = 0;
            m_switchCount = 0;
        }
        var image = default(PlayerIntent);
        Array.Clear(m_holdScratch);
        Array.Clear(m_sum);
        Array.Clear(m_maximum);
        m_reboundActivations.Clear();
        m_reboundSources.Clear();
        foreach (var source in m_sources) {
            if (source.Source.Kind == WorldInputContributionKind.Level &&
                source.RawValue != 0 && (reset || !m_catalog.Keeps(previous, next, source.Source.Channel))) {
                if (source.Activation != 0) { m_reboundActivations.Add(source.Activation); }
                m_reboundSources.Add(source.Source.Name);
            }
        }
        for (var index = 0; index < m_sources.Length; index++) {
            var source = m_sources[index];
            var channel = source.Source.Channel;
            var keep = !reset && m_catalog.Keeps(previous, next, channel) && source.Source.Kind != WorldInputContributionKind.Relative &&
                (source.Source.Kind != WorldInputContributionKind.Level ||
                    !m_reboundSources.Contains(source.Source.Name) && !m_reboundActivations.Contains(source.Activation));
            if (!keep) {
                m_sources[index] = source with { RawValue = 0, AwaitingNeutral = source.AwaitingNeutral || source.RawValue != 0 ||
                    source.Source.Kind == WorldInputContributionKind.Level && m_reboundSources.Contains(source.Source.Name) };
                continue;
            }
            var address = next.Address(channel);
            var shape = m_catalog.Channels[channel].Shape;
            if (shape is ChannelShape.Bipolar or ChannelShape.Binary) { m_sum[address] += source.RawValue; }
            else { m_maximum[address] = Math.Max(m_maximum[address], source.RawValue); }
            var hold = m_holds[previous.Address(channel)];
            if (source.RawValue != 0 && hold.Press != 0) {
                m_holdScratch[address] = hold with { Consumed = hold.Consumed || consumedActivations.Contains(hold.Origin.Event.Activation) };
            }
        }
        foreach (var channel in next.Live) {
            var address = next.Address(channel);
            if (address < 0 || m_catalog.Channels[channel].Shape == ChannelShape.Relative) { continue; }
            var shape = m_catalog.Channels[channel].Shape;
            var raw = shape == ChannelShape.Unipolar ? m_maximum[address] :
                (long)Int128.Clamp(m_sum[address], shape == ChannelShape.Bipolar ? -FixedQ4816.One.Value : 0, FixedQ4816.One.Value);
            image = image.WithChannel(address, next.Channels.Quantize(address, FixedQ4816.FromRawBits(raw)));
            if (image[address] == FixedQ4816.Zero) { m_holdScratch[address] = default; }
        }
        var cancellations = 0;
        foreach (var hold in m_holds) {
            if (hold.Press != 0 && (next.Address(hold.Channel) < 0 || m_holdScratch[next.Address(hold.Channel)].Press != hold.Press)) { cancellations++; }
        }
        if (m_count + cancellations > m_history.Length) {
            // A terminal boundary cannot be refused. Overflow resets all continuation and uses the reserved cancellation slots.
            RefusedBatches++;
            Array.Clear(m_history);
            m_head = 0;
            m_count = 0;
            Array.Clear(m_holdScratch);
            image = default;
            reset = true;
            for (var index = 0; index < m_sources.Length; index++) {
                var source = m_sources[index];
                m_sources[index] = source with { RawValue = 0, AwaitingNeutral = source.AwaitingNeutral || source.RawValue != 0 };
            }
        }
        var epoch = checked(Epoch + 1);
        foreach (var hold in m_holds) {
            if (hold.Press == 0 || next.Address(hold.Channel) >= 0 && m_holdScratch[next.Address(hold.Channel)].Press == hold.Press) { continue; }
            var cause = consumedActivations.Contains(hold.Origin.Event.Activation) ? hold.Origin.Event.Activation : 0;
            var input = new WorldInputEvent(hold.Channel, WorldInputEventKind.Cancel, 0, interval.Tick, default, 0, cause);
            m_history[(m_head + m_count++) % m_history.Length] = new(checked(++m_eventId), epoch, input, interval, interval, hold.Press, Calibration: Calibration);
        }
        var decisions = new List<WorldInputContinuation>();
        foreach (var channel in previous.Live) {
            var address = previous.Address(channel);
            if (address < 0 || Image[address].Value == 0) { continue; }
            var target = next.Address(channel);
            var raw = target >= 0 ? image[target].Value : 0;
            var hold = m_holds[address];
            var reason = reset ? WorldInputContinuationReason.Reset : raw != 0 ? WorldInputContinuationReason.Kept :
                target < 0 ? WorldInputContinuationReason.Inactive : !m_catalog.Keeps(previous, next, channel) ? WorldInputContinuationReason.Unlisted :
                WorldInputContinuationReason.Rebound;
            decisions.Add(new(channel, Image[address].Value, raw, reason, hold.Press, hold.Press == 0 ? default : hold.Origin.Time(WorldInputJudgement.Capture)));
        }
        m_holdScratch.CopyTo(m_holds, 0);
        Mapping = next;
        Image = image;
        for (var crossing = 0; crossing < m_crossings.Length; crossing++) {
            if (reset || !m_catalog.Keeps(previous, next, m_crossings[crossing].Channel)) { m_crossingHeld[crossing] = false; }
        }
        Epoch = epoch;
        m_sequence = 0;
        m_lastCapture = default;
        m_lastOffset = default;
        LastSwitch = new(interval, Epoch, previous.Id, next.Id, consumedActivations.ToArray(), reset, decisions.ToArray());
        m_switches[(m_switchHead + m_switchCount++) % m_switches.Length] = LastSwitch;
        foreach (var claims in m_claimed) { Array.Clear(claims); }
        Array.Clear(m_matchCached);
    }
    /// <summary>Changes calibration at an unconditional epoch boundary; historical records keep their admitted value.</summary>
    public bool TryCalibrate(FixedQ4816 value, WorldInputInterval interval) {
        if (value < -m_calibrationLimit || value > m_calibrationLimit) { return false; }
        if (value == Calibration) { return true; }
        Switch(Mapping, interval, reset: true);
        Calibration = value;
        return true;
    }
    private static FixedQ4816 Parse(decimal value) => FixedQ4816.Parse(value.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
