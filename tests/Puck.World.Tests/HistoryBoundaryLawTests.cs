using Puck.Commands;
using Puck.Testing;
using Puck.World.Authoring;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class HistoryBoundaryLawTests {
    private static WorldDefinition Scored() => Fixtures.BuildDocument() with {
        StateRaw = new(World: [new WorldStateRow(
            Name: CellName.Parse(candidate: "score"),
            Kind: CellKind.Int,
            Cells: [new StateCell(CellName.Parse(candidate: "value"), CellValue.Int(value: 0L))]
        )]),
    };
    private static WorldMutation Score(long value) => new WorldMutation.UpsertStateCell(
        Kind: WorldDocumentWriteKind.Set,
        Key: "value",
        Principal: Principal.Console,
        Row: "score",
        Value: value
    );
    private static WorldRecordedExtension Extension(WorldServer server) => new(
        server: server,
        principal: Principal.Console,
        requests: [new WorldCapabilityRequest(
            Capability: WorldCapability.Mutate,
            Subject: GrantSubject.Section(section: WorldSection.State)
        )],
        maximumPendingContributions: 8
    );

    [Fact]
    public void AHistoryRefusesSessionsThatDurableCheckpointsOmit() {
        using var harness = new WorldHistoryHarness(seats: 0);

        harness.Steps(count: 3);
        var server = harness.Fixture.Server;
        var before = harness.Tick;
        var session = Principal.Session(epoch: 1, ordinal: 0);

        server.ApplyServerEvent(serverEvent: new WorldServerEvent.SessionAdmitted(
            MintedGrants: [], Session: session, Templates: []
        ));
        harness.StepWithoutInput();
        Assert.False(condition: harness.History.TrySeek(
            documentPath: null, refusal: out var refusal, report: out _, target: before
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "session");
        Assert.True(condition: server.IsLiveSession(principal: session));
    }
    [Fact]
    public void ASeekAcrossANewAuthoredKeyRestoresTheCapturedLedger() {
        using var harness = new WorldHistoryHarness(definition: Scored());

        harness.Submit(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console, Row: "score", Key: "orphan", Value: 1L, Kind: WorldDocumentWriteKind.Set
        ));
        harness.Step();
        harness.Submit(mutation: new WorldMutation.RemoveStateCell(Principal: Principal.Console, Row: "score", Key: "orphan"));
        harness.Steps(count: 3);
        harness.History.Off();
        Assert.True(condition: harness.History.TryOn(budgetBytes: WorldHistory.DefaultBudgetBytes, refusal: out _));
        harness.Step();
        var before = harness.Tick;

        harness.Submit(mutation: new WorldMutation.UpsertStateRow(
            Principal: Principal.Console,
            Row: new WorldStateRow(
                Name: CellName.Parse(candidate: "new-row"),
                Kind: CellKind.Int,
                Cells: [new StateCell(CellName.Parse(candidate: "new-key"), CellValue.Int(value: 5L))]
            )
        ));
        harness.Steps(count: 3);
        _ = harness.SeekAndProve(target: before);
        Assert.DoesNotContain(collection: harness.Fixture.Server.Arena.Keys.Names,
            filter: static key => (key.Value == "new-key"));
        _ = harness.SeekAndProve(target: harness.History.HeadTick!.Value);
    }
    [Fact]
    public void AOneTickReplayEditKeepsTheMovedEditExactlyOnce() {
        using var harness = new WorldHistoryHarness(definition: Scored());

        harness.Steps(count: 10);
        harness.Submit(mutation: Score(value: 7L));
        harness.Step();
        Assert.True(condition: harness.History.TryReplayEdit(
            documentPath: null, refusal: out var refusal, report: out var report, ticksBack: 1
        ), userMessage: refusal);
        Assert.Null(@object: report!.ControlDivergedAt);
        Assert.Null(@object: report.DivergedAt);
        Assert.Contains(expectedSubstring: "accepted", actualString: Assert.Single(collection: report.Edits));
    }
    [Fact]
    public void ASeekRefusesAnUndrainedProviderContributionWithoutDroppingIt() {
        using var harness = new WorldHistoryHarness(definition: Scored());

        harness.Steps(count: 5);
        using var extension = Extension(server: harness.Fixture.Server);

        _ = extension.Submit(mutation: Score(value: 3L));
        var before = harness.Tick;

        Assert.False(condition: harness.History.TrySeek(
            documentPath: null, refusal: out var refusal, report: out _, target: (before - 1UL)
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "contribution");
        Assert.Equal(expected: before, actual: harness.Tick);
        Assert.Equal(expected: 1, actual: extension.PendingCount);
        Assert.True(condition: extension.IsActive);
    }
    [Fact]
    public void ASeekRetiresProvidersFromTheAbandonedTimeline() {
        using var harness = new WorldHistoryHarness();

        harness.Steps(count: 5);
        using var extension = Extension(server: harness.Fixture.Server);

        _ = harness.SeekAndProve(target: (harness.Tick - 1UL));
        Assert.False(condition: extension.IsActive);
        Assert.Throws<InvalidOperationException>(testCode: () => extension.Submit(mutation: Score(value: 3L)));
        harness.Fixture.Server.StartRecordedExtensionEpoch();
        using var resumed = Extension(server: harness.Fixture.Server);

        Assert.True(condition: resumed.IsActive);
        Assert.False(condition: extension.IsActive);
    }
    [Fact]
    public async Task ASeekChecksRefusalsUnderTheGateThatRestoresTheTimeline() {
        using var harness = new WorldHistoryHarness();

        harness.Steps(count: 5);
        var server = harness.Fixture.Server;
        var before = harness.Tick;
        using var contended = new ManualResetEventSlim(initialState: false);

        server.AuthorityGateContended = contended.Set;
        Task<(bool Accepted, string Refusal)>? seek = null;

        server.ExecuteAuthorityOperation(operation: () => {
            seek = Task.Run(() => {
                var accepted = harness.History.TrySeek(
                    documentPath: null, refusal: out var refusal, report: out _, target: (before - 1UL)
                );

                return (accepted, refusal);
            }, TestContext.Current.CancellationToken);
            Assert.True(condition: contended.Wait(millisecondsTimeout: 30000, cancellationToken: TestContext.Current.CancellationToken));
            server.NoteScreenOpApplied();
        });
        var result = await seek!;

        server.AuthorityGateContended = null;
        Assert.False(condition: result.Accepted);
        Assert.Contains(actualString: result.Refusal, expectedSubstring: "screen");
        Assert.Equal(expected: before, actual: harness.Tick);
    }
    [Fact]
    public void ASeekDoesNotRepeatSaveEffects() {
        using var harness = new WorldHistoryHarness(definition: Fixtures.BuildDocument() with {
            Rules = [new WorldRule(Name: CellName.Parse(candidate: "save-each-tick"), Effects: [new WorldEffect.Save()])],
        });
        var saves = 0;

        harness.Fixture.Server.SaveEffectTap = _ => saves++;
        harness.Steps(count: 5);
        Assert.Equal(actual: saves, expected: 5);
        _ = harness.SeekAndProve(target: (harness.Tick - 1UL));
        Assert.Equal(actual: saves, expected: 5);
        harness.Step();
        Assert.Equal(actual: saves, expected: 6);
    }
    [Fact]
    public void ASeekRefusesScreenOperationsAfterItsLastKeyframe() {
        using var harness = new WorldHistoryHarness();

        harness.Steps(count: 5);
        harness.Fixture.Server.NoteScreenOpApplied();
        var before = harness.Tick;

        Assert.False(condition: harness.History.TrySeek(
            documentPath: null, refusal: out var refusal, report: out _, target: (before - 1UL)
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "screen");
        Assert.Equal(expected: before, actual: harness.Tick);
    }
    [Fact]
    public void ASeekRefusesChangedReloadContentEvenWhenOnlyUnhashedStateChanged() {
        using var directory = new TemporaryDirectory(prefix: "puck-history-reload-");
        var definition = Fixtures.BuildDocument();
        var path = directory.WriteBytes(name: "world.json", bytes: WorldDefinitionSerialization.Serialize(definition: definition));
        using var harness = new WorldHistoryHarness(definition: definition, seats: 0);

        harness.Steps(count: 3);
        harness.Fixture.Server.EnqueueRebuild(
            principal: Principal.Console,
            request: new WorldRebuildRequest(Kind: WorldRebuildKind.Reload, Definition: null, Origin: new WorldRebuildOrigin.File(Path: path), Force: true)
        );
        harness.StepWithoutInput();
        var loaded = harness.Tick;

        harness.StepWithoutInput();
        _ = harness.SeekAndProve(target: (loaded - 1UL));
        File.WriteAllBytes(path: path, bytes: WorldDefinitionSerialization.Serialize(definition: definition with {
            HostRaw = definition.Host with { Title = "changed-after-recording" },
        }));

        Assert.False(condition: harness.History.TrySeek(
            documentPath: null, refusal: out var refusal, report: out _, target: loaded
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "content");
        Assert.Equal(expected: (loaded - 1UL), actual: harness.Tick);
        Assert.False(condition: harness.History.TryDiff(
            diff: out _, divergedAt: out _, documentPath: null, from: (loaded - 1UL), refusal: out refusal, to: loaded
        ));
        Assert.Contains(actualString: refusal, expectedSubstring: "content");
    }
    [Fact]
    public void ASeekPreservesAnArmedMusicTransition() {
        using var directory = new TemporaryDirectory(prefix: "puck-history-music-");
        var definition = AudioAssetFixtures.ScoredDocument(
            directory: directory,
            musicName: "history-score",
            segment: new MusicSegmentDocument(
                Id: "calm",
                Transitions: [new MusicTransitionDocument(
                    At: MusicTransitionBoundary.BarEnd, To: "calm", When: WorldAudioCue.SeatJoin
                )]
            )
        );
        using var harness = new WorldHistoryHarness(definition: definition);

        harness.Step();
        var first = harness.Tick;
        var pending = harness.Fixture.Server.DescribeMusicState();

        Assert.Contains(actualString: pending, expectedSubstring: "pending=calm");
        harness.Steps(count: 45);
        var head = harness.Tick;
        var transitioned = harness.Fixture.Server.DescribeMusicState();

        Assert.Contains(actualString: transitioned, expectedSubstring: "transitions=1");

        _ = harness.SeekAndProve(target: first);
        Assert.Equal(expected: pending, actual: harness.Fixture.Server.DescribeMusicState());
        _ = harness.SeekAndProve(target: head);
        Assert.Equal(expected: transitioned, actual: harness.Fixture.Server.DescribeMusicState());
    }
    [Fact]
    public void AKeyframeKeepsItsOwnAssetDirectoryAcrossLoads() {
        using var original = new TemporaryDirectory(prefix: "puck-history-origin-");
        using var replacement = new TemporaryDirectory(prefix: "puck-history-replacement-");
        var document = AudioAssetFixtures.ScoredDocument(
            directory: original, musicName: "local-score", segment: new MusicSegmentDocument(Id: "calm", Transitions: null)
        );

        document = document with {
            DocumentDirectory = original.RootPath,
            Music = [document.Music![0] with { Source = Path.GetFileName(path: document.Music[0].Source) }],
        };
        using var harness = new WorldHistoryHarness(definition: document, seats: 0);

        harness.Steps(count: 3);
        var first = harness.History.OldestTick!.Value;
        var other = replacement.WriteBytes(name: "world.json", bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()));

        harness.Fixture.Server.EnqueueRebuild(
            principal: Principal.Console,
            request: new WorldRebuildRequest(Kind: WorldRebuildKind.Load, Definition: null, Origin: new WorldRebuildOrigin.File(Path: other), Force: true)
        );
        harness.StepWithoutInput();
        using (var shadow = harness.History.OpenShadow(documentPath: null, keyframeTick: first)) {
            Assert.Equal(expected: original.RootPath, actual: shadow.Server.Definition.DocumentDirectory);
        }
        _ = harness.SeekAndProve(target: first);
        Assert.Equal(expected: original.RootPath, actual: harness.Fixture.Server.Definition.DocumentDirectory);
    }
    [Fact]
    public void AShadowUsesTheKeyframesMachineAssetDirectory() {
        using var original = new TemporaryDirectory(prefix: "puck-history-machine-origin-");
        using var replacement = new TemporaryDirectory(prefix: "puck-history-machine-replacement-");
        using var harness = new WorldHistoryHarness(
            definition: Fixtures.BuildDocument() with { DocumentDirectory = original.RootPath }, seats: 0, on: false
        );
        string? machineDirectory = null;
        var history = new WorldHistory(
            server: harness.Fixture.Server,
            tape: harness.Tape,
            engines: TestHookInstaller.CreateMachineCatalog().Engines.Values,
            machineHostFactory: (screens, engines, path, output) => {
                machineDirectory = ((path is null) ? null : WorldDocumentPaths.DirectoryOf(documentPath: path));
                return Fixtures.MachineHostFactory(screens, engines, path, output);
            },
            stateRoot: new WorldStateRoot(path: original.RootPath)
        );

        Assert.True(condition: history.TryOn(budgetBytes: WorldHistory.DefaultBudgetBytes, refusal: out _));
        harness.Steps(count: 3);
        var first = history.OldestTick!.Value;
        var other = replacement.WriteBytes(name: "world.json", bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()));

        harness.Fixture.Server.EnqueueRebuild(principal: Principal.Console,
            request: new WorldRebuildRequest(Kind: WorldRebuildKind.Load, Definition: null, Origin: new WorldRebuildOrigin.File(Path: other), Force: true));
        harness.StepWithoutInput();
        using var shadow = history.OpenShadow(documentPath: other, keyframeTick: first);

        Assert.Equal(expected: WorldDocumentPaths.DirectoryOf(documentPath: Path.Combine(path1: original.RootPath, path2: "world.json")), actual: machineDirectory);
    }
    [Fact]
    public void AHistoryDefersAKeyframeWhoseMusicPlanIsNotCaptured() {
        using var directory = new TemporaryDirectory(prefix: "puck-history-music-rebuild-");
        using var harness = new WorldHistoryHarness(definition: AudioAssetFixtures.ScoredDocument(
            directory: directory, musicName: "initial-score", segment: new MusicSegmentDocument(Id: "calm", Transitions: null)
        ), seats: 0);

        harness.Steps(count: 3);
        var other = directory.WriteBytes(name: "world.json", bytes: WorldDefinitionSerialization.Serialize(definition: Fixtures.BuildDocument()));

        harness.Fixture.Server.EnqueueRebuild(principal: Principal.Console,
            request: new WorldRebuildRequest(Kind: WorldRebuildKind.Load, Definition: null, Origin: new WorldRebuildOrigin.File(Path: other), Force: true));
        harness.StepWithoutInput();
        Assert.Empty(collection: (harness.Fixture.Server.Definition.Music ?? []));
        Assert.Contains(expectedSubstring: "calm", actualString: harness.Fixture.Server.DescribeMusicState());
        harness.History.Off();
        Assert.True(condition: harness.History.TryOn(budgetBytes: WorldHistory.DefaultBudgetBytes, refusal: out _));
        harness.StepWithoutInput();
        Assert.Null(@object: harness.History.OldestTick);
        Assert.Contains(expectedSubstring: "music", actualString: harness.History.Status().Waiting);
    }
}
