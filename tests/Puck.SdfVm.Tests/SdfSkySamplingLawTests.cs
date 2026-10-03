using System.Numerics;
using System.Text.RegularExpressions;
using Puck.Shaders;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky's validity alpha guards unwritten run images; every field evaluation reaches its pass's counters, a
/// composite's in-place fallback and the environment map's texels included; the composite's atmosphere reads the
/// residency's environment map, evaluates no sky layer and counts each kind it evaluates in its own atmosphere row; and the
/// environment map evaluates the gradient alone, never a body, star or cloud.
/// These laws inspect the shipped shader sources.</summary>
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
    public void EverySkyFieldEvaluationCountsIncludingTheFallback() {
        var sources = Directory.EnumerateFiles(path: Root, searchPattern: "*.hlsl*", searchOption: SearchOption.AllDirectories)
            .Select(selector: path => (Path: path, Code: CodeOf(path: path)))
            .Where(predicate: static source => !source.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: "sdf-sky.hlsli"))
            .Where(predicate: static source => GradientCallPattern().IsMatch(input: source.Code))
            .ToArray();

        // The composite's fallback, the sky runs' base and the environment map's texel.
        Assert.Equal(expected: 3, actual: sources.Length);
        // Counting at the callee records every evaluation, whichever pass reaches it, once each.
        Assert.Matches(expectedRegexPattern: @"float3 sdfSkyGradient\(float3 direction\) \{\s*puckCountDetail\(0u, 0u, 0u, 1u, 0u, 0u\);",
            actualString: CodeOf(path: "shade/sdf-sky.hlsli"));
    }
    [Fact]
    public void TheCompositesAtmosphereReadsTheEnvironmentMapAndCountsInItsOwnRow() {
        var composite = CodeOf(path: "passes/sdf-composite.comp.hlsl");
        var kinds = KindBlockPattern().Matches(input: composite);

        // One block a kind, each under a positive in-scatter weight, each counting one evaluation in the atmosphere row and
        // evaluating no sky layer: the fog and the haze read the sky from the environment map.
        Assert.Equal(expected: 3, actual: kinds.Count);
        foreach (Match kind in kinds) {
            Assert.Contains(expectedSubstring: "puckCountDetail(SdfCompositeAtmosphereDetail, 0u, 0u, 1u, 0u, 0u);", actualString: kind.Value);
            Assert.DoesNotMatch(expectedRegexPattern: EvaluatorPattern, actualString: kind.Value);
        }
        Assert.Matches(expectedRegexPattern: $@"static const uint SdfCompositeAtmosphereDetail = {SdfWorldWorkDetails.Of(part: SdfWorldPackage.Parts.Composite).ToList().IndexOf(item: SdfWorldWorkDetails.Atmosphere)}u;", actualString: composite);

        // The lookup filters the map's texels and evaluates no layer.
        var lookup = LookupPattern().Match(input: CodeOf(path: "passes/sdf-sky-pass.hlsli"));

        Assert.True(condition: lookup.Success);
        Assert.Contains(actualString: lookup.Value, expectedSubstring: "sdfSkyEnvironment[");
        Assert.DoesNotMatch(expectedRegexPattern: EvaluatorPattern, actualString: lookup.Value);
    }
    [Fact]
    public void TheEnvironmentMapEvaluatesTheGradientAloneAndNoBody() {
        var map = CodeOf(path: "passes/sdf-sky-environment.comp.hlsl");

        // One gradient evaluation an invocation (counted by the law above); no point run or cloud, so a bright disc never
        // enters the map.
        Assert.Single(collection: GradientCallPattern().Matches(input: map));
        Assert.DoesNotMatch(actualString: map, expectedRegexPattern: @"\bsdf(SkyPoints|StarField|SkyCloudRun|CloudLayer)\(");

        // The reduction reads the map and evaluates nothing.
        var reduce = CodeOf(path: "passes/sdf-sky-environment-reduce.comp.hlsl");

        Assert.DoesNotMatch(actualString: reduce, expectedRegexPattern: EvaluatorPattern);
        Assert.DoesNotContain(actualString: reduce, expectedSubstring: "puckCountSky");
    }
    [Fact]
    public void DisabledFogReadsNoMap() {
        // The composite reads the sky the fog and the haze in-scatter only under a positive in-scatter weight of either,
        // which a zero density makes exactly zero: its depth is zero and its transmittance exactly one.
        Assert.Matches(expectedRegexPattern: @"float3 ambient = \(\(\(kinds\.y > 0\.0\) \|\| \(\(kinds\.x > 0\.0\) && sdfAirFogReadsSky\(\)\)\) \? sdfSkyPassEnvironment\(", actualString: CodeOf(path: "passes/sdf-composite.comp.hlsl"));
        Assert.Matches(expectedRegexPattern: @"if \(\(extinction <= 0\.0\) \|\| \(extent <= 0\.0\)\) \{\s*return 0\.0;", actualString: CodeOf(path: "shade/sdf-atmosphere.hlsli"));

        var block = default(SdfSkyBlock);

        SdfSky.PackAtmosphere(atmosphere: (SdfAtmosphere.Default with { FogDensity = 0f }), block: ref block, farDistance: 40f, lights: new SdfLights());
        Assert.Equal(expected: 0f, actual: SdfSurfaceTransport.Sample(air: new SdfAirRay(Block: block, Direction: -Vector3.UnitZ, Origin: Vector3.Zero), sample: new SdfRenderSample(Color: Vector3.One, Coverage: 1f, Distance: 100f)).Fog);
    }

    // A call of any sky layer's evaluator.
    private const string EvaluatorPattern = @"\bsdf(SkyGradient|SkyCloudRun|SkyPoints|CloudLayer|StarField)\(";

    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: Root, path2: path)), replacement: string.Empty);
    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
    [GeneratedRegex(pattern: @"float4 runBase = skyBase.Load\(tap\);\s*puckCountDetail\(0u, 0u, 0u, 0u, 0u, 1u\);\s*if \(runBase.a <= 0.0\) \{\s*continue;\s*\}\s*weight \*= runBase.a;")]
    private static partial Regex ValidTapPattern();
    [GeneratedRegex(pattern: @"\bsdfSkyGradient\(")]
    private static partial Regex GradientCallPattern();
    [GeneratedRegex(pattern: @"if \(kinds\.[xyz] > 0\.0\) \{[^{}]*\}")]
    private static partial Regex KindBlockPattern();
    [GeneratedRegex(pattern: @"float3 sdfSkyPassEnvironment\(float3 direction\) \{.*?\n\}", options: RegexOptions.Singleline)]
    private static partial Regex LookupPattern();
}
