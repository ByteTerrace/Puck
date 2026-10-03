using System.Text.Json;
using Puck.Cli.Affected;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class AffectedCompiledWorldsLawTests {
    private const string After = """
        schema: "puck.world.definition.v1"
        host { exitAfterSeconds: 0.5 width: 1 }
        """;
    private const string Before = """{"host":{"width":1.0,"exitAfterSeconds":0.50},"schema":"puck.world.definition.v1"}""";
    private const string Document = (Stem + ".world.json");
    private const string Source = (Stem + ".puck");
    private const string Stem = (AffectedCommand.ShippedTree + "/modules/sample");

    [InlineData("1.0", "1")]
    [InlineData("0.50", "0.5")]
    [InlineData("1e40", "10e39")]
    [InlineData("1e-40", "10e-41")]
    [InlineData("{\"b\":2,\"a\":1}", "{\"a\":1,\"b\":2}")]
    [InlineData("[{\"b\":[true,null,0.50],\"a\":[[1.0],\"x\"]}]", "[{\"a\":[[1],\"x\"],\"b\":[true,null,0.5]}]")]
    [Theory]
    public void EqualValuesIgnoreNumberSpellingAndObjectOrder(string left, string right) {
        using var a = JsonDocument.Parse(json: left);
        using var b = JsonDocument.Parse(json: right);

        Assert.True(condition: AffectedCompiledWorlds.ValueEqual(left: a.RootElement, right: b.RootElement));
        Assert.True(condition: AffectedCompiledWorlds.ValueEqual(left: b.RootElement, right: a.RootElement));
    }
    [InlineData("1", "2")]
    [InlineData("[1,2]", "[2,1]")]
    [InlineData("{\"a\":1}", "{\"a\":1,\"b\":2}")]
    [InlineData("\"1\"", "1")]
    [InlineData("true", "false")]
    [InlineData("null", "false")]
    [InlineData("1e-40", "0")]
    [InlineData("0.1234567890123456789012345678901", "0.1234567890123456789012345678902")]
    [Theory]
    public void DifferentValuesStayDifferentWithoutDecimalRounding(string left, string right) {
        using var a = JsonDocument.Parse(json: left);
        using var b = JsonDocument.Parse(json: right);

        Assert.False(condition: AffectedCompiledWorlds.ValueEqual(left: a.RootElement, right: b.RootElement));
        Assert.False(condition: AffectedCompiledWorlds.ValueEqual(left: b.RootElement, right: a.RootElement));
    }
    [Fact]
    public void ADocumentReplacedByEquivalentSourceJudgesBothPathsUnchanged() {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: Document, text: Before);
        var since = checkout.Commit(message: "base document");

        File.Delete(path: Path.Combine(path1: checkout.Root, path2: Document));
        checkout.Write(name: Source, text: After);

        Assert.True(condition: AffectedCommand.TryReadChanged(repositoryRoot: checkout.Root, since: since,
            changed: out var changed, deleted: out var deleted, error: out var error), userMessage: error);
        Assert.Equal(actual: changed, expected: [Source, Document]);
        Assert.Equal(actual: deleted, expected: [Document]);
        Assert.Equal(expected: changed, actual: AffectedCompiledWorlds.Unchanged(repositoryRoot: checkout.Root, since: since, changed: changed).Order(comparer: StringComparer.Ordinal));
    }
    [InlineData("different")]
    [InlineData("library")]
    [InlineData("library-beside-document")]
    [InlineData("composition")]
    [InlineData("deleted")]
    [InlineData("invalid")]
    [Theory]
    public void AnUnprovedOrDifferentDocumentKeepsOrdinarySelection(string change) {
        using var checkout = new GitScratchCheckout();

        checkout.Write(name: Document, text: ((change == "composition") ? Before.Insert(startIndex: 1, value: "\"documentId\":\"sample\",") : Before));
        var since = checkout.Commit(message: "base document");

        if (change != "library-beside-document") {
            File.Delete(path: Path.Combine(path1: checkout.Root, path2: Document));
        }
        var source = change switch {
            "different" => After.Replace(comparisonType: StringComparison.Ordinal, newValue: "width: 2", oldValue: "width: 1"),
            "library" or "library-beside-document" => "let width = 2",
            "composition" => """
                module room() {
                    schema: "puck.world.definition.v1"
                    host { exitAfterSeconds: 0.5 width: 1 }
                }
                world sample = room()
                world other = room()
                """,
            "invalid" => "schema: [",
            _ => null,
        };

        if (source is not null) {
            checkout.Write(name: Source, text: source);
        }

        Assert.Empty(collection: AffectedCompiledWorlds.Unchanged(repositoryRoot: checkout.Root, since: since, changed: [Document, Source]));
    }
}
