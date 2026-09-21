using System.Text.Json.Nodes;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Exercises <see cref="WorldSemanticValidator.ValidateComposedWorld"/> — the entry point that composes a
/// document's <c>basis</c>/<c>imports</c> graph before validating it, in contrast to
/// <see cref="WorldSemanticValidator.ValidateWorld"/>, which validates exactly the JSON it is given.</summary>
public sealed class ValidateComposedWorldTests : IDisposable {
    private const string BasisJson = """
        {
          "placements": {
            "rows": [
              { "id": "rock", "prototypeId": "rockProto", "scale": 1 }
            ]
          }
        }
        """;
    // The root placement row deliberately omits prototypeId and scale; standard.basis.json-style composition
    // supplies them by merging a same-id basis row over it. Reproduces the exact shape of the bug this entry point
    // fixes: WorldPlacement is a required-field record, so deserializing the root JSON alone throws before any
    // semantic check runs.
    private const string RootJson = """
        {
          "schema": "puck.world.definition.v1",
          "basis": "basis.json",
          "placements": {
            "rows": [
              { "id": "rock", "position": [0, 0, 0], "yawDegrees": 0 }
            ]
          }
        }
        """;

    private readonly string m_directory = Directory.CreateDirectory(path: Path.Combine(
        path1: Path.GetTempPath(),
        path2: ("puck-validate-composed-tests-" + Guid.NewGuid().ToString(format: "N"))
    )).FullName;

    public void Dispose() => Directory.Delete(
        path: m_directory,
        recursive: true
    );
    [Fact]
    public void ValidateComposedWorld_ComposesBasisBeforeValidating() {
        File.WriteAllText(
            Path.Combine(
                path1: m_directory,
                path2: "basis.json"
            ),
            BasisJson
        );
        var rootPath = Path.Combine(
            path1: m_directory,
            path2: "root.world.json"
        );

        var root = JsonNode.Parse(RootJson)!.AsObject();
        var diagnostics = new DiagnosticBag();

        WorldSemanticValidator.ValidateComposedWorld(
            root,
            sourceMap: null,
            diagnostics: diagnostics,
            sourcePath: rootPath
        );

        // The basis merge supplied prototypeId/scale, so the root's own required-field failure (PUCK031) is gone —
        // composition ran before validation, unlike ValidateWorld above on the identical uncomposed root.
        Assert.DoesNotContain(
            collection: diagnostics,
            filter: d => (d.Code == "PUCK031")
        );
    }
    [Fact]
    public void ValidateComposedWorld_WithNoBasisOrImports_ValidatesTheDocumentAsGiven() {
        const string FlatJson = """{ "schema": "puck.world.definition.v1" }""";
        var root = JsonNode.Parse(FlatJson)!.AsObject();
        var diagnostics = new DiagnosticBag();
        var rootPath = Path.Combine(
            path1: m_directory,
            path2: "flat.world.json"
        );

        var result = WorldSemanticValidator.ValidateComposedWorld(
            root,
            sourceMap: null,
            diagnostics: diagnostics,
            sourcePath: rootPath
        );

        Assert.True(condition: result);
        Assert.False(condition: diagnostics.HasErrors);
    }
    [Fact]
    public void MultiWorldDiagnosticsComposeEachBasisBeforeValidation() {
        File.WriteAllText(Path.Combine(path1: m_directory, path2: "basis.json"), BasisJson);
        const string Source = """
                module room() {
                    basis: "basis.json"
                    placements { rows [ { id: "rock" position [0, 0, 0] yawDegrees: 0 } ] }
                }
                world first = room()
                world second = room()
                """;
        var sourcePath = Path.Combine(path1: m_directory, path2: "composition.puck");
        var compiled = WorldCompiler.Compile(Source, sourcePath: sourcePath, allowMultiple: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(condition: compiled.Success, userMessage: string.Join(separator: "\n", values: compiled.Diagnostics));
        var diagnostics = WorldSourceDiagnostics.Diagnose(source: Source, sourcePath: sourcePath, cancellationToken: TestContext.Current.CancellationToken);

        // As for an ordinary root, basis rows supply required placement fields before deserialization.
        Assert.DoesNotContain(collection: diagnostics, filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.SchemaRejected));
        Assert.DoesNotContain(collection: diagnostics, filter: static diagnostic => (diagnostic.Code == PuckDiagnosticCodes.CompositionRefused));
    }
    [Fact]
    public void ValidateWorld_OnUncomposedRootWithBasis_FailsWithMissingRequiredProperties() {
        var root = JsonNode.Parse(RootJson)!.AsObject();
        var diagnostics = new DiagnosticBag();

        var result = WorldSemanticValidator.ValidateWorld(
            root,
            sourceMap: null,
            diagnostics: diagnostics
        );

        Assert.False(condition: result);
        Assert.True(condition: diagnostics.HasErrors);
        Assert.Contains(
            collection: diagnostics,
            filter: d => (d.Code == "PUCK031")
        );
    }
}
