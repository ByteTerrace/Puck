using Puck.Commands;
using Puck.Hosting;
using Puck.Maths;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Presentation.Tests;

public sealed partial class FederationTransferLawTests {
    private static WorldReplayTape ArrivalTape(WorldFixture fixture, string directory, LoopbackTransport transport) => new(
        liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: [],
        machineHostFactory: Fixtures.MachineHostFactory, addonHostFactory: static (_, _) => new NullAddonHost(),
        stateRoot: new WorldStateRoot(path: directory));
    private static WorldReplaySnapshot ReadArrivalTape(WorldReplayTape tape, string name = "arrivals") {
        using var stream = File.OpenRead(path: tape.PathFor(name: name));

        return WorldReplaySnapshot.Read(stream: stream);
    }
    private static WorldCrossingArrival DecodeArrival(WorldReplayEntry.Arrival arrival) {
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecodeCrossingArrival(
                arrival: out var decoded,
                bytes: arrival.Encoded,
                reason: out var reason
            ),
            userMessage: reason
        );
        return decoded!;
    }
    // A recording that lands travelers cannot be driven into a live session (TryBeginDrive refuses it), so an
    // occupant comparison lands every taped arrival onto a fresh server through the reland a re-drive applies, at
    // its recorded tick, and steps once per recorded tick.
    private static void RelandRecordedArrivals(WorldFixture fixture, WorldReplaySnapshot recorded) {
        foreach (var tick in recorded.Ticks) {
            foreach (var arrival in tick.Authority.OfType<WorldReplayEntry.Arrival>()) {
                var decoded = DecodeArrival(
                    arrival: arrival
                );
                var reason = string.Empty;

                Assert.True(
                    condition: fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.TransferEscrow.TryReland(
                        arrival: decoded,
                        reason: out reason,
                        recorded: arrival.Outcome
                    )),
                    userMessage: reason
                );
            }
            fixture.Step();
        }
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
            Assert.Equal(WorldTransferStatus.Missing, fixture.Server.CommitTransfer(sourceAuthority: request.SourceAuthority, transferId: request.TransferId,
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
        Assert.True(condition: (fixture.Server.CommitTransfer(sourceAuthority: retry.SourceAuthority, transferId: retry.TransferId,
            members: [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)], reason: out var reason) == WorldTransferStatus.Committed), userMessage: reason);
        foreach (var slot in new[] { 4, 5 }) {
            Assert.Equal(2, fixture.Server.Population.Generation(index: slot));
        }
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        var recorded = ReadArrivalTape(tape: tape);

        var arrivals = recorded.Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>().ToArray();

        // The refused commit landed two travelers and rolled both back; the retry landed the same indices again.
        Assert.Equal(2, arrivals.Length);
        Assert.Equal([1, 1], arrivals[0].Outcome.Generations);
        Assert.True(condition: arrivals[0].Outcome.RolledBack);
        Assert.Equal([2, 2], arrivals[1].Outcome.Generations);
        Assert.False(condition: arrivals[1].Outcome.RolledBack);
        Assert.DoesNotContain(collection: recorded.Ticks[0].Authority, filter: static entry => (entry.GetType().Name == "PeerAdmitted"));
        Assert.Equal(-1, tape.Verify(name: "arrivals").Primary.DivergedAt);
    }

    private sealed class AnsweringCrossingLog(WorldCrossingDurability answer) : IWorldCrossingLog {
        public WorldCrossingDurability Append(in WorldCrossingEntry entry, out string reason) {
            reason = ((answer == WorldCrossingDurability.Durable)
                ? string.Empty
                : $"the log answered {answer}");
            return answer;
        }
    }

    // THE LAW: an arrival whose crossing record is not made durable replays as it ran. The commit lands every traveler
    // before it writes the record ahead of its answer; a refused or uncertain record rolls every landing back through
    // the one undo, so the destination embodies nothing. The landings advanced their indices' generations, which
    // outlive the rollback, so the tape holds the arrival with every landing rolled back, and the re-drive lands and
    // rolls back the same travelers. The red leg is a rollback the tape never hears of: the re-drive diverges at the
    // commit's tick.
    [InlineData(WorldCrossingDurability.Refused)]
    [InlineData(WorldCrossingDurability.Uncertain)]
    [Theory]
    public void AnArrivalWhoseRecordIsNotDurableReplaysAsItRan(WorldCrossingDurability answer) {
        using var directory = new TemporaryDirectory(prefix: "puck-arrival-durability-");
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2));
        var tape = ArrivalTape(fixture, directory.RootPath, new LoopbackTransport(server: fixture.Server));

        fixture.Server.InstallCrossingLog(log: new AnsweringCrossingLog(answer: answer));
        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var request = PeerCohort(151, 4, 5);

        Assert.True(condition: fixture.Server.ReserveTransfer(request: request).Accepted);
        Assert.Equal(
            actual: fixture.Server.CommitTransfer(request.SourceAuthority, request.TransferId,
                [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)], out _),
            expected: ((answer == WorldCrossingDurability.Uncertain)
                ? WorldTransferStatus.Uncertain
                : WorldTransferStatus.Missing)
        );
        foreach (var slot in new[] { 4, 5 }) {
            Assert.False(condition: fixture.Server.Population.IsActive(index: slot));
            Assert.Equal(1, fixture.Server.Population.Generation(index: slot));
        }
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        var arrival = Assert.Single(collection: ReadArrivalTape(tape: tape).Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>());

        Assert.True(condition: arrival.Outcome.RolledBack);
        Assert.Equal([1, 1], arrival.Outcome.Generations);
        Assert.Equal(-1, tape.Verify(name: "arrivals").Primary.DivergedAt);
    }

    private static WorldReplaySnapshot WithArrivalEntries(WorldReplaySnapshot recorded, IReadOnlyList<WorldReplayEntry> entries) => new() {
        Authority = recorded.Authority,
        DefinitionJson = recorded.DefinitionJson,
        Instance = recorded.Instance,
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
        Assert.True(condition: (fixture.Server.CommitTransfer(request.SourceAuthority, request.TransferId,
            [Turned(travelTurn: FixedQ4816.One) with { Profile = profile.Project() }], out var reason) == WorldTransferStatus.Committed), userMessage: reason);
        var slot = Assert.Single(collection: reserved.BodyIndices);

        Assert.Equal("traveler-id", fixture.Server.Population.EntryBody(index: slot)!.Profile!.Id);
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        Assert.Equal(-1, tape.Verify(name: "arrivals").Primary.DivergedAt);
        using var replay = Fixtures.FreshServer(definition: fixture.Server.Definition);

        RelandRecordedArrivals(fixture: replay, recorded: ReadArrivalTape(tape: tape));
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
        _ = WorldServerStepShell.Step(server: fixture.Server, tape: tape, pacing: HostPacing.OneTickPerFrame,
            context: new FixedStepContext(Tick: 0, ElapsedTicks: 0, StepTicks: Fixtures.StepTicks),
            publishTick: tick => {
                Assert.True(condition: (fixture.Server.CommitTransfer(request.SourceAuthority, request.TransferId,
                    [Turned(travelTurn: FixedQ4816.One) with { Position = new FixedVector3(X: default, Y: FixedQ4816.FromInteger(value: 3), Z: default) }],
                    out var reason) == WorldTransferStatus.Committed), userMessage: reason);
            });
        _ = WorldServerStepShell.Step(server: fixture.Server, tape: tape, pacing: HostPacing.OneTickPerFrame, publishTick: static _ => { },
            context: new FixedStepContext(Tick: 1, ElapsedTicks: Fixtures.StepTicks, StepTicks: Fixtures.StepTicks));
        _ = tape.StopRecording();
        var recorded = ReadArrivalTape(tape: tape);

        Assert.Empty(collection: recorded.Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>());
        Assert.Single(collection: recorded.Ticks[1].Authority.OfType<WorldReplayEntry.Arrival>());
        Assert.Equal(-1, tape.Verify(name: "arrivals").Primary.DivergedAt);
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
            Assert.True(condition: (server.CommitTransfer(offer.SourceAuthority, offer.TransferId, [Turned(travelTurn: FixedQ4816.One)], out var reason) == WorldTransferStatus.Committed), userMessage: reason);
        }
        Commit(fixture.Server, request);
        var departure = fixture.Server.Population.EnsureMobility(index: 0, authority: fixture.Server.AuthorityIdentity);

        Assert.NotNull(@object: fixture.Server.ExecuteAuthorityOperation(operation: () => fixture.Server.DetachForTransfer(slot: 0, transferId: 122)));
        Commit(onward.Server, request with { TransferId = 122, Members = [request.Members[0] with { Mobility = departure }] });
        tape.NoteTransfer(departedSlots: [0], destinationName: "onward", generationId: 0, outcome: "committed:1/1",
            scopeKey: "seat", target: "onward", targetRemote: false, transferId: 122);
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
        Assert.Equal(-1, tape.Verify(name: "arrivals").Primary.DivergedAt);
    }

    private static WorldReplaySnapshot RoundTripArrival(WorldReplaySnapshot recorded) {
        using var stream = new MemoryStream();

        WorldReplaySnapshot.Write(recording: recorded, stream: stream);
        stream.Position = 0;
        return WorldReplaySnapshot.Read(stream: stream);
    }

    // Each corrupt input starts from a real committed transfer of two travelers. Intake refuses an arrival no commit
    // could have decided: a token that arrives again after its commit stood, a body index outside every world or named
    // twice, a traveler with no mobility identity, a member whose carried motion has no sound shape, or an outcome
    // that does not name a landing for every traveler of a commit that stood. The re-drive refuses an arrival its own
    // escrow does not reproduce: a traveler landing at another generation, or a motion program the world lacks. An
    // arrival is never allowed to overwrite an occupied index or silently ignore a failed landing.
    [InlineData("duplicate")]
    [InlineData("negative-slot")]
    [InlineData("large-slot")]
    [InlineData("repeated-slot")]
    [InlineData("no-mobility")]
    [InlineData("turn")]
    [InlineData("continuum")]
    [InlineData("landings")]
    [InlineData("shape")]
    [InlineData("generation")]
    [InlineData("motion")]
    [Theory]
    public void ArrivalReplayRefusesMalformedEntries(string fault) {
        using var directory = new TemporaryDirectory(prefix: "puck-arrival-codec-");
        using var fixture = Fixtures.FreshServer(definition: Fixtures.PeerPopulationDocument(networkPlayers: 2));
        var tape = ArrivalTape(fixture, directory.RootPath, new LoopbackTransport(server: fixture.Server));

        Assert.True(condition: tape.TryBeginRecording(name: "arrivals", refusal: out var refusal), userMessage: refusal);
        var request = PeerCohort(111, 4, 5);

        Assert.True(condition: fixture.Server.ReserveTransfer(request: request).Accepted);
        Assert.True(condition: (fixture.Server.CommitTransfer(request.SourceAuthority, request.TransferId,
            [Turned(travelTurn: FixedQ4816.One), Turned(travelTurn: FixedQ4816.One)], out var reason) == WorldTransferStatus.Committed), userMessage: reason);
        fixture.Step();
        tape.NoteTick();
        _ = tape.StopRecording();
        Assert.Equal(-1, tape.Verify(name: "arrivals").Primary.DivergedAt);
        var recorded = ReadArrivalTape(tape: tape);
        var arrival = Assert.Single(collection: recorded.Ticks[0].Authority.OfType<WorldReplayEntry.Arrival>());
        var entries = recorded.Ticks[0].Authority.ToList();

        if (fault == "shape") {
            var bytes = WorldReplaySnapshot.Encode(recording: recorded);

            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: bytes.AsSpan(start: sizeof(uint)), value: 7u);
            using var stream = new MemoryStream(buffer: bytes);
            var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldReplaySnapshot.Read(stream: stream));

            Assert.Contains("shape", exception.Message, StringComparison.OrdinalIgnoreCase);
            return;
        }
        var index = entries.IndexOf(item: arrival);
        var decoded = DecodeArrival(arrival: arrival);

        WorldReplayEntry.Arrival Reencoded(WorldCrossingArrival changed) => arrival with { Encoded = WorldAuthorityCheckpointCodec.EncodeCrossingArrival(arrival: changed) };
        WorldCrossingArrival WithFirstMember(WorldTransferCommitMember member) => decoded with { Members = [member, decoded.Members[1]] };

        switch (fault) {
            case "duplicate": entries.Add(item: arrival); break;
            case "negative-slot": entries[index] = Reencoded(changed: decoded with { Slots = [-1, decoded.Slots[1]] }); break;
            case "large-slot": entries[index] = Reencoded(changed: decoded with { Slots = [WorldBodiesLimits.CapacityCeiling, decoded.Slots[1]] }); break;
            case "repeated-slot": entries[index] = Reencoded(changed: decoded with { Slots = [decoded.Slots[0], decoded.Slots[0]] }); break;
            case "no-mobility":
                entries[index] = Reencoded(changed: decoded with {
                    Request = decoded.Request with { Members = [decoded.Request.Members[0] with { Mobility = null }, decoded.Request.Members[1]] },
                }); break;
            case "turn": entries[index] = Reencoded(changed: WithFirstMember(member: decoded.Members[0] with { TravelTurn = FixedQ4816.FromInteger(value: 7) })); break;
            case "continuum":
                entries[index] = Reencoded(changed: WithFirstMember(member: decoded.Members[0] with {
                    Continuum = new WorldContinuumTrajectory(BoundaryEvents: 0, ConsumedThroughEngineTick: 5, ContinuumEndEngineTick: 5, ContinuumStartEngineTick: 10, PreviousPosition: default, SourceTick: 0),
                })); break;
            case "landings": entries[index] = arrival with { Outcome = arrival.Outcome with { Generations = [arrival.Outcome.Generations[0]] } }; break;
            case "generation": entries[index] = arrival with { Outcome = arrival.Outcome with { Generations = [(arrival.Outcome.Generations[0] + 1), arrival.Outcome.Generations[1]] } }; break;
            case "motion": entries[index] = Reencoded(changed: WithFirstMember(member: decoded.Members[0] with { BodyMotionProgramName = "absent" })); break;
        }
        var malformed = WithArrivalEntries(entries: entries, recorded: recorded);

        if (fault is "generation" or "motion") {
            var reread = RoundTripArrival(recorded: malformed);
            var exception = Assert.Throws<InvalidDataException>(testCode: () => reread.Drive(profiles: fixture.Server.Profiles, engines: [],
                machineHostFactory: Fixtures.MachineHostFactory, addonHostFactory: static (_, _) => new NullAddonHost()));

            Assert.StartsWith("ArrivalRefused:", exception.Message, StringComparison.Ordinal);
        } else {
            Assert.Throws<InvalidDataException>(testCode: () => RoundTripArrival(recorded: malformed));
        }
    }
}
