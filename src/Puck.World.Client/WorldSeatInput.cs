using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>The seat's compiled source registry and ordered semantic producer, driven by authority publications.</summary>
public sealed class WorldSeatInput {
    private readonly WorldInputCatalog m_catalog;
    private readonly Dictionary<(string Source, int Channel), ushort> m_handles = [];
    private readonly Dictionary<ushort, ulong> m_keptActivations = [];
    private readonly string[] m_sources;
    private readonly WorldInputSource[] m_contracts;
    private readonly Int128[] m_relative;
    private readonly WorldInputProducer m_producer;
    private readonly int m_moveForward;
    private readonly int m_moveStrafe;

    private bool m_admitted;
    private WorldInputSwitch m_boundary;
    // The last edge (a press, a release or an axis sample) this producer sent on each logical channel in the current
    // epoch, by its event sequence (zero for none), and the highest of those whose answer the seat has seen presented
    // (TryLead), which stays answered whatever a later delivery carries.
    private readonly uint[] m_edgeSequence;
    private readonly uint[] m_answeredSequence;

    /// <summary>Reads a named action's authority-published eligibility, rearming and continuation.</summary>
    public WorldInputActionStatus ActionStatus(string name) {
        if (!m_catalog.TryGetLogicalId(id: out var logical, name: name)) { return default; }
        var waiting = false;
        var toggled = false;
        var retained = false;
        var held = false;
        foreach (var source in m_producer.Sources) {
            if (source.Source.Channel != logical) { continue; }
            waiting |= source.AwaitingNeutral;
            held |= (source.RawValue != 0);
            toggled |= ((source.Source.Kind == WorldInputContributionKind.Toggle) && (source.RawValue != 0));
            // Retained is the hold the switch kept, still down; a release and a fresh press carries a new activation.
            retained |= ((source.RawValue != 0) &&
                m_keptActivations.TryGetValue(key: source.Source.Handle, value: out var kept) && (kept == source.Activation));
        }
        return new(Active: m_producer.Mapping.IsLive(logicalId: logical), AwaitingNeutral: waiting, Toggled: toggled, Retained: (retained && held));
    }

    /// <summary>Compiles every page's sources once, including destination-owned toggles and analog movement.</summary>
    /// <param name="definition">The destination's definition, whose input catalog the sources compile against.</param>
    /// <param name="bindings">The routed binding sources.</param>
    /// <param name="previous">The producer this one replaces on the same attachment, whose observations it keeps, or
    /// <see langword="null"/>.</param>
    public WorldSeatInput(WorldDefinition definition, IReadOnlyList<BindingSourceContract> bindings, WorldSeatInput? previous = null) {
        m_catalog = WorldInputCatalog.For(definition: definition);
        m_edgeSequence = new uint[m_catalog.Channels.Length];
        m_answeredSequence = new uint[m_catalog.Channels.Length];
        var contracts = new List<WorldInputSource>();
        var names = new List<string>();

        foreach (var binding in bindings) {
            const string Prefix = "channel.logical.";

            if (!binding.Command.StartsWith(comparisonType: StringComparison.Ordinal, value: Prefix) ||
                !int.TryParse(binding.Command.AsSpan(start: Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var channel)) { continue; }
            if (((uint)channel) >= m_catalog.Channels.Length) { throw new ArgumentException(message: "binding source names an unknown logical action", paramName: nameof(bindings)); }
            var kind = (binding.Relative ? WorldInputContributionKind.Relative :
                ((binding.Mode == BindingEntryMode.Toggle) ? WorldInputContributionKind.Toggle : WorldInputContributionKind.Level));

            Add(binding.Source, channel, kind, binding.Semantics);
        }
        m_moveForward = AddRole(role: ChannelRole.MoveAdvance);
        m_moveStrafe = AddRole(role: ChannelRole.MoveStrafe);
        m_contracts = contracts.ToArray();
        m_sources = names.ToArray();
        m_relative = new Int128[m_contracts.Length];
        m_producer = new(m_catalog, m_catalog.DefaultMapping, m_contracts, (definition.InputHistory?.MaximumEventsPerStep ?? 128));
        if (previous is not null) { m_producer.InheritObservations(previous: previous.m_producer); }

        int AddRole(ChannelRole role) {
            for (var channel = 0; (channel < m_catalog.Channels.Length); channel++) {
                if (m_catalog.Channels[channel].Role != role) { continue; }
                return Add(("$motion:" + role), channel, WorldInputContributionKind.Level);
            }
            return -1;
        }
        ushort Add(string name, int channel, WorldInputContributionKind kind, string semantics = "") {
            var key = (name, channel);

            if (m_handles.TryGetValue(key: key, value: out var existing)) {
                if (contracts[existing].Kind != kind) { throw new ArgumentException(message: "a binding source changes lifetime for the same logical action", paramName: nameof(bindings)); }
                return existing;
            }
            if (contracts.Count == WorldInputCatalog.MaximumSources) { throw new ArgumentException(message: $"input profile exceeds {WorldInputCatalog.MaximumSources} sources", paramName: nameof(bindings)); }
            var handle = ((ushort)contracts.Count);

            m_handles.Add(key: key, value: handle);
            var identity = ((kind == WorldInputContributionKind.Toggle) ? ("$toggle:" + m_catalog.Channels[channel].Name) :
                (name.StartsWith(comparisonType: StringComparison.Ordinal, value: "$binding:") ? ("$binding:" + m_catalog.Channels[channel].Name) : name));

            contracts.Add(item: new(Binding: semantics, Channel: ((ushort)channel), Handle: handle, Kind: kind, Name: identity));
            names.Add(item: name);
            return handle;
        }
    }

    /// <summary>Gets whether the producer holds input it has not published: an event, a displacement or a return to
    /// neutral a device made since the last batch. A seat of an authority in another process sends such input at once,
    /// where it otherwise sends one batch for each tick that authority delivers.</summary>
    public bool HoldsUnpublished => !m_producer.IsQuiescent;
    /// <summary>The authority-published mapping; it is never selected from presentation context.</summary>
    public WorldChannelTable Channels => m_producer.Mapping.Channels;
    /// <summary>Whether the authority has acknowledged this exact source registry.</summary>
    public bool Admitted => m_admitted;

    /// <summary>Sends the immutable compiled source registry through the existing grant-checked command door. A repeat of
    /// the admission last sent, to the same authority for the same owner and body, is held back while that one is
    /// unanswered, and after its answer until <see cref="AdmissionRetryTicks"/> of that authority's ticks have passed
    /// since it was sent: the authority answers an identical admission by keeping the profile it holds, and its
    /// publication follows the answer by a delivery, so a seat keeps at most one admission on the authority's ordered
    /// lane and never queues a copy behind it each step.</summary>
    public void Admit(WorldAuthorityEndpoint endpoint, Principal owner, int body, bool freshAttachment) {
        var tick = endpoint.NextInputTick;

        if (!freshAttachment && ReferenceEquals(objA: m_admittedAt, objB: endpoint) && (m_admittedOwner == owner) && (m_admittedBody == body) &&
            ((Volatile.Read(location: ref m_admissionsUnanswered) != 0) || ((tick >= m_admittedTick) && ((tick - m_admittedTick) < AdmissionRetryTicks)))) {
            return;
        }
        (m_admittedAt, m_admittedOwner, m_admittedBody, m_admittedTick) = (endpoint, owner, body, tick);
        _ = Interlocked.Increment(location: ref m_admissionsUnanswered);
        _ = endpoint.Submissions.SubmitEnvelope(
            completion: _ => Interlocked.Decrement(location: ref m_admissionsUnanswered),
            operationId: Guid.Empty,
            payload: new WorldSubmissionPayload.Command(Value: new WorldCommand.AdmitInput(EntityIndex: body, FreshAttachment: freshAttachment, Principal: owner, Sources: m_contracts)),
            principal: owner
        );
    }
    /// <summary>The authority ticks a seat waits, from sending an admission, before it resends an identical one whose
    /// publication has not arrived.</summary>
    public const ulong AdmissionRetryTicks = 30;
    private WorldAuthorityEndpoint? m_admittedAt;
    private Principal m_admittedOwner;
    private int m_admittedBody = int.MinValue;
    private ulong m_admittedTick;
    // The admissions sent and not yet answered. An answer arrives on whichever thread the link completes on.
    private int m_admissionsUnanswered;
    /// <summary>Installs a matching publication and rebuilds the controller's kept physical/latch image.</summary>
    public bool Reconcile(in WorldInputPublication publication, SeatController seat, InputRouter? router = null, int slot = 0) {
        var sources = publication.Sources.Span;

        if ((sources.Length != m_contracts.Length) || (publication.Mapping >= m_catalog.Mappings.Length)) { return false; }
        for (var index = 0; (index < sources.Length); index++) { if (sources[index].Source != m_contracts[index]) { return false; } }
        if (m_admitted && (publication.Revision == m_producer.Revision) && (publication.Epoch == m_producer.Epoch)) { return true; }
        m_producer.Reconcile(publication.Revision, publication.Epoch, m_catalog.Mappings[publication.Mapping], publication.Baseline, sources);
        // A new epoch ends every guess: its edges belong to the epoch the authority closed.
        Array.Clear(array: m_edgeSequence);
        Array.Clear(array: m_answeredSequence);
        m_boundary = publication.Boundary;
        m_keptActivations.Clear();
        foreach (var decision in m_boundary.Decisions.Span) {
            if ((decision.Reason != WorldInputContinuationReason.Kept) || (decision.CurrentRaw == 0)) { continue; }
            foreach (var source in sources) {
                if ((source.Source.Channel == decision.Channel) && (source.RawValue != 0)) { m_keptActivations[source.Source.Handle] = source.Activation; }
            }
        }
        m_admitted = true;
        Array.Clear(array: m_relative);
        seat.InstallInputSources(Channels, sources, m_sources);
        for (var index = 0; (index < sources.Length); index++) {
            var state = sources[index];

            if ((state.Source.Kind != WorldInputContributionKind.Toggle) || (state.RawValue != 0) || !state.AwaitingNeutral) { continue; }
            router?.ClearToggle(slot, PlayerCommandNames.RoutedChannelCommandName(ordinal: state.Source.Channel));
            // A latch's neutral is its authoritative off decision. The router still requires the opener's real
            // release before a fresh Started edge, independently of this destination-owned lifetime.
            m_producer.AcknowledgeNeutral(handle: state.Source.Handle);
        }
        return true;
    }
    /// <summary>Reports neutral for every button the authority holds released and awaiting neutral whose physical
    /// source the router reports up. The router reports a button's edge once, so a release it dispatched where this
    /// producer could not observe it (the map-change cancellation of a crossing, a release after that cancellation
    /// ended the command it held) would otherwise leave the button unable to act again until it was pressed and
    /// released twice.</summary>
    /// <param name="router">The host's input router.</param>
    /// <param name="slot">The seat's 0-based slot.</param>
    public void AcknowledgeReleasedButtons(InputRouter router, int slot) {
        ArgumentNullException.ThrowIfNull(argument: router);

        if (!m_admitted) {
            return;
        }

        var sources = m_producer.Sources;

        for (var index = 0; (index < sources.Length); index++) {
            var state = sources[index];

            if ((state.RawValue != 0) || !state.AwaitingNeutral || (state.Source.Kind != WorldInputContributionKind.Level) ||
                (m_catalog.Channels[state.Source.Channel].Shape != ChannelShape.Binary) || m_sources[index].StartsWith(value: '$') ||
                router.IsSourcePressed(slot: slot, source: m_sources[index])) {
                continue;
            }
            m_producer.AcknowledgeNeutral(handle: state.Source.Handle);
        }
    }
    /// <summary>Captures a bound source before the host folds away its intermediate transitions.</summary>
    public bool Observe(int channel, in CommandContext context, ulong tick) {
        if (!m_admitted || (context.Source is null) || !m_handles.TryGetValue(key: (context.Source, channel), value: out var handle)) { return false; }
        var active = (context.Phase is CommandPhase.Started or CommandPhase.Active);
        long raw;

        if (m_contracts[handle].Kind == WorldInputContributionKind.Relative) {
            if (!active) { return true; }
            var address = m_producer.Mapping.Address(logicalId: channel);

            if (address < 0) { return false; }
            var declaration = m_catalog.Channels[channel].Relative!;
            var value = FixedQ3232.FromDouble(value: (context.Value.AsAxis1D * ((double)declaration.Sensitivity))).Value;

            m_relative[handle] += value;
            raw = Channels.ClampRelative(ordinal: address, raw: m_relative[handle]).Value;
        } else { raw = (active ? FixedQ4816.FromDouble(value: context.Value.AsAxis1D).Value : 0); }
        var stamp = Stamp(context: context, tick: tick);

        return m_producer.Observe(handle, raw, stamp.Tick, stamp.Offset, context.Activation, processingTick: tick);
    }
    /// <summary>Captures the quantized analog movement source through the same ordered stream as bindings.</summary>
    public void ObserveMove(FixedVector2 value, in CommandContext context, ulong tick) {
        if (!m_admitted) { return; }
        var stamp = Stamp(context: context, tick: tick);

        if (m_moveForward >= 0) { _ = m_producer.Observe(((ushort)m_moveForward), value.Y.Value, stamp.Tick, stamp.Offset, context.Activation, processingTick: tick); }
        if (m_moveStrafe >= 0) { _ = m_producer.Observe(((ushort)m_moveStrafe), value.X.Value, stamp.Tick, stamp.Offset, context.Activation, processingTick: tick); }
    }

    private static (ulong Tick, FixedQ4816 Offset) Stamp(in CommandContext context, ulong tick) {
        if (context.SimulationOffset is { } offset) { return (tick, offset); }
        return Stamp(context.CaptureTick, context.CaptureWindowStart, context.CaptureWindowEnd, tick);
    }

    /// <summary>Routes committed Unicode scalars to the authority-selected text focus.</summary>
    public bool ObserveText(in CommittedTextInput input, ulong tick) {
        if (!m_admitted) { return false; }
        var stamp = ((input.SimulationOffset is { } offset) ? (Tick: tick, Offset: offset) : Stamp(input.CaptureTick, input.WindowStart, input.WindowEnd, tick));

        for (var channel = 0; (channel < m_catalog.Channels.Length); channel++) {
            if ((m_catalog.Channels[channel].Shape == ChannelShape.Text) && m_producer.Mapping.IsLive(logicalId: channel)) {
                return m_producer.Text(((ushort)channel), input.Text, stamp.Tick, stamp.Offset, input.Activation, processingTick: tick);
            }
        }
        return false;
    }

    private static (ulong Tick, FixedQ4816 Offset) Stamp(ulong captureTick, ulong start, ulong end, ulong tick) {
        var offset = FixedQ4816.Zero;

        if ((captureTick > start) && (end > start)) {
            var interval = new WorldInputInterval(tick, 0, (end - start));

            (tick, offset) = interval.Quantize(denominator: FixedTickConversion.TicksPerSecond, numerator: (captureTick - start));
        }
        return (tick, offset);
    }

    /// <summary>Freezes the tick's ordered input and the separately composed presentation-facing motion request.</summary>
    public IntentSubmission Publish(Principal owner, int body, ulong tick, SeatController seat, PlayerIntent motion) {
        var batch = m_producer.Publish(tick, seat.PointerRay);

        Array.Clear(array: m_relative);
        foreach (var input in batch.Events.Span) {
            if ((input.Kind is WorldInputEventKind.Press or WorldInputEventKind.Release or WorldInputEventKind.AxisSample) && (input.Channel < m_edgeSequence.Length)) {
                m_edgeSequence[input.Channel] = input.Sequence;
            }
        }
        return new(tick, body, motion, owner, Input: batch);
    }
    /// <summary>Answers whether an edge this seat sent on a channel is outstanding (<c>CreationDriverDocument.Lead</c>):
    /// the one function a guess's start and end are read through, deciding through <see cref="ReadGuess"/>. The only
    /// state it writes is the seat's own record that an edge's answer was presented, so an answer a later delivery no
    /// longer carries (a restored checkpoint, a withdrawn span) revives no guess.</summary>
    /// <param name="channel">The input channel's name.</param>
    /// <param name="answer">The last input the authority answered for this seat's owner, or <see langword="null"/>
    /// when none has been delivered.</param>
    /// <param name="presentedTick">The simulation tick the presented state was last delivered at.</param>
    /// <param name="presentedFraction">How far the frame has presented from the tick before it to
    /// <paramref name="presentedTick"/>, in [0, 1].</param>
    /// <param name="level">The seat's own folded level on the channel; zero when no edge is outstanding.</param>
    /// <param name="edge">The outstanding edge's identity, its epoch and event sequence together, which no other edge of
    /// this producer shares; zero when none is outstanding.</param>
    /// <returns><see langword="true"/> while an edge is outstanding (<see cref="WorldGuessState.InForce"/>).</returns>
    /// <remarks>How long an unanswered edge may show (<c>leadSeconds</c>) is measured by the caller on the presentation
    /// clock (<see cref="WorldRigReads.TryLead"/>), never here on the wall clock. The caller decides the audience: it asks
    /// only for the body the seat controls, and draws what it learns in that seat's own view alone.</remarks>
    public bool TryLead(string channel, WorldInputAnswer? answer, ulong presentedTick, float presentedFraction, out float level, out ulong edge) {
        var reading = ReadGuess(answer: answer, channel: channel, presentedFraction: presentedFraction, presentedTick: presentedTick);

        level = reading.Level;
        edge = 0UL;
        if ((reading.State == WorldGuessState.Answered) && m_catalog.TryGetLogicalId(id: out var logical, name: channel)) {
            m_answeredSequence[logical] = Math.Max(val1: m_answeredSequence[logical], val2: reading.Outstanding);
        }
        if (reading.State != WorldGuessState.InForce) { return false; }
        // A sequence restarts with each epoch, so the epoch keeps a later epoch's edge from reading as an earlier one.
        edge = ((reading.Epoch << 32) ^ reading.Outstanding);

        return true;
    }
    /// <summary>Reads why an edge on a channel is or is not outstanding, with the figures it was decided from: the one
    /// decision <see cref="TryLead"/> answers from. It writes nothing.</summary>
    /// <param name="channel">The input channel's name.</param>
    /// <param name="answer">The last input the authority answered for this seat's owner, or <see langword="null"/>.</param>
    /// <param name="presentedTick">The simulation tick the presented state was last delivered at.</param>
    /// <param name="presentedFraction">How far the frame has presented toward <paramref name="presentedTick"/>, in [0, 1].</param>
    /// <returns>The reading; its level is zero unless the state is <see cref="WorldGuessState.InForce"/>.</returns>
    public WorldGuessReading ReadGuess(string channel, WorldInputAnswer? answer, ulong presentedTick, float presentedFraction) {
        if (!m_admitted) { return new(WorldGuessState.NotAdmitted, 0U, 0f, m_producer.Epoch); }
        if (m_producer.CaptureLost) { return new(WorldGuessState.CaptureLost, 0U, 0f, m_producer.Epoch); }
        if (!m_catalog.TryGetLogicalId(id: out var logical, name: channel) ||
            (m_catalog.Channels[logical].Shape is not (ChannelShape.Binary or ChannelShape.Bipolar or ChannelShape.Unipolar))) {
            return new(WorldGuessState.NotALevelChannel, 0U, 0f, m_producer.Epoch);
        }
        if (!m_producer.Mapping.IsLive(logicalId: logical)) { return new(WorldGuessState.NotLive, 0U, 0f, m_producer.Epoch); }

        var sequence = m_edgeSequence[logical];

        if (sequence == 0U) { return new(WorldGuessState.NoEdge, 0U, 0f, m_producer.Epoch); }
        // Answered once the authority's answer covers the edge in this epoch and the tick it answered in is presented, and
        // answered for good once the seat has seen that (TryLead).
        if ((sequence <= m_answeredSequence[logical]) || ((answer is { } answered) && (answered.Epoch == m_producer.Epoch) && (answered.Sequence >= sequence) &&
            ((presentedTick > answered.Tick) || ((presentedTick == answered.Tick) && (presentedFraction >= 1f))))) {
            return new(WorldGuessState.Answered, sequence, 0f, m_producer.Epoch);
        }

        return new(WorldGuessState.InForce, sequence, ((float)m_producer.Level(channel: logical)), m_producer.Epoch);
    }
}
/// <summary>Why a seat's edge on a channel is or is not outstanding (<see cref="WorldSeatInput.ReadGuess"/>).</summary>
public enum WorldGuessState {
    /// <summary>The seat's input is not admitted by its authority.</summary>
    NotAdmitted,
    /// <summary>The producer lost a capture.</summary>
    CaptureLost,
    /// <summary>The channel is not a button or an axis the world declares.</summary>
    NotALevelChannel,
    /// <summary>The channel is not live in the mapping the authority last published.</summary>
    NotLive,
    /// <summary>The seat has sent no edge on the channel in the current epoch.</summary>
    NoEdge,
    /// <summary>The authority's answer to the seat's last edge has been presented.</summary>
    Answered,
    /// <summary>The seat's last edge is outstanding; a guess layer shows it for at most its <c>leadSeconds</c> of
    /// presented time.</summary>
    InForce,
}
/// <summary>An edge's decision and the figures it rests on.</summary>
/// <param name="State">Why the edge is or is not outstanding.</param>
/// <param name="Outstanding">The event sequence of the seat's last edge on the channel in the epoch; zero for none.</param>
/// <param name="Level">The seat's folded level while in force; otherwise zero.</param>
/// <param name="Epoch">The producer's input epoch.</param>
public readonly record struct WorldGuessReading(WorldGuessState State, uint Outstanding, float Level, ulong Epoch);
