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
          "schema": "puck.world.def.v1",
          "screens": [
            {
              "index": 0,
              "origin": [0, 0, 0],
              "right": [1, 0, 0],
              "up": [0, 1, 0],
              "halfWidth": 0.3,
              "halfHeight": 0.3,
              "halfDepth": 0.03,
              "round": 0,
              "source": { "$type": "machine", "engine": "{ENGINE}", "contentPath": "", "options": null },
              "route": { "engageable": false, "engageRadius": 0 }
            }
          ]
        }
        """;

    [Theory]
    [InlineData("gaming-brick")]
    [InlineData("advanced-gaming-brick")]
    public void EnsureInstalledRegistersEveryShippedScreenMachineEngine(string engine) {
        var catalog = CliWorldVocabulary.EnsureInstalled();

        Assert.True(catalog.IsRegistered(engine));

        var diagnostics = new DiagnosticBag();
        var root = JsonNode.Parse(ScreenJsonTemplate.Replace("{ENGINE}", engine, StringComparison.Ordinal))!.AsObject();

        var validated = WorldSemanticValidator.ValidateWorld(root, sourceMap: null, diagnostics: diagnostics, machines: catalog);

        Assert.True(validated, userMessage: string.Join(separator: Environment.NewLine, values: diagnostics.Select(selector: d => d.Message)));
    }

    // The refusal half: with the hooks installed, an engine nobody registered is still rejected, so a passing
    // registration assertion above cannot be an inert check that accepts anything.
    [Fact]
    public void SemanticValidationReportsAnUnavailableProviderCatalog() {
        _ = CliWorldVocabulary.EnsureInstalled();
        var diagnostics = new DiagnosticBag();
        var root = JsonNode.Parse(ScreenJsonTemplate.Replace("{ENGINE}", "gaming-brick", StringComparison.Ordinal))!.AsObject();

        Assert.False(WorldSemanticValidator.ValidateWorld(root, sourceMap: null, diagnostics: diagnostics));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Message.Contains("no machine catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnregisteredScreenMachineEngineIsStillRefused() {
        var catalog = CliWorldVocabulary.EnsureInstalled();

        Assert.False(catalog.IsRegistered("no-such-brick"));

        var diagnostics = new DiagnosticBag();
        var root = JsonNode.Parse(ScreenJsonTemplate.Replace("{ENGINE}", "no-such-brick", StringComparison.Ordinal))!.AsObject();

        var validated = WorldSemanticValidator.ValidateWorld(root, sourceMap: null, diagnostics: diagnostics, machines: catalog);

        Assert.False(validated);
        Assert.Contains(diagnostics, d => d.Message.Contains("'no-such-brick' names no registered screen-machine engine", StringComparison.Ordinal));
    }
}
