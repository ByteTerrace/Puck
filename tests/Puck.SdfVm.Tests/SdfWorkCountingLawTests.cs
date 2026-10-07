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
                    Assert.Contains(actualString: code, expectedSubstring: "#include \"../isa/sdf-bricks.interface.hlsli\"");
                    break;
                case "passes/sdf-light-depth.comp.hlsl":
                    Assert.Contains(actualString: code, expectedSubstring: "if (slice.x == 0u || id.y >= slice.z * SdfIndirectLightSliceRowEdge) { return; }");
                    Assert.Matches(actualString: code, expectedRegexPattern: @"indirectLightDepthRW\[address\] = depth;\s*puckCountWork\(0u, 1u\);");
                    break;
                case "passes/sdf-indirect-shade.comp.hlsl":
                    Assert.Contains(actualString: code, expectedSubstring: "#include \"../indirect/sdf-indirect-shade.hlsli\"");
                    Assert.Contains(actualString: code, expectedSubstring: "sdfIndirectShadeProbe(index, update.z, lattice, lane, passGroup.indirectReadGeneration,");
                    Assert.Empty(collection: CountPattern().Matches(input: code));
                    break;
                case "passes/sdf-screen-emission.comp.hlsl":
                    AssertScreenEmissionCounts(code: code);
                    break;
                default:
                    // New kernels keep the ordinary contract until their actual specialized work is witnessed here.
                    Assert.Contains(actualString: code, expectedSubstring: "puckCountWork(sdfWorkSteps, sdfWorkTexels);");
                    break;
            }
        }
        foreach (var path in Sources) {
            var calls = CountPattern().Matches(input: CodeOf(path: path)).Select(selector: static match => match.Value).ToArray();

            switch (path) {
                case "passes/sdf-light-depth.comp.hlsl":
                    Assert.Equal(actualArray: calls, expectedSpan: ["puckCountWork(0u, 1u)"]);
                    break;
                case "passes/sdf-indirect-trace.comp.hlsl":
                    Assert.Equal(actualArray: calls, expectedSpan: ["puckCountWork(0u, 1u)", "puckCountWork(sdfWorkSteps, sdfWorkTexels)"]);
                    break;
                case "indirect/sdf-indirect-shade.hlsli":
                    Assert.Equal(actualArray: calls, expectedSpan: ["puckCountWork(sdfWorkSteps, passGroup.workCounterRowDetail == 0u ? 1u : 0u)"]);
                    break;
                default:
                    Assert.All(calls, static call => Assert.Equal(actual: call, expected: "puckCountWork(sdfWorkSteps, sdfWorkTexels)"));
                    break;
            }
        }
        var trace = CodeOf(path: "passes/sdf-indirect-trace.comp.hlsl");

        Assert.Matches(actualString: trace, expectedRegexPattern: @"if \(group.x == 0u && lane == 0u\) \{\s*sdfIndirectStore\(sdfIndirectReceiverProofWordOffset\(passGroup.indirectTier\), 0u\);\s*puckCountDetail\(4u, 0u, 1u, 0u, 0u, 0u\);\s*if \(passGroup.workCounterRowDetail == 0u\) \{ puckCountWork\(0u, 1u\); \}");
        Assert.Contains(actualString: trace, expectedSubstring: "if (passGroup.workCounterRowDetail != 0u) { sdfWorkSteps = 0u; sdfWorkTexels = 0u; }");
        var shade = CodeOf(path: "indirect/sdf-indirect-shade.hlsli");

        Assert.Contains(actualString: shade, expectedSubstring: "void sdfIndirectShadeProbe(uint index, uint level, int3 lattice, uint lane, uint readGeneration, uint readPublication,");
        Assert.Matches(actualString: shade, expectedRegexPattern: @"sdfIndirectStore\(address \+ source, total > 0\.0 \? sdfIndirectPackRadiance\(irradiance.values\[source\] / total\)\s*: SdfIndirectUnresolvedSample\);");
        Assert.Matches(actualString: shade, expectedRegexPattern: "puckCountDetail\\(detail, 0u, 1u, 0u, sdfIndirectHashes, sdfIndirectLoads\\);\\s*if \\(passGroup.workCounterRowDetail != 0u\\) \\{ sdfWorkSteps = 0u; \\}\\s*puckCountWork\\(sdfWorkSteps, passGroup.workCounterRowDetail == 0u \\? 1u : 0u\\);");
    }

    private static void AssertScreenEmissionCounts(string code) {
        Assert.Contains(actualString: code, expectedSubstring: "#include \"../isa/sdf-sky-environment.interface.hlsli\"");
        Assert.Contains(actualString: code, expectedSubstring: "if ((passGroup.screenEmissionWriteMask & (1u << screen)) == 0u) { return; }");
        Assert.Contains(actualString: code, expectedSubstring: "uint samples = 0u;");
        Assert.Contains(actualString: code, expectedSubstring: "uint writes = 0u;");
        Assert.Equal(2, ScreenLoadPattern().Matches(input: code).Count);
        Assert.Equal(2, ScreenWritePattern().Matches(input: code).Count);
        Assert.Matches(actualString: code, expectedRegexPattern: @"sum \+= max\(screenSources\[screen\]\.Load\(int3\(at, 0\)\)\.rgb, 0.0\);\s*samples\+\+;");
        Assert.Matches(actualString: code, expectedRegexPattern: @"average = float4\(max\(screenSources\[screen\]\.Load\(int3\(at, 0\)\)\.rgb, 0.0\), 1.0\);\s*samples\+\+;");
        Assert.Matches(actualString: code, expectedRegexPattern: @"sdfScreenEmissionRW\[row \+ cell\] = average;\s*writes\+\+;");
        Assert.Matches(actualString: code, expectedRegexPattern: "sdfScreenEmissionRW\\[row \\+ SDF_SCREEN_EMISSION_MEAN\\] = total.w > 0.0\\s*\\? float4\\(total.rgb / total.w, SDF_SCREEN_EMISSION_DIRECT_GAIN\\) : 0.0;\\s*writes\\+\\+;");
        Assert.Equal(["puckCountDetail(0u, 0u, writes, 0u, 0u, samples)"],
            DetailCountPattern().Matches(input: code).Select(selector: static match => match.Value).ToArray());
        Assert.Empty(collection: CountPattern().Matches(input: code));
        var recorder = File.ReadAllText(path: RepositoryPaths.Resolve(relativePath: "src/Puck.SdfVm/SdfSkyEnvironmentRecorder.cs"));

        Assert.Contains(actualString: recorder, expectedSubstring: "static readonly string[] ScreenWorkDetails = [\"screen-images\"]");
        Assert.Contains(actualString: recorder, expectedSubstring: "Screens ? ScreenWorkDetails");
        var counters = CodeOf(path: "isa/sdf-sky-environment.interface.hlsli");

        Assert.Matches(actualString: counters, expectedRegexPattern: @"void puckCountDetail\([^)]*\) \{\s*if \(passGroup.workCounterRowDetail == 0u\) \{\s*return;\s*\}");
        Assert.Contains(actualString: counters, expectedSubstring: "puckAddWork((row + PuckWorkTexelsWord), texels);");
        Assert.Contains(actualString: counters, expectedSubstring: "puckAddWork((row + PuckWorkSkyTextureLoadsWord), loads);");
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
