using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Puck.World;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Schema dispatch and emitted source read their owning declarations. Wire-spelling fixture literals are
/// independent evidence, but live consumers cannot keep a second spelling when the owning declaration changes.</summary>
public sealed class SchemaTokenOwnershipLawTests {
    private static bool IsInlineSchema(string text) {
        const string Header = "schema: \"";
        var headerIndex = text.IndexOf(comparisonType: StringComparison.Ordinal, value: Header);

        // An interpolation through the owner ends this text token immediately after the opening quote. A literal
        // has schema characters after it, whatever the current version or spelling of the owning constant is.
        return (((headerIndex >= 0) && ((headerIndex + Header.Length) < text.Length)) ||
            (text.StartsWith(comparisonType: StringComparison.Ordinal, value: "puck.") && !text.Any(predicate: char.IsWhiteSpace)));
    }
    private static int[] InlineSchemaLines(string source) => CSharpSyntaxTree.ParseText(text: source)
        .GetRoot().DescendantTokens()
        .Where(predicate: token => ((
            token.IsKind(kind: SyntaxKind.StringLiteralToken) ||
            token.IsKind(kind: SyntaxKind.InterpolatedStringTextToken)
        ) && IsInlineSchema(text: token.ValueText)))
        .Select(selector: token => (token.GetLocation().GetLineSpan().StartLinePosition.Line + 1))
        .ToArray();

    [InlineData("src/Puck.GamingBricks.Transpiler/CartridgeLanguageServices.cs")]
    [InlineData("src/Puck.World.Transpiler/Lsp/PuckLanguageServer.cs")]
    [InlineData("src/Puck.World.Transpiler/Decompiler/WorldDecompiler.Sql.cs")]
    [InlineData("src/Puck.World.Transpiler/Lsp/PuckSchemaHover.cs")]
    [Theory]
    public void LiveSchemaConsumersReadTheOwningDeclaration(string file) {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        var source = File.ReadAllText(path: Path.Combine(path1: repositoryRoot, path2: file));
        var offending = InlineSchemaLines(source: source);

        Assert.True(
            condition: (offending.Length == 0),
            userMessage: $"{file} spells a schema inline at lines {string.Join(separator: ", ", values: offending)}"
        );
    }
    [Fact]
    public void TheOwnershipCheckFindsDispatchAndEmittedSourceButAllowsDescriptions() {
        var schema = WorldDefinition.SchemaVersion;
        var source = $$"""
            if (document.Schema == "{{schema}}") { }
            Add(insertText: "schema: \"{{schema}}\"");
            var source = $"schema: \"{{schema}}\"\n{sql}";
            var description = "A document such as {{schema}}.";
            var correct = $"schema: \"{WorldDefinition.SchemaVersion}\"";
            """;

        Assert.Equal(expected: [1, 2, 3], actual: InlineSchemaLines(source: source));
    }
}
