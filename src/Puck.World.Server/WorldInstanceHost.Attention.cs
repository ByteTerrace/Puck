using Puck.Maths;

namespace Puck.World;

// SIMULATION FOLLOWS ATTENTION. A world's document states the least attention it steps at
// (simulation.stepsAt); the host reads each running world's attention once a host step, from authority facts alone,
// and a wave row that stands below its document's bar lapses instead of stepping (SettleLapsed, in the waves).
//
// The invariant: a world's state is a function of its document and its input, and a lapse is part of that input: the
// host decides it, the row's tape records it, and a re-drive applies it. What enforces it:
// - One reading. ReadAttention is the only place attention is derived, and it reads authority state: who stands in
//   each world (the population), which sessions the host's worlds hold on each other and how far each surface's
//   attention reaches (fixed-point distances the owning world's settle measured), and whether an observer in another
//   process is attached. No presentation, camera or clock enters it, and nothing about whether the row is recorded,
//   so a recorded host and an unrecorded one lapse the same ticks.
// - One door. Lapses is the only place a row is told not to step for want of attention, and it answers false for a
//   world whose document authors no bar (WorldDefinition.SimulationStepsAt reads Unobserved, which nothing is below).
// - One clock move, written down where it is made. WorldTick.Lapse is the only way an authority's time passes without
//   a step; SettleLapsed is its one caller outside a replay and notes the lapse on the row's tape as it calls it, and
//   a replay reaches it only by applying that entry (WorldReplayEntry.Lapse).
// Where it stops: a row that neighbours another world or holds a transfer in flight always steps, because a neighbour
// and a lease read its ticks one by one, and a row whose tape is driving steps the ticks, and takes the lapses, its
// tape recorded. A desktop's boot row is stepped by the pump, never by a wave, so it never lapses.
public sealed partial class WorldInstanceHost {
    // Each running world's attention as of the host step being composed; a world absent from it is unobserved.
    private readonly Dictionary<string, WorldAttention> m_attention = new(comparer: StringComparer.Ordinal);

    /// <summary>Reads the attention a running instance stood at when the host last composed a host step.</summary>
    /// <param name="name">The instance name.</param>
    /// <returns>The attention, or <see cref="WorldAttention.Unobserved"/> for a name that is not running.</returns>
    public WorldAttention AttentionOf(string name) => m_attention.GetValueOrDefault(key: name);

    // What an observation gives its destination at most, for an owning world at one attention: a world someone stands
    // in watches through its surfaces, and a world that is itself only observed gives a glimpse.
    private static WorldAttention Through(WorldAttention owner) => owner switch {
        WorldAttention.Occupied => WorldAttention.Watched,
        WorldAttention.Watched or WorldAttention.Glimpsed => WorldAttention.Glimpsed,
        _ => WorldAttention.Unobserved,
    };

    // Reads every running world's attention: occupied where a human stands in it, watched where an observer from
    // another process is attached or where it is the boot world a presentation shows, and otherwise the greatest any
    // session of another world gives it, each bounded by its surface's reach and by what its own world passes on.
    // Attention only rises as the walk repeats, so it ends at the one fixed point whatever order the sessions are met.
    private void ReadAttention() {
        m_attention.Clear();
        foreach (var (name, instance) in m_instances) {
            if (IsOccupied(instance: instance)) {
                m_attention[name] = WorldAttention.Occupied;
            } else if (
                (instance.Server.Output.Watchers > 0) ||
                (string.Equals(a: name, b: BootInstanceName, comparisonType: StringComparison.Ordinal) && !SuppressesClientBootScreens(boot: instance))
            ) {
                m_attention[name] = WorldAttention.Watched;
            }
        }

        for (var changed = true; changed;) {
            changed = false;
            foreach (var (owner, owned) in m_screenSessions) {
                var through = Through(owner: m_attention.GetValueOrDefault(key: owner));

                if (through == WorldAttention.Unobserved) {
                    continue;
                }
                for (var index = 0; (index < owned.Rows.Count); index++) {
                    var session = owned.Rows.Values[index];

                    if ((session.Observation is not { Ended: false }) || (session.InstanceName is not { } observed)) {
                        continue;
                    }

                    var given = ((session.Attention < through) ? session.Attention : through);

                    if (given > m_attention.GetValueOrDefault(key: observed)) {
                        m_attention[observed] = given;
                        changed = true;
                    }
                }
            }
        }
        foreach (var (name, instance) in m_instances) {
            instance.Attention = m_attention.GetValueOrDefault(key: name);
        }
    }

    // THE LAPSE DECISION. Whether a wave row lapses this host step in place of stepping: it stands below the attention
    // its document steps at, and nothing obliges it to step. A row that neighbours another world and a row holding a
    // transfer always step (see the remarks above); so does a row that owes a step for input only a step applies, and
    // a row whose tape is driving, which takes its lapses from the tape. Whether the row is being recorded is no part
    // of the answer.
    private static bool Lapses(WorldInstance instance) {
        var server = instance.Server;
        var definition = server.Configuration;

        return (
            (instance.Attention < definition.SimulationStepsAt) &&
            !instance.StepOwed &&
            (instance.Tape is not { Mode: WorldReplayMode.Replaying }) &&
            (definition.Adjacencies is not { Count: > 0 }) &&
            (server.TransferEscrow.Counts.ActiveTransactions == 0) &&
            !server.HasQueuedIntents
        );
    }

    // Reads how closely each of an owning world's sessions attends its destination, from the owning world's own
    // population: the squared distance from the surface's frame to the nearest body a human occupies within the
    // surface's farthest authored reach, against that reach. Runs in the owning world's settle, when its population
    // may be read; a surface that authors no reach reads no body, and the population is read once however many do.
    private static void ReadSiteAttention(WorldInstance owner, SortedList<WorldObservationSite, WorldObservationSession> sessions) {
        WorldFaceCatalog? faces = null;

        for (var index = 0; (index < sessions.Count); index++) {
            var session = sessions.Values[index];
            // An infinity layer is a far view of its destination: it never gives more than a glimpse.
            var most = ((sessions.Keys[index] is WorldObservationSite.InfinityLayer) ? WorldAttention.Glimpsed : WorldAttention.Watched);

            if (session.Source.Attention is not { } reach) {
                session.Attention = most;

                continue;
            }

            FixedQ4816? nearest = null;

            if (sessions.Keys[index] is WorldObservationSite.Screen screen) {
                if (faces is null) {
                    faces = WorldFaceCatalog.For(definition: owner.Server.Configuration);
                    owner.Server.Population.ReadOccupied();
                }
                // By index: the rows are read through their interface, whose enumerator is an allocation a session a tick.
                var rows = faces.Rows;

                for (var face = 0; (face < rows.Count); face++) {
                    var row = rows[face];

                    if (row.ScreenIndex == screen.Index) {
                        // Nobody past the farthest authored reach changes the reading, so the search ends there.
                        nearest = owner.Server.Population.NearestOccupiedSquared(
                            point: row.Frame.Origin,
                            reach: FixedQ4816.FromDouble(value: MathF.Max(x: (reach.WatchedWithin ?? 0f), y: (reach.GlimpsedWithin ?? 0f)))
                        );

                        break;
                    }
                }
            }

            var read = WorldSessionAttention.At(attention: reach, nearestSquared: nearest);

            session.Attention = ((read < most) ? read : most);
        }
    }
}
