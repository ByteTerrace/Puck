using Microsoft.CodeAnalysis.CSharp;

using Puck.Cli.Format;

using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Pins the one line-ending contract the format verb owes .editorconfig: <c>end_of_line = lf</c> for every
/// <c>*.cs</c> file in the tree. Roslyn copies the SOURCE's newline trivia for code it does not touch, so the only
/// newlines a pass gets to choose are the ones it synthesizes — and every one of those must be a bare line feed.
/// A pass that emits <c>\r\n</c> leaves an LF file with mixed terminators the moment it changes anything, which
/// is invisible in a diff and shows up as a whole tree of files git reports modified with nothing to show.
/// </summary>
public sealed class FormatLineEndingTests {
    /// <summary>
    /// An LF fixture that trips <c>member-spacing</c> (a constructor packed against a method), <c>decl-spacing</c>
    /// (a declaration packed against the statement that follows it) and <c>arg-lines</c> (a two-argument call).
    /// </summary>
    private const string LineFeedFixture = "using System;\n\nnamespace Fixture;\n\ninternal sealed class Sample {\n    private readonly int m_count;\n\n    public Sample(int count) {\n        m_count = count;\n    }\n    public int Combine(int left, int right) {\n        var total = Math.Max(val1: left, val2: right);\n        return (total + m_count);\n    }\n}\n";
    /// <summary>
    /// An LF fixture whose method body carries a logical condition and a ternary, for the two opt-in vertical
    /// wrappers that are not in the bare-<c>format</c> selection.
    /// </summary>
    private const string WrapFixture = "namespace Fixture;\n\ninternal sealed class Wrapped {\n    public int Pick(int left, int right) {\n        if ((left > 0) && (right > 0)) {\n            return (left + right);\n        }\n\n        var chosen = ((left > right) ? left : right);\n\n        return chosen;\n    }\n}\n";

    /// <summary>
    /// Every syntactic pass in the bare-<c>format</c> selection, run as one pipeline exactly as the disk phase runs
    /// it, must leave an LF file free of carriage returns — and must actually have rewritten something, so the
    /// assertion cannot pass vacuously.
    /// </summary>
    [Fact]
    public void TheDefaultPipelineLeavesAnLfFileFreeOfCarriageReturns() {
        var rewritten = ApplyAll(passes: FormatPasses.All.Where(predicate: static pass => (pass.Default && !pass.Semantic)).ToList(), text: LineFeedFixture);

        Assert.NotEqual(actual: rewritten, expected: LineFeedFixture);
        Assert.DoesNotContain(actualString: rewritten, expectedSubstring: "\r");
    }
    /// <summary>
    /// Each pass that synthesizes a line break, on its own, over a fixture it is known to rewrite. Naming them one
    /// by one is what makes a regression point at the rewriter that reintroduced <c>\r\n</c>.
    /// </summary>
    [Theory]
    [InlineData("member-spacing", false)]
    [InlineData("decl-spacing", false)]
    [InlineData("arg-lines", false)]
    [InlineData("logical-lines", true)]
    [InlineData("ternary-lines", true)]
    public void APassThatSynthesizesALineBreakSynthesizesALineFeed(string name, bool useWrapFixture) {
        var source = (useWrapFixture ? WrapFixture : LineFeedFixture);
        var pass = FormatPasses.All.Single(predicate: candidate => (candidate.Name == name));
        var rewritten = ApplyAll(passes: [pass], text: source);

        Assert.NotEqual(actual: rewritten, expected: source);
        Assert.DoesNotContain(actualString: rewritten, expectedSubstring: "\r");
    }

    // The disk phase's pipeline: apply each pass in order, re-parsing between them, and take the full text back.
    private static string ApplyAll(string text, IReadOnlyList<FormatPass> passes) {
        foreach (var pass in passes) {
            text = pass.Apply!(arg: CSharpSyntaxTree.ParseText(text: text).GetRoot()).ToFullString();
        }

        return text;
    }
}
