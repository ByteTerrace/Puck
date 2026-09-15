using System.Text.Json.Nodes;
using Xunit;

namespace Puck.World.Schema.Tests;

public sealed class WorldVectorSchemaTests {
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
                            Vector: vector
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
                            Vector: vector
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
                    Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: 10)],
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
                            Vector: SampleVector(dimensions: 16)
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

    [Fact]
    public void VectorCellWithNonZeroValueOrTextIsRefused() {
        var definitionValue = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: 42,
                            Vector: SampleVector(dimensions: 32)
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
            actualString: Validate(definition: definitionValue),
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "value must be 0 for a vector cell"
        );

        var definitionText = BuildDefinition(
            rows: [
                new WorldStateRow(
                    Cells: [
                        new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Text: "hello",
                            Vector: SampleVector(dimensions: 32)
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
            expectedSubstring: "text must be null for a vector cell"
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

}
