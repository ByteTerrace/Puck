using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Pins the per-character durable store: the one write door a package's rules write facts and records
/// through (its own namespace only, never a home-scoped name), record pools above capacity one whose instances are
/// named by slot and generation, the travel class that decides what a projection carries, the stage a rule firing
/// writes and rewinds, and an award a resubmission cannot credit twice.</summary>
public sealed class IdentityStoreLawTests {
    private const string Package = "store-world";
    private const string Character = "wren";

    private static readonly CellName Notes = CellName.Parse(candidate: "notes");
    private static readonly CellName Marks = CellName.Parse(candidate: "marks");
    private static readonly CellName Links = CellName.Parse(candidate: "noteLinks");
    private static readonly CellName Text = CellName.Parse(candidate: "text");
    private static readonly CellName Kind = CellName.Parse(candidate: "kind");
    private static readonly WorldIdentityWriter Rules = WorldIdentityWriter.Package(@namespace: Package);

    [Fact]
    public void APackageWritesOnlyItsOwnNamespace_AHomeScopedNameRefusesEveryPackage_HomeWritesAll() {
        var identity = Identity(homeScoped: [CellName.Parse(candidate: GeneratedName.Qualify(head: Package, name: "coins")), Marks]);
        var own = CellName.Parse(candidate: GeneratedName.Qualify(head: Package, name: "visits"));
        var foreign = CellName.Parse(candidate: GeneratedName.Qualify(head: "elsewhere", name: "visits"));
        var coins = CellName.Parse(candidate: GeneratedName.Qualify(head: Package, name: "coins"));

        Assert.True(condition: identity.TrySetFact(changed: out _, key: own, reason: out var ownReason, value: 1L, writer: Rules), userMessage: ownReason);
        Assert.False(condition: identity.TrySetFact(changed: out _, key: foreign, reason: out var foreignReason, value: 1L, writer: Rules));
        Assert.Contains(actualString: foreignReason, expectedSubstring: "outside package 'store-world''s namespace");
        Assert.False(condition: identity.TrySetFact(changed: out _, key: coins, reason: out var coinsReason, value: 9L, writer: Rules));
        Assert.Contains(actualString: coinsReason, expectedSubstring: "home-scoped");
        Assert.True(condition: identity.TrySetFact(changed: out _, key: coins, reason: out var homeReason, value: 9L, writer: WorldIdentityWriter.Home), userMessage: homeReason);
        Assert.False(condition: identity.TrySetFact(changed: out _, key: own, reason: out var nobodyReason, value: 2L, writer: WorldIdentityWriter.Nobody));
        Assert.Contains(actualString: nobodyReason, expectedSubstring: "no package");

        Assert.True(condition: identity.TryOpenStage(stage: out var stage, reason: out var stageReason), userMessage: stageReason);
        Assert.False(condition: stage.TryClaim(generation: out _, pool: Marks, reason: out var markReason, slot: out _, values: [], writer: Rules));
        Assert.Contains(actualString: markReason, expectedSubstring: "home-scoped");
        Assert.True(condition: stage.TryClaim(generation: out _, pool: Marks, reason: out var homeMarkReason, slot: out _, values: [], writer: WorldIdentityWriter.Home), userMessage: homeMarkReason);

        Assert.False(condition: Rules.Admits(fact: false, identity: identity.Document!.Identity, name: CellName.Parse(candidate: "elsewhere$secret"), reason: out var poolReason));
        Assert.Contains(actualString: poolReason, expectedSubstring: "another package's");
        Assert.True(condition: Rules.Admits(fact: false, identity: identity.Document.Identity, name: CellName.Parse(candidate: GeneratedName.Qualify(head: Package, name: "quests")), reason: out var ownPoolReason), userMessage: ownPoolReason);
    }
    [Fact]
    public void APoolHoldsManyInstances_AReleaseAdvancesItsSlotsGeneration_AStaleLifetimeIsRefused() {
        var identity = Identity();

        Assert.True(condition: identity.TryOpenStage(stage: out var stage, reason: out var reason), userMessage: reason);

        for (var index = 0; (index < 3); index++) {
            Assert.True(condition: stage.TryClaim(generation: out var generation, pool: Notes, reason: out reason, slot: out var slot, values: [new WorldIdentityField(Field: Text, Value: CellValue.Text(value: $"note {index}"))], writer: Rules), userMessage: reason);
            Assert.Equal(expected: index, actual: slot);
            Assert.Equal(expected: 0L, actual: generation);
        }
        Assert.True(condition: stage.TryRelease(generation: 0L, pool: Notes, reason: out reason, slot: 1, writer: Rules), userMessage: reason);
        Assert.True(condition: stage.TryClaim(generation: out var reclaimed, pool: Notes, reason: out reason, slot: out var reclaimedSlot, values: [], writer: Rules), userMessage: reason);
        Assert.Equal(expected: 1, actual: reclaimedSlot);
        Assert.NotEqual(expected: 0L, actual: reclaimed);
        Assert.False(condition: stage.TryWrite(field: Text, generation: 0L, pool: Notes, reason: out reason, slot: 1, value: CellValue.Text(value: "stale"), writer: Rules));
        Assert.Contains(actualString: reason, expectedSubstring: "generation 0");
        Assert.True(condition: stage.TryClaimPair(generation: out _, leftSlot: 0, pairPool: Links, reason: out reason, rightSlot: 2, slot: out _, values: [], writer: Rules), userMessage: reason);

        Assert.True(condition: identity.Apply(reason: out reason, stage: stage), userMessage: reason);
        Assert.Equal(expected: 3, actual: identity.CountRecords(pool: Notes));
        Assert.True(condition: identity.TryReadRecord(field: Text, pool: Notes, slot: 2, value: out var text));
        Assert.Equal(expected: "note 2", actual: text.AsText);
        Assert.True(condition: identity.TryRecordGeneration(generation: out var generation1, pool: Notes, slot: 1));
        Assert.Equal(expected: reclaimed, actual: generation1);
    }
    [Fact]
    public void ADroppedStageLeavesTheIdentityAsItWas_AStaleStageIsRefused() {
        var identity = Identity();
        var before = identity.Document;

        Assert.True(condition: identity.TryOpenStage(stage: out var dropped, reason: out var reason), userMessage: reason);
        Assert.True(condition: dropped.TryClaim(generation: out _, pool: Notes, reason: out reason, slot: out _, values: [], writer: Rules), userMessage: reason);
        Assert.Same(expected: before, actual: identity.Document);
        Assert.Equal(expected: 0, actual: identity.CountRecords(pool: Notes));

        Assert.True(condition: identity.TryOpenStage(stage: out var first, reason: out reason), userMessage: reason);
        Assert.True(condition: identity.TryOpenStage(stage: out var second, reason: out reason), userMessage: reason);
        Assert.True(condition: first.TryClaim(generation: out _, pool: Notes, reason: out reason, slot: out _, values: [], writer: Rules), userMessage: reason);
        Assert.True(condition: second.TryClaim(generation: out _, pool: Notes, reason: out reason, slot: out _, values: [], writer: Rules), userMessage: reason);
        Assert.True(condition: identity.Apply(reason: out reason, stage: first), userMessage: reason);
        Assert.False(condition: identity.Apply(reason: out reason, stage: second));
        Assert.Contains(actualString: reason, expectedSubstring: "moved after the stage opened");
        Assert.Equal(expected: 1, actual: identity.CountRecords(pool: Notes));
    }
    [Fact]
    public void AProjectionCarriesItsCrossingPoolsAndNoHomePool() {
        var identity = Identity();

        Assert.True(condition: identity.TryOpenStage(stage: out var stage, reason: out var reason), userMessage: reason);
        Assert.True(condition: stage.TryClaim(generation: out _, pool: Notes, reason: out reason, slot: out _, values: [], writer: Rules), userMessage: reason);
        Assert.True(condition: stage.TryClaim(generation: out _, pool: Marks, reason: out reason, slot: out _, values: [new WorldIdentityField(Field: Kind, Value: CellValue.Int(value: 7L))], writer: Rules), userMessage: reason);
        Assert.True(condition: identity.Apply(reason: out reason, stage: stage), userMessage: reason);

        var projection = identity.Project();
        var carried = Assert.IsType<WorldStateSection>(@object: projection.Records);

        Assert.Equal(expected: Marks, actual: Assert.Single(collection: carried.Pools!).Name);
        Assert.Null(@object: carried.PairPools);

        var traveller = WorldIdentity.FromProjection(defaults: WorldPlayerDefaults.Default, projection: in projection);

        Assert.True(condition: traveller.TryReadRecord(field: Kind, pool: Marks, slot: 0, value: out var kind));
        Assert.Equal(expected: 7L, actual: kind.AsInt);
        Assert.Equal(expected: 0, actual: traveller.CountRecords(pool: Notes));
    }
    [Fact]
    public void AnEmptyAccountMintsTheTemplatesFirstCharacter_HoldingNoneOfTheHostWorld() {
        using var directory = new TemporaryDirectory(prefix: "puck-identity-store-");
        var templatePath = Path.Combine(path1: directory.RootPath, path2: "template.world.json");

        File.WriteAllBytes(path: templatePath, bytes: WorldDefinitionSerialization.Serialize(definition: IdentityDocument(id: "first", homeScoped: null)));

        var host = Fixtures.BuildDocument() with {
            DocumentDirectory = directory.RootPath,
            PlayerDefaultsRaw = WorldPlayerDefaults.Default with { IdentityTemplate = "template.world.json" },
        };
        var account = new WorldOwnedWorlds(directory: Path.Combine(path1: directory.RootPath, path2: "account"), machineId: Guid.NewGuid(), template: host);
        var character = Assert.Single(collection: account.All);
        var document = Assert.IsType<WorldDefinition>(@object: character.Document);

        Assert.Equal(expected: "first", actual: character.Id);
        Assert.Null(@object: document.PlacementsRaw);
        Assert.Null(@object: document.KitsRaw);
        Assert.Null(@object: document.Rules);
        Assert.NotNull(@object: WorldIdentityRecords.Store(document: document));
        Assert.Equal(expected: WorldIdentityTravel.Crossing, actual: document.Identity!.TravelOf(pool: Marks));
        Assert.Equal(expected: WorldIdentityTravel.Home, actual: document.Identity.TravelOf(pool: Notes));
    }
    [Fact]
    public void ACommittedAwardIsCreditedOnce_AnAccountThatSavesNothingSaysSo() {
        using var directory = new TemporaryDirectory(prefix: "puck-identity-award-");
        var account = new WorldOwnedWorlds(directory: directory.RootPath, machineId: Guid.NewGuid(), template: Fixtures.BuildDocument());

        Assert.True(condition: account.ReplaceFromSync(document: IdentityDocument(id: Character, homeScoped: null), reason: out var adopted), userMessage: adopted);

        var identity = Assert.IsType<WorldIdentity>(@object: account.FindById(id: Character));
        var award = CellName.Parse(candidate: "award-0000000000000001");

        for (var attempt = 0; (attempt < 2); attempt++) {
            Assert.True(condition: identity.TryOpenStage(stage: out var stage, reason: out var reason), userMessage: reason);
            Assert.True(condition: stage.TryClaim(generation: out _, pool: Marks, reason: out reason, slot: out _, values: [], writer: Rules), userMessage: reason);
            Assert.True(condition: account.TryCommit(award: award, durability: out var durability, reason: out reason, stage: stage), userMessage: reason);
            Assert.Equal(expected: WorldWriteDurability.Durable, actual: durability);
        }
        Assert.Equal(expected: 1, actual: identity.CountRecords(pool: Marks));
        Assert.True(condition: identity.HoldsAward(award: award));

        var replay = account.CreateReplayCopy();
        var copy = Assert.IsType<WorldIdentity>(@object: replay.FindById(id: Character));

        Assert.True(condition: replay.TrySetFact(changed: out _, durability: out var replayDurability, identity: copy, key: CellName.Parse(candidate: GeneratedName.Qualify(head: Package, name: "seen")), reason: out var replayReason, value: 1L, writer: Rules), userMessage: replayReason);
        Assert.Equal(expected: WorldWriteDurability.Applied, actual: replayDurability);
    }
    [Fact]
    public void RulesClaimAndWriteThroughTheDoor_ARefusedFiringWritesNothing_AnotherPackagesPoolRefuses() {
        var document = Fixtures.BuildDocument() with {
            DocumentId = Package,
            StateRaw = new WorldStateSection(World: [Slot(name: "go")]),
            Rules = [
                Rule(name: "mark", gate: 1L, effects: [new WorldEffect.ClaimIdentityRecord(Key: "0", Pool: Marks.Value, Fields: [new WorldIdentityRecordValue(Field: Kind.Value, Value: 4m)])]),
                Rule(name: "torn", gate: 2L, effects: [
                    new WorldEffect.ClaimIdentityRecord(Key: "0", Pool: Marks.Value),
                    new WorldEffect.SetIdentityRecord(Key: "0", Pool: Marks.Value, Slot: ExpressionProgram.Parse(text: "0"), Fields: [new WorldIdentityRecordValue(Field: "missing", Value: 1m)]),
                ]),
            ],
        };

        using var fixture = Fixtures.FreshServer(definition: document);

        Assert.True(condition: fixture.Server.Profiles.ReplaceFromSync(document: IdentityDocument(id: Character, homeScoped: null), reason: out var adopted), userMessage: adopted);
        Assert.True(condition: fixture.Server.ApplySession(request: new SessionRequest.Join(IdentityName: Character, Principal: Principal.Seat(slot: 0), Slot: 0, WireProtocolKey: WorldProtocol.WireProtocolKey)).Accepted);

        var identity = Assert.IsType<WorldIdentity>(@object: fixture.Server.Profiles.FindById(id: Character));

        Go(fixture: fixture, value: 1L);
        Assert.Equal(expected: 1, actual: identity.CountRecords(pool: Marks));
        Assert.True(condition: identity.TryReadRecord(field: Kind, pool: Marks, slot: 0, value: out var kind));
        Assert.Equal(expected: 4L, actual: kind.AsInt);
        Assert.Contains(
            collection: Directory.GetFiles(path: fixture.Server.Profiles.FilePath),
            filter: static path => path.EndsWith(value: WorldDocumentName.For(id: SafeName.Parse(candidate: Character)), comparisonType: StringComparison.OrdinalIgnoreCase)
        );

        Go(fixture: fixture, value: 2L);
        Assert.Equal(expected: 1, actual: identity.CountRecords(pool: Marks));
        Assert.Contains(collection: fixture.Server.RuleRuntimeDiagnostics(), filter: static diagnostic => WorldRuleEffectRefusal.IdentityRecordUnwritable.Equals(diagnostic.Refusal) &&diagnostic.Detail.Contains(value: "missing", comparisonType: StringComparison.Ordinal));
    }

    private static void Go(WorldFixture fixture, long value) {
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Kind: WorldDocumentWriteKind.Set, Key: WorldStateRow.SlotKey.Value, Principal: Principal.Console, Row: "go", Value: value));
        fixture.Step();
        fixture.Step();
    }
    private static WorldRule Rule(string name, long gate, IReadOnlyList<ActionEffect> effects) => new(
        Name: CellName.Parse(candidate: name),
        Effects: effects,
        Gate: new ActionPredicate.CompareState(State: "go", Comparison: ExpressionOp.Equal, Value: gate),
        Mode: ActionTriggerMode.Edge
    );
    private static WorldStateRow Slot(string name) => new(
        Name: CellName.Parse(candidate: name),
        Kind: CellKind.Int,
        Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 0L))]
    );
    private static WorldIdentity Identity(IReadOnlyList<CellName>? homeScoped = null) => new(
        defaults: WorldPlayerDefaults.Default,
        document: IdentityDocument(homeScoped: homeScoped, id: Character)
    );
    // A character holding a home notes pool, a note-to-note pair pool and a crossing marks pool.
    private static WorldDefinition IdentityDocument(string id, IReadOnlyList<CellName>? homeScoped) => new() {
        DocumentId = id,
        Identity = new WorldIdentityDefinition(
            Id: SafeName.Parse(candidate: id),
            Name: id,
            Color: "#336699",
            MoveSpeedState: CellName.Parse(candidate: "moveSpeed"),
            TurnSpeedState: CellName.Parse(candidate: "turnSpeed"),
            Travel: [new WorldIdentityPool(Pool: Marks, Class: WorldIdentityTravel.Crossing)],
            HomeScoped: homeScoped
        ),
        StateRaw = new WorldStateSection(
            Records: [
                new StateRecord(Name: CellName.Parse(candidate: "Note"), Fields: [new StatePoolField(Name: Text, Kind: CellKind.Text, Default: CellValue.Text(value: string.Empty))]),
                new StateRecord(Name: CellName.Parse(candidate: "Mark"), Fields: [new StatePoolField(Name: Kind, Kind: CellKind.Int, Default: CellValue.Int(value: 0L), Min: 0L, Max: 255L)]),
                new StateRecord(Name: CellName.Parse(candidate: "Join"), Fields: [new StatePoolField(Name: CellName.Parse(candidate: "weight"), Kind: CellKind.Int, Default: CellValue.Int(value: 0L))]),
            ],
            Pools: [
                new StatePool(Name: Notes, Record: CellName.Parse(candidate: "Note"), Capacity: 8),
                new StatePool(Name: Marks, Record: CellName.Parse(candidate: "Mark"), Capacity: 8),
            ],
            PairPools: [new StatePairPool(Name: Links, Record: CellName.Parse(candidate: "Join"), LeftPool: Notes, RightPool: Notes, MaxLive: 8)]
        ),
    };
}
