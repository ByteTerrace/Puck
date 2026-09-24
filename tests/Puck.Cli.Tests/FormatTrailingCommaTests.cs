using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Format;
using Puck.Cli.Format.Rewriters;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Pins where <c>trailing-comma</c> may add a comma. A collection, array or object initializer admits one; the
/// braces around one element of a dictionary-style initializer (<c>{ key, value }</c>) are an argument list, which
/// does not, so a comma there turns valid source into a syntax error and the write guard skips the whole file.
/// </summary>
public sealed class FormatTrailingCommaTests {
    private static string Apply(string source) =>
        new TrailingCommaRewriter().Visit(node: CSharpSyntaxTree.ParseText(text: source).GetRoot())!.ToFullString();

    [Fact]
    public void AMultiLineElementInitializerGetsNoCommaAndItsCollectionDoes() {
        const string Source = """
            var table = new System.Collections.Generic.Dictionary<string, int[]> {{
                "key", new[] {
                    1,
                    2
                }
            }};

            """;
        const string Expected = """
            var table = new System.Collections.Generic.Dictionary<string, int[]> {{
                "key", new[] {
                    1,
                    2,
                }
            }};

            """;
        var rewritten = Apply(source: Source);

        Assert.Equal(
            actual: rewritten,
            expected: Expected
        );
        Assert.False(condition: RewriteIo.HasSyntaxErrors(
            original: Source,
            rewritten: rewritten
        ));
    }
    [Fact]
    public void AMultiLineCollectionOfElementInitializersEndsWithAComma() {
        const string Source = """
            var table = new System.Collections.Generic.Dictionary<string, int> {
                { "a", 1 },
                { "b", 2 }
            };

            """;

        Assert.Equal(
            actual: Apply(source: Source),
            expected: Source.Replace(
                newValue: "{ \"b\", 2 },\n",
                oldValue: "{ \"b\", 2 }\n"
            )
        );
    }
}
