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
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the deep snapshot."
        );
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

    // One spelling of a shared header resolves in a checkout's layout (the header in another project) and in a deployed one
    // (the header beside the kernel tree), through the include directory each layout names, and the
    // shared header's own relative include resolves beside it; an error in the shared header reports at its own path.
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Theory]
    public void ASharedHeaderSpelledOnceCompilesInTheCheckoutAndTheDeployedLayout(bool deployed, bool broken) {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile through the include directory."
        );
        using var scratch = new TemporaryDirectory(prefix: "puck-include-");
        var shaders = (deployed
            ? Path.Combine(path1: scratch.RootPath, path2: "app", path3: "Assets", path4: "Shaders")
            : Path.Combine(paths: [scratch.RootPath, "src", "Puck.Shaders", "Assets", "Shaders"]));
        var tree = (deployed
            ? Path.Combine(path1: shaders, path2: "Kernels")
            : Path.Combine(paths: [scratch.RootPath, "src", "Puck.Kernels", "Assets", "Shaders", "Kernels"]));
        var header = Path.Combine(path1: shaders, path2: ShaderIncludeDirectories.SharedDirectoryName, path3: "scale.hlsli");
        var source = Path.Combine(path1: tree, path2: "passes", path3: "s.comp.hlsl");

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: header)!);
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: source)!);
        File.WriteAllText(contents: (broken ? "static const uint Base = undefinedName;\n" : "static const uint Base = 2u;\n"), path: Path.Combine(path1: Path.GetDirectoryName(path: header)!, path2: "base.hlsli"));
        File.WriteAllText(contents: "#include \"base.hlsli\"\nstatic const uint Scale = Base;\n", path: header);

        var text = "#include \"Shared/scale.hlsli\"\nRWStructuredBuffer<uint> output : register(u0);\n[numthreads(1, 1, 1)]\nvoid CSMain() { output[0] = Scale; }\n";

        File.WriteAllText(contents: text, path: source);

        // A runtime reader finds a deployment's directory itself; the build names a checkout's
        // (ShaderIncludeDirectories.CheckoutDirectory below the checkout's root).
        var includeDirectories = (deployed ? ShaderIncludeDirectories.Of(sourcePath: source) : [Path.Combine(path1: scratch.RootPath, path2: ShaderIncludeDirectories.CheckoutDirectory)]);

        Assert.Equal(expected: [Path.GetFullPath(path: shaders)], actual: includeDirectories.Select(selector: static directory => Path.GetFullPath(path: directory)));

        var compiled = new ShaderCompiler(cacheDirectory: Path.Combine(path1: scratch.RootPath, path2: "cache")).Compile(descriptor: new ShaderCompilationRequest(
            name: "shared",
            stages: [new ShaderStageSource(
                EntryPoint: "CSMain",
                IncludeDirectories: includeDirectories,
                Path: source,
                Source: text,
                Stage: ShaderStage.Compute
            )]
        ));

        if (broken) {
            Assert.False(condition: compiled.IsSuccess);
            Assert.Contains(collection: compiled.Diagnostics, filter: diagnostic => (diagnostic.IsError && (diagnostic.Line == 1) && string.Equals(a: diagnostic.Path, b: Path.Combine(path1: Path.GetDirectoryName(path: header)!, path2: "base.hlsli"), comparisonType: StringComparison.OrdinalIgnoreCase)));
        } else {
            Assert.True(condition: compiled.IsSuccess, userMessage: string.Join(separator: "; ", values: compiled.Diagnostics.Select(selector: static diagnostic => diagnostic.Message)));
        }

        // Without the include directory the same spelling is refused by name, before any tool runs.
        var refused = Assert.Throws<ShaderClosureRefusedException>(testCode: () => ShaderSourceClosure.Collect(limits: ShaderSourceLimits.Default, sources: [(source, text)]));

        Assert.Equal(expected: ShaderClosureRefusedException.IncludeMissing, actual: refused.Code);
    }

    // Directory names of SegmentLength characters, as many as fit in length.
    private static string[] Segments(string prefix, int length) =>
        [.. Enumerable.Range(count: (length / (SegmentLength + 1)), start: 0).Select(selector: index => $"{prefix}{index:D9}")];
}
