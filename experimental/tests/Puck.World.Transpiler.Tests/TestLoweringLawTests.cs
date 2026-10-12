using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler.Diagnostics;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>What a <c>test</c> block lowers to, and the one thing the enclosing world must never carry — a trace of
/// its own tests.</summary>
public class TestLoweringLawTests {
    private static string Canonical(JsonNode node) =>
        Encoding.UTF8.GetString(bytes: CanonicalJsonDocument.Serialize(node: node));
    private static WorldCompilation Green(string source, string? sourcePath = null, bool allowMultiple = false) {
        var compilation = WorldCompiler.Compile(
            allowMultiple: allowMultiple,
            cancellationToken: TestContext.Current.CancellationToken,
            source: source,
            sourcePath: sourcePath
        );

        Assert.False(
            condition: compilation.Diagnostics.HasErrors,
            userMessage: compilation.Diagnostics.FormatReport(source)
        );

        return compilation;
    }
    // Every document a source publishes, named: the one it lowers to, or one per `world` statement.
    private static IReadOnlyList<(string Name, string Json)> Published(WorldCompilation compilation) => ((compilation.Worlds.Count > 0)
        ? [.. compilation.Worlds.Select(selector: static world => (world.Name, Canonical(node: world.Json)))]
        : [("", Canonical(node: compilation.RequireJson()))]
    );
    // The source with every `test` block cut out, brace by brace — what an author would be left with if they
    // deleted the tests by hand.
    private static string StripTests(string source) {
        var builder = new StringBuilder();
        var index = 0;

        while (index < source.Length) {
            var found = source.IndexOf(
                comparisonType: StringComparison.Ordinal,
                startIndex: index,
                value: "\ntest \""
            );

            if (found < 0) {
                _ = builder.Append(value: source[index..]);

                break;
            }

            _ = builder.Append(value: source[index..(found + 1)]);

            var depth = 0;
            var cursor = source.IndexOf(
                startIndex: found,
                value: '{'
            );

            while (cursor < source.Length) {
                if (source[cursor] == '{') {
                    depth++;
                } else if (source[cursor] == '}') {
                    depth--;

                    if (depth == 0) {
                        break;
                    }
                }
                cursor++;
            }

            index = Math.Min(
                val1: source.Length,
                val2: (cursor + 2)
            );
        }

        return builder.ToString();
    }

    /// <summary>Returns every shipped world source and asset-package source that authors a test, so the
    /// byte-identity law names the source it read. A package source is an absolute path, which
    /// <see cref="Path.Combine(string, string)"/> returns as written.</summary>
    /// <returns>The tested sources as xUnit theory data.</returns>
    public static TheoryData<string> TestedSources() => new(values: ShippedWorlds.SourcePaths().Concat(second: ShippedWorlds.PackageSourcePaths()).Where(predicate: static relativePath => File.ReadAllText(path: Path.Combine(
        path1: ShippedWorlds.FindDirectory(),
        path2: relativePath
    )).Contains(
        comparisonType: StringComparison.Ordinal,
        value: "\ntest \""
    )));
    [MemberData(nameof(TestedSources))]
    [Theory]
    public void AShippedSourceCompilesToTheDocumentItWouldWithItsTestsDeleted(string relativePath) {
        var sourcePath = Path.Combine(
            path1: ShippedWorlds.FindDirectory(),
            path2: relativePath
        );
        var source = File.ReadAllText(path: sourcePath);
        var stripped = StripTests(source: source);

        Assert.NotEqual(
            actual: stripped,
            expected: source
        );

        var tested = Green(
            allowMultiple: true,
            source: source,
            sourcePath: sourcePath
        );

        Assert.NotEmpty(collection: tested.TestWorlds);
        Assert.Equal(
            actual: Published(compilation: tested),
            expected: Published(compilation: Green(
                allowMultiple: true,
                source: stripped,
                sourcePath: sourcePath
            ))
        );
    }
}
