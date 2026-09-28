using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>The load gate binds a row's parameters against its source: a parameter naming a scalar config field of a
/// pass the source declares installs, and one naming a field the pass does not declare, or binding an array to a row
/// that cannot fill it, is refused as <see cref="WorldPipelineOverrideRefusal.ParameterUnbound"/>.</summary>
public sealed partial class PipelineOverrideLawTests {
    private static WorldDefinition WithParameter(WorldDefinition definition, string field) => (definition with {
        ViewsRaw = (definition.Views with {
            Graphs = [.. (definition.Views.Graphs ?? []).Select(selector: row => ((row.Name == "left")
                ? (row with {
                    Overrides = null,
                    Parameters = new Dictionary<string, IReadOnlyDictionary<string, BindableScalar>>(comparer: StringComparer.Ordinal) {
                        ["visualize"] = new Dictionary<string, BindableScalar>(comparer: StringComparer.Ordinal) {
                            [field] = new BindableScalar(literal: 2f),
                        },
                    },
                })
                : row))],
        }),
    });

    // A board pass declaring a uint config field, four-element int, uint and float arrays, and an eight-element float
    // array.
    private const string BoardGraph = """
        {
          "$schema": "puck.render.graph.v1",
          "name": "board",
          "resources": [
            { "name": "image", "kind": "Image", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Relative", "width": 1, "height": 1 } }
          ],
          "passes": [
            {
              "name": "draw",
              "source": "board.hlsl",
              "entryPoint": "main",
              "kind": "Compute",
              "outputs": [{ "name": "image" }],
              "config": { "level": { "type": "uint", "default": 0 } },
              "arrays": { "tiles": { "type": "int", "length": 4 }, "marks": { "type": "uint", "length": 4 }, "heights": { "type": "float", "length": 4 }, "column": { "type": "float", "length": 8 } }
            }
          ],
          "outputs": ["image"]
        }
        """;

    // Keyed rows of four cells, bounded in the int range, unbounded, holding Fixed values and one bounded below zero; a
    // keyed row of eight; and a keyless scalar row.
    private static WorldDefinition WithBoardRows(WorldDefinition definition) {
        static WorldStateRow Keyed(string name, CellKind kind, long? min, long? max, int capacity) => new(
            Name: CellName.Parse(candidate: name),
            Kind: kind,
            Min: min,
            Max: max,
            Capacity: capacity,
            Domain: StateDomain.Keys.Instance,
            Cells: []
        );

        return definition.WithWorldState(rows: [
            Keyed(capacity: 4, kind: CellKind.Int, max: 255L, min: 0L, name: "tiles"),
            Keyed(capacity: 4, kind: CellKind.Int, max: null, min: null, name: "loose"),
            Keyed(capacity: 4, kind: CellKind.Int, max: 5L, min: -1L, name: "signed"),
            Keyed(capacity: 4, kind: CellKind.Fixed, max: null, min: null, name: "fixed"),
            Keyed(capacity: 8, kind: CellKind.Int, max: 255L, min: 0L, name: "wide"),
            new WorldStateRow(
                Name: CellName.Parse(candidate: "score"),
                Kind: CellKind.Int,
                Min: 0,
                Max: 9,
                Cells: [new StateCell(Key: WorldStateRow.SlotKey, Value: CellValue.Int(value: 1))]
            ),
        ]);
    }
    // The document with its left row drawing the board, binding one member of its draw pass.
    private WorldDefinition WithBoardParameter(WorldDefinition definition, string field, BindableScalar value) {
        File.WriteAllText(
            contents: BoardGraph,
            path: Path.Combine(
                path1: m_directory,
                path2: "board.graph.json"
            )
        );

        var rows = WithBoardRows(definition: definition);

        return (rows with {
            ViewsRaw = (rows.Views with {
                Graphs = [.. (rows.Views.Graphs ?? []).Select(selector: row => ((row.Name == "left")
                    ? (row with {
                        Overrides = null,
                        Parameters = new Dictionary<string, IReadOnlyDictionary<string, BindableScalar>>(comparer: StringComparer.Ordinal) {
                            ["draw"] = new Dictionary<string, BindableScalar>(comparer: StringComparer.Ordinal) {
                                [field] = value,
                            },
                        },
                        Source = "board.graph.json",
                    })
                    : row))],
            }),
        });
    }

    /// <summary>The load gate's array refusals, each against a control binding the same member to a row that fits it:
    /// an array bound to a literal, to a row that is not keyed, to a row longer than the array, to an Int row without
    /// bounds or with bounds outside the element's range, or to a Fixed row for an integer element; and a scalar field
    /// bound to a keyed row with no key. Each is refused as <see cref="WorldPipelineOverrideRefusal.ParameterUnbound"/>
    /// naming why.</summary>
    [InlineData("tiles", "2", "an array binds a whole row")]
    [InlineData("tiles", "state.score", "'score' names no keyed state row")]
    [InlineData("tiles", "state.wide", "row 'wide' presents 8 elements and the array holds 4")]
    [InlineData("tiles", "state.loose", "row 'loose' holds Int values bounded [-, -], which Int elements cannot hold exactly")]
    [InlineData("marks", "state.signed", "row 'signed' holds Int values bounded [-1, 5], which Uint elements cannot hold exactly")]
    [InlineData("tiles", "state.fixed", "row 'fixed' holds Fixed values, which Int elements cannot hold exactly")]
    [InlineData("level", "state.tiles", "'tiles' is a keyed row, and a scalar field reads one cell")]
    [Theory]
    public void AWorldLoadRefusesAnArrayBindingItsRowCannotFill(string field, string binding, string reason) {
        using var fixture = Server();

        static BindableScalar Value(string text) => (double.TryParse(
            s: text,
            provider: System.Globalization.CultureInfo.InvariantCulture,
            result: out var literal
        )
            ? new BindableScalar(literal: ((float)literal))
            : new BindableScalar(binding: text));

        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.parameter-array",
            controlOutcome: () => Load(
                candidate: WithBoardParameter(
                    definition: fixture.Server.Definition,
                    field: field,
                    value: Value(text: ((field == "level")
                        ? "state.score"
                        : "state.tiles"))
                ),
                fixture: fixture
            ),
            deniedOutcome: () => {
                var accepted = Load(
                    candidate: WithBoardParameter(
                        definition: fixture.Server.Definition,
                        field: field,
                        value: Value(text: binding)
                    ),
                    fixture: fixture
                );

                AssertLastRefusal(refusal: nameof(WorldPipelineOverrideRefusal.ParameterUnbound));
                Assert.Contains(
                    actualString: m_echoes[^1].Message,
                    expectedSubstring: reason
                );

                return accepted;
            }
        );
    }
    /// <summary>A field row binds an array like any keyed row, one element per cell of the field lattice: a four-by-two
    /// lattice's field loads bound to an eight-element float array, and is refused bound to a four-element one as
    /// <see cref="WorldPipelineOverrideRefusal.ParameterUnbound"/> naming the eight elements it presents.</summary>
    [Fact]
    public void AWorldLoadBindsAFieldRowToAnArrayOnlyAsLongAsItsLattice() {
        var lattice = WorldFieldsSection.ToStateSection(composite: new WorldFieldsSection(
            Lattice: new WorldFieldLatticeDefinition(
                Origin: new Puck.Assets.Documents.DocumentVector3(x: 0f, y: 0f, z: 0f),
                CellSize: 1f,
                Width: 4,
                Depth: 2
            ),
            Fields: [new WorldFieldRow(Name: "heat", Max: 4f)]
        ));

        // The live field lattice is fixed at boot, so the server boots with it and every candidate keeps it.
        WorldDefinition WithField(WorldDefinition definition) => (definition with {
            StateRaw = ((definition.StateRaw ?? new WorldStateSection()) with {
                Lattices = lattice.Lattices,
                World = [.. definition.AuthoredState.Where(predicate: static row => (row.Field is null)), .. (lattice.World ?? [])],
            }),
        });

        using var fixture = Fixtures.FreshServer(definition: WithField(definition: Document()));

        fixture.Server.PipelineSources = new WorldPipelineSources(documentDirectory: m_directory);
        fixture.Server.EchoTap = m_echoes.Add;

        WorldDefinition Binding(string array) => WithField(definition: WithBoardParameter(
            definition: fixture.Server.Definition,
            field: array,
            value: new BindableScalar(binding: "state.heat")
        ));

        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.parameter-field-row",
            controlOutcome: () => Load(
                candidate: Binding(array: "column"),
                fixture: fixture
            ),
            deniedOutcome: () => {
                var accepted = Load(
                    candidate: Binding(array: "heights"),
                    fixture: fixture
                );

                AssertLastRefusal(refusal: nameof(WorldPipelineOverrideRefusal.ParameterUnbound));
                Assert.Contains(
                    actualString: m_echoes[^1].Message,
                    expectedSubstring: "row 'heat' presents 8 elements and the array holds 4"
                );

                return accepted;
            }
        );
    }
    [Fact]
    public void AWorldLoadBindsItsParametersAndRefusesAFieldThePassDoesNotDeclare() {
        using var fixture = Server();

        Laws.RefusalWithControl(
            lawId: "pipeline.overrides.parameter-unbound",
            controlOutcome: () => Load(
                candidate: WithParameter(definition: fixture.Server.Definition, field: "exposure"),
                fixture: fixture
            ),
            deniedOutcome: () => {
                var accepted = Load(
                    candidate: WithParameter(definition: fixture.Server.Definition, field: "brightness"),
                    fixture: fixture
                );

                AssertLastRefusal(refusal: nameof(WorldPipelineOverrideRefusal.ParameterUnbound));

                return accepted;
            }
        );
    }
}
