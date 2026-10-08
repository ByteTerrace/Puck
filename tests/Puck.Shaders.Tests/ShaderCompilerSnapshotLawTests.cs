using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// A compile runs over a snapshot of its closure, and DXC refuses any path past the Windows limit. The snapshot lies in
/// a short directory of the temporary directory, never under the cache, and a directive whose include would join past
/// the limit names that include by its snapshot path. A source deep in a tree, whose include climbs back out past the
/// limit, compiles into a cache that is itself deep, and an error in that include is reported at the include's own path.
/// </summary>
public sealed class ShaderCompilerSnapshotLawTests {
    private const int SegmentLength = 10;

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnIncludeThatClimbsOutOfADeepDirectoryCompilesIntoADeepCacheAndReportsAtItsOwnPath(bool broken) {
        using var scratch = new TemporaryDirectory(prefix: "puck-deep-");
        var root = scratch.RootPath;

        // A source directory near the limit, whose relative include joins past it, and a cache directory deep enough
        // that mirroring the sources' tree under it would pass the limit too.
        var segments = Segments(prefix: "d", length: (221 - root.Length));
        var directory = Path.Combine(paths: [root, .. segments]);
        var include = Path.Combine(path1: root, path2: "inc", path3: "i.hlsli");
        var source = Path.Combine(path1: directory, path2: "s.hlsl");
        var relative = (string.Concat(values: Enumerable.Repeat(count: segments.Length, element: "../")) + "inc/i.hlsli");
        var cache = Path.Combine(paths: [root, "c", .. Segments(prefix: "c", length: (160 - root.Length))]);

        Assert.True(condition: (((directory.Length + 1) + relative.Length) > 260));
        Assert.True(condition: ((cache.Length + (source.Length - root.Length)) > 260));

        Directory.CreateDirectory(path: directory);
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: include)!);
        File.WriteAllText(contents: (broken ? "static const uint Scale = undefinedName;\n" : "static const uint Scale = 2u;\n"), path: include);

        var text = $"#include \"{relative}\"\nRWStructuredBuffer<uint> output : register(u0);\n[numthreads(1, 1, 1)]\nvoid CSMain() {{ output[0] = Scale; }}\n";

        File.WriteAllText(contents: text, path: source);

        var compiled = new ShaderCompiler(cacheDirectory: cache).Compile(descriptor: new ShaderCompilationRequest(
            name: "deep",
            stages: [new ShaderStageSource(
                EntryPoint: "CSMain",
                Path: source,
                Source: text,
                Stage: ShaderStage.Compute
            )]
        ));

        if (broken) {
            Assert.False(condition: compiled.IsSuccess);
            Assert.Contains(collection: compiled.Diagnostics, filter: diagnostic => (diagnostic.IsError && (diagnostic.Line == 1) && string.Equals(a: diagnostic.Path, b: include, comparisonType: StringComparison.OrdinalIgnoreCase)));
        } else {
            Assert.True(condition: compiled.IsSuccess, userMessage: string.Join(separator: "; ", values: compiled.Diagnostics.Select(selector: static diagnostic => diagnostic.Message)));
        }
    }

    // Directory names of SegmentLength characters, as many as fit in length.
    private static string[] Segments(string prefix, int length) =>
        [.. Enumerable.Range(count: (length / (SegmentLength + 1)), start: 0).Select(selector: index => $"{prefix}{index:D9}")];
}
