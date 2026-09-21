using System.Text;
using Puck.Assets.Documents;
using Puck.Physics.Fields;
using Puck.Maths;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>The named state hashes keep their declared boundaries deterministic and distinguish stored state that
/// can produce different future decisions even when its present resolved value happens to agree.</summary>
public sealed class WorldStateHashCompositionLawTests {
    // Every component the document-walking hasher folded, mapped onto the component that covers it now. Coverage
    // moves only by a deliberate edit here and to WorldStateHashComposition.Authoritative together.
    private static readonly (string Folded, WorldStateHashComponent Component)[] AuthoritativeCoverage = [
        ("the authoritative domain seed", WorldStateHashComponent.Seed),
        ("the tick", WorldStateHashComponent.Tick),
        ("every body's pose, rigid residue and carry relationship", WorldStateHashComponent.PopulationPose),
        ("each row's draw cursor, history cursor, phase sequence and drawn masks", WorldStateHashComponent.Arena),
        ("each cell's key, stored value, text, vector, visibility, observation, provenance, behavior and clock", WorldStateHashComponent.Arena),
        ("each cell's resolved value and text at the tick", WorldStateHashComponent.Arena),
        ("the participant and identity lanes", WorldStateHashComponent.Arena),
        ("each row's declared kind, envelope, capacity, overflow, gatesDrive, evicts and domain", WorldStateHashComponent.Declaration),
        ("each row's advance, draw, dynamics, field and cycle records and its audience", WorldStateHashComponent.Declaration),
        ("each cell's own advance, dynamics and cycle records", WorldStateHashComponent.Declaration),
        ("the physical field lattice's cells", WorldStateHashComponent.HostOwnedRows),
        ("every declared lattice topology", WorldStateHashComponent.Topologies),
        ("the rule family's edge latch", WorldStateHashComponent.RuleLatch),
        ("the interaction family's edge latch", WorldStateHashComponent.InteractionLatch),
        ("every rule group's progress", WorldStateHashComponent.RuleGroups),
        ("each decision's runtime", WorldStateHashComponent.Decisions),
        ("the board enforcement verdict latch", WorldStateHashComponent.BoardEnforcement),
        ("every body's action state", WorldStateHashComponent.BodyActionState),
        ("cached navigation", WorldStateHashComponent.Navigation),
        ("flock perception, slot generations and prior travel", WorldStateHashComponent.Flock),
        ("every search job's progress", WorldStateHashComponent.Search),
    ];

    // What the capture fold adds for a cell whose clock has never settled: the unset flag, then the five columns,
    // all zero. A trait-free cell folds this and a follower mid-ease does not.
    private static void FoldRestingClock(ref Fnv1aHash hash) {
        hash.Add(value: 0UL);

        for (var column = 0; (column < 5); column++) {
            hash.Add(value: 0L);
        }
    }

    [Fact]
    public void AuthoritativeComposition_NamesEveryComponentTheDocumentHasherFolded() {
        var named = WorldStateHashComposition.Authoritative;

        foreach (var (folded, component) in AuthoritativeCoverage) {
            Assert.True(
                condition: named.Contains(value: component),
                userMessage: $"the authoritative composition no longer names a component covering '{folded}'"
            );
        }

        Assert.Equal(
            expected: AuthoritativeCoverage.Select(selector: static row => row.Component).Distinct().Order().ToArray(),
            actual: named.Order().ToArray()
        );
    }
    [Fact]
    public void EveryComponentIsFoldedByADeclaredScopeAndNamesItsOwner() {
        var declared = Enum.GetValues<WorldStateHashScope>()
            .SelectMany(selector: WorldStateHashComposition.Order)
            .Distinct()
            .Order()
            .ToArray();

        Assert.Equal(
            expected: Enum.GetValues<WorldStateHashComponent>().Order().ToArray(),
            actual: declared
        );

        foreach (var component in Enum.GetValues<WorldStateHashComponent>()) {
            Assert.False(condition: string.IsNullOrEmpty(value: WorldStateHashComposition.Owner(component: component)));
        }
    }
    [Fact]
    public void DroppingAnyOneComponentMovesTheAuthoritativeHash() {
        using var fixture = Fixtures.FreshServer();
        var order = WorldStateHashComposition.Authoritative;
        var whole = WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: 0UL
        );

        foreach (var dropped in order) {
            var without = order.Where(predicate: component => (component != dropped)).ToArray();

            Assert.True(
                condition: (whole != WorldStateHashComposition.Compose(
                    order: without,
                    seed: 0x4155544853543031UL,
                    server: fixture.Server,
                    tick: 0UL
                )),
                userMessage: $"dropping '{dropped}' left the authoritative hash unchanged, so it folds nothing"
            );
        }
    }
    [Fact]
    public void AuthoritativeScope_IsStableAcrossEquivalentServers() {
        var definition = Fixtures.BuildDocument();

        using var left = Fixtures.FreshServer(definition: definition);
        using var right = Fixtures.FreshServer(definition: definition);

        Assert.Equal(
            expected: WorldStateHashComposition.HashAuthoritative(
                server: left.Server,
                tick: 0UL
            ),
            actual: WorldStateHashComposition.HashAuthoritative(
                server: right.Server,
                tick: 0UL
            )
        );
    }
    [Fact]
    public void CaptureScope_FoldsThePoseThenEachCellsValueAndClock() {
        var definition = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [
                new WorldStateRow(
                Name: CellName.Parse(candidate: "count"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 17L)
                    )]
            ),
                new WorldStateRow(
                Name: CellName.Parse(candidate: "label"),
                Kind: CellKind.Text,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Text(value: "café")
                    )]
            ),
            ]),
        };

        using var fixture = Fixtures.FreshServer(definition: definition);
        var expected = Fnv1aHash.Create();

        expected.Add(value: WorldReplaySnapshot.HashState(population: fixture.Server.Population));
        expected.Add(value: 17L);
        FoldRestingClock(hash: ref expected);
        // A Text cell carries no stored number, so the numeric fold takes zero and the UTF-8 bytes follow it.
        expected.Add(value: 0L);
        expected.Add(values: Encoding.UTF8.GetBytes(s: "café"));
        FoldRestingClock(hash: ref expected);

        Assert.Equal(
            expected: expected.Value,
            actual: WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.Capture,
                server: fixture.Server,
                tick: 0UL
            )
        );
    }
    [Fact]
    public void PoseScope_IsTheReplayPoseDigest() {
        using var fixture = Fixtures.FreshServer();

        Assert.Equal(
            expected: WorldReplaySnapshot.HashState(population: fixture.Server.Population),
            actual: WorldStateHashComposition.Hash(
                scope: WorldStateHashScope.Pose,
                server: fixture.Server,
                tick: 0UL
            )
        );
    }
    [Fact]
    public void WorldScope_DistinguishesDrawTraitsWithTheSameCurrentValue() {
        var name = CellName.Parse(candidate: "future-draw");
        var plain = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: name,
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 5L)
                    )]
            )]),
        };
        var drawn = plain with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: name,
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 5L)
                    )],
                Draw: new Draw(
                    Generator: new StateGenerator(
                        Source: GeneratorSource.UniformRange,
                        RangeMin: 0L,
                        RangeMax: 10L
                    ),
                    Timing: DrawTiming.Event
                )
            )]),
        };

        using var plainFixture = Fixtures.FreshServer(definition: plain);
        using var drawnFixture = Fixtures.FreshServer(definition: drawn);

        Assert.NotEqual(
            expected: WorldStateHashComposition.HashWorld(
                server: plainFixture.Server,
                tick: 0UL
            ),
            actual: WorldStateHashComposition.HashWorld(
                server: drawnFixture.Server,
                tick: 0UL
            )
        );
    }
    [Fact]
    public void WorldScope_DistinguishesLiveLatticeCells() {
        var fields = new WorldFieldsSection(
            Lattice: new WorldFieldLatticeDefinition(
                Origin: new DocumentVector3(
                    x: 0f,
                    y: 0f,
                    z: 0f
                ),
                CellSize: 1f,
                Width: 1,
                Depth: 1
            ),
            Fields: [new WorldFieldRow(
                    Name: "heat",
                    Min: 0f,
                    Max: 10f
                )]
        );
        var definition = Fixtures.WithLattice(
            definition: Fixtures.BuildDocument(),
            composite: fields
        );

        using var left = Fixtures.FreshServer(definition: definition);
        using var right = Fixtures.FreshServer(definition: definition);

        Assert.IsType<FieldLattice>(@object: right.Server.Population.Fields).Restore(checkpoint: new FieldLattice.Checkpoint(Raw: [[FixedQ4816.One.Value]]));

        Assert.NotEqual(
            expected: WorldStateHashComposition.HashWorld(
                server: left.Server,
                tick: 0UL
            ),
            actual: WorldStateHashComposition.HashWorld(
                server: right.Server,
                tick: 0UL
            )
        );
    }
    [Fact]
    public void WorldScope_DistinguishesStoredAdvanceTraitsWithTheSameCurrentValue() {
        var name = CellName.Parse(candidate: "future-state");
        var plain = Fixtures.BuildDocument() with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: name,
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 5L)
                    )]
            )]),
        };
        var advancing = plain with {
            StateRaw = new WorldStateSection(World: [new WorldStateRow(
                Name: name,
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: 5L)
                    )],
                Advance: new StateAdvance(
                    PerSecondDenominator: 1L,
                    PerSecondNumerator: 1L
                )
            )]),
        };

        using var plainFixture = Fixtures.FreshServer(definition: plain);
        using var advancingFixture = Fixtures.FreshServer(definition: advancing);

        Assert.NotEqual(
            expected: WorldStateHashComposition.HashWorld(
                server: plainFixture.Server,
                tick: 0UL
            ),
            actual: WorldStateHashComposition.HashWorld(
                server: advancingFixture.Server,
                tick: 0UL
            )
        );
    }
    [Fact]
    public void DeclarationHashDistinguishesPoolFieldAdvance() {
        ulong Hash(StateAdvance? advance) {
            var hash = Fnv1aHash.Create();

            WorldStateHashComposition.AppendDeclaration(hash: ref hash, state: new WorldStateSection(
                Records: [new StateRecord(Name: CellName.Parse(candidate: "actor"), Fields: [new StatePoolField(Name: CellName.Parse(candidate: "score"), Advance: advance)])],
                Pools: [new StatePool(Name: CellName.Parse(candidate: "actors"), Record: CellName.Parse(candidate: "actor"), Capacity: 1)]));
            return hash.Value;
        }

        Assert.NotEqual(expected: Hash(advance: null), actual: Hash(advance: new StateAdvance(PerSecondDenominator: 1L, PerSecondNumerator: 1L)));
    }
    [Fact]
    public void DeclarationHashFoldsEveryRowVisibilityField() {
        var baseline = DeclarationHash(visibility: new StateVisibility(Readers: null));

        Assert.NotEqual(
            actual: DeclarationHash(visibility: new StateVisibility(Readers: [])),
            expected: baseline
        );
        Assert.NotEqual(
            actual: DeclarationHash(visibility: new StateVisibility(
                Hidden: HiddenCells.Count,
                Readers: null
            )),
            expected: baseline
        );
        Assert.NotEqual(
            actual: DeclarationHash(visibility: new StateVisibility(
                Readers: null,
                ReadersFrom: "audience"
            )),
            expected: baseline
        );
    }

    private static ulong DeclarationHash(StateVisibility? visibility) {
        var hash = Fnv1aHash.Create();

        WorldStateHashComposition.AppendDeclaration(
            hash: ref hash,
            state: new WorldStateSection(World: [new WorldStateRow(
                Name: CellName.Parse(candidate: "visible"),
                Kind: CellKind.Int,
                Visibility: visibility
            )])
        );

        return hash.Value;
    }
}
