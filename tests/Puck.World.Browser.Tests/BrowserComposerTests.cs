using System.Text;
using System.Text.Json.Nodes;

using Xunit;

namespace Puck.World.Browser.Tests;

/// <summary>Exercises <see cref="BrowserComposer"/> — the resolver-backed <c>ComposeTree</c> core — over the real
/// shipped worlds directory loaded entirely into memory, so the whole graph composes exactly as a directory load
/// would with no filesystem underneath.</summary>
public sealed class BrowserComposerTests {
    private static string RepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while ((directory is not null) && !File.Exists(path: Path.Combine(path1: directory.FullName, path2: "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(@object: directory);

        return directory!.FullName;
    }
    // Every *.json file under the worlds directory, keyed by its forward-slash path relative to that directory —
    // the same worlds-relative name shape "puck.world.json"/"games/tictactoe.world.json" the real imports author.
    private static Dictionary<string, byte[]> WorldsDirectoryDocuments() {
        var root = Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds");
        var result = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(path: root, searchPattern: "*.json", searchOption: SearchOption.AllDirectories)) {
            var relative = Path.GetRelativePath(relativeTo: root, path: file).Replace(oldChar: '\\', newChar: '/');

            result[relative] = File.ReadAllBytes(path: file);
        }

        return result;
    }
    // A copy of tictactoe.world.json whose rules array carries the same rule name twice. "rules" (unlike
    // state.world, which every game/module contributes to and standard.basis.json itself already declares) is a
    // member standard.basis.json never declares, so composing tictactoe as the SOLE import under the bare basis
    // never puts tictactoe's own "rules" array on both sides of a WorldDocumentBasis merge — the duplicate survives
    // composition untouched and is caught only by WorldDefinitionValidator's own document-local duplicate check.
    private static byte[] TicTacToeWithADuplicatedRuleName() {
        var original = File.ReadAllBytes(path: Path.Combine(RepositoryRoot(), "src", "Puck.World", "Assets", "worlds", "games", "tictactoe.world.json"));
        var root = ((JsonObject)JsonNode.Parse(json: Encoding.UTF8.GetString(bytes: original))!);
        var rules = ((JsonArray)root["rules"]!);

        rules.Add(item: rules[0]!.DeepClone());

        return Encoding.UTF8.GetBytes(s: root.ToJsonString());
    }
    // A synthetic root — basis: standard.basis.json, imports: [games/tictactoe.world.json] unaliased — the same
    // shape TryComposeFragmentBytes builds in memory, so tictactoe composes alone (no sibling game import to fold
    // its "rules" array against).
    private static byte[] SyntheticRootNamingTicTacToeAlone() {
        var root = new JsonObject {
            ["basis"] = "standard.basis.json",
            ["imports"] = new JsonArray { new JsonObject { ["document"] = "games/tictactoe.world.json" } },
        };

        return Encoding.UTF8.GetBytes(s: root.ToJsonString());
    }

    [Fact]
    public void ComposeTree_composes_the_real_island_through_the_resolver_overload_with_the_arcade_deferrals() {
        var documents = WorldsDirectoryDocuments();

        var result = BrowserComposer.ComposeTree(rootName: "puck.world.json", documents: documents, editedName: null, editedUtf8: null);

        Assert.True(condition: result.Ok, userMessage: string.Join(separator: "; ", values: (result.Errors ?? []).Select(selector: error => error.Message)));
        Assert.NotNull(@object: result.Composed);
        Assert.NotNull(@object: result.Document);
        Assert.Contains(collection: result.Deferred!, filter: message => message.Contains(value: "screen-machine engine 'gaming-brick' registration deferred", comparisonType: StringComparison.Ordinal));
        Assert.Contains(collection: result.Deferred!, filter: message => message.Contains(value: "screen-machine engine 'advanced-gaming-brick' registration deferred", comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void ComposeTree_reports_an_edited_fragments_own_duplicate_row_by_its_own_path() {
        var documents = WorldsDirectoryDocuments();

        documents["test-root.json"] = SyntheticRootNamingTicTacToeAlone();

        var result = BrowserComposer.ComposeTree(
            documents: documents,
            editedName: "games/tictactoe.world.json",
            editedUtf8: TicTacToeWithADuplicatedRuleName(),
            rootName: "test-root.json"
        );

        Assert.False(condition: result.Ok);
        Assert.NotNull(@object: result.Errors);
        Assert.Contains(collection: result.Errors!, filter: error => error.Message.Contains(value: "ttt-place-mark", comparisonType: StringComparison.Ordinal) && error.Message.Contains(value: "duplicates an earlier rule's name", comparisonType: StringComparison.Ordinal));
        // games/tictactoe.world.json is imported unaliased, so its own diagnostic never carried a namespace prefix
        // to strip — it is already the fragment's own message, with no "<name>: " attribution to another document.
        Assert.DoesNotContain(collection: result.Errors!, filter: error => error.Message.Contains(value: "test-root.json:", comparisonType: StringComparison.Ordinal));
    }
    [Fact]
    public void ComposeTree_root_naming_no_held_document_fails_by_name() {
        var result = BrowserComposer.ComposeTree(rootName: "no-such-document.json", documents: new Dictionary<string, byte[]>(), editedName: null, editedUtf8: null);

        Assert.False(condition: result.Ok);
        Assert.Contains(collection: result.Errors!, filter: error => error.Message.Contains(value: "no-such-document.json", comparisonType: StringComparison.Ordinal));
    }
}
