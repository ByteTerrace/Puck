using System.Numerics;
using System.Text.RegularExpressions;
using Puck.SignedDistance;
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
            .Where(predicate: static source => !source.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: "sdf-sky.hlsli"))
            .Where(predicate: static source => GradientCallPattern().IsMatch(input: source.Code))
            .ToArray();

        Assert.Equal(expected: 2, actual: sources.Length);
        // Counting at the callee records both a fog gradient and a fallback at the same pixel, once each.
        Assert.Matches(expectedRegexPattern: @"float3 sdfSkyGradient\(float3 direction\) \{\s*puckCountDetail\(0u, 0u, 0u, 1u, 0u, 0u\);",
            actualString: CodeOf(path: "shade/sdf-sky.hlsli"));
        foreach (var source in sources) { Assert.DoesNotContain(actualString: source.Code, expectedSubstring: "puckCountSky("); }
    }
    [Fact]
    public void DisabledFogDoesNotEvaluateTheGradient() {
        // The composite evaluates the fog's gradient only under a positive in-scatter weight, which a zero density makes
        // exactly zero: its transmittance is exactly one.
        Assert.Matches(expectedRegexPattern: @"if \(fog > 0\.0\) \{[^{}]*sdfSkyGradient\(", actualString: CodeOf(path: "passes/sdf-composite.comp.hlsl"));
        Assert.Matches(expectedRegexPattern: @"\(\(density > 0\.0\) \? exp\(-density \* t\) : 1\.0\)", actualString: CodeOf(path: "shade/sdf-transport.hlsli"));
        Assert.Equal(expected: 0f, actual: SdfSurfaceTransport.Sample(fogDensity: 0f, sample: new SdfRenderSample(Color: Vector3.One, Coverage: 1f, Distance: 100f)).Fog);
    }

    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: Root, path2: path)), replacement: string.Empty);
    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
    [GeneratedRegex(pattern: @"float4 runBase = skyBase.Load\(tap\);\s*puckCountDetail\(0u, 0u, 0u, 0u, 0u, 1u\);\s*if \(runBase.a <= 0.0\) \{\s*continue;\s*\}\s*weight \*= runBase.a;")]
    private static partial Regex ValidTapPattern();
    [GeneratedRegex(pattern: @"\bsdfSkyGradient\(")]
    private static partial Regex GradientCallPattern();
}
