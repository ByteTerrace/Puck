using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>A local producer's actual authorizing actor and stable source, independent of its input session identity.</summary>
public readonly record struct WorldInputControllerOrigin(Principal Actor, string Source, ulong ActivationSequence = 0, ulong ObservationTick = 0);

public sealed partial class WorldInputRuntime {
    private bool IsAuthoredProducer(Principal actor, int body, string source) => actor.Kind == PrincipalKind.World &&
        m_host.Body(body)?.Source.ProducerName == source &&
        m_host.Population.SupportsSource(body, m_host.Body(body)!.Source, out _);

    /// <summary>Admits a world's own compiled producer before its body consumes the resulting image. The
    /// structural actor is local-only; external submissions cannot claim the World principal.</summary>
    internal PlayerIntent AdmitProgramSnapshot(int body, PlayerIntent image, ulong tick) {
        if (m_host.Body(body) is not { Source.ProducerName: { } source } target) { return default; }
        if (!TryPublishSnapshot(Principal.World, body, source, image, tick, out var principal, out var batch, out var reason) ||
            !TryAdmit(principal, batch, out reason, body)) {
            RejectControllerBatch(principal);
            m_host.Output.Narrate("input.refused", reason);
            return default;
        }
        var channels = Catalog.Mappings[batch.Mapping].Channels;
        var accepted = batch.Image with { Channels = default };
        foreach (var logical in Catalog.Mappings[batch.Mapping].Live) {
            var address = Catalog.Mappings[batch.Mapping].Address(logical);
            if (address < 0 || !target.Channels.TryGetOrdinal(channels.Name(address)!, out var destination)) { continue; }
            accepted = channels.Shape(address) == ChannelShape.Relative
                ? accepted.WithRelative(destination, batch.Image.Channels.Relative(address))
                : accepted.WithChannel(destination, batch.Image[address]);
        }
        return accepted;
    }

    private static WorldInputSource[] SnapshotProfile(WorldInputCatalog catalog) {
        var sources = new List<WorldInputSource>();
        for (var channel = 0; channel < catalog.Channels.Length; channel++) {
            var declaration = catalog.Channels[channel];
            if (declaration.Shape == ChannelShape.Text) { continue; }
            sources.Add(new((ushort)channel, (ushort)channel,
                declaration.Shape == ChannelShape.Relative ? WorldInputContributionKind.Relative : WorldInputContributionKind.Level,
                "$snapshot:" + declaration.Name));
        }
        return sources.ToArray();
    }
    /// <summary>Publishes a complete synthetic observation through a generation-bearing input session. The caller
    /// still submits under the actual actor for grants and contribution consent; the session owns only its history.
    /// The returned batch borrows producer storage and must be admitted synchronously before another publication.</summary>
    public bool TryPublishSnapshot(Principal actor, int body, string source, PlayerIntent image, ulong tick,
        out Principal principal, out WorldInputBatch batch, out string reason) {
        principal = default;
        batch = default;
        reason = "input.owner: invalid snapshot producer or route";
        if (string.IsNullOrWhiteSpace(source) || source.Length > 256 || body < 0 || body >= m_host.Population.Capacity ||
            m_host.Body(body) is not { } target) { return false; }
        var observedChannels = target.Channels;
        if (m_host.GrantTable.TryGetDriveGate(body, out _) ||
            !IsAuthoredProducer(actor, body, source) &&
            !m_host.GrantTable.Allows(actor, WorldCapability.Drive, GrantSubject.Body(body)).IsAllowed) {
            reason = "input.permission: snapshot producer cannot drive its target";
            return false;
        }
        var key = (actor, body, source);
        if (m_controllers.TryGetValue(key, out var owner) &&
            (owner.Generation != m_host.Population.Generation(body) || !m_host.GrantTable.IsLiveSession(owner.Principal))) {
            Reset(owner.Principal);
            m_host.GrantTable.MarkObserverEnded(owner.Principal);
            RemoveOwner(owner);
            m_controllers.Remove(key);
            owner = null;
        }
        if (owner is null) {
            if (m_ordered.Count == ushort.MaxValue) { reason = "input.capacity: too many input owners"; return false; }
            var sources = SnapshotProfile(Catalog);
            var session = m_host.GrantTable.AdmitInputControllerSession();
            try { owner = PrepareOwner(session, body, sources, 1, controller: new(actor, source)); }
            catch (ArgumentException error) { m_host.GrantTable.MarkObserverEnded(session); reason = error.Message; return false; }
            InstallOwner(owner);
            m_controllers.Add(key, owner);
        }
        if (!TrySynchronize(owner, 0, out reason)) { return false; }
        var input = owner.Input;
        var producer = owner.Producer ??= WorldInputProducer.FromAccepted(Catalog, input.Mapping, input.Sources,
            m_host.Definition.InputHistory?.MaximumEventsPerStep ?? 128, input.Revision, input.Epoch, input.Sequence, input.Image);
        producer.Reconcile(input.Revision, input.Epoch, input.Mapping, input.Image, input.Sources);
        var origin = owner.Controller!.Value;
        var activationSequence = origin.ActivationSequence;
        foreach (var state in input.Sources) {
            var contract = state.Source;
            var declaration = Catalog.Channels[contract.Channel];
            var raw = !observedChannels.TryGetOrdinal(declaration.Name, out var address) ? 0 :
                declaration.Shape == ChannelShape.Relative ? image.Channels.Relative(address).Value : image[address].Value;
            // A snapshot's newly nonzero channel is one activation; all its later samples retain that origin.
            _ = producer.ObserveSnapshot(contract.Handle, raw, tick, ref activationSequence);
        }
        owner.Controller = origin with { ActivationSequence = activationSequence, ObservationTick = tick };
        if (producer.CaptureLost) { Reset(owner.Principal); reason = "input.capacity: synthetic snapshot exceeds the event allowance"; return false; }
        principal = owner.Principal;
        batch = producer.PublishBorrowed(tick, image.SourceRay);
        reason = string.Empty;
        return true;
    }

    /// <summary>Checks that a submitted input session belongs to this exact actor and routing subject.</summary>
    public bool IsController(Principal principal, Principal actor, int body) => m_owners.TryGetValue(principal, out var owner) &&
        owner.Controller is { } controller && controller.Actor == actor && owner.Body == body &&
        owner.Generation == m_host.Population.Generation(body) && m_host.GrantTable.IsLiveSession(principal);

    /// <summary>Finishes one pure snapshot producer's interval. An omitted target contributes neutral through the
    /// same event admission, so stopping a snapshot stream releases its holds rather than leaving them in history.</summary>
    public void CompleteSnapshots(Principal actor, string source, ulong tick) {
        foreach (var owner in m_ordered) {
            if (owner.Controller is not { } origin || origin.Actor != actor || origin.Source != source ||
                origin.ObservationTick == tick || !m_host.GrantTable.IsLiveSession(owner.Principal)) { continue; }
            if (owner.Body < 0 || owner.Generation != m_host.Population.Generation(owner.Body) || m_host.Body(owner.Body) is null) {
                Reset(owner.Principal);
                m_host.GrantTable.MarkObserverEnded(owner.Principal);
                continue;
            }
            if (TryPublishSnapshot(actor, owner.Body, source, default, tick, out var principal, out var batch, out _) &&
                !TryAdmit(principal, batch, out _, owner.Body)) { owner.Producer = null; }
        }
    }

    /// <summary>Cancels a detached, disabled or replaced producer. Reattachment receives a new session generation.</summary>
    public void CancelSnapshots(Principal actor, string source) {
        foreach (var owner in m_ordered) {
            if (owner.Controller is not { } origin || origin.Actor != actor || origin.Source != source ||
                !m_host.GrantTable.IsLiveSession(owner.Principal)) { continue; }
            Reset(owner.Principal);
            m_host.GrantTable.MarkObserverEnded(owner.Principal);
            owner.Producer = null;
        }
    }

    internal void RejectControllerBatch(Principal principal) {
        if (m_owners.TryGetValue(principal, out var owner) && owner.Controller is not null) { owner.Producer = null; }
    }
}
