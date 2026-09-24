using Puck.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>LSP tests for the embedded State SQL dialect: completions, dot-access column completions, and hover info,
/// offered inside a world document's <c>sql</c> block and nowhere else.</summary>
public class StateSqlLspTests {
    private const string World = "schema: \"puck.world.definition.v1\"\n";
    // A cartridge document routes to a vocabulary that embeds no language, so its `sql` block is plain data.
    private const string Cartridge = "schema: \"puck.cartridge.v1\"\n\nsql {\n    CREATE TABLE fi|ghters (\n        hero TEXT PRIMARY KEY\n    );\n}\n";

    private static readonly DocumentVocabularyResolver Resolver = InertVocabulary.ResolverFor(schema: "puck.cartridge.v1");

    [InlineData((World + "sql {\n    |\n}\n"), new[] { "CREATE TABLE", "DECLARE", "SELECT", "INSERT INTO", "UPDATE", "DELETE FROM", "BEGIN TRANSACTION", "ON OVERFLOW SATURATE", "INT", "KEY" }, new string[0])]
    [InlineData((World + "sql {\n    CREATE TABLE fighters (\n        hero KEY(16),\n        hp INT\n    );\n    DECLARE turnCount INT;\n    |\n}\n"), new[] { "fighters", "hero", "hp", "turnCount" }, new string[0])]
    [InlineData((World + "sql {\n    CREATE TABLE fighters (\n        hero KEY(16),\n        hp INT\n    );\n}\nrule \"check\" {\n    when fighters.|\n    hp += 1\n}\n"), new[] { "hero", "hp" }, new string[0])]
    [InlineData((World + "\nsql {\n    DECLARE turnCount INT DEFAULT 1;\n}\n\nstate {\n    world {\n        slot mysqlRow = |\n    }\n}\n"), new string[0], new[] { "CREATE TABLE", "DECLARE", "INSERT INTO" })]
    [InlineData(Cartridge, new string[0], new[] { "CREATE TABLE", "DECLARE" })]
    [Theory]
    public async Task CompletionOffersSqlOnlyInsideAWorldsSqlBlock(string markedSource, string[] offered, string[] withheld) {
        var labels = await LanguageServerClient.CompletionLabelsAsync(
            markedSource: markedSource,
            vocabularyResolver: Resolver
        );

        Assert.Equal(
            actual: $"missing [{string.Join(separator: ", ", values: offered.Where(predicate: label => !labels.Contains(item: label)))}] offered [{string.Join(separator: ", ", values: withheld.Where(predicate: labels.Contains))}]",
            expected: "missing [] offered []"
        );
    }
    [InlineData((World + "sql {\n    CREATE TABLE figh|ters (\n        hero TEXT PRIMARY KEY,\n        hp INT CHECK (hp BETWEEN 0 AND 100)\n    ) CAPACITY 16;\n}\n"), new[] { "**`fighters`** — SQL table", "Primary key: `hero` (capacity: 16)", "fightersHp" })]
    [InlineData((World + "sql {\n    CREATE TABLE fighters (\n        hero TEXT PRIMARY KEY,\n        h|p INT CHECK (hp BETWEEN 0 AND 100) ON OVERFLOW SATURATE\n    ) CAPACITY 16;\n}\n"), new[] { "**`hp`** — SQL column (`fighters.hp`)", "State row: `fightersHp`", "Check: `BETWEEN 0 AND 100`", "Overflow: `SATURATE`" })]
    [InlineData((World + "sql {\n    DECLARE turn|Count INT DEFAULT 1;\n}\n"), new[] { "**`turnCount`** — SQL scalar slot", "State row: `turnCount`" })]
    [InlineData((World + "sql {\n    CREATE TABLE fighters (\n        hero KEY(16),\n        hp INT ON OVERFLOW SATU|RATE\n    );\n}\n"), new[] { "**`ON OVERFLOW SATURATE`** — SQL dialect keyword" })]
    [Theory]
    public async Task HoverInsideASqlBlockDescribesWhatIsUnderTheCursor(string markedSource, string[] lines) {
        var markdown = await LanguageServerClient.HoverAsync(
            markedSource: markedSource,
            vocabularyResolver: Resolver
        );

        Assert.NotNull(@object: markdown);
        Assert.Equal(
            actual: string.Join(
                separator: " | ",
                values: lines.Where(predicate: line => !markdown.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: line
                ))
            ),
            expected: ""
        );
    }
    [InlineData((World + "\nsql {\n    CREATE TABLE fighters (\n        hero TEXT PRIMARY KEY,\n        hp INT\n    );\n}\n\nstate {\n    world {\n        slot h|p : Int = 10\n    }\n}\n"))]
    [InlineData(Cartridge)]
    [Theory]
    public async Task HoverOutsideAWorldsSqlBlockDescribesNoSql(string markedSource) {
        var markdown = (await LanguageServerClient.HoverAsync(
            markedSource: markedSource,
            vocabularyResolver: Resolver
        ) ?? "");

        Assert.DoesNotContain(
            actualString: markdown,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "SQL column"
        );
        Assert.DoesNotContain(
            actualString: markdown,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "SQL table"
        );
    }
}
