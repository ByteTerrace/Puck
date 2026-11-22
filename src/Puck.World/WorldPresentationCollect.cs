using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The composition root's collect: once a host step, after every instance has stepped and on the pump inside the
/// boundary window, it copies what the frame reads of the authority into the values <see cref="WorldPresentationFeed"/>
/// publishes (the boot world's tick record, every running world's machine presentation, the diagnostic copy). This is
/// the one place those values are read for the frame; the frame's types live in <c>Puck.World.Presentation</c>, whose
/// architecture profile forbids the server and the machines, so they read nothing else of the authority. Each value is
/// reused while what it copies has not moved, so a step that changes nothing publishes the same objects.
/// </summary>
/// <param name="server">The boot authority.</param>
/// <param name="instances">The running world instances, whose machine hosts the table captures.</param>
/// <param name="history">The boot world's in-session history.</param>
/// <param name="diagnostics">The diagnostics whose copy the overlay draws.</param>
/// <param name="feed">The feed the copies are published through.</param>
/// <param name="drawnReport">Reads the report the overlay last drew, or <see langword="null"/> for a host that draws
/// none.</param>
internal sealed class WorldPresentationCollect(WorldServer server, WorldInstanceHost instances, WorldHistory history, WorldDiagnostics diagnostics, WorldPresentationFeed feed, Func<WorldDiagnosticReport?> drawnReport) {
    private WorldMachineTable m_machines = WorldMachineTable.Empty;
    private WorldObservationTable m_observations = WorldObservationTable.Empty;
    private WorldTickRecord m_tick = WorldTickRecord.Empty;

    /// <summary>Copies and publishes one collect's values. Call on the pump inside the boundary window: after a host
    /// step's instances have stepped, as the World boots, and after a timeline is restored outside a step.</summary>
    public void Collect() => feed.Publish(
        diagnostics: diagnostics.Collect(drawn: drawnReport),
        machines: (m_machines = WorldPresentationCopy.Machines(held: m_machines, instances: instances)),
        observations: (m_observations = WorldPresentationCopy.Observations(held: m_observations, instances: instances)),
        tick: CopyTick()
    );

    private WorldTickRecord CopyTick() {
        var held = m_tick;
        var places = held.SeatPlaces;
        string?[]? fresh = null;

        for (var seat = 0; (seat < WorldBodiesLimits.LocalSeatCount); seat++) {
            var place = server.Events.SeatPlace(seat: seat);

            if ((fresh is null) && ((seat >= places.Count) || !string.Equals(a: places[seat], b: place, comparisonType: StringComparison.Ordinal))) {
                fresh = new string?[WorldBodiesLimits.LocalSeatCount];
                for (var kept = 0; (kept < seat); kept++) {
                    fresh[kept] = places[kept];
                }
            }
            if (fresh is not null) {
                fresh[seat] = place;
            }
        }

        var population = server.Population;
        var commands = WorldConsoleCommands.For(definition: server.Configuration);
        var version = server.DocumentVersion;
        var tick = (server.NextInputTick - 1UL);
        var window = WorldPresentationCopy.HistoryWindow(history: history);

        if ((fresh is null) && (tick == held.Tick) && (version == held.DocumentVersion) && (window == held.History) &&
            (population.SimulatedCount == held.SimulatedBodies) && (population.PeerCapacity == held.PeerCapacity) &&
            ReferenceEquals(objA: commands, objB: held.Commands)) {
            return held;
        }

        return m_tick = new WorldTickRecord(
            Commands: commands,
            DocumentVersion: version,
            History: window,
            PeerCapacity: population.PeerCapacity,
            SeatPlaces: ((IReadOnlyList<string?>?)fresh ?? places),
            SimulatedBodies: population.SimulatedCount,
            Tick: tick
        );
    }
}
