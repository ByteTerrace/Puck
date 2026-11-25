using Puck.Commands;
using Puck.Maths;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The authority's followed rows (<see cref="WorldStateFollow"/>): each holds a row of a world this one
/// observes, and is written only by its adoption here.</summary>
/// <remarks>
/// <para>The invariant is one value with one writer: a followed row reads what its leader's row reads, at the leader's
/// tick that stands beside this authority's, within one tick's advance of the leader's row. The one place that enforces
/// it is <see cref="AdoptFollows"/>, which runs at the host boundary that resolves neighbours, where no authority holds a
/// tick: it compares the row with the leader's latest delivery and, where they differ, writes the leader's value
/// through the taped mutation door as the world's own program, so the tick about to step reads it, a replay drive
/// re-applies it from the tape, and every reader of the row (rules, the clock it keys, every recipient's delivery and
/// anchor) reads the leader's value. Every other writer is refused at the cell doors by name.</para>
/// <para>A followed destination this world also neighbours reads the adjacency's own observation; any other is
/// observed once, as a session, for as long as a row follows it.</para>
/// </remarks>
public sealed partial class WorldAdjacencyFields {
    private readonly Dictionary<string, FollowLease> m_followLeases = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, FollowState> m_follows = new(comparer: StringComparer.Ordinal);

    // Where a followed row stands against its leader: the leader's ticks ahead of this authority's (fixed at each
    // adoption), the tick and delivery it was last compared at (a boundary that meets both again has nothing new, and an
    // adoption it wrote has yet to drain), and the one refusal narrated for a delivery it cannot adopt.
    private sealed class FollowState {
        public bool Adopted;
        public long LeaderAhead;
        public ulong ComparedTick = ulong.MaxValue;
        public ulong ComparedDelivery = ulong.MaxValue;
        public string? Narrated;
        // A delayed follow's values in flight, oldest first: each is what the leader's row read when this authority
        // observed a write of it, and the tick it arrives at. The last one observed is the leader's line as this
        // authority knows it: its value and the leader tick it was read at.
        public Queue<(ulong DueTick, long Raw)>? InFlight;
        public bool Observed;
        public long ObservedRaw;
        public long ObservedLeaderTick;
    }

    /// <summary>The most values one delayed follow holds in flight. A leader that writes more often than that inside
    /// one delay has its newest value replaced, so the follower still ends on the leader's last value and still
    /// never reads one early.</summary>
    public const int MaxFollowValuesInFlight = 256;
    private sealed record FollowLease(string InstanceName, ulong GenerationId, WorldSessionMirror Mirror, IDisposable Lease) : IDisposable {
        public void Dispose() => Lease.Dispose();
    }

    // Brings every followed row of the source authority to its leader's latest delivery. A boundary read: it attaches
    // observations through the host and reads the source's live arena.
    private void AdoptFollows(WorldInstance source) {
        var server = source.Server;
        var definition = server.Configuration;
        var followed = false;

        foreach (var row in definition.State) {
            if (row?.Follows is not { } follows) { continue; }

            followed = true;
            if (source.Tape?.DriveProgress is not null) { continue; }
            if (FollowMirror(source: source, destination: follows.Destination) is not { } mirror) { continue; }
            Adopt(server: server, definition: definition, row: row, follows: follows, mirror: mirror);
        }
        if (!followed && (m_followLeases.Count > 0)) { ReleaseFollows(); }
    }

    private void Adopt(WorldServer server, WorldDefinition definition, WorldStateRow row, WorldStateFollow follows, WorldSessionMirror mirror) {
        var name = row.Name.Value;
        var state = (m_follows.GetValueOrDefault(key: name) ?? (m_follows[name] = new FollowState()));
        var delivered = mirror.Tick;

        if ((mirror.SnapshotRevision < 1) || (mirror.StepTicks == 0UL) || (definition.SimulationRateHz <= 0)) { return; }

        var tick = server.NextInputTick;

        if ((state.ComparedTick == tick) && (state.ComparedDelivery == delivered)) { return; }
        state.ComparedTick = tick;
        state.ComparedDelivery = delivered;
        WorldFederationWork.Process.Add(kind: WorldFederationWork.FollowReads, amount: 1L);
        var engineTick = (server.CompletedEngineTicks + FixedTickConversion.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz)));
        var leaderTick = (state.Adopted ? (((long)tick) + state.LeaderAhead) : (((long)delivered) + 1L));
        var leaderRow = follows.LeaderRow(row: name);

        if (
            !TryReadLeader(mirror: mirror, row: leaderRow, tick: leaderTick, value: out var leader) ||
            !TryReadLeader(mirror: mirror, row: leaderRow, tick: (leaderTick + 1L), value: out var next)
        ) {
            NarrateFollow(state: state, text: $"'{name}' follows row '{leaderRow}' of '{follows.Destination}', which its delivery does not disclose");
            return;
        }
        if (leader.Kind != row.Kind) {
            NarrateFollow(state: state, text: $"'{name}' is a {StateSpelling.Kind(kind: row.Kind)} row and follows row '{leaderRow}' of '{follows.Destination}', a {StateSpelling.Kind(kind: leader.Kind)} row");
            return;
        }
        state.Narrated = null;

        if (follows.DelaySeconds > 0m) {
            AdoptDelayed(server: server, definition: definition, name: name, follows: follows, mirror: mirror, leaderRow: leaderRow, state: state, tick: tick, leaderTick: leaderTick, leader: leader);
            return;
        }

        if (state.Adopted && WorldStateReader.TryReadEasedValue(
            arena: server.Arena,
            definition: definition,
            engineTick: engineTick,
            key: null,
            row: out _,
            rowName: name,
            tick: tick,
            value: out var local
        ) && local.HasValue) {
            // One tick of the leader's own advance, and a unit of the floor its raw accumulation takes, is the most two
            // rows of one trait standing a tick apart read differently; a row the leader holds still is compared exactly.
            var step = Math.Abs(value: (next.Raw - leader.Raw));
            var tolerance = ((step == 0L) ? 0L : (step + 1L));

            if (Math.Abs(value: (leader.Raw - local.Raw)) <= tolerance) { return; }
        }

        state.Adopted = true;
        state.LeaderAhead = (leaderTick - ((long)tick));
        WorldFederationWork.Process.Add(kind: WorldFederationWork.FollowAdoptions, amount: 1L);
        server.AdoptFollowed(mutation: new WorldMutation.UpsertStateCell(
            Key: StateRow.SlotKey.Value,
            Kind: WorldDocumentWriteKind.Set,
            Principal: Principal.World,
            Row: name,
            Value: leader.Raw
        ));
    }

    // A delayed follow. The invariant: the row never reads a value earlier than its delay after this authority observed
    // the leader holding it, and values arrive in the order the leader wrote them. Both hold here and nowhere else:
    // an observed write joins the back of the line with its arrival tick, and only the front of the line is ever
    // written. A write is told from the row's own advance by reading the latest delivery back at the tick of the last
    // observation: the leader's trait is exact, so the two read alike unless something wrote the row since.
    private void AdoptDelayed(WorldServer server, WorldDefinition definition, string name, WorldStateFollow follows, WorldSessionMirror mirror, string leaderRow, FollowState state, ulong tick, long leaderTick, CellValue leader) {
        var inFlight = (state.InFlight ??= new Queue<(ulong DueTick, long Raw)>());
        var written = (
            !state.Observed ||
            !TryReadLeader(mirror: mirror, row: leaderRow, tick: state.ObservedLeaderTick, value: out var line) ||
            (Math.Abs(value: (line.Raw - state.ObservedRaw)) > 1L)
        );

        if (written) {
            var due = (tick + Math.Max(val1: 1UL, val2: FixedTickConversion.DurationTicks(ratePerSecond: ((ulong)definition.SimulationRateHz), seconds: follows.DelaySeconds)));

            // The leader's ticks ahead of this authority's are fixed at each observation, as an undelayed adoption
            // fixes them, so later boundaries read the leader at the tick that stands beside this one.
            state.Adopted = true;
            state.LeaderAhead = (leaderTick - ((long)tick));
            state.Observed = true;
            state.ObservedRaw = leader.Raw;
            state.ObservedLeaderTick = leaderTick;
            if (inFlight.Count >= MaxFollowValuesInFlight) {
                // The line is full: the newest value in flight gives way to this one, keeping its arrival tick.
                var held = inFlight.ToArray();

                inFlight.Clear();
                for (var index = 0; (index < (held.Length - 1)); index++) { inFlight.Enqueue(item: held[index]); }
                due = held[^1].DueTick;
                WorldFederationWork.Process.Add(kind: WorldFederationWork.FollowDelayOverflows, amount: 1L);
            }
            inFlight.Enqueue(item: (due, leader.Raw));
            WorldFederationWork.Process.Add(kind: WorldFederationWork.FollowDelayed, amount: 1L);
        }

        while ((inFlight.Count > 0) && (inFlight.Peek().DueTick <= tick)) {
            var arrived = inFlight.Dequeue();

            WorldFederationWork.Process.Add(kind: WorldFederationWork.FollowAdoptions, amount: 1L);
            server.AdoptFollowed(mutation: new WorldMutation.UpsertStateCell(
                Key: StateRow.SlotKey.Value,
                Kind: WorldDocumentWriteKind.Set,
                Principal: Principal.World,
                Row: name,
                Value: arrived.Raw
            ));
        }
    }

    // Reads the leader's row at one of its ticks, by its own trait from its latest delivery.
    private static bool TryReadLeader(WorldSessionMirror mirror, string row, long tick, out CellValue value) {
        var delivered = mirror.Tick;
        var engine = (((Int128)mirror.EngineTick) + ((((Int128)tick) - delivered) * mirror.StepTicks));

        if ((tick < 0L) || (engine < 0)) {
            value = default;
            return false;
        }
        return mirror.TryReadDelivered(rowName: row, tick: ((ulong)tick), engineTick: ((ulong)engine), value: out value);
    }

    // The observation a followed destination is read through: an adjacency's own when this world neighbours it, else
    // one held for the follows, attached at this boundary.
    private WorldSessionMirror? FollowMirror(WorldInstance source, string destination) {
        foreach (var handle in m_handles.Values) {
            if (string.Equals(a: handle.Identity.Destination, b: destination, comparisonType: StringComparison.Ordinal)) { return handle.Mirror; }
        }
        if (!m_instances.TryResolveObservedProjection(source, destination, out var instanceName, out var generation,
            out var definition, out var attach, out _) || (definition is null) || (attach is null)) {
            return null;
        }
        if (m_followLeases.TryGetValue(key: destination, value: out var held)) {
            if ((held.InstanceName == instanceName) && (held.GenerationId == generation)) { return held.Mirror; }
            held.Dispose();
            _ = m_followLeases.Remove(key: destination);
        }

        var mirror = new WorldSessionMirror(placeholder: definition);
        var lease = new FollowLease(InstanceName: instanceName, GenerationId: generation, Mirror: mirror, Lease: attach(mirror));

        m_followLeases.Add(key: destination, value: lease);
        return mirror;
    }

    private void NarrateFollow(FollowState state, string text) {
        if (string.Equals(a: state.Narrated, b: text, comparisonType: StringComparison.Ordinal) || !m_instances.Narration.HasNarrationSink) { return; }
        state.Narrated = text;
        m_instances.Narration.Narrate(channel: "world.follows", text: $"[world.follows: {m_sourceInstanceName}: {text}; the row keeps its own value]");
    }

    private void ReleaseFollows() {
        foreach (var lease in m_followLeases.Values) { lease.Dispose(); }
        m_followLeases.Clear();
        m_follows.Clear();
    }
}
