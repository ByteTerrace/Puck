using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A local producer's actual authorizing actor and stable source, independent of its input session identity.</summary>
public readonly record struct WorldInputControllerOrigin(Principal Actor, string Source, ulong ActivationSequence = 0, ulong ObservationTick = 0);
public sealed partial class WorldInputRuntime {
    private const string BodyScriptSource = "$body-script";

    private bool IsAuthoredProducer(Principal actor, int body, string source) => ((actor.Kind == PrincipalKind.World) &&
        (m_host.Body(index: body) is { } target) && ((source == BodyScriptSource) ||
        ((target.Source.ProducerName == source) && m_host.Population.SupportsSource(body, target.Source, out _))));

    internal PlayerIntent AdmitScriptSnapshot(Principal actor, int body, PlayerIntent image, ulong tick) {
        if (TryPublishSnapshot(actor, body, BodyScriptSource, image, tick, out var principal, out var batch, out var reason) &&
            TryAdmit(batch: batch, bodyIndex: body, principal: principal, reason: out reason)) { return ProjectSnapshot(batch: batch, body: body); }
        RejectControllerBatch(principal: principal);
        m_host.Output.Narrate("input.refused", reason);
        return default;
    }
    internal void CompleteBodyScripts(int body, ulong tick) {
        if (!m_bodyOwners.TryGetValue(key: body, value: out var owners)) { return; }
        foreach (var owner in owners) {
            if ((owner.Controller is not { Source: BodyScriptSource } origin) || (origin.ObservationTick == tick) ||
                !m_host.GrantTable.IsLiveSession(principal: owner.Principal)) { continue; }
            _ = AdmitScriptSnapshot(actor: origin.Actor, body: body, image: default, tick: tick);
        }
    }
    /// <summary>Admits a world's own compiled producer before its body consumes the resulting image. The
    /// structural actor is local-only; external submissions cannot claim the World principal.</summary>
    internal PlayerIntent AdmitProgramSnapshot(int body, PlayerIntent image, ulong tick) {
        if (m_host.Body(index: body) is not { Source.ProducerName: { } source } target) { return default; }
        if (!TryPublishSnapshot(Principal.World, body, source, image, tick, out var principal, out var batch, out var reason) ||
            !TryAdmit(batch: batch, bodyIndex: body, principal: principal, reason: out reason)) {
            RejectControllerBatch(principal: principal);
            m_host.Output.Narrate("input.refused", reason);
            return default;
        }
        return ProjectSnapshot(batch: batch, body: body);
    }

    private PlayerIntent ProjectSnapshot(int body, in WorldInputBatch batch) {
        var target = m_host.Body(index: body)!;
        var channels = Catalog.Mappings[batch.Mapping].Channels;
        var accepted = batch.Image with { Channels = default };

        foreach (var logical in Catalog.Mappings[batch.Mapping].Live) {
            var address = Catalog.Mappings[batch.Mapping].Address(logicalId: logical);

            if ((address < 0) || !target.Channels.TryGetOrdinal(name: channels.Name(ordinal: address)!, ordinal: out var destination)) { continue; }
            accepted = ((channels.Shape(ordinal: address) == ChannelShape.Relative)
                ? accepted.WithRelative(ordinal: destination, value: batch.Image.Channels.Relative(ordinal: address))
                : accepted.WithChannel(ordinal: destination, value: batch.Image[address]));
        }
        return accepted;
    }
    private static WorldInputSource[] SnapshotProfile(WorldInputCatalog catalog) {
        var sources = new List<WorldInputSource>();

        for (var channel = 0; (channel < catalog.Channels.Length); channel++) {
            var declaration = catalog.Channels[channel];

            if (declaration.Shape == ChannelShape.Text) { continue; }
            sources.Add(item: new(((ushort)channel), ((ushort)channel),
                ((declaration.Shape == ChannelShape.Relative) ? WorldInputContributionKind.Relative : WorldInputContributionKind.Level),
                ("$snapshot:" + declaration.Name)));
        }
        return sources.ToArray();
    }

    /// <summary>Publishes a complete synthetic observation through a generation-bearing input session. The caller
    /// still submits under the actual actor for grants and contribution consent; the session owns only its history.
    /// The returned batch borrows producer storage and must be admitted synchronously before another publication.</summary>
    public bool TryPublishSnapshot(Principal actor, int body, string source, PlayerIntent image, ulong tick,
        out Principal principal, out WorldInputBatch batch, out string reason, ReadOnlySpan<WorldInputEvent> observations = default) {
        principal = default;
        batch = default;
        reason = "input.owner: invalid snapshot producer or route";
        if (string.IsNullOrWhiteSpace(value: source) || (source.Length > 256) || (body < 0) || (body >= m_host.Population.Capacity) ||
            (m_host.Body(index: body) is not { } target)) { return false; }
        var observedChannels = target.Channels;

        if (m_host.GrantTable.TryGetDriveGate(bodyIndex: body, gateRow: out _) ||
            (!IsAuthoredProducer(actor: actor, body: body, source: source) &&
            !m_host.GrantTable.Allows(actor, WorldCapability.Drive, GrantSubject.Body(index: body)).IsAllowed)) {
            reason = "input.permission: snapshot producer cannot drive its target";
            return false;
        }
        var key = (actor, body, source);

        if (m_controllers.TryGetValue(key: key, value: out var owner) &&
            ((owner.Generation != m_host.Population.Generation(index: body)) || !m_host.GrantTable.IsLiveSession(principal: owner.Principal))) {
            Reset(owner.Principal);
            m_host.GrantTable.MarkObserverEnded(session: owner.Principal);
            RemoveOwner(owner: owner);
            m_controllers.Remove(key: key);
            owner = null;
        }
        if (owner is null) {
            if (m_ordered.Count == ushort.MaxValue) { reason = "input.capacity: too many input owners"; return false; }
            var sources = SnapshotProfile(catalog: Catalog);
            var session = m_host.GrantTable.AdmitInputControllerSession();

            try { owner = PrepareOwner(session, body, sources, 1, controller: new(actor, source)); } catch (ArgumentException error) { m_host.GrantTable.MarkObserverEnded(session: session); reason = error.Message; return false; }
            // Adding a local observation stream does not detach the body's existing scripted durations.
            InstallOwner(owner, Catalog.Mappings[target.InputMappingId], reset: false, recompiled: true);
            m_controllers.Add(key: key, value: owner);
        }
        if (!TrySynchronize(activation: 0, owner: owner, reason: out reason)) { return false; }
        var input = owner.Input;
        var producer = owner.Producer ??= WorldInputProducer.FromAccepted(Catalog, input.Mapping, input.Sources,
            (m_host.Definition.InputHistory?.MaximumEventsPerStep ?? 128), input.Revision, input.Epoch, input.Sequence, input.Image);

        producer.Reconcile(input.Revision, input.Epoch, input.Mapping, input.Image, input.Sources);
        var origin = owner.Controller!.Value;
        var activationSequence = origin.ActivationSequence;
        var continuingOnly = ((input.Epoch > 1) && (input.LastSwitch.Tick == tick));

        if (!continuingOnly) {
            foreach (var observation in observations) {
                activationSequence = Math.Max(val1: activationSequence, val2: observation.Activation);
                var accepted = ((observation.Kind == WorldInputEventKind.Text)
                    ? producer.Text(observation.Channel, char.ConvertFromUtf32(utf32: checked((int)observation.RawValue)), tick, observation.Offset, observation.Activation)
                    : producer.Observe(observation.Channel, observation.RawValue, tick, observation.Offset, observation.Activation));

                if (!accepted) { reason = "input.observation: recorded observation does not belong to the current semantic profile"; return false; }
            }
        }
        foreach (var state in (continuingOnly ? ReadOnlySpan<WorldInputSourceState>.Empty : input.Sources)) {
            var contract = state.Source;
            var declaration = Catalog.Channels[contract.Channel];
            var raw = (!observedChannels.TryGetOrdinal(name: declaration.Name, ordinal: out var address) ? 0 :
                ((declaration.Shape == ChannelShape.Relative) ? image.Channels.Relative(ordinal: address).Value : image[address].Value));
            // A snapshot's newly nonzero channel is one activation; all its later samples retain that origin.
            _ = producer.ObserveSnapshot(contract.Handle, raw, tick, ref activationSequence);
        }
        owner.Controller = origin with { ActivationSequence = activationSequence, ObservationTick = tick };
        if (producer.CaptureLost) { Reset(owner.Principal); reason = "input.capacity: synthetic snapshot exceeds the event allowance"; return false; }
        principal = owner.Principal;
        batch = producer.PublishBorrowed(tick, (continuingOnly ? input.Image.SourceRay : image.SourceRay));
        reason = string.Empty;
        return true;
    }
    /// <summary>Checks that a submitted input session belongs to this exact actor and routing subject.</summary>
    public bool IsController(Principal principal, Principal actor, int body) => (m_owners.TryGetValue(key: principal, value: out var owner) &&
        (owner.Controller is { } controller) && (controller.Actor == actor) && (owner.Body == body) &&
        (owner.Generation == m_host.Population.Generation(index: body)) && m_host.GrantTable.IsLiveSession(principal: principal));
    /// <summary>Finishes one pure snapshot producer's interval. An omitted target contributes neutral through the
    /// same event admission, so stopping a snapshot stream releases its holds rather than leaving them in history.</summary>
    public void CompleteSnapshots(Principal actor, string source, ulong tick) {
        foreach (var owner in m_ordered) {
            if ((owner.Controller is not { } origin) || (origin.Actor != actor) || (origin.Source != source) ||
                (origin.ObservationTick == tick) || !m_host.GrantTable.IsLiveSession(principal: owner.Principal)) { continue; }
            if ((owner.Body < 0) || (owner.Generation != m_host.Population.Generation(index: owner.Body)) || (m_host.Body(index: owner.Body) is null)) {
                Reset(owner.Principal);
                m_host.GrantTable.MarkObserverEnded(session: owner.Principal);
                continue;
            }
            if (TryPublishSnapshot(actor, owner.Body, source, default, tick, out var principal, out var batch, out _) &&
                !TryAdmit(batch: batch, bodyIndex: owner.Body, principal: principal, reason: out _)) { owner.Producer = null; }
        }
    }
    /// <summary>Cancels a detached, disabled or replaced producer. Reattachment receives a new session generation.</summary>
    public void CancelSnapshots(Principal actor, string source) {
        foreach (var owner in m_ordered) {
            if ((owner.Controller is not { } origin) || (origin.Actor != actor) || (origin.Source != source) ||
                !m_host.GrantTable.IsLiveSession(principal: owner.Principal)) { continue; }
            Reset(owner.Principal);
            m_host.GrantTable.MarkObserverEnded(session: owner.Principal);
            owner.Producer = null;
        }
    }

    internal void RejectControllerBatch(Principal principal) {
        if (m_owners.TryGetValue(key: principal, value: out var owner) && (owner.Controller is not null)) { owner.Producer = null; }
    }
}
