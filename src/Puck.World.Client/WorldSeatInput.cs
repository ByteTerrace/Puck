using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>The seat's compiled source registry and ordered semantic producer, driven by authority publications.</summary>
public sealed class WorldSeatInput {
    private readonly WorldInputCatalog m_catalog;
    private readonly Dictionary<(string Source, int Channel), ushort> m_handles = [];
    private readonly string[] m_sources;
    private readonly WorldInputSource[] m_contracts;
    private readonly Int128[] m_relative;
    private readonly WorldInputProducer m_producer;
    private readonly int m_moveForward;
    private readonly int m_moveStrafe;

    private bool m_admitted;

    /// <summary>Compiles every page's sources once, including destination-owned toggles and analog movement.</summary>
    public WorldSeatInput(WorldDefinition definition, IReadOnlyList<BindingSourceContract> bindings, WorldSeatInput? previous = null) {
        m_catalog = WorldInputCatalog.For(definition: definition);
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

    /// <summary>The authority-published mapping; it is never selected from presentation context.</summary>
    public WorldChannelTable Channels => m_producer.Mapping.Channels;
    /// <summary>Whether the authority has acknowledged this exact source registry.</summary>
    public bool Admitted => m_admitted;

    /// <summary>Sends the immutable compiled source registry through the existing grant-checked command door.</summary>
    public void Admit(WorldAuthorityEndpoint endpoint, Principal owner, int body, bool freshAttachment) =>
        endpoint.Submissions.SubmitCommand(command: new WorldCommand.AdmitInput(EntityIndex: body, FreshAttachment: freshAttachment, Principal: owner, Sources: m_contracts));
    /// <summary>Installs a matching publication and rebuilds the controller's kept physical/latch image.</summary>
    public bool Reconcile(in WorldInputPublication publication, SeatController seat, InputRouter? router = null, int slot = 0) {
        var sources = publication.Sources.Span;

        if ((sources.Length != m_contracts.Length) || (publication.Mapping >= m_catalog.Mappings.Length)) { return false; }
        for (var index = 0; (index < sources.Length); index++) { if (sources[index].Source != m_contracts[index]) { return false; } }
        if (m_admitted && (publication.Revision == m_producer.Revision) && (publication.Epoch == m_producer.Epoch)) { return true; }
        m_producer.Reconcile(publication.Revision, publication.Epoch, m_catalog.Mappings[publication.Mapping], publication.Baseline, sources);
        m_admitted = true;
        Array.Clear(array: m_relative);
        seat.InstallInputSources(Channels, sources, m_sources);
        for (var index = 0; (index < sources.Length); index++) {
            var state = sources[index];

            if ((state.Source.Kind != WorldInputContributionKind.Toggle) || (state.RawValue != 0) || !state.AwaitingNeutral) { continue; }
            router?.ClearToggle(slot, PlayerCommandNames.RoutedChannelCommandName(ordinal: state.Source.Channel));
            // A latch's neutral is its authoritative off decision. The router still requires the opener's real
            // release before a fresh Started edge, independently of this destination-owned lifetime.
            m_producer.AcknowledgeClearedToggle(handle: state.Source.Handle);
        }
        return true;
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
        return new(tick, body, motion, owner, Input: batch);
    }
}
