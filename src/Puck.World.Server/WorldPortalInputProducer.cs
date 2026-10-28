using System.Text;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Forwards semantic observations through an admitted portal session's ordinary timed input door.</summary>
/// <remarks>Source-world intervals are not destination clock coordinates. Forwarded observations use the next
/// destination interval's boundary, preserving capture order within that boundary.</remarks>
public sealed class WorldPortalInputProducer {
    private readonly Principal m_session;

    private WorldDefinition m_definition;
    private WorldInputCatalog m_catalog;
    private WorldInputSource[] m_profile;
    private WorldInputProducer? m_producer;
    private ulong[] m_activations;
    private Principal? m_source;
    private ulong m_sourceEpoch;
    private ulong m_sourceCursor;
    private (uint Revision, ulong Epoch)? m_waitForReset;

    /// <summary>Requests the session's source profile through its destination link.</summary>
    public WorldPortalInputProducer(WorldInstance destination, Principal session) {
        m_session = session;
        m_definition = destination.Server.Configuration;
        m_catalog = destination.Server.Population.InputCatalog;
        m_profile = Profile(catalog: m_catalog);
        m_activations = new ulong[m_catalog.Channels.Length];
        RequestProfile(destination, fresh: destination.Server.Input.TryGet(input: out _, principal: session));
    }

    private static WorldInputSource[] Profile(WorldInputCatalog catalog) {
        var profile = new List<WorldInputSource>();

        for (var channel = 0; (channel < catalog.Channels.Length); channel++) {
            var declaration = catalog.Channels[channel];

            if (declaration.Shape == ChannelShape.Text) { continue; }
            profile.Add(item: new(((ushort)channel), ((ushort)channel),
                ((declaration.Shape == ChannelShape.Relative) ? WorldInputContributionKind.Relative : WorldInputContributionKind.Level),
                ("$portal:" + declaration.Name)));
        }
        return profile.ToArray();
    }
    private void RequestProfile(WorldInstance destination, bool fresh) {
        if (fresh && destination.Server.Input.TryGet(input: out var owner, principal: m_session)) {
            m_waitForReset = (owner!.Revision, owner.Epoch);
        }
        destination.Link.SubmitCommand(command: new WorldCommand.AdmitInput(EntityIndex: -1, FreshAttachment: fresh, Principal: m_session, Sources: m_profile));
        m_producer = null;
    }

    /// <summary>Cancels a lost route without synthesizing a gameplay release.</summary>
    public void Stop(WorldInstance destination) {
        RequestProfile(destination, fresh: true);
        m_source = null;
        m_sourceEpoch = 0;
        m_sourceCursor = 0;
    }

    private bool Ready(WorldInstance destination) {
        var definition = destination.Server.Configuration;

        if (!ReferenceEquals(objA: m_definition, objB: definition)) {
            var contractChanged = !WorldInputCatalog.SameContract(left: m_definition, right: definition);

            m_definition = definition;
            if (contractChanged) {
                m_catalog = destination.Server.Population.InputCatalog;
                m_profile = Profile(catalog: m_catalog);
                m_activations = new ulong[m_catalog.Channels.Length];
                RequestProfile(destination, fresh: false);
            }
        }
        if (!destination.Server.Input.TryGet(input: out var owner, principal: m_session) || (owner!.Sources.Length != m_profile.Length)) { return false; }
        if ((m_waitForReset is { } pending) && (pending == (owner.Revision, owner.Epoch))) { return false; }
        for (var index = 0; (index < m_profile.Length); index++) {
            if (owner.Sources[index].Source != m_profile[index]) { return false; }
        }
        m_waitForReset = null;
        var changed = ((m_producer is null) || (m_producer.Revision != owner.Revision) || (m_producer.Epoch != owner.Epoch));

        if (m_producer is null) {
            m_producer = WorldInputProducer.FromAccepted(m_catalog, owner.Mapping, owner.Sources,
                (m_definition.InputHistory?.MaximumEventsPerStep ?? 128), owner.Revision, owner.Epoch, owner.Sequence, owner.Image);
        } else {
            m_producer.Reconcile(owner.Revision, owner.Epoch, owner.Mapping, owner.Image, owner.Sources);
        }
        if (changed) {
            foreach (var source in owner.Sources) { m_activations[source.Source.Channel] = source.Activation; }
        }
        return true;
    }

    /// <summary>Forwards ordered edges and text, then reconciles the mapped final level image.</summary>
    /// <returns>Whether a batch was queued; false while profile admission or a dispatch reset is pending.</returns>
    public bool Forward(WorldInstance destination, WorldServer sourceWorld, in WorldPortalForward forward, PlayerIntent mapped) {
        sourceWorld.Input.TryGet(forward.Source, out var source);
        var epoch = (source?.Epoch ?? 0);

        if ((m_source is { } previous) && ((previous != forward.Source) || (m_sourceEpoch != epoch))) {
            Stop(destination: destination);
            m_source = forward.Source;
            m_sourceEpoch = epoch;
            return false;
        }
        m_source = forward.Source;
        m_sourceEpoch = epoch;
        if (!Ready(destination: destination)) { return false; }
        var producer = m_producer!;
        var tick = destination.Server.NextInputTick;
        var sourceCatalog = sourceWorld.Population.InputCatalog;

        if (source is not null) {
            for (var index = 0; (index < source.Count); index++) {
                var record = source.Record(index: index);

                if ((record.Id <= m_sourceCursor) || (record.Epoch != source.Epoch) || (record.Admission.Tick != sourceWorld.Input.Interval.Tick)) { continue; }
                m_sourceCursor = record.Id;
                var captured = record.Event;
                var declaration = sourceCatalog.Channels[captured.Channel];

                if (!m_catalog.TryGetLogicalId(declaration.Name, out var channel) ||
                    (m_catalog.Channels[channel].Shape != declaration.Shape) || !producer.Mapping.IsLive(logicalId: channel)) { continue; }
                // These are observations at a different authority's ingress boundary. Preserve semantic order,
                // but never attach a source world's interval or partial pattern to an unrelated destination clock.
                if (captured.Kind == WorldInputEventKind.Text) {
                    _ = producer.Text(channel, new Rune(value: checked((int)captured.RawValue)).ToString(), tick, default, captured.Activation);
                } else if (captured.Kind is WorldInputEventKind.Press or WorldInputEventKind.Release or WorldInputEventKind.AxisSample) {
                    m_activations[channel] = captured.Activation;
                    _ = producer.Observe(channel, captured.RawValue, tick, default, captured.Activation);
                }
            }
        }
        foreach (var registered in m_profile) {
            var address = producer.Mapping.Address(logicalId: registered.Channel);
            var raw = ((address < 0) ? 0 : ((registered.Kind == WorldInputContributionKind.Relative)
                ? mapped.Channels.Relative(ordinal: address).Value : mapped[address].Value));

            _ = producer.Observe(registered.Handle, raw, tick, default, m_activations[registered.Channel]);
        }
        var batch = producer.Publish(tick, mapped.SourceRay);

        destination.Link.SubmitIntent(submission: new IntentSubmission(tick, -1, default, m_session, Input: batch));
        return true;
    }
}
