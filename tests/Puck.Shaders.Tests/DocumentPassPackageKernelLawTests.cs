using Puck.Abstractions;
using Puck.Testing;

namespace Puck.Shaders.Tests;

/// <summary>
/// Every engine package's kernel compiles as a document pass: a graph pass naming the kernel's source reads the
/// declarations the loader generates in memory for the interface its source's name spells
/// (<see cref="ShaderFrameInterface.NameOf"/>, <see cref="ShaderPipelineLoader.GeneratedIncludeOf"/>), which replace the
/// package's checked-in include and declare no work counters, since a document pass has none. A kernel that counts its
/// work (<see cref="ShaderWorkCounters"/>) still compiles there, because every generated interface declares the counting
/// functions and one without the counters declares them empty. Each kernel is compiled, in both bytecodes, against the
/// interface a document pass carrying its package's config and pass-group members, less the work counters, generates.
/// </summary>
public sealed class DocumentPassPackageKernelLawTests {
    private const string InterfaceSuffix = ".interface.hlsli";

    // Every package kernel a document pass can compile against generated declarations: a stage source beside its
    // package's checked-in include whose own name spells that package's interface, by repository-relative path.
    private static IReadOnlyDictionary<string, RenderGraphPackage> Kernels() {
        var source = RepositoryPaths.Resolve(relativePath: "src");
        var kernels = new SortedDictionary<string, RenderGraphPackage>(comparer: StringComparer.Ordinal);
        var packages = RenderGraphPackageCatalog.Engine.Packages
            .Where(predicate: static package => (package.Members.Count > 0))
            .ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: static package => package,
                keySelector: static package => ShaderPipelineParameterLayout.ForPackage(
                    config: package.Config,
                    members: package.Members,
                    package: package.Id,
                    pushesIndex: package.PushesIndex
                ).Interface.Name
            );

        foreach (var include in Directory.EnumerateFiles(path: source, searchOption: SearchOption.AllDirectories, searchPattern: ("*" + InterfaceSuffix))) {
            var relative = Path.GetRelativePath(path: include, relativeTo: source).Replace(newChar: '/', oldChar: '\\');

            if (relative.Split(separator: '/').Any(predicate: static segment => (segment is "bin" or "obj"))) {
                continue;
            }

            var name = Path.GetFileName(path: include)[..^InterfaceSuffix.Length];

            if (!packages.TryGetValue(key: name, value: out var package)) {
                continue;
            }

            foreach (var kernel in Directory.EnumerateFiles(path: Path.GetDirectoryName(path: include)!, searchPattern: "*.hlsl")) {
                if ((StageOf(path: kernel) is not null) && string.Equals(a: ShaderFrameInterface.NameOf(sourcePath: kernel), b: name, comparisonType: StringComparison.Ordinal)) {
                    kernels[("src/" + Path.GetRelativePath(path: kernel, relativeTo: source).Replace(newChar: '/', oldChar: '\\'))] = package;
                }
            }
        }

        return kernels;
    }
    // The stage a kernel source compiles as, by its file name, at the entry point the build compiles it with.
    private static (ShaderStage Stage, string EntryPoint)? StageOf(string path) => Path.GetFileName(path: path) switch {
        var file when file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".comp.hlsl") => (ShaderStage.Compute, "CSMain"),
        var file when file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".frag.hlsl") => (ShaderStage.Fragment, "PSMain"),
        var file when file.EndsWith(comparisonType: StringComparison.Ordinal, value: ".vert.hlsl") => (ShaderStage.Vertex, "VSMain"),
        _ => null,
    };

    public static TheoryData<string> KernelPaths => new(values: Kernels().Keys);

    [Fact]
    public void ThePlacementKernelIsAmongThePackageKernels() =>
        Assert.Contains(
            collection: Kernels().Keys,
            expected: "src/Puck.Shaders/Assets/Shaders/Graph/place.comp.hlsl"
        );
    [MemberData(memberName: nameof(KernelPaths))]
    [Theory]
    public async Task APackageKernelCompilesAsADocumentPass(string kernel) {
        Assert.SkipWhen(
            condition: (new ShaderToolchain().Locate(name: ShaderCompiler.DxcTool) is null),
            reason: "DXC is required to compile the package kernels."
        );

        var package = Kernels()[kernel];
        var path = RepositoryPaths.Resolve(relativePath: kernel);

        var (stage, entryPoint) = StageOf(path: path)!.Value;
        var shaderInterface = ShaderPipelineParameterLayout.Grouped(
            config: package.Config,
            interfaceName: ShaderFrameInterface.NameOf(sourcePath: path),
            members: [.. package.Members.Where(predicate: static member => !ShaderWorkCounters.Members.Contains(value: member))]
        ).Interface;

        Assert.False(condition: ShaderWorkCounters.IsDeclaredBy(shaderInterface: shaderInterface));

        using var cache = new TemporaryDirectory(prefix: "puck-document-kernel-");

        var shader = await new ShaderCompiler(cacheDirectory: cache.RootPath).CompileAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            descriptor: new ShaderCompilationRequest(
                generatedIncludes: new Dictionary<string, string>(comparer: PuckPaths.Comparer) {
                    [Path.Combine(path1: Path.GetDirectoryName(path: path)!, path2: ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name))] = ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface),
                },
                name: Path.GetFileName(path: path),
                stages: [new ShaderStageSource(
                    EntryPoint: entryPoint,
                    Path: path,
                    Source: await File.ReadAllTextAsync(cancellationToken: TestContext.Current.CancellationToken, path: path),
                    Stage: stage
                )]
            )
        );

        Assert.True(
            condition: shader.IsSuccess,
            userMessage: string.Join(
                separator: " | ",
                values: shader.Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Path}:{diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}")
            )
        );
    }
}
