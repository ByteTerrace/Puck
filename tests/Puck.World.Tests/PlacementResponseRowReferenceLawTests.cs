using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;
using Xunit;

namespace Puck.World.Tests;

/// <summary>THE LAW: a placement response condition whose expected value names a state row
/// (<c>WorldLatticeScalar.Row</c>) swaps the prototype on the exact same tick a literal condition with the row's
/// own value would — the row-referenced seam resolves the row through the document catalog rather than reading a
/// different value or missing the crossing tick.</summary>
public sealed class PlacementResponseRowReferenceLawTests {
    private const string BaseCreation = "leaf";
    private const string FieldName = "char";
    private const string PlacementId = "grove";
    private const string TargetCreation = "stump";
    private const string ThresholdRow = "charThreshold";
    private const double ThresholdValue = 0.25;

    private static WorldDefinition Document(WorldLatticeScalar threshold) {
        var document = Fixtures.BuildDocument();

        return (document with {
            StateRaw = FieldsSection(),
            CreationsRaw = [CreationFixtures.UnitSphere(id: BaseCreation), CreationFixtures.UnitSphere(id: TargetCreation)],
            PlacementRowsRaw = [
                new WorldPlacement(
                Id: PlacementId,
                PrototypeId: BaseCreation,
                Position: new DocumentVector3(value: Vector3.Zero),
                YawDegrees: 0f,
                Scale: 1f,
                Respond: [
                        new WorldPlacementResponse(
                        When: new WorldPlacementResponseCondition.FieldCondition(
                            Comparison: ExpressionOp.GreaterOrEqual,
                            Field: FieldName,
                            Value: threshold
                        ),
                        PrototypeId: TargetCreation
                    ),
                    ]
            ),
            ],
        });
    }
    // One 1x1x1 lattice cell over a field named "char", climbing 0.1/tick unconditionally, plus a plain scalar row
    // holding the threshold both documents compare against — one by literal, one by name.
    private static WorldStateSection FieldsSection() => new(
        World: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: FieldName),
                Kind: CellKind.Fixed,
                Domain: new StateDomain.CellsOf(Topology: "world"),
                Field: new WorldStateFieldTrait(
                    Initial: 0f,
                    Min: 0f,
                    Max: 1f
                )
            ),
            new WorldStateRow(
                Name: CellName.Parse(candidate: ThresholdRow),
                Kind: CellKind.Fixed,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Fixed(rawBits: FixedQ4816.FromDouble(value: ThresholdValue).Value)
                    )]
            ),
        ],
        Lattices: [
            new WorldFieldTopology(
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
                        Then: [new WorldFieldWrite(
                                Field: FieldName,
                                Op: WorldFieldWriteOp.Add,
                                Value: 0.1f
                            )]
                    ),
                ]
            ),
        ]
    );
    private static string PrototypeOf(WorldFixture fixture) => WorldDefinitionRows.FindPlacement(
        id: PlacementId,
        placements: fixture.Server.Definition.Placements
    )!.ShownPrototypeId;

    [Fact]
    public void RowReferencedThresholdMatchesTheEquivalentLiteralTickForTick() {
        using var literal = Fixtures.FreshServer(definition: Document(threshold: ((float)ThresholdValue)));
        using var row = Fixtures.FreshServer(definition: Document(threshold: new WorldLatticeScalar(Row: ThresholdRow)));

        for (var index = 0; (index < 20); index++) {
            literal.Step();
            row.Step();

            Assert.Equal(
                actual: PrototypeOf(fixture: row),
                expected: PrototypeOf(fixture: literal)
            );
        }

        // Both runs actually exercised the swap -- a tick-for-tick match over a run that never fires proves nothing.
        Assert.Equal(
            actual: PrototypeOf(fixture: literal),
            expected: TargetCreation
        );
    }
}
