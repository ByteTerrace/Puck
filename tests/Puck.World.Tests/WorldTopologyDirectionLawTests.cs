using Puck.Assets.Documents;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Pins directions as content: an unauthored topology compiles exactly the fixed set every kind carried
/// before <see cref="IDiscreteLatticeTopology.Directions"/> existed, while an authored list replaces it wholesale —
/// a 4-connected grid orthogonal-only, or a renamed box vocabulary — and the validator refuses a list that would
/// leave <see cref="CompiledTopology.Opposite"/> unable to close.</summary>
public sealed class WorldTopologyDirectionLawTests {
    private static LatticeTopology.Box Box() =>
        new(
            "box",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            0.5f,
            2,
            2,
            Layers: 2,
            LayerHeight: 0.5f
        );
    private static LatticeTopology.Grid Grid() =>
        new(
            "grid",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            4,
            4
        );

    [Fact]
    public void AFourConnectedGridReplacesTheDefaultCompassVocabularyWholesale() {
        var orthogonal = new TopologyDirection[] {
            new(
            "north",
            0,
            -1
        ), new(
            "south",
            0,
            1
        ), new(
            "east",
            1,
            0
        ), new(
            "west",
            -1,
            0
        ),
        };
        var topology = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [Grid() with { Directions = orthogonal }]),
            "grid"
        )!;

        Assert.Equal(
            4,
            topology.DirectionCount
        );
        // The default compass names are gone entirely — an authored list is the topology's ONLY vocabulary.
        Assert.Equal(
            -1,
            topology.Direction(token: "N")
        );
        Assert.Equal(
            -1,
            topology.Direction(token: "NE")
        );
        var north = topology.Direction(token: "north");

        Assert.True(condition: (north >= 0));
        Assert.Equal(
            topology.Direction(token: "south"),
            topology.Opposite(direction: north)
        );
        // Origin cell (0,0) has no north neighbour but does have an east one.
        Assert.Equal(
            -1,
            topology.Neighbour(
                cell: 0,
                direction: north
            )
        );
        Assert.Equal(
            1,
            topology.Neighbour(
                cell: 0,
                direction: topology.Direction(token: "east")
            )
        );
    }
    // A physical field's case type carries no 'directions' property to author in the first place; the invariant
    // the old runtime-validator check named is now enforced by the document's own strict-parsed JSON shape instead.
    [Fact]
    public void APhysicalFieldRefusesAnAuthoredDirectionVocabulary() {
        var field = new WorldFieldTopology(
            "field",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            4,
            4
        );
        var definition = Fixtures.BuildDocument() with { StateRaw = new(Lattices: [field]) };
        var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes: WorldDefinitionSerialization.Serialize(definition: definition)))!.AsObject();
        var lattice = node["state"]!["lattices"]!.AsArray()[0]!.AsObject();

        lattice["directions"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["name"] = "east", ["x"] = 1, ["y"] = 0 });

        var exception = Assert.Throws<InvalidDataException>(testCode: () => WorldDefinitionSerialization.Deserialize(utf8Json: System.Text.Encoding.UTF8.GetBytes(s: node.ToJsonString())));

        Assert.IsType<System.Text.Json.JsonException>(@object: exception.InnerException);
    }
    [Fact]
    public void ARenamedBoxVocabularyStillDerivesACorrectClosedOppositeTable() {
        var renamed = new TopologyDirection[] { new(
            Name: "up",
            X: 0,
            Y: 0,
            Z: 1
        ), new(
            Name: "down",
            X: 0,
            Y: 0,
            Z: -1
        ) };
        var topology = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [Box() with { Directions = renamed }]),
            "box"
        )!;

        Assert.Equal(
            2,
            topology.DirectionCount
        );
        Assert.Equal(
            topology.Direction(token: "down"),
            topology.Opposite(direction: topology.Direction(token: "up"))
        );
        Assert.Equal(
            4,
            topology.Neighbour(
                cell: 0,
                direction: topology.Direction(token: "up")
            )
        ); // one layer up: 2x2 footprint.
    }
    [Fact]
    public void ARingRefusesAYStepAndAWrappedAxisRefusesAStepAtOrPastItsExtent() {
        var ring = new LatticeTopology.Ring(
            "ring",
            new DocumentVector3(
                x: 0,
                y: 0,
                z: 0
            ),
            1,
            5
        );

        Assert.False(condition: TopologyCompilation.TryValidate(
            ring with { Directions = [new(
                    "skew",
                    1,
                    1
                )] },
            out var ringReason
        ));
        Assert.Contains(
            actualString: ringReason,
            expectedSubstring: "no second axis"
        );
        Assert.False(condition: TopologyCompilation.TryValidate(
            ring with { Directions = [new(
                    "around",
                    5,
                    0
                ), new(
                    "back",
                    -5,
                    0
                )] },
            out var wideReason
        ));
        Assert.Contains(
            actualString: wideReason,
            expectedSubstring: "magnitude must be under the width"
        );
        // Control: a step under the ring's own width is admitted.
        Assert.True(condition: TopologyCompilation.TryValidate(
            ring with { Directions = [new(
                    "step",
                    4,
                    0
                ), new(
                    "back",
                    -4,
                    0
                )] },
            out _
        ));

        var wrappedGrid = Grid() with { Wrap = TopologyWrap.Both };

        Assert.False(condition: TopologyCompilation.TryValidate(
            wrappedGrid with { Directions = [new(
                    "far",
                    0,
                    4
                ), new(
                    "near",
                    0,
                    -4
                )] },
            out var deepReason
        ));
        Assert.Contains(
            actualString: deepReason,
            expectedSubstring: "magnitude must be under the depth"
        );
    }
    [Fact]
    public void ARuleCompilesAgainstAnAuthoredDirectionAndRefusesTheRetiredDefaultName() {
        var orthogonal = new TopologyDirection[] {
            new(
            "north",
            0,
            -1
        ), new(
            "south",
            0,
            1
        ), new(
            "east",
            1,
            0
        ), new(
            "west",
            -1,
            0
        ),
        };
        var board = new WorldStateRow(
            CellName.Parse(candidate: "board"),
            CellKind.Int,
            Domain: new StateDomain.CellsOf("grid")
        );
        var slot = new WorldStateRow(
            CellName.Parse(candidate: "neighbour"),
            CellKind.Int,
            Cells: [new StateCell(
                    WorldStateRow.SlotKey,
                    CellValue.Int(value: 0L)
                )]
        );

        WorldDefinition Document(string direction) => Fixtures.BuildDocument() with {
            StateRaw = new(
            World: [board, slot],
            Lattices: [Grid() with { Directions = orthogonal }]
        ),
            Rules = [new WorldRule(
                CellName.Parse(candidate: "read"),
                [new ActionEffect.SetState(
                        State: "neighbour",
                        FromState: $"$board:neighbour:board:{direction}",
                        FromKey: "0"
                    )]
            )],
        };

        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: Document(direction: "north"),
                reason: out var authoredReason
            ),
            userMessage: authoredReason
        );
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: Document(direction: "N"),
            reason: out var retiredReason
        ));
        Assert.Contains(
            actualString: retiredReason,
            expectedSubstring: "is not a direction of 'grid'"
        );
    }
    [Fact]
    public void AnAuthoredDirectionListRoundTripsThroughJson() {
        var orthogonal = new TopologyDirection[] { new(
            "east",
            1,
            0
        ), new(
            "west",
            -1,
            0
        ) };
        var definition = Fixtures.BuildDocument() with { StateRaw = new(Lattices: [Grid() with { Directions = orthogonal }]) };
        var restored = WorldDefinitionSerialization.Deserialize(utf8Json: WorldDefinitionSerialization.Serialize(definition: definition));
        var topology = TopologyCompilation.Find(
            restored.StateRaw,
            "grid"
        )!;

        Assert.Equal(
            2,
            topology.DirectionCount
        );
        Assert.Equal(
            -1,
            topology.Direction(token: "N")
        );
        Assert.True(condition: (topology.Direction(token: "east") >= 0));
    }
    [Fact]
    public void AnUnauthoredGridStillCompilesTheEightCompassNames() {
        var topology = TopologyCompilation.Find(
            new WorldStateSection(Lattices: [Grid()]),
            "grid"
        )!;

        Assert.Equal(
            8,
            topology.DirectionCount
        );
        Assert.Equal(
            0,
            topology.Direction(token: "N")
        );
        Assert.Equal(
            7,
            topology.Direction(token: "NW")
        );
        Assert.Equal(
            -1,
            topology.Direction(token: "orthoNorth")
        );
    }
    [Fact]
    public void AnUnclosedOrDuplicateOrMisplacedDirectionListRefuses() {
        var missingOpposite = new TopologyDirection[] { new(
            "east",
            1,
            0
        ) };

        Assert.False(condition: TopologyCompilation.TryValidate(
            Grid() with { Directions = missingOpposite },
            out var noOppositeReason
        ));
        Assert.Contains(
            actualString: noOppositeReason,
            expectedSubstring: "opposite"
        );

        var duplicateName = new TopologyDirection[] { new(
            "east",
            1,
            0
        ), new(
            "east",
            -1,
            0
        ) };

        Assert.False(condition: TopologyCompilation.TryValidate(
            Grid() with { Directions = duplicateName },
            out var duplicateReason
        ));
        Assert.Contains(
            actualString: duplicateReason,
            expectedSubstring: "distinct"
        );

        var zeroStep = new TopologyDirection[] { new(
            "east",
            1,
            0
        ), new(
            "nowhere",
            0,
            0
        ) };

        Assert.False(condition: TopologyCompilation.TryValidate(
            Grid() with { Directions = zeroStep },
            out var zeroReason
        ));
        Assert.Contains(
            actualString: zeroReason,
            expectedSubstring: "zero step"
        );

        var layerOnAGrid = new TopologyDirection[] { new(
            Name: "up",
            X: 0,
            Y: 0,
            Z: 1
        ), new(
            Name: "down",
            X: 0,
            Y: 0,
            Z: -1
        ) };

        Assert.False(condition: TopologyCompilation.TryValidate(
            Grid() with { Directions = layerOnAGrid },
            out var layerReason
        ));
        Assert.Contains(
            actualString: layerReason,
            expectedSubstring: "layer step outside a box"
        );

        // Control: the same four-direction list validates and compiles when it IS closed under negation.
        var closed = new TopologyDirection[] { new(
            "east",
            1,
            0
        ), new(
            "west",
            -1,
            0
        ) };

        Assert.True(condition: TopologyCompilation.TryValidate(
            Grid() with { Directions = closed },
            out _
        ));
    }
}
