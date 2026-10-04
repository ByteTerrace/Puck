using System.Numerics;
using System.Text.RegularExpressions;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky's validity alpha guards unwritten run images; every layer evaluation goes through the one evaluator
/// whose kinds' modules count it, which the sky's runs, a composite's in-place fallback and the environment map's texels
/// all reach; the composite's fog reads the residency's environment map and evaluates no sky; and the environment map
/// evaluates the layers the lighting sees alone, never a body. These laws inspect the shipped shader sources.</summary>
public sealed partial class SdfSkySamplingLawTests {
    private static string Root => RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);

    [Fact]
    public void InvalidSkyTapsAreRejectedBeforeReadingUnwrittenRuns() {
        var source = CodeOf(path: "passes/sdf-sky-pass.hlsli");

        // A temporal resolve can expose sky beside an invalid run even though the render-grid pixel is covered.
        // The scale and offset there have no current value; even a zero weight cannot mask a NaN left in storage.
        var guard = ValidTapPattern().Match(input: source);

        Assert.True(condition: guard.Success, userMessage: "Reject a zero validity alpha before reading any upper run.");
        foreach (var image in new[] { "skyUpper0", "skyUpper1", "skyUpper2" }) {
            var load = Assert.Single(collection: Regex.Matches(input: source, pattern: $@"\b{image}\.Load\(tap\)"));

            Assert.True(condition: (load.Index >= (guard.Index + guard.Length)));
        }
    }
    [Fact]
    public void EverySkyFieldEvaluationCountsIncludingTheFallback() {
        var sources = Directory.EnumerateFiles(path: Root, searchPattern: "*.hlsl*", searchOption: SearchOption.AllDirectories)
            .Select(selector: path => (Path: path, Code: CodeOf(path: path)))
            .Where(predicate: static source => !source.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: "sdf-sky.hlsli"))
            .Where(predicate: static source => WalkCallPattern().IsMatch(input: source.Code))
            .ToArray();

        // The sky runs' fields, the composite's stack (its in-place fallback among them) and the environment map's texel.
        Assert.Equal(expected: 3, actual: sources.Length);
        // Every walk evaluates a layer through the one evaluator, whose kinds' modules each count the evaluation, so every
        // evaluation is counted once, whichever pass reaches it.
        var sky = CodeOf(path: "sky/sdf-sky.hlsli");

        Assert.Single(collection: Regex.Matches(input: sky, pattern: @"\bsdfSkyKindEvaluate\("));
        Assert.Matches(actualString: sky, expectedRegexPattern: @"float4 sdfSkyLayerValue\(SdfSkyLayer layer, float3 world\) \{[^}]*\}[^}]*sdfSkyKindEvaluate\(layer, sample\)");
    }
    [Fact]
    public void TheCompositesFogReadsTheEnvironmentMapAndEvaluatesNoSky() {
        var composite = CodeOf(path: "passes/sdf-composite.comp.hlsl");
        var fog = FogBlockPattern().Match(input: composite);

        Assert.True(condition: fog.Success, userMessage: "The composite's fog reads the environment map under a positive in-scatter weight.");
        Assert.DoesNotMatch(expectedRegexPattern: EvaluatorPattern, actualString: fog.Value);
        Assert.DoesNotContain(expectedSubstring: "evaluations", actualString: fog.Value);

        // The lookup filters the map's texels and evaluates no layer.
        var lookup = LookupPattern().Match(input: CodeOf(path: "passes/sdf-sky-pass.hlsli"));

        Assert.True(condition: lookup.Success);
        Assert.Contains(actualString: lookup.Value, expectedSubstring: "sdfSkyEnvironment[");
        Assert.DoesNotMatch(expectedRegexPattern: EvaluatorPattern, actualString: lookup.Value);
    }
    [Fact]
    public void TheEnvironmentMapEvaluatesTheLitLayersAloneAndNoBody() {
        var map = CodeOf(path: "passes/sdf-sky-environment.comp.hlsl");

        // One walk of the layers the lighting sees an invocation (its evaluations counted by the law above); a disc and a
        // layer only the camera sees are passed over, so a bright body never enters the map.
        Assert.Single(collection: Regex.Matches(input: map, pattern: @"\bsdfSkyEnvironmentColor\("));
        Assert.DoesNotMatch(actualString: map, expectedRegexPattern: @"\bsdf(SkyCompose|SkyFieldRuns|SkyKindEvaluate)\(");
        Assert.Matches(
            actualString: CodeOf(path: "sky/sdf-sky.hlsli"),
            expectedRegexPattern: @"float3 sdfSkyEnvironmentColor\(float3 world, out float3 reflection\) \{[^}]*if \(\(\(layer.Visibility & SDF_SKY_VISIBILITY_LIGHTING\) == 0u\) \|\| \(layer.Kind == SDF_SKY_KIND_DISC\)\) \{\s*continue;"
        );

        // The reduction reads the map and evaluates nothing.
        var reduce = CodeOf(path: "passes/sdf-sky-environment-reduce.comp.hlsl");

        Assert.DoesNotMatch(actualString: reduce, expectedRegexPattern: EvaluatorPattern);
        Assert.DoesNotContain(actualString: reduce, expectedSubstring: "puckCountSky");
    }
    [Fact]
    public void DisabledFogReadsNoMap() {
        // The composite reads the fog's sky only under a positive in-scatter weight, which a zero density makes exactly
        // zero: its transmittance is exactly one.
        Assert.Matches(expectedRegexPattern: @"if \(fog > 0\.0\) \{[^{}]*sdfSkyPassEnvironment\(", actualString: CodeOf(path: "passes/sdf-composite.comp.hlsl"));
        Assert.Matches(expectedRegexPattern: @"\(\(density > 0\.0\) \? exp\(-density \* t\) : 1\.0\)", actualString: CodeOf(path: "shade/sdf-transport.hlsli"));
        Assert.Equal(expected: 0f, actual: SdfSurfaceTransport.Sample(fogDensity: 0f, sample: new SdfRenderSample(Color: Vector3.One, Coverage: 1f, Distance: 100f)).Fog);
    }

    // A call of any sky layer's evaluator.
    private const string EvaluatorPattern = @"\bsdf(SkyKindEvaluate|SkyLayerValue|SkyApplyLayer|SkyCompose|SkyFieldRuns|SkyEnvironmentColor)\(";

    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: Root, path2: path)), replacement: string.Empty);
    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
    [GeneratedRegex(pattern: @"float4 runBase = skyBase.Load\(tap\);\s*puckCountDetail\(0u, 0u, 0u, 0u, 0u, 1u\);\s*if \(runBase.a <= 0.0\) \{\s*continue;\s*\}\s*weight \*= runBase.a;")]
    private static partial Regex ValidTapPattern();
    [GeneratedRegex(pattern: @"\bsdf(SkyFieldRuns|SkyCompose|SkyEnvironmentColor)\(")]
    private static partial Regex WalkCallPattern();
    [GeneratedRegex(pattern: @"if \(fog > 0\.0\) \{[^{}]*\}")]
    private static partial Regex FogBlockPattern();
    [GeneratedRegex(pattern: @"float3 sdfSkyPassEnvironment\(float3 direction\) \{.*?\n\}", options: RegexOptions.Singleline)]
    private static partial Regex LookupPattern();
}
