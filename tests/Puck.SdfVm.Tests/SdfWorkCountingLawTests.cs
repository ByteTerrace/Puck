using System.Text.RegularExpressions;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// These source-contract laws inspect counted access and forwarding sites; they do not establish numerical GPU readback.
/// Every SDF kernel counts its own work where it does it (<c>frame/sdf-work.hlsli</c>). A texel counts where a pass writes
/// one: every store to a pixel's visibility record goes through <c>sdfVisibilityStoreWord</c>, which marks the pixel
/// written, and every write to the output image marks it too, so a stage that returns before it stores (the ambient
/// stage on an unlit pixel) counts nothing. A march step counts where a march samples: every sample of a bounded volume's
/// integration counts before anything can skip the rest of it. And every compute kernel hands what it counted to its
/// pass's row. Specialized bank writers count actual stored records, and detail-backed stages keep the plain and
/// named rows disjoint. A helper-owned stage must reach its counting helper through the kernel's explicit include.
/// </summary>
public sealed partial class SdfWorkCountingLawTests {
    private static string Root => RepositoryPaths.Resolve(relativePath: SdfKernelInterfaces.KernelDirectory);
    private static IEnumerable<string> Sources => Directory.EnumerateFiles(path: Root, searchPattern: "*.hlsl*", searchOption: SearchOption.AllDirectories)
        .Select(selector: path => Path.GetRelativePath(path: path, relativeTo: Root).Replace(newChar: '/', oldChar: '\\'))
        .Where(predicate: static path => (
            !path.StartsWith(comparisonType: StringComparison.Ordinal, value: "isa/") &&
            !path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".interface.hlsli")
        ))
        .Order(comparer: StringComparer.Ordinal);

    [Fact]
    public void OnlyTheCountingStoreWritesAPixelsVisibilityRecord() {
        var writers = Sources.Where(predicate: path => (RecordWritePattern().Matches(input: CodeOf(path: path)).Count != 0)).ToArray();
        var store = CodeOf(path: "frame/sdf-visibility.hlsli");

        Assert.Equal(
            actual: writers,
            expected: ["frame/sdf-visibility.hlsli"]
        );
        Assert.Single(collection: RecordWritePattern().Matches(input: store));
        Assert.Matches(
            actualString: store,
            expectedRegexPattern: @"void sdfVisibilityStoreWord\(uint word, uint value\) \{\s*sdfVisibilityRecordBuffer\[word\] = value;\s*sdfWorkTexels = 1u;\s*\}"
        );
    }
    [Fact]
    public void EveryOutputWriteCountsItsTexel() {
        var writes = Sources.SelectMany(selector: path => OutputWritePattern().Matches(input: CodeOf(path: path)).Select(selector: match => (path, match.Value))).ToArray();

        Assert.NotEmpty(collection: writes);
        Assert.All(
            action: static write => Assert.EndsWith(actualString: write.Value, expectedEndString: "sdfWorkTexels = 1u;"),
            collection: writes
        );
    }
    [Fact]
    public void EveryComputeKernelHandsItsPassWhatItCounted() {
        var kernels = Sources.Where(predicate: static path => path.EndsWith(comparisonType: StringComparison.Ordinal, value: ".comp.hlsl"))
            .Select(selector: path => (path, CodeOf(path: path)))
            .Where(predicate: static kernel => kernel.Item2.Contains(comparisonType: StringComparison.Ordinal, value: "void CSMain"))
            .ToArray();
        Assert.NotEmpty(collection: kernels);
        foreach (var (path, code) in kernels) {
            switch (path) {
                case "passes/sdf-brick-bake.comp.hlsl":
                    // The existing sliced brick baker owns no pass-work counter interface.
                    Assert.Contains("#include \"../isa/sdf-bricks.interface.hlsli\"", code);
                    break;
                case "passes/sdf-light-depth.comp.hlsl":
                    Assert.Contains("if (any(id.xy >= passGroup.imageExtent) || passGroup.lightMap == 0u) { return; }", code);
                    Assert.Matches(@"indirectLightDepthRW\[address\] = depth;\s*puckCountWork\(0u, 1u\);", code);
                    break;
                case "passes/sdf-indirect-shade.comp.hlsl":
                    Assert.Contains("#include \"../indirect/sdf-indirect-shade.hlsli\"", code);
                    Assert.Contains("sdfIndirectShadeProbe(index, update.z, lattice, lane, passGroup.indirectReadGeneration,", code);
                    Assert.Empty(CountPattern().Matches(code));
                    break;
                case "passes/sdf-screen-emission.comp.hlsl":
                    AssertScreenEmissionCounts(code);
                    break;
                default:
                    // New kernels keep the ordinary contract until their actual specialized work is witnessed here.
                    Assert.Contains("puckCountWork(sdfWorkSteps, sdfWorkTexels);", code);
                    break;
            }
        }
        foreach (var path in Sources) {
            var calls = CountPattern().Matches(CodeOf(path)).Select(static match => match.Value).ToArray();
            switch (path) {
                case "passes/sdf-light-depth.comp.hlsl":
                    Assert.Equal(["puckCountWork(0u, 1u)"], calls);
                    break;
                case "passes/sdf-indirect-trace.comp.hlsl":
                    Assert.Equal(["puckCountWork(0u, 2u)", "puckCountWork(sdfWorkSteps, sdfWorkTexels)"], calls);
                    break;
                case "indirect/sdf-indirect-shade.hlsli":
                    Assert.Equal(["puckCountWork(sdfWorkSteps, passGroup.workCounterRowDetail == 0u ? 1u : 0u)"], calls);
                    break;
                default:
                    Assert.All(calls, static call => Assert.Equal("puckCountWork(sdfWorkSteps, sdfWorkTexels)", call));
                    break;
            }
        }
        var trace = CodeOf("passes/sdf-indirect-trace.comp.hlsl");
        Assert.Matches(@"if \(group.x == 0u && lane == 0u\) \{\s*" +
            @"indirectCacheRW\[sdfIndirectReceiverProofWordOffset\(passGroup.indirectTier\)\] = 0u;\s*" +
            @"indirectCacheRW\[sdfIndirectReceiverProofWordOffset\(passGroup.indirectTier\) \+ 1u\] = 0u;\s*" +
            @"puckCountDetail\(4u, 0u, 2u, 0u, 0u, 0u\);\s*" +
            @"if \(passGroup.workCounterRowDetail == 0u\) \{ puckCountWork\(0u, 2u\); \}", trace);
        Assert.Contains("if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps = 0u; sdfWorkTexels = 0u; }", trace);
        var shade = CodeOf("indirect/sdf-indirect-shade.hlsli");
        Assert.Contains("void sdfIndirectShadeProbe(uint index, uint level, int3 lattice, uint lane, uint readGeneration, uint readPublication,", shade);
        Assert.Contains("indirectCacheRW[address + source] = sdfIndirectPackRadiance", shade);
        Assert.Matches(@"puckCountDetail\(detail, 0u, 1u, 0u, sdfIndirectHashes, sdfIndirectLoads\);\s*" +
            @"if \(passGroup.workCounterRowDetail != 0u\) \{ sdfWorkSteps = 0u; \}\s*" +
            @"puckCountWork\(sdfWorkSteps, passGroup.workCounterRowDetail == 0u \? 1u : 0u\);", shade);
    }

    private static void AssertScreenEmissionCounts(string code) {
        Assert.Contains("#include \"../isa/sdf-sky-environment.interface.hlsli\"", code);
        Assert.Contains("if ((passGroup.screenEmissionWriteMask & (1u << screen)) == 0u) { return; }", code);
        Assert.Contains("uint samples = 0u;", code);
        Assert.Contains("uint writes = 0u;", code);
        Assert.Equal(2, ScreenLoadPattern().Matches(code).Count);
        Assert.Equal(2, ScreenWritePattern().Matches(code).Count);
        Assert.Matches(@"sum \+= max\(screenSources\[screen\]\.Load\(int3\(at, 0\)\)\.rgb, 0.0\);\s*samples\+\+;", code);
        Assert.Matches(@"average = float4\(max\(screenSources\[screen\]\.Load\(int3\(at, 0\)\)\.rgb, 0.0\), 1.0\);\s*samples\+\+;", code);
        Assert.Matches(@"sdfScreenEmissionRW\[row \+ cell\] = average;\s*writes\+\+;", code);
        Assert.Matches(@"sdfScreenEmissionRW\[row \+ SDF_SCREEN_EMISSION_MEAN\] = total.w > 0.0\s*" +
            @"\? float4\(total.rgb / total.w, SDF_SCREEN_EMISSION_DIRECT_GAIN\) : 0.0;\s*writes\+\+;", code);
        Assert.Equal(["puckCountDetail(0u, 0u, writes, 0u, 0u, samples)"],
            DetailCountPattern().Matches(code).Select(static match => match.Value).ToArray());
        Assert.Empty(CountPattern().Matches(code));
        Assert.Contains("Screens ? [\"screen-images\"]", File.ReadAllText(RepositoryPaths.Resolve("src/Puck.SdfVm/SdfSkyEnvironmentRecorder.cs")));
        var counters = CodeOf("isa/sdf-sky-environment.interface.hlsli");
        Assert.Matches(@"void puckCountDetail\([^)]*\) \{\s*if \(passGroup.workCounterRowDetail == 0u\) \{\s*return;\s*\}", counters);
        Assert.Contains("puckAddWork((row + PuckWorkTexelsWord), texels);", counters);
        Assert.Contains("puckAddWork((row + PuckWorkSkyTextureLoadsWord), loads);", counters);
    }
    [Fact]
    public void EveryVolumeSampleCountsAStepBeforeItCanBeSkipped() {
        var integration = VolumeLoopPattern().Match(input: CodeOf(path: "shade/shade-volumes.hlsli"));

        Assert.True(condition: integration.Success);
        Assert.StartsWith(
            actualString: integration.Groups[1].Value.TrimStart(),
            expectedStartString: "sdfWorkSteps += 1u;"
        );
    }

    // A source's code with its line comments removed, so a comment naming a write is no write.
    private static string CodeOf(string path) =>
        LineCommentPattern().Replace(input: File.ReadAllText(path: Path.Combine(path1: Root, path2: path)), replacement: string.Empty);
    [GeneratedRegex(pattern: @"//[^\n]*")]
    private static partial Regex LineCommentPattern();
    [GeneratedRegex(pattern: @"sdfVisibilityRecordBuffer\[[^\]]*\]\s*=[^=]")]
    private static partial Regex RecordWritePattern();
    [GeneratedRegex(pattern: @"\boutput\[[^\]]*\]\s*=[^;]*;\s*[^\n]*")]
    private static partial Regex OutputWritePattern();
    [GeneratedRegex(pattern: @"\bpuckCountWork\([^)]*\)")]
    private static partial Regex CountPattern();
    [GeneratedRegex(pattern: @"screenSources\[screen\]\.Load\(")]
    private static partial Regex ScreenLoadPattern();
    [GeneratedRegex(pattern: @"sdfScreenEmissionRW\[[^\]]+\]\s*=")]
    private static partial Regex ScreenWritePattern();
    [GeneratedRegex(pattern: @"\bpuckCountDetail\([^)]*\)")]
    private static partial Regex DetailCountPattern();
    [GeneratedRegex(pattern: @"for \(int i = 0; i < steps; i\+\+\) \{\s*([^\n]*)")]
    private static partial Regex VolumeLoopPattern();
}
