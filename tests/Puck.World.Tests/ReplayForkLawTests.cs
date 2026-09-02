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
    private static readonly WorldChannelTable Channels = WorldChannelTable.Compile(channels: Fixtures.BuildDocument().Channels);

    private static IntentSubmission Forward(ulong tick, FixedQ4816 forward, FixedQ4816 strafe = default) => new(
        Tick: tick,
        EntityIndex: 0,
        Intent: Channels.RoleOrdinals.Intent(
            moveAdvance: forward,
            moveStrafe: strafe
        ),
        Principal: WorldPrincipal.Seat(slot: 0)
    );
    private static WorldReplaySnapshot ReadTape(string name) {
        using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));

        return WorldReplaySnapshot.Read(stream: stream);
    }
    // One live tick the way WorldServerStepShell drives it: recorded input into the doors, the step, the close.
    private static void ShellStep(WorldFixture fixture, WorldReplayTape tape) {
        tape.InjectDriveTick();
        fixture.Step();
        tape.NoteTick();
    }
    private static WorldReplaySnapshot Snapshot(int ticks, WorldReplayForkProvenance? forkedFrom) {
        var tickInputs = new List<WorldReplayTickInput>(capacity: ticks);
        var hashes = new ulong[ticks];

        for (var tick = 0; (tick < ticks); tick++) {
            tickInputs.Add(item: new WorldReplayTickInput(Authority: [], Intents: []));
            hashes[tick] = ((ulong)(tick + 1));
        }

        return new WorldReplaySnapshot {
            DefinitionJson = WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()),
            ForkedFrom = forkedFrom,
            MountedAddons = [],
            RecordedHashes = hashes,
            RecordedAuthoritativeHashes = [.. hashes],
            Seats = [],
            SimulationRate = 240U,
            Ticks = tickInputs,
        };
    }
    private static WorldReplaySnapshot RoundTrip(WorldReplaySnapshot recording) {
        using var buffer = new MemoryStream();

        WorldReplaySnapshot.Write(stream: buffer, recording: recording);
        buffer.Position = 0L;

        return WorldReplaySnapshot.Read(stream: buffer);
    }

    [Fact]
    public void ForkProvenance_RoundTripsThroughTheTapeHeader() {
        var forked = RoundTrip(recording: Snapshot(ticks: 3, forkedFrom: new WorldReplayForkProvenance(ParentName: "parent", Tick: 2)));

        Assert.Equal(expected: new WorldReplayForkProvenance(ParentName: "parent", Tick: 2), actual: forked.ForkedFrom);
        Assert.Equal(expected: 3, actual: forked.TickCount);

        // THE CONTROL: a tape recorded from boot reads back with no provenance at all.
        Assert.Null(RoundTrip(recording: Snapshot(ticks: 3, forkedFrom: null)).ForkedFrom);
    }
    [Fact]
    public void ForkProvenance_ClaimingMoreCopiedTicksThanTheTapeHolds_IsRefusedOnBothSides() {
        // The writer refuses the inconsistent header as a host bug; a doctored file (three ticks, provenance claiming
        // four) is refused by the reader before the snapshot is ever handed out.
        Assert.Throws<WorldReplayCodecException>(testCode: () => RoundTrip(recording: Snapshot(ticks: 3, forkedFrom: new WorldReplayForkProvenance(ParentName: "parent", Tick: 4))));

        using var buffer = new MemoryStream();

        WorldReplaySnapshot.Write(stream: buffer, recording: Snapshot(ticks: 3, forkedFrom: new WorldReplayForkProvenance(ParentName: "parent", Tick: 3)));

        var bytes = buffer.ToArray();
        // Header layout: magic u32, shape token u32, rate u32, fork-present bool, then the length-prefixed parent name
        // ("parent" — one length byte plus six characters) and the int32 tick, which this doctors from 3 to 4.
        var tickOffset = (4 + 4 + 4 + 1 + 1 + "parent".Length);

        Assert.Equal(expected: 3, actual: BitConverter.ToInt32(value: bytes, startIndex: tickOffset));
        BitConverter.GetBytes(value: 4).CopyTo(array: bytes, index: tickOffset);

        using var doctored = new MemoryStream(buffer: bytes);
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldReplaySnapshot.Read(stream: doctored));

        Assert.Contains(expectedSubstring: "fork provenance claims 4", actualString: exception.Message);
    }
    [Fact]
    public void Fork_CopiesTheParentPrefixVerbatim_ReachesTheBootImageLive_AndTheChildVerifiesStandalone() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: [], addonHostFactory: static (_, _) => new NullAddonHost());
        var parent = $"fork-parent-{Guid.NewGuid():N}";
        var child = $"fork-child-{Guid.NewGuid():N}";

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: 0), Slot: 0, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        Assert.True(condition: tape.TryBeginRecording(name: parent, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        // Four recorded ticks: forward held for the first three (so the body moves and every hash is distinct), a
        // grant landing on tick 1 as an authority entry the prefix must carry across.
        for (var tick = 0UL; (tick < 4UL); tick++) {
            if (tick == 1UL) {
                transport.SubmitGrant(
                    grant: new WorldGrant(Principal: WorldPrincipal.Seat(slot: 1), Capability: WorldCapability.Drive, Subject: GrantSubject.Body(index: 0), Exclusive: false),
                    actor: WorldPrincipal.Console
                );
            }

            transport.SubmitIntent(submission: Forward(tick: (tick + 1UL), forward: ((tick < 3UL)
                ? FixedQ4816.One
                : FixedQ4816.Zero)));
            fixture.Step();
            tape.NoteTick();
        }

        var parentStop = tape.StopRecording();

        Assert.Null(parentStop.VerifyFault);
        Assert.True(condition: parentStop.Verdict!.Value.Match, userMessage: parentStop.Verdict.Value.Describe());

        var parentTape = ReadTape(name: parent);
        var movedHash = WorldReplaySnapshot.HashState(population: fixture.Server.Population);

        // The live server has moved on from the boot image (the body drove forward for three ticks) — the fork must
        // bring it back there before the first recorded tick is fed in.
        Assert.NotEqual(expected: parentTape.RecordedHashes[0], actual: movedHash);
        Assert.True(condition: tape.TryBeginDrive(name: parent, toTick: 2, forkName: child, documentPath: null, refusal: out refusal), userMessage: $"refused to drive: {refusal}");
        Assert.Equal(expected: WorldReplayMode.Replaying, actual: tape.Mode);
        Assert.True(condition: transport.InputMasked);

        var stepped = 0;

        while (tape.Mode == WorldReplayMode.Replaying) {
            // THE MASK: a live seat intent submitted on every driven tick must never fold in — the recorded intents
            // are the only intents, so the live hashes below still equal the parent's.
            transport.SubmitIntent(submission: Forward(tick: ((ulong)(stepped + 1)), forward: -FixedQ4816.One, strafe: FixedQ4816.One));
            ShellStep(fixture: fixture, tape: tape);
            stepped++;
        }

        Assert.Equal(expected: 2, actual: stepped);
        Assert.Equal(expected: WorldReplayMode.Recording, actual: tape.Mode);
        Assert.Equal(expected: child, actual: tape.Name);
        Assert.Equal(expected: 2, actual: tape.TickCount);
        Assert.Null(tape.DriveProgress);
        Assert.False(condition: transport.InputMasked);
        // THE DISCRIMINATOR for the boot-image reset: the running server, re-driven from the image the drive
        // installed, reached exactly the parent's recorded hashes at ticks 0 and 1.
        Assert.Equal(expected: parentTape.RecordedHashes[1], actual: WorldReplaySnapshot.HashState(population: fixture.Server.Population));

        // Two live ticks under the child, steering differently from the parent's tick 2.
        for (var tick = 2UL; (tick < 4UL); tick++) {
            transport.SubmitIntent(submission: Forward(tick: (tick + 1UL), forward: FixedQ4816.Zero, strafe: FixedQ4816.One));
            fixture.Step();
            tape.NoteTick();
        }

        var childStop = tape.StopRecording();

        Assert.Null(childStop.VerifyFault);
        Assert.True(condition: childStop.Verdict!.Value.Match, userMessage: childStop.Verdict.Value.Describe());

        var childTape = ReadTape(name: child);

        Assert.Equal(expected: new WorldReplayForkProvenance(ParentName: parent, Tick: 2), actual: childTape.ForkedFrom);
        Assert.Equal(expected: 4, actual: childTape.TickCount);
        Assert.Equal(expected: parentTape.SimulationRate, actual: childTape.SimulationRate);
        Assert.Equal(expected: parentTape.DefinitionJson, actual: childTape.DefinitionJson);

        for (var tick = 0; (tick < 2); tick++) {
            var expected = parentTape.Ticks[tick];
            var actual = childTape.Ticks[tick];

            Assert.Equal(expected: expected.Authority.Select(selector: static entry => entry.GetType().Name), actual: actual.Authority.Select(selector: static entry => entry.GetType().Name));
            Assert.Equal(expected: expected.Intents, actual: actual.Intents);
            Assert.Equal(expected: parentTape.RecordedHashes[tick], actual: childTape.RecordedHashes[tick]);
        }

        Assert.Contains(collection: childTape.Ticks[1].Authority, filter: static entry => (entry.GetType().Name == "Grant"));
        // The child's own live ticks diverge from the parent's tick 2 onward (a strafe, not the parent's forward).
        Assert.NotEqual(expected: parentTape.RecordedHashes[2], actual: childTape.RecordedHashes[2]);
        // STANDALONE: the child verifies from its own boot image with the parent never consulted.
        Assert.True(condition: tape.Verify(name: child).Match);
    }
    [Fact]
    public void Cancel_EndsTheDriveWhereItStands_AndSeatsAreLiveAgain() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: [], addonHostFactory: static (_, _) => new NullAddonHost());
        var parent = $"drive-cancel-{Guid.NewGuid():N}";

        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(Principal: WorldPrincipal.Seat(slot: 0), Slot: 0, IdentityName: null, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);
        Assert.True(condition: tape.TryBeginRecording(name: parent, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        for (var tick = 0UL; (tick < 3UL); tick++) {
            transport.SubmitIntent(submission: Forward(tick: (tick + 1UL), forward: FixedQ4816.One));
            fixture.Step();
            tape.NoteTick();
        }

        _ = tape.StopRecording();

        var parentTape = ReadTape(name: parent);

        Assert.True(condition: tape.TryBeginDrive(name: parent, toTick: null, forkName: "never-recorded", documentPath: null, refusal: out refusal), userMessage: $"refused to drive: {refusal}");
        ShellStep(fixture: fixture, tape: tape);
        Assert.Equal(expected: parentTape.RecordedHashes[0], actual: WorldReplaySnapshot.HashState(population: fixture.Server.Population));

        Assert.Equal(expected: parent, actual: tape.CancelDrive());
        // A cancel abandons the fork: Idle, never Recording, and the mask is lifted.
        Assert.Equal(expected: WorldReplayMode.Idle, actual: tape.Mode);
        Assert.False(condition: transport.InputMasked);

        // THE CONTROL for the mask: the same live intent the drive dropped now steers the body, so the next tick's hash
        // leaves the parent's trajectory.
        transport.SubmitIntent(submission: Forward(tick: 2UL, forward: -FixedQ4816.One, strafe: FixedQ4816.One));
        fixture.Step();
        Assert.NotEqual(expected: parentTape.RecordedHashes[1], actual: WorldReplaySnapshot.HashState(population: fixture.Server.Population));
        // Nothing was recorded under the abandoned fork name.
        Assert.False(condition: File.Exists(path: WorldReplayTape.PathFor(name: "never-recorded")));
    }
}
