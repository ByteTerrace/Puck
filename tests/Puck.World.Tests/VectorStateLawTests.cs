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
        Fixtures.SkipIfReplayDirectoryUnwritable();

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
        Assert.True(tape.TryBeginRecording(name: tapeName, refusal: out var refusal), refusal);

        var keys = new[] { "e1", "e2", "e3", "e4" };
        var recordedHashes = new List<ulong>();

        for (var i = 0; i < keys.Length; i++) {
            var vec = SampleVector(dimensions: 8, nonZeroIndex: (i % 8));
            transport.SubmitWorldMutation(mutation: new WorldMutation.UpsertStateCell(
                Principal: WorldPrincipal.Console,
                Row: "events",
                Key: keys[i],
                Value: 0L,
                Kind: WorldDocumentWriteKind.Set,
                Vector: vec
            ));

            serverA.Step();
            tape.NoteTick();
            recordedHashes.Add(WorldRuntimeStateHash.HashAuthoritative(server: serverA.Server, tick: serverA.Server.NextInputTick));
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

            Assert.True(tapeB.TryBeginDrive(documentPath: null, forkName: "never-recorded", name: tapeName, refusal: out refusal, toTick: null), refusal);

            for (var i = 0; i < keys.Length; i++) {
                tapeB.InjectDriveTick();
                serverB.Step();
                tapeB.NoteTick();

                var replayHash = WorldRuntimeStateHash.HashAuthoritative(server: serverB.Server, tick: serverB.Server.NextInputTick);
                Assert.Equal(recordedHashes[i], replayHash);
            }

            var rowB = WorldDefinitionRows.FindStateRow(rows: serverB.Server.Definition.State, name: "events")!;
            Assert.Equal(2, rowB.Cells!.Count);
            Assert.Equal("e3", rowB.Cells[0].Key.Value);
            Assert.Equal("e4", rowB.Cells[1].Key.Value);
        } finally {
            var path = WorldReplayTape.PathFor(name: tapeName);
            if (File.Exists(path)) {
                File.Delete(path);
            }
        }
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

    [Fact]
    public void VectorRemember_SkipsNearDuplicate_AndIgnoresOwnKey() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var componentsPrime = new sbyte[8];
        componentsPrime[0] = 126;
        componentsPrime[1] = 15;
        Assert.True(StateVector.TryCreate(components: componentsPrime, vector: out var vecAPrime, error: out var err), err);

        var eventsRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "e1"), Vector: vecAPrime),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse(candidate: "events"),
            Space: "lore"
        );
        var memoriesRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "k1"), Vector: vecA),
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
            spaces: [space],
            rows: [eventsRow, memoriesRow],
            rules: [rule]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        var memoriesAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories")!;
        Assert.NotNull(memoriesAfter.Cells);
        var k1Cell = Assert.Single(memoriesAfter.Cells);
        Assert.Equal("k1", k1Cell.Key.Value);
        Assert.NotNull(k1Cell.Vector);
        Assert.True(k1Cell.Vector.Components.SequenceEqual(vecAPrime.Components));
    }

    [Fact]
    public void Vector_RuleCopyFiredIntoAbsentKey_ReadsBackCopiedBytes() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecB = SampleVector(dimensions: 8, nonZeroIndex: 1);

        var eventsRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse(candidate: "k1"), Vector: vecA),
                new StateCell(Key: CellName.Parse(candidate: "k2"), Vector: vecB),
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
            spaces: [space],
            rows: [eventsRow, memoriesRow],
            rules: [rule]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        fixture.Step();

        var memoriesAfter = WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "memories")!;
        Assert.NotNull(memoriesAfter.Cells);
        Assert.Equal(2, memoriesAfter.Cells.Count);

        var c1 = StateRows.FindCell(cells: memoriesAfter.Cells, key: CellName.Parse("k1"));
        var c2 = StateRows.FindCell(cells: memoriesAfter.Cells, key: CellName.Parse("k2"));

        Assert.NotNull(c1);
        Assert.NotNull(c2);
        Assert.NotNull(c1.Vector);
        Assert.NotNull(c2.Vector);
        Assert.True(c1.Vector.Components.SequenceEqual(vecA.Components));
        Assert.True(c2.Vector.Components.SequenceEqual(vecB.Components));
    }

    [Fact]
    public void Vector_WorldStateTransform_RefusesInvalidShapesByName() {
        var spaceLore = SampleSpace(name: "lore", dimensions: 8);
        var spaceOther = SampleSpace(name: "other", dimensions: 16);

        var vecLore = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecOther = SampleVector(dimensions: 16, nonZeroIndex: 0);

        var loreRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse("c1"), Vector: vecLore)],
            Kind: CellKind.Vector,
            Name: CellName.Parse("loreRow"),
            Space: "lore"
        );
        var otherRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse("c1"), Vector: vecOther)],
            Kind: CellKind.Vector,
            Name: CellName.Parse("otherRow"),
            Space: "other"
        );
        var ranksRow = new WorldStateRow(
            Capacity: 4,
            Cells: [],
            Kind: CellKind.Int,
            Name: CellName.Parse("ranks")
        );
        var unconstrainedKeyedRow = new WorldStateRow(
            Cells: [],
            Domain: StateDomain.Keys.Instance,
            Kind: CellKind.Int,
            Name: CellName.Parse("unconstrainedKeyed")
        );
        var textSlotRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Text: "")],
            Kind: CellKind.Text,
            Name: CellName.Parse("textSlot")
        );
        var textTableRow = new WorldStateRow(
            Capacity: 4,
            Cells: [],
            Kind: CellKind.Text,
            Name: CellName.Parse("textTable")
        );
        var notKeyedBoolRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 1L)],
            Kind: CellKind.Bool,
            Name: CellName.Parse("boolSlot")
        );
        var intWhereRow = new WorldStateRow(
            Capacity: 4,
            Cells: [new StateCell(Key: CellName.Parse("c1"), Value: 1L)],
            Kind: CellKind.Int,
            Name: CellName.Parse("intWhere")
        );

        var definition = BuildDocumentWithState(
            spaces: [spaceLore, spaceOther],
            rows: [loreRow, otherRow, ranksRow, unconstrainedKeyedRow, textSlotRow, textTableRow, notKeyedBoolRow, intWhereRow]
        );

        using var row = HostRow.Build(name: "boot", definition: definition);
        var registry = new CommandRegistry(modules: [new WorldStateCommandModule(
            authority: new FakeConsoleAuthority(instance: row.Instance),
            echoes: new WorldDeferredVerbEchoes(),
            link: row.Instance.Link
        )]);

        string? diag = null;
        row.Server.EchoTap = echo => { if (echo.Rejected) { diag = echo.Message; } };

        void AssertRefused(string transformJson, string expectedReason) {
            diag = null;
            var res = registry.Submit($"world.state.transform {transformJson}");
            Assert.False(res.IsError);
            row.Server.Advance(stepTicks: Fixtures.StepTicks);
            Assert.NotNull(diag);
            Assert.Contains(expectedReason, diag, StringComparison.OrdinalIgnoreCase);
        }

        // 1. Cross-space operand refused by name
        AssertRefused(
            """{"$type":"nearest","from":"loreRow","query":"otherRow[c1]","into":"ranks","k":1}""",
            "do not match"
        );

        // 2. Where that is not a keyed Bool row:
        // Case A: kind is not Bool (Int)
        AssertRefused(
            """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"ranks","k":1,"where":"intWhere"}""",
            "must be kind bool"
        );

        // Case B: Bool but not keyed (slot)
        AssertRefused(
            """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"ranks","k":1,"where":"boolSlot"}""",
            "must be a keyed Bool table"
        );

        // 3. Keyed nearest into without capacity
        AssertRefused(
            """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"unconstrainedKeyed","k":1}""",
            "declares no capacity"
        );

        // 4. Text into that is not a slot or has k != 1
        // Case A: Text table (not a slot)
        AssertRefused(
            """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"textTable","k":1}""",
            "must be a slot"
        );

        // Case B: Text slot with k != 1 (k = 2)
        AssertRefused(
            """{"$type":"nearest","from":"loreRow","query":"loreRow[c1]","into":"textSlot","k":2}""",
            "requires k = 1"
        );
    }

    [Fact]
    public void Vector_WorldStateTransform_PrincipalHoldingEditOnlyOnInto_SucceedsForMixMeanNearestRemember() {
        var space = SampleSpace(name: "lore", dimensions: 8);
        var vecA = SampleVector(dimensions: 8, nonZeroIndex: 0);
        var vecB = SampleVector(dimensions: 8, nonZeroIndex: 1);

        var sourceRow = new WorldStateRow(
            Capacity: 4,
            Cells: [
                new StateCell(Key: CellName.Parse("s1"), Vector: vecA),
                new StateCell(Key: CellName.Parse("s2"), Vector: vecB),
            ],
            Kind: CellKind.Vector,
            Name: CellName.Parse("source"),
            Space: "lore"
        );
        var intoMixRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Vector: vecA)],
            Kind: CellKind.Vector,
            Name: CellName.Parse("intoMix"),
            Space: "lore"
        );
        var intoMeanRow = new WorldStateRow(
            Cells: [new StateCell(Key: WorldStateRow.SlotKey, Vector: vecA)],
            Kind: CellKind.Vector,
            Name: CellName.Parse("intoMean"),
            Space: "lore"
        );
        var intoNearestRow = new WorldStateRow(
            Capacity: 4,
            Cells: [],
            Kind: CellKind.Int,
            Name: CellName.Parse("intoNearest")
        );
        var intoRememberRow = new WorldStateRow(
            Capacity: 4,
            Cells: [],
            Evicts: true,
            Kind: CellKind.Vector,
            Name: CellName.Parse("intoRemember"),
            Space: "lore"
        );

        var definition = BuildDocumentWithState(
            spaces: [space],
            rows: [sourceRow, intoMixRow, intoMeanRow, intoNearestRow, intoRememberRow]
        );

        using var fixture = Fixtures.FreshServer(definition: definition);
        var peer = WorldPrincipal.Peer(generation: 1, index: 4);

        var kinds = WorldMutationKindCatalog.KindsOf(section: WorldSection.State);
        fixture.Server.Grant(
            actor: WorldPrincipal.Console,
            grant: new WorldGrant(
                Budget: 64,
                Capability: WorldCapability.Mutate,
                Exclusive: false,
                KindMask: kinds,
                Principal: peer,
                Subject: GrantSubject.Section(section: WorldSection.State)
            )
        );

        foreach (var targetName in new[] { "intoMix", "intoMean", "intoNearest", "intoRemember" }) {
            fixture.Server.Grant(
                actor: WorldPrincipal.Console,
                grant: new WorldGrant(
                    Capability: WorldCapability.Edit,
                    Exclusive: false,
                    Principal: peer,
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
        Assert.NotNull(rowMix.Cells);
        Assert.NotEmpty(rowMix.Cells);

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
        Assert.NotNull(rowMean.Cells);
        Assert.NotEmpty(rowMean.Cells);

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
        Assert.NotNull(rowNearest.Cells);
        Assert.Single(rowNearest.Cells);

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
        Assert.NotNull(rowRemember.Cells);
        Assert.Single(rowRemember.Cells);
        Assert.Equal("m1", rowRemember.Cells[0].Key.Value);
    }
}
