using Puck.Maths;
using Puck.Physics.Fields;
using Puck.State;

namespace Puck.Physics.Tests;

/// <summary>Pins the field lattice kernel's own contract now that it lives beside every other Physics kernel: it
/// builds from a plain <see cref="FieldLatticeInput"/> and reaches body/state-row values only through
/// <see cref="IFieldLatticeHost"/> — no <c>Puck.World</c> or <c>Puck.World.Schema</c> type appears anywhere in this
/// file.</summary>
public sealed class FieldLatticeLawTests {
    private static StateHandle Row(string name, CellKind kind = CellKind.Fixed) {
        var catalog = StateCatalog.Compile(section: new StateSection(Rows: [new StateRow(Name: CellName.Parse(candidate: name), Kind: kind)]));

        _ = catalog.TryResolve(lane: StateLane.Document, name: name, handle: out var handle);

        return handle;
    }
    private static FieldLatticeInput Fields(
        int width = 1,
        int depth = 1,
        int layers = 1,
        FixedQ4816? cellSize = null,
        FixedQ4816? heightScale = null,
        IReadOnlyList<FieldReactionInput>? reactions = null
    ) => new(
        Lattice: new FieldLatticeTopology(Origin: FixedVector3.Zero, CellSize: (cellSize ?? FixedQ4816.One), Width: width, Depth: depth, Layers: layers, StepEveryTicks: 1),
        Fields: [new FieldDescriptorInput(Name: "heat", Initial: FixedQ4816.Zero, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.FromInteger(value: 10), HeightScale: (heightScale ?? FixedQ4816.Zero), IsMedium: false, Color: null)],
        Reactions: (reactions ?? []),
        Paint: []
    );
    private static FieldLatticeInput FilledFields(FieldFillInput fill, int width = 32, int depth = 32) => new(
        Lattice: new FieldLatticeTopology(Origin: FixedVector3.Zero, CellSize: FixedQ4816.One, Width: width, Depth: depth, Layers: 1, StepEveryTicks: 1),
        Fields: [new FieldDescriptorInput(Name: "grass", Initial: FixedQ4816.Zero, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.One, HeightScale: FixedQ4816.Zero, IsMedium: false, Color: null)],
        Reactions: [],
        Paint: [fill]
    );
    private static FieldLatticeInput MediumFields(FixedQ4816 initial, FixedQ4816 heightScale, int width = 4, int depth = 4) => new(
        Lattice: new FieldLatticeTopology(Origin: FixedVector3.Zero, CellSize: FixedQ4816.One, Width: width, Depth: depth, Layers: 1, StepEveryTicks: 1),
        Fields: [new FieldDescriptorInput(Name: "medium", Initial: initial, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.One, HeightScale: heightScale, IsMedium: true, Color: "#3B7BD6")],
        Reactions: [],
        Paint: []
    );
    private static long SumBits(FieldLattice lattice, int cells) {
        var sum = 0L;

        for (var cell = 0; (cell < cells); cell++) {
            sum = unchecked((sum + (lattice.Value(cell: cell, field: 0).Value * 31L)));
        }

        return sum;
    }

    // The test double for IFieldLatticeHost: every hook defaults to the same no-op/zero Step itself falls back to
    // when a caller omits a delegate.
    private sealed class LambdaHost(
        Func<int, FixedVector3?>? bodyPosition = null,
        Func<StateHandle, int, ulong, long>? readTag = null,
        Action<StateHandle, int, long, ulong>? writeTag = null,
        Func<StateHandle, ulong, FixedQ4816>? readScalar = null,
        Action<StateHandle, FixedQ4816, ulong>? addScalar = null
    ) : IFieldLatticeHost {
        public FixedVector3? BodyPosition(int body) => bodyPosition?.Invoke(body);
        public long ReadTag(StateHandle row, int body, ulong tick) => (readTag?.Invoke(row, body, tick) ?? 0L);
        public void WriteTag(StateHandle row, int body, long value, ulong tick) => writeTag?.Invoke(row, body, value, tick);
        public FixedQ4816 ReadScalar(StateHandle row, ulong tick) => (readScalar?.Invoke(row, tick) ?? FixedQ4816.Zero);
        public void AddScalar(StateHandle row, FixedQ4816 amount, ulong tick) => addScalar?.Invoke(row, amount, tick);
    }
    private static void StepLattice(FieldLattice lattice, IFieldLatticeHost? host = null) => lattice.Step(
        tick: 1,
        bodyCount: 0,
        host: (host ?? new LambdaHost())
    );

    [Fact]
    public void ANoiseFillIsBitIdenticalAcrossConstructionsAndMovesWithTheWorldSeed() {
        var fill = new FieldFillInput.Noise(Field: 0, Value: FixedQ4816.One, Frequency: 8, Threshold: FixedQ4816.FromDouble(value: 0.4), Octaves: 3, Seed: 7u);
        var a = new FieldLattice(input: FilledFields(fill: fill), worldSeed: 5UL);
        var b = new FieldLattice(input: FilledFields(fill: fill), worldSeed: 5UL);
        var rerolled = new FieldLattice(input: FilledFields(fill: fill), worldSeed: 6UL);
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
    public void DrawFill_PreservesAuthoredPaintOrder() {
        var input = new FieldLatticeInput(
            Lattice: new FieldLatticeTopology(Origin: FixedVector3.Zero, CellSize: FixedQ4816.One, Width: 2, Depth: 1, Layers: 1, StepEveryTicks: 1),
            Fields: [new FieldDescriptorInput(Name: "heat", Initial: FixedQ4816.Zero, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.FromInteger(value: 10), HeightScale: FixedQ4816.Zero, IsMedium: false, Color: null)],
            Reactions: [],
            Paint: [
                new FieldFillInput.Rect(0, FixedQ4816.FromInteger(value: 9), FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.One, FixedQ4816.One),
                new FieldFillInput.DrawMarker(0),
                new FieldFillInput.Rect(0, FixedQ4816.FromInteger(value: 7), FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.FromInteger(value: 2), FixedQ4816.One),
            ]
        );
        var lattice = new FieldLattice(input: input);
        var one = FixedQ4816.FromInteger(value: 1).Value;

        lattice.FillFromDraw(field: 0, raw: [one, one], worldSeed: 0UL);

        Assert.Equal(expected: FixedQ4816.FromInteger(value: 1), actual: lattice.Value(field: 0, cell: 0));
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 7), actual: lattice.Value(field: 0, cell: 1));
    }
    [Fact]
    public void AScatterFillWritesDiscsAndNothingOutsideThem() {
        var fill = new FieldFillInput.Scatter(Field: 0, Value: FixedQ4816.One, Spacing: 8, Radius: 2, Seed: 3u);
        var lattice = new FieldLattice(input: FilledFields(fill: fill), worldSeed: 1UL);
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
        var seasonRow = Row(name: "season");
        var input = FilledFields(fill: new FieldFillInput.Rect(0, FixedQ4816.One, FixedQ4816.Zero, FixedQ4816.Zero, FixedQ4816.FromInteger(value: 32), FixedQ4816.FromInteger(value: 32)), width: 4, depth: 4) with {
            Reactions = [new FieldReactionInput.Transform(
                When: [new FieldConditionInput(Field: 0, Comparison: ActionStateComparison.Greater, Value: new FieldScalarInput(Literal: FixedQ4816.Zero, State: default))],
                Then: [new FieldWriteInput(Field: 0, Op: FieldWriteOp.Add, Value: new FieldScalarInput(Literal: default, State: seasonRow))]
            )],
        };
        var lattice = new FieldLattice(input: input);
        var season = FixedQ4816.Zero;

        void StepOnce() => lattice.Step(
            tick: 1,
            bodyCount: 0,
            host: new LambdaHost(readScalar: (_, _) => season)
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
        var hot = Row(name: "hot", kind: CellKind.Int);
        var lattice = new FieldLattice(input: Fields(
            heightScale: FixedQ4816.FromInteger(value: 2),
            reactions: [new FieldReactionInput.Emit(Tag: hot, Field: 0, Amount: new FieldScalarInput(Literal: FixedQ4816.FromInteger(value: 4), State: default))]
        ));

        lattice.Step(
            tick: 1,
            bodyCount: 1,
            host: new LambdaHost(
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
        var hot = Row(name: "hot", kind: CellKind.Int);
        var lattice = new FieldLattice(input: Fields(
            heightScale: FixedQ4816.FromInteger(value: 2),
            reactions: [new FieldReactionInput.Emit(Tag: hot, Field: 0, Amount: new FieldScalarInput(Literal: FixedQ4816.FromInteger(value: 4), State: default))]
        ));

        lattice.Step(
            tick: 1,
            bodyCount: 1,
            host: new LambdaHost(
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
        var lattice = new FieldLattice(input: MediumFields(initial: FixedQ4816.One, heightScale: FixedQ4816.FromInteger(value: 5)));
        var position = new FixedVector3(
            X: FixedQ4816.FromDouble(value: 0.5),
            Y: FixedQ4816.Zero,
            Z: FixedQ4816.FromDouble(value: 0.5)
        );

        var surface = lattice.MediumSurface(position: in position);

        Assert.NotNull(@object: surface);
        Assert.Equal(
            expected: FixedQ4816.FromInteger(value: 5),
            actual: surface!.Value.Point.Y
        );
        Assert.Equal(expected: position.X, actual: surface.Value.Point.X);
        Assert.Equal(expected: position.Z, actual: surface.Value.Point.Z);
    }
    [Fact]
    public void ABodyOutsideTheLatticeOrOverAZeroValueCellHasNoMediumSurface() {
        var zeroValued = new FieldLattice(input: MediumFields(initial: FixedQ4816.Zero, heightScale: FixedQ4816.FromInteger(value: 5)));
        var outside = new FieldLattice(input: MediumFields(initial: FixedQ4816.One, heightScale: FixedQ4816.FromInteger(value: 5)));
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
    public void AMediumFieldAdmitsAPointAboveItsSingleVoxelLayer_WhereTheFreeSurfaceReachesThatHigh() {
        // Layers = 1, cellSize 1: the raw voxel box tops out at Y = 1. The free surface (value 1 * heightScale 5)
        // reaches Y = 5 — a swimmer well inside that depth (Y = 3) is inside the medium; the topology's own layer
        // count says nothing about how tall a medium's surface may rise, the same reasoning the derived coupling
        // ceiling above already applies to reaction coupling.
        var lattice = new FieldLattice(input: MediumFields(initial: FixedQ4816.One, heightScale: FixedQ4816.FromInteger(value: 5)));
        var submerged = new FixedVector3(X: FixedQ4816.FromDouble(value: 1.5), Y: FixedQ4816.FromInteger(value: 3), Z: FixedQ4816.FromDouble(value: 1.5));
        var aboveSurface = new FixedVector3(X: FixedQ4816.FromDouble(value: 1.5), Y: FixedQ4816.FromInteger(value: 6), Z: FixedQ4816.FromDouble(value: 1.5));

        Assert.True(condition: lattice.IsInsideMedium(field: 0, position: in submerged));
        Assert.False(condition: lattice.IsInsideMedium(field: 0, position: in aboveSurface));
    }
    [Fact]
    public void ASegmentEntirelyBelowTheFreeSurfaceProvesInsideTheMedium_WhereOneCrossingTheSurfaceRefuses() {
        // Same lattice as above: surface at Y = 5, raw voxel box only one cellSize (1) tall. A short segment well
        // beneath the surface but above that one voxel must still prove submerged for navigation's medium-domain
        // locomotion check (Puck.Physics.Navigation.NavigationRuntime.Domain.AdmitsLocomotion) to ever admit a
        // swimmer's move.
        var lattice = new FieldLattice(input: MediumFields(initial: FixedQ4816.One, heightScale: FixedQ4816.FromInteger(value: 5)));
        var from = new FixedVector3(X: FixedQ4816.FromDouble(value: 1.5), Y: FixedQ4816.FromInteger(value: 3), Z: FixedQ4816.FromDouble(value: 1.5));
        var to = new FixedVector3(X: FixedQ4816.FromDouble(value: 1.6), Y: FixedQ4816.FromInteger(value: 3), Z: FixedQ4816.FromDouble(value: 1.5));
        var crossing = new FixedVector3(X: FixedQ4816.FromDouble(value: 1.5), Y: FixedQ4816.FromInteger(value: 6), Z: FixedQ4816.FromDouble(value: 1.5));

        Assert.True(condition: lattice.IsSegmentInsideMedium(field: 0, from: in from, to: in to, clearance: FixedQ4816.FromDouble(value: 0.1), maximumSubdivisions: 8));
        Assert.False(condition: lattice.IsSegmentInsideMedium(field: 0, from: in from, to: in crossing, clearance: FixedQ4816.FromDouble(value: 0.1), maximumSubdivisions: 8));
    }
    [Fact]
    public void DiffusionUsesTheCompiledFieldOrdinalAndSnapshotsBeforeWriting() {
        var lattice = new FieldLattice(input: Fields(
            width: 3,
            reactions: [new FieldReactionInput.Diffuse(Field: 0, Rate: new FieldScalarInput(Literal: FixedQ4816.One, State: default))]
        ));

        lattice.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [[
            FixedQ4816.Zero.Value,
            FixedQ4816.FromInteger(value: 3).Value,
            FixedQ4816.Zero.Value,
        ]]));

        StepLattice(lattice: lattice);

        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3), actual: lattice.Value(cell: 0, field: 0));
        Assert.Equal(expected: FixedQ4816.Zero, actual: lattice.Value(cell: 1, field: 0));
        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3), actual: lattice.Value(cell: 2, field: 0));
    }
    [Fact]
    public void ExposureWritesTheCompiledStateHandle() {
        var exposed = Row(name: "exposed", kind: CellKind.Int);
        var lattice = new FieldLattice(input: Fields(reactions: [new FieldReactionInput.Expose(
            Field: 0,
            Comparison: ActionStateComparison.Greater,
            Value: new FieldScalarInput(Literal: FixedQ4816.One, State: default),
            Row: exposed
        )]));

        lattice.Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [[FixedQ4816.FromInteger(value: 2).Value]]));
        var expose = Assert.IsType<FieldReactionInput.Expose>(@object: Assert.Single(collection: lattice.Input.Reactions));
        StateHandle written = default;
        var value = -1L;

        lattice.Step(
            tick: 1UL,
            bodyCount: 1,
            host: new LambdaHost(
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
        var lattice = new FieldLattice(input: Fields(reactions: [
            new FieldReactionInput.Transform(
                When: [],
                Then: [
                    new FieldWriteInput(Field: 0, Op: FieldWriteOp.Add, Value: new FieldScalarInput(Literal: FixedQ4816.One, State: default)),
                    new FieldWriteInput(Field: 0, Op: FieldWriteOp.Add, Value: new FieldScalarInput(Literal: FixedQ4816.FromInteger(value: 2), State: default)),
                ]
            ),
        ]));

        _ = lattice.TakeDeltas(full: false, isFull: out _);
        StepLattice(lattice: lattice);

        var deltas = lattice.TakeDeltas(full: false, isFull: out var full);

        Assert.False(condition: full);
        var delta = Assert.Single(collection: deltas);

        Assert.Equal(expected: FixedQ4816.FromInteger(value: 3).Value, actual: delta.Raw);
    }
    [Fact]
    public void PrimerFullTake_DoesNotConsumeSharedIncrementalDeltas() {
        var lattice = new FieldLattice(input: Fields(reactions: [
            new FieldReactionInput.Transform(
                When: [],
                Then: [new FieldWriteInput(Field: 0, Op: FieldWriteOp.Add, Value: new FieldScalarInput(Literal: FixedQ4816.One, State: default))]
            ),
        ]));

        _ = lattice.TakeDeltas(full: false, isFull: out _);
        StepLattice(lattice: lattice);

        var primer = lattice.TakeDeltas(full: true, isFull: out var primerFull);
        var incremental = lattice.TakeDeltas(full: false, isFull: out var incrementalFull);

        Assert.True(condition: primerFull);
        Assert.Single(collection: primer);
        Assert.False(condition: incrementalFull);
        Assert.Single(collection: incremental);
    }
    [Fact]
    public void CompatibleReactionReplacementPreservesCellsAndExecutesTheNewPlanInDocumentOrder() {
        var original = Fields(reactions: [new FieldReactionInput.Transform(
            When: [],
            Then: [new FieldWriteInput(Field: 0, Op: FieldWriteOp.Add, Value: new FieldScalarInput(Literal: FixedQ4816.One, State: default))]
        )]);
        var lattice = new FieldLattice(input: original);

        _ = lattice.TakeDeltas(full: false, isFull: out _);
        StepLattice(lattice: lattice);
        var preservedRevision = lattice.Revision;
        var preservedRaw = lattice.Capture().Raw[0][0];
        var replacement = Fields(reactions: [
            new FieldReactionInput.Transform(
                When: [],
                Then: [new FieldWriteInput(Field: 0, Op: FieldWriteOp.Add, Value: new FieldScalarInput(Literal: FixedQ4816.FromInteger(value: 2), State: default))]
            ),
            new FieldReactionInput.Decay(Field: 0, Rate: new FieldScalarInput(Literal: FixedQ4816.FromDouble(value: 0.5), State: default)),
        ]);

        Assert.True(condition: lattice.CanInstallInput(input: replacement, reason: out var reason), userMessage: reason);
        lattice.InstallInput(input: replacement);

        Assert.Equal(expected: preservedRaw, actual: lattice.Capture().Raw[0][0]);
        Assert.Equal(expected: preservedRevision, actual: lattice.Revision);
        Assert.Same(expected: replacement, actual: lattice.Input);

        StepLattice(lattice: lattice);

        // Document order is add two, then decay by half: (1 + 2) / 2 = 1.5.
        Assert.Equal(expected: FixedQ4816.FromDouble(value: 1.5), actual: lattice.Value(cell: 0, field: 0));
    }
    [Fact]
    public void LiveFieldEnvelopeChangesRefuseInsteadOfMigratingCells() {
        var lattice = new FieldLattice(input: Fields());
        var liveInput = lattice.Input;
        var incompatible = Fields() with {
            Fields = [new FieldDescriptorInput(Name: "heat", Initial: FixedQ4816.Zero, Minimum: FixedQ4816.Zero, Maximum: FixedQ4816.FromInteger(value: 20), HeightScale: FixedQ4816.Zero, IsMedium: false, Color: null)],
        };

        Assert.False(condition: lattice.CanInstallInput(input: incompatible, reason: out var reason));
        Assert.Contains(actualString: reason, comparisonType: StringComparison.Ordinal, expectedSubstring: "restart the host");
        Assert.Same(expected: liveInput, actual: lattice.Input);
    }
    [Fact]
    public void ReadBackNamesTheCompiledExecutionAndDependencyPlan() {
        var lattice = new FieldLattice(input: Fields(reactions: [
            new FieldReactionInput.Transform(
                When: [],
                Then: [new FieldWriteInput(Field: 0, Op: FieldWriteOp.Add, Value: new FieldScalarInput(Literal: FixedQ4816.One, State: default))]
            ),
            new FieldReactionInput.Decay(Field: 0, Rate: new FieldScalarInput(Literal: FixedQ4816.FromDouble(value: 0.5), State: default)),
        ]));

        var readBack = lattice.Describe();

        Assert.Contains(actualString: readBack, comparisonType: StringComparison.Ordinal, expectedSubstring: "plan nodes=2");
        Assert.Contains(actualString: readBack, comparisonType: StringComparison.Ordinal, expectedSubstring: "order=[0:transform,1:decay]");
        Assert.Contains(actualString: readBack, comparisonType: StringComparison.Ordinal, expectedSubstring: "dependencies=[0>1]");
    }
    [Fact]
    public void BudgetCostUsesCompilerPassClassesInsteadOfTreatingEveryNodeAsOneCellPass() {
        var exposed = Row(name: "exposed", kind: CellKind.Int);
        var lattice = new FieldLattice(input: Fields(width: 2, reactions: [
            new FieldReactionInput.Diffuse(Field: 0, Rate: new FieldScalarInput(Literal: FixedQ4816.FromDouble(value: 0.5), State: default)),
            new FieldReactionInput.Transform(
                When: [],
                Then: [new FieldWriteInput(Field: 0, Op: FieldWriteOp.Add, Value: new FieldScalarInput(Literal: FixedQ4816.One, State: default))]
            ),
            new FieldReactionInput.Expose(Field: 0, Comparison: ActionStateComparison.Greater, Value: new FieldScalarInput(Literal: FixedQ4816.One, State: default), Row: exposed),
        ]));

        var cost = lattice.DescribeCost(activeBodyCount: 3, bodyCapacity: 8);

        Assert.Contains(actualString: cost, comparisonType: StringComparison.Ordinal, expectedSubstring: "3 node(s) every 1 tick(s)");
        Assert.Contains(actualString: cost, comparisonType: StringComparison.Ordinal, expectedSubstring: "2 cell(s) x 3 pass(es) = 6 cell visit(s)");
        Assert.Contains(actualString: cost, comparisonType: StringComparison.Ordinal, expectedSubstring: "bodies 3/8 active/capacity x 1 pass(es) = 8 slot visit(s)");
    }
    [Fact]
    public void RestoreRefusesCellValuesOutsideTheAuthoredRangeBeforeWritingAnything() {
        var lattice = new FieldLattice(input: Fields());
        var invalid = new FieldLattice.Checkpoint(Raw: [[FixedQ4816.FromInteger(value: 11).Value]]);

        Assert.Throws<InvalidOperationException>(testCode: () => lattice.Restore(checkpoint: invalid));
        Assert.Equal(expected: FixedQ4816.Zero, actual: lattice.Value(cell: 0, field: 0));
    }
}
