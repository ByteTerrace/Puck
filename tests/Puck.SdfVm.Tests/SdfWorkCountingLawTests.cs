using System.Text.RegularExpressions;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Every SDF kernel counts its own work where it does it (<c>frame/sdf-work.hlsli</c>). A texel counts where a pass writes
/// one: every store to a pixel's visibility record goes through <c>sdfVisibilityStoreWord</c>, which marks the pixel
/// written, and every write to the output image marks it too, so a stage that returns before it stores (the ambient
/// stage on an unlit pixel) counts nothing. A march step counts where a march samples: every sample of a bounded volume's
/// integration counts before anything can skip the rest of it. And every compute kernel hands what it counted to its
/// pass's row, never a count of its own making.
/// </summary>
public sealed partial class SdfWorkCountingLawTests {
    private static string Root => RepositoryPaths.Resolve(relativePath: SdfWorldInterfaces.KernelDirectory);
    private static IEnumerable<string> Sources => Directory.EnumerateFiles(path: Root, searchPattern: "*.hlsl*", searchOption: SearchOption.AllDirectories)
        .Select(selector: path => Path.GetRelativePath(path: path, relativeTo: Root).Replace(newChar: '/', oldChar: '\\'))
        .Where(predicate: static path => !path.StartsWith(comparisonType: StringComparison.Ordinal, value: "isa/"))
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
        var calls = Sources.SelectMany(selector: path => CountPattern().Matches(input: CodeOf(path: path)).Select(selector: match => match.Value)).ToArray();

        Assert.NotEmpty(collection: kernels);
        Assert.All(
            action: static kernel => Assert.Contains(actualString: kernel.Item2, expectedSubstring: "puckCountWork(sdfWorkSteps, sdfWorkTexels);"),
            collection: kernels.Where(predicate: static kernel => !kernel.path.EndsWith(comparisonType: StringComparison.Ordinal, value: "brick-bake.comp.hlsl"))
        );
        Assert.All(
            action: static call => Assert.Equal(actual: call, expected: "puckCountWork(sdfWorkSteps, sdfWorkTexels)"),
            collection: calls
        );
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
    [GeneratedRegex(pattern: @"for \(int i = 0; i < steps; i\+\+\) \{\s*([^\n]*)")]
    private static partial Regex VolumeLoopPattern();
}
