using System.Numerics;
using Puck.Assets.Documents;
using Puck.World.Authoring;
using Puck.SignedDistance;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// THE LAW: every way of authoring a placement's response facet wrong is refused BY NAME, and the one well-formed
/// spelling validates. An unknown target creation, an unknown condition field, and a malformed comparison each
/// refuse; a target creation carrying timeline frames refuses; the facet composing with attach/inhabit/faceSources
/// refuses.
/// <para>Each arm is a denial paired with a control differing in exactly one authored field.</para>
/// </summary>
public sealed class PlacementResponseValidationLawTests {
    private const string BaseCreation = "leaf";
    private const string CounterRow = "declaredCounter";
    private const string FieldName = "char";
    private const string PlacementId = "grove";
    private const string TargetCreation = "stump";

    private static WorldPrototype Creation(string id, bool animated = false) {
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
            Frames: (animated
            ? [new FrameDocument(
                        Name: "idle",
                        Transforms: []
                    )]
            : null)
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
    // One 1x1x1 lattice cell carrying the one field the well-formed condition names, plus one plain Int row a state
    // condition's own laws validate against — the smallest fields+state section either arm needs.
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
                Name: CellName.Parse(candidate: CounterRow),
                Kind: CellKind.Int
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
                StepEveryTicks: 1
            ),
        ]
    );
    private static WorldPlacement WellFormed() => new(
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
                    Value: 0.5f
                ),
                PrototypeId: TargetCreation
            ),
        ]
    );
    private static WorldDefinition With(WorldPlacement placement) {
        var document = Fixtures.BuildDocument();

        return (document with {
            StateRaw = FieldsSection(),
            CreationsRaw = [Creation(id: BaseCreation), Creation(id: TargetCreation)],
            PlacementRowsRaw = [placement],
        });
    }

    /// <summary>DENIAL: a comparison outside the declared enum — a malformed comparison is a hard parse-time enum
    /// refusal, never a silent default to the first member. CONTROL: a declared comparison validates.</summary>
    [Fact]
    public void ComparisonMustBeADeclaredEnumMember() {
        Laws.Refuses(
            locally: true,
            definition: With(placement: (WellFormed() with {
                Respond = [new WorldPlacementResponse(
                    When: new WorldPlacementResponseCondition.FieldCondition(
                        Comparison: ((ExpressionOp)byte.MaxValue),
                        Field: FieldName,
                        Value: 0.5f
                    ),
                    PrototypeId: TargetCreation
                )],
            })),
            needle: "is unknown"
        );
        Laws.Validates(locally: true, definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: a condition naming a field the fields section does not declare. CONTROL: the declared field
    /// name.</summary>
    [Fact]
    public void ConditionFieldMustBeDeclared() {
        Laws.Refuses(
            locally: true,
            definition: With(placement: (WellFormed() with {
                Respond = [new WorldPlacementResponse(
                    When: new WorldPlacementResponseCondition.FieldCondition(
                        Comparison: ExpressionOp.GreaterOrEqual,
                        Field: "no-such-field",
                        Value: 0.5f
                    ),
                    PrototypeId: TargetCreation
                )],
            })),
            needle: "which fields.fields does not declare"
        );
        Laws.Validates(locally: true, definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: an empty response list and one past the entry ceiling. CONTROL: one well-formed entry.</summary>
    [Fact]
    public void EntryCountMustSitInsideItsBand() {
        Laws.Refuses(
            definition: With(placement: (WellFormed() with { Respond = [] })),
            locally: true,
            needle: "declares no response entry"
        );

        var over = new List<WorldPlacementResponse>();

        for (var index = 0; (index <= WorldResponseCapacity.MaxEntries); index++) {
            over.Add(item: WellFormed().Respond![0]);
        }

        Laws.Refuses(
            definition: With(placement: (WellFormed() with { Respond = over })),
            locally: true,
            needle: $"exceeding the {WorldResponseCapacity.MaxEntries}-entry ceiling"
        );
        Laws.Validates(locally: true, definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: an entry naming a creation the document does not declare. CONTROL: the declared one.</summary>
    [Fact]
    public void EntryPrototypeIdMustResolve() {
        Laws.Refuses(
            locally: true,
            definition: With(placement: (WellFormed() with {
                Respond = [new WorldPlacementResponse(
                    When: WellFormed().Respond![0].When,
                    PrototypeId: "no-such-creation"
                )],
            })),
            needle: "names no creation row"
        );
        Laws.Validates(locally: true, definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: a response entry targeting an ANIMATED creation (timeline frames) — a response only ever
    /// swaps between static creations. CONTROL: the static target.</summary>
    [Fact]
    public void EntryPrototypeMustBeStatic() {
        var document = Fixtures.BuildDocument();
        var animated = (document with {
            StateRaw = FieldsSection(),
            CreationsRaw = [Creation(id: BaseCreation), Creation(
                animated: true,
                id: TargetCreation
            )],
            PlacementRowsRaw = [WellFormed()],
        });

        Laws.Refuses(
            definition: animated,
            locally: true,
            needle: "carries timeline frames"
        );
        Laws.Validates(locally: true, definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: the facet composing with attach, inhabit, or faceSources. CONTROL: the facet alone.</summary>
    [Fact]
    public void FacetRefusesAlongsideAttachInhabitAndFaceSources() {
        Laws.Refuses(
            locally: true,
            definition: With(placement: (WellFormed() with {
                Attach = new WorldPlacementAttach(
                BodyIndex: 0,
                LocalOffset: new DocumentVector3(value: Vector3.Zero)
            ),
            })),
            needle: "is refused alongside attach/inhabit/faceSources"
        );
        Laws.Refuses(
            locally: true,
            definition: With(placement: (WellFormed() with {
                Inhabit = new WorldPlacementInhabit(
                Kit: Fixtures.SeatKitName,
                Look: null,
                Source: Puck.World.Protocol.IntentSource.Idle
            ),
            })),
            needle: "is refused alongside attach/inhabit/faceSources"
        );
        Laws.Refuses(
            locally: true,
            definition: With(placement: (WellFormed() with {
                FaceSources = [new WorldPlacementFace(
                    Face: "front",
                    Source: new WorldScreenSource.None()
                )],
            })),
            needle: "is refused alongside attach/inhabit/faceSources"
        );
        Laws.Validates(locally: true, definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: a state condition naming a row the document does not declare. CONTROL: the declared
    /// row.</summary>
    [Fact]
    public void StateConditionRowMustBeDeclared() {
        Laws.Refuses(
            locally: true,
            definition: With(placement: (WellFormed() with {
                Respond = [new WorldPlacementResponse(
                    When: new WorldPlacementResponseCondition.StateCondition(
                        State: "no-such-row",
                        Comparison: ExpressionOp.GreaterOrEqual,
                        Value: 3
                    ),
                    PrototypeId: TargetCreation
                )],
            })),
            needle: "which the document does not declare"
        );
        Laws.Validates(locally: true, definition: With(placement: (WellFormed() with {
            Respond = [new WorldPlacementResponse(
                When: new WorldPlacementResponseCondition.StateCondition(
                    State: CounterRow,
                    Comparison: ExpressionOp.GreaterOrEqual,
                    Value: 3
                ),
                PrototypeId: TargetCreation
            )],
        })));
    }
}
