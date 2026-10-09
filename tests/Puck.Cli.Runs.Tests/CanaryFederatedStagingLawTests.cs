using System.Text.Json.Nodes;
using Puck.Cli.Canary;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Runs.Tests;

/// <summary>CONTRACT UNDER TEST: a federated leg stages each world it boots into the run's own mirror of the tree the
/// worlds share, and a staged document keeps resolving what its source resolved. A basis or import that names a document
/// outside the mirrored tree, as a canary's own delta over a shipped world does, is rooted where the source's resolves;
/// one inside the tree keeps naming the staged copy, which is the sibling the runner patches.</summary>
public sealed class CanaryFederatedStagingLawTests {
    [Fact]
    public void AStagedDocumentRootsOnlyTheDocumentsItNamesOutsideTheMirror() {
        using var files = new TemporaryDirectory(prefix: "puck-canary-federated-staging-");
        var sourceDirectory = Path.Combine(
            path1: files.RootPath,
            path2: "tests",
            path3: "fixture"
        );
        var mirror = Path.Combine(
            path1: files.RootPath,
            path2: "run",
            path3: "federated-worlds"
        );

        _ = Directory.CreateDirectory(path: sourceDirectory);
        _ = Directory.CreateDirectory(path: mirror);

        var source = Path.Combine(
            path1: sourceDirectory,
            path2: "delta.world.json"
        );
        var staged = Path.Combine(
            path1: mirror,
            path2: "delta.world.json"
        );
        var document = new JsonObject {
            ["basis"] = "../../shipped/worlds/island",
            ["imports"] = new JsonArray(
                new JsonObject { ["document"] = "sibling" },
                new JsonObject { ["document"] = "../../shipped/worlds/modules/arcade" }
            ),
        }.ToJsonString();

        File.WriteAllText(
            contents: document,
            path: source
        );
        File.WriteAllText(
            contents: document,
            path: staged
        );

        CanaryCommand.RootEscapingDocuments(
            mirror: mirror,
            source: source,
            staged: staged
        );

        var written = JsonNode.Parse(json: File.ReadAllText(path: staged))!.AsObject();
        var imports = written["imports"]!.AsArray();

        Assert.Equal(
            actual: written["basis"]!.GetValue<string>(),
            expected: Path.GetFullPath(path: Path.Combine(
                path1: sourceDirectory,
                path2: "../../shipped/worlds/island"
            )).Replace(
                newChar: '/',
                oldChar: '\\'
            )
        );
        Assert.Equal(
            actual: imports[0]!["document"]!.GetValue<string>(),
            expected: "sibling"
        );
        Assert.Equal(
            actual: imports[1]!["document"]!.GetValue<string>(),
            expected: Path.GetFullPath(path: Path.Combine(
                path1: sourceDirectory,
                path2: "../../shipped/worlds/modules/arcade"
            )).Replace(
                newChar: '/',
                oldChar: '\\'
            )
        );
    }
}
