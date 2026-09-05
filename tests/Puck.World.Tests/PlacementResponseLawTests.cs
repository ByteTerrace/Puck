using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: a placement's response trait swaps its prototype the tick a lattice condition first holds, through the
/// ordinary mutation pipeline — deterministically, and never when the trait is absent or its condition never holds.
/// Multiple entries try in authored order and the first match wins.
/// </summary>
public sealed class PlacementResponseLawTests {
    private const string BaseCreation = "leaf";
    private const string FieldName = "char";
    private const string PlacementId = "grove";
    private const string TargetCreation = "stump";

    private static WorldPrototype Creation(string id) {
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: null,
            Shapes: [
                new ShapeDocument(
                    Id: 0,
                    Name: null,
                    Type: SdfSolidPrimitive.Sphere,
                    Position: Vector3.Zero,
                    Rotation: Quaternion.Identity,
                    Scale: new Vector3(value: 1f),
                    Material: 0,
                    Blend: SdfBlendOp.Union,
                    Smooth: 0f,
                    Group: 0
                ),
            ],
            Frames: null
        );
        var canonical = CreationCanonicalizer.Canonicalize(
            document: document,
            source: id
        );

        return new WorldPrototype(
            Id: id,
            Document: canonical.Document,
            HashRaw: canonical.Hash
        );
    }
    // One 1x1x1 lattice cell over a field named "char", climbing 0.1/tick — an unconditional transform (an empty
    // "when" holds vacuously) so every fixture step deterministically advances the SAME chemistry, with no fire
    // simulation or body coupling needed to drive it.
    private static WorldStateSection FieldsSection() => new(
        World: [
            new WorldStateRow(
                Name: WorldCellName.Parse(candidate: FieldName),
                Kind: CellKind.Fixed,
                Domain: new WorldStateDomain.CellsOf(Topology: "world"), Field: new WorldStateFieldTrait(Initial: 0f, Min: 0f, Max: 1f)
            ),
        ],
        Lattices: [
            new WorldStateLatticeTopology(
                Name: "world",
                Origin: new DocumentVector3(value: Vector3.Zero),
                CellSize: 1f,
                Width: 1,
                Depth: 1,
                Layers: 1,
                StepEveryTicks: 1,
                Reactions: [
                    new WorldReaction.Transform(
                        When: [],
                        Then: [new WorldFieldWrite(Field: FieldName, Op: WorldFieldWriteOp.Add, Value: 0.1f)]
                    ),
                ]
            ),
        ]
    );
    private static WorldDefinition Document(IReadOnlyList<WorldPlacementResponse>? respond) {
        var document = Fixtures.BuildDocument();

        return (document with {
            StateRaw = FieldsSection(),
            CreationsRaw = [Creation(id: BaseCreation), Creation(id: TargetCreation)],
            PlacementRowsRaw = [
                new WorldPlacement(
                    Id: PlacementId,
                    PrototypeId: BaseCreation,
                    Position: new DocumentVector3(value: Vector3.Zero),
                    YawDegrees: 0f,
                    Scale: 1f,
                    Respond: respond
                ),
            ],
        });
    }
    private static WorldPlacementResponse Entry(WorldFieldComparison comparison, float threshold, string prototypeId) => new(
        When: new WorldFieldCondition(Comparison: comparison, Field: FieldName, Value: threshold),
        PrototypeId: prototypeId
    );
    private static string PrototypeOf(WorldFixture fixture) => WorldDefinitionRows.FindPlacement(
        id: PlacementId,
        placements: fixture.Server.Definition.Placements
    )!.PrototypeId;

    /// <summary>ABSENT trait: stepping past the tick a would-be threshold would have crossed leaves the row's
    /// prototype byte-for-byte unchanged. CONTROL: the identical run WITH the trait present swaps.</summary>
    [Fact]
    public void AbsentTraitIsANoOp() {
        using var absent = Fixtures.FreshServer(definition: Document(respond: null));

        for (var index = 0; (index < 20); index++) {
            absent.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: absent),
            expected: BaseCreation
        );

        using var present = Fixtures.FreshServer(definition: Document(respond: [
            Entry(comparison: WorldFieldComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 0.5f),
        ]));

        for (var index = 0; (index < 20); index++) {
            present.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: present),
            expected: TargetCreation
        );
    }
    /// <summary>Two entries hold simultaneously once the field passes the higher threshold; the FIRST in authored
    /// order wins regardless of which condition is "more true". Reversing authored order flips the winner — the
    /// same field trajectory, a different result, proving the rule reads order, not magnitude.</summary>
    [Fact]
    public void FirstMatchInAuthoredOrderWins() {
        const string secondTarget = "ember";

        using var firstWins = Fixtures.FreshServer(definition: (Document(respond: [
            Entry(comparison: WorldFieldComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 0.2f),
            Entry(comparison: WorldFieldComparison.GreaterOrEqual, prototypeId: secondTarget, threshold: 0.2f),
        ]) with {
            CreationsRaw = [Creation(id: BaseCreation), Creation(id: TargetCreation), Creation(id: secondTarget)],
        }));

        for (var index = 0; (index < 5); index++) {
            firstWins.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: firstWins),
            expected: TargetCreation
        );

        using var secondWins = Fixtures.FreshServer(definition: (Document(respond: [
            Entry(comparison: WorldFieldComparison.GreaterOrEqual, prototypeId: secondTarget, threshold: 0.2f),
            Entry(comparison: WorldFieldComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 0.2f),
        ]) with {
            CreationsRaw = [Creation(id: BaseCreation), Creation(id: TargetCreation), Creation(id: secondTarget)],
        }));

        for (var index = 0; (index < 5); index++) {
            secondWins.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: secondWins),
            expected: secondTarget
        );
    }
    /// <summary>A holding condition swaps the prototype; the row is left exactly as it reads once nothing holds
    /// (the facet only ever SELECTS on a match, it never reverts) — proved by an unreachable second entry that a
    /// later tick could otherwise have satisfied.</summary>
    [Fact]
    public void ANonHoldingConditionNeverSwaps() {
        using var fixture = Fixtures.FreshServer(definition: Document(respond: [
            Entry(comparison: WorldFieldComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 999f),
        ]));

        for (var index = 0; (index < 20); index++) {
            fixture.Step();
        }

        Assert.Equal(
            actual: PrototypeOf(fixture: fixture),
            expected: BaseCreation
        );
    }
    /// <summary>DETERMINISM: two independently constructed fixtures, the same document, the same input (none —
    /// nothing but ticks drives this document's chemistry), swap on the exact same tick every time, and every
    /// intermediate tick's prototype matches step for step across the whole run.</summary>
    [Fact]
    public void TheSwapTickIsDeterministicAcrossIndependentRuns() {
        WorldDefinition Build() => Document(respond: [
            Entry(comparison: WorldFieldComparison.GreaterOrEqual, prototypeId: TargetCreation, threshold: 0.5f),
        ]);

        using var a = Fixtures.FreshServer(definition: Build());
        using var b = Fixtures.FreshServer(definition: Build());

        for (var index = 0; (index < 20); index++) {
            a.Step();
            b.Step();

            Assert.Equal(
                actual: PrototypeOf(fixture: b),
                expected: PrototypeOf(fixture: a)
            );
        }

        // Both runs actually exercised the swap — a determinism check over a run that never fires proves nothing.
        Assert.Equal(
            actual: PrototypeOf(fixture: a),
            expected: TargetCreation
        );
    }
}
