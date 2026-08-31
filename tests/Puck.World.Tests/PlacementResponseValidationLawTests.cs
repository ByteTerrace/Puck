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
    private const string FieldName = "char";
    private const string PlacementId = "grove";
    private const string TargetCreation = "stump";

    private static void AssertValidates(WorldDefinition definition) {
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: definition,
                reason: out var reason
            ),
            userMessage: reason
        );
    }
    private static void AssertRefusedNaming(WorldDefinition definition, string needle) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: needle
        );
    }
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
                ? [new FrameDocument(Name: "idle", Transforms: [])]
                : null
            )
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
    // One 1x1x1 lattice cell carrying the one field the well-formed condition names — the smallest fields section a
    // response's condition can validate against.
    private static WorldStateSection FieldsSection() => new(
        World: [
            new WorldStateRow(
                Name: WorldCellName.Parse(candidate: FieldName),
                Kind: CellKind.Fixed,
                Lattice: new WorldStateLatticeTrait(Topology: "world", Initial: 0f, Min: 0f, Max: 1f)
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
                StepEveryTicks: 1
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
    private static WorldPlacement WellFormed() => new(
        Id: PlacementId,
        PrototypeId: BaseCreation,
        Position: new DocumentVector3(value: Vector3.Zero),
        YawDegrees: 0f,
        Scale: 1f,
        Respond: [
            new WorldPlacementResponse(
                When: new WorldFieldCondition(Field: FieldName, Comparison: WorldFieldComparison.GreaterOrEqual, Value: 0.5f),
                PrototypeId: TargetCreation
            ),
        ]
    );

    /// <summary>DENIAL: an entry naming a creation the document does not declare. CONTROL: the declared one.</summary>
    [Fact]
    public void EntryPrototypeIdMustResolve() {
        AssertRefusedNaming(
            definition: With(placement: (WellFormed() with {
                Respond = [new WorldPlacementResponse(When: WellFormed().Respond![0].When, PrototypeId: "no-such-creation")],
            })),
            needle: "names no creation row"
        );
        AssertValidates(definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: a condition naming a field the fields section does not declare. CONTROL: the declared field
    /// name.</summary>
    [Fact]
    public void ConditionFieldMustBeDeclared() {
        AssertRefusedNaming(
            definition: With(placement: (WellFormed() with {
                Respond = [new WorldPlacementResponse(
                    When: new WorldFieldCondition(Field: "no-such-field", Comparison: WorldFieldComparison.GreaterOrEqual, Value: 0.5f),
                    PrototypeId: TargetCreation
                )],
            })),
            needle: "which fields.fields does not declare"
        );
        AssertValidates(definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: a comparison outside the declared enum — a malformed comparison is a hard parse-time enum
    /// refusal, never a silent default to the first member. CONTROL: a declared comparison validates.</summary>
    [Fact]
    public void ComparisonMustBeADeclaredEnumMember() {
        AssertRefusedNaming(
            definition: With(placement: (WellFormed() with {
                Respond = [new WorldPlacementResponse(
                    When: new WorldFieldCondition(Field: FieldName, Comparison: ((WorldFieldComparison)byte.MaxValue), Value: 0.5f),
                    PrototypeId: TargetCreation
                )],
            })),
            needle: "is unknown"
        );
        AssertValidates(definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: a response entry targeting an ANIMATED creation (timeline frames) — a response only ever
    /// swaps between static creations. CONTROL: the static target.</summary>
    [Fact]
    public void EntryPrototypeMustBeStatic() {
        var document = Fixtures.BuildDocument();
        var animated = (document with {
            StateRaw = FieldsSection(),
            CreationsRaw = [Creation(id: BaseCreation), Creation(id: TargetCreation, animated: true)],
            PlacementRowsRaw = [WellFormed()],
        });

        AssertRefusedNaming(
            definition: animated,
            needle: "carries timeline frames"
        );
        AssertValidates(definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: the facet composing with attach, inhabit, or faceSources. CONTROL: the facet alone.</summary>
    [Fact]
    public void FacetRefusesAlongsideAttachInhabitAndFaceSources() {
        AssertRefusedNaming(
            definition: With(placement: (WellFormed() with {
                Attach = new WorldPlacementAttach(BodyIndex: 0, LocalOffset: new DocumentVector3(value: Vector3.Zero)),
            })),
            needle: "is refused alongside attach/inhabit/faceSources"
        );
        AssertRefusedNaming(
            definition: With(placement: (WellFormed() with {
                Inhabit = new WorldPlacementInhabit(Kit: Fixtures.SeatKitName, Look: null, Source: Puck.World.Protocol.IntentSource.Idle),
            })),
            needle: "is refused alongside attach/inhabit/faceSources"
        );
        AssertRefusedNaming(
            definition: With(placement: (WellFormed() with {
                FaceSources = [new WorldPlacementFace(Face: "front", Source: new WorldScreenSource.None())],
            })),
            needle: "is refused alongside attach/inhabit/faceSources"
        );
        AssertValidates(definition: With(placement: WellFormed()));
    }
    /// <summary>DENIAL: an empty response list and one past the entry ceiling. CONTROL: one well-formed entry.</summary>
    [Fact]
    public void EntryCountMustSitInsideItsBand() {
        AssertRefusedNaming(
            definition: With(placement: (WellFormed() with { Respond = [] })),
            needle: "declares no response entry"
        );

        var over = new List<WorldPlacementResponse>();

        for (var index = 0; (index <= WorldResponseCapacity.MaxEntries); index++) {
            over.Add(item: WellFormed().Respond![0]);
        }

        AssertRefusedNaming(
            definition: With(placement: (WellFormed() with { Respond = over })),
            needle: $"exceeding the {WorldResponseCapacity.MaxEntries}-entry ceiling"
        );
        AssertValidates(definition: With(placement: WellFormed()));
    }
}
