using System.Reflection;
using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.Physics.Fields;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Pins the field lattice's authoritative install/rebuild/undo boundaries through a live
/// <see cref="WorldPopulation"/>/<see cref="WorldServer"/> — the kernel's own reaction, paint, checkpoint, and
/// read-back laws live in <c>tests/Puck.Physics.Tests/FieldLatticeLawTests.cs</c>, which needs no server.</summary>
public sealed class WorldFieldLatticeLawTests {
    private static WorldFieldsSection Fields(
        int width = 1,
        int depth = 1,
        int layers = 1,
        float cellSize = 1f,
        float heightScale = 0f,
        IReadOnlyList<WorldReaction>? reactions = null
    ) => new(
        Lattice: new WorldFieldLatticeDefinition(
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: cellSize,
            Width: width,
            Depth: depth,
            Layers: layers,
            StepEveryTicks: 1
        ),
        Fields: [new WorldFieldRow(Name: "heat", Min: 0f, Max: 10f, HeightScale: heightScale, Color: ((heightScale > 0f) ? "#ffffff" : null))],
        Reactions: reactions
    );
    private static WorldDefinition DrawLatticeDefinition(long cursor = 0L) {
        var definition = Fixtures.BuildDocument();

        return definition with {
            StateRaw = new WorldStateSection(
                World: [new WorldStateRow(
                    Name: CellName.Parse(candidate: "drawn"),
                    Kind: CellKind.Fixed,
                    DrawCursor: cursor,
                    Domain: new StateDomain.CellsOf(Topology: "grid"),
                    Field: new WorldStateFieldTrait(
                        Min: 0f,
                        Max: 1f,
                        Paint: [new WorldLatticeFill.Draw(Generator: new StateGenerator(Source: GeneratorSource.UniformRange, RangeMin: 0L, RangeMax: FixedQ4816.One.Value))]
                    )
                )],
                Lattices: [new WorldFieldTopology(
                    Name: "grid",
                    Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
                    CellSize: 1f,
                    Width: 2,
                    Depth: 1,
                    Layers: 1,
                    StepEveryTicks: 1
                )]
            )
        };
    }

    [Fact]
    public void GenerateUndoAndWholeDocumentLoad_RepaintTheCursorNamedPass() {
        var boot = DrawLatticeDefinition();

        using var fixture = Fixtures.FreshServer(definition: boot);

        var lattice = Assert.IsType<FieldLattice>(@object: fixture.Server.Population.Fields);
        var bootCells = lattice.Capture().Raw[0].ToArray();

        fixture.Server.EnqueueMutation(mutation: new WorldMutation.Generate(Principal: WorldPrincipal.Console, Row: "drawn"));
        fixture.Step();

        var generatedCells = lattice.Capture().Raw[0].ToArray();

        Assert.NotEqual(expected: bootCells, actual: generatedCells);
        Assert.Equal(expected: 2L, actual: WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "drawn")!.DrawCursor);

        fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
        fixture.Step();

        Assert.Equal(expected: 0L, actual: WorldDefinitionRows.FindStateRow(rows: fixture.Server.Definition.State, name: "drawn")!.DrawCursor);
        Assert.Equal(expected: bootCells, actual: lattice.Capture().Raw[0]);

        var loaded = DrawLatticeDefinition(cursor: 2L);

        using var expectedFixture = Fixtures.FreshServer(definition: loaded);

        var expectedCells = Assert.IsType<FieldLattice>(@object: expectedFixture.Server.Population.Fields).Capture().Raw[0].ToArray();

        lattice.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [[0L, 0L]]));
        fixture.Server.EnqueueRebuild(
            request: new WorldRebuildRequest(
                ContentHash: WorldDefinitionFileSource.ComputeContentHash(content: WorldDefinitionSerialization.Serialize(definition: loaded)),
                Definition: loaded,
                Force: true,
                Kind: WorldRebuildKind.Load,
                PathHint: "lattice-draw-load-probe.world.json"
            ),
            principal: WorldPrincipal.Console
        );
        fixture.Step();

        Assert.Equal(expected: expectedCells, actual: lattice.Capture().Raw[0]);
    }
    [Fact]
    public void PopulationCreatesFields_WhenCollisionAndTargetsDoNotRequireAnSdfField() {
        var definition = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: Fields());

        Assert.True(
            condition: WorldDefinitionValidator.TryValidate(definition: definition, neighbours: null, reason: out var reason),
            userMessage: reason
        );

        var population = new WorldPopulation(definition: definition);

        Assert.NotNull(@object: population.Fields);
    }
    [Fact]
    public void PopulationRebuildInstallsReactionOnlyEditsWithoutResettingLiveCells() {
        var originalFields = Fields(reactions: [new WorldReaction.Transform(
            When: [],
            Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 1f)]
        )]);
        var replacementFields = Fields(reactions: [new WorldReaction.Transform(
            When: [],
            Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 2f)]
        )]);
        var original = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: originalFields);
        var replacement = Fixtures.WithLattice(composite: replacementFields, definition: original);
        var population = new WorldPopulation(definition: original);
        var lattice = Assert.IsType<FieldLattice>(@object: population.Fields);

        Fixtures.StepLattice(lattice: lattice);
        population.Rebuild(definition: replacement, solids: null);

        Assert.Same(expected: lattice, actual: population.Fields);
        Assert.Equal(expected: FixedQ4816.One, actual: lattice.Value(cell: 0, field: 0));

        Fixtures.StepLattice(lattice: lattice);

        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3), actual: lattice.Value(cell: 0, field: 0));
    }
    [Fact]
    public void PopulationRebuildRejectsAnIncompatibleLatticeBeforeChangingDerivedState() {
        var original = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: Fields());
        var incompatible = Fixtures.WithLattice(definition: original, composite: Fields(width: 2));
        var population = new WorldPopulation(definition: original);
        var lattice = Assert.IsType<FieldLattice>(@object: population.Fields);
        var input = lattice.Input;
        var revision = population.Revision;
        var seats = population.LocalSeatCount;

        Assert.Throws<InvalidOperationException>(testCode: () => population.Rebuild(
            definition: incompatible,
            solids: null
        ));

        Assert.Same(expected: lattice, actual: population.Fields);
        Assert.Same(expected: input, actual: lattice.Input);
        Assert.Equal(expected: FixedQ4816.Zero, actual: lattice.Value(cell: 0, field: 0));
        Assert.Equal(expected: revision, actual: population.Revision);
        Assert.Equal(expected: seats, actual: population.LocalSeatCount);
    }
    [Fact]
    public void UndoRefusesAnInjectedIncompatibleBaseBeforeChangingDefinitionProgramCellsOrSolids() {
        var boot = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: Fields());

        using var fixture = Fixtures.FreshServer(definition: boot);

        // Establish one honest journal entry so undo reaches its final base reconcile. The incompatible historical
        // base is injected because the current live gates correctly make such a journal/base combination
        // unreachable through public authoring; this law targets the defensive all-or-nothing door itself.
        fixture.Server.EnqueueMutation(mutation: new WorldMutation.UpsertStateRow(
            Principal: WorldPrincipal.Console,
            Row: new WorldStateRow(Name: CellName.Parse(candidate: "probe"), Kind: CellKind.Int)
        ));
        fixture.Step();

        var lattice = Assert.IsType<FieldLattice>(@object: fixture.Server.Population.Fields);
        var definitionBefore = fixture.DefinitionBytes();
        var inputBefore = lattice.Input;
        var cellsBefore = lattice.Capture().Raw.Select(selector: static field => field.ToArray()).ToArray();
        var revisionBefore = lattice.Revision;
        var solidsField = typeof(WorldServer).GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_solids")!;
        var solidsBefore = solidsField.GetValue(obj: fixture.Server);
        var baseField = typeof(WorldServer).GetField(bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic, name: "m_base")!;
        var incompatibleBase = Fixtures.WithLattice(definition: fixture.Server.Definition, composite: Fields(width: 2));
        WorldEditEcho? echo = null;

        baseField.SetValue(obj: fixture.Server, value: incompatibleBase);
        fixture.Server.EchoTap = next => echo = next;
        fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
        fixture.Step();

        Assert.True(condition: echo.HasValue);
        var observed = echo.Value;

        Assert.True(condition: observed.Rejected);
        Assert.Contains(expectedSubstring: "restored field runtime is incompatible", actualString: observed.Message, comparisonType: StringComparison.Ordinal);
        Assert.Equal(expected: definitionBefore, actual: fixture.DefinitionBytes());
        Assert.Same(expected: lattice, actual: fixture.Server.Population.Fields);
        Assert.Same(expected: inputBefore, actual: lattice.Input);
        Assert.Equal(expected: revisionBefore, actual: lattice.Revision);
        Assert.Equal(expected: cellsBefore, actual: lattice.Capture().Raw.Select(selector: static field => field.ToArray()).ToArray());
        Assert.Same(expected: solidsBefore, actual: solidsField.GetValue(obj: fixture.Server));
    }
    [Fact]
    public void ValidatorRefusesHeightGeometryThatCannotFitOneRenderBrick() {
        var tooWide = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: Fields(width: (WorldFieldCapacity.MaxSurfaceCells + 1), heightScale: 1f));
        var tooTall = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: Fields(layers: 2, heightScale: 64f));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: tooWide, neighbours: null, reason: out var wideReason));
        Assert.Contains(actualString: wideReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "render brick");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: tooTall, neighbours: null, reason: out var tallReason));
        Assert.Contains(actualString: tallReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "across 2 layers");
    }
    [Fact]
    public void ValidatorRefusesValuesThatCollapseOrChangeMeaningAtTheFixedPointBoundary() {
        var tinyCell = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: Fields(cellSize: 0.000001f));
        var invalidComparison = Fixtures.WithLattice(definition: Fixtures.BuildDocument(), composite: Fields(reactions: [
            new WorldReaction.Transform(
                When: [new WorldFieldCondition(Comparison: ((ActionStateComparison)byte.MaxValue), Field: "heat", Value: 0f)],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Set, Value: 1f)]
            ),
        ]));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: tinyCell, neighbours: null, reason: out var cellReason));
        Assert.Contains(actualString: cellReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "quantize to a positive Q48.16");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: invalidComparison, neighbours: null, reason: out var comparisonReason));
        Assert.Contains(actualString: comparisonReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "comparison");
    }
}
