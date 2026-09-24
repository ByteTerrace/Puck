using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Exercises <see cref="CliWorldVocabulary.EnsureInstalled"/> — the CLI process's one vocabulary-hook
/// installer, which every verb that validates or composes a world document goes through.</summary>
public sealed class CliWorldVocabularyTests {
    // An empty content path isolates engine registration from source-format preparation.
    private const string ScreenJsonTemplate = """
        {
          "schema": "puck.world.definition.v1",
          "machines": [
            {
              "name": "brick1",
              "engine": "{ENGINE}",
              "configuration": { "schema": "{SCHEMA}" }
            }
          ]
        }
        """;

    [Fact]
    public void AnUnregisteredScreenMachineEngineIsStillRefused() {
        var catalog = CliWorldVocabulary.EnsureInstalled();

        Assert.False(condition: catalog.IsRegistered(engineId: "no-such-brick"));

        var diagnostics = new DiagnosticBag();
        var root = JsonNode.Parse(ScreenJsonTemplate
            .Replace(comparisonType: StringComparison.Ordinal, newValue: "no-such-brick", oldValue: "{ENGINE}")
            .Replace(comparisonType: StringComparison.Ordinal, newValue: "puck.no-such-brick.configuration.v1", oldValue: "{SCHEMA}")
        )!.AsObject();

        var validated = WorldSemanticValidator.ValidateWorld(
            root,
            sourceMap: null,
            diagnostics: diagnostics,
            machines: catalog
        );

        Assert.False(condition: validated);
        Assert.Contains(
            collection: diagnostics,
            filter: d => d.Message.Contains(
                comparisonType: StringComparison.Ordinal,
                value: "provider 'no-such-brick' is unavailable in the selected machine catalog"
            )
        );
    }
    [InlineData("gaming-brick")]
    [InlineData("advanced-gaming-brick")]
    [Theory]
    public void EnsureInstalledRegistersEveryShippedScreenMachineEngine(string engine) {
        var catalog = CliWorldVocabulary.EnsureInstalled();

        Assert.True(condition: catalog.IsRegistered(engineId: engine));

        var schema = ((engine == "advanced-gaming-brick")
            ? "puck.advanced-gaming-brick.config.v1"
            : $"puck.{engine}.configuration.v1");
        var diagnostics = new DiagnosticBag();
        var root = JsonNode.Parse(ScreenJsonTemplate
            .Replace(comparisonType: StringComparison.Ordinal, newValue: engine, oldValue: "{ENGINE}")
            .Replace(comparisonType: StringComparison.Ordinal, newValue: schema, oldValue: "{SCHEMA}")
        )!.AsObject();

        var validated = WorldSemanticValidator.ValidateWorld(
            root,
            sourceMap: null,
            diagnostics: diagnostics,
            machines: catalog
        );

        Assert.True(
            validated,
            userMessage: string.Join(
                separator: Environment.NewLine,
                values: diagnostics.Select(selector: d => d.Message)
            )
        );
    }
    // With no catalog at all, the machine check belongs to the host that runs the world: validation passes and a
    // PUCK118 information notice names the deferral, so the author sees it without a finding against them.
    [Fact]
    public void SemanticValidationDefersAnUnavailableProviderCatalogAsANotice() {
        _ = CliWorldVocabulary.EnsureInstalled();
        var diagnostics = new DiagnosticBag();
        var root = JsonNode.Parse(ScreenJsonTemplate
            .Replace(comparisonType: StringComparison.Ordinal, newValue: "gaming-brick", oldValue: "{ENGINE}")
            .Replace(comparisonType: StringComparison.Ordinal, newValue: "puck.gaming-brick.configuration.v1", oldValue: "{SCHEMA}")
        )!.AsObject();

        Assert.True(condition: WorldSemanticValidator.ValidateWorld(
            root,
            sourceMap: null,
            diagnostics: diagnostics
        ));
        Assert.False(condition: diagnostics.HasErrors);
        Assert.Contains(
            collection: diagnostics,
            filter: diagnostic => ((diagnostic.Code == PuckDiagnosticCodes.SemanticValidationDeferred)
                && (diagnostic.Severity == DiagnosticSeverity.Information)
                && diagnostic.Message.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: "no machine catalog"
                ))
        );
    }
}
