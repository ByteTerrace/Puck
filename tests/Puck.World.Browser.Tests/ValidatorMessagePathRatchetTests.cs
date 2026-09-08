using System.Text;

using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>Pins how many of <see cref="WorldDefinitionValidator"/>'s real messages already carry a leading
/// <see cref="BrowserErrorPaths"/> path token versus how many are still bare prose, over two fixed documents —
/// the flagship island's own three screen-machine-engine deferrals (a path on every message) and one deliberately
/// minimal admission-shape defect (no path on its one message). A future pass that adds a path token to a message
/// the validator emits today should move one of these two counts, which is the point: this test fails, by design,
/// the moment that sweep touches either fixture's own message, so the sweep's own progress is visible rather than
/// silently absorbed.</summary>
public sealed class ValidatorMessagePathRatchetTests {
    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    private static IReadOnlyList<BrowserErrorPath> ValidateAndSplitErrors(byte[] utf8Json) {
        var errors = new List<string>();
        var deferred = new List<string>();

        BrowserParser.TryParseAndValidate(utf8Json: utf8Json, errors: errors, deferred: deferred, definition: out _, compilation: out _);

        return [.. errors.Select(selector: BrowserErrorPaths.Split)];
    }
    private static IReadOnlyList<BrowserErrorPath> ValidateAndSplitDeferred(byte[] utf8Json) {
        var errors = new List<string>();
        var deferred = new List<string>();

        Assert.True(condition: BrowserParser.TryParseAndValidate(utf8Json: utf8Json, errors: errors, deferred: deferred, definition: out _, compilation: out _), userMessage: string.Join(separator: "; ", values: errors));

        return [.. deferred.Select(selector: BrowserErrorPaths.Split)];
    }

    [Fact]
    public void Composed_puck_world_admits_and_defers_a_path_on_every_machine_engine_message() {
        var path = Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "puck.world.json");

        Assert.True(condition: WorldDefinitionFileSource.TryComposeDocumentTree(path: path, tree: out var tree, reason: out var reason), userMessage: reason);

        var split = ValidateAndSplitDeferred(utf8Json: Encoding.UTF8.GetBytes(s: tree!.ToJsonString()));

        Assert.Equal(expected: 3, actual: split.Count);
        Assert.All(collection: split, action: error => Assert.NotNull(@object: error.Path));
    }
    [Fact]
    public void Nameless_addon_refusal_carries_no_path() {
        const string Json = /*lang=json*/ """
            { "schema": "puck.world.def.v1", "addons": [{ "name": "", "modulePath": "m", "hash": "sha256-64/0000000000000000", "fuel": 1000, "enabled": true }] }
            """;

        var split = ValidateAndSplitErrors(utf8Json: Encoding.UTF8.GetBytes(s: Json));

        var error = Assert.Single(collection: split);

        Assert.Null(@object: error.Path);
        Assert.Equal(expected: "an addon requires a name.", actual: error.Message);
    }
}
