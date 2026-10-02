using System.Text.RegularExpressions;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky's validity alpha guards unwritten run images, and every field evaluation reaches its pass's
/// counters, including fog and a fallback in the same invocation. These laws inspect the shipped shader sources.</summary>
public sealed partial class SdfSkySamplingLawTests {
    private static string Root => RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);

    [Fact]
    public void InvalidSkyTapsAreRejectedBeforeReadingUnwrittenRuns() {
        var source = CodeOf(path: "passes/sdf-sky-pass.hlsli");

        // A temporal resolve can expose sky beside an invalid run even though the render-grid pixel is covered.
        // The scale and offset there have no current value; even a zero weight cannot mask a NaN left in storage.
        var guard = ValidTapPattern().Match(input: source);

        Assert.True(condition: guard.Success, userMessage: "Reject a zero validity alpha before reading either upper run.");
        foreach (var image in new[] { "skyScale", "skyOffset" }) {
            var load = Assert.Single(collection: Regex.Matches(input: source, pattern: $@"\b{image}\.Load\(tap\)"));

            Assert.True(condition: (load.Index >= (guard.Index + guard.Length)));
        }
    }

    [Fact]
    public void EverySkyFieldEvaluationCountsIncludingFogAndFallback() {
        var sources = Directory.EnumerateFiles(path: Root, searchPattern: "*.hlsl*", searchOption: SearchOption.AllDirectories)
            .Select(selector: path => (Path: path, Code: CodeOf(path: path)))
            .Where(predicate: static source => !source.Path.EndsWith(value: "sdf-sky.hlsli", comparisonType: StringComparison.Ordinal))
            .Where(predicate: static source => GradientCallPattern().IsMatch(input: source.Code))
            .ToArray();

        Assert.Equal(expected: 2, actual: sources.Length);
        foreach (var source in sources) {
            Assert.Equal(expected: GradientCallPattern().Matches(input: source.Code).Count,
                actual: CountedGradientPattern().Matches(input: source.Code).Count);
            Assert.Contains(expectedSubstring: "puckCountSky(evaluations);", actualString: source.Code);
            // A fogged partial hit may also need the in-place fallback. An assignment of one loses the fog's count.
            Assert.Equal(expected: ["evaluations = 0u;"],
                actual: CounterAssignmentPattern().Matches(input: source.Code).Select(selector: static match => match.Value));
        }
    }

    [Fact]
    public void DisabledFogDoesNotEvaluateTheGradient() => Assert.Matches(
        expectedRegexPattern: @"if \(\(litColor.a > 0.0\) && \(t > 0.0\) && \(sdfSky\[0\].FogDensity > 0.0\)\) \{[^{}]*sdfSkyGradient\(",
        actualString: CodeOf(path: "passes/sdf-composite.comp.hlsl")
    );

    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: Root, path2: path)), replacement: string.Empty);

    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
    [GeneratedRegex(pattern: @"float4 runBase = skyBase.Load\(tap\);\s*if \(runBase.a <= 0.0\) \{\s*continue;\s*\}\s*weight \*= runBase.a;")]
    private static partial Regex ValidTapPattern();
    [GeneratedRegex(pattern: @"\bsdfSkyGradient\(")]
    private static partial Regex GradientCallPattern();
    [GeneratedRegex(pattern: @"\bsdfSkyGradient\([^;]+;\s*evaluations \+= 1u;")]
    private static partial Regex CountedGradientPattern();
    [GeneratedRegex(pattern: @"\bevaluations\s*=[^;]+;")]
    private static partial Regex CounterAssignmentPattern();
}
