using Puck.Assets.Documents;
using Puck.Maths;

using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Proves the field compiler's typed handles, dependencies, order, and cost classes.</summary>
public sealed class WorldFieldProgramLawTests {
    [Fact]
    public void Compile_LowersOrderedReactionsToTypedNodesAndCanonicalDependencies() {
        var state = BuildState();
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));
        var catalog = WorldStateCatalog.Compile(section: state);

        var program = WorldFieldProgram.Compile(
            document: fields,
            state: catalog
        );

        Assert.Equal(expected: 4, actual: program.Nodes.Count);
        Assert.Equal(expected: 2, actual: program.CellNodeCount);
        Assert.Equal(expected: 3, actual: program.CellPassCount);
        Assert.Equal(expected: 2, actual: program.BodyPassCount);
        Assert.Equal(expected: 12, actual: program.CellCount);
        Assert.Equal(expected: 6, actual: program.Dependencies.Count);
        Assert.Equal(
            expected: [(0, 1), (0, 2), (1, 2), (0, 3), (1, 3), (2, 3)],
            actual: program.Dependencies.Select(selector: static dependency => (dependency.Before.Ordinal, dependency.After.Ordinal))
        );
        Assert.Collection(
            program.Fields,
            field => {
                Assert.Equal(expected: 0, actual: field.Handle.Ordinal);
                Assert.Equal(expected: "heat", actual: field.Name);
                Assert.Equal(expected: FixedQ4816.FromInteger(value: 10), actual: field.Maximum);
                Assert.Equal(expected: WorldStateStorageShape.Lattice, actual: catalog[field.State].Storage);
            }
        );

        var diffuse = Assert.IsType<WorldFieldNode.Diffuse>(@object: program.Nodes[0]);

        Assert.Equal(expected: 0, actual: diffuse.Handle.Ordinal);
        Assert.Equal(expected: 0, actual: diffuse.Field.Ordinal);
        Assert.True(condition: diffuse.Rate.IsState);
        Assert.Equal(expected: "season", actual: catalog[diffuse.Rate.State].Name);
        Assert.Same(expected: catalog, actual: program.StateCatalog);

        var transform = Assert.IsType<WorldFieldNode.Transform>(@object: program.Nodes[1]);

        Assert.Equal(expected: [0], actual: transform.FieldReads.Select(selector: static handle => handle.Ordinal));
        Assert.Equal(expected: [0], actual: transform.FieldWrites.Select(selector: static handle => handle.Ordinal));
        Assert.Single(collection: transform.StateReads);
        Assert.Equal(expected: "season", actual: catalog[transform.StateReads[0]].Name);

        var emit = Assert.IsType<WorldFieldNode.Emit>(@object: program.Nodes[2]);

        Assert.Equal(expected: WorldFieldWorkKind.Bodies, actual: emit.Work);
        Assert.Equal(expected: [0], actual: emit.FieldReads.Select(selector: static handle => handle.Ordinal));
        Assert.Equal(expected: [0], actual: emit.FieldWrites.Select(selector: static handle => handle.Ordinal));
        Assert.Equal(expected: "burning", actual: catalog[emit.Tag].Name);
        Assert.Equal(expected: ["season", "burning"], actual: emit.StateReads.Select(selector: handle => catalog[handle].Name));

        var expose = Assert.IsType<WorldFieldNode.Expose>(@object: program.Nodes[3]);

        Assert.Empty(collection: expose.StateReads);
        Assert.Single(collection: expose.StateWrites);
        Assert.Equal(expected: "exposed", actual: catalog[expose.Row].Name);
    }
    [Fact]
    public void Compile_QuantizesLiteralsOnceAtTheCompilerBoundary() {
        var state = BuildState(
            reactions: [new WorldReaction.Decay(Field: "heat", Rate: 0.125f)]
        );
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));

        var program = WorldFieldProgram.Compile(document: fields, state: WorldStateCatalog.Compile(section: state));
        var decay = Assert.IsType<WorldFieldNode.Decay>(@object: Assert.Single(collection: program.Nodes));

        Assert.False(condition: decay.Rate.IsState);
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 0.125), actual: decay.Rate.Literal);
    }
    [Fact]
    public void WorldDefinition_FieldProgram_RecompilesWhenAWithExpressionReplacesState() {
        var original = new WorldDefinition(StateRaw: BuildState());
        var originalFields = Assert.IsType<WorldFieldsSection>(@object: original.Fields);

        Assert.NotNull(@object: originalFields.Reactions);
        var originalReactions = originalFields.Reactions!;
        var originalProgram = Assert.IsType<WorldFieldProgram>(@object: original.FieldProgram);
        var originalField = Assert.Single(collection: originalProgram.Fields).Handle;

        Assert.Equal(expected: 4, actual: originalReactions.Count);
        Assert.Equal(expected: 4, actual: originalProgram.Nodes.Count);

        var replacementState = BuildState(
            fieldName: "cold",
            reactions: [new WorldReaction.Decay(Field: "cold", Rate: 0.25f)]
        );
        var replaced = original with { StateRaw = replacementState };
        var replacementFields = Assert.IsType<WorldFieldsSection>(@object: replaced.Fields);
        var replacementProgram = Assert.IsType<WorldFieldProgram>(@object: replaced.FieldProgram);

        Assert.NotNull(@object: replacementFields.Reactions);
        var replacementReactions = replacementFields.Reactions!;

        Assert.Equal(expected: "cold", actual: Assert.Single(collection: replacementFields.Fields).Name);
        Assert.IsType<WorldReaction.Decay>(@object: Assert.Single(collection: replacementReactions));
        Assert.Single(collection: replacementProgram.Nodes);
        Assert.Equal(expected: "cold", actual: Assert.Single(collection: replacementProgram.Fields).Name);
        Assert.IsType<WorldFieldNode.Decay>(@object: replacementProgram.Nodes[0]);
        Assert.NotSame(actual: replacementProgram, expected: originalProgram);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => replacementProgram[originalField]);
    }
    [Fact]
    public void WorldDefinition_ValueOnlyStateUpdatePreservesCompiledFieldsCatalogProgramAndHandles() {
        var original = new WorldDefinition(StateRaw: BuildState());
        var fields = Assert.IsType<WorldFieldsSection>(@object: original.Fields);
        var catalog = original.StateCatalog;
        var program = Assert.IsType<WorldFieldProgram>(@object: original.FieldProgram);

        Assert.True(condition: catalog.TryResolve(handle: out var season, lane: WorldStateOwnershipLane.World, name: "season"));

        var rows = original.StateRaw!.World!.Select(selector: row => (
            string.Equals(a: row.Name, b: "season", comparisonType: StringComparison.Ordinal)
                ? row with { Cells = [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 123L)] }
                : row
        )).ToArray();
        var updated = original.WithWorldState(rows: rows);

        Assert.Same(expected: fields, actual: updated.Fields);
        Assert.Same(expected: catalog, actual: updated.StateCatalog);
        Assert.Same(expected: program, actual: updated.FieldProgram);
        Assert.Equal(expected: "season", actual: updated.StateCatalog[season].Name);

        var unrelatedSectionEdit = original with { DefaultSeatKitRaw = "builder" };

        Assert.Same(expected: fields, actual: unrelatedSectionEdit.Fields);
        Assert.Same(expected: catalog, actual: unrelatedSectionEdit.StateCatalog);
        Assert.Same(expected: program, actual: unrelatedSectionEdit.FieldProgram);

        var chainedEdit = updated with { DefaultSeatKitRaw = "builder" };

        Assert.Same(expected: fields, actual: chainedEdit.Fields);
        Assert.Same(expected: catalog, actual: chainedEdit.StateCatalog);
        Assert.Same(expected: program, actual: chainedEdit.FieldProgram);
    }
    [Fact]
    public void CompiledViewReadsDoNotChangeWorldDefinitionEqualityOrHashCodes() {
        var state = BuildState();
        var left = new WorldDefinition(StateRaw: state);
        var right = new WorldDefinition(StateRaw: state);
        var expectedHash = left.GetHashCode();

        Assert.Equal(actual: right, expected: left);
        _ = left.Fields;
        _ = left.StateCatalog;
        _ = left.FieldProgram;

        Assert.Equal(expected: expectedHash, actual: left.GetHashCode());
        Assert.Equal(actual: right, expected: left);
        Assert.Equal(expected: right.GetHashCode(), actual: left.GetHashCode());

        _ = right.Fields;
        _ = right.StateCatalog;
        _ = right.FieldProgram;

        Assert.Equal(actual: right, expected: left);
        Assert.Equal(expected: expectedHash, actual: right.GetHashCode());
    }
    [Fact]
    public void WarmCompiledViewReadsAllocateNothing() {
        var definition = new WorldDefinition(StateRaw: BuildState());
        var fields = Assert.IsType<WorldFieldsSection>(@object: definition.Fields);
        var catalog = definition.StateCatalog;
        var program = Assert.IsType<WorldFieldProgram>(@object: definition.FieldProgram);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; (index < 64); index++) {
            if (
                !ReferenceEquals(objA: fields, objB: definition.Fields) ||
                !ReferenceEquals(objA: catalog, objB: definition.StateCatalog) ||
                !ReferenceEquals(objA: program, objB: definition.FieldProgram)
            ) {
                throw new InvalidOperationException(message: "A warm compatible compiled view changed identity.");
            }
        }

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - before);

        Assert.Equal(actual: allocated, expected: 0L);
    }
    [Fact]
    public void Compile_AdditiveTransformDeclaresItsTargetAsAReadAndWrite() {
        var state = BuildState(
            reactions: [new WorldReaction.Transform(
                When: [],
                Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Add, Value: 1f)]
            )]
        );
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));

        var transform = Assert.IsType<WorldFieldNode.Transform>(@object: Assert.Single(
            collection: WorldFieldProgram.Compile(document: fields, state: WorldStateCatalog.Compile(section: state)).Nodes
        ));

        Assert.Equal(expected: [0], actual: transform.FieldReads.Select(selector: static handle => handle.Ordinal));
        Assert.Equal(expected: [0], actual: transform.FieldWrites.Select(selector: static handle => handle.Ordinal));
    }
    [Fact]
    public void Compile_EmissionDeclaresItsTargetAsAReadAndWrite() {
        var state = BuildState(
            reactions: [new WorldReaction.Emit(Amount: 1f, Field: "heat", Tag: "burning")]
        );
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));

        var emit = Assert.IsType<WorldFieldNode.Emit>(@object: Assert.Single(
            collection: WorldFieldProgram.Compile(document: fields, state: WorldStateCatalog.Compile(section: state)).Nodes
        ));

        Assert.Equal(expected: [0], actual: emit.FieldReads.Select(selector: static handle => handle.Ordinal));
        Assert.Equal(expected: [0], actual: emit.FieldWrites.Select(selector: static handle => handle.Ordinal));
    }
    [Fact]
    public void Compile_NodeCollectionsAreImmutableAfterDependencyAnalysis() {
        var state = BuildState();
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));
        var program = WorldFieldProgram.Compile(document: fields, state: WorldStateCatalog.Compile(section: state));
        var transform = Assert.IsType<WorldFieldNode.Transform>(@object: program.Nodes[1]);
        var originalWrite = transform.FieldWrites[0];
        var alteredWrites = transform.FieldWrites.SetItem(index: 0, item: default);

        Assert.True(condition: originalWrite.IsValid);
        Assert.False(condition: alteredWrites[0].IsValid);
        Assert.Equal(expected: originalWrite, actual: transform.FieldWrites[0]);
        Assert.Equal(expected: 6, actual: program.Dependencies.Count);
    }
    [Fact]
    public void Compile_StateWriteToReadCreatesAnEdgeBetweenOtherwiseIndependentFields() {
        var state = BuildState(reactions: [
            new WorldReaction.Expose(Comparison: WorldFieldComparison.Greater, Field: "heat", Row: "burning", Value: 1f),
            new WorldReaction.Emit(Amount: 1f, Field: "cold", Tag: "burning"),
        ]);

        state = state with {
            World = [
                .. state.World!,
                new WorldStateRow(
                    Name: WorldCellName.Parse(candidate: "cold"),
                    Kind: CellKind.Fixed,
                    Lattice: new WorldStateLatticeTrait(Topology: "ground")
                ),
            ],
        };
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));

        var program = WorldFieldProgram.Compile(document: fields, state: WorldStateCatalog.Compile(section: state));
        var dependency = Assert.Single(collection: program.Dependencies);

        Assert.Equal(expected: 0, actual: dependency.Before.Ordinal);
        Assert.Equal(expected: 1, actual: dependency.After.Ordinal);
    }
    [Fact]
    public void Compile_SetOnlyTransformWithoutConditionsDoesNotInventAFieldRead() {
        var state = BuildState(reactions: [new WorldReaction.Transform(
            When: [],
            Then: [new WorldFieldWrite(Field: "heat", Op: WorldFieldWriteOp.Set, Value: 1f)]
        )]);
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));

        var transform = Assert.IsType<WorldFieldNode.Transform>(@object: Assert.Single(
            collection: WorldFieldProgram.Compile(document: fields, state: WorldStateCatalog.Compile(section: state)).Nodes
        ));

        Assert.Empty(collection: transform.FieldReads);
        Assert.Equal(expected: [0], actual: transform.FieldWrites.Select(selector: static handle => handle.Ordinal));
    }
    [Fact]
    public void WorldDefinition_PaintAndColorOnlyChangesPreserveProgramButEnvelopeChangesReplaceIt() {
        var original = new WorldDefinition(StateRaw: BuildState());
        var originalFields = Assert.IsType<WorldFieldsSection>(@object: original.Fields);
        var originalProgram = Assert.IsType<WorldFieldProgram>(@object: original.FieldProgram);
        var originalHandle = Assert.Single(collection: originalProgram.Fields).Handle;
        var cosmeticRows = original.StateRaw!.World!.Select(selector: row => (
            string.Equals(a: row.Name, b: "heat", comparisonType: StringComparison.Ordinal)
                ? row with {
                    Lattice = row.Lattice! with {
                        Color = "#336699",
                        Paint = [new WorldLatticeFill.Rect(MaxX: 1f, MaxZ: 1f, MinX: 0f, MinZ: 0f, Value: 1f)],
                    },
                }
                : row
        )).ToArray();
        var cosmetic = original.WithWorldState(rows: cosmeticRows);

        Assert.NotSame(expected: originalFields, actual: cosmetic.Fields);
        Assert.Same(expected: originalProgram, actual: cosmetic.FieldProgram);
        Assert.Equal(expected: "heat", actual: cosmetic.FieldProgram![originalHandle].Name);

        var envelopeRows = cosmetic.StateRaw!.World!.Select(selector: row => (
            string.Equals(a: row.Name, b: "heat", comparisonType: StringComparison.Ordinal)
                ? row with { Lattice = row.Lattice! with { Initial = 2f } }
                : row
        )).ToArray();
        var envelope = cosmetic.WithWorldState(rows: envelopeRows);
        var replacementProgram = Assert.IsType<WorldFieldProgram>(@object: envelope.FieldProgram);

        Assert.NotSame(actual: replacementProgram, expected: originalProgram);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => replacementProgram[originalHandle]);
    }
    [Fact]
    public void WorldDefinition_InPlaceReactionArrayMutationCannotLeaveAStaleProgram() {
        WorldReaction[] reactions = [new WorldReaction.Decay(Field: "heat", Rate: 0.25f)];
        var state = BuildState(reactions: reactions);
        var definition = new WorldDefinition(StateRaw: state);
        var original = Assert.IsType<WorldFieldProgram>(@object: definition.FieldProgram);
        var originalHandle = Assert.Single(collection: original.Fields).Handle;

        reactions[0] = new WorldReaction.Diffuse(Field: "heat", Rate: 0.5f);
        var refreshed = Assert.IsType<WorldFieldProgram>(@object: definition.FieldProgram);

        Assert.NotSame(actual: refreshed, expected: original);
        Assert.IsType<WorldFieldNode.Diffuse>(@object: Assert.Single(collection: refreshed.Nodes));
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => refreshed[originalHandle]);
    }
    [Fact]
    public void Compile_DoesNotInventAnOrderingEdgeBetweenIndependentBodyOutputs() {
        var state = BuildState(
            reactions: [
                new WorldReaction.Expose(Comparison: WorldFieldComparison.Greater, Field: "heat", Row: "burning", Value: 1f),
                new WorldReaction.Expose(Comparison: WorldFieldComparison.Greater, Field: "heat", Row: "exposed", Value: 2f),
            ]
        );
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));

        var program = WorldFieldProgram.Compile(document: fields, state: WorldStateCatalog.Compile(section: state));

        Assert.Empty(collection: program.Dependencies);
    }
    [Fact]
    public void AFieldHandleFromAnotherProgramIsRejectedEvenWhenItsOrdinalFits() {
        var state = BuildState();
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));
        var catalog = WorldStateCatalog.Compile(section: state);
        var first = WorldFieldProgram.Compile(document: fields, state: catalog);
        var second = WorldFieldProgram.Compile(document: fields, state: catalog);
        var firstHeat = Assert.Single(collection: first.Fields).Handle;

        Assert.Equal(expected: 0, actual: firstHeat.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(testCode: () => second[firstHeat]);
    }
    [Fact]
    public void Compile_RefusesAStateDependencyWithTheWrongShapeOrKind() {
        var state = BuildState(
            season: new WorldStateRow(
                Name: WorldCellName.Parse(candidate: "season"),
                Kind: CellKind.Int,
                Capacity: 4
            ),
            reactions: [new WorldReaction.Decay(
                Field: "heat",
                Rate: new WorldLatticeScalar(Row: "season")
            )]
        );
        var fields = Assert.IsType<WorldFieldsSection>(@object: WorldFieldsSection.Compile(state: state));

        var exception = Assert.Throws<InvalidOperationException>(testCode: () => WorldFieldProgram.Compile(
            document: fields,
            state: WorldStateCatalog.Compile(section: state)
        ));

        Assert.Contains(
            expectedSubstring: "requires a Slot Fixed world state row",
            actualString: exception.Message,
            comparisonType: StringComparison.Ordinal
        );
    }

    private static WorldStateSection BuildState(
        WorldStateRow? season = null,
        IReadOnlyList<WorldReaction>? reactions = null,
        string fieldName = "heat"
    ) => new(
        Lattices: [new WorldStateLatticeTopology.Field(
            Name: "ground",
            Origin: new DocumentVector3(x: 0f, y: 0f, z: 0f),
            CellSize: 1f,
            Width: 4,
            Depth: 3,
            Reactions: (reactions ?? [
                new WorldReaction.Diffuse(Field: fieldName, Rate: new WorldLatticeScalar(Row: "season")),
                new WorldReaction.Transform(
                    When: [new WorldFieldCondition(Field: fieldName, Comparison: WorldFieldComparison.Greater, Value: new WorldLatticeScalar(Row: "season"))],
                    Then: [
                        new WorldFieldWrite(Field: fieldName, Op: WorldFieldWriteOp.Add, Value: 1f),
                        new WorldFieldWrite(Field: fieldName, Op: WorldFieldWriteOp.Add, Value: new WorldLatticeScalar(Row: "season")),
                    ]
                ),
                new WorldReaction.Emit(Tag: "burning", Field: fieldName, Amount: new WorldLatticeScalar(Row: "season")),
                new WorldReaction.Expose(Comparison: WorldFieldComparison.Greater, Field: fieldName, Row: "exposed", Value: 5f),
            ])
        )],
        World: [
            new WorldStateRow(
                Name: WorldCellName.Parse(candidate: fieldName),
                Kind: CellKind.Fixed,
                Lattice: new WorldStateLatticeTrait(
                    Topology: "ground",
                    Initial: 0f,
                    Min: 0f,
                    Max: 10f
                )
            ),
            (season ?? new WorldStateRow(
                Name: WorldCellName.Parse(candidate: "season"),
                Kind: CellKind.Fixed,
                Cells: [new WorldStateCell(Key: WorldStateRow.SlotKey, Value: 0L)]
            )),
            new WorldStateRow(
                Name: WorldCellName.Parse(candidate: "burning"),
                Kind: CellKind.Int,
                Capacity: 16
            ),
            new WorldStateRow(
                Name: WorldCellName.Parse(candidate: "exposed"),
                Kind: CellKind.Int,
                Capacity: 16
            ),
        ]
    );
}
