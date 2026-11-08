using System.Text;
using System.Text.Json;
using Puck.Testing;
using Puck.Transpiler.Diagnostics;
using Puck.World.Authoring;
using Puck.World.Transpiler.Validation;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>A document composed over a basis refines one shape of the basis's creation by its name, carries only the
/// members it writes, and a shape list that cannot tell its rows apart refuses instead of replacing the basis's.</summary>
public sealed class ShapeRefinementLawTests {
    private const string Creation = "      name: \"thing\"\n      schema: \"puck.creation.v1\"\n      palette [\n        { color: \"#9AA0A8\", roughness: 0.6, specular: 0.04, emissive: 0 }\n      ]\n";

    private static (JsonDocument Composed, DiagnosticBag Diagnostics) Compose(TemporaryDirectory files, string basis, string child) {
        _ = files.WriteText(name: "base.puck", text: (WorldSources.Header + basis));

        var path = files.WriteText(name: "child.puck", text: child);
        var compiled = WorldCompiler.Compile(cancellationToken: TestContext.Current.CancellationToken, source: child, sourcePath: path);

        Assert.False(condition: compiled.Diagnostics.HasErrors, userMessage: compiled.Diagnostics.FormatReport(child));

        var diagnostics = new DiagnosticBag();
        var composed = WorldSemanticValidator.TryComposeWorld(
            composed: out var tree,
            diagnostics: diagnostics,
            loweredJson: compiled.RequireJson(),
            sourceMap: compiled.SourceMap,
            sourcePath: path
        );

        return (composed ? JsonDocument.Parse(json: tree.ToJsonString()) : null!, diagnostics);
    }
    private static CreationDocument Read(JsonDocument composed) {
        var document = composed.RootElement.GetProperty(propertyName: "prototypes")[0].GetProperty(propertyName: "document");
        var reader = new Utf8JsonReader(jsonData: Encoding.UTF8.GetBytes(s: document.GetRawText()));

        return CreationDocumentJson.Read(reader: ref reader)!;
    }

    // The refinement names its target and one member. The basis's position, material and id stand, and the shape does
    // not move to the origin or replace the shape at its own list position.
    [Fact]
    public void ARefinementNamingOnlyTheScaleLeavesTheRestOfTheBasisShape() {
        using var files = new TemporaryDirectory();

        var (composed, _) = Compose(
            basis: $"prototypes {{\n  prototype \"thing\" {{\n    document {{\n{Creation}      shape Capsule \"body\" {{\n        position [0, 0.3, 0]\n        scale [0.3, 2.2, 0.3]\n      }}\n      shape Box \"visor\" {{\n        material: 1\n        position [0, 1.5, 0.27]\n        scale [0.2, 0.06, 0.05]\n      }}\n    }}\n  }}\n}}\n",
            child: "basis: \"base\"\n\nprototypes {\n  prototype \"thing\" {\n    document {\n      shape Box \"visor\" { scale [0.4, 0.06, 0.05] }\n      shape Sphere \"knob\" { scale [0.1, 0.1, 0.1] }\n    }\n  }\n}\n",
            files: files
        );
        var shapes = Read(composed: composed).Shapes!;

        Assert.Equal(expected: ["body", "visor", "knob"], actual: shapes.Select(selector: static shape => shape.Name!.Value));
        Assert.Equal(expected: [0, 1, 2], actual: shapes.Select(selector: static shape => shape.Id));
        Assert.Equal(expected: SignedDistance.SdfSolidPrimitive.Capsule, actual: shapes[0].Type);
        Assert.Equal(expected: 1, actual: shapes[1].Material);
        Assert.Equal(expected: 1.5f, actual: shapes[1].Position.Y);
        Assert.Equal(expected: 0.4f, actual: shapes[1].Scale.X);
    }
    // Two lists whose rows cannot be told apart would replace the basis's shapes without a word.
    [Fact]
    public void ShapesWithoutAnIdentityRefuseToReplaceTheBasisCreation() {
        using var files = new TemporaryDirectory();

        var (composed, diagnostics) = Compose(
            basis: $"prototypes {{\n  prototype \"thing\" {{\n    document {{\n{Creation}      shape Box {{ scale [1, 1, 1] }}\n      shape Box {{ scale [2, 2, 2] }}\n    }}\n  }}\n}}\n",
            child: "basis: \"base\"\n\nprototypes {\n  prototype \"thing\" {\n    document {\n      shape Box \"a\" { scale [3, 3, 3] }\n    }\n  }\n}\n",
            files: files
        );

        Assert.Null(@object: composed);
        Assert.Contains("prototypes[id=\"thing\"].document.shapes cannot refine the basis creation's shapes", diagnostics.FormatReport());
    }
}
