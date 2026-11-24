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
        m_host.Population.ThrowIfPartitioned();
        if (TryPublishSnapshotCore(actor, body, BodyScriptSource, image, tick, default, out var owner, out var principal, out var batch, out var reason) &&
            TryAdmitSynchronized(alreadyPermitted: (actor.Kind == PrincipalKind.World), batch: in batch, owner: owner!, reason: out reason)) { return ProjectSnapshot(batch: batch, body: body); }
        RejectControllerBatch(principal: principal);
        m_host.Output.Narrate("input.refused", reason);
        return default;
    }
    /// <summary>Returns whether a body holds a script controller, whose interval its step admits.</summary>
    /// <param name="body">The body's population index.</param>
    /// <returns><see langword="true"/> when some owner of the body is a script controller.</returns>
    internal bool HasBodyScripts(int body) {
        if (!m_bodyOwners.TryGetValue(key: body, value: out var owners)) { return false; }
        foreach (var owner in owners) {
            if (owner.Controller is { Source: BodyScriptSource }) { return true; }
        }
        return false;
    }
    /// <summary>Finishes the interval of every script controller of the body that did not observe this tick. A
    /// controller admitted under an earlier generation of the body (it left through a door and came back) has no
    /// interval in this admission and is retired first, so completing the rest never changes the owner list it walks.</summary>
    internal void CompleteBodyScripts(int body, ulong tick) {
        m_host.Population.ThrowIfPartitioned();
        if (!m_bodyOwners.TryGetValue(key: body, value: out var owners)) { return; }
        var generation = m_host.Population.Generation(index: body);

        for (var index = (owners.Count - 1); (index >= 0); index--) {
            var owner = owners[index];

            if ((owner.Controller is not { Source: BodyScriptSource } origin) || (owner.Generation == generation)) { continue; }
            Reset(owner.Principal);
            m_host.GrantTable.MarkObserverEnded(session: owner.Principal);
            RemoveOwner(owner: owner);
            if (m_controllers.TryGetValue(key: (origin.Actor, body, BodyScriptSource), value: out var keyed) && ReferenceEquals(objA: keyed, objB: owner)) {
                m_controllers.Remove(key: (origin.Actor, body, BodyScriptSource));
            }
        }
        for (var index = 0; (index < owners.Count); index++) {
            var owner = owners[index];

            if ((owner.Controller is not { Source: BodyScriptSource } origin) || (origin.ObservationTick == tick) ||
                !m_host.GrantTable.IsLiveSession(principal: owner.Principal)) { continue; }
            _ = AdmitScriptSnapshot(actor: origin.Actor, body: body, image: default, tick: tick);
        }
    }
    /// <summary>Admits a world's own compiled producer before its body consumes the resulting image. The
    /// structural actor is local-only; external submissions cannot claim the World principal.</summary>
    internal PlayerIntent AdmitProgramSnapshot(int body, PlayerIntent image, ulong tick) {
        m_host.Population.ThrowIfPartitioned();
        if (m_host.Body(index: body) is not { Source.ProducerName: { } source } target) { return default; }
        var standing = StandingProgramOwner(body: body, target: target);

        // The same observation over standing facts: the admission would take the owner's steady branch and answer the
        // projection it answered last.
        if ((standing?.Program is { Repeats: true } held) && (image == held.Image) && standing.Input.HoldsSteady(image: in held.Accepted)) {
            WorldTickWork.Process.Add(WorldTickWork.InputAdmissionsSteady, 1);
            return held.Projection;
        }
        WorldTickWork.Process.Add(((standing is null) ? WorldTickWork.InputAdmissions : WorldTickWork.InputAdmissionsStanding), 1);
        if (!TryPublishSnapshotCore(Principal.World, body, source, image, tick, default, out var owner, out var principal, out var batch, out var reason, standing) ||
            !TryAdmitSynchronized(alreadyPermitted: true, batch: in batch, owner: owner!, reason: out reason)) {
            if (standing?.Program is { } refused) { refused.Valid = false; }
            RejectControllerBatch(principal: principal);
            m_host.Output.Narrate("input.refused", reason);
            return default;
        }
        var projection = ProjectSnapshot(batch: batch, body: body);

        RecordProgramStanding(accepted: batch.Image, image: in image, owner: owner!, projection: in projection, source: source, target: target, tick: tick);
        return projection;
    }

    private PlayerIntent ProjectSnapshot(int body, in WorldInputBatch batch) {
        if (batch.Image == default) { return default; }
        var target = m_host.Body(index: body)!;
        var mapping = Catalog.Mappings[batch.Mapping];
        var channels = mapping.Channels;
        if (ReferenceEquals(objA: channels, objB: target.Channels)) { return batch.Image; }
        var accepted = default(ChannelValues);
        var remap = channels.RemapTo(target: target.Channels);

        foreach (var logical in mapping.Live) {
            var address = mapping.Address(logicalId: logical);

            if (address < 0) { continue; }
            var destination = remap[address];

            if (destination < 0) { continue; }
            if (channels.Shape(ordinal: address) == ChannelShape.Relative) {
                accepted.SetRelative(ordinal: destination, value: batch.Image.Channels.Relative(ordinal: address));
            } else {
                accepted[destination] = batch.Image[address];
            }
        }
        return batch.Image with { Channels = accepted };
    }

    /// <summary>Publishes a complete synthetic observation through a generation-bearing input session. The caller
    /// still submits under the actual actor for grants and contribution consent; the session owns only its history.
    /// The returned batch borrows producer storage and must be admitted synchronously before another publication.</summary>
    public bool TryPublishSnapshot(Principal actor, int body, string source, PlayerIntent image, ulong tick,
        out Principal principal, out WorldInputBatch batch, out string reason, ReadOnlySpan<WorldInputEvent> observations = default) =>
        TryPublishSnapshotCore(actor, body, source, image, tick, observations, out _, out principal, out batch, out reason);

    // standing names the owner of a world's own producer whose permission and selection facts all stand
    // (StandingProgramOwner): the route check, the owner lookup and the synchronization would answer what they did.
    private bool TryPublishSnapshotCore(Principal actor, int body, string source, PlayerIntent image, ulong tick,
        ReadOnlySpan<WorldInputEvent> observations, out Owner? resolvedOwner, out Principal principal, out WorldInputBatch batch, out string reason, Owner? standing = null) {
        resolvedOwner = null;
        principal = default;
        batch = default;
        reason = "input.owner: invalid snapshot producer or route";
        if (string.IsNullOrWhiteSpace(value: source) || (source.Length > 256) || (body < 0) || (body >= m_host.Population.Capacity) ||
            (m_host.Body(index: body) is not { } target)) { return false; }
        var observedChannels = target.Channels;
        var catalog = Catalog;
        var owner = standing;

        if (owner is null) {
            if (!TryResolveSnapshotOwner(actor: actor, body: body, catalog: catalog, owner: out owner, reason: out reason, source: source, target: target)) { return false; }
            if (!TrySynchronize(activation: 0, owner: owner, reason: out reason)) { return false; }
        }
        return TryPublishSnapshotOf(owner: owner, catalog: catalog, image: image, observations: observations, observedChannels: observedChannels, tick: tick,
            batch: out batch, principal: out principal, reason: out reason, resolvedOwner: out resolvedOwner);
    }
    // Resolves the controller owner a snapshot producer publishes through, admitting one for a first observation and
    // replacing one whose body generation or session ended.
    private bool TryResolveSnapshotOwner(Principal actor, int body, string source, WorldBody target, WorldInputCatalog catalog, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out Owner? owner, out string reason) {
        owner = null;
        reason = string.Empty;
        if (m_host.GrantTable.TryGetDriveGate(bodyIndex: body, gateRow: out _) ||
            (!IsAuthoredProducer(actor: actor, body: body, source: source) &&
            !m_host.GrantTable.Allows(actor, WorldCapability.Drive, GrantSubject.Body(index: body)).IsAllowed)) {
            reason = "input.permission: snapshot producer cannot drive its target";
            return false;
        }
        var key = (actor, body, source);

        if (m_controllers.TryGetValue(key: key, value: out owner) &&
            ((owner.Generation != m_host.Population.Generation(index: body)) || !m_host.GrantTable.IsLiveSession(principal: owner.Principal))) {
            Reset(owner.Principal);
            m_host.GrantTable.MarkObserverEnded(session: owner.Principal);
            RemoveOwner(owner: owner);
            m_controllers.Remove(key: key);
            owner = null;
        }
        if (owner is null) {
            if (m_ordered.Count == ushort.MaxValue) { reason = "input.capacity: too many input owners"; return false; }
            var sources = catalog.SnapshotSources(prefix: "$snapshot:");
            var session = m_host.GrantTable.AdmitInputControllerSession();

            try { owner = PrepareOwner(session, body, sources, 1, controller: new(actor, source)); } catch (ArgumentException error) { m_host.GrantTable.MarkObserverEnded(session: session); reason = error.Message; return false; }
            // Adding a local observation stream does not detach the body's existing scripted durations.
            InstallOwner(owner, catalog.Mappings[target.InputMappingId], reset: false, recompiled: true);
            m_controllers.Add(key: key, value: owner);
        }
        return true;
    }
    // Observes the snapshot into the owner's producer and publishes its batch, over storage the producer lends.
    private bool TryPublishSnapshotOf(Owner owner, WorldInputCatalog catalog, PlayerIntent image, ulong tick, WorldChannelTable observedChannels,
        ReadOnlySpan<WorldInputEvent> observations, out Owner? resolvedOwner, out Principal principal, out WorldInputBatch batch, out string reason) {
        resolvedOwner = null;
        principal = default;
        batch = default;
        reason = string.Empty;
        var input = owner.Input;
        var producer = owner.Producer ??= WorldInputProducer.FromAccepted(catalog, input.Mapping, input.Sources,
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
            var inputSources = input.Sources;
            if (!ReferenceEquals(objA: owner.SnapshotChannels, objB: observedChannels) ||
                !ReferenceEquals(objA: owner.SnapshotCatalog, objB: catalog) ||
                (owner.SnapshotAddresses.Length != inputSources.Length)) {
                var addresses = ((owner.SnapshotAddresses.Length == inputSources.Length) ? owner.SnapshotAddresses : new int[inputSources.Length]);

                for (var sourceIndex = 0; (sourceIndex < inputSources.Length); sourceIndex++) {
                    addresses[sourceIndex] = (observedChannels.TryGetOrdinal(name: catalog.Channels[inputSources[sourceIndex].Source.Channel].Name, ordinal: out var resolved) ? resolved : -1);
                }
                owner.SnapshotAddresses = addresses;
                owner.SnapshotChannels = observedChannels;
                owner.SnapshotCatalog = catalog;
            }
            var cachedAddresses = owner.SnapshotAddresses;
            var relative = false;

            for (var sourceIndex = 0; (sourceIndex < inputSources.Length); sourceIndex++) {
                var address = cachedAddresses[sourceIndex];
                var declaration = catalog.Channels[inputSources[sourceIndex].Source.Channel];
                var raw = ((address < 0) ? 0 :
                    ((declaration.Shape == ChannelShape.Relative) ? image.Channels.Relative(ordinal: address).Value : image[address].Value));

                relative |= ((declaration.Shape == ChannelShape.Relative) && (raw != 0));
                // A snapshot's newly nonzero channel is one activation; all its later samples retain that origin.
                _ = producer.ObserveSnapshotAt(index: sourceIndex, rawValue: raw, tick: tick, activationSequence: ref activationSequence);
            }
            owner.SnapshotRelative = relative;
        }
        owner.Controller = origin with { ActivationSequence = activationSequence, ObservationTick = tick };
        if (producer.CaptureLost) { Reset(owner.Principal); reason = "input.capacity: synthetic snapshot exceeds the event allowance"; return false; }
        resolvedOwner = owner;
        principal = owner.Principal;
        batch = producer.PublishBorrowed(tick, (continuingOnly ? input.Image.SourceRay : image.SourceRay));
        reason = string.Empty;
        return true;
    }
    /// <summary>Checks that a submitted input session belongs to this exact actor and routing subject.</summary>
    public bool IsController(Principal principal, Principal actor, int body) => (m_owners.TryGetValue(key: principal, value: out var owner) &&
        (owner.Controller is { } controller) && (controller.Actor == actor) && (owner.Body == body) &&
        (owner.Generation == m_host.Population.Generation(index: body)) && m_host.GrantTable.IsLiveSession(principal: principal));

    // Synthetic producer sessions have no socket observer by design. Their owner and origin are checkpointed
    // input state, and the producer's own detach/replacement path retires them.
    internal bool OwnsControllerSession(Principal principal) => (m_owners.TryGetValue(key: principal, value: out var owner) && (owner.Controller is not null));

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
