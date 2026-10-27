using Puck.Commands;
using Puck.World.Protocol;
using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;
namespace Puck.World.Server.Tests;

/// <summary>Independent physical regions retain their identity across live controls and durable restore.</summary>
public sealed class WorldFieldRegionLawTests {
    private static WorldFieldsSection Region(string name, string field, float x, int cadence = 1, WorldLatticeScalar? resistance = null) => new(
        Name: name, Lattice: new WorldFieldLatticeDefinition(new DocumentVector3(x: x, y: -2f, z: -2f), 1f, 4, 4, 4, cadence),
        Fields: [new WorldFieldRow(Name: field, Min: 0f, Max: 100f)],
        Reactions: [new WorldReaction.Transform(When: [], Then: [new WorldFieldWrite(Field: field, Op: WorldFieldWriteOp.Add, Value: new WorldLatticeScalar(Row: (name + "Rate")))])],
        TravelResistance: resistance);
    private static WorldDefinition Definition() {
        var section = WorldFieldsSection.ToStateSection(composite: [Region("first", "firstValue", -2f), Region("second", "secondValue", 20f, 2)]);

        return Fixtures.BuildDocument() with { StateRaw = section with { World = [.. section.World!, Scalar(name: "firstRate", value: 1), Scalar(name: "secondRate", value: 2)] } };
    }
    private static WorldStateRow Scalar(string name, int value) => new(Name: CellName.Parse(candidate: name), Kind: CellKind.Fixed,
        Min: 0, Max: FixedQ4816.FromInteger(value: 100).Value, Cells: [new StateCell(CellName.Parse(candidate: WorldStateRow.SlotKey), CellValue.Fixed(rawBits: FixedQ4816.FromInteger(value: value).Value))]);
    private static void Rate(WorldServer server, string name, int value) => server.EnqueueMutation(new WorldMutation.UpsertStateCell(
        Principal.Console, (name + "Rate"), WorldStateRow.SlotKey, FixedQ4816.FromInteger(value: value).Value, WorldDocumentWriteKind.Set));

    [Fact]
    public void IndependentControlsCheckpointRestoreAndReplayPreserveOtherRegions() {
        using var changed = Fixtures.FreshServer(Definition());
        using var control = Fixtures.FreshServer(Definition());

        for (var tick = 0; (tick < 3); tick++) { changed.Step(); control.Step(); }
        var first = changed.Server.Population.FieldRegions[0].Lattice.Capture().Raw[0].ToArray();

        Rate(changed.Server, "first", 0);
        for (var tick = 0; (tick < 4); tick++) {
            changed.Step(); control.Step();
            Assert.Equal(first, changed.Server.Population.FieldRegions[0].Lattice.Capture().Raw[0]);
            Assert.Equal(control.Server.Population.FieldRegions[1].Lattice.Capture().Raw[0], changed.Server.Population.FieldRegions[1].Lattice.Capture().Raw[0]);
        }
        Assert.True(condition: changed.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!), out var decoded, out reason), userMessage: reason);
        Assert.Equal(new[] { "first", "second" }, decoded!.Fields!.Select(selector: region => region.Region));
        Assert.Equal("secondValue", decoded.Fields![1].FieldNames[0]);
        using var restored = Fixtures.FreshServer(Definition());

        restored.Server.RestoreCheckpoint(checkpoint: decoded);
        Assert.Equal(WorldStateHashComposition.HashWorld(server: changed.Server, tick: (changed.Server.NextInputTick - 1UL)), WorldStateHashComposition.HashWorld(server: restored.Server, tick: (restored.Server.NextInputTick - 1UL)));
        Rate(changed.Server, "first", 3); Rate(restored.Server, "first", 3);
        for (var tick = 0; (tick < 4); tick++) {
            changed.Step(); restored.Step();
            Assert.Equal(WorldStateHashComposition.HashWorld(server: changed.Server, tick: (changed.Server.NextInputTick - 1UL)), WorldStateHashComposition.HashWorld(server: restored.Server, tick: (restored.Server.NextInputTick - 1UL)));
        }
        var before = restored.Server.Population.CaptureFieldRegions();

        Assert.Throws<InvalidOperationException>(testCode: () => restored.Server.Population.RestoreFieldRegions(checkpoints: [before[0], before[1] with { FieldNames = ["wrong"] }]));
        Assert.Equal(before[0].Cells.Raw[0], restored.Server.Population.FieldRegions[0].Lattice.Capture().Raw[0]);
        Assert.Throws<InvalidOperationException>(testCode: () => restored.Server.Population.ValidateFieldRegions(checkpoints: [before[0] with { Region = "wrong" }, before[1]]));
    }

    private static WorldRule ReadFieldRule(string output, string key) => new(
        Name: CellName.Parse(candidate: output), Mode: ActionTriggerMode.Level,
        Effects: [new ActionEffect.SetState(State: output, Expression: new ExpressionProgram(Instructions: [Instruction.Operand(key: key, name: "firstValue")]))]);

    [Fact]
    public void StaticAndComputedPhysicalCellReadsObserveLiveValuesAndBoundTheirIndices() {
        var definition = Definition();

        definition = definition with {
            StateRaw = definition.StateRaw! with {
                World = [.. definition.StateRaw!.World!, Scalar(name: "literalRead", value: 0), Scalar(name: "computedRead", value: 0), StateFixtures.IntSlot(name: "ordinaryRead", value: 0L),
                new WorldStateRow(Name: CellName.Parse(candidate: "index"), Kind: CellKind.Int, Domain: StateDomain.Keys.Instance,
                    Cells: [new StateCell(CellName.Parse(candidate: "0"), CellValue.Int(value: 63))])],
            },
            Rules = [ReadFieldRule(key: "63", output: "literalRead"), ReadFieldRule(key: "$cell:index:0", output: "computedRead"),
                new WorldRule(Name: CellName.Parse(candidate: "ordinaryRead"), Effects: [new ActionEffect.SetState(State: "ordinaryRead",
                    Expression: new ExpressionProgram(Instructions: [Instruction.Operand(key: "0", name: "index")]))])],
        };
        using var fixture = Fixtures.FreshServer(definition);
        var cells = new long[64]; cells[63] = FixedQ4816.FromInteger(value: 7).Value;
        fixture.Server.Population.FieldRegions[0].Lattice.Restore(checkpoint: new Puck.Physics.Fields.FieldLattice.Checkpoint(Raw: [cells]));
        fixture.Step();
        long Output(string name) => StateRows.FindCell(cells: WorldDefinitionRows.FindStateRow(fixture.Server.ExportDefinition().State, name)!.Cells, key: WorldStateRow.SlotKey)!.Value.Raw;
        Assert.Equal(FixedQ4816.FromInteger(value: 7).Value, Output(name: "literalRead"));
        Assert.Equal(Output(name: "literalRead"), Output(name: "computedRead"));
        Assert.Equal(63L, Output(name: "ordinaryRead"));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "index", "0", 64, WorldDocumentWriteKind.Set));
        fixture.Step();
        Assert.Equal(FixedQ4816.FromInteger(value: 8).Value, Output(name: "literalRead"));
        Assert.Equal(0L, Output(name: "computedRead"));
        Assert.Equal(64L, Output(name: "ordinaryRead"));
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertStateCell(Principal.Console, "index", "0", -1, WorldDocumentWriteKind.Set));
        fixture.Step();
        Assert.Equal(0L, Output(name: "computedRead"));
        Assert.Equal(-1L, Output(name: "ordinaryRead"));
        Assert.True(condition: fixture.Server.Definition.StateCatalog.TryResolve(handle: out var row, lane: StateLane.Document, name: "firstValue"));
        Assert.False(condition: fixture.Server.RuleHost.TryReadHostOwnedCell(row.Ordinal, -1, out _));
        Assert.False(condition: fixture.Server.RuleHost.TryReadHostOwnedCell(row.Ordinal, 64, out _));
    }
    [InlineData("-1")]
    [InlineData("64")]
    [InlineData("not-index")]
    [Theory]
    public void StaticPhysicalCellReadsRefuseKeysOutsideTheirOwnRegion(string key) {
        var definition = Definition();

        definition = definition with {
            StateRaw = definition.StateRaw! with { World = [.. definition.StateRaw!.World!, Scalar(name: "result", value: 0)] },
            Rules = [ReadFieldRule(key: key, output: "result")],
        };
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out var reason));
        Assert.Contains(actualString: reason, expectedSubstring: "StateCellUndeclared");
    }
    [Fact]
    public void TravelResistanceReadsLiveScalarAndLeavesBodiesOutsideRegionUnaffected() {
        var definition = Definition();

        definition = definition with {
            StateRaw = definition.StateRaw! with {
                Lattices = definition.StateRaw!.Lattices!.Select(selector: topology => (((topology is WorldFieldTopology physical) && (physical.Name == "first"))
            ? physical with { TravelResistance = new WorldLatticeScalar(Row: "firstRate") } : topology)).ToArray(),
            },
        };
        using var fixture = Fixtures.FreshServer(definition);
        var body = fixture.JoinSeat(); var baseline = body.EffectiveMoveSpeed;

        fixture.Step();
        Assert.Equal((baseline / FixedQ4816.FromInteger(value: 2)), body.EffectiveMoveSpeed);
        Rate(fixture.Server, "first", 3); fixture.Step();
        Assert.Equal((baseline / FixedQ4816.FromInteger(value: 4)), body.EffectiveMoveSpeed);
        body.Pose(pitchRadians: 0f, rollRadians: 0f, x: 10f, y: 0f, yawRadians: 0f, z: 0f);
        fixture.Step();
        Assert.Equal(baseline, body.EffectiveMoveSpeed);
    }
}
