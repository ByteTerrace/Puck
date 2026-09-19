using Puck.Transpiler.Formatting;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>Formats world-vocabulary source the way <c>puck fmt</c> does, failing the test that asked when the
/// source does not parse.</summary>
public static class PuckFormat {
    /// <summary>Returns <paramref name="source"/> parsed and printed back.</summary>
    /// <param name="source">The source text to format.</param>
    /// <param name="tabSize">The spaces one indentation level costs.</param>
    /// <param name="insertSpaces">Whether one level indents with spaces rather than a tab.</param>
    /// <returns>The formatted source.</returns>
    public static string Format(string source, int tabSize = 2, bool insertSpaces = true) {
        var result = PuckPrinter.Format(
            options: new PuckPrintOptions { InsertSpaces = insertSpaces, TabSize = tabSize },
            source: source,
            vocabulary: WorldDocumentVocabulary.Instance
        );

        Assert.True(
            condition: (result.Value is not null),
            userMessage: $"the source does not parse:{Environment.NewLine}{result.Diagnostics.FormatReport(source)}"
        );

        return result.Value!;
    }
}
