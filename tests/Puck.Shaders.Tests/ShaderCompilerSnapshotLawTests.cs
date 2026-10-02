using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// A compile runs over a snapshot of its closure, and DXC joins a relative include to the including file's directory
/// before it normalizes the result. A source deep in a tree whose include climbs back out still compiles, where the
/// joined path passes the Windows path limit though the normalized one does not, because the snapshot names each
/// include by its normalized snapshot path; and an error in that include is reported at the include's own path.
/// </summary>
public sealed class ShaderCompilerSnapshotLawTests {
    // The snapshot directory's name under the cache, the build's own layout: .build-<12 hex>/src/.
    private const int SnapshotPrefixLength = 27;
    private const int SegmentLength = 10;

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AnIncludeThatClimbsOutOfADeepDirectoryCompilesAndReportsAtItsOwnPath(bool broken) {
        using var scratch = new TemporaryDirectory(prefix: "puck-deep-");
        var root = scratch.RootPath;

        // As many directories as keep the snapshot's source path under the limit; the relative include from the
        // deepest of them then joins past it.
        var depth = ((221 - root.Length) / (SegmentLength + 1));
        var segments = Enumerable.Range(count: depth, start: 0).Select(selector: static index => $"d{index:D9}").ToArray();
        var directory = Path.Combine(paths: [root, .. segments]);
        var include = Path.Combine(path1: root, path2: "inc", path3: "i.hlsli");
        var source = Path.Combine(path1: directory, path2: "s.hlsl");
        var relative = (string.Concat(values: Enumerable.Repeat(count: depth, element: "../")) + "inc/i.hlsli");

        var snapshotDirectory = (directory.Length + SnapshotPrefixLength);

        Assert.True(condition: ((snapshotDirectory + "/s.hlsl".Length) < 260));
        Assert.True(condition: (((snapshotDirectory + 1) + relative.Length) > 260));

        Directory.CreateDirectory(path: directory);
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: include)!);
        File.WriteAllText(contents: (broken ? "static const uint Scale = undefinedName;\n" : "static const uint Scale = 2u;\n"), path: include);

        var text = $"#include \"{relative}\"\nRWStructuredBuffer<uint> output : register(u0);\n[numthreads(1, 1, 1)]\nvoid CSMain() {{ output[0] = Scale; }}\n";

        File.WriteAllText(contents: text, path: source);

        var compiled = new ShaderCompiler(cacheDirectory: Path.Combine(path1: root, path2: "c")).Compile(descriptor: new ShaderCompilationRequest(
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
}
