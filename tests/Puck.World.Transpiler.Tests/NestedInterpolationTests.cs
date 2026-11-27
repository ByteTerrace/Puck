using Puck.Transpiler.Formatting;
using Puck.Transpiler.Parsing;
using Puck.World.Transpiler.Lowering;
using Xunit;

namespace Puck.World.Transpiler.Tests;

public sealed class NestedInterpolationTests {
    [Theory]
    [InlineData("$\"{(true) ? $\"a-{7}\" : \"b\"}\"", "a-7")]
    [InlineData("$\"{false ? \"b\" : $\"a-{$\"{7}\"}\"}\"", "a-7")]
    [InlineData("$\"{({ value: $\"a-{7}\" }).value}\"", "a-7")]
    [InlineData("$\"{\"}\"}-{\"\\\"\"}-{\"\\\\\"}\"", "}-\"-\\")]
    [InlineData("$\"{7 /* } \" */}\"", "7")]
    [InlineData("$\"{{outer}}-{$\"{{inner}}-{7}\"}\"", "{outer}-{inner}-7")]
    [InlineData("$\"\"\"{true ? $\"\"\"a-{7}\"\"\" : \"b\"}\"\"\"", "a-7")]
    [InlineData("$\"{$\"\"\"a-{7}\"\"\"}\"", "a-7")]
    public void NestedHolesCompileAndFormatWithoutChangingTheirValue(string expression, string expected) {
        var source = "schema: \"puck.world.definition.v1\"\ndocumentId: " + expression + "\n";
        Assert.Equal(source.Length - 1, SourceLexemes.End(source, source.IndexOf('$')));
        var formatted = PuckPrinter.Format(source, WorldDocumentVocabulary.Instance);
        Assert.False(formatted.Diagnostics.HasErrors, formatted.Diagnostics.FormatReport(source));
        foreach (var spelling in new[] { source, formatted.Value! }) {
            var compiled = WorldCompileSession.Shared.Compile(source: spelling, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(compiled.Diagnostics.HasErrors, compiled.Diagnostics.FormatReport(spelling));
            Assert.Equal(expected, compiled.RequireJson()["documentId"]!.GetValue<string>());
        }
        Assert.Equal(formatted.Value, PuckPrinter.Format(formatted.Value!, WorldDocumentVocabulary.Instance).Value);
    }

    [Theory]
    [InlineData("$\"{true ? $\"a-{7}\" : \"b\"\"")]
    [InlineData("$\"{true ? $\"a-{7\" : \"b\"}\"")]
    public void AnUnclosedNestedHoleIsASyntaxError(string expression) {
        var result = PuckPrinter.Format("documentId: " + expression, WorldDocumentVocabulary.Instance);
        Assert.True(result.Diagnostics.HasErrors);
        Assert.Null(result.Value);
    }
}
