using System.Numerics;
using Puck.Assets.Documents;
using Puck.Maths;
using Puck.World.Authoring;
using Puck.SignedDistance;
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
    // One 1x1x1 lattice cell over a field named "char", climbing 0.1/tick unconditionally, plus a plain scalar row
    // holding the threshold both documents compare against — one by literal, one by name.
    private static WorldStateSection FieldsSection() => new(
        World: [
            new WorldStateRow(
                Name: CellName.Parse(candidate: FieldName),
                Kind: CellKind.Fixed,
                Domain: new StateDomain.CellsOf(Topology: "world"), Field: new WorldStateFieldTrait(Initial: 0f, Min: 0f, Max: 1f)
            ),
            new WorldStateRow(
                Name: CellName.Parse(candidate: ThresholdRow),
                Kind: CellKind.Fixed,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: FixedQ4816.FromDouble(value: ThresholdValue).Value)]
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
                        Then: [new WorldFieldWrite(Field: FieldName, Op: WorldFieldWriteOp.Add, Value: 0.1f)]
                    ),
                ]
            ),
        ]
    );
    private static WorldDefinition Document(WorldLatticeScalar threshold) {
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
                    Respond: [
                        new WorldPlacementResponse(
                            When: new WorldPlacementResponseCondition.FieldCondition(Comparison: ActionStateComparison.GreaterOrEqual, Field: FieldName, Value: threshold),
                            PrototypeId: TargetCreation
                        ),
                    ]
                ),
            ],
        });
    }
    private static string PrototypeOf(WorldFixture fixture) => WorldDefinitionRows.FindPlacement(
        id: PlacementId,
        placements: fixture.Server.Definition.Placements
    )!.PrototypeId;

    [Fact]
    public void RowReferencedThresholdMatchesTheEquivalentLiteralTickForTick() {
        using var literal = Fixtures.FreshServer(definition: Document(threshold: ((float)ThresholdValue)));
        using var row = Fixtures.FreshServer(definition: Document(threshold: new WorldLatticeScalar(Row: ThresholdRow)));

        for (var index = 0; (index < 2); index++) {
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
