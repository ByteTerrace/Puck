using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>Generation-bearing input owner, routing subject and its complete admission state.</summary>
public sealed record WorldInputParticipantCheckpoint(Principal Principal, int Body, int Generation,
    WorldInputSeatState[] SeatStates, WorldInputOwnerCheckpoint Input, WorldInputControllerOrigin? Controller = null);
/// <summary>Exact historical intervals and semantic input owners; independent of attached hardware.</summary>
public sealed record WorldInputRuntimeCheckpoint(WorldInputInterval Interval, WorldInputInterval[] Intervals,
    WorldInputParticipantCheckpoint[] Owners) {
    /// <summary>No admitted input owners or elapsed intervals.</summary>
    public static WorldInputRuntimeCheckpoint Empty { get; } = new(Interval: default, Intervals: [], Owners: []);
}
public sealed partial class WorldInputRuntime {
    private static int ComparePrincipal(Principal a, Principal b) {
        var order = a.Kind.CompareTo(target: b.Kind);

        if (order != 0) { return order; }
        order = a.Index.CompareTo(value: b.Index);
        if (order != 0) { return order; }
        order = a.Generation.CompareTo(value: b.Generation);
        return ((order != 0) ? order : string.CompareOrdinal(strA: a.Name, strB: b.Name));
    }

    /// <summary>Captures canonical owner order and chronological interval order at a checkpoint boundary.</summary>
    public WorldInputRuntimeCheckpoint Capture() {
        var intervals = new WorldInputInterval[m_intervalCount];
        var start = (((m_intervalNext - m_intervalCount) + m_intervals.Length) % m_intervals.Length);

        for (var index = 0; (index < intervals.Length); index++) { intervals[index] = m_intervals[((start + index) % m_intervals.Length)]; }
        return new(Interval: Interval, Intervals: intervals, Owners: m_ordered.Select(selector: static owner => new WorldInputParticipantCheckpoint(
            owner.Principal, owner.Body, owner.Generation,
            owner.SeatStates.OrderBy(static pair => pair.Key, StringComparer.Ordinal).Select(selector: static pair => new WorldInputSeatState(Family: pair.Key, State: pair.Value)).ToArray(),
            owner.Input.Capture(), owner.Controller)).ToArray());
    }
    /// <summary>Builds a fully validated replacement before any live authority state is changed.</summary>
    public static WorldInputRuntime PrepareRestore(WorldServer host, WorldDefinition definition, WorldInputRuntimeCheckpoint checkpoint) {
        var runtime = new WorldInputRuntime(definition: definition, host: host);
        var catalog = WorldInputCatalog.For(definition: definition);

        if (checkpoint.Intervals.Length > runtime.m_intervals.Length) { throw new ArgumentException(message: "input checkpoint exceeds retained interval budget"); }
        WorldInputInterval previous = default;

        foreach (var interval in checkpoint.Intervals) {
            if ((interval.EngineDuration == 0) || (interval.Tick <= previous.Tick) ||
                ((previous.Tick != 0) && (interval.EngineStart != (previous.EngineStart + previous.EngineDuration)))) {
                throw new ArgumentException(message: "input checkpoint intervals are not chronological and contiguous");
            }
            previous = interval;
        }
        if ((checkpoint.Intervals.Length != 0) && (previous != checkpoint.Interval)) { throw new ArgumentException(message: "input checkpoint current interval differs from its history"); }
        runtime.Interval = checkpoint.Interval;
        checkpoint.Intervals.CopyTo(array: runtime.m_intervals, index: 0);
        runtime.m_intervalCount = checkpoint.Intervals.Length;
        runtime.m_intervalNext = (runtime.m_intervalCount % runtime.m_intervals.Length);
        foreach (var saved in checkpoint.Owners) {
            if ((saved.Body < -1) || (saved.Body >= definition.Population.Capacity) ||
                ((runtime.m_ordered.Count != 0) && (ComparePrincipal(a: runtime.m_ordered[^1].Principal, b: saved.Principal) >= 0))) {
                throw new ArgumentException(message: "input checkpoint owner identity or body is invalid");
            }
            var input = new WorldInputOwner(catalog, catalog.DefaultMapping, (definition.InputHistory ?? new()),
                saved.Input.Sources.Select(selector: static row => row.Source).ToArray(), saved.Input.Revision, definition.InputPatterns);

            input.Restore(checkpoint: saved.Input);
            var owner = new Owner {
                Principal = saved.Principal,
                Body = saved.Body,
                Generation = saved.Generation,
                Definition = definition,
                Input = input,
                Selection = input.Mapping.States.ToArray(),
                Facing = new int[(definition.InputHistory?.Crossings?.Count ?? 0)],
                Controller = saved.Controller,
            };

            if ((saved.Controller is { } controller) && ((saved.Principal.Kind != PrincipalKind.Session) || (saved.Body < 0) ||
                string.IsNullOrWhiteSpace(value: controller.Source) || (controller.Source.Length > 256) ||
                !runtime.m_controllers.TryAdd(key: (controller.Actor, saved.Body, controller.Source), value: owner))) {
                throw new ArgumentException(message: "input checkpoint controller identity is invalid or duplicate");
            }
            foreach (var cell in saved.SeatStates) {
                var family = definition.SeatModes.FirstOrDefault(predicate: row => (row.Name == cell.Family));

                if ((family is null) || !family.States.Any(predicate: row => (row.Name == cell.State)) || !owner.SeatStates.TryAdd(key: cell.Family, value: cell.State)) {
                    throw new ArgumentException(message: "input checkpoint seat state is undeclared or duplicate");
                }
            }
            if (owner.SeatStates.Count != definition.SeatModes.Count) { throw new ArgumentException(message: "input checkpoint omits a finite seat state"); }
            runtime.m_owners.Add(key: owner.Principal, value: owner);
            runtime.m_ordered.Add(item: owner);
            runtime.IndexBodyOwner(owner: owner);
        }
        return runtime;
    }
    /// <summary>Rebinds restored body consumers after population restoration without generating a switch or press.</summary>
    public void BindRestoredBodies() {
        m_host.Population.ActionStateLane.Input = this;
        foreach (var owner in m_ordered) {
            owner.KitName = ((owner.Body >= 0) ? m_host.Population.CandidateKitName(bodyIndex: owner.Body, definition: owner.Definition) : null);
            if (OwnsBodyMapping(owner: owner)) { m_host.Population.RestoreInputMapping(bodyIndex: owner.Body, owner: owner.Input); }
        }
    }
    /// <summary>Hashes all future-decision state in canonical order, with no temporary allocations.</summary>
    public void AppendStateHash(ref Fnv1aHash hash) {
        Interval.AppendStateHash(hash: ref hash);
        hash.Add(value: ((uint)m_intervalCount));
        var start = (((m_intervalNext - m_intervalCount) + m_intervals.Length) % m_intervals.Length);

        for (var index = 0; (index < m_intervalCount); index++) {
            var interval = m_intervals[((start + index) % m_intervals.Length)];

            interval.AppendStateHash(hash: ref hash);
        }
        hash.Add(value: ((uint)m_ordered.Count));
        foreach (var owner in m_ordered) {
            hash.Add(value: ((byte)owner.Principal.Kind)); hash.Add(value: owner.Principal.Index); hash.Add(value: owner.Principal.Generation); hash.Add(value: owner.Principal.Name);
            hash.Add(value: owner.Body); hash.Add(value: owner.Generation);
            hash.Add(value: ((byte)(owner.Controller.HasValue ? 1 : 0)));
            if (owner.Controller is { } controller) {
                hash.Add(value: ((byte)controller.Actor.Kind)); hash.Add(value: controller.Actor.Index); hash.Add(value: controller.Actor.Generation); hash.Add(value: controller.Actor.Name);
                hash.Add(value: controller.Source); hash.Add(value: controller.ActivationSequence); hash.Add(value: controller.ObservationTick);
            }
            // SeatModes is document order, which is also the stable authority's cell layout.
            foreach (var family in m_host.Document.Definition.SeatModes) { hash.Add(value: family.Name); hash.Add(value: owner.SeatStates[family.Name]); }
            owner.Input.AppendStateHash(hash: ref hash);
        }
    }
}
