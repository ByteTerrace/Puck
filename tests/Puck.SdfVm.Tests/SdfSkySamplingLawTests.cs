using System.Numerics;
using System.Text.RegularExpressions;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>The sky's validity alpha guards unwritten run images; every layer evaluation goes through the one evaluator
/// whose kinds' modules count it, which the sky's runs, a composite's in-place fallback and the environment map's texels
/// all reach; the composite's atmosphere reads the residency's environment map, evaluates no sky layer and counts each
/// kind it evaluates in its own atmosphere row; and the environment map evaluates the layers the lighting sees alone,
/// never a body. These laws inspect the shipped shader sources.</summary>
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
    public void TheCompositesAtmosphereReadsTheEnvironmentMapAndCountsInItsOwnRow() {
        var composite = CodeOf(path: "passes/sdf-composite.comp.hlsl");
        var kinds = KindBlockPattern().Matches(input: composite);

        // One block a kind, each under a positive in-scatter weight, each counting one evaluation in the atmosphere row and
        // evaluating no sky layer: the fog and the haze read the sky from the environment map.
        Assert.Equal(expected: 3, actual: kinds.Count);
        foreach (Match kind in kinds) {
            Assert.Contains(expectedSubstring: "puckCountDetail(SDF_SKY_DETAIL_ATMOSPHERE, 0u, 0u, 1u, 0u, 0u);", actualString: kind.Value);
            Assert.DoesNotMatch(expectedRegexPattern: EvaluatorPattern, actualString: kind.Value);
        }
        Assert.Contains(expectedSubstring: $"#define SDF_SKY_DETAIL_ATMOSPHERE {SdfSkyDetails.AtmosphereRow}u", actualString: CodeOf(path: "isa/sdf-sky-kinds.hlsli"));

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
            expectedRegexPattern: @"float3 sdfSkyEnvironmentColor\(float3 world\) \{[^}]*if \(\(\(layer.Visibility & SDF_SKY_VISIBILITY_LIGHTING\) == 0u\) \|\| \(layer.Kind == SDF_SKY_KIND_DISC\)\) \{\s*continue;"
        );

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
    private const string EvaluatorPattern = @"\bsdf(SkyKindEvaluate|SkyLayerValue|SkyApplyLayer|SkyCompose|SkyFieldRuns|SkyEnvironmentColor)\(";

    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: Root, path2: path)), replacement: string.Empty);
    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
    [GeneratedRegex(pattern: @"float4 runBase = skyBase.Load\(tap\);\s*puckCountDetail\(0u, 0u, 0u, 0u, 0u, 1u\);\s*if \(runBase.a <= 0.0\) \{\s*continue;\s*\}\s*weight \*= runBase.a;")]
    private static partial Regex ValidTapPattern();
    [GeneratedRegex(pattern: @"\bsdf(SkyFieldRuns|SkyCompose|SkyEnvironmentColor)\(")]
    private static partial Regex WalkCallPattern();
    [GeneratedRegex(pattern: @"if \(kinds\.[xyz] > 0\.0\) \{[^{}]*\}")]
    private static partial Regex KindBlockPattern();
    [GeneratedRegex(pattern: @"float3 sdfSkyPassEnvironment\(float3 direction\) \{.*?\n\}", options: RegexOptions.Singleline)]
    private static partial Regex LookupPattern();
}
