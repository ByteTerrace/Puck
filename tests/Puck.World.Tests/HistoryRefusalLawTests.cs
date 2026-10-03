using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The history's refusals and the read-once rule behind them. A recorded reload is read once, before anything moves,
/// and proved against its recorded hash; a live seek, a diff's shadow, and both of a replay-edit's runs re-apply those
/// verified bytes, so content that changes after the read can neither refuse from inside a step nor reach the world.
/// A replay-edit refuses a span it cannot re-simulate and an edit it cannot move. A live session at a seek and a
/// session event recorded in its window are refused each on its own account.
/// </summary>
public sealed class HistoryRefusalLawTests {
    // Serves one document by path: the recorded bytes until it is armed, then the recorded bytes for the next
    // `flipAfter` reads and changed bytes after, so content changes between the preflight read and the apply.
    private sealed class FlippingDocuments(byte[] recorded, byte[] changed) : IWorldDocumentSource {
        private int m_armedReads;
        private int? m_flipAfter;

        public int ArmedReads => m_armedReads;
        public int ChangedReads { get; private set; }

        public void Arm(int flipAfter) {
            m_flipAfter = flipAfter;
            m_armedReads = 0;
        }
        public bool TryRead(string name, string referrerName, out string resolvedName, out byte[]? content, out string reason) {
            resolvedName = name;
            reason = string.Empty;

            if (
                (m_flipAfter is { } flipAfter) &&
                (m_armedReads++ >= flipAfter)
            ) {
                ChangedReads++;
                content = changed;

                return true;
            }

            content = recorded;

            return true;
        }
    }

    private static (WorldHistoryHarness Harness, FlippingDocuments Documents, ulong Loaded) RecordedReload(TemporaryDirectory directory) {
        var definition = Fixtures.BuildDocument();
        var recorded = WorldDefinitionSerialization.Serialize(definition: definition);
        // Only unhashed document state changes, so nothing but the pin can tell the two apart.
        var changed = WorldDefinitionSerialization.Serialize(definition: definition with {
            HostRaw = definition.Host with { Title = "changed-mid-history" },
        });
        // A source-named root reads through the rebuild's document source, which serves the lowered document.
        var path = directory.WriteBytes(bytes: recorded, name: "world.puck");
        var documents = new FlippingDocuments(changed: changed, recorded: recorded);
        var harness = new WorldHistoryHarness(definition: definition, seats: 0);

        harness.Fixture.Server.RebuildDocuments = documents;
        harness.Steps(count: 6);
        harness.Fixture.Server.EnqueueRebuild(
            principal: Principal.Console,
            request: new WorldRebuildRequest(Kind: WorldRebuildKind.Reload, Definition: null, Origin: new WorldRebuildOrigin.File(Path: path), Force: true)
        );
        harness.StepWithoutInput();

        var loaded = harness.Tick;

        harness.Steps(count: 6);

        return (harness, documents, loaded);
    }
    private static bool HoldsEntry(WorldHistoryHarness harness, ulong from, ulong to, string kind) {
        for (var tick = (from + 1UL); (tick <= to); tick++) {
            if (harness.History.RecordedInput(tick: tick).Authority.Any(predicate: entry => (entry.GetType().Name == kind))) {
                return true;
            }
        }

        return false;
    }

    // Each law reads the recorded reload exactly once — the preflight — and the file changes right after that read.
    // The operation completes from the bytes the preflight verified, so a change after it can neither refuse from
    // inside a step nor reach the world.
    [Fact]
    public void ASeekReadsARecordedReloadOnceAndReAppliesTheVerifiedBytes() {
        using var directory = new TemporaryDirectory(prefix: "puck-history-pin-seek-");

        var (harness, documents, loaded) = RecordedReload(directory: directory);

        using (harness) {
            _ = harness.SeekAndProve(target: (loaded - 1UL));
            documents.Arm(flipAfter: 1);
            _ = harness.SeekAndProve(target: (loaded + 2UL));
            Assert.Equal(expected: (1, 0), actual: (documents.ArmedReads, documents.ChangedReads));
            Assert.Equal(expected: Fixtures.BuildDocument().Host.Title, actual: harness.Fixture.Server.Definition.Host.Title);
        }
    }
    [Fact]
    public void ADiffsShadowReadsARecordedReloadOnceAndReAppliesTheVerifiedBytes() {
        using var directory = new TemporaryDirectory(prefix: "puck-history-pin-diff-");

        var (harness, documents, loaded) = RecordedReload(directory: directory);

        using (harness) {
            documents.Arm(flipAfter: 1);
            Assert.True(condition: harness.History.TryDiff(
                diff: out _, divergedAt: out var divergedAt, documentPath: null, from: (loaded - 1UL), refusal: out var refusal, to: (loaded + 2UL)
            ), userMessage: refusal);
            Assert.Null(@object: divergedAt);
            Assert.Equal(expected: (1, 0), actual: (documents.ArmedReads, documents.ChangedReads));
        }
    }
    [Fact]
    public void AReplayEditReadsARecordedReloadOnceForBothItsRuns() {
        using var directory = new TemporaryDirectory(prefix: "puck-history-pin-edit-");

        var (harness, documents, loaded) = RecordedReload(directory: directory);

        using (harness) {
            harness.Submit(mutation: new WorldMutation.UpsertStateRow(
                Principal: Principal.Console,
                Row: new WorldStateRow(Kind: CellKind.Int, Name: CellName.Parse(candidate: "edited"))
            ));
            documents.Arm(flipAfter: 1);
            Assert.True(condition: harness.History.TryReplayEdit(
                documentPath: null, refusal: out var refusal, report: out var report, ticksBack: ((int)((harness.Tick - loaded) + 3UL))
            ), userMessage: refusal);
            Assert.Null(@object: report!.ControlDivergedAt);
            Assert.Equal(expected: (1, 0), actual: (documents.ArmedReads, documents.ChangedReads));
        }
    }
    // A hosted world's reload names its store, not a file, and a history has no store to read it from: a seek that
    // would re-simulate across one refuses by that type before moving, and the live world stays where it was.
    [Fact]
    public void ASeekAcrossAHostedStoreReloadRefusesByItsType() {
        using var harness = new WorldHistoryHarness(seats: 0);
        var hosted = Fixtures.BuildDocument();

        harness.Steps(count: 6);

        var before = harness.Tick;

        harness.Fixture.Server.EnqueueRebuild(
            principal: Principal.Console,
            request: new WorldRebuildRequest(
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: hosted)),
                Definition: hosted,
                Force: true,
                Kind: WorldRebuildKind.Reload,
                Origin: new WorldRebuildOrigin.Store(Owner: Guid.Parse(input: "00000000-0000-0000-0000-000000000003"), World: SafeName.Parse(candidate: "hosted"))
            )
        );
        harness.StepWithoutInput();
        harness.Steps(count: 3);

        var head = harness.Tick;

        _ = harness.SeekAndProve(target: before);

        var live = WorldStateHashComposition.HashAuthoritative(server: harness.Fixture.Server, tick: before);

        Assert.False(condition: harness.History.TrySeek(
            documentPath: null, refusal: out var refusal, report: out _, target: head
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "a hosted world's reload is replayed from its store");
        Assert.Equal(expected: (before, live), actual: (harness.History.CursorTick!.Value, WorldStateHashComposition.HashAuthoritative(server: harness.Fixture.Server, tick: before)));
    }
    // The append invariant a seek relies on: a live tick behind the head cuts the future first and lands at the new
    // head, so nothing recorded ever lies ahead of the cursor that a seek did not scan to get there.
    [Fact]
    public void ALiveTickBehindTheHeadAppendsAtTheCutHead() {
        using var harness = new WorldHistoryHarness(seed: 47UL);

        harness.Steps(count: 30);
        _ = harness.SeekAndProve(target: 12UL);
        harness.Step();

        Assert.Equal(expected: (13UL, 13UL), actual: (harness.History.CursorTick, harness.History.HeadTick));
        Assert.Equal(expected: harness.Live[13UL], actual: harness.History.RecordedHash(tick: 13UL));
        Assert.All(collection: harness.History.KeyframeTicks, action: static tick => Assert.True(condition: (tick <= 13UL)));
    }

    private static bool TryReplayEditAcrossCrossing(bool crossing, out string refusal) {
        using var harness = new WorldHistoryHarness();

        harness.Steps(count: 10);

        if (crossing) {
            harness.Tape.NoteTransfer(
                departedSlots: [], destinationName: string.Empty, generationId: 0UL, outcome: "aborted:law",
                scopeKey: string.Empty, target: "elsewhere", targetRemote: false, transferId: 1UL
            );
        }

        harness.Steps(count: 10);
        harness.Submit(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(Kind: CellKind.Int, Name: CellName.Parse(candidate: "edited"))
        ));

        return harness.History.TryReplayEdit(documentPath: null, refusal: out refusal, report: out _, ticksBack: 15);
    }

    [Fact]
    public void AReplayEditRefusesASpanItCannotReSimulate() {
        Assert.False(condition: TryReplayEditAcrossCrossing(crossing: true, refusal: out var refusal));
        Assert.Contains(actualString: refusal, expectedSubstring: "a traveler crossed to another authority");
        // The control: the same span without the crossing replays.
        Assert.True(condition: TryReplayEditAcrossCrossing(crossing: false, refusal: out refusal), userMessage: refusal);
    }
    [Fact]
    public void AReplayEditRefusesAnEditItCannotMove() {
        using var harness = new WorldHistoryHarness();

        harness.Steps(count: 10);
        harness.Submit(mutation: new WorldMutation.UpsertAddon(
            Addon: new WorldAddonRow(Enabled: true, Fuel: 1000UL, Hash: "sha256-64/0000000000000000", ModulePath: "absent.wasm", Name: "guest"),
            Principal: Principal.Console
        ));

        Assert.False(condition: harness.History.TryReplayEdit(
            documentPath: null, refusal: out var refusal, report: out _, ticksBack: 5
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "an addon edit requires guest state");
    }
    // A session admitted before the history switched on records no session event at all, so only the live session
    // itself — which no authority checkpoint captures — can stop the history taking a keyframe and refuse the seek.
    [Fact]
    public void ALiveSessionAtTheKeyframeRefusesWithNoSessionEventRecorded() {
        using var harness = new WorldHistoryHarness(on: false, seats: 0);
        var session = Principal.Session(epoch: 1, ordinal: 0);

        harness.Fixture.Server.ApplyServerEvent(serverEvent: new WorldServerEvent.SessionAdmitted(
            MintedGrants: [], Session: session, Templates: []
        ));
        Assert.True(condition: harness.History.TryOn(budgetBytes: WorldHistory.DefaultBudgetBytes, refusal: out _));
        harness.Steps(count: 8);

        Assert.Null(@object: harness.History.OldestTick);
        Assert.Contains(expectedSubstring: "live session input and grants are not captured", actualString: harness.History.Status().Waiting);
        Assert.False(condition: harness.History.TrySeek(
            documentPath: null, refusal: out var refusal, report: out _, target: 1UL
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "live session input and grants are not captured");

        // The control: once the session ends, the next tick takes the first keyframe.
        harness.Fixture.Server.ApplyServerEvent(serverEvent: new WorldServerEvent.SessionEnded(RevokedGrants: [], Session: session));
        harness.Steps(count: 2);
        Assert.NotNull(@object: harness.History.OldestTick);
    }
    // A session admitted and ended inside the window leaves no live session at the seek, so only the recorded
    // session event can refuse it.
    [Fact]
    public void ASeekRefusesASessionEventEvenWhenNoSessionIsLive() {
        using var harness = new WorldHistoryHarness(seats: 0);
        var server = harness.Fixture.Server;
        var session = Principal.Session(epoch: 1, ordinal: 0);

        harness.Steps(count: 4);

        var before = harness.Tick;

        server.ApplyServerEvent(serverEvent: new WorldServerEvent.SessionAdmitted(
            MintedGrants: [], Session: session, Templates: []
        ));
        harness.StepWithoutInput();
        server.ApplyServerEvent(serverEvent: new WorldServerEvent.SessionEnded(RevokedGrants: [], Session: session));
        harness.Steps(count: 3);

        Assert.False(condition: server.IsLiveSession(principal: session));
        Assert.True(condition: HoldsEntry(harness: harness, from: before, kind: "SessionEvent", to: harness.Tick));
        Assert.False(condition: harness.History.TrySeek(
            documentPath: null, refusal: out var refusal, report: out _, target: before
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "a session changed state that authority checkpoints do not preserve");
    }
}
