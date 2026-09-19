using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.Commands;
using Puck.SignedDistance;
using Puck.World.Authoring;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldVectorSchemaTests {
    private static WorldPrototype Prototype(string id) {
        var shape = new ShapeDocument(
            Id: 0,
            Name: null,
            Type: SdfSolidPrimitive.Sphere,
            Position: new DocumentVector3(0f, 0f, 0f),
            Rotation: new DocumentQuaternion(0f, 0f, 0f, 1f),
            Scale: new DocumentVector3(1f, 1f, 1f),
            Material: 0,
            Blend: SdfBlendOp.Union,
            Smooth: 0f,
            Group: 0
        );
        var document = new CreationDocument(
            Schema: CreationDocument.CurrentSchema,
            Name: id,
            Palette: null,
            Shapes: [shape],
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

    private static StateVector SampleVector(int dimensions) {
        var components = new sbyte[dimensions];
        components[0] = 127;
        StateVector.TryCreate(components: components, vector: out var vector, error: out _);

        return vector!;
    }

    private static StateSpace SampleSpace(string name = "lore", string model = "text-embedding-3-small", string revision = "1", int dimensions = 32) =>
        new(
            Dimensions: dimensions,
            Model: model,
            Name: CellName.Parse(candidate: name),
            Revision: revision
        );

    private static WorldDefinition BuildDefinition(WorldStateRow[]? rows = null, StateSpace[]? spaces = null) =>
        new(
            Simulation: new WorldSimulationDefaults(RateHz: 240),
            StateRaw: new WorldStateSection(
                Spaces: spaces,
                World: rows
            )
        );

    private static string Validate(WorldDefinition definition) =>
        (WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out var reason
        )
            ? string.Empty
            : reason
        );

    [Fact]
    public void ValidVectorRowWithSpacePassesValidation() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Vector(components: vector.Memory)
                        )
                    ],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "situation"),
                    Space: "lore"
                )
            ],
            spaces: [space]
        );

        Assert.Equal(expected: string.Empty, actual: Validate(definition: definition));
    }

    [Fact]
    public void VectorRowInfereSingleDefaultSpace() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Vector(components: vector.Memory)
                        )
                    ],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "situation")
                )
            ],
            spaces: [space]
        );

        Assert.Equal(expected: string.Empty, actual: Validate(definition: definition));
    }

    [Fact]
    public void SpaceDimensionsOutOfRangeIsRefused() {
        var lowDim = BuildDefinition(spaces: [SampleSpace(dimensions: 4)]);
        Assert.Contains(
            actualString: Validate(definition: lowDim),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "must be between 8 and 1024"
        );

        var highDim = BuildDefinition(spaces: [SampleSpace(dimensions: 2048)]);
        Assert.Contains(
            actualString: Validate(definition: highDim),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "must be between 8 and 1024"
        );
    }

    [Fact]
    public void DuplicateSpaceNameIsRefused() {
        var definition = BuildDefinition(spaces: [
            SampleSpace(name: "lore"),
            SampleSpace(name: "lore")
        ]);

        Assert.Contains(
            actualString: Validate(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is duplicated"
        );
    }

    [Fact]
    public void VectorRowWithoutSpaceWhenMultipleExistIsRefused() {
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "ambush")
                )
            ],
            spaces: [
                SampleSpace(name: "space1"),
                SampleSpace(name: "space2")
            ]
        );

        Assert.Contains(
            actualString: Validate(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "without a declared space and multiple spaces are declared"
        );
    }

    [Fact]
    public void VectorRowWithUndeclaredSpaceIsRefused() {
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "ambush"),
                    Space: "unknown-space"
                )
            ],
            spaces: [SampleSpace(name: "lore")]
        );

        Assert.Contains(
            actualString: Validate(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "is not a declared space"
        );
    }

    [Fact]
    public void NonVectorRowDeclaringSpaceIsRefused() {
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 10))],
                    Kind: CellKind.Int,
                    Name: CellName.Parse(candidate: "score"),
                    Space: "lore"
                )
            ],
            spaces: [SampleSpace(name: "lore")]
        );

        Assert.Contains(
            actualString: Validate(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "only vector rows carry a space"
        );
    }

    [Fact]
    public void VectorRowRefusesDrawAndGatesDrive() {
        var definitionDraw = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Draw: new Draw(Source: CellName.Parse(candidate: "dice")),
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "vdraw"),
                    Space: "lore"
                )
            ],
            spaces: [SampleSpace(name: "lore")]
        );
        Assert.Contains(
            actualString: Validate(definition: definitionDraw),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "vector rows cannot draw"
        );

        var definitionGate = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Capacity: 10,
                    GatesDrive: true,
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "vgate"),
                    Space: "lore"
                )
            ],
            spaces: [SampleSpace(name: "lore")]
        );
        Assert.Contains(
            actualString: Validate(definition: definitionGate),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "declares gatesDrive on a vector row"
        );
    }

    [Fact]
    public void VectorRowPerByteCeilingExceededIsRefused() {
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Capacity: 2000,
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "huge"),
                    Space: "lore"
                )
            ],
            spaces: [SampleSpace(dimensions: 128, name: "lore")]
        );

        Assert.Contains(
            actualString: Validate(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "exceeds the maximum per-row ceiling"
        );
    }

    [Fact]
    public void VectorCellDimensionsMismatchIsRefused() {
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Vector(components: SampleVector(dimensions: 16).Memory)
                        )
                    ],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "badCell"),
                    Space: "lore"
                )
            ],
            spaces: [SampleSpace(dimensions: 32, name: "lore")]
        );

        Assert.Contains(
            actualString: Validate(definition: definition),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "must match space 'lore' dimensions 32"
        );
    }

    // A cell's own CellValue case now closes off the sibling-field bug this law used to test (a cell can no longer
    // carry both a Vector case and a stray numeric or text one): the surviving refusal is a cell of the WRONG case
    // altogether, caught by StateRow.TryAdmitKind.
    [Fact]
    public void VectorCellOfAnotherCaseIsRefused() {
        var definitionInt = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Int(value: 42)
                        )
                    ],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "badVal"),
                    Space: "lore"
                )
            ],
            spaces: [SampleSpace(dimensions: 32, name: "lore")]
        );
        Assert.Contains(
            actualString: Validate(definition: definitionInt),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "declares kind Vector"
        );

        var definitionText = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Text(value: "hello")
                        )
                    ],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "badText"),
                    Space: "lore"
                )
            ],
            spaces: [SampleSpace(dimensions: 32, name: "lore")]
        );
        Assert.Contains(
            actualString: Validate(definition: definitionText),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "declares kind Vector"
        );
    }

    [Fact]
    public void BasisMergeDetectsSpaceIdentityMismatch() {
        var basisJson = new JsonObject {
            ["state"] = new JsonObject {
                ["spaces"] = new JsonArray {
                    new JsonObject {
                        ["name"] = "lore",
                        ["model"] = "model-A",
                        ["revision"] = "1",
                        ["dimensions"] = 32
                    }
                }
            }
        };

        var overlayMismatch = new JsonObject {
            ["state"] = new JsonObject {
                ["spaces"] = new JsonArray {
                    new JsonObject {
                        ["name"] = "lore",
                        ["model"] = "model-B",
                        ["revision"] = "1",
                        ["dimensions"] = 32
                    }
                }
            }
        };

        var overlayMatch = new JsonObject {
            ["state"] = new JsonObject {
                ["spaces"] = new JsonArray {
                    new JsonObject {
                        ["name"] = "lore",
                        ["model"] = "model-A",
                        ["revision"] = "1",
                        ["dimensions"] = 32
                    }
                }
            }
        };

        var success = WorldDocumentBasis.TryMerge(
            basis: basisJson,
            composed: out _,
            overlay: overlayMismatch,
            reason: out var mismatchReason
        );
        Assert.False(success);
        Assert.Contains(expectedSubstring: "redeclaration mismatch", actualString: mismatchReason);

        var matchSuccess = WorldDocumentBasis.TryMerge(
            basis: basisJson,
            composed: out var composed,
            overlay: overlayMatch,
            reason: out var matchReason
        );
        Assert.True(matchSuccess);
        Assert.NotNull(composed);
    }

    [Fact]
    public void HostIdentitySpaceValidationEnforcesIdentity() {
        var hostSpaces = new[] { SampleSpace(dimensions: 32, model: "model-A", name: "lore", revision: "1") };
        var matchingSpaces = new[] { SampleSpace(dimensions: 32, model: "model-A", name: "lore", revision: "1") };
        var differingSpaces = new[] { SampleSpace(dimensions: 64, model: "model-A", name: "lore", revision: "1") };

        Assert.True(WorldIdentity.TryValidateSpacesAgainstHost(
            hostSpaces: hostSpaces,
            identitySpaces: matchingSpaces,
            reason: out var okReason
        ));
        Assert.Equal(expected: string.Empty, actual: okReason);

        Assert.False(WorldIdentity.TryValidateSpacesAgainstHost(
            hostSpaces: hostSpaces,
            identitySpaces: differingSpaces,
            reason: out var failReason
        ));
        Assert.Contains(expectedSubstring: "does not match host space identity", actualString: failReason);
    }

    [Fact]
    public void VectorCellRefusesAdvanceDynamicsAndCycle() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);

        var defAdvance = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Vector(components: vector.Memory),
                            Advance: new StateAdvance(1, 1)
                        )
                    ],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "adv")
                )
            ],
            spaces: [space]
        );
        Assert.Contains("declares advance on a vector cell — vector cells do not accumulate.", Validate(defAdvance));

        var defDynamics = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Vector(components: vector.Memory),
                            Dynamics: new StateDynamics("dynRow")
                        )
                    ],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "dyn")
                )
            ],
            spaces: [space]
        );
        Assert.Contains("declares dynamics on a vector cell — vector cells do not ease.", Validate(defDynamics));

        var defCycle = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Vector(components: vector.Memory),
                            Cycle: new StateCycle()
                        )
                    ],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "cyc")
                )
            ],
            spaces: [space]
        );
        Assert.Contains("declares cycle on a vector cell — vector cells do not turn.", Validate(defCycle));
    }

    [Fact]
    public void HudRefusesVectorRowBinding() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);
        var unitRect = new WorldHudRect(0f, 0f, 100f, 100f);
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Vector(components: vector.Memory))],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "situation")
                )
            ],
            spaces: [space]
        ) with {
            HudRaw = new WorldHudSection(
                Defaults: new WorldHudDefaults(Enabled: true),
                Panels: [
                    new WorldHudPanel(
                        Id: "p1",
                        Rect: unitRect,
                        Layer: WorldHudLayer.Over,
                        Style: WorldHudPanelStyle.Chip,
                        Elements: [
                            new WorldHudElement(
                                Id: "e1",
                                Kind: WorldHudElementKind.Text,
                                Rect: unitRect,
                                Style: WorldHudStyleToken.Primary,
                                Binding: "state.situation"
                            )
                        ]
                    )
                ]
            )
        };

        var reason = Validate(definition);
        Assert.Contains("addresses vector row 'situation' — a HUD element cannot bind to a vector row.", reason);
    }

    [Fact]
    public void ResponseRefusesVectorRow() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Vector(components: vector.Memory))],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "situation")
                )
            ],
            spaces: [space]
        ) with {
            CreationsRaw = [Prototype(id: "proto1")],
            PlacementsRaw = new WorldPlacementsSection(
                Rows: [
                    new WorldPlacement(
                        Id: "pl1",
                        PrototypeId: "proto1",
                        Position: new DocumentVector3(0f, 0f, 0f),
                        YawDegrees: 0f,
                        Scale: 1f,
                        Respond: [
                            new WorldPlacementResponse(
                                When: new WorldPlacementResponseCondition.StateCondition(
                                    State: "situation",
                                    Comparison: ActionStateComparison.Equal,
                                    Value: 10f
                                ),
                                PrototypeId: "proto1"
                            )
                        ]
                    )
                ]
            )
        };

        var reason = Validate(definition);
        Assert.Contains("references state row 'situation', which is kind=Vector — a response compares numbers, never Vector.", reason);
    }

    [Fact]
    public void SearchRefusesVectorTokens() {
        var space = SampleSpace(dimensions: 32);
        var vector = SampleVector(dimensions: 32);
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Capacity: 4,
                    Cells: [new StateCell(Key: CellName.Parse("t1"), Value: CellValue.Vector(components: vector.Memory))],
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "vTokens")
                ),
                new WorldStateRow(
                    Capacity: 4,
                    Cells: [],
                    Kind: CellKind.Int,
                    Name: CellName.Parse(candidate: "z1")
                ),
                new WorldStateRow(
                    Capacity: 4,
                    Cells: [],
                    Kind: CellKind.Int,
                    Name: CellName.Parse(candidate: "z2")
                )
            ],
            spaces: [space]
        ) with {
            SearchRaw = new WorldSearchSection(
                Jobs: [
                    new WorldSearchRow(
                        Name: "job1",
                        Tokens: "vTokens",
                        Zones: ["z1", "z2"]
                    )
                ]
            )
        };

        var reason = Validate(definition);
        Assert.Contains("tokens 'vTokens' must be the keyed row the zones draw their tokens from", reason);
    }

    [Fact]
    public void BindingRefusesVectorControlContext() {
        var doc = new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Chords: [],
            Contexts: [new BindingContextDefinition(Family: "state:vRow", State: "1", Group: "g1")]
        );
        var errors = new List<string>();
        WorldStateBindingContext.Validate(
            document: doc,
            stateRows: new Dictionary<string, WorldStateRow> {
                ["vRow"] = new WorldStateRow(Kind: CellKind.Vector, Name: CellName.Parse("vRow"))
            },
            errors: errors
        );

        Assert.Contains(errors, e => e.Contains("whose row is kind vector — vector rows cannot serve as control contexts"));
    }

    [Fact]
    public void Defect3_UnauthoredCapacityVectorTableUsesDefaultRoom() {
        var space = SampleSpace(dimensions: 128);
        var definition = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Kind: CellKind.Vector,
                    Name: CellName.Parse(candidate: "unbounded"),
                    Space: "lore"
                )
            ],
            spaces: [space]
        );

        Assert.Equal(string.Empty, Validate(definition));
    }
}


