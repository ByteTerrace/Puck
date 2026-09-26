using System.Text.Json;
using Xunit;

using Puck.World.Client;

namespace Puck.World.Tests;

/// <summary>
/// Laws for a <c>views.graphs</c> row's bound parameters: a field a row both binds and overrides is refused naming the
/// row, the pass and the field, beside a control binding a different field; a parameter on a package row and a value
/// that is not a finite number or a state token are refused the same way; and a bound token joins the presentation
/// manifest, so its mirror slot is registered at install and a frame only looks it up.
/// </summary>
public sealed class WorldViewGraphParameterLawTests {
    private static WorldDefinition Document(params WorldViewGraph[] graphs) {
        var document = Fixtures.BuildDocument();

        return (document with { ViewsRaw = (document.Views with { Graphs = graphs }) });
    }
    private static WorldViewGraph Row(IReadOnlyDictionary<string, IReadOnlyDictionary<string, BindableScalar>>? parameters, string? overrides = null) => new(
        Name: "board",
        Overrides: ((overrides is null)
            ? null
            : new Dictionary<string, JsonElement>(comparer: StringComparer.Ordinal) {
                ["draw"] = JsonDocument.Parse(json: overrides).RootElement.Clone(),
            }),
        Parameters: parameters,
        Source: "graphs/board.graph.json"
    );
    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, BindableScalar>> Bind(string field, BindableScalar value) => new Dictionary<string, IReadOnlyDictionary<string, BindableScalar>>(comparer: StringComparer.Ordinal) {
        ["draw"] = new Dictionary<string, BindableScalar>(comparer: StringComparer.Ordinal) {
            [field] = value,
        },
    };
    private static void Refuses(WorldDefinition denied, WorldDefinition control, string expected) {
        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: denied,
            reason: out var reason
        ));
        Assert.Contains(
            actualString: reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: expected
        );
        Assert.True(
            condition: WorldDefinitionValidator.TryValidateLocally(
                definition: control,
                reason: out var controlReason
            ),
            userMessage: controlReason
        );
    }

    [Fact]
    public void AFieldBothBoundAndOverriddenIsRefusedNamingTheRowThePassAndTheField() => Refuses(
        control: Document(Row(
            overrides: "{\"exposure\":0.5}",
            parameters: Bind(
                field: "level",
                value: new BindableScalar(literal: 2f)
            )
        )),
        denied: Document(Row(
            overrides: "{\"level\":0.5}",
            parameters: Bind(
                field: "level",
                value: new BindableScalar(literal: 2f)
            )
        )),
        expected: "graph 'board' binds pass 'draw' field 'level' and also overrides it"
    );
    [Fact]
    public void AParameterOnAPackageRowIsRefused() => Refuses(
        control: Document(new WorldViewGraph(
            Name: "world2",
            Package: "sdf.world"
        )),
        denied: Document(new WorldViewGraph(
            Name: "world2",
            Package: "sdf.world",
            Parameters: Bind(
                field: "level",
                value: new BindableScalar(literal: 1f)
            )
        )),
        expected: "takes no timeScale, output, overrides or parameters"
    );
    [Fact]
    public void ABindingNamingNoCellIsRefusedAndALiteralIsNot() => Refuses(
        control: Document(Row(parameters: Bind(
            field: "level",
            value: new BindableScalar(literal: 0.25f)
        ))),
        denied: Document(Row(parameters: Bind(
            field: "level",
            value: new BindableScalar(binding: "state.noSuchRow")
        ))),
        expected: $"views.graphs[0].parameters.draw.level {BindableScalar.Grammar}"
    );
    [Fact]
    public void ABoundTokenJoinsThePresentationManifest() {
        var document = Document(Row(parameters: Bind(
            field: "level",
            value: new BindableScalar(binding: "state.cisternLevel.$target")
        )));
        var manifest = WorldPresentationManifest.Of(definition: document);

        Assert.Contains(
            collection: manifest.Bindings.ToArray(),
            filter: static binding => (
                (binding.Binding == new StateBinding(Key: null, Row: "cisternLevel", Target: true)) &&
                (binding.Conversion == WorldStateConversion.Number)
            )
        );
    }
}
