using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class FederationTransferLawTests {
    private static WorldReplayTape ArrivalTape(WorldFixture fixture, string directory, LoopbackTransport transport) => new(
        liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: [],
        machineHostFactory: Fixtures.MachineHostFactory, addonHostFactory: static (_, _) => new NullAddonHost(),
        stateRoot: new WorldStateRoot(path: directory));
    private static WorldReplaySnapshot ReadArrivalTape(WorldReplayTape tape) {
        using var stream = File.OpenRead(path: tape.PathFor(name: "arrivals"));

        return WorldReplaySnapshot.Read(stream: stream);
    }

    // A completion runs inside the ordered drain. Transfer admission must finish there, including its grants,
    // before a later member can refuse and roll the cohort back. The same slots can then arrive again this tick.
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ArrivalRollbackInsideAnOrderedCompletionCannotReadmitAGhost(bool livePeer) {
        using var directory = new TemporaryDirectory(prefix: "puck-arrival-order-");
        var refuse = true;
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 3),
            landingRefusal: ordinal => ((refuse && (ordinal == 2)) ? "cohort admission refused" : null));
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = ArrivalTape(fixture: fixture, directory: directory.RootPath, transport: transport);

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var request = PeerCohort(101, 4, 5, 6);

        if (livePeer) {
            request = request with { Members = request.Members.Select(selector: member => member with { Source = IntentSource.Live }).ToArray() };
        }
        Assert.True(condition: fixture.Server.ReserveTransfer(request: request).Accepted);
        var completed = false;

        transport.Query(query: new WorldQuery.PlayerWhere(Index: 0), completion: answer => {
            completed = true;
            Assert.False(condition: fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId,
                members: [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)], reason: out _));
        });
        Assert.True(condition: completed);
        foreach (var slot in new[] { 4, 5 }) {
            Assert.False(condition: fixture.Server.Population.IsActive(index: slot));
            Assert.Empty(collection: fixture.Server.GrantRows(principal: Principal.Peer(generation: 1, index: slot)));
            Assert.False(condition: fixture.Server.TransferEscrow.TryArrivalBorder(bodyIndex: slot, border: out _));
        }
        Assert.Equal(0, fixture.Server.Population.SimulatedCount);
        refuse = false;
        var retry = request with { TransferId = 102, Members = request.Members.Take(count: 2).ToArray() };

        Assert.True(condition: fixture.Server.ReserveTransfer(request: retry).Accepted);
        Assert.True(condition: fixture.Server.CommitTransfer(sourceAuthority: retry.SourceAuthority, transferId: retry.TransferId,
            members: [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)], reason: out var reason), userMessage: reason);
        foreach (var slot in new[] { 4, 5 }) {
            Assert.Equal(2, fixture.Server.Population.Generation(index: slot));
        }
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        var recorded = ReadArrivalTape(tape: tape);

        Assert.Equal(4, recorded.Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>().Count());
        Assert.Equal(-1, tape.Verify(name: "arrivals").DivergedAt);
    }

    private static WorldReplaySnapshot WithArrivalEntries(WorldReplaySnapshot recorded, IReadOnlyList<WorldReplayEntry> entries) => new() {
        DefinitionJson = recorded.DefinitionJson,
        SimulationRate = recorded.SimulationRate,
        MountedAddons = recorded.MountedAddons,
        RecordedHashes = recorded.RecordedHashes,
        RecordedAuthoritativeHashes = recorded.RecordedAuthoritativeHashes,
        Seats = recorded.Seats,
        Ticks = [new WorldReplayTickInput(Authority: entries, Intents: [])],
    };

    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Theory]
    public void AnArrivalReplayPreservesTheProfileIdentityAndOwnedRecords(bool peer, bool ownedDocument) {
        using var directory = new TemporaryDirectory(prefix: "puck-arrival-profile-");
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2));
        var tape = ArrivalTape(fixture, directory.RootPath, new LoopbackTransport(server: fixture.Server));
        var stats = CellName.Parse(candidate: "stats");
        var score = CellName.Parse(candidate: "score");
        var record = CellName.Parse(candidate: "Stats");
        var profile = WorldIdentity.FromProjection(new WorldIdentityProjection(
            Id: "traveler-id", Name: "Traveler", ColorHex: "#123456", MoveSpeed: FixedQ4816.One, TurnSpeed: FixedQ4816.One,
            Records: new WorldStateSection(
                Records: [new StateRecord(Name: record, Fields: [new StatePoolField(Name: score, Default: CellValue.Int(value: 42))])],
                Pools: [new StatePool(Name: stats, Record: record, Capacity: 1, Initial: [new StatePoolSeed(Slot: 0)])])),
            defaults: fixture.Server.Definition.PlayerDefaults);

        if (ownedDocument) {
            profile = new WorldIdentity(document: fixture.Server.Definition with {
                Identity = new WorldIdentityDefinition(Id: SafeName.Parse(candidate: "traveler-id"), Name: "Traveler", Color: "#123456",
                    MoveSpeedState: CellName.Parse(candidate: "move"), TurnSpeedState: CellName.Parse(candidate: "turn"), Records: [stats]),
                StateRaw = profile.RecordState,
            }, defaults: fixture.Server.Definition.PlayerDefaults);
            Assert.True(condition: profile.TrySetFact(key: CellName.Parse(candidate: "visited"), value: 7, changed: out _, reason: out var factRefusal), userMessage: factRefusal);
        }
        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var request = (peer ? PeerCohort(141, 4) : Reservation(border: "door", sourceAuthority: "source", transferId: 141));
        var reserved = fixture.Server.ReserveTransfer(request: request);

        Assert.True(condition: reserved.Accepted, userMessage: reserved.Reason);
        Assert.True(condition: fixture.Server.CommitTransfer(request.SourceAuthority, request.TransferId,
            [Turned(travelTurn: FixedQ4816.One) with { Profile = profile }], out var reason), userMessage: reason);
        var slot = Assert.Single(collection: reserved.BodyIndices);

        Assert.Equal("traveler-id", fixture.Server.Population.EntryBody(index: slot)!.Profile!.Id);
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        Assert.Equal(-1, tape.Verify(name: "arrivals").DivergedAt);
        using var replay = Fixtures.FreshServer(definition: fixture.Server.Definition);
        var drive = ArrivalTape(replay, directory.RootPath, new LoopbackTransport(server: replay.Server));

        Assert.True(condition: drive.TryBeginDrive(documentPath: null, forkName: null, name: "arrivals", refusal: out refusal, toTick: null), userMessage: refusal);
        drive.InjectDriveTick();
        replay.Step();
        drive.NoteTick();
        var restored = replay.Server.Population.EntryBody(index: slot)!.Profile!;

        Assert.Equal(profile.Id, restored.Id);
        Assert.Equal(profile.ColorHex, restored.ColorHex);
        Assert.True(condition: restored.TryReadRecord(field: score, record: stats, value: out var value));
        Assert.Equal(42, value.AsInt);
        if (ownedDocument) {
            Assert.Equal(profile.Facts!.Cells, restored.Facts!.Cells);
        }
    }
    [Fact]
    public void AnArrivalAfterTheStepBelongsToTheNextTapeTick() {
        using var directory = new TemporaryDirectory(prefix: "puck-arrival-tick-");
        using var fixture = Fixtures.FreshServer();
        var tape = ArrivalTape(fixture, directory.RootPath, new LoopbackTransport(server: fixture.Server));

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var request = Reservation(border: "door", sourceAuthority: "source", transferId: 131);

        Assert.True(condition: fixture.Server.ReserveTransfer(request: request).Accepted);
        _ = WorldServerStepShell.Step(server: fixture.Server, tape: tape,
            context: new FixedStepContext(Tick: 0, ElapsedTicks: 0, StepTicks: Fixtures.StepTicks),
            publishTick: tick => {
                Assert.True(condition: fixture.Server.CommitTransfer(request.SourceAuthority, request.TransferId,
                    [Turned(travelTurn: FixedQ4816.One) with { Position = new FixedVector3(X: default, Y: FixedQ4816.FromInteger(value: 3), Z: default) }],
                    out var reason), userMessage: reason);
            });
        _ = WorldServerStepShell.Step(server: fixture.Server, tape: tape, publishTick: static _ => { },
            context: new FixedStepContext(Tick: 1, ElapsedTicks: Fixtures.StepTicks, StepTicks: Fixtures.StepTicks));
        _ = tape.StopRecording();
        var recorded = ReadArrivalTape(tape: tape);

        Assert.Empty(collection: recorded.Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>());
        Assert.Single(collection: recorded.Ticks[1].Authority.OfType<WorldReplayEntry.Arrival>());
        Assert.Equal(-1, tape.Verify(name: "arrivals").DivergedAt);
    }
    [Fact]
    public void ASeatCanArriveDepartAndReturnBeforeOneStep() {
        using var directory = new TemporaryDirectory(prefix: "puck-arrival-return-");
        using var fixture = Fixtures.FreshServer();
        using var onward = Fixtures.FreshServer();
        var tape = ArrivalTape(fixture, directory.RootPath, new LoopbackTransport(server: fixture.Server));

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var request = Reservation(border: "door", sourceAuthority: "source", transferId: 121);

        void Commit(WorldServer server, WorldTransferReservationRequest offer) {
            Assert.True(condition: server.ReserveTransfer(request: offer).Accepted);
            Assert.True(condition: server.CommitTransfer(offer.SourceAuthority, offer.TransferId, [Turned(travelTurn: FixedQ4816.One)], out var reason), userMessage: reason);
        }
        Commit(fixture.Server, request);
        var departure = fixture.Server.Population.EnsureMobility(index: 0, authority: fixture.Server.AuthorityIdentity);

        Assert.True(condition: fixture.Server.Population.TryDetachSeatForTransfer(profile: out _, slot: 0));
        Commit(onward.Server, request with { TransferId = 122, Members = [request.Members[0] with { Mobility = departure }] });
        tape.NoteTransfer(departedBootSlots: [0], destinationName: "onward", generationId: 0, outcome: "committed:1/1",
            scopeKey: "seat", transferId: 122);
        var returning = onward.Server.Population.EnsureMobility(authority: "onward", index: 0);

        Assert.True(condition: onward.Server.Population.TryDetachSeatForTransfer(profile: out _, slot: 0));
        Commit(fixture.Server, request with {
            TransferId = 123,
            SourceAuthority = "onward",
            Members = [request.Members[0] with { Mobility = returning }],
        });
        Commit(fixture.Server, request with {
            TransferId = 124,
            Members = [request.Members[0] with { PreferredSlot = 1, Mobility = Mobility(index: 1) }],
        });
        Assert.Equal(2, fixture.Server.Population.Generation(index: 0));
        Assert.Equal(1, fixture.Server.Population.Generation(index: 1));
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        var recorded = ReadArrivalTape(tape: tape);

        Assert.Equal(3, recorded.Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>().Count());
        Assert.Equal(-1, tape.Verify(name: "arrivals").DivergedAt);
    }

    private static WorldReplaySnapshot RoundTripArrival(WorldReplaySnapshot recorded) {
        using var stream = new MemoryStream();

        WorldReplaySnapshot.Write(recording: recorded, stream: stream);
        stream.Position = 0;
        return WorldReplaySnapshot.Read(stream: stream);
    }

    // Each corrupt input starts from a real committed transfer. An arrival is never allowed to manufacture a peer
    // without its earlier admission, overwrite an occupied seat, or silently ignore a failed landing.
    [InlineData("missing-admission")]
    [InlineData("late-admission")]
    [InlineData("duplicate")]
    [InlineData("negative-slot")]
    [InlineData("large-slot")]
    [InlineData("zero-epoch")]
    [InlineData("generation")]
    [InlineData("shape")]
    [InlineData("continuum")]
    [InlineData("motion")]
    [Theory]
    public void ArrivalReplayRefusesMalformedOrOutOfOrderEntries(string fault) {
        using var directory = new TemporaryDirectory(prefix: "puck-arrival-codec-");
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2));
        var tape = ArrivalTape(fixture, directory.RootPath, new LoopbackTransport(server: fixture.Server));

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var request = PeerCohort(111, 4);

        Assert.True(condition: fixture.Server.ReserveTransfer(request: request).Accepted);
        Assert.True(condition: fixture.Server.CommitTransfer(request.SourceAuthority, request.TransferId,
            [Turned(travelTurn: FixedQ4816.One)], out var reason), userMessage: reason);
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        Assert.Equal(-1, tape.Verify(name: "arrivals").DivergedAt);
        var recorded = ReadArrivalTape(tape: tape);
        var arrival = Assert.Single(collection: recorded.Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>());
        var entries = recorded.Ticks[0].Authority.ToList();

        if (fault == "shape") {
            var bytes = WorldReplaySnapshot.Encode(recording: recorded);

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: sizeof(uint)), value: 6u);
            using var stream = new MemoryStream(buffer: bytes);
            var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldReplaySnapshot.Read(stream: stream));

            Assert.Contains("shape", exception.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }
        var index = entries.IndexOf(item: arrival);

        switch (fault) {
            case "missing-admission": entries = [arrival]; break;
            case "late-admission": entries.Reverse(); break;
            case "duplicate": entries.Add(item: arrival); break;
            case "negative-slot": entries[index] = arrival with { Value = arrival.Value with { Slot = -1 } }; break;
            case "large-slot": entries[index] = arrival with { Value = arrival.Value with { Slot = WorldBodiesLimits.CapacityCeiling } }; break;
            case "zero-epoch": entries[index] = arrival with { Value = arrival.Value with { Mobility = arrival.Value.Mobility with { Epoch = 0 } } }; break;
            case "generation": entries[index] = arrival with { Value = arrival.Value with { Generation = (arrival.Value.Generation + 1) } }; break;
            case "continuum":
                entries[index] = arrival with {
                    Value = arrival.Value with {
                        Member = arrival.Value.Member with {
                            Continuum = new WorldContinuumTrajectory(BoundaryEvents: 0, ConsumedThroughEngineTick: 5, ContinuumEndEngineTick: 5, ContinuumStartEngineTick: 10, PreviousPosition: default, SourceTick: 0),
                        },
                    },
                }; break;
            case "motion":
                entries[index] = arrival with {
                    Value = arrival.Value with {
                        Member = arrival.Value.Member with {
                            BodyMotionProgramName = "absent",
                        },
                    },
                }; break;
        }
        var malformed = WithArrivalEntries(entries: entries, recorded: recorded);

        if (fault == "motion") {
            var decoded = RoundTripArrival(recorded: malformed);

            Assert.Throws<InvalidDataException>(testCode: () => decoded.Drive(profiles: fixture.Server.Profiles, engines: [],
                machineHostFactory: Fixtures.MachineHostFactory, addonHostFactory: static (_, _) => new NullAddonHost()));
        } else {
            Assert.Throws<InvalidDataException>(testCode: () => RoundTripArrival(recorded: malformed));
        }
    }
}
