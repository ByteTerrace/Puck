using Puck.World.Server;
using Puck.Testing;
using Puck.Commands;
using Xunit;

using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Tests;

/// <summary>
/// The laws behind <c>replay.fork</c> and the live drive it rides on: the fork provenance header round-trips through
/// the tape; a fork's child carries the parent's leading tick groups verbatim ahead of its own live ticks; the live
/// drive's boot-image reset reproduces the parent's own recorded hashes on the running server (the discriminator that
/// the rebuild plus population-image doors really reach the boot image); the loopback mask keeps a live intent out of
/// a driven tick; and the child verifies standalone.
/// </summary>
public sealed class ReplayForkLawTests {
    [Fact]
    public void TapeHeaderPinsTheRuntimeKeyLedgerHashShape() {
        using var buffer = new MemoryStream();

        WorldReplaySnapshot.Write(
            stream: buffer,
            recording: Snapshot(
                forkedFrom: null,
                ticks: 0
            )
        );

        Assert.Equal(
            expected: 9u,
            actual: BitConverter.ToUInt32(
                startIndex: sizeof(uint),
                value: buffer.ToArray()
            )
        );
    }

    private static readonly WorldChannelTable Channels = WorldChannelTable.Compile(channels: Fixtures.BuildDocument().Channels);

    private static IntentSubmission Forward(ulong tick, FixedQ4816 forward, FixedQ4816 strafe = default) => new(
        Tick: tick,
        EntityIndex: 0,
        Intent: Channels.RoleOrdinals.Intent(
            moveAdvance: forward,
            moveStrafe: strafe
        ),
        Principal: Principal.Seat(slot: 0)
    );
    private static WorldReplaySnapshot ReadTape(WorldReplayTape tape, string name) {
        using var stream = File.OpenRead(path: tape.PathFor(name: name));

        return WorldReplaySnapshot.Read(stream: stream);
    }
    private static WorldReplaySnapshot RoundTrip(WorldReplaySnapshot recording) {
        using var buffer = new MemoryStream();

        WorldReplaySnapshot.Write(
            recording: recording,
            stream: buffer
        );
        buffer.Position = 0L;

        return WorldReplaySnapshot.Read(stream: buffer);
    }
    // One live tick the way WorldServerStepShell drives it: recorded input into the doors, the step, the close.
    private static void ShellStep(WorldFixture fixture, WorldReplayTape tape) {
        tape.InjectDriveTick();
        fixture.Step();
        tape.NoteTick();
    }
    private static WorldReplaySnapshot Snapshot(int ticks, WorldReplayForkProvenance? forkedFrom, IReadOnlyList<WorldReplayEntry>? firstTick = null, WorldDefinition? definition = null, IReadOnlyList<WorldReplaySeat>? seats = null) {
        var tickInputs = new List<WorldReplayTickInput>(capacity: ticks);
        var hashes = new ulong[ticks];

        for (var tick = 0; (tick < ticks); tick++) {
            tickInputs.Add(item: new WorldReplayTickInput(
                Authority: ((tick == 0)
                    ? (firstTick ?? [])
                    : []),
                Intents: []
            ));
            hashes[tick] = ((ulong)(tick + 1));
        }

        return new WorldReplaySnapshot {
            Authority = "boot",
            Instance = "boot",
            DefinitionJson = WorldDefinitionSerialization.Serialize(definition: (definition ?? Fixtures.BuildDocument())),
            ForkedFrom = forkedFrom,
            MountedAddons = [],
            RecordedHashes = hashes,
            RecordedAuthoritativeHashes = [.. hashes],
            Seats = (seats ?? []),
            SimulationRate = ((definition is null)
                ? 240U
                : ((uint)definition.SimulationRateHz)),
            Ticks = tickInputs,
        };
    }
    private static WorldReplaySnapshot WithSeatIdentity(int slot) => Snapshot(
        firstTick: [new WorldReplayEntry.SeatIdentity(
            Profile: new WorldIdentityProjection(Id: "guest", Name: "Guest", ColorHex: "#112233", MoveSpeed: null, TurnSpeed: null),
            Slot: slot
        )],
        forkedFrom: null,
        ticks: 1
    );

    // The switch a fork records for a rebound seat survives the tape file: its slot and the projection it names.
    [Fact]
    public void ASeatIdentityEntry_RoundTripsThroughTheTape() {
        var entry = Assert.IsType<WorldReplayEntry.SeatIdentity>(@object: Assert.Single(collection: RoundTrip(recording: WithSeatIdentity(slot: 1)).Ticks[0].Authority));

        Assert.Equal(expected: 1, actual: entry.Slot);
        Assert.Equal(expected: "guest", actual: entry.Profile.Id);
        Assert.Equal(expected: "#112233", actual: entry.Profile.ColorHex);
    }
    // A doctored slot would index straight past the local seats during a re-drive, so the reader refuses it by name.
    // The red leg drops the range check: the file reads back and the throw never comes.
    [Fact]
    public void ASeatIdentityEntry_NamingASlotOutsideTheLocalSeats_IsRefusedByName() {
        var slot = ((int)WorldBodiesLimits.LocalSeatCount);
        using var buffer = new MemoryStream();

        WorldReplaySnapshot.Write(
            stream: buffer,
            recording: WithSeatIdentity(slot: slot)
        );
        buffer.Position = 0L;

        var exception = Assert.ThrowsAny<Exception>(testCode: () => WorldReplaySnapshot.Read(stream: buffer));

        Assert.Contains(
            expectedSubstring: $"seat identity names slot {slot}",
            actualString: exception.Message
        );
    }
    // THE LAW: a recorded switch is applied against the population of the re-drive, not the host's seat ceiling. The tape
    // embeds a world authoring one local seat and switches seat 1, which the format's four-seat bound admits: the re-drive
    // refuses it by name rather than indexing a seat the world does not hold. The red leg applies the switch unchecked.
    [Fact]
    public void ASeatIdentityEntry_NamingASeatTheWorldDoesNotHold_IsRefusedByNameWhenApplied() {
        var basis = Fixtures.BuildDocument();
        var definition = basis with {
            PopulationRaw = basis.Population with {
                LocalSeatsRaw = 1,
                SeatActivationRaw = [SeatActivationPolicy.Eager],
                SeatSpawnsRaw = ["seat-1"],
                CapacityRaw = 1,
                PeerColorsRaw = new WorldSequence(Name: WorldSequence.Additive, Offset: -1, Step: 0.618034f),
                PeerVariationRaw = new WorldPopulationVariation(
                    Phase: new WorldSequence(Name: WorldSequence.Additive, Offset: -1, Step: 0.38196602f),
                    Weave: new WorldSequence(Name: WorldSequence.Additive, Offset: -1, Step: 0.618034f),
                    Activity: new WorldSequence(Name: WorldSequence.R2, Offset: 1, Step: 0f)
                ),
            },
        };
        var recording = Snapshot(
            definition: definition,
            firstTick: [new WorldReplayEntry.SeatIdentity(
                Profile: new WorldIdentityProjection(Id: "guest", Name: "Guest", ColorHex: "#112233", MoveSpeed: null, TurnSpeed: null),
                Slot: 1
            )],
            forkedFrom: null,
            seats: [new WorldReplaySeat(Profile: null, Slot: 0)],
            ticks: 1
        );

        using var fixture = Fixtures.FreshServer();

        var exception = Assert.Throws<InvalidDataException>(testCode: () => recording.Drive(
            addonHostFactory: static (_, _) => new NullAddonHost(),
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            profiles: fixture.Server.Profiles
        ));

        Assert.True(
            condition: exception.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "SeatSwitchRefused"
            ),
            userMessage: exception.Message
        );
    }
    [Fact]
    public void Cancel_EndsTheDriveWhereItStands_AndSeatsAreLiveAgain() {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-");

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: transport,
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var parent = $"drive-cancel-{Guid.NewGuid():N}";

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        Assert.True(
            condition: tape.TryBeginRecording(
                name: parent,
                refusal: out var refusal
            ),
            userMessage: $"refused to arm: {refusal}"
        );

        for (var tick = 0UL; (tick < 3UL); tick++) {
            transport.SubmitIntent(submission: Forward(
                tick: (tick + 1UL),
                forward: FixedQ4816.One
            ));
            fixture.Step();
            tape.NoteTick();
        }

        _ = tape.StopRecording();

        var parentTape = ReadTape(
            name: parent,
            tape: tape
        );

        Assert.True(
            condition: tape.TryBeginDrive(
                documentPath: null,
                forkName: "never-recorded",
                name: parent,
                refusal: out refusal,
                toTick: null
            ),
            userMessage: $"refused to drive: {refusal}"
        );
        ShellStep(
            fixture: fixture,
            tape: tape
        );
        Assert.Equal(
            expected: parentTape.RecordedHashes[0],
            actual: WorldReplaySnapshot.HashState(population: fixture.Server.Population)
        );

        Assert.Equal(
            expected: parent,
            actual: tape.CancelDrive()
        );
        // A cancel abandons the fork: Idle, never Recording, and the mask is lifted.
        Assert.Equal(
            expected: WorldReplayMode.Idle,
            actual: tape.Mode
        );
        Assert.False(condition: transport.InputMasked);

        // THE CONTROL for the mask: the same live intent the drive dropped now steers the body, so the next tick's hash
        // leaves the parent's trajectory.
        transport.SubmitIntent(submission: Forward(
            tick: 2UL,
            forward: -FixedQ4816.One,
            strafe: FixedQ4816.One
        ));
        fixture.Step();
        Assert.NotEqual(
            expected: parentTape.RecordedHashes[1],
            actual: WorldReplaySnapshot.HashState(population: fixture.Server.Population)
        );
        // Nothing was recorded under the abandoned fork name.
        Assert.False(condition: File.Exists(path: tape.PathFor(name: "never-recorded")));
    }
    [Fact]
    public void ForkProvenance_ClaimingMoreCopiedTicksThanTheTapeHolds_IsRefusedOnBothSides() {
        // The writer refuses the inconsistent header as a host bug; a doctored file (three ticks, provenance claiming
        // four) is refused by the reader before the snapshot is ever handed out.
        Assert.Throws<WorldReplayCodecException>(testCode: () => RoundTrip(recording: Snapshot(
            ticks: 3,
            forkedFrom: new WorldReplayForkProvenance(
                ParentName: "parent",
                Tick: 4
            )
        )));

        using var buffer = new MemoryStream();

        WorldReplaySnapshot.Write(
            stream: buffer,
            recording: Snapshot(
                ticks: 3,
                forkedFrom: new WorldReplayForkProvenance(
                    ParentName: "parent",
                    Tick: 3
                )
            )
        );

        var bytes = buffer.ToArray();
        // Header layout: magic u32, shape token u32, rate u32, fork-present bool, then the length-prefixed parent name
        // ("parent" — a u16 byte length plus six characters) and the int32 tick, which this doctors from 3 to 4.
        var tickOffset = (((((4 + 4) + 4) + 1) + sizeof(ushort)) + "parent".Length);

        Assert.Equal(
            expected: 3,
            actual: BitConverter.ToInt32(
                startIndex: tickOffset,
                value: bytes
            )
        );
        BitConverter.GetBytes(value: 4).CopyTo(
            array: bytes,
            index: tickOffset
        );

        using var doctored = new MemoryStream(buffer: bytes);
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldReplaySnapshot.Read(stream: doctored));

        Assert.Contains(
            expectedSubstring: "fork provenance claims 4",
            actualString: exception.Message
        );
    }
    [Fact]
    public void ForkProvenance_RoundTripsThroughTheTapeHeader() {
        var forked = RoundTrip(recording: Snapshot(
            ticks: 3,
            forkedFrom: new WorldReplayForkProvenance(
                ParentName: "parent",
                Tick: 2
            )
        ));

        Assert.Equal(
            expected: new WorldReplayForkProvenance(
                ParentName: "parent",
                Tick: 2
            ),
            actual: forked.ForkedFrom
        );
        Assert.Equal(
            expected: 3,
            actual: forked.TickCount
        );

        // THE CONTROL: a tape recorded from boot reads back with no provenance at all.
        Assert.Null(value: RoundTrip(recording: Snapshot(
            forkedFrom: null,
            ticks: 3
        )).ForkedFrom);
    }
    [Fact]
    public void Fork_CopiesTheParentPrefixVerbatim_ReachesTheBootImageLive_AndTheChildVerifiesStandalone() {
        using var stateDirectory = new TemporaryDirectory(prefix: "puck-replay-");

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(
            stateRoot: new WorldStateRoot(path: stateDirectory.RootPath),
            liveServer: fixture.Server,
            profiles: fixture.Server.Profiles,
            transport: transport,
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var parent = $"fork-parent-{Guid.NewGuid():N}";
        var child = $"fork-child-{Guid.NewGuid():N}";

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(
            Principal: Principal.Seat(slot: 0),
            Slot: 0,
            IdentityName: null,
            WireProtocolKey: WorldProtocol.WireProtocolKey
        )).Accepted);
        Assert.True(
            condition: tape.TryBeginRecording(
                name: parent,
                refusal: out var refusal
            ),
            userMessage: $"refused to arm: {refusal}"
        );

        // Four recorded ticks: forward held for the first three (so the body moves and every hash is distinct), a
        // grant landing on tick 1 as an authority entry the prefix must carry across.
        for (var tick = 0UL; (tick < 4UL); tick++) {
            if (tick == 1UL) {
                transport.SubmitGrant(
                    grant: new WorldGrant(
                        Grantee: Principal.Seat(slot: 1),
                        Capability: WorldCapability.Drive,
                        Subject: GrantSubject.Body(index: 0),
                        Exclusive: false
                    ),
                    actor: Principal.Console
                );
            }

            transport.SubmitIntent(submission: Forward(
                tick: (tick + 1UL),
                forward: ((tick < 3UL)
                ? FixedQ4816.One
                : FixedQ4816.Zero)
            ));
            fixture.Step();
            tape.NoteTick();
        }

        var parentStop = tape.StopRecording();

        Assert.Null(@object: parentStop.VerifyFault);
        Assert.True(
            condition: parentStop.Verdict!.Primary.Match,
            userMessage: parentStop.Verdict.Primary.Describe()
        );

        var parentTape = ReadTape(
            name: parent,
            tape: tape
        );
        var movedHash = WorldReplaySnapshot.HashState(population: fixture.Server.Population);

        // The live server has moved on from the boot image (the body drove forward for three ticks) — the fork must
        // bring it back there before the first recorded tick is fed in.
        Assert.NotEqual(
            expected: parentTape.RecordedHashes[0],
            actual: movedHash
        );
        Assert.True(
            condition: tape.TryBeginDrive(
                documentPath: null,
                forkName: child,
                name: parent,
                refusal: out refusal,
                toTick: 2
            ),
            userMessage: $"refused to drive: {refusal}"
        );
        Assert.Equal(
            expected: WorldReplayMode.Replaying,
            actual: tape.Mode
        );
        Assert.True(condition: transport.InputMasked);

        var stepped = 0;

        while (tape.Mode == WorldReplayMode.Replaying) {
            // THE MASK: a live seat intent submitted on every driven tick must never fold in — the recorded intents
            // are the only intents, so the live hashes below still equal the parent's.
            transport.SubmitIntent(submission: Forward(
                tick: ((ulong)(stepped + 1)),
                forward: -FixedQ4816.One,
                strafe: FixedQ4816.One
            ));
            ShellStep(
                fixture: fixture,
                tape: tape
            );
            stepped++;
        }

        Assert.Equal(
            actual: stepped,
            expected: 2
        );
        Assert.Equal(
            expected: WorldReplayMode.Recording,
            actual: tape.Mode
        );
        Assert.Equal(
            expected: child,
            actual: tape.Name
        );
        Assert.Equal(
            expected: 2,
            actual: tape.TickCount
        );
        Assert.Null(value: tape.DriveProgress);
        Assert.False(condition: transport.InputMasked);
        // THE DISCRIMINATOR for the boot-image reset: the running server, re-driven from the image the drive
        // installed, reached exactly the parent's recorded hashes at ticks 0 and 1.
        Assert.Equal(
            expected: parentTape.RecordedHashes[1],
            actual: WorldReplaySnapshot.HashState(population: fixture.Server.Population)
        );

        // Two live ticks under the child, steering differently from the parent's tick 2.
        for (var tick = 2UL; (tick < 4UL); tick++) {
            transport.SubmitIntent(submission: Forward(
                tick: (tick + 1UL),
                forward: FixedQ4816.Zero,
                strafe: FixedQ4816.One
            ));
            fixture.Step();
            tape.NoteTick();
        }

        var childStop = tape.StopRecording();

        Assert.Null(@object: childStop.VerifyFault);
        Assert.True(
            condition: childStop.Verdict!.Primary.Match,
            userMessage: childStop.Verdict.Primary.Describe()
        );

        var childTape = ReadTape(
            name: child,
            tape: tape
        );

        Assert.Equal(
            expected: new WorldReplayForkProvenance(
                ParentName: parent,
                Tick: 2
            ),
            actual: childTape.ForkedFrom
        );
        Assert.Equal(
            expected: 4,
            actual: childTape.TickCount
        );
        Assert.Equal(
            expected: parentTape.SimulationRate,
            actual: childTape.SimulationRate
        );
        Assert.Equal(
            expected: parentTape.DefinitionJson,
            actual: childTape.DefinitionJson
        );

        for (var tick = 0; (tick < 2); tick++) {
            var expected = parentTape.Ticks[tick];
            var actual = childTape.Ticks[tick];

            Assert.Equal(
                expected: expected.Authority.Select(selector: static entry => entry.GetType().Name),
                actual: actual.Authority.Select(selector: static entry => entry.GetType().Name)
            );
            Assert.Equal(
                expected: expected.Intents,
                actual: actual.Intents
            );
            Assert.Equal(
                expected: parentTape.RecordedHashes[tick],
                actual: childTape.RecordedHashes[tick]
            );
        }

        Assert.Contains(
            collection: childTape.Ticks[1].Authority,
            filter: static entry => (entry.GetType().Name == "Grant")
        );
        // The child's own live ticks diverge from the parent's tick 2 onward (a strafe, not the parent's forward).
        Assert.NotEqual(
            expected: parentTape.RecordedHashes[2],
            actual: childTape.RecordedHashes[2]
        );
        // STANDALONE: the child verifies from its own boot image with the parent never consulted.
        Assert.True(condition: tape.Verify(name: child).Primary.Match);
    }
}
