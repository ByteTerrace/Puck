using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Exercises <see cref="WorldSemanticValidator.ValidateComposedWorld"/> — the entry point that composes a
/// document's <c>basis</c>/<c>imports</c> graph before validating it, in contrast to
/// <see cref="WorldSemanticValidator.ValidateWorld"/>, which validates exactly the JSON it is given.</summary>
public sealed class ValidateComposedWorldTests : IDisposable {
    private readonly string m_directory = Directory.CreateDirectory(
        path: Path.Combine(Path.GetTempPath(), "puck-validate-composed-tests-" + Guid.NewGuid().ToString("N"))
    ).FullName;

    public void Dispose() => Directory.Delete(path: m_directory, recursive: true);

    // The root placement row deliberately omits prototypeId and scale; standard.basis.json-style composition
    // supplies them by merging a same-id basis row over it. Reproduces the exact shape of the bug this entry point
    // fixes: WorldPlacement is a required-field record, so deserializing the root JSON alone throws before any
    // semantic check runs.
    private const string RootJson = """
        {
          "schema": "puck.world.def.v1",
          "basis": "basis.json",
          "placements": {
            "rows": [
              { "id": "rock", "position": [0, 0, 0], "yawDegrees": 0 }
            ]
          }
        }
        """;

    private const string BasisJson = """
        {
          "placements": {
            "rows": [
              { "id": "rock", "prototypeId": "rockProto", "scale": 1 }
            ]
          }
        }
        """;

    [Fact]
    public void ValidateWorld_OnUncomposedRootWithBasis_FailsWithMissingRequiredProperties() {
        var root = JsonNode.Parse(RootJson)!.AsObject();
        var diagnostics = new DiagnosticBag();

        var result = WorldSemanticValidator.ValidateWorld(root, sourceMap: null, diagnostics: diagnostics);

        Assert.False(result);
        Assert.True(diagnostics.HasErrors);
        Assert.Contains(diagnostics, d => d.Code == "PUCK031");
    }

    [Fact]
    public void ValidateComposedWorld_ComposesBasisBeforeValidating() {
        File.WriteAllText(Path.Combine(m_directory, "basis.json"), BasisJson);
        var rootPath = Path.Combine(m_directory, "root.world.json");

        var root = JsonNode.Parse(RootJson)!.AsObject();
        var diagnostics = new DiagnosticBag();

        WorldSemanticValidator.ValidateComposedWorld(root, sourceMap: null, diagnostics: diagnostics, sourcePath: rootPath);

        // The basis merge supplied prototypeId/scale, so the root's own required-field failure (PUCK031) is gone —
        // composition ran before validation, unlike ValidateWorld above on the identical uncomposed root.
        Assert.DoesNotContain(diagnostics, d => d.Code == "PUCK031");
    }

    [Fact]
    public void ValidateComposedWorld_WithNoBasisOrImports_ValidatesTheDocumentAsGiven() {
        const string flatJson = """{ "schema": "puck.world.def.v1" }""";
        var root = JsonNode.Parse(flatJson)!.AsObject();
        var diagnostics = new DiagnosticBag();
        var rootPath = Path.Combine(m_directory, "flat.world.json");

        var result = WorldSemanticValidator.ValidateComposedWorld(root, sourceMap: null, diagnostics: diagnostics, sourcePath: rootPath);

        Assert.True(result);
        Assert.False(diagnostics.HasErrors);
    }
}
