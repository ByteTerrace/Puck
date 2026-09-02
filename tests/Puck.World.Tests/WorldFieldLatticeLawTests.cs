using System.Reflection;
using Xunit;

using Puck.Assets.Documents;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Pins the field lattice's authoritative state, wire-delta, validation, and checkpoint boundaries.</summary>
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
    private static WorldFieldsSection FilledFields(WorldLatticeFill fill, int width = 32, int depth = 32) => new(
        Lattice: new WorldFieldLatticeDefinition(
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: width,
            Depth: depth,
            Layers: 1,
            StepEveryTicks: 1
        ),
        Fields: [new WorldFieldRow(Name: "grass", Min: 0f, Max: 1f)],
        Paint: [(fill with { Field = "grass" })]
    );
    private static WorldFieldsSection MediumFields(float initial, float heightScale = 5f, int width = 4, int depth = 4) => new(
        Lattice: new WorldFieldLatticeDefinition(
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: width,
            Depth: depth,
            Layers: 1,
            StepEveryTicks: 1
        ),
        Fields: [new WorldFieldRow(Color: "#3B7BD6", HeightScale: heightScale, Initial: initial, Max: 1f, Medium: true, Min: 0f, Name: "medium")]
    );
    private static long SumBits(WorldFieldLattice lattice, int cells) {
        var sum = 0L;

        for (var cell = 0; (cell < cells); cell++) {
            sum = unchecked((sum + (lattice.Value(cell: cell, field: 0).Value * 31L)));
        }

        return sum;
    }

    [Fact]
    public void ANoiseFillIsBitIdenticalAcrossConstructionsAndMovesWithTheWorldSeed() {
        var fill = new WorldLatticeFill.Noise(Frequency: 8, Octaves: 3, Seed: 7u, Threshold: 0.4f, Value: 1f);
        var a = Fixtures.BuildLattice(document: FilledFields(fill: fill), worldSeed: 5UL);
        var b = Fixtures.BuildLattice(document: FilledFields(fill: fill), worldSeed: 5UL);
        var rerolled = Fixtures.BuildLattice(document: FilledFields(fill: fill), worldSeed: 6UL);
        var filled = 0;

        for (var cell = 0; (cell < (32 * 32)); cell++) {
            Assert.Equal(
                actual: b.Value(cell: cell, field: 0).Value,
                expected: a.Value(cell: cell, field: 0).Value
            );

            if (a.Value(cell: cell, field: 0).Value != 0L) {
                filled++;
            }
        }

        // Patchy, not degenerate: some cells filled, some not, and the world seed rerolls the pattern.
        Assert.InRange(actual: filled, high: ((32 * 32) - 1), low: 1);
        Assert.NotEqual(
            actual: SumBits(cells: (32 * 32), lattice: rerolled),
            expected: SumBits(cells: (32 * 32), lattice: a)
        );
    }
    [Fact]
    public void AScatterFillWritesDiscsAndNothingOutsideThem() {
        var fill = new WorldLatticeFill.Scatter(Radius: 2, Seed: 3u, Spacing: 8, Value: 1f);
        var lattice = Fixtures.BuildLattice(document: FilledFields(fill: fill), worldSeed: 1UL);
        var filled = 0;

        for (var cell = 0; (cell < (32 * 32)); cell++) {
            if (lattice.Value(cell: cell, field: 0).Value != 0L) {
                filled++;
            }
        }

        // 16 blocks of one disc each: pi*r^2 ~ 13 cells per disc; discs never merge (radius <= spacing/2), so the
        // count stays within the per-block disc envelope on BOTH sides.
        Assert.InRange(actual: filled, high: (16 * 21), low: 16);
    }
    [Fact]
    public void ARowReferencedReactionScalarModulatesChemistryOnlyWhileTheRowIsNonzero() {
        var lattice = Fixtures.BuildLattice(document: (FilledFields(fill: new WorldLatticeFill.Rect(MaxX: 32f, MaxZ: 32f, MinX: 0f, MinZ: 0f, Value: 1f), width: 4, depth: 4) with {
            Reactions = [new WorldReaction.Transform(
                When: [new WorldFieldCondition(Comparison: WorldFieldComparison.Greater, Field: "grass", Value: 0f)],
                Then: [new WorldFieldWrite(Field: "grass", Op: WorldFieldWriteOp.Add, Value: new WorldLatticeScalar(Row: "season"))]
            )],
        }), state: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "season"), Kind: CellKind.Fixed)]);
        var season = FixedQ4816.Zero;

        void StepOnce() => lattice.Step(
            tick: 1,
            bodyCount: 0,
            host: new Fixtures.LambdaHost(readScalar: (_, _) => season)
        );

        StepOnce();
        Assert.Equal(expected: FixedQ4816.One.Value, actual: lattice.Value(cell: 0, field: 0).Value);

        season = FixedQ4816.FromDouble(value: -0.25);
        StepOnce();
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 0.75).Value, actual: lattice.Value(cell: 0, field: 0).Value);

        season = FixedQ4816.Zero;
        StepOnce();
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 0.75).Value, actual: lattice.Value(cell: 0, field: 0).Value);
    }
    [Fact]
    public void ABodyStandingOnAGroundLatticeSurfaceCouplesToItsColumn() {
        // Layers = 1, cellSize 1, heightScale 2, max 10: the derived coupling ceiling is 1 + 2*10 = 21. A body at
        // y = 1.5 stands ABOVE the one-voxel slab (a bare inside test refuses it) yet ON a plausible surface, so the
        // emit reaction must deposit into its column.
        var lattice = Fixtures.BuildLattice(document: Fields(
            heightScale: 2f,
            reactions: [new WorldReaction.Emit(Amount: 4f, Field: "heat", Tag: "hot")]
        ), state: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "hot"), Kind: CellKind.Int, Capacity: 1)]);

        lattice.Step(
            tick: 1,
            bodyCount: 1,
            host: new Fixtures.LambdaHost(
                bodyPosition: _ => new FixedVector3(
                    X: FixedQ4816.FromDouble(value: 0.5),
                    Y: FixedQ4816.FromDouble(value: 1.5),
                    Z: FixedQ4816.FromDouble(value: 0.5)
                ),
                readTag: (_, _, _) => 1
            )
        );

        Assert.Equal(
            expected: FixedQ4816.FromInteger(value: 4).Value,
            actual: lattice.Value(cell: 0, field: 0).Value
        );
    }
    [Fact]
    public void ABodyAboveTheDerivedCouplingCeilingDoesNotCouple() {
        // Same lattice: ceiling 21. A body at y = 30 flies far above any reachable surface — no deposit.
        var lattice = Fixtures.BuildLattice(document: Fields(
            heightScale: 2f,
            reactions: [new WorldReaction.Emit(Amount: 4f, Field: "heat", Tag: "hot")]
        ), state: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "hot"), Kind: CellKind.Int, Capacity: 1)]);

        lattice.Step(
            tick: 1,
            bodyCount: 1,
            host: new Fixtures.LambdaHost(
                bodyPosition: _ => new FixedVector3(
                    X: FixedQ4816.FromDouble(value: 0.5),
                    Y: FixedQ4816.FromInteger(value: 30),
                    Z: FixedQ4816.FromDouble(value: 0.5)
                ),
                readTag: (_, _, _) => 1
            )
        );

        Assert.Equal(
            expected: 0L,
            actual: lattice.Value(cell: 0, field: 0).Value
        );
    }
    [Fact]
    public void AMediumFieldsSurfaceIsOriginYPlusValueTimesHeightScaleAtTheCoupledCell() {
        var lattice = Fixtures.BuildLattice(document: MediumFields(initial: 1f, heightScale: 5f));
        var position = new FixedVector3(
            X: FixedQ4816.FromDouble(value: 0.5),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: 0.5)
        );

        Assert.Equal(
            expected: FixedQ4816.FromInteger(value: 5),
            actual: lattice.MediumSurface(position: in position)
        );
    }
    [Fact]
    public void ABodyOutsideTheLatticeOrOverAZeroValueCellHasNoMediumSurface() {
        var zeroValued = Fixtures.BuildLattice(document: MediumFields(initial: 0f, heightScale: 5f));
        var outside = Fixtures.BuildLattice(document: MediumFields(initial: 1f, heightScale: 5f));
        var insidePosition = new FixedVector3(
            X: FixedQ4816.FromDouble(value: 0.5),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: 0.5)
        );
        var outsidePosition = new FixedVector3(
            X: FixedQ4816.FromInteger(value: 30),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromInteger(value: 30)
        );

        Assert.Null(@object: zeroValued.MediumSurface(position: in insidePosition));
        Assert.Null(@object: outside.MediumSurface(position: in outsidePosition));
    }
    [Fact]
    public void DiffusionUsesTheCompiledFieldHandleAndSnapshotsBeforeWriting() {
        var lattice = Fixtures.BuildLattice(document: Fields(
            width: 3,
            reactions: [new WorldReaction.Diffuse(Field: "heat", Rate: 1f)]
        ));

        lattice.Restore(checkpoint: new WorldFieldLattice.WorldFieldCheckpoint(Raw: [[
            FixedQ4816.Zero.Value,
            FixedQ4816.FromInteger(value: 3).Value,
            FixedQ4816.Zero.Value,
        ]]));

        Fixtures.StepLattice(lattice: lattice);

        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3), actual: lattice.Value(cell: 0, field: 0));
        Assert.Equal(expected: FixedQ4816.Zero, actual: lattice.Value(cell: 1, field: 0));
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3), actual: lattice.Value(cell: 2, field: 0));
    }
    [Fact]
    public void ExposureWritesTheCompiledStateHandle() {
        var document = Fields(reactions: [new WorldReaction.Expose(
            Comparison: WorldFieldComparison.Greater,
            Field: "heat",
            Row: "exposed",
            Value: 1f
        )]);
        var lattice = Fixtures.BuildLattice(
            document: document,
            state: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "exposed"), Kind: CellKind.Int, Capacity: 1)]
        );

        lattice.Restore(checkpoint: new WorldFieldLattice.WorldFieldCheckpoint(Raw: [[FixedQ4816.FromInteger(value: 2).Value]]));
        var expose = Assert.IsType<WorldFieldNode.Expose>(@object: Assert.Single(collection: lattice.Program.Nodes));
        WorldStateHandle written = default;
        var value = -1L;

        lattice.Step(
            tick: 1UL,
            bodyCount: 1,
            host: new Fixtures.LambdaHost(
                bodyPosition: static _ => new FixedVector3(X: FixedQ4816.FromDouble(value: 0.5), Y: FixedQ4816.FromDouble(value: 0.5), Z: FixedQ4816.FromDouble(value: 0.5)),
                writeTag: (row, _, next, _) => {
                    written = row;
                    value = next;
                }
            )
        );

        Assert.Equal(expected: expose.Row, actual: written);
        Assert.Equal(actual: value, expected: 1L);
    }
    [Fact]
    public void MultipleWritesToOneCell_DeliverOneFinalDelta() {
        var lattice = Fixtures.BuildLattice(document: Fields(reactions: [
            new WorldReaction.Transform(
                When: [],
                Then: [
                    new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 1f),
                    new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 2f),
                ]
            ),
        ]));

        _ = lattice.TakeDeltas(full: false, isFull: out _);
        Fixtures.StepLattice(lattice: lattice);

        var deltas = lattice.TakeDeltas(full: false, isFull: out var full);

        Assert.False(condition: full);
        var delta = Assert.Single(collection: deltas);

        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3).Value, actual: delta.Raw);
    }
    [Fact]
    public void PrimerFullTake_DoesNotConsumeSharedIncrementalDeltas() {
        var lattice = Fixtures.BuildLattice(document: Fields(reactions: [
            new WorldReaction.Transform(
                When: [],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 1f)]
            ),
        ]));

        _ = lattice.TakeDeltas(full: false, isFull: out _);
        Fixtures.StepLattice(lattice: lattice);

        var primer = lattice.TakeDeltas(full: true, isFull: out var primerFull);
        var incremental = lattice.TakeDeltas(full: false, isFull: out var incrementalFull);

        Assert.True(condition: primerFull);
        Assert.Single(collection: primer);
        Assert.False(condition: incrementalFull);
        Assert.Single(collection: incremental);
    }
    [Fact]
    public void ConstructorRefusesAProgramCompiledFromDifferentReactions() {
        var document = Fields(reactions: [new WorldReaction.Decay(Field: "heat", Rate: 0.25f)]);
        var other = Fields(reactions: [new WorldReaction.Decay(Field: "heat", Rate: 0.5f)]);
        var state = WorldFieldsSection.ToStateSection(composite: other);
        var program = WorldFieldProgram.Compile(
            document: other,
            state: WorldStateCatalog.Compile(section: state)
        );

        Assert.Throws<ArgumentException>(testCode: () => new WorldFieldLattice(
            document: document,
            program: program
        ));
    }
    [Fact]
    public void CompatibleReactionReplacementPreservesCellsAndExecutesTheNewPlanInDocumentOrder() {
        var original = Fields(reactions: [new WorldReaction.Transform(
            When: [],
            Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 1f)]
        )]);
        var lattice = Fixtures.BuildLattice(document: original);

        _ = lattice.TakeDeltas(full: false, isFull: out _);
        Fixtures.StepLattice(lattice: lattice);
        var preservedRevision = lattice.Revision;
        var preservedRaw = lattice.Capture().Raw[0][0];
        var replacement = Fields(reactions: [
            new WorldReaction.Transform(
                When: [],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 2f)]
            ),
            new WorldReaction.Decay(Field: "heat", Rate: 0.5f),
        ]);
        var state = WorldFieldsSection.ToStateSection(composite: replacement);
        var program = WorldFieldProgram.Compile(
            document: replacement,
            state: WorldStateCatalog.Compile(section: state)
        );

        Assert.True(condition: lattice.CanInstallProgram(document: replacement, program: program, reason: out var reason), userMessage: reason);
        lattice.InstallProgram(document: replacement, program: program);

        Assert.Equal(expected: preservedRaw, actual: lattice.Capture().Raw[0][0]);
        Assert.Equal(expected: preservedRevision, actual: lattice.Revision);
        Assert.Same(expected: program, actual: lattice.Program);

        Fixtures.StepLattice(lattice: lattice);

        // Document order is add two, then decay by half: (1 + 2) / 2 = 1.5.
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 1.5), actual: lattice.Value(cell: 0, field: 0));
    }
    [Fact]
    public void LiveFieldEnvelopeChangesRefuseInsteadOfMigratingCells() {
        var lattice = Fixtures.BuildLattice(document: Fields());
        var liveProgram = lattice.Program;
        var incompatible = Fields() with {
            Fields = [new WorldFieldRow(Name: "heat", Min: 0f, Max: 20f)],
        };
        var state = WorldFieldsSection.ToStateSection(composite: incompatible);
        var program = WorldFieldProgram.Compile(
            document: incompatible,
            state: WorldStateCatalog.Compile(section: state)
        );

        Assert.False(condition: lattice.CanInstallProgram(document: incompatible, program: program, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "restart the host");
        Assert.Same(expected: liveProgram, actual: lattice.Program);
    }
    [Fact]
    public void ReadBackNamesTheCompiledExecutionAndDependencyPlan() {
        var lattice = Fixtures.BuildLattice(document: Fields(reactions: [
            new WorldReaction.Transform(
                When: [],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 1f)]
            ),
            new WorldReaction.Decay(Field: "heat", Rate: 0.5f),
        ]));

        var readBack = lattice.Describe();

        Assert.Contains(actualString: readBack, comparisonType: StringComparison.Ordinal, expectedSubstring: "plan nodes=2");
        Assert.Contains(actualString: readBack, comparisonType: StringComparison.Ordinal, expectedSubstring: "order=[0:transform,1:decay]");
        Assert.Contains(actualString: readBack, comparisonType: StringComparison.Ordinal, expectedSubstring: "dependencies=[0>1]");
    }
    [Fact]
    public void BudgetCostUsesCompilerPassClassesInsteadOfTreatingEveryNodeAsOneCellPass() {
        var document = Fields(width: 2, reactions: [
            new WorldReaction.Diffuse(Field: "heat", Rate: 0.5f),
            new WorldReaction.Transform(
                When: [],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 1f)]
            ),
            new WorldReaction.Expose(Comparison: WorldFieldComparison.Greater, Field: "heat", Row: "exposed", Value: 1f),
        ]);
        var lattice = Fixtures.BuildLattice(
            document: document,
            state: [new WorldStateRow(Name: WorldCellName.Parse(candidate: "exposed"), Kind: CellKind.Int, Capacity: 8)]
        );

        var cost = lattice.DescribeCost(activeBodyCount: 3, bodyCapacity: 8);

        Assert.Contains(actualString: cost, comparisonType: StringComparison.Ordinal, expectedSubstring: "3 node(s) every 1 tick(s)");
        Assert.Contains(actualString: cost, comparisonType: StringComparison.Ordinal, expectedSubstring: "2 cell(s) x 3 pass(es) = 6 cell visit(s)");
        Assert.Contains(actualString: cost, comparisonType: StringComparison.Ordinal, expectedSubstring: "bodies 3/8 active/capacity x 1 pass(es) = 8 slot visit(s)");
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
        var lattice = Assert.IsType<WorldFieldLattice>(@object: population.Fields);

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
        var lattice = Assert.IsType<WorldFieldLattice>(@object: population.Fields);
        var program = lattice.Program;
        var revision = population.Revision;
        var seats = population.LocalSeatCount;

        Assert.Throws<InvalidOperationException>(testCode: () => population.Rebuild(
            definition: incompatible,
            solids: null
        ));

        Assert.Same(expected: lattice, actual: population.Fields);
        Assert.Same(expected: program, actual: lattice.Program);
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
            Row: new WorldStateRow(Name: WorldCellName.Parse(candidate: "probe"), Kind: CellKind.Int)
        ));
        fixture.Step();

        var lattice = Assert.IsType<WorldFieldLattice>(@object: fixture.Server.Population.Fields);
        var definitionBefore = fixture.DefinitionBytes();
        var programBefore = lattice.Program;
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
        Assert.Same(expected: programBefore, actual: lattice.Program);
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
                When: [new WorldFieldCondition(Comparison: ((WorldFieldComparison)byte.MaxValue), Field: "heat", Value: 0f)],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Set, Value: 1f)]
            ),
        ]));

        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: tinyCell, neighbours: null, reason: out var cellReason));
        Assert.Contains(actualString: cellReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "quantize to a positive Q48.16");
        Assert.False(condition: WorldDefinitionValidator.TryValidate(definition: invalidComparison, neighbours: null, reason: out var comparisonReason));
        Assert.Contains(actualString: comparisonReason, comparisonType: StringComparison.Ordinal, expectedSubstring: "comparison");
    }
    [Fact]
    public void RestoreRefusesCellValuesOutsideTheAuthoredRangeBeforeWritingAnything() {
        var lattice = Fixtures.BuildLattice(document: Fields());
        var invalid = new WorldFieldLattice.WorldFieldCheckpoint(Raw: [[FixedQ4816.FromInteger(value: 11).Value]]);

        Assert.Throws<InvalidOperationException>(testCode: () => lattice.Restore(checkpoint: invalid));
        Assert.Equal(expected: FixedQ4816.Zero, actual: lattice.Value(cell: 0, field: 0));
    }
}
