using Xunit;

using Puck.Hosting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// F7's law: <c>rateHz</c> 0 is legitimate <c>.puckreplay</c> tape metadata — a static/stopped world's recording has
/// ZERO steps, and deriving a step width for one must reach an honest answer rather than throw
/// <see cref="ArgumentOutOfRangeException"/> out of <c>EngineTicks.PerRate(ratePerSecond: 0)</c>, which
/// <see cref="WorldReplaySnapshot.Drive"/> did unconditionally before this fix.
/// </summary>
/// <remarks>
/// Driven against <see cref="WorldReplaySnapshot.ResolveStepWidth"/> — the guard extracted FROM <c>Drive</c> as its
/// own testable primitive — rather than through a full record/stop/verify cycle over a loaded world document.
/// <see cref="Puck.World.WorldDefinitionValidator"/> still refuses an authored <c>simulation.rateHz</c> &lt;= 0 as of
/// this change (confirmed live in this tree: a hand-built rate-0 <see cref="WorldDefinition"/> handed straight to
/// <see cref="WorldServer"/>'s constructor — which never calls <c>Validate</c> itself — still fails inside
/// <see cref="WorldReplayTape.StopRecording"/>'s post-persist re-drive, because <c>Drive</c>'s own FIRST act is to
/// re-deserialize its embedded document, which DOES call <c>Validate</c> and refuses rate 0 there, before ever
/// reaching the step-width logic this fix touches) — a separate, in-flight partner-session landing is what makes
/// rate 0 legitimate AUTHORED input, and until it lands NO path (app-level OR the full tape layer) can drive a
/// rate-0 recording through an actual <see cref="WorldReplaySnapshot.Drive"/> call end to end. What CAN be proven
/// today, and what these laws prove, is the extracted primitive itself: it needs nothing but the two raw numbers a
/// hand-built tape can supply directly, with no document in the loop at all.
/// </remarks>
public sealed class ReplayRateZeroLawTests {
    [Fact]
    public void RateZeroWithNoRecordedTicks_ResolvesToZeroWithoutThrowing() {
        // Before this fix: WorldReplaySnapshot.Drive called EngineTicks.PerRate(ratePerSecond: 0) unconditionally,
        // which throws ArgumentOutOfRangeException — this exact legitimate case (a static world's empty recording)
        // would have thrown instead of reaching a step width at all.
        var stepWidth = WorldReplaySnapshot.ResolveStepWidth(simulationRate: 0U, recordedTickCount: 0);

        Assert.Equal(expected: 0UL, actual: stepWidth);
    }

    [Fact]
    public void RateZeroWithARecordedTick_RefusesByNameRatherThanThrowingUnnamed() {
        // THE DISCRIMINATOR against the pre-fix behavior: a rate-0 tape that somehow recorded a tick is the ONE
        // shape that is genuinely inconsistent (Drive's own step loop would have nothing to derive a width FOR).
        // Before this fix that inconsistency was indistinguishable from the legitimate zero-tick case — both threw
        // the SAME unnamed ArgumentOutOfRangeException. After it, only this shape throws, and it throws NAMED.
        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldReplaySnapshot.ResolveStepWidth(simulationRate: 0U, recordedTickCount: 3));

        Assert.Contains(expectedSubstring: "RateZeroCarriesTicks", actualString: exception.Message);
    }

    [Fact]
    public void NonZeroRate_IsUnaffectedAndStillDerivesTheOrdinaryStepWidth() {
        // The control: an ordinary authored rate must resolve EXACTLY as EngineTicks.PerRate always has, ticks
        // present or not — the rate-0 guard must never shadow the ordinary path.
        Assert.Equal(expected: EngineTicks.PerRate(ratePerSecond: 240U), actual: WorldReplaySnapshot.ResolveStepWidth(simulationRate: 240U, recordedTickCount: 0));
        Assert.Equal(expected: EngineTicks.PerRate(ratePerSecond: 240U), actual: WorldReplaySnapshot.ResolveStepWidth(simulationRate: 240U, recordedTickCount: 5));
    }
}

/// <summary>
/// F8's law: a <c>.puckreplay</c> tape's header stamps the RECORD-START rate, never the live rate as it happens to
/// stand when <c>replay.stop</c> is typed — and a mid-capture rebuild that changes the rate stops the recording
/// loudly rather than let the header and a later span of ticks silently disagree about what rate produced them.
/// </summary>
public sealed class ReplayRateStampLawTests {
    [Fact]
    public void MidCaptureRebuildChangingRate_StopsTheRecordingAndKeepsTheRecordStartHeaderRate() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: []);
        var name = $"f8-rate-change-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        // One ordinary tick at the record-start rate (240 Hz, Fixtures.BuildDocument's unauthored default).
        fixture.Step();
        tape.NoteTick();

        Assert.Equal(expected: WorldReplayMode.Recording, actual: tape.Mode);

        // A LIVE rebuild swapping in an otherwise-identical, fully valid document at a DIFFERENT (but still legal)
        // rate — 120 Hz divides 50400 exactly, same as 240 Hz does, so this never touches the rate-0 validator gate
        // at all; it only needs to disagree with the record-start rate. A real on-disk path is required: a live
        // world.load is CAS-pinned against a re-readable file (WorldReplayEntry.Rebuild carries no document, only a
        // path hint + content hash, on both the live tape write and the replay re-drive).
        var (path, contentHash) = WriteTempWorldFile(definition: (Fixtures.BuildDocument() with { Simulation = new WorldSimulationDefaults(RateHz: 120) }));

        try {
            fixture.Server.EnqueueRebuild(
                request: new WorldRebuildRequest(Kind: WorldRebuildKind.Load, Definition: null, PathHint: path, Force: false, ContentHash: contentHash),
                principal: WorldPrincipal.Console
            );

            // Drains the rebuild (WorldServer.Step -> DrainPendingOps -> ApplyRebuild swaps the live definition to
            // 120 Hz) and closes the tick it landed on.
            fixture.Step();
            tape.NoteTick();

            // NoteTick's own mid-capture rate-change check fires the instant the live rate (120) disagrees with the
            // rate snapshotted at record-start (240) — auto-stopping rather than leaving the recording open to
            // silently mix rates under one header.
            Assert.Equal(expected: WorldReplayMode.Idle, actual: tape.Mode);

            using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));
            var persisted = WorldReplaySnapshot.Read(stream: stream);

            // THE DISCRIMINATOR: before this fix the header stamped the LIVE rate at stop time (120, the
            // post-rebuild rate) even though the recorded span actually ran at 240 — an internally inconsistent
            // tape that would then report RateMismatch against its OWN embedded (still-240) definition the moment
            // it was driven. After this fix the header keeps the RECORD-START rate.
            Assert.Equal(expected: 240U, actual: persisted.SimulationRate);
            // Both ticks closed before the auto-stop — the rebuild's own tick is still captured, just as the LAST one.
            Assert.Equal(expected: 2, actual: persisted.Ticks.Count);
        } finally {
            TryDeleteFile(path: path);
        }
    }

    [Fact]
    public void MidCaptureRebuildKeepingTheSameRate_KeepsRecording() {
        // THE CONTROL: a rebuild that does NOT change the rate must never trip the auto-stop — only a genuine rate
        // disagreement does.
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: []);
        var name = $"f8-rate-same-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        fixture.Step();
        tape.NoteTick();

        // A same-rate rebuild — same 240 Hz, otherwise identical document.
        var (path, contentHash) = WriteTempWorldFile(definition: Fixtures.BuildDocument());

        try {
            fixture.Server.EnqueueRebuild(
                request: new WorldRebuildRequest(Kind: WorldRebuildKind.Load, Definition: null, PathHint: path, Force: false, ContentHash: contentHash),
                principal: WorldPrincipal.Console
            );

            fixture.Step();
            tape.NoteTick();

            Assert.Equal(expected: WorldReplayMode.Recording, actual: tape.Mode);

            var result = tape.StopRecording();

            Assert.Null(result.VerifyFault);
            Assert.NotNull(result.Verdict);

            using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));

            Assert.Equal(expected: 240U, actual: WorldReplaySnapshot.Read(stream: stream).SimulationRate);
        } finally {
            TryDeleteFile(path: path);
        }
    }

    // Serializes a definition to a fresh temp file and returns its path plus content hash — the shape a live
    // world.load needs (WorldReplayEntry.Rebuild carries no embedded document for Load/Reload, only a path hint the
    // tape and its own re-drive both re-read fresh).
    private static (string Path, string ContentHash) WriteTempWorldFile(WorldDefinition definition) {
        var bytes = WorldDefinitionSerialization.Serialize(definition: definition);
        var path = Path.Combine(Path.GetTempPath(), $"puck-world-tests-rebuild-{Guid.NewGuid():N}.json");

        File.WriteAllBytes(path: path, bytes: bytes);

        return (path, WorldDefinitionFileSource.ComputeContentHash(content: bytes));
    }

    private static void TryDeleteFile(string path) {
        try {
            File.Delete(path: path);
        } catch (IOException) {
        }
    }
}

/// <summary>
/// F9's law: <c>replay.stop</c> while paused must not drop the pause lever it just armed — the tape's last recorded
/// fact must be the pause that was live when the recording stopped, never silently discarded because it never
/// closed a tick of its own.
/// </summary>
public sealed class ReplayPendingLeverFlushLawTests {
    [Fact]
    public void StopWhilePaused_FlushesThePendingPauseLeverOntoTheLastClosedTick() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: []);
        var name = $"f9-pause-stop-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        // One ordinary CLOSED tick, so there is a tick group to fold the pending lever onto.
        fixture.Step();
        tape.NoteTick();

        // The live pause lever landing — mirrors WorldRateCommandModule.Pause calling this on a real world.rate
        // pause. NOTHING closes another tick after this: a paused boot world never steps again (out of reach from
        // this project's own composition-root gate), so NoteTick never fires again and the pause event would be
        // stranded in the still-open bucket at stop time without this fix.
        tape.NoteRateLever(paused: true);

        var result = tape.StopRecording();

        Assert.Null(result.VerifyFault);
        Assert.NotNull(result.Verdict);

        using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));
        var persisted = WorldReplaySnapshot.Read(stream: stream);

        // THE DISCRIMINATOR: before this fix StopRecording persisted only the CLOSED tick groups — the pending
        // pause event, never rotated into one, was silently dropped, and this assertion would find no RateLever
        // entry anywhere in the tape at all.
        Assert.Single(collection: persisted.Ticks);
        Assert.Contains(collection: persisted.Ticks[0].Authority, filter: entry => (entry.GetType().Name == "RateLever"));
    }

    [Fact]
    public void StopWithNoPendingLever_CarriesNoRateLeverEntry() {
        // THE CONTROL: an ordinary stop with nothing pending must not manufacture a lever entry out of nowhere.
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: []);
        var name = $"f9-no-pause-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        fixture.Step();
        tape.NoteTick();

        var result = tape.StopRecording();

        Assert.Null(result.VerifyFault);
        Assert.NotNull(result.Verdict);

        using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));
        var persisted = WorldReplaySnapshot.Read(stream: stream);

        Assert.Single(collection: persisted.Ticks);
        Assert.DoesNotContain(collection: persisted.Ticks[0].Authority, filter: entry => (entry.GetType().Name == "RateLever"));
    }

    // G5 — THE LEVER FLUSH REORDERS ARBITRARY AUTHORITY. StopRecording used to fold the WHOLE pending bucket — every
    // authority kind and every pending intent, not just the rate lever — onto the last CLOSED tick: a command
    // submitted while paused would replay BEFORE the tick it actually followed, and recording it there would claim
    // it ran on a tick it did not. Only RateLever is provably harmless to fold this way (Drive's own re-drive treats
    // it as a documented no-op); everything else must be DISCARDED instead.
    [Fact]
    public void StopWithAPendingNonLeverGrant_DiscardsItRatherThanFoldingItOntoTheLastClosedTick() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: []);
        var name = $"g5-discard-non-lever-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        // One ordinary CLOSED tick — a tick group the OLD behavior would have folded the pending grant onto.
        fixture.Step();
        tape.NoteTick();

        // A pending GRANT, submitted straight through the loopback (GrantTap fires synchronously, inline — see
        // LoopbackTransport.SubmitGrant's own remarks) with NO fixture.Step()/tape.NoteTick() afterward, so it never
        // closes onto a tick of its own — the identical "stranded in the open bucket" shape NoteRateLever's own
        // pause event used to strand in, but for a kind that is NOT provably harmless to relocate.
        transport.SubmitGrant(
            grant: new WorldGrant(Principal: WorldPrincipal.Seat(slot: 1), Capability: WorldCapability.Drive, Subject: GrantSubject.Body(index: 0), Exclusive: false),
            actor: WorldPrincipal.Console
        );

        var result = tape.StopRecording();

        Assert.Null(result.VerifyFault);
        Assert.NotNull(result.Verdict);

        using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));
        var persisted = WorldReplaySnapshot.Read(stream: stream);

        // THE DISCRIMINATOR: before this fix, this Grant would appear in tick 0's own authority list (folded onto
        // the last closed tick, exactly like a RateLever) — a command that never actually ran on tick 0 would
        // replay as if it had. After this fix it is discarded outright.
        Assert.Single(collection: persisted.Ticks);
        Assert.DoesNotContain(collection: persisted.Ticks[0].Authority, filter: entry => (entry.GetType().Name == "Grant"));
    }

    // G5's OTHER edge: a recording stopped before its very first step has NO closed tick to fold a pending lever
    // onto at all — the tape's own wire shape (WorldReplaySnapshot) carries no header/trailer slot outside the
    // per-tick Ticks list a lever fact could attach to without one, so this case drops the lever BY DESIGN rather
    // than minting a phantom tick Drive never actually ran.
    [Fact]
    public void StopWithZeroClosedTicksAndAPendingLever_RecordsZeroTicksRatherThanMintingAPhantomOne() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: []);
        var name = $"g5-zero-tick-lever-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        // A pending pause lever with NO fixture.Step() ever having run — recording stops before its first tick.
        tape.NoteRateLever(paused: true);

        var result = tape.StopRecording();

        Assert.Null(result.VerifyFault);
        Assert.NotNull(result.Verdict);

        using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: name));
        var persisted = WorldReplaySnapshot.Read(stream: stream);

        Assert.Empty(collection: persisted.Ticks);
    }
}

/// <summary>
/// G6's law: <c>replay.stop</c> resolving the tape's own on-disk path (<see cref="WorldReplayTape.PathFor"/>, which
/// creates the <c>Replays</c> directory as a side effect) USED TO run BEFORE
/// <see cref="WorldReplayTape.StopRecording"/>'s guarded try/finally — an unwritable state root threw straight out
/// of the method, leaving the tape stuck at <see cref="WorldReplayMode.Recording"/> with its taps still attached, so
/// a retry re-merged the same pending entries a second time and a mid-capture forced stop
/// (<see cref="WorldReplayTape.NoteTick"/>'s own rate-change guard) could never actually complete.
/// </summary>
/// <remarks>This law reproduces the observable CONTRACT the fix restores — any write failure during stop still
/// leaves the tape Idle and re-armable — via a directory pre-created at the tape's OWN target path, which is
/// deterministic and process-safe to set up. It does NOT specifically discriminate "PathFor now sits inside the
/// try" from "PathFor sat outside it": <see cref="WorldReplaySnapshot.WriteFile"/>'s own call was ALREADY inside a
/// try/finally before this fix, so a write failure originating there was already recoverable pre-fix too — only a
/// failure inside <c>PathFor</c> ITSELF (its own <c>Directory.CreateDirectory</c> call) is the NEW behavior this
/// fix adds, and reproducing that specific failure would require making the REAL, process-wide state root
/// unwritable (<c>WorldStateRoot.Override</c> can only ever apply ONCE per process — see its own remarks — so no
/// individual law may safely pull that lever without risking every other law that resolves a path afterward). What
/// this law DOES prove, honestly: the guarantee "a stop failure never leaves the tape stuck" holds for at least one
/// concrete failure shape, and — via <see cref="Fixtures.SkipIfReplayDirectoryUnwritable"/> below — every replay law
/// in this file now tells a genuinely read-only sandbox apart from a code regression rather than reporting both as
/// the same red.</remarks>
public sealed class ReplayStopFailureLawTests {
    [Fact]
    public void StopWhenTheTapeFileCannotBeWritten_LeavesTheTapeIdleWithTapsDetachedRatherThanStuckRecording() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        using var fixture = Fixtures.FreshServer();
        var transport = new LoopbackTransport(server: fixture.Server);
        var tape = new WorldReplayTape(liveServer: fixture.Server, profiles: fixture.Server.Profiles, transport: transport, engines: []);
        var name = $"g6-unwritable-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: name, refusal: out var refusal), userMessage: $"refused to arm: {refusal}");

        fixture.Step();
        tape.NoteTick();

        // Forces StopRecording's OWN WriteFile call to fail: a DIRECTORY already occupies the exact path the tape
        // would write its file to. This reproduces "the state root refuses the write" deterministically and
        // environment-independently, without touching the process-wide WorldStateRoot.Override (which this test
        // project's OTHER laws may already have applied this run, and which throws on a SECOND application — see
        // that type's own remarks — so it can never be safely re-applied here).
        var path = WorldReplayTape.PathFor(name: name);

        Directory.CreateDirectory(path: path);

        try {
            var thrown = Record.Exception(testCode: () => tape.StopRecording());

            Assert.True(condition: (thrown is IOException or UnauthorizedAccessException), userMessage: $"expected a write failure (IOException/UnauthorizedAccessException), got: {thrown}");

            // THE DISCRIMINATOR (G6): the tape must be Idle, not stuck at Recording, and — the stronger proof that
            // the taps were actually detached rather than merely the mode flag flipped — a FRESH recording must be
            // armable immediately.
            Assert.Equal(expected: WorldReplayMode.Idle, actual: tape.Mode);
            Assert.True(condition: tape.TryBeginRecording(name: $"{name}-retry", refusal: out var retryRefusal), userMessage: $"tape stayed stuck after the write failure: {retryRefusal}");

            tape.CancelRecording();
        } finally {
            Directory.Delete(path: path, recursive: true);
        }
    }
}
