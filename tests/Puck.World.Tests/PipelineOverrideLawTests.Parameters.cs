using Puck.World.Server;

using Xunit;

namespace Puck.World.Tests;

/// <summary>The load gate binds a row's parameters against its source: a parameter naming a scalar config field of a
/// pass the source declares installs, and one naming a field the pass does not declare is refused as
/// <see cref="WorldPipelineOverrideRefusal.ParameterUnbound"/>.</summary>
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
