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
        if (!StateVector.TryCreate(components: components, vector: out var vector, error: out var error)) {
            throw new InvalidOperationException(error);
        }
        return vector;
    }

    private static StateSpace SampleSpace(string name = "lore", int dimensions = 8) => new(
        Dimensions: dimensions,
        Model: "test-model",
        Name: CellName.Parse(candidate: name),
        Revision: "1"
    );

    private static WorldDefinition BuildDocumentWithState(StateSpace[] spaces, WorldStateRow[] rows, WorldRule[]? rules = null) =>
        Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(
                Spaces: spaces,
                World: rows
            ),
            Rules = rules
        };

    [Fact]
    public void Vector_GrantedAndUngrantedWrites() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );
        var definition = BuildDocumentWithState(spaces: [space], rows: [memoriesRow]);
        using var fixture = Fixtures.FreshServer(definition: definition);

        var peer = WorldPrincipal.Peer(generation: 1, index: 4);
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
        Assert.NotNull(stateRowBefore);
        Assert.True(stateRowBefore.Cells is null || stateRowBefore.Cells.Count == 0);

        // 2. Grant Mutate on State section and Edit on memories row
        var kinds = WorldMutationKindCatalog.KindsOf(section: WorldSection.State);
        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Budget: 16,
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                KindMask: kinds,
                Principal: peer,
                Subject: GrantSubject.Section(section: WorldSection.State)
            )
        );
        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Capability: WorldCapability.Edit,
                Exclusive: false,
                Principal: peer,
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
        Assert.NotNull(stateRowAfter);
        Assert.NotNull(stateRowAfter.Cells);
        Assert.Single(stateRowAfter.Cells);
        var writtenCell = stateRowAfter.Cells[0];
        Assert.Equal("m1", writtenCell.Key.Value);
        Assert.NotNull(writtenCell.Vector);
        Assert.True(writtenCell.Vector.Components.SequenceEqual(vector.Components));
    }

    [Fact]
    public void Vector_Undo() {
        var space = SampleSpace(name: "lore", dimensions: 8);
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
            Principal: WorldPrincipal.Console,
            Row: "memories",
            Key: "m1",
            Value: 0L,
            Kind: WorldDocumentWriteKind.Set,
            Vector: vector
        ));
        fixture.Step();

        var stateRowAfterWrite = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories");
        Assert.NotNull(stateRowAfterWrite);
        Assert.NotNull(stateRowAfterWrite.Cells);
        Assert.Single(stateRowAfterWrite.Cells);

        // Enqueue undo
        fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
        fixture.Step();

        var stateRowAfterUndo = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories");
        Assert.NotNull(stateRowAfterUndo);
        Assert.True(stateRowAfterUndo.Cells is null || stateRowAfterUndo.Cells.Count == 0);
    }

    [Fact]
    public void Vector_CheckpointRoundTrip() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var vector = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "m1"), Vector: vector)],
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
        Assert.NotNull(checkpoint);

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

            Assert.NotNull(restoredServer.Definition.Spaces);
            Assert.Single(restoredServer.Definition.Spaces);
            Assert.Equal("lore", restoredServer.Definition.Spaces[0].Name.Value);
            Assert.Equal(8, restoredServer.Definition.Spaces[0].Dimensions);

            var restoredRow = WorldDefinitionRows.FindStateRow(rows: restoredServer.Definition.State, name: "memories");
            Assert.NotNull(restoredRow);
            Assert.NotNull(restoredRow.Cells);
            Assert.Single(restoredRow.Cells);
            var restoredCell = restoredRow.Cells[0];
            Assert.Equal("m1", restoredCell.Key.Value);
            Assert.NotNull(restoredCell.Vector);
            Assert.True(restoredCell.Vector.Components.SequenceEqual(vector.Components));
        } finally {
            Directory.Delete(path: tempDir, recursive: true);
        }
    }

    [Fact]
    public void Vector_WorldSaveAndReload() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var vector = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "m1"), Vector: vector)],
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

            Assert.NotNull(reloadedDef.Spaces);
            Assert.Single(reloadedDef.Spaces);
            Assert.Equal("lore", reloadedDef.Spaces[0].Name.Value);
            Assert.Equal(8, reloadedDef.Spaces[0].Dimensions);

            var reloadedRow = WorldDefinitionRows.FindStateRow(rows: reloadedDef.State, name: "memories");
            Assert.NotNull(reloadedRow);
            Assert.NotNull(reloadedRow.Cells);
            Assert.Single(reloadedRow.Cells);
            var reloadedCell = reloadedRow.Cells[0];
            Assert.Equal("m1", reloadedCell.Key.Value);
            Assert.NotNull(reloadedCell.Vector);
            Assert.True(reloadedCell.Vector.Components.SequenceEqual(vector.Components));
        } finally {
            if (File.Exists(path: tempPath)) {
                File.Delete(path: tempPath);
            }
        }
    }

    [Fact]
    public void Vector_EvictingTableReplayDeterminism() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var eventsRow = new WorldStateRow(
            Capacity: 2,
            Evicts: true,
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "events"),
            Space: "lore"
        );
        var definition = BuildDocumentWithState(spaces: [space], rows: [eventsRow]);

        using var serverA = Fixtures.FreshServer(definition: definition);
        using var serverB = Fixtures.FreshServer(definition: definition);

        var keys = new[] { "e1", "e2", "e3", "e4" };
        for (var i = 0; i < keys.Length; i++) {
            var vec = SampleVector(dimensions: 8, nonZeroIndex: (i % 8));
            var mutation = new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.Console,
                Row: "events",
                Key: keys[i],
                Value: 0L,
                Kind: WorldDocumentWriteKind.Set,
                Vector: vec
            );

            serverA.Server.EnqueueMutation(mutation: mutation);
            serverB.Server.EnqueueMutation(mutation: mutation);

            serverA.Step();
            serverB.Step();

            Assert.Equal(
                expected: WorldRuntimeStateHash.HashAuthoritative(server: serverA.Server, tick: serverA.Server.NextInputTick),
                actual: WorldRuntimeStateHash.HashAuthoritative(server: serverB.Server, tick: serverB.Server.NextInputTick)
            );
        }

        // Both servers should have evicted the oldest entries and retain only e3 and e4
        var rowA = WorldDefinitionRows.FindStateRow(rows: serverA.Server.Definition.State, name: "events")!;
        var rowB = WorldDefinitionRows.FindStateRow(rows: serverB.Server.Definition.State, name: "events")!;
        Assert.Equal(2, rowA.Cells!.Count);
        Assert.Equal(2, rowB.Cells!.Count);
        Assert.Equal("e3", rowA.Cells[0].Key.Value);
        Assert.Equal("e4", rowA.Cells[1].Key.Value);
        Assert.Equal("e3", rowB.Cells[0].Key.Value);
        Assert.Equal("e4", rowB.Cells[1].Key.Value);
    }

    [Fact]
    public void Vector_FrameMixEqualsCrossRow() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecB = SampleVector(dimensions: 8, nonZeroIndex: 1);

        var sourceTable = new StateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "s1"), Vector: vecA),
                new StateCell(Key: CellName.Parse(candidate: "s2"), Vector: vecB),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "sources"),
            Space: "lore"
        );
        var targetSlot = new StateRow(
            Cells: [new StateCell(Key: StateRow.SlotKey, Vector: vecA)],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "target"),
            Space: "lore"
        );

        var rows = new StateRow[] { sourceTable, targetSlot };
        var layout = new FrameLayout(
            rows: rows,
            spaces: name => (name == "lore") ? space : null,
            topology: static _ => null
        );

        var frame = new StateFrame(layout: layout, rows: rows);
        frame.Load(source: new RowStore(rows: rows));

        Assert.True(layout.TryOrdinal(name: "sources", ordinal: out var sourceOrd));
        Assert.True(layout.TryOrdinal(name: "target", ordinal: out var targetOrd));

        // 1. Frame mix
        var mixTransform = new ResolvedVectorTransform.Mix(
            TargetKey: StateRow.SlotKey,
            TargetRowName: "target",
            TargetRowOrdinal: targetOrd,
            Terms: [
                new ResolvedMixTerm(SourceRowOrdinal: sourceOrd, SourceRowName: "sources", SourceKey: CellName.Parse(candidate: "s1"), Vector: null, Weight: 3),
                new ResolvedMixTerm(SourceRowOrdinal: sourceOrd, SourceRowName: "sources", SourceKey: CellName.Parse(candidate: "s2"), Vector: null, Weight: 1),
            ]
        );

        Assert.True(condition: frame.TryApplyVector(transform: mixTransform, resultVector: out var frameResult, reason: out var frameReason), userMessage: frameReason);
        Assert.NotNull(frameResult);

        // 2. Cross-row mix via VectorTransforms kernel
        Span<sbyte> crossRowDest = stackalloc sbyte[8];
        Assert.True(condition: VectorTransforms.TryMix(
            destination: crossRowDest,
            refusal: out var mixRefusal,
            vectors: [vecA.Memory, vecB.Memory],
            weights: [3, 1]
        ), userMessage: mixRefusal?.ToString());

        Assert.True(crossRowDest.SequenceEqual(frameResult.Components));
    }

    [Fact]
    public void Vector_CrossRowNearestAndRemember() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var vecAmbush = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecGift = SampleVector(dimensions: 8, nonZeroIndex: 1);

        var eventsRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "ambush"), Vector: vecAmbush),
                new StateCell(Key: CellName.Parse(candidate: "gift"), Vector: vecGift),
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
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Vector: vecAmbush)],
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
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 1L)],
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
                Comparison: ActionStateComparison.Equal,
                State: "caravanAttacked",
                Value: 1m
            ),
            Name: CellName.Parse(candidate: "remember-and-recall")
        );

        var definition = BuildDocumentWithState(
            spaces: [space],
            rows: [eventsRow, memoriesRow, situationRow, recalledRow, caravanAttackedRow],
            rules: [rule]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        // Check that remember wrote "ambush" into memories
        var memoriesAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories")!;
        Assert.NotNull(memoriesAfter.Cells);
        var ambushMemory = Assert.Single(memoriesAfter.Cells);
        Assert.Equal("ambush", ambushMemory.Key.Value);
        Assert.NotNull(ambushMemory.Vector);
        Assert.True(ambushMemory.Vector.Components.SequenceEqual(vecAmbush.Components));

        // Check that nearest wrote "ambush" into recalled with similarity >= 0.5
        var recalledAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "recalled")!;
        Assert.NotNull(recalledAfter.Cells);
        var recalledCell = Assert.Single(recalledAfter.Cells);
        Assert.Equal("ambush", recalledCell.Key.Value);
        var sim = FixedQ4816.FromRawBits(value: recalledCell.Value);
        Assert.True(sim >= FixedQ4816.FromRawBits(value: 32768)); // >= 0.5

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
        Assert.NotNull(memoriesDup.Cells);
        Assert.Single(memoriesDup.Cells);
        Assert.Equal("ambush", memoriesDup.Cells[0].Key.Value);
    }

    [Fact]
    public void Vector_LargeRowPresentationProjection() {
        var space = SampleSpace(name: "lore", dimensions: 256);
        // 256 cells * 256 dimensions = 65,536 bytes (the MaxVectorRowBytes limit)
        var cells = new StateCell[256];
        for (var i = 0; i < 256; i++) {
            var vec = SampleVector(dimensions: 256, nonZeroIndex: (i % 256));
            cells[i] = new StateCell(Key: CellName.Parse(candidate: $"c{i}"), Vector: vec);
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
        var projection = WorldProjection.Compose(
            authority: "boot",
            definition: definition,
            revision: 1,
            tier: WorldDisclosureTier.Presentation
        );
        Assert.NotNull(projection);

        // Serialize and deserialize projection
        var serialized = WorldProjection.Serialize(projection: projection);
        Assert.True(condition: WorldProjection.TryDeserialize(utf8Json: serialized, projection: out var decoded, reason: out var decodeReason), userMessage: decodeReason);
        Assert.NotNull(decoded);
        Assert.NotNull(decoded.Observations);

        var observedRow = Assert.Single(decoded.Observations);
        Assert.Equal("largeTable", observedRow.Name);
        Assert.Equal(CellKind.Vector, observedRow.Kind);
        Assert.NotNull(observedRow.Cells);
        Assert.Equal(256, observedRow.Cells.Count);

        for (var i = 0; i < 256; i++) {
            var origCell = cells[i];
            var obsCell = observedRow.Cells[i];
            Assert.Equal(origCell.Key.Value, obsCell.Key);
            Assert.NotNull(obsCell.Vector);
            Assert.Equal(256, obsCell.Vector.Dimensions);
            Assert.True(origCell.Vector!.Components.SequenceEqual(obsCell.Vector.Components));
        }

        // Hydrate back into definition (undisclosed state section becomes default empty)
        Assert.True(condition: WorldProjection.TryToDefinition(projection: decoded, definition: out var hydrated, reason: out var hydrateReason), userMessage: hydrateReason);
        Assert.NotNull(hydrated);
        Assert.Empty(hydrated.State);
    }

    [Fact]
    public void Vector_DigestEcho() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var vector = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse(candidate: "m1"), Vector: vector)],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "memories"),
            Space: "lore"
        );
        var definition = BuildDocumentWithState(spaces: [space], rows: [memoriesRow]);

        using var row = HostRow.Build(name: "boot", definition: definition);
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new FakeConsoleAuthority(instance: row.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: row.Instance.Link
        )]);

        var result = registry.Submit(line: "world.state memories m1");
        Assert.False(result.IsError);

        var expectedDigest = vector.ComputeDigest();
        var expectedDigestString = $"vector[8] #{expectedDigest:x8}";
        Assert.Contains(expectedSubstring: expectedDigestString, actualString: result.Output);
    }

    [Fact]
    public void Vector_SimilarQueryAndVisibility() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecB = SampleVector(dimensions: 8, nonZeroIndex: 1);

        var situationRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Vector: vecA)],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "situation"),
            Space: "lore"
        );
        var eventsRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "gift"), Vector: vecA),
                new StateCell(
                    Key: CellName.Parse(candidate: "secret"),
                    Vector: vecB,
                    Visibility: new StateVisibility(Readers: ["console", "seat1"])
                ),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "events"),
            Space: "lore"
        );

        var definition = BuildDocumentWithState(spaces: [space], rows: [situationRow, eventsRow]);
        using var row = HostRow.Build(name: "boot", definition: definition);
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new FakeConsoleAuthority(instance: row.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: row.Instance.Link
        )]);

        // 1. Query as Console (unrestricted): both gift and secret appear
        var consoleResult = registry.Submit(line: "world.state.similar situation $value events");
        Assert.False(consoleResult.IsError);
        Assert.Contains(expectedSubstring: "'events'.'gift'", actualString: consoleResult.Output);
        Assert.Contains(expectedSubstring: "'events'.'secret'", actualString: consoleResult.Output);

        // 2. Query as Peer (restricted): secret is hidden, only gift appears
        var commandSource = new TextCommandSource(registry: registry);
        var peerResults = new List<CommandResult>();
        using var peerSession = commandSource.CreateSession(
            principal: CommandPrincipal.Peer(index: 4, generation: 1),
            onResult: (_, res) => peerResults.Add(item: res)
        );
        peerSession.Enqueue(line: "world.state.similar situation $value events");
        commandSource.Collect();
        var peerResult = Assert.Single(peerResults);
        Assert.False(peerResult.IsError);
        Assert.Contains(expectedSubstring: "'events'.'gift'", actualString: peerResult.Output);
        Assert.DoesNotContain(expectedSubstring: "'events'.'secret'", actualString: peerResult.Output);

        // 3. Query with a hidden query vector returns an error
        var hiddenSituationRow = new WorldStateRow(
            Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Vector: vecA
            )],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "hiddenQuery"),
            Space: "lore",
            Visibility: new StateVisibility(Readers: ["seat1"])
        );
        var defWithHidden = BuildDocumentWithState(spaces: [space], rows: [hiddenSituationRow, eventsRow]);
        using var rowHidden = HostRow.Build(name: "boot2", definition: defWithHidden);
        var registryHidden = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new FakeConsoleAuthority(instance: rowHidden.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: rowHidden.Instance.Link
        )]);

        var hiddenCommandSource = new TextCommandSource(registry: registryHidden);
        var hiddenResults = new List<CommandResult>();
        using var peerSession2 = hiddenCommandSource.CreateSession(
            principal: CommandPrincipal.Peer(index: 4, generation: 1),
            onResult: (_, res) => hiddenResults.Add(item: res)
        );
        peerSession2.Enqueue(line: "world.state.similar hiddenQuery $value events");
        hiddenCommandSource.Collect();
        var hiddenQueryResult = Assert.Single(hiddenResults);
        Assert.True(hiddenQueryResult.IsError);
        Assert.Contains(expectedSubstring: "query cell 'hiddenQuery.$value' is hidden", actualString: hiddenQueryResult.Output);
    }
}
