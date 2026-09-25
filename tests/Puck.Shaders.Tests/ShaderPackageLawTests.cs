using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Puck.Hosting;

namespace Puck.Shaders.Tests;

/// <summary>Laws for the source closure, the compile identity, and <c>puck.shader.package.v1</c> packages, over the
/// fixtures under <c>Assets/ShaderPackages</c>. A fake tool runner stands in for DXC: its bytecode is a hash of the
/// input it is handed, so equal bytecode means an equal compile input. The last law runs the installed toolchain.</summary>
public sealed partial class ShaderPackageLawTests {
    public static TheoryData<string, int, int, int, int, int> Limits => new() {
        { ShaderClosureRefusedException.ExpandedBytes, 1024, int.MaxValue, 256, 32, 512 },
        { ShaderClosureRefusedException.FileBytes, ((16 * 1024) * 1024), 400, 256, 32, 512 },
        { ShaderClosureRefusedException.DependencyCount, ((16 * 1024) * 1024), int.MaxValue, 2, 32, 512 },
        { ShaderClosureRefusedException.IncludeDepth, ((16 * 1024) * 1024), int.MaxValue, 256, 1, 512 },
        { ShaderClosureRefusedException.CompileSteps, ((16 * 1024) * 1024), int.MaxValue, 256, 32, 3 },
    };
    public static TheoryData<string> MalformedManifests => [
        "backslash-path.json",
        "dot-segment.json",
        "duplicate-path.json",
        "empty-passes.json",
        "escaping-path.json",
        "manifest-as-file.json",
        "missing-member.json",
        "truncated.json",
        "unknown-member.json",
        "unknown-stage.json",
        "unlisted-document.json",
        "uppercase-pin.json",
        "wrong-schema.json",
    ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Dictionary<string, byte[]> Bytecode(CompiledShaderPipeline pipeline) {
        var result = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal);

        foreach (var (name, shader) in pipeline.Shaders) {
            foreach (var (stage, spirv) in shader.SpirvByStage) {
                result[$"{name}.{stage}.spv"] = spirv.ToArray();
            }

            foreach (var (stage, dxil) in shader.DxilByStage) {
                result[$"{name}.{stage}.dxil"] = dxil.ToArray();
            }
        }

        return result;
    }
    private static void AssertRefused(ShaderPackageResult result, string code) {
        Assert.Null(@object: result.Pipeline);
        Assert.Equal(
            actual: result.Status,
            expected: ShaderPipelineLoadStatus.Failed
        );
        Assert.Equal(
            actual: result.Code,
            expected: code
        );
        Assert.Contains(
            actualString: result.Message,
            expectedSubstring: $"[{code}]"
        );
    }
    private static Dictionary<string, byte[]> PackageFiles(string directory) =>
        Directory.EnumerateFiles(
            path: directory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        ).ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: File.ReadAllBytes,
            keySelector: path => Path.GetRelativePath(
                path: path,
                relativeTo: directory
            ).Replace(
                newChar: '/',
                oldChar: '\\'
            )
        );

    [Fact]
    public void Every_include_line_joins_the_closure_including_transitive_ones() {
        using var fixture = new Fixture(name: "transitive");
        var present = fixture.PathOf(logicalPath: "present.hlsl");
        var closure = ShaderSourceClosure.Collect(
            limits: ShaderSourceLimits.Default,
            sources: [(present, File.ReadAllText(path: present))]
        );

        // tone.hlsli is named only on the fourth line, and math.hlsli only by common.hlsli.
        Assert.Equal(
            actual: closure.Includes.Select(selector: include => fixture.LogicalPathOf(path: include.Path)),
            expected: ["lib/common.hlsli", "lib/math.hlsli", "lib/tone.hlsli"]
        );
        Assert.Equal(
            actual: closure.Depth,
            expected: 2
        );
        Assert.Equal(
            actual: closure.Includes[2].ContentHash,
            expected: ShaderSourceClosure.HashOf(text: File.ReadAllText(path: fixture.PathOf(logicalPath: "lib/tone.hlsli")))
        );
    }
    [Fact]
    public async Task An_edit_to_an_include_below_the_first_line_invalidates_the_cache() {
        using var fixture = new Fixture(name: "transitive");
        var runner = new PackageRunner();
        var compiler = fixture.Compiler(runner: runner);
        var request = fixture.Fragment(logicalPath: "present.hlsl");

        Assert.True(condition: (await compiler.CompileAsync(cancellationToken: Token, descriptor: request)).IsSuccess);
        var cold = runner.CompileRuns;

        Assert.True(condition: (await compiler.CompileAsync(cancellationToken: Token, descriptor: request)).IsSuccess);
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: cold
        );
        File.AppendAllText(
            contents: "\n// edited\n",
            path: fixture.PathOf(logicalPath: "lib/tone.hlsli")
        );
        Assert.True(condition: (await compiler.CompileAsync(cancellationToken: Token, descriptor: request)).IsSuccess);
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: (cold * 2)
        );
    }
    [Fact]
    public async Task A_missing_include_is_refused_by_name_before_any_tool_runs() {
        using var fixture = new Fixture(name: "missing-include");
        var runner = new PackageRunner();
        var result = await fixture.Compiler(runner: runner).CompileAsync(
            cancellationToken: Token,
            descriptor: fixture.Compute(logicalPath: "fill.hlsl")
        );

        Assert.False(condition: result.IsSuccess);
        Assert.Contains(
            collection: result.Diagnostics,
            filter: static diagnostic => (diagnostic.IsError && diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: $"[{ShaderClosureRefusedException.IncludeMissing}]") && diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "absent.hlsli"))
        );
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: 0
        );

        var package = await new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: runner)).BuildAsync(
            cancellationToken: Token,
            output: fixture.Output(name: "package"),
            source: fixture.PathOf(logicalPath: "missing.graph.json")
        );

        AssertRefused(
            code: ShaderClosureRefusedException.IncludeMissing,
            result: package
        );
    }
    [Fact]
    public async Task A_warm_cache_cannot_conceal_a_deleted_dependency() {
        using var fixture = new Fixture(name: "transitive");
        var runner = new PackageRunner();
        var compiler = fixture.Compiler(runner: runner);
        var request = fixture.Compute(logicalPath: "blur.hlsl");

        Assert.True(condition: (await compiler.CompileAsync(cancellationToken: Token, descriptor: request)).IsSuccess);
        var warm = runner.CompileRuns;

        File.Delete(path: fixture.PathOf(logicalPath: "lib/math.hlsli"));
        var result = await compiler.CompileAsync(
            cancellationToken: Token,
            descriptor: request
        );

        Assert.False(condition: result.IsSuccess);
        Assert.Contains(
            collection: result.Diagnostics,
            filter: static diagnostic => (diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: $"[{ShaderClosureRefusedException.IncludeMissing}]") && diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: "math.hlsli"))
        );
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: warm
        );
    }
    [Fact]
    public async Task A_warm_package_cannot_conceal_a_deleted_dependency() {
        using var fixture = new Fixture(name: "transitive");
        var runner = new PackageRunner();
        var packager = new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: runner));
        var output = fixture.Output(name: "package");

        Assert.Equal(
            actual: (await packager.BuildAsync(cancellationToken: Token, output: output, source: fixture.PathOf(logicalPath: "transitive.graph.json"))).Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        Assert.Equal(
            actual: (await packager.LoadAsync(cancellationToken: Token, package: output)).Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        File.Delete(path: Path.Combine(
            path1: output,
            path2: "lib",
            path3: "math.hlsli"
        ));
        AssertRefused(
            code: ShaderClosureRefusedException.PackageFileMissing,
            result: await packager.LoadAsync(cancellationToken: Token, package: output)
        );
    }
    [Fact]
    public async Task A_package_loads_with_no_compiler_and_runs_no_tool() {
        using var fixture = new Fixture(name: "transitive");
        var toolchain = fixture.Output(name: "toolchain");

        Directory.CreateDirectory(path: toolchain);
        foreach (var tool in ((string[])["dxc", "dxc.exe"])) {
            File.WriteAllBytes(
                bytes: [],
                path: Path.Combine(
                    path1: toolchain,
                    path2: tool
                )
            );
        }

        var runner = new PackageRunner();
        var compiler = fixture.Compiler(
            runner: runner,
            toolchain: toolchain
        );
        var request = fixture.Compute(logicalPath: "blur.hlsl");
        var packager = new ShaderPackager(compiler: compiler, reflectDxil: false);
        var output = fixture.Output(name: "package");

        Assert.True(condition: (await compiler.CompileAsync(cancellationToken: Token, descriptor: request)).IsSuccess);
        Assert.Equal(
            actual: (await packager.BuildAsync(cancellationToken: Token, output: output, source: fixture.PathOf(logicalPath: "transitive.graph.json"))).Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        File.Delete(path: Path.Combine(path1: toolchain, path2: "dxc"));
        File.Delete(path: Path.Combine(path1: toolchain, path2: "dxc.exe"));

        var missing = await Assert.ThrowsAsync<ShaderToolMissingException>(testCode: () => compiler.CompileAsync(
            cancellationToken: Token,
            descriptor: request
        ));

        Assert.Equal(
            actual: missing.Tool,
            expected: "dxc"
        );

        // The source compile needs the compiler; the package's binaries do not.
        var runs = runner.CompileRuns;
        var loaded = await packager.LoadAsync(
            cancellationToken: Token,
            package: output
        );

        Assert.True(
            condition: (loaded.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: loaded.Message
        );
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: runs
        );
    }
    [Fact]
    public async Task An_include_outside_the_closure_is_refused_and_a_wider_root_admits_it() {
        using var fixture = new Fixture(name: "outside");
        var packager = new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: new PackageRunner()));
        var source = fixture.PathOf(logicalPath: "pipeline/outside.graph.json");

        AssertRefused(
            code: ShaderClosureRefusedException.OutsideClosure,
            result: await packager.BuildAsync(cancellationToken: Token, output: fixture.Output(name: "narrow"), source: source)
        );

        var wide = await packager.BuildAsync(
            cancellationToken: Token,
            output: fixture.Output(name: "wide"),
            root: fixture.Root,
            source: source
        );

        Assert.Equal(
            actual: wide.Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        Assert.Equal(
            actual: wide.Manifest!.Document,
            expected: "pipeline/outside.graph.json"
        );
        Assert.Equal(
            actual: wide.Manifest.Files.Select(selector: static file => file.Path),
            expected: ["pipeline/fill.hlsl", "pipeline/outside.graph.json", "shared/outside.hlsli"]
        );
        Assert.Equal(
            actual: (await packager.LoadAsync(cancellationToken: Token, package: fixture.Output(name: "wide"))).Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
    }
    [MemberData(nameof(Limits))]
    [Theory]
    public async Task Each_limit_refuses_by_name_when_building_and_when_loading(string code, int expandedBytes, int fileBytes, int dependencies, int depth, int steps) {
        using var fixture = new Fixture(name: "transitive");
        var limits = new ShaderSourceLimits(
            MaxCompileSteps: steps,
            MaxDependencies: dependencies,
            MaxExpandedBytes: expandedBytes,
            MaxFileBytes: fileBytes,
            MaxIncludeDepth: depth
        );
        var runner = new PackageRunner();
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");

        AssertRefused(
            code: code,
            result: await new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: runner), limits: limits).BuildAsync(cancellationToken: Token, output: fixture.Output(name: "limited"), source: source)
        );
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: 0
        );
        Assert.False(condition: Directory.Exists(path: fixture.Output(name: "limited")));

        var output = fixture.Output(name: "package");

        Assert.Equal(
            actual: (await new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: runner)).BuildAsync(cancellationToken: Token, output: output, source: source)).Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );

        var runs = runner.CompileRuns;

        AssertRefused(
            code: code,
            result: await new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: runner), limits: limits).LoadAsync(cancellationToken: Token, package: output)
        );
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: runs
        );
    }
    [MemberData(nameof(MalformedManifests))]
    [Theory]
    public async Task A_malformed_manifest_is_refused_by_name(string manifest) {
        using var fixture = new Fixture(name: "transitive");
        var runner = new PackageRunner();
        var packager = new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: runner));
        var output = fixture.Output(name: "package");

        Assert.Equal(
            actual: (await packager.BuildAsync(cancellationToken: Token, output: output, source: fixture.PathOf(logicalPath: "transitive.graph.json"))).Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        File.Copy(
            destFileName: Path.Combine(
                path1: output,
                path2: ShaderPackageManifest.FileName
            ),
            overwrite: true,
            sourceFileName: Fixture.Asset(logicalPath: $"malformed/{manifest}")
        );

        var runs = runner.CompileRuns;

        AssertRefused(
            code: ShaderClosureRefusedException.PackageMalformed,
            result: await packager.LoadAsync(cancellationToken: Token, package: output)
        );
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: runs
        );
    }
    [Fact]
    public void The_malformed_fixtures_differ_from_a_well_formed_manifest_by_their_one_defect() {
        var manifest = ShaderPackager.Read(utf8: File.ReadAllBytes(path: Fixture.Asset(logicalPath: "malformed/well-formed.json")));

        Assert.Equal(
            actual: manifest.Files.Count,
            expected: 3
        );
        Assert.Equal(
            actual: ShaderPackager.Read(utf8: ShaderPackager.Write(manifest: manifest)),
            expected: manifest,
            comparer: new ManifestComparer()
        );
    }
    [Fact]
    public async Task An_altered_missing_or_unreached_package_file_is_refused_by_name() {
        using var fixture = new Fixture(name: "transitive");
        var runner = new PackageRunner();
        var packager = new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: runner));
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");
        var output = fixture.Output(name: "package");

        async Task Rebuild() => Assert.Equal(
            actual: (await packager.BuildAsync(cancellationToken: Token, output: output, source: source)).Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );

        await Rebuild();
        File.AppendAllText(
            contents: "\n// altered\n",
            path: Path.Combine(path1: output, path2: "blur.hlsl")
        );
        AssertRefused(
            code: ShaderClosureRefusedException.PackageFilePin,
            result: await packager.LoadAsync(cancellationToken: Token, package: output)
        );

        await Rebuild();
        File.Delete(path: Path.Combine(path1: output, path2: "lib", path3: "tone.hlsli"));
        AssertRefused(
            code: ShaderClosureRefusedException.PackageFileMissing,
            result: await packager.LoadAsync(cancellationToken: Token, package: output)
        );

        // A listed file no pass reaches: pinned correctly, so only the closure comparison can refuse it.
        await Rebuild();
        const string Extra = "float Unused() { return 0.0; }\n";
        var manifestPath = Path.Combine(
            path1: output,
            path2: ShaderPackageManifest.FileName
        );
        var manifest = ShaderPackager.Read(utf8: File.ReadAllBytes(path: manifestPath));

        File.WriteAllText(
            contents: Extra,
            path: Path.Combine(path1: output, path2: "lib", path3: "extra.hlsli")
        );
        File.WriteAllBytes(
            bytes: ShaderPackager.Write(manifest: manifest with {
                Files = [.. manifest.Files, new ShaderPackageFile(Bytes: Encoding.UTF8.GetByteCount(s: Extra), Path: "lib/extra.hlsli", Pin: ("sha256/" + ShaderSourceClosure.HashOf(text: Extra)))],
            }),
            path: manifestPath
        );
        AssertRefused(
            code: ShaderClosureRefusedException.PackageClosure,
            result: await packager.LoadAsync(cancellationToken: Token, package: output)
        );
    }
    [Fact]
    public async Task A_package_loads_only_under_the_capabilities_and_interfaces_it_records() {
        using var fixture = new Fixture(name: "transitive");
        var runner = new PackageRunner();
        var packager = new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: runner));
        var output = fixture.Output(name: "package");
        var built = await packager.BuildAsync(
            cancellationToken: Token,
            output: output,
            source: fixture.PathOf(logicalPath: "transitive.graph.json")
        );
        var manifestPath = Path.Combine(
            path1: output,
            path2: ShaderPackageManifest.FileName
        );
        var manifest = built.Manifest!;

        // The manifest's stages are the compiler's own identity records, not a copy assembled beside them, and each
        // pass records the interface its binaries read.
        foreach (var pass in manifest.Passes) {
            var planned = built.Pipeline!.Plan.Passes.Single(predicate: candidate => (candidate.Name == pass.Name));

            Assert.Same(
                actual: pass.Stages,
                expected: built.Pipeline.Shaders[pass.Name].Identity!.Stages
            );
            Assert.Equal(
                actual: pass.Interface.Pin,
                expected: planned.Parameters.Interface.Hash.ToString()
            );
            Assert.Equal(
                actual: File.ReadAllText(path: Path.Combine(path1: output, path2: pass.Declarations.Path)),
                expected: ShaderInterfaceHlsl.Generate(shaderInterface: planned.Parameters.Interface)
            );
        }

        Assert.Equal(
            actual: manifest.Compiler.Tools.Select(selector: static tool => tool.Name),
            expected: [ShaderCompiler.DxcTool]
        );

        // A different tool version where the package loads changes nothing: the load runs no tool.
        runner.Versions[ShaderCompiler.DxcTool] = "dxcompiler: another";
        Assert.Equal(
            actual: (await packager.LoadAsync(cancellationToken: Token, package: output)).Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        runner.Versions.Clear();

        File.WriteAllBytes(
            bytes: ShaderPackager.Write(manifest: manifest with { Capabilities = manifest.Capabilities with { Buffers = true } }),
            path: manifestPath
        );
        AssertRefused(
            code: ShaderClosureRefusedException.PackageCapabilities,
            result: await packager.LoadAsync(cancellationToken: Token, package: output)
        );

        // Binaries built for another interface: the pin is well formed and the file matches it, but the document's pass
        // now reads a different interface.
        var blur = manifest.Passes[0];
        var other = ShaderFrameInterface.For(
            config: new Dictionary<string, ShaderConfigField>(comparer: StringComparer.Ordinal) { ["radius"] = new(Type: ShaderValueType.Float) },
            name: blur.Interface.Path[..blur.Interface.Path.IndexOf(value: '.')]
        ).ToJson();

        File.WriteAllText(
            contents: other,
            path: Path.Combine(path1: output, path2: blur.Interface.Path)
        );
        File.WriteAllBytes(
            bytes: ShaderPackager.Write(manifest: manifest with {
                Passes = [blur with { Interface = blur.Interface with { Bytes = Encoding.UTF8.GetByteCount(s: other), Pin = ("sha256/" + ShaderSourceClosure.HashOf(text: other)) } }, .. manifest.Passes.Skip(count: 1)],
            }),
            path: manifestPath
        );
        AssertRefused(
            code: ShaderClosureRefusedException.PackageInterface,
            result: await packager.LoadAsync(cancellationToken: Token, package: output)
        );
    }
    [Fact]
    public async Task A_relocated_package_loads_with_its_source_tree_gone() {
        using var fixture = new Fixture(name: "transitive");
        var runner = new PackageRunner();
        var built = await new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: runner)).BuildAsync(
            cancellationToken: Token,
            output: fixture.Output(name: "package"),
            source: fixture.PathOf(logicalPath: "transitive.graph.json")
        );

        Assert.Equal(
            actual: built.Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );

        using var clean = new Scratch();
        var relocated = Path.Combine(
            path1: clean.Path,
            path2: "moved"
        );

        Directory.Move(
            destDirName: relocated,
            sourceDirName: fixture.Output(name: "package")
        );
        fixture.DeleteSources();

        // A fresh cache, so the relocated sources are what compiles.
        var loaded = await new ShaderPackager(compiler: new ShaderCompiler(
            cacheDirectory: Path.Combine(path1: clean.Path, path2: "cache"),
            processRunner: new PackageRunner()
        )).LoadAsync(
            cancellationToken: Token,
            package: relocated
        );

        Assert.Equal(
            actual: loaded.Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        Assert.Equal(
            actual: ShaderPackager.Write(manifest: loaded.Manifest!),
            expected: ShaderPackager.Write(manifest: built.Manifest!)
        );
        Assert.Equal(
            actual: loaded.Pipeline!.Plan.PassOrder,
            expected: built.Pipeline!.Plan.PassOrder
        );
        Assert.Equal(
            actual: Bytecode(pipeline: loaded.Pipeline),
            expected: Bytecode(pipeline: built.Pipeline)
        );
    }
    [Fact]
    public async Task A_package_is_named_by_its_directory_and_read_as_a_pipeline_source() {
        using var fixture = new Fixture(name: "transitive");
        var packager = new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: new PackageRunner()));
        var built = await packager.BuildAsync(
            cancellationToken: Token,
            output: fixture.Output(name: "package"),
            source: fixture.PathOf(logicalPath: "transitive.graph.json")
        );

        Assert.Equal(
            actual: built.Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );

        using var clean = new Scratch();
        var relocated = Path.Combine(
            path1: clean.Path,
            path2: "moved"
        );

        Directory.Move(
            destDirName: relocated,
            sourceDirName: fixture.Output(name: "package")
        );
        fixture.DeleteSources();

        // The directory is the one spelling: its manifest file names no package.
        Assert.True(condition: ShaderPackager.IsPackage(path: relocated));
        AssertRefused(
            code: ShaderClosureRefusedException.PackageMalformed,
            result: await packager.LoadAsync(cancellationToken: Token, package: Path.Combine(path1: relocated, path2: ShaderPackageManifest.FileName))
        );

        // A source read of the package is its document's definition, named by the manifest, and its identity pins the
        // canonical manifest, whatever the instance is called.
        Assert.True(
            condition: ShaderPipelineSource.TryRead(name: "instance", path: relocated, reason: out var reason, source: out var source),
            userMessage: reason
        );
        Assert.Equal(
            actual: source.Definition.Name,
            expected: built.Manifest!.Name
        );
        Assert.Equal(
            actual: source.SourceIdentity,
            expected: Puck.Assets.ContentPin.Compute(content: ShaderPackager.Write(manifest: built.Manifest)).ToString()
        );

        var loaded = packager.LoadSource(
            cancellationToken: Token,
            name: "instance",
            path: relocated
        );

        Assert.True(
            condition: (loaded.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: loaded.Message
        );
        Assert.Equal(
            actual: loaded.Dependencies.Select(selector: path => Path.GetRelativePath(path: path, relativeTo: relocated).Replace(newChar: '/', oldChar: '\\')).Order(comparer: StringComparer.Ordinal),
            expected: [.. PackageFiles(directory: relocated).Keys.Order(comparer: StringComparer.Ordinal)]
        );

        // An altered file refuses by its code through both doors a World uses.
        File.AppendAllText(
            contents: "\n// altered\n",
            path: Path.Combine(path1: relocated, path2: "blur.hlsl")
        );
        Assert.False(condition: ShaderPipelineSource.TryRead(name: "instance", path: relocated, reason: out reason, source: out _));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: $"[{ShaderClosureRefusedException.PackageFilePin}]"
        );

        var refused = packager.LoadSource(
            cancellationToken: Token,
            name: "instance",
            path: relocated
        );

        Assert.Equal(
            actual: refused.Status,
            expected: ShaderPipelineLoadStatus.Failed
        );
        Assert.Contains(
            actualString: refused.Message,
            expectedSubstring: $"[{ShaderClosureRefusedException.PackageFilePin}]"
        );
    }
    [Fact]
    public async Task A_package_records_exactly_the_stages_the_loader_compiles() {
        using var fixture = new Fixture(name: "transitive");
        var built = await new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: new PackageRunner())).BuildAsync(
            cancellationToken: Token,
            output: fixture.Output(name: "package"),
            source: fixture.PathOf(logicalPath: "transitive.graph.json")
        );

        Assert.Equal(
            actual: built.Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );

        foreach (var planned in built.Pipeline!.Plan.Passes) {
            var stages = ShaderPipelineLoader.StagesOf(
                pass: planned.Declaration,
                source: string.Empty,
                sourcePath: fixture.PathOf(logicalPath: planned.Declaration.Source)
            );

            Assert.Equal(
                actual: built.Manifest!.Passes.Single(predicate: pass => (pass.Name == planned.Name)).Stages.Select(selector: static stage => (stage.Stage, stage.EntryPoint)),
                expected: stages.Select(selector: static stage => (stage.Stage, stage.EntryPoint))
            );
        }

        // The fullscreen pass compiles the loader's HLSL vertex stage before its own fragment stage.
        Assert.Equal(
            actual: ShaderPipelineLoader.StagesOf(
                pass: built.Pipeline.Plan.Passes.Single(predicate: static pass => (pass.Name == "present")).Declaration,
                source: string.Empty,
                sourcePath: fixture.PathOf(logicalPath: "present.hlsl")
            ).Select(selector: static stage => (stage.Stage, stage.EntryPoint)),
            expected: [(ShaderStage.Vertex, "main"), (ShaderStage.Fragment, "main")]
        );
    }
    [Fact]
    public async Task A_clean_cache_and_a_warm_cache_package_the_same_bytes() {
        using var fixture = new Fixture(name: "transitive");
        var runner = new PackageRunner();
        var compiler = fixture.Compiler(runner: runner);
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");
        var cold = await new ShaderPackager(compiler: compiler, reflectDxil: false).BuildAsync(cancellationToken: Token, output: fixture.Output(name: "cold"), source: source);
        var runs = runner.CompileRuns;
        var warm = await new ShaderPackager(compiler: compiler, reflectDxil: false).BuildAsync(cancellationToken: Token, output: fixture.Output(name: "warm"), source: source);

        Assert.Equal(
            actual: runner.CompileRuns,
            expected: runs
        );

        var fresh = await new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: new PackageRunner(), cache: "fresh-cache")).BuildAsync(cancellationToken: Token, output: fixture.Output(name: "fresh"), source: source);

        foreach (var other in ((string[])["warm", "fresh"])) {
            Assert.Equal(
                actual: PackageFiles(directory: fixture.Output(name: other)),
                expected: PackageFiles(directory: fixture.Output(name: "cold"))
            );
        }

        Assert.Equal(
            actual: Bytecode(pipeline: warm.Pipeline!),
            expected: Bytecode(pipeline: cold.Pipeline!)
        );
        Assert.Equal(
            actual: Bytecode(pipeline: fresh.Pipeline!),
            expected: Bytecode(pipeline: cold.Pipeline!)
        );

        var loadedWarm = await new ShaderPackager(compiler: compiler, reflectDxil: false).LoadAsync(cancellationToken: Token, package: fixture.Output(name: "cold"));
        var loadedClean = await new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: new PackageRunner(), cache: "clean-cache")).LoadAsync(cancellationToken: Token, package: fixture.Output(name: "cold"));

        Assert.Equal(
            actual: Bytecode(pipeline: loadedClean.Pipeline!),
            expected: Bytecode(pipeline: loadedWarm.Pipeline!)
        );
    }
    [Fact]
    public async Task Image_only_authoring_packages_and_reloads_without_a_surface_or_importer() {
        using var fixture = new Fixture(name: "image-only");
        var built = await new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: new PackageRunner())).BuildAsync(
            cancellationToken: Token,
            output: fixture.Output(name: "package"),
            source: fixture.PathOf(logicalPath: "image.hlsl")
        );

        Assert.Equal(
            actual: built.Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        Assert.Equal(
            actual: built.Manifest!.Document,
            expected: "image.hlsl"
        );
        Assert.Equal(
            actual: built.Manifest.Files.Select(selector: static file => file.Path),
            expected: ["image.hlsl"]
        );
        Assert.Equal(
            actual: built.Manifest.Capabilities.ImageFormats,
            expected: ["R8G8B8A8Unorm"]
        );

        using var clean = new Scratch();
        var relocated = Path.Combine(
            path1: clean.Path,
            path2: "image"
        );

        Directory.Move(
            destDirName: relocated,
            sourceDirName: fixture.Output(name: "package")
        );
        fixture.DeleteSources();

        var loaded = await new ShaderPackager(compiler: new ShaderCompiler(
            cacheDirectory: Path.Combine(path1: clean.Path, path2: "cache"),
            processRunner: new PackageRunner()
        )).LoadAsync(
            cancellationToken: Token,
            package: relocated
        );

        Assert.Equal(
            actual: loaded.Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        Assert.Equal(
            actual: loaded.Pipeline!.Plan.Outputs,
            expected: built.Pipeline!.Plan.Outputs
        );
    }
    [Fact]
    public async Task A_failed_build_keeps_the_last_complete_package_and_never_replaces_other_files() {
        using var fixture = new Fixture(name: "transitive");
        var packager = new ShaderPackager(reflectDxil: false, compiler: fixture.Compiler(runner: new PackageRunner()));
        var output = fixture.Output(name: "package");
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");

        Assert.Equal(
            actual: (await packager.BuildAsync(cancellationToken: Token, output: output, source: source)).Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );

        var before = PackageFiles(directory: output);

        File.Delete(path: fixture.PathOf(logicalPath: "lib/tone.hlsli"));
        AssertRefused(
            code: ShaderClosureRefusedException.IncludeMissing,
            result: await packager.BuildAsync(cancellationToken: Token, output: output, source: source)
        );
        Assert.Equal(
            actual: PackageFiles(directory: output),
            expected: before
        );
        // No staging or replaced directory outlives the build.
        Assert.Equal(
            actual: Directory.GetDirectories(path: fixture.Output(name: string.Empty)).Select(selector: Path.GetFileName).Where(predicate: static name => name!.StartsWith(comparisonType: StringComparison.Ordinal, value: "package")),
            expected: ["package"]
        );

        var occupied = fixture.Output(name: "occupied");

        Directory.CreateDirectory(path: occupied);
        File.WriteAllText(
            contents: "not a package",
            path: Path.Combine(path1: occupied, path2: "notes.txt")
        );
        File.WriteAllText(
            contents: "#ifndef FIXTURE_TONE_HLSLI\n#define FIXTURE_TONE_HLSLI\nfloat4 Tone(float4 color) { return color; }\n#endif\n",
            path: fixture.PathOf(logicalPath: "lib/tone.hlsli")
        );
        AssertRefused(
            code: ShaderClosureRefusedException.PackageOutput,
            result: await packager.BuildAsync(cancellationToken: Token, output: occupied, source: source)
        );
        Assert.Equal(
            actual: Directory.GetFiles(path: occupied).Select(selector: Path.GetFileName),
            expected: ["notes.txt"]
        );
    }
    [Fact]
    public async Task Native_tools_package_a_relocated_closure_to_the_same_bytecode() {
        var dxc = new ShaderToolchain().Locate(name: "dxc");

        Assert.SkipWhen(
            condition: (dxc is null),
            reason: "DXC is required for this native packaging law."
        );

        using var fixture = new Fixture(name: "transitive");
        var built = await new ShaderPackager(compiler: new ShaderCompiler(cacheDirectory: fixture.Output(name: "cache"))).BuildAsync(
            cancellationToken: Token,
            output: fixture.Output(name: "package"),
            source: fixture.PathOf(logicalPath: "transitive.graph.json")
        );

        Assert.True(
            condition: (built.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: built.Message
        );
        Assert.Equal(
            actual: built.Manifest!.Compiler.Tools.Single().Version,
            expected: await new ShaderCompiler(cacheDirectory: fixture.Output(name: "cache")).ToolVersionAsync(cancellationToken: Token, tool: ShaderCompiler.DxcTool)
        );

        using var clean = new Scratch();
        var relocated = Path.Combine(
            path1: clean.Path,
            path2: "moved"
        );

        Directory.Move(
            destDirName: relocated,
            sourceDirName: fixture.Output(name: "package")
        );
        fixture.DeleteSources();

        var loaded = await new ShaderPackager(compiler: new ShaderCompiler(cacheDirectory: Path.Combine(path1: clean.Path, path2: "cache"))).LoadAsync(
            cancellationToken: Token,
            package: relocated
        );

        Assert.True(
            condition: (loaded.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: loaded.Message
        );
        Assert.Equal(
            actual: Bytecode(pipeline: loaded.Pipeline!),
            expected: Bytecode(pipeline: built.Pipeline!)
        );
    }

    /// <summary>A copy of one fixture directory in a scratch directory, beside the outputs a law writes.</summary>
    private sealed class Fixture : IDisposable {
        private readonly Scratch m_scratch = new();

        public Fixture(string name) {
            Root = System.IO.Path.Combine(
                path1: m_scratch.Path,
                path2: "source"
            );
            Copy(
                destination: Root,
                source: Asset(logicalPath: name)
            );
        }

        public string Root { get; }

        public static string Asset(string logicalPath) => System.IO.Path.Combine(
            path1: AppContext.BaseDirectory,
            path2: "Assets",
            path3: "ShaderPackages",
            path4: logicalPath
        );

        private static void Copy(string source, string destination) {
            Directory.CreateDirectory(path: destination);

            foreach (var file in Directory.EnumerateFiles(path: source)) {
                File.Copy(
                    destFileName: System.IO.Path.Combine(path1: destination, path2: System.IO.Path.GetFileName(path: file)),
                    sourceFileName: file
                );
            }

            foreach (var directory in Directory.EnumerateDirectories(path: source)) {
                Copy(
                    destination: System.IO.Path.Combine(path1: destination, path2: System.IO.Path.GetFileName(path: directory)),
                    source: directory
                );
            }
        }

        public ShaderCompiler Compiler(PackageRunner runner, string? toolchain = null, string cache = "cache") => new(
            cacheDirectory: Output(name: cache),
            processRunner: runner,
            toolchainDirectory: toolchain
        );
        public ShaderCompilationRequest Compute(string logicalPath) => new(
            name: System.IO.Path.GetFileNameWithoutExtension(path: logicalPath),
            stages: [new ShaderStageSource(
                ShaderStage.Compute,
                PathOf(logicalPath: logicalPath),
                File.ReadAllText(path: PathOf(logicalPath: logicalPath))
            )]
        );
        public void DeleteSources() => Directory.Delete(
            path: Root,
            recursive: true
        );
        public void Dispose() => m_scratch.Dispose();
        public ShaderCompilationRequest Fragment(string logicalPath) => new(
            name: System.IO.Path.GetFileNameWithoutExtension(path: logicalPath),
            stages: [new ShaderStageSource(
                ShaderStage.Fragment,
                PathOf(logicalPath: logicalPath),
                File.ReadAllText(path: PathOf(logicalPath: logicalPath))
            )]
        );
        public string LogicalPathOf(string path) => System.IO.Path.GetRelativePath(
            path: path,
            relativeTo: Root
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        );
        public string Output(string name) => System.IO.Path.Combine(
            path1: m_scratch.Path,
            path2: "out",
            path3: name
        );
        public string PathOf(string logicalPath) => System.IO.Path.GetFullPath(path: System.IO.Path.Combine(
            path1: Root,
            path2: logicalPath
        ));
    }
    private sealed class Scratch : IDisposable {
        public Scratch() {
            Path = System.IO.Path.Combine(
                path1: System.IO.Path.GetTempPath(),
                path2: ("puck-shader-package-" + Guid.NewGuid().ToString(format: "N")[..12])
            );
            Directory.CreateDirectory(path: Path);
        }

        public string Path { get; }

        public void Dispose() {
            try {
                Directory.Delete(
                    path: Path,
                    recursive: true
                );
            } catch (IOException) { }
        }
    }
    private sealed class ManifestComparer : IEqualityComparer<ShaderPackageManifest> {
        public bool Equals(ShaderPackageManifest? x, ShaderPackageManifest? y) =>
            ((x is not null) && (y is not null) && ShaderPackager.Write(manifest: x).AsSpan().SequenceEqual(other: ShaderPackager.Write(manifest: y)));
        public int GetHashCode(ShaderPackageManifest obj) => 0;
    }
    /// <summary>Stands in for the native tools: a compile writes a hash of the input it is handed and the options it
    /// runs under, and a version query answers from <see cref="Versions"/> or a fixed line per tool.</summary>
    private sealed class PackageRunner : IShaderProcessRunner {
        private int m_compileRuns;

        public ConcurrentDictionary<string, string> Versions { get; } = new(comparer: StringComparer.Ordinal);
        public int CompileRuns => Volatile.Read(location: ref m_compileRuns);

        // A SPIR-V module a reader can walk: the five-word header, then one OpNop instruction whose operands carry the
        // hash, so a package build's reflection finds no binding in it.
        private static byte[] SpirvHolding(byte[] hash) {
            var words = new uint[(6 + (hash.Length / 4))];

            words[0] = 0x07230203u;
            words[1] = 0x00010600u;
            words[5] = (((uint)(words.Length - 5)) << 16) | 0u;
            Buffer.BlockCopy(
                count: hash.Length,
                dst: words,
                dstOffset: 24,
                src: hash,
                srcOffset: 0
            );

            var bytes = new byte[(words.Length * 4)];

            Buffer.BlockCopy(
                count: bytes.Length,
                dst: bytes,
                dstOffset: 0,
                src: words,
                srcOffset: 0
            );

            return bytes;
        }

        public Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();

            var tool = System.IO.Path.GetFileNameWithoutExtension(path: fileName);

            if (arguments is ["--version"] or ["--revision"]) {
                return Task.FromResult(result: new ChildProcessResult(
                    ExitCode: 0,
                    Stderr: string.Empty,
                    Stdout: ((Versions.TryGetValue(key: tool, value: out var version) ? version : $"{tool} fixture 1") + "\n")
                ));
            }

            if (!File.Exists(path: fileName) && System.IO.Path.IsPathRooted(path: fileName)) {
                throw new System.ComponentModel.Win32Exception(message: $"{fileName} was not found");
            }

            Interlocked.Increment(location: ref m_compileRuns);

            string? output = null;
            string? input = null;
            var options = new List<string>();

            for (var index = 0; (index < arguments.Count); index++) {
                if (arguments[index] is "-Fo" or "-o" or "--output") {
                    output = arguments[++index];
                } else if (arguments[index].StartsWith(comparisonType: StringComparison.Ordinal, value: "-I")) {
                } else if (File.Exists(path: arguments[index])) {
                    input = arguments[index];
                } else {
                    options.Add(item: arguments[index]);
                }
            }

            if (output is not null) {
                var hash = SHA256.HashData(source: [.. ((input is null) ? [] : File.ReadAllBytes(path: input)), .. Encoding.UTF8.GetBytes(s: (tool + string.Join(separator: " ", values: options)))]);

                File.WriteAllBytes(
                    bytes: (options.Contains(item: "-spirv")
                        ? SpirvHolding(hash: hash)
                        : hash),
                    path: output
                );
            }

            return Task.FromResult(result: new ChildProcessResult(
                ExitCode: 0,
                Stderr: string.Empty,
                Stdout: string.Empty
            ));
        }
    }
}
