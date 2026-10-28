using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>Produces ordered semantic input from an admitted source profile, before tick images can lose edges.</summary>
/// <remarks>Published batches own immutable arrays. An unchanged held tick reuses its source image and allocates nothing.</remarks>
public sealed class WorldInputProducer {
    private readonly WorldInputCatalog m_catalog;
    private readonly WorldInputSourceState[] m_sources;
    private readonly Dictionary<ushort, int> m_handles;
    private readonly WorldInputEvent[] m_pending;
    private readonly bool[] m_neutral;
    private readonly bool[] m_observed;
    private readonly long[] m_observedRaw;
    private readonly bool[] m_reconciledNeutral;

    private bool m_needsNeutral;

    private readonly DeferredObservation[] m_deferred;

    private int m_deferredCount;

    private readonly record struct DeferredObservation(ushort Handle, long Raw, string? Text, ulong Tick, FixedQ4816 Offset, ulong Activation);

    private WorldInputContribution[] m_published = [];

    private readonly WorldInputContribution[] m_borrowed;

    private int m_eventCount;
    private uint m_sequence;
    private bool m_sourcesChanged = true;
    private PlayerIntent m_image;

    /// <summary>Creates one bounded producer from the profile admitted by its authority.</summary>
    public WorldInputProducer(WorldInputCatalog catalog, WorldInputMapping mapping, IReadOnlyList<WorldInputSource> sources, int maximumEventsPerStep,
        uint revision = 1, ulong epoch = 1) {
        if ((sources.Count > WorldInputCatalog.MaximumSources) || (maximumEventsPerStep is < 1 or > ushort.MaxValue) || (revision == 0) || (epoch == 0)) {
            throw new ArgumentException(message: "input producer exceeds its admitted bounds");
        }
        m_catalog = catalog;
        Mapping = mapping;
        Revision = revision;
        Epoch = epoch;
        m_pending = new WorldInputEvent[maximumEventsPerStep];
        m_deferred = new DeferredObservation[maximumEventsPerStep];
        m_sources = new WorldInputSourceState[sources.Count];
        m_borrowed = new WorldInputContribution[sources.Count];
        m_neutral = new bool[sources.Count];
        m_observed = new bool[sources.Count];
        m_observedRaw = new long[sources.Count];
        m_reconciledNeutral = new bool[sources.Count];
        m_handles = new(capacity: sources.Count);
        var identities = new HashSet<(string, ushort)>();

        for (var index = 0; (index < sources.Count); index++) {
            var source = sources[index];

            if (string.IsNullOrWhiteSpace(value: source.Name) || (source.Name.Length > 256) || (source.Binding is null) || (source.Binding.Length > 4096) ||
                !identities.Add(item: (source.Name, source.Channel)) || (source.Channel >= catalog.Channels.Length) || !m_handles.TryAdd(key: source.Handle, value: index) ||
                !Enum.IsDefined(value: source.Kind) || (catalog.Channels[source.Channel].Shape == ChannelShape.Text) ||
                ((source.Kind == WorldInputContributionKind.Relative) != (catalog.Channels[source.Channel].Shape == ChannelShape.Relative))) {
                throw new ArgumentException(message: "input producer source profile has an invalid contract");
            }
            m_sources[index] = new(Activation: 0, AwaitingNeutral: false, RawValue: 0, Source: source);
        }
    }

    /// <summary>The last authority-published mapping.</summary>
    public WorldInputMapping Mapping { get; private set; }
    /// <summary>The last authority-published mapping revision.</summary>
    public uint Revision { get; private set; }
    /// <summary>The last authority-published epoch.</summary>
    public ulong Epoch { get; private set; }
    /// <summary>A capture overflow requires an authority reset; no truncated batch is published.</summary>
    public bool CaptureLost { get; private set; }

    /// <summary>Retains actual observations when recompiling the same attachment; pending old-epoch events are not copied.</summary>
    public void InheritObservations(WorldInputProducer previous) {
        if (previous.CaptureLost) { return; }
        var observations = new Dictionary<(string Name, string Channel, WorldInputContributionKind Kind, string Binding), int>();

        for (var index = 0; (index < previous.m_sources.Length); index++) {
            var source = previous.m_sources[index].Source;

            if (previous.m_observed[index]) {
                observations.Add(key: (source.Name, previous.m_catalog.Channels[source.Channel].Name, source.Kind, source.Binding), value: index);
            }
        }
        for (var index = 0; (index < m_sources.Length); index++) {
            var source = m_sources[index].Source;

            if (!observations.TryGetValue(key: (source.Name, m_catalog.Channels[source.Channel].Name, source.Kind, source.Binding), value: out var prior)) { continue; }
            m_observed[index] = true;
            m_observedRaw[index] = previous.m_observedRaw[prior];
        }
    }
    /// <summary>Resumes a synthetic producer from the authority's accepted image and sequence.</summary>
    /// <remarks>A fresh physical attachment instead requests an authority reset; it cannot claim these observations.</remarks>
    public static WorldInputProducer FromAccepted(WorldInputCatalog catalog, WorldInputMapping mapping,
        ReadOnlySpan<WorldInputSourceState> sources, int maximumEventsPerStep, uint revision, ulong epoch,
        uint sequence, PlayerIntent image) {
        var profile = new WorldInputSource[sources.Length];

        for (var index = 0; (index < profile.Length); index++) { profile[index] = sources[index].Source; }
        var producer = new WorldInputProducer(catalog: catalog, epoch: epoch, mapping: mapping, maximumEventsPerStep: maximumEventsPerStep, revision: revision, sources: profile);

        sources.CopyTo(destination: producer.m_sources);
        producer.m_sequence = sequence;
        producer.m_image = image;
        return producer;
    }
    /// <summary>Records a physical or synthetic source observation in capture order, including explicit neutral.</summary>
    public bool ObserveSnapshot(ushort handle, long rawValue, ulong tick, ref ulong activationSequence) {
        if (!m_handles.TryGetValue(key: handle, value: out var index)) { return false; }
        FlushNeutral(tick: tick);
        var prior = m_sources[index];
        var activation = prior.Activation;

        if ((rawValue != 0) && (prior.RawValue == 0) && (prior.Source.Kind != WorldInputContributionKind.Relative) &&
            !prior.AwaitingNeutral && Mapping.IsLive(logicalId: prior.Source.Channel)) {
            if (activationSequence == ulong.MaxValue) { CaptureLost = true; return false; }
            activation = ++activationSequence;
        }
        return Observe(handle, rawValue, tick, default, activation);
    }
    /// <summary>Records a physical or synthetic source observation in capture order, including explicit neutral.</summary>
    public bool Observe(ushort handle, long rawValue, ulong tick, FixedQ4816 offset, ulong activation, ulong? processingTick = null) {
        if (CaptureLost || !m_handles.TryGetValue(key: handle, value: out var index)) { return false; }
        if (processingTick is { } current) {
            FlushNeutral(tick: current);
            Drain(tick: current);
            if ((tick > current) && (m_sources[index].Source.Kind != WorldInputContributionKind.Relative)) {
                return Defer(observation: new(Activation: activation, Handle: handle, Offset: offset, Raw: rawValue, Text: null, Tick: tick));
            }
        }
        FlushNeutral(tick: tick);
        var previous = m_sources[index];
        var channel = previous.Source.Channel;
        var address = Mapping.Address(logicalId: channel);
        var shape = m_catalog.Channels[channel].Shape;

        if ((shape != ChannelShape.Relative) && ((rawValue > FixedQ4816.One.Value) || (rawValue < ((shape == ChannelShape.Bipolar) ? -FixedQ4816.One.Value : 0)))) {
            throw new ArgumentOutOfRangeException(paramName: nameof(rawValue));
        }
        m_observed[index] = true;
        m_observedRaw[index] = rawValue;
        if (((address < 0) || previous.AwaitingNeutral) && (rawValue != 0)) { return false; }
        if ((rawValue == 0) && previous.AwaitingNeutral) { m_neutral[index] = true; }
        var next = new WorldInputSourceState(previous.Source, rawValue, activation, false);

        if (next == previous) { return true; }
        m_sources[index] = next;
        m_sourcesChanged = true;
        if (address < 0) { return true; }
        var value = Fold(channel: channel);
        var prior = m_image[address].Value;

        m_image = ((shape == ChannelShape.Relative) ? m_image.WithRelative(ordinal: address, value: FixedQ3232.FromRawBits(value: value)) :
            m_image.WithChannel(ordinal: address, value: FixedQ4816.FromRawBits(value: value)));
        if ((shape == ChannelShape.Relative) || (prior == value)) { return true; }
        var kind = ((shape == ChannelShape.Binary) ? ((value == 0) ? WorldInputEventKind.Release : WorldInputEventKind.Press) : WorldInputEventKind.AxisSample);

        return Append(activation: activation, channel: channel, kind: kind, offset: offset, raw: value, tick: tick);
    }
    /// <summary>Commits typed, pasted, or IME-committed text to a live event-only action as one atomic capture.</summary>
    public bool Text(ushort channel, string value, ulong tick, FixedQ4816 offset, ulong activation = 0, ulong? processingTick = null) {
        if (CaptureLost || (channel >= m_catalog.Channels.Length) || !Mapping.IsLive(logicalId: channel) || (m_catalog.Channels[channel].Shape != ChannelShape.Text)) { return false; }
        var remaining = value.AsSpan();
        var count = 0;

        while (!remaining.IsEmpty) {
            if (Rune.DecodeFromUtf16(charsConsumed: out var consumed, result: out _, source: remaining) != System.Buffers.OperationStatus.Done) { return false; }
            if (++count > m_pending.Length) { CaptureLost = true; return false; }
            remaining = remaining[consumed..];
        }
        if (processingTick is { } current) {
            FlushNeutral(tick: current);
            Drain(tick: current);
            if (tick > current) { return Defer(observation: new(Activation: activation, Handle: channel, Offset: offset, Raw: 0, Text: value, Tick: tick)); }
        }
        FlushNeutral(tick: tick);
        if ((count > (m_pending.Length - m_eventCount)) || ((((ulong)m_sequence) + ((uint)count)) > uint.MaxValue)) { CaptureLost = true; return false; }
        foreach (var scalar in value.EnumerateRunes()) { _ = Append(channel, WorldInputEventKind.Text, scalar.Value, tick, offset, activation); }
        return true;
    }
    /// <summary>Freezes ordered events, active contributions and explicit neutral observations for one transport/tape submission.</summary>
    public WorldInputBatch Publish(ulong tick, SourceRay? ray = null) => PublishCore(borrow: false, ray: ray, tick: tick);
    /// <summary>Publishes reusable storage for immediate, synchronous authority admission. The batch must not be
    /// queued, taped or retained: its contributions and events expire on the next producer operation.</summary>
    public WorldInputBatch PublishBorrowed(ulong tick, SourceRay? ray = null) => PublishCore(borrow: true, ray: ray, tick: tick);

    private WorldInputBatch PublishCore(ulong tick, SourceRay? ray, bool borrow) {
        FlushNeutral(tick: tick);
        Drain(tick: tick);
        if (CaptureLost) { return new(Revision, Epoch, Mapping.Id, tick, default, default, default, Mapping.NumericCount, CaptureLost: true); }
        ReadOnlyMemory<WorldInputContribution> published = m_published;

        if (m_sourcesChanged || borrow) {
            var count = 0;

            for (var index = 0; (index < m_sources.Length); index++) {
                if ((m_sources[index].RawValue != 0) || m_neutral[index]) { count++; }
            }
            var contributions = (borrow ? m_borrowed : ((count == 0) ? [] : new WorldInputContribution[count]));
            var destination = 0;

            for (var index = 0; (index < m_sources.Length); index++) {
                var state = m_sources[index];

                if ((state.RawValue == 0) && !m_neutral[index]) { continue; }
                contributions[destination++] = new(state.Source.Handle, state.Source.Channel, state.Source.Kind, state.RawValue, state.Activation, m_neutral[index]);
            }
            published = contributions.AsMemory(length: count, start: 0);
            if (!borrow) { m_published = contributions; }
            m_sourcesChanged = borrow;
        }
        ReadOnlyMemory<WorldInputEvent> events = (borrow ? m_pending.AsMemory(length: m_eventCount, start: 0) :
            ((m_eventCount == 0) ? default : m_pending.AsSpan(length: m_eventCount, start: 0).ToArray()));
        var batch = new WorldInputBatch(Revision, Epoch, Mapping.Id, tick, m_image with { SourceRay = ray }, published, events, Mapping.NumericCount);

        m_eventCount = 0;
        for (var index = 0; (index < m_neutral.Length); index++) {
            if (!m_neutral[index]) { continue; }
            m_neutral[index] = false;
            m_sourcesChanged = true;
        }
        for (var index = 0; (index < m_sources.Length); index++) {
            if ((m_sources[index].Source.Kind != WorldInputContributionKind.Relative) || (m_sources[index].RawValue == 0)) { continue; }
            m_sources[index] = m_sources[index] with { RawValue = 0 };
            m_sourcesChanged = true;
            var address = Mapping.Address(logicalId: m_sources[index].Source.Channel);

            if (address >= 0) { m_image = m_image.WithRelative(ordinal: address, value: FixedQ3232.Zero); }
        }
        return batch;
    }

    /// <summary>Installs the authority's kept baseline and source decisions; never invents a mode or carries an old event.</summary>
    public void Reconcile(uint revision, ulong epoch, WorldInputMapping mapping, PlayerIntent image, ReadOnlySpan<WorldInputSourceState> sources) {
        if ((revision == Revision) && (epoch == Epoch)) { return; }
        if ((revision < Revision) || ((revision == Revision) && (epoch < Epoch)) || (sources.Length != m_sources.Length)) {
            throw new ArgumentException(message: "input publication is stale or belongs to another profile");
        }
        for (var index = 0; (index < sources.Length); index++) {
            if (sources[index].Source != m_sources[index].Source) { throw new ArgumentException(message: "input publication source registry differs"); }
        }
        m_needsNeutral = false;
        for (var index = 0; (index < sources.Length); index++) {
            var source = sources[index];

            m_reconciledNeutral[index] = (!CaptureLost && m_observed[index] && (m_observedRaw[index] == 0) &&
                ((source.RawValue != 0) || source.AwaitingNeutral) && (source.Source.Kind != WorldInputContributionKind.Relative));
            m_needsNeutral |= m_reconciledNeutral[index];
        }
        Revision = revision;
        Epoch = epoch;
        Mapping = mapping;
        m_image = image;
        sources.CopyTo(destination: m_sources);
        m_sourcesChanged = true;
        m_eventCount = 0;
        m_sequence = 0;
        Array.Clear(array: m_deferred);
        m_deferredCount = 0;
        Array.Clear(array: m_neutral);
        CaptureLost = false;
    }

    private bool Defer(DeferredObservation observation) {
        if (m_deferredCount == m_deferred.Length) { CaptureLost = true; return false; }
        m_deferred[m_deferredCount++] = observation;
        return true;
    }

    /// <summary>Acknowledges an authority-cleared toggle without assigning an event to a nonexistent interval.</summary>
    public void AcknowledgeClearedToggle(ushort handle) {
        if (!m_handles.TryGetValue(key: handle, value: out var index) || (m_sources[index].Source.Kind != WorldInputContributionKind.Toggle) ||
            !m_sources[index].AwaitingNeutral || (m_sources[index].RawValue != 0)) { return; }
        m_observed[index] = true;
        m_observedRaw[index] = 0;
        m_reconciledNeutral[index] = true;
        m_needsNeutral = true;
    }

    private void FlushNeutral(ulong tick) {
        if (!m_needsNeutral) { return; }
        m_needsNeutral = false;
        for (var index = 0; (index < m_reconciledNeutral.Length); index++) {
            if (!m_reconciledNeutral[index]) { continue; }
            m_reconciledNeutral[index] = false;
            _ = Observe(m_sources[index].Source.Handle, 0, tick, default, m_sources[index].Activation);
        }
    }
    private void Drain(ulong tick) {
        var retained = 0;

        for (var index = 0; (index < m_deferredCount); index++) {
            var observation = m_deferred[index];

            if (observation.Tick > tick) { m_deferred[retained++] = observation; continue; }
            if (observation.Text is { } text) { _ = Text(observation.Handle, text, observation.Tick, observation.Offset, observation.Activation); } else { _ = Observe(observation.Handle, observation.Raw, observation.Tick, observation.Offset, observation.Activation); }
        }
        Array.Clear(array: m_deferred, index: retained, length: (m_deferredCount - retained));
        m_deferredCount = retained;
    }
    private bool Append(ushort channel, WorldInputEventKind kind, long raw, ulong tick, FixedQ4816 offset, ulong activation) {
        if ((m_eventCount == m_pending.Length) || (m_sequence == uint.MaxValue)) { CaptureLost = true; return false; }
        m_pending[m_eventCount++] = new(channel, kind, raw, tick, offset, ++m_sequence, activation);
        return true;
    }
    private long Fold(ushort channel) {
        var shape = m_catalog.Channels[channel].Shape;
        Int128 sum = 0;
        var maximum = 0L;

        foreach (var state in m_sources) {
            if (state.Source.Channel != channel) { continue; }
            sum += state.RawValue;
            maximum = Math.Max(val1: maximum, val2: state.RawValue);
        }
        var address = Mapping.Address(logicalId: channel);

        if (shape == ChannelShape.Relative) { return Mapping.Channels.ClampRelative(ordinal: address, raw: sum).Value; }
        var raw = ((shape == ChannelShape.Unipolar) ? maximum : (long)Int128.Clamp(sum, ((shape == ChannelShape.Bipolar) ? -FixedQ4816.One.Value : 0), FixedQ4816.One.Value));

        return Mapping.Channels.Quantize(ordinal: address, value: FixedQ4816.FromRawBits(value: raw)).Value;
    }
}
