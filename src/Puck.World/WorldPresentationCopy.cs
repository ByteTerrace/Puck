using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The copies a collect makes of authority state for the frame (<see cref="WorldPresentationFeed"/>), each a pure
/// read of what it is handed. Call on the pump inside the boundary window.</summary>
public static class WorldPresentationCopy {
    /// <summary>Copies an in-session history's window as a frame reads it.</summary>
    /// <param name="history">The history.</param>
    /// <returns>The window, or <see langword="null"/> while the history holds none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="history"/> is <see langword="null"/>.</exception>
    public static WorldHistoryWindow? HistoryWindow(WorldHistory history) {
        ArgumentNullException.ThrowIfNull(argument: history);

        return (((history.OldestTick is { } oldest) && (history.HeadTick is { } head) && (history.CursorTick is { } cursor))
            ? new WorldHistoryWindow(Cursor: cursor, Head: head, Oldest: oldest, WindowTicks: history.WindowTicks)
            : null);
    }
    /// <summary>Copies the sessions every running world's screens and infinity layers observe, each running instance's
    /// render envelope and the boot endpoint. Call on the pump inside the boundary window, after the host settled its
    /// sessions.</summary>
    /// <param name="instances">The running world instances.</param>
    /// <param name="held">The table the last copy made, returned again when nothing it holds moved; each session whose
    /// values did not move keeps its record.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldObservationTable Observations(WorldInstanceHost instances, WorldObservationTable held) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentNullException.ThrowIfNull(argument: held);

        var changed = false;
        var running = held.Running;
        var names = instances.Names;

        if (running.Count != names.Count) {
            changed = true;
        } else {
            foreach (var name in names) {
                if (!running.TryGetValue(key: name, value: out var envelope) || !instances.TryGet(instance: out var instance, name: name) ||
                    (instance is null) || !ReferenceEquals(objA: envelope, objB: instance.Server.Envelope)) {
                    changed = true;
                    break;
                }
            }
        }
        if (changed) {
            var fresh = new Dictionary<string, WorldRenderEnvelope>(comparer: StringComparer.Ordinal);

            foreach (var name in names) {
                if (instances.TryGet(instance: out var instance, name: name) && (instance is not null)) {
                    fresh[name] = instance.Server.Envelope;
                }
            }
            running = fresh;
        }

        var boot = (instances.TryEndpoint(endpoint: out var endpoint, name: WorldDefinitionLoader.BootInstanceName) ? endpoint : null);

        changed |= !ReferenceEquals(objA: boot, objB: held.BootEndpoint);

        var screens = CopySessions(changed: ref changed, held: held.Screens, instances: instances, published: instances.ScreenSessions);
        var layers = CopySessions(changed: ref changed, held: held.Layers, instances: instances, published: instances.InfinitySessions);

        return (changed ? new WorldObservationTable(bootEndpoint: boot, layers: layers, running: running, screens: screens) : held);
    }
    // Copies one index of published sessions, keeping each record whose values did not move and the held index itself
    // when no record moved and no key came or went.
    private static IReadOnlyDictionary<TKey, WorldObservedSession> CopySessions<TKey>(WorldInstanceHost instances,
        IReadOnlyDictionary<TKey, WorldObservationSession> published, IReadOnlyDictionary<TKey, WorldObservedSession> held, ref bool changed)
        where TKey : notnull {
        var moved = (published.Count != held.Count);
        Dictionary<TKey, WorldObservedSession>? copied = null;

        foreach (var (key, session) in published) {
            var was = held.GetValueOrDefault(key: key);
            var record = Session(held: was, instances: instances, session: session);

            moved |= !ReferenceEquals(objA: record, objB: was);
            (copied ??= new Dictionary<TKey, WorldObservedSession>(capacity: published.Count))[key] = record;
        }
        if (!moved) {
            return held;
        }

        changed = true;

        return ((IReadOnlyDictionary<TKey, WorldObservedSession>?)copied ?? new Dictionary<TKey, WorldObservedSession>());
    }
    // One session's record: the held one while every value it copies is the same.
    private static WorldObservedSession Session(WorldObservationSession session, WorldObservedSession? held, WorldInstanceHost instances) {
        if ((held is not null) && !ReferenceEquals(objA: held.Token, objB: session)) {
            held = null;
        }

        var admission = Admission(held: held?.Observation, instanceName: session.InstanceName, instances: instances, observation: session.Observation);

        if ((held is not null) && ReferenceEquals(objA: admission, objB: held.Observation) && Equals(objA: held.Source, objB: session.Source) &&
            string.Equals(a: held.InstanceName, b: session.InstanceName, comparisonType: StringComparison.Ordinal) &&
            (held.GenerationId == session.GenerationId) && string.Equals(a: held.Refusal, b: session.Refusal, comparisonType: StringComparison.Ordinal)) {
            return held;
        }

        return new WorldObservedSession(
            GenerationId: session.GenerationId,
            InstanceName: session.InstanceName,
            Mirror: session.Mirror,
            Observation: admission,
            Refusal: session.Refusal,
            Source: session.Source,
            Token: session
        );
    }
    // One admission's record: the held one while every value it copies is the same.
    private static WorldObservationAdmission? Admission(WorldSessionObservation? observation, WorldObservationAdmission? held, string? instanceName, WorldInstanceHost instances) {
        if (observation is null) {
            return null;
        }
        if ((held is not null) && !ReferenceEquals(objA: held.Token, objB: observation)) {
            held = null;
        }

        var proof = observation.SceneProof;
        var share = ((proof is null) ? (WorldSceneShare?)null : new WorldSceneShare(End: proof.End, First: proof.First, Scene: proof.Scene, SharesAuthority: proof.SharesAuthority, SharesContent: proof.SharesContent));
        var holds = ((proof is not null) && (instanceName is not null) && instances.TryGet(instance: out var instance, name: instanceName) &&
            (instance is not null) && observation.Observes(server: instance.Server) && proof.IssuedBy(server: instance.Server));
        var window = (((instanceName is not null) && instances.TryWindowEndpoint(endpoint: out var endpoint, name: instanceName, observation: observation)) ? endpoint : null);

        if ((held is not null) && (held.Ended == observation.Ended) && (held.CanObserve == observation.CanObserve) && (held.Tier == observation.Tier) &&
            (held.Session == observation.Session) && (held.Proof == share) && (held.ProofHolds == holds) && ReferenceEquals(objA: held.Window, objB: window)) {
            return held;
        }

        return new WorldObservationAdmission(
            CanObserve: observation.CanObserve,
            Disclose: (held?.Disclose ?? observation.Disclose),
            Ended: observation.Ended,
            Proof: share,
            ProofHolds: holds,
            Session: observation.Session,
            Tier: observation.Tier,
            Token: observation,
            Window: window
        );
    }
    /// <summary>Captures every running world instance's machine presentation, by instance name. Call on the pump inside
    /// the boundary window.</summary>
    /// <param name="instances">The running world instances.</param>
    /// <param name="held">The table the last capture made, returned again when no world's presentation moved.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static WorldMachineTable Machines(WorldInstanceHost instances, WorldMachineTable held) {
        ArgumentNullException.ThrowIfNull(argument: instances);
        ArgumentNullException.ThrowIfNull(argument: held);

        var names = instances.Names;
        var copied = ((held.Count != names.Count) ? new KeyValuePair<string, WorldMachinePresentation>[names.Count] : null);

        for (var index = 0; (index < names.Count); index++) {
            var name = names[index];
            var presentation = ((instances.TryGet(instance: out var instance, name: name) && (instance is not null))
                ? instance.Server.Machines.CapturePresentation()
                : WorldMachinePresentation.None);

            if ((copied is null) && (!string.Equals(a: held.At(index: index).Key, b: name, comparisonType: StringComparison.Ordinal) || !ReferenceEquals(objA: held.At(index: index).Value, objB: presentation))) {
                copied = new KeyValuePair<string, WorldMachinePresentation>[names.Count];
                for (var kept = 0; (kept < index); kept++) {
                    copied[kept] = held.At(index: kept);
                }
            }
            if (copied is not null) {
                copied[index] = new(key: name, value: presentation);
            }
        }

        return ((copied is null) ? held : new WorldMachineTable(worlds: copied));
    }
}
