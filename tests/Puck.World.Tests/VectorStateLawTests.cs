using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

public sealed class VectorStateLawTests {
    private sealed class FakeConsoleAuthority(WorldInstance instance) : IWorldConsoleAuthority {
        public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
            resolved = instance;
            refusal = string.Empty;
            return true;
        }
    }

    private static StateVector SampleVector(int dimensions, int nonZeroIndex = 0, sbyte value = 127) {
        var components = new sbyte[dimensions];

        components[nonZeroIndex] = value;
        if (!StateVector.TryCreate(components: components, error: out var error, vector: out var vector)) {
            throw new InvalidOperationException(message: error);
        }
        return vector;
    }
    private static StateSpace SampleSpace(string name = "lore", int dimensions = 8) => new(
        dimensions: dimensions,
        model: "test-model",
        name: CellName.Parse(candidate: name),
        revision: "1"
    );
    private static WorldDefinition BuildDocumentWithState(StateSpace[] spaces, WorldStateRow[] rows, WorldRule[]? rules = null) =>
        Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                Spaces: spaces,
                World: rows
            ),
            Rules = rules,
        };

    [Fact]
    public void Vector_GrantedAndUngrantedWrites() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );
        var definition = BuildDocumentWithState(spaces: [space], rows: [memoriesRow]);
        using var fixture = Fixtures.FreshServer(definition: definition);

        var peer = Principal.Peer(generation: 1, index: 4);
        var vector = SampleVector(dimensions: 8, nonZeroIndex: 0);

        // 1. Ungranted write is refused
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: peer,
            Row: "memories",
            Key: "m1",
            Value: 0L,
            Kind: WorldDocumentWriteKind.Set,
            Vector: vector
        ));
        fixture.Step();

        var stateRowBefore = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories");

        Assert.NotNull(@object: stateRowBefore);
        Assert.True(condition: ((stateRowBefore.Cells is null) || (stateRowBefore.Cells.Count == 0)));

        // 2. Grant Mutate on State section and Edit on memories row
        var kinds = WorldMutationKindCatalog.KindsOf(section: WorldSection.State);

        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Budget: 16,
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                KindMask: kinds,
                Grantee: peer,
                Subject: GrantSubject.Section(section: WorldSection.State)
            )
        );
        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Capability: WorldCapability.Edit,
                Exclusive: false,
                Grantee: peer,
                Subject: GrantSubject.State(name: "memories")
            )
        );

        // 3. Granted write is admitted
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: peer,
            Row: "memories",
            Key: "m1",
            Value: 0L,
            Kind: WorldDocumentWriteKind.Set,
            Vector: vector
        ));
        fixture.Step();

        var stateRowAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories");

        Assert.NotNull(@object: stateRowAfter);
        Assert.NotNull(@object: stateRowAfter.Cells);
        Assert.Single(collection: stateRowAfter.Cells);
        var writtenCell = stateRowAfter.Cells[0];

        Assert.Equal("m1", writtenCell.Key.Value);
        Assert.True(condition: writtenCell.Value.AsVector.Span.SequenceEqual(other: vector.Components));
    }
    [Fact]
    public void Vector_Undo() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );
        var definition = BuildDocumentWithState(spaces: [space], rows: [memoriesRow]);
        using var fixture = Fixtures.FreshServer(definition: definition);

        var vector = SampleVector(dimensions: 8, nonZeroIndex: 0);

        // Enqueue mutation from Console
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: "memories",
            Key: "m1",
            Value: 0L,
            Kind: WorldDocumentWriteKind.Set,
            Vector: vector
        ));
        fixture.Step();

        var stateRowAfterWrite = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories");

        Assert.NotNull(@object: stateRowAfterWrite);
        Assert.NotNull(@object: stateRowAfterWrite.Cells);
        Assert.Single(collection: stateRowAfterWrite.Cells);

        // Enqueue undo
        fixture.Server.EnqueueUndo(count: 1, principal: Principal.Console);
        fixture.Step();

        var stateRowAfterUndo = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories");

        Assert.NotNull(@object: stateRowAfterUndo);
        Assert.True(condition: ((stateRowAfterUndo.Cells is null) || (stateRowAfterUndo.Cells.Count == 0)));
    }
    [Fact]
    public void Vector_CheckpointRoundTrip() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var vector = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "m1"), Value: CellValue.Vector(components: vector.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );
        var definition = BuildDocumentWithState(spaces: [space], rows: [memoriesRow]);
        using var fixture = Fixtures.FreshServer(definition: definition);

        Assert.True(
            condition: fixture.Server.TryCaptureCheckpoint(
                checkpoint: out var checkpoint,
                hostRow: WorldAuthorityHostRowCheckpoint.Empty,
                reason: out var captureReason
            ),
            userMessage: captureReason
        );
        Assert.NotNull(@object: checkpoint);

        var restoredDef = WorldDefinitionSerialization.Deserialize(utf8Json: checkpoint.Server.DefinitionJson);
        using var restoredMachines = new WorldMachineHost(engines: [], screens: restoredDef.Screens);
        var tempDir = Directory.CreateTempSubdirectory(prefix: "puck-checkpoint-vector-").FullName;

        try {
            var (restoredServer, _) = WorldServer.FromCheckpoint(
                checkpoint: checkpoint,
                instanceIdentity: "boot",
                machines: restoredMachines,
                profiles: new WorldOwnedWorlds(directory: tempDir, machineId: Guid.NewGuid(), template: restoredDef)
            );

            Assert.NotNull(@object: restoredServer.Definition.Spaces);
            Assert.Single(collection: restoredServer.Definition.Spaces);
            Assert.Equal("lore", restoredServer.Definition.Spaces[0].Name.Value);
            Assert.Equal(8, restoredServer.Definition.Spaces[0].Identity.Dimensions);

            var restoredRow = WorldDefinitionRows.FindStateRow(rows: restoredServer.Definition.State, name: "memories");

            Assert.NotNull(@object: restoredRow);
            Assert.NotNull(@object: restoredRow.Cells);
            Assert.Single(collection: restoredRow.Cells);
            var restoredCell = restoredRow.Cells[0];

            Assert.Equal("m1", restoredCell.Key.Value);
            Assert.True(condition: restoredCell.Value.AsVector.Span.SequenceEqual(other: vector.Components));
        } finally {
            Directory.Delete(path: tempDir, recursive: true);
        }
    }
    [Fact]
    public void Vector_WorldSaveAndReload() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var vector = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "m1"), Value: CellValue.Vector(components: vector.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );
        var definition = BuildDocumentWithState(spaces: [space], rows: [memoriesRow]);
        using var fixture = Fixtures.FreshServer(definition: definition);

        var tempPath = Path.Combine(path1: Path.GetTempPath(), path2: $"{Guid.NewGuid():N}.world.json");

        try {
            _ = WorldDefinitionSerialization.Save(definition: fixture.Server.Definition, path: tempPath);
            var reloadedDef = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: tempPath));

            Assert.NotNull(@object: reloadedDef.Spaces);
            Assert.Single(collection: reloadedDef.Spaces);
            Assert.Equal("lore", reloadedDef.Spaces[0].Name.Value);
            Assert.Equal(8, reloadedDef.Spaces[0].Identity.Dimensions);

            var reloadedRow = WorldDefinitionRows.FindStateRow(rows: reloadedDef.State, name: "memories");

            Assert.NotNull(@object: reloadedRow);
            Assert.NotNull(@object: reloadedRow.Cells);
            Assert.Single(collection: reloadedRow.Cells);
            var reloadedCell = reloadedRow.Cells[0];

            Assert.Equal("m1", reloadedCell.Key.Value);
            Assert.True(condition: reloadedCell.Value.AsVector.Span.SequenceEqual(other: vector.Components));
        } finally {
            if (File.Exists(path: tempPath)) {
                File.Delete(path: tempPath);
            }
        }
    }
    [Fact]
    public void Vector_EvictingTableReplayDeterminism() {
        Fixtures.SkipIfReplayDirectoryUnwritable();

        var space = SampleSpace(dimensions: 8, name: "lore");
        var eventsRow = new WorldStateRow(
            Capacity: 2,
            Evicts: true,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "events"),
            Space: "lore"
        );
        var definition = BuildDocumentWithState(spaces: [space], rows: [eventsRow]);

        using var serverA = Fixtures.FreshServer(definition: definition);
        var transport = new LoopbackTransport(server: serverA.Server);
        var tape = new WorldReplayTape(
            liveServer: serverA.Server,
            profiles: serverA.Server.Profiles,
            transport: transport,
            engines: [],
            machineHostFactory: Fixtures.MachineHostFactory,
            addonHostFactory: static (_, _) => new NullAddonHost()
        );
        var tapeName = $"vector-evicting-replay-{Guid.NewGuid():N}";

        Assert.True(condition: tape.TryBeginRecording(name: tapeName, refusal: out var refusal), userMessage: refusal);

        var keys = new[] { "e1", "e2", "e3", "e4" };
        var recordedHashes = new List<ulong>();

        for (var i = 0; (i < keys.Length); i++) {
            var vec = SampleVector(dimensions: 8, nonZeroIndex: (i % 8));

            transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
                Principal: Principal.Console,
                Row: "events",
                Key: keys[i],
                Value: 0L,
                Kind: WorldDocumentWriteKind.Set,
                Vector: vec
            ));

            serverA.Step();
            tape.NoteTick();
            recordedHashes.Add(item: WorldStateHashComposition.HashAuthoritative(server: serverA.Server, tick: serverA.Server.NextInputTick));
        }

        _ = tape.StopRecording();

        try {
            using var stream = File.OpenRead(path: WorldReplayTape.PathFor(name: tapeName));
            var snapshot = WorldReplaySnapshot.Read(stream: stream);

            using var serverB = Fixtures.FreshServer(definition: definition);
            var tapeB = new WorldReplayTape(
                liveServer: serverB.Server,
                profiles: serverB.Server.Profiles,
                transport: new LoopbackTransport(server: serverB.Server),
                engines: [],
                machineHostFactory: Fixtures.MachineHostFactory,
                addonHostFactory: static (_, _) => new NullAddonHost()
            );

            Assert.True(condition: tapeB.TryBeginDrive(documentPath: null, forkName: "never-recorded", name: tapeName, refusal: out refusal, toTick: null), userMessage: refusal);

            for (var i = 0; (i < keys.Length); i++) {
                tapeB.InjectDriveTick();
                serverB.Step();
                tapeB.NoteTick();

                var replayHash = WorldStateHashComposition.HashAuthoritative(server: serverB.Server, tick: serverB.Server.NextInputTick);

                Assert.Equal(recordedHashes[i], replayHash);
            }

            var rowB = WorldDefinitionRows.FindStateRow(rows: serverB.Server.Definition.State, name: "events")!;

            Assert.Equal(2, rowB.Cells!.Count);
            Assert.Equal("e3", rowB.Cells[0].Key.Value);
            Assert.Equal("e4", rowB.Cells[1].Key.Value);
        } finally {
            var path = WorldReplayTape.PathFor(name: tapeName);

            if (File.Exists(path: path)) {
                File.Delete(path: path);
            }
        }
    }
    [Fact]
    public void Vector_CrossRowNearestAndRemember() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var vecAmbush = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecGift = SampleVector(dimensions: 8, nonZeroIndex: 1);

        var eventsRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "ambush"), Value: CellValue.Vector(components: vecAmbush.Memory)),
                new StateCell(Key: CellName.Parse(candidate: "gift"), Value: CellValue.Vector(components: vecGift.Memory)),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "events"),
            Space: "lore"
        );
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Evicts: true,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );
        var situationRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Vector(components: vecAmbush.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "situation"),
            Space: "lore"
        );
        var recalledRow = new WorldStateRow(
            Capacity: 2,
            Kind: CellKind.Fixed,
            Name: CellName.Parse(candidate: "recalled")
        );
        var caravanAttackedRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 1L))],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "caravanAttacked")
        );

        var rule = new WorldRule(
            Effects: [
                new ActionEffect.TransformState(Transform: new StateTransform.Remember(
                    From: "events[ambush]",
                    Into: "memories",
                    Key: "ambush",
                    UnlessWithin: "0.9"
                )),
                new ActionEffect.TransformState(Transform: new StateTransform.Nearest(
                    From: "memories",
                    Into: "recalled",
                    K: 1,
                    Query: "situation",
                    Threshold: "0.5"
                )),
            ],
            Gate: new ActionPredicate.CompareState(
                Comparison: ExpressionOp.Equal,
                State: "caravanAttacked",
                Value: 1m
            ),
            Name: CellName.Parse(candidate: "remember-and-recall")
        );

        var definition = BuildDocumentWithState(
            rows: [eventsRow, memoriesRow, situationRow, recalledRow, caravanAttackedRow],
            rules: [rule],
            spaces: [space]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        // Check that remember wrote "ambush" into memories
        var memoriesAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories")!;

        Assert.NotNull(@object: memoriesAfter.Cells);
        var ambushMemory = Assert.Single(collection: memoriesAfter.Cells);

        Assert.Equal("ambush", ambushMemory.Key.Value);
        Assert.True(condition: ambushMemory.Value.AsVector.Span.SequenceEqual(other: vecAmbush.Components));

        // Check that nearest wrote "ambush" into recalled with similarity >= 0.5
        var recalledAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "recalled")!;

        Assert.NotNull(@object: recalledAfter.Cells);
        var recalledCell = Assert.Single(collection: recalledAfter.Cells);

        Assert.Equal("ambush", recalledCell.Key.Value);
        var sim = FixedQ4816.FromRawBits(value: recalledCell.Value.AsFixed);

        Assert.True(condition: (sim >= FixedQ4816.FromRawBits(value: 32768))); // >= 0.5

        // Now fire a second rule trying to remember a duplicate under unlessWithin: 0.9
        var duplicateRule = new WorldRule(
            Effects: [
                new ActionEffect.TransformState(Transform: new StateTransform.Remember(
                    From: "events[ambush]",
                    Into: "memories",
                    Key: "ambush_dup",
                    UnlessWithin: "0.9"
                )),
            ],
            Gate: null,
            Name: CellName.Parse(candidate: "remember-duplicate")
        );

        var defWithDup = fixture.Server.Definition with { Rules = [duplicateRule] };
        using var fixtureDup = Fixtures.FreshServer(definition: defWithDup);

        fixtureDup.Step();

        // memories still has only the original "ambush", duplicate was refused/skipped
        var memoriesDup = WorldDefinitionRows.FindStateRow(rows: fixtureDup.Server.Definition.State, name: "memories")!;

        Assert.NotNull(@object: memoriesDup.Cells);
        Assert.Single(collection: memoriesDup.Cells);
        Assert.Equal("ambush", memoriesDup.Cells[0].Key.Value);
    }
    [Fact]
    public void Vector_LargeRowPresentationProjection() {
        var space = SampleSpace(dimensions: 256, name: "lore");
        // 256 cells * 256 dimensions = 65,536 bytes (the MaxVectorRowBytes limit)
        var cells = new StateCell[256];

        for (var i = 0; (i < 256); i++) {
            var vec = SampleVector(dimensions: 256, nonZeroIndex: (i % 256));

            cells[i] = new StateCell(Key: CellName.Parse(candidate: $"c{i}"), Value: CellValue.Vector(components: vec.Memory));
        }

        var largeRow = new WorldStateRow(
            Capacity: 256,
            Cells: cells,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "largeTable"),
            Space: "lore",
            Visibility: new StateVisibility()
        );

        var definition = BuildDocumentWithState(spaces: [space], rows: [largeRow]);

        // Definition must be valid
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var validationReason),
            userMessage: validationReason
        );

        // Compose presentation-tier projection
        var projection = Fixtures.Project(
            authority: "boot",
            definition: definition,
            revision: 1,
            tier: WorldDisclosureTier.Presentation
        );

        Assert.NotNull(@object: projection);

        // Serialize and deserialize projection
        var serialized = WorldProjection.Serialize(projection: projection);

        Assert.True(condition: WorldProjection.TryDeserialize(projection: out var decoded, reason: out var decodeReason, utf8Json: serialized), userMessage: decodeReason);
        Assert.NotNull(@object: decoded);
        Assert.NotNull(@object: decoded.Observations);

        var observedRow = Assert.Single(collection: decoded.Observations);

        Assert.Equal("largeTable", observedRow.Name);
        Assert.Equal(CellKind.Vector, observedRow.Kind);
        Assert.NotNull(@object: observedRow.Cells);
        Assert.Equal(256, observedRow.Cells.Count);

        for (var i = 0; (i < 256); i++) {
            var origCell = cells[i];
            var obsCell = observedRow.Cells[i];

            Assert.Equal(origCell.Key.Value, obsCell.Key);
            Assert.NotNull(@object: obsCell.Vector);
            Assert.Equal(256, obsCell.Vector.Dimensions);
            Assert.True(condition: origCell.Value.AsVector.Span.SequenceEqual(other: obsCell.Vector.Components));
        }

        // Hydrate back into definition: the vector observation arrives as a vector row in the space the projection
        // carries for it
        Assert.Equal(expected: space, actual: Assert.Single(collection: decoded.Spaces!));
        Assert.True(condition: WorldProjection.TryToDefinition(definition: out var hydrated, projection: decoded, reason: out var hydrateReason), userMessage: hydrateReason);
        Assert.NotNull(@object: hydrated);

        var hydratedRow = Assert.Single(collection: hydrated.State);

        Assert.Equal(expected: "lore", actual: hydratedRow.Space);
        Assert.Equal(expected: 256, actual: hydratedRow.Cells!.Count);
        Assert.Equal(expected: space, actual: Assert.Single(collection: hydrated.StateRaw!.Spaces!));
    }
    [Fact]
    public void Vector_DigestEcho() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var vector = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "m1"), Value: CellValue.Vector(components: vector.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );
        var definition = BuildDocumentWithState(spaces: [space], rows: [memoriesRow]);

        using var row = HostRow.Build(definition: definition, name: "boot");
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new FakeConsoleAuthority(instance: row.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: row.Instance.Link
        )]);

        var result = registry.Submit(line: "world.state memories m1");

        Assert.False(condition: result.IsError);

        var expectedDigest = vector.ComputeDigest();
        var expectedDigestString = $"vector[8] #{expectedDigest:x8}";

        Assert.Contains(expectedSubstring: expectedDigestString, actualString: result.Output);
    }
    [Fact]
    public void Vector_SimilarQueryAndVisibility() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecB = SampleVector(dimensions: 8, nonZeroIndex: 1);

        var situationRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Vector(components: vecA.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "situation"),
            Space: "lore"
        );
        var eventsRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "gift"), Value: CellValue.Vector(components: vecA.Memory)),
                new StateCell(
                    Key: CellName.Parse(candidate: "secret"),
                    Value: CellValue.Vector(components: vecB.Memory),
                    Visibility: new StateVisibility(Readers: ["console", "seat1"])
                ),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "events"),
            Space: "lore"
        );

        var definition = BuildDocumentWithState(spaces: [space], rows: [situationRow, eventsRow]);
        using var row = HostRow.Build(definition: definition, name: "boot");
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new FakeConsoleAuthority(instance: row.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: row.Instance.Link
        )]);

        // 1. Query as Console (unrestricted): both gift and secret appear
        var consoleResult = registry.Submit(line: "world.state.similar situation $value events");

        Assert.False(condition: consoleResult.IsError);
        Assert.Contains(expectedSubstring: "'events'.'gift'", actualString: consoleResult.Output);
        Assert.Contains(expectedSubstring: "'events'.'secret'", actualString: consoleResult.Output);

        // 2. Query as Peer (restricted): secret is hidden, only gift appears
        var commandSource = new TextCommandSource(registry: registry);
        var peerResults = new List<CommandResult>();
        using var peerSession = commandSource.CreateSession(
            principal: Principal.Peer(generation: 1, index: 4),
            onResult: (_, res) => peerResults.Add(item: res)
        );

        peerSession.Enqueue(line: "world.state.similar situation $value events");
        commandSource.Collect();
        var peerResult = Assert.Single(collection: peerResults);

        Assert.False(condition: peerResult.IsError);
        Assert.Contains(expectedSubstring: "'events'.'gift'", actualString: peerResult.Output);
        Assert.DoesNotContain(expectedSubstring: "'events'.'secret'", actualString: peerResult.Output);

        // 3. Query with a hidden query vector returns an error
        var hiddenSituationRow = new WorldStateRow(
            Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Vector(components: vecA.Memory)
            )],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "hiddenQuery"),
            Space: "lore",
            Visibility: new StateVisibility(Readers: ["seat1"])
        );
        var defWithHidden = BuildDocumentWithState(spaces: [space], rows: [hiddenSituationRow, eventsRow]);
        using var rowHidden = HostRow.Build(definition: defWithHidden, name: "boot2");
        var registryHidden = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new FakeConsoleAuthority(instance: rowHidden.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: rowHidden.Instance.Link
        )]);

        var hiddenCommandSource = new TextCommandSource(registry: registryHidden);
        var hiddenResults = new List<CommandResult>();
        using var peerSession2 = hiddenCommandSource.CreateSession(
            principal: Principal.Peer(generation: 1, index: 4),
            onResult: (_, res) => hiddenResults.Add(item: res)
        );

        peerSession2.Enqueue(line: "world.state.similar hiddenQuery $value events");
        hiddenCommandSource.Collect();
        var hiddenQueryResult = Assert.Single(collection: hiddenResults);

        Assert.True(condition: hiddenQueryResult.IsError);
        Assert.Contains(expectedSubstring: "query cell 'hiddenQuery.$value' is hidden", actualString: hiddenQueryResult.Output);
    }
    [Fact]
    public void VectorRemember_SkipsNearDuplicate_AndIgnoresOwnKey() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var componentsPrime = new sbyte[8];

        componentsPrime[0] = 126;
        componentsPrime[1] = 15;
        Assert.True(condition: StateVector.TryCreate(components: componentsPrime, error: out var err, vector: out var vecAPrime), userMessage: err);

        var eventsRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "e1"), Value: CellValue.Vector(components: vecAPrime.Memory)),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "events"),
            Space: "lore"
        );
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "k1"), Value: CellValue.Vector(components: vecA.Memory)),
            ],
            Evicts: true,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );

        var rule = new WorldRule(
            Effects: [
                new ActionEffect.TransformState(Transform: new StateTransform.Remember(
                    From: "events[e1]",
                    Into: "memories",
                    Key: "k1",
                    UnlessWithin: "0.9"
                )),
            ],
            Gate: null,
            Name: CellName.Parse(candidate: "remember-own-key")
        );

        var definition = BuildDocumentWithState(
            rows: [eventsRow, memoriesRow],
            rules: [rule],
            spaces: [space]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        var memoriesAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories")!;

        Assert.NotNull(@object: memoriesAfter.Cells);
        var k1Cell = Assert.Single(collection: memoriesAfter.Cells);

        Assert.Equal("k1", k1Cell.Key.Value);
        Assert.True(condition: k1Cell.Value.AsVector.Span.SequenceEqual(other: vecAPrime.Components));
    }
    [Fact]
    public void Vector_RuleCopyIntoACellTheDestinationHolds_WritesTheSourceComponentsUnchanged() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecB = SampleVector(dimensions: 8, nonZeroIndex: 1);
        var seed = SampleVector(
            dimensions: 8,
            nonZeroIndex: 7
        );

        var eventsRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "k1"), Value: CellValue.Vector(components: vecA.Memory)),
                new StateCell(Key: CellName.Parse(candidate: "k2"), Value: CellValue.Vector(components: vecB.Memory)),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "events"),
            Space: "lore"
        );
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "k1"), Value: CellValue.Vector(components: seed.Memory)),
                new StateCell(Key: CellName.Parse(candidate: "k2"), Value: CellValue.Vector(components: seed.Memory)),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );
        var rule = new WorldRule(
            ForEach: "events",
            Effects: [
                new ActionEffect.SetState(
                    FromKey: "$each",
                    FromState: "events",
                    Key: "$each",
                    State: "memories"
                ),
            ],
            Gate: null,
            Name: CellName.Parse(candidate: "copy-each")
        );
        var definition = BuildDocumentWithState(
            rows: [eventsRow, memoriesRow],
            rules: [rule],
            spaces: [space]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        var memoriesAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories")!;
        var c1 = StateRows.FindCell(cells: memoriesAfter.Cells, key: CellName.Parse(candidate: "k1"));
        var c2 = StateRows.FindCell(cells: memoriesAfter.Cells, key: CellName.Parse(candidate: "k2"));

        Assert.NotNull(@object: c1);
        Assert.NotNull(@object: c2);
        // A copy is not a one-term mix: the components arrive unnormalized, byte for byte.
        Assert.True(condition: c1.Value.AsVector.Span.SequenceEqual(other: vecA.Components));
        Assert.True(condition: c2.Value.AsVector.Span.SequenceEqual(other: vecB.Components));
        Assert.Empty(collection: fixture.Server.RuleRuntimeDiagnostics());
    }
    [Fact]
    public void Vector_RuleCopyIntoACellTheDestinationDoesNotHold_IsRefusedByName() {

        var space = SampleSpace(dimensions: 8, name: "lore");
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecB = SampleVector(dimensions: 8, nonZeroIndex: 1);

        var eventsRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "k1"), Value: CellValue.Vector(components: vecA.Memory)),
                new StateCell(Key: CellName.Parse(candidate: "k2"), Value: CellValue.Vector(components: vecB.Memory)),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "events"),
            Space: "lore"
        );
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );

        var rule = new WorldRule(
            ForEach: "events",
            Effects: [
                new ActionEffect.SetState(
                    FromKey: "$each",
                    FromState: "events",
                    Key: "$each",
                    State: "memories"
                ),
            ],
            Gate: null,
            Name: CellName.Parse(candidate: "copy-each")
        );

        var definition = BuildDocumentWithState(
            rows: [eventsRow, memoriesRow],
            rules: [rule],
            spaces: [space]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);

        fixture.Step();

        var memoriesAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories")!;

        // A write addresses a cell its row holds; it never mints one, so the row a document declares empty stays
        // empty and the copy is refused by row and cell name.
        Assert.Empty(collection: memoriesAfter.Cells!);
        Assert.Contains(
            collection: fixture.Server.RuleRuntimeDiagnostics(),
            filter: static diagnostic => diagnostic.Detail.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "holds no cell"
            )
        );

    }
    [Fact]
    public void Vector_WorldStateTransform_RefusesInvalidShapesByName() {
        var spaceLore = SampleSpace(dimensions: 8, name: "lore");
        var spaceOther = SampleSpace(dimensions: 16, name: "other");

        var vecLore = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecOther = SampleVector(dimensions: 16, nonZeroIndex: 0);

        var loreRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "c1"), Value: CellValue.Vector(components: vecLore.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "loreRow"),
            Space: "lore"
        );
        var otherRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "c1"), Value: CellValue.Vector(components: vecOther.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "otherRow"),
            Space: "other"
        );
        var ranksRow = new WorldStateRow(
            Capacity: 4,
            Cells: [],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "ranks")
        );
        var unconstrainedKeyedRow = new WorldStateRow(
            Cells: [],
            Domain: StateDomain.Keys.Instance,
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "unconstrainedKeyed")
        );
        var textSlotRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Text(value: ""))],
            Kind: CellKind.Text,
            Name: CellName.Parse(candidate: "textSlot")
        );
        var textTableRow = new WorldStateRow(
            Capacity: 4,
            Cells: [],
            Kind: CellKind.Text,
            Name: CellName.Parse(candidate: "textTable")
        );
        var notKeyedBoolRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Bool(value: true))],
            Kind: CellKind.Bool,
            Name: CellName.Parse(candidate: "boolSlot")
        );
        var intWhereRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "c1"), Value: CellValue.Int(value: 1L))],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "intWhere")
        );

        var definition = BuildDocumentWithState(
            spaces: [spaceLore, spaceOther],
            rows: [loreRow, otherRow, ranksRow, unconstrainedKeyedRow, textSlotRow, textTableRow, notKeyedBoolRow, intWhereRow]
        );

        using var row = HostRow.Build(definition: definition, name: "boot");
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new FakeConsoleAuthority(instance: row.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: row.Instance.Link
        )]);

        string? diag = null;

        row.Server.EchoTap = echo => { if (echo.Rejected) { diag = echo.Message; } };

        void AssertRefused(string transformJson, string expectedReason) {
            diag = null;
            var res = registry.Submit(line: $"world.state.transform {transformJson}");

            Assert.False(condition: res.IsError);
            row.Server.Advance(stepTicks: Fixtures.StepTicks);
            Assert.NotNull(@object: diag);
            Assert.Contains(actualString: diag, comparisonType: StringComparison.OrdinalIgnoreCase, expectedSubstring: expectedReason);
        }

        // 1. Cross-space operand refused by name
        AssertRefused(
            expectedReason: "VectorSpaceMismatch",
            transformJson: """{"$type":"nearest","from":"loreRow","query":"otherRow[c1]","into":"ranks","k":1}"""
        );

        // 2. Where that is not a keyed Bool row:
        // Case A: kind is not Bool (Int)
        AssertRefused(
            expectedReason: "must be a keyed Bool table",
            transformJson: """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"ranks","k":1,"where":"intWhere"}"""
        );

        // Case B: Bool but not keyed (slot)
        AssertRefused(
            expectedReason: "must be a keyed Bool table",
            transformJson: """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"ranks","k":1,"where":"boolSlot"}"""
        );

        // 3. Keyed nearest into without capacity
        AssertRefused(
            expectedReason: "VectorNearestShape",
            transformJson: """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"unconstrainedKeyed","k":1}"""
        );

        // 4. Text into that is not a slot or has k != 1
        // Case A: Text table (not a slot)
        AssertRefused(
            expectedReason: "must be a slot",
            transformJson: """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"textTable","k":1}"""
        );

        // Case B: Text slot with k != 1 (k = 2)
        AssertRefused(
            expectedReason: "requires k: 1",
            transformJson: """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"textSlot","k":2}"""
        );
    }
    [Fact]
    public void Vector_WorldStateTransform_PrincipalHoldingEditOnIntoAndObserveOnSource_SucceedsForMixMeanNearestRemember() {
        var space = SampleSpace(dimensions: 8, name: "lore");
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecB = SampleVector(dimensions: 8, nonZeroIndex: 1);

        var sourceRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "s1"), Value: CellValue.Vector(components: vecA.Memory)),
                new StateCell(Key: CellName.Parse(candidate: "s2"), Value: CellValue.Vector(components: vecB.Memory)),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "source"),
            Space: "lore"
        );
        var intoMixRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Vector(components: vecA.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "intoMix"),
            Space: "lore"
        );
        var intoMeanRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Vector(components: vecA.Memory))],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "intoMean"),
            Space: "lore"
        );
        var intoNearestRow = new WorldStateRow(
            Capacity: 4,
            Cells: [],
            Kind: CellKind.Int,
            Name: CellName.Parse(candidate: "intoNearest")
        );
        var intoRememberRow = new WorldStateRow(
            Capacity: 4,
            Cells: [],
            Evicts: true,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "intoRemember"),
            Space: "lore"
        );

        var definition = BuildDocumentWithState(
            spaces: [space],
            rows: [sourceRow, intoMixRow, intoMeanRow, intoNearestRow, intoRememberRow]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        var peer = Principal.Peer(generation: 1, index: 4);

        var kinds = WorldMutationKindCatalog.KindsOf(section: WorldSection.State);

        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Budget: 64,
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                KindMask: kinds,
                Grantee: peer,
                Subject: GrantSubject.Section(section: WorldSection.State)
            )
        );

        // Each operation only reads the source table, so observe over it is the whole of its authority there.
        fixture.Server.Grant(
            actor: Principal.Console,
            grant: new WorldGrant(
                Budget: 64,
                Capability: WorldCapability.Observe,
                Exclusive: false,
                Grantee: peer,
                Subject: GrantSubject.State(name: "source")
            )
        );

        foreach (var targetName in new[] { "intoMix", "intoMean", "intoNearest", "intoRemember" }) {
            fixture.Server.Grant(
                actor: Principal.Console,
                grant: new WorldGrant(
                    Capability: WorldCapability.Edit,
                    Exclusive: false,
                    Grantee: peer,
                    Subject: GrantSubject.State(name: targetName)
                )
            );
        }

        // 1. mix into intoMix
        fixture.Server.EnqueueMutation(new WorldMutation.TransformState(
            Principal: peer,
            Transform: new StateTransform.Mix(
                Into: "intoMix",
                Terms: [
                    new VectorTerm(From: "source[s1]", Weight: 1),
                    new VectorTerm(From: "source[s2]", Weight: 1),
                ]
            )
        ));
        fixture.Step();
        var rowMix = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "intoMix")!;

        Assert.NotNull(@object: rowMix.Cells);
        Assert.NotEmpty(collection: rowMix.Cells);

        // 2. mean into intoMean
        fixture.Server.EnqueueMutation(new WorldMutation.TransformState(
            Principal: peer,
            Transform: new StateTransform.Mean(
                From: "source",
                Into: "intoMean"
            )
        ));
        fixture.Step();
        var rowMean = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "intoMean")!;

        Assert.NotNull(@object: rowMean.Cells);
        Assert.NotEmpty(collection: rowMean.Cells);

        // 3. nearest into intoNearest
        fixture.Server.EnqueueMutation(new WorldMutation.TransformState(
            Principal: peer,
            Transform: new StateTransform.Nearest(
                From: "source",
                Into: "intoNearest",
                K: 1,
                Query: "source[s1]"
            )
        ));
        fixture.Step();
        var rowNearest = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "intoNearest")!;

        Assert.NotNull(@object: rowNearest.Cells);
        Assert.Single(collection: rowNearest.Cells);

        // 4. remember into intoRemember
        fixture.Server.EnqueueMutation(new WorldMutation.TransformState(
            Principal: peer,
            Transform: new StateTransform.Remember(
                From: "source[s1]",
                Into: "intoRemember",
                Key: "m1",
                UnlessWithin: "0.9"
            )
        ));
        fixture.Step();
        var rowRemember = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "intoRemember")!;

        Assert.NotNull(@object: rowRemember.Cells);
        Assert.Single(collection: rowRemember.Cells);
        Assert.Equal("m1", rowRemember.Cells[0].Key.Value);
    }
}
