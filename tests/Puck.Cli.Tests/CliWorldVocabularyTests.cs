using System.Text.Json.Nodes;
using Puck.World;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Exercises <see cref="CliWorldVocabulary.EnsureInstalled"/> — the CLI process's one vocabulary-hook
/// installer, which every verb that validates or composes a world document goes through.</summary>
/// <remarks><see cref="WorldScreenMachineEngines"/> and the schema vocabulary hooks are process-global and never
/// un-register, so these assertions are written to hold whatever else in the assembly ran first.</remarks>
public sealed class CliWorldVocabularyTests {
    // A passive machine screen with an empty contentPath skips the cartridge-compiler check entirely (only a
    // cartridge document path — a ".cartridge.json" suffix — triggers it), isolating this document to exercising
    // WorldExtensionVocabularyHook.ScreenMachineEngineCheck alone.
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
        CliWorldVocabulary.EnsureInstalled();

        Assert.True(WorldScreenMachineEngines.IsRegistered(engine));

        var diagnostics = new DiagnosticBag();
        var root = JsonNode.Parse(ScreenJsonTemplate.Replace("{ENGINE}", engine, StringComparison.Ordinal))!.AsObject();

        var validated = WorldSemanticValidator.ValidateWorld(root, sourceMap: null, diagnostics: diagnostics);

        Assert.True(validated, userMessage: string.Join(separator: Environment.NewLine, values: diagnostics.Select(selector: d => d.Message)));
    }

    // The refusal half: with the hooks installed, an engine nobody registered is still rejected, so a passing
    // registration assertion above cannot be an inert check that accepts anything.
    [Fact]
    public void AnUnregisteredScreenMachineEngineIsStillRefused() {
        CliWorldVocabulary.EnsureInstalled();

        Assert.False(WorldScreenMachineEngines.IsRegistered("no-such-brick"));

        var diagnostics = new DiagnosticBag();
        var root = JsonNode.Parse(ScreenJsonTemplate.Replace("{ENGINE}", "no-such-brick", StringComparison.Ordinal))!.AsObject();

        var validated = WorldSemanticValidator.ValidateWorld(root, sourceMap: null, diagnostics: diagnostics);

        Assert.False(validated);
        Assert.Contains(diagnostics, d => d.Message.Contains("'no-such-brick' names no registered screen-machine engine", StringComparison.Ordinal));
    }
}
