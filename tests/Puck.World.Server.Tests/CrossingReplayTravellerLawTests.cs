using System.Numerics;
using Puck.Commands;
using Puck.Maths;
using Puck.Networking;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>
/// CONTRACT UNDER TEST (acceptance law 2): a crossing, recorded and replayed, reaches the same traveller. A profiled
/// seat walks into a mapped door turned a quarter turn from its counterpart, crosses to the other row of one host,
/// and walks on there, while the source row records with the destination as its companion. The set verifies with its
/// crossing verified, both tapes replay with no divergence in their authoritative hash traces, and at every recorded
/// tick the replayed destination holds the traveller the live one held: its mobility credential (incarnation, epoch,
/// departed-from), generation, profile id and projection, pose, yaw, planar and vertical velocity, travel turn,
/// catalog rig and the census. The red leg strips the arrival from the destination tape, which then diverges at the
/// arrival tick.
/// </summary>
public sealed class CrossingReplayTravellerLawTests {
    private const int StepsAfterArrival = 10;
    private const string TravellerId = "traveller";
    private const int WalkBound = 120;

    // Everything the law compares about the traveller on one server, or its absence and the census.
    private static string Traveller(WorldServer server) {
        var population = server.Population;

        for (var slot = 0; (slot < population.Capacity); slot++) {
            if ((population.EntryBody(index: slot) is { Profile.Id: TravellerId } body) && population.IsActive(index: slot)) {
                var state = body.CaptureTransferState();
                var projection = new WireWriter();

                WorldIdentityProjectionWire.Write(projection: body.Profile!.Project(), writer: projection);
                return string.Join(separator: " ", values: [
                    $"slot={slot}",
                    $"mobility={population.CurrentMobility(index: slot)}",
                    $"generation={population.Generation(index: slot)}",
                    $"projection={Convert.ToHexString(inArray: projection.WrittenSpan.ToArray())}",
                    $"position={body.FixedPosition}",
                    $"yaw={body.FixedYaw}",
                    $"planar={state.PlanarVelocity}",
                    $"vertical={state.VerticalVelocity}",
                    $"turn={population.TravelTurn(index: slot)}",
                    $"rig={population.CatalogRig(index: slot)}",
                    $"census={population.SimulatedCount}",
                ]);
            }
        }
        return $"absent census={population.SimulatedCount}";
    }
    private static WorldReplaySnapshot Read(WorldReplayTape tape, string name) {
        using var stream = File.OpenRead(path: tape.PathFor(name: name));

        return WorldReplaySnapshot.Read(stream: stream);
    }
    private static WorldReplayHashTraces Drive(WorldReplaySnapshot tape, WorldServer live, Action<int, WorldServer>? observeTick = null) => tape.DriveTraces(
        addonHostFactory: static (_, _) => new NullAddonHost(),
        engines: [],
        machineHostFactory: Fixtures.MachineHostFactory,
        observeTick: observeTick,
        profiles: live.Profiles
    );
    private static void Submit(WorldInstance row, int entityIndex) => ((LoopbackTransport)row.Link).SubmitIntent(submission: new IntentSubmission(
        Tick: row.Server.NextInputTick,
        EntityIndex: entityIndex,
        Intent: default(PlayerIntent).WithChannel(ordinal: 0, value: FixedQ4816.One),
        Principal: Principal.Seat(slot: 0)
    ));

    [Fact]
    public void AWalkedMappedCrossingReplaysToTheSameTraveller() {
        using var files = new TemporaryDirectory(prefix: "puck-crossing-traveller-files-");
        using var tapes = new TemporaryDirectory(prefix: "puck-crossing-traveller-tapes-");
        using var hostState = new TemporaryDirectory(prefix: "puck-crossing-traveller-host-");
        var rowAPath = files.PathOf(name: "row-a.world.json");
        var rowBPath = files.PathOf(name: "row-b.world.json");
        var rowADefinition = PortalWalkThroughLawTests.Row(
            door: PortalWalkThroughLawTests.Door(
                id: "door-a",
                portal: new WorldPlacementPortal(
                    Arrival: WorldPortalArrival.Mapped,
                    Counterpart: $"door-b/{PortalWalkThroughLawTests.Face}",
                    Destination: "neighbour",
                    Travel: WorldPortalTravel.Body
                ),
                position: new Vector3(x: 0f, y: 0f, z: -10f),
                yawDegrees: 180f
            ),
            neighbourPath: rowBPath
        );
        var rowBDefinition = PortalWalkThroughLawTests.Row(
            door: PortalWalkThroughLawTests.Door(id: "door-b", portal: null, position: new Vector3(x: 30f, y: 0f, z: 30f), yawDegrees: 90f),
            neighbourPath: rowAPath
        );

        // Seat 0 stands a short walk in front of row A's door, facing it, high enough to stay above the door's floor as
        // it falls: authored, because a tape re-establishes the document and the seats, never a pose set before it.
        rowADefinition = rowADefinition with {
            SpawnPointsRaw = [.. rowADefinition.SpawnPoints.Select(selector: point => ((point.Id == "seat-1") ? (point with { Position = new Vector3(x: 0f, y: 4.5f, z: -3f) }) : point))],
        };
        FileBackedRows.Write((rowAPath, rowADefinition), (rowBPath, rowBDefinition));

        using var host = new WorldInstanceHost(
            admitsSpawn: true,
            applicationStopping: CancellationToken.None,
            machineHostFactory: Fixtures.MachineHostFactory,
            machineId: Guid.NewGuid(),
            resolver: new WorldSessionResolver(),
            seats: WorldEmbodiedSeats.None,
            stateRoot: new WorldStateRoot(path: hostState.RootPath)
        );

        var (rowA, rowAServer, rowAState) = FileBackedRows.Build(definition: rowADefinition, name: "row-a", path: rowAPath);
        var (rowB, rowBServer, rowBState) = FileBackedRows.Build(definition: rowBDefinition, name: "row-b", path: rowBPath);

        using (rowAState)
        using (rowBState) {
            host.Admit(row: rowA);
            host.Admit(row: rowB);
            Assert.NotNull(@object: rowAServer.Profiles.Create(colorHex: "#22aa66", name: SafeName.Parse(candidate: TravellerId), reason: out _));
            Assert.True(condition: rowAServer.ApplySession(request: new SessionRequest.Join(IdentityName: TravellerId, Principal: Principal.Seat(slot: 0), Slot: 0, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
            // A second seat keeps the source running once the traveller leaves it.
            Assert.True(condition: rowAServer.ApplySession(request: new SessionRequest.Join(IdentityName: null, Principal: Principal.Seat(slot: 1), Slot: 1, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
            // The recording starts with the rows' first tick.
            var tape = new WorldReplayTape(
                addonHostFactory: static (_, _) => new NullAddonHost(),
                engines: [],
                liveServer: rowAServer,
                machineHostFactory: Fixtures.MachineHostFactory,
                profiles: rowAServer.Profiles,
                stateRoot: new WorldStateRoot(path: tapes.RootPath),
                transport: ((LoopbackTransport)rowA.Link)
            );
            var name = $"crossing-{Guid.NewGuid():N}";
            var live = new List<string>();
            var arrivedAt = -1;

            rowA.Tape = tape;
            Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: refusal);
            host.RecordCompanions(tape: tape);

            for (var step = 0; ((step < WalkBound) && ((arrivedAt < 0) || (step < (arrivedAt + StepsAfterArrival)))); step++) {
                host.DrainPendingTransfers();
                if (rowAServer.Population.IsActive(index: 0)) {
                    Submit(entityIndex: 0, row: rowA);
                } else {
                    Submit(entityIndex: 0, row: rowB);
                }
                host.StepInstances(masterDeltaTicks: Fixtures.StepTicks);
                live.Add(item: Traveller(server: rowBServer));
                if ((arrivedAt < 0) && !live[^1].StartsWith(comparisonType: StringComparison.Ordinal, value: "absent")) {
                    arrivedAt = step;
                }
            }

            var stop = tape.StopRecording();

            Assert.Null(@object: stop.VerifyFault);
            Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());
            Assert.True(condition: (arrivedAt > 0), userMessage: $"the traveller never crossed; it stands at {rowAServer.Body(index: 0)?.FixedPosition}");
            // The controls: the traveller arrived moving, carrying its profile.
            Assert.DoesNotContain(expectedSubstring: $"planar={FixedVector3.Zero}", actualString: live[arrivedAt]);
            Assert.Contains(expectedSubstring: Convert.ToHexString(inArray: System.Text.Encoding.UTF8.GetBytes(s: TravellerId)), actualString: live[arrivedAt]);
            Assert.True(condition: Assert.Single(collection: stop.Verdict.Crossings).Verified, userMessage: stop.Verdict.Describe());

            var primary = Read(name: name, tape: tape);
            var companion = Assert.Single(collection: primary.Companions);
            var replayed = new List<string>();

            Assert.Equal(expected: -1, actual: HashTrace.FirstDivergence(left: primary.RecordedAuthoritativeHashes, right: Drive(live: rowAServer, tape: primary).Authoritative));
            Assert.Equal(
                expected: -1,
                actual: HashTrace.FirstDivergence(
                    left: companion.RecordedAuthoritativeHashes,
                    right: Drive(live: rowBServer, observeTick: (_, server) => replayed.Add(item: Traveller(server: server)), tape: companion).Authoritative
                )
            );
            Assert.Equal(actual: replayed, expected: live);

            // The red leg: the destination tape with its arrival stripped replays identically up to the arrival and
            // diverges on it.
            var stripped = CrossingReplayLawTests.KeepingAuthority(keep: static entry => (entry is not WorldReplayEntry.Arrival), tape: companion);

            Assert.Equal(expected: arrivedAt, actual: HashTrace.FirstDivergence(left: companion.RecordedAuthoritativeHashes, right: Drive(live: rowBServer, tape: stripped).Authoritative));
        }
    }
}
