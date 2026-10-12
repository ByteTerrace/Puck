using System.Text;

using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>Pins how many of <see cref="WorldDefinitionValidator"/>'s real messages already carry a leading
/// <see cref="BrowserErrorPaths"/> path token versus how many are still bare prose, over two fixed documents —
/// the flagship island's machine deferrals, one per machine configuration and one per machine-sourced screen output
/// (a path on every message), and one deliberately
/// minimal admission-shape defect (no path on its one message). A future pass that adds a path token to a message
/// the validator emits today should move one of these two counts, which is the point: this test fails, by design,
/// the moment that sweep touches either fixture's own message, so the sweep's own progress is visible rather than
/// silently absorbed.</summary>
public sealed class ValidatorMessagePathRatchetTests {
    private static IReadOnlyList<BrowserErrorPath> ValidateAndSplitDeferred(byte[] utf8Json) {
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(
            condition: BrowserParser.TryParseAndValidate(
                utf8Json: utf8Json,
                errors: errors,
                deferred: deferred,
                definition: out _
            ),
            userMessage: string.Join(
                separator: "; ",
                values: errors
            )
        );

        return [.. deferred.Select(selector: BrowserErrorPaths.Split)];
    }
    [Fact]
    public void Composed_puck_world_admits_and_defers_a_path_on_every_machine_engine_message() {
        var path = RepositoryPaths.Resolve(relativePath: "src/Puck.World/Assets/worlds/puck.world.json");

        Assert.True(
            condition: WorldDefinitionFileSource.TryComposeDocumentTree(
                path: path,
                tree: out var tree,
                reason: out var reason
            ),
            userMessage: reason
        );

        var split = ValidateAndSplitDeferred(utf8Json: Encoding.UTF8.GetBytes(s: tree!.ToJsonString()));
        var machines = tree["machines"]!.AsArray().Count;
        var machineScreens = tree["screens"]!.AsArray().Count(predicate: screen => (((string?)screen?["source"]?["$type"]) == "machine"));

        Assert.NotEqual(
            actual: machines,
            expected: 0
        );
        Assert.Equal(
            expected: (machines + machineScreens),
            actual: split.Count
        );
        Assert.All(
            collection: split,
            action: error => Assert.NotNull(@object: error.Path)
        );
    }
}
