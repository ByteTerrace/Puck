namespace Puck.Shaders.Tests;

/// <summary>Laws for a build's package store: a source loads from the package keyed by its closure, interfaces and
/// plan names without running a tool, an edited source misses it, and a miss where nothing compiles is refused by
/// name.</summary>
public sealed partial class ShaderPackageLawTests {
    // A toolchain directory holding stand-in dxc files, so the fake runner compiles until DeleteCompiler removes them.
    private static string Toolchain(Fixture fixture) {
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

        return toolchain;
    }
    private static void DeleteCompiler(string toolchain) {
        foreach (var tool in ((string[])["dxc", "dxc.exe"])) {
            File.Delete(path: Path.Combine(
                path1: toolchain,
                path2: tool
            ));
        }
    }

    [Fact]
    public async Task A_stored_package_is_keyed_by_its_source_and_loads_it_with_no_compiler() {
        using var fixture = new Fixture(name: "transitive");
        var toolchain = Toolchain(fixture: fixture);
        var runner = new PackageRunner();
        var compiler = fixture.Compiler(
            runner: runner,
            toolchain: toolchain
        );
        var packager = new ShaderPackager(
            compiler: compiler,
            reflectDxil: false,
            store: fixture.Output(name: "store")
        );
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");
        var (stored, package) = await packager.StoreAsync(
            cancellationToken: Token,
            name: "graph",
            source: source
        );

        Assert.True(
            condition: (stored.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: stored.Message
        );

        var key = packager.KeyOf(
            name: "graph",
            source: source
        );

        Assert.Equal(
            actual: ShaderPackager.KeyOf(manifest: stored.Manifest!),
            expected: key
        );
        Assert.Equal(
            actual: package,
            expected: ShaderPackager.StorePathOf(
                key: key,
                store: packager.Store!
            )
        );

        // A second store keeps the verified package and runs no tool.
        var built = runner.CompileRuns;

        Assert.Equal(
            actual: (await packager.StoreAsync(cancellationToken: Token, name: "graph", source: source)).Result.Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: built
        );

        DeleteCompiler(toolchain: toolchain);

        var loaded = packager.LoadSource(
            cancellationToken: Token,
            name: "graph",
            path: source
        );

        Assert.True(
            condition: (loaded.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: loaded.Message
        );
        Assert.StartsWith(
            actualString: loaded.Message,
            expectedStartString: "from the build's package "
        );
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: built
        );
        // The source's own files are what a watch sees, so an edit reaches the loader.
        Assert.Contains(
            collection: loaded.Dependencies,
            expected: fixture.PathOf(logicalPath: "lib/math.hlsli")
        );
        Assert.DoesNotContain(
            collection: loaded.Dependencies,
            filter: dependency => dependency.StartsWith(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: package
            )
        );

        // The control: without the store the same source needs the compiler it no longer has.
        var unstored = new ShaderPackager(
            compiler: compiler,
            reflectDxil: false
        ).LoadSource(
            cancellationToken: Token,
            name: "graph",
            path: source
        );

        Assert.Equal(
            actual: unstored.Status,
            expected: ShaderPipelineLoadStatus.Unsupported
        );
        Assert.DoesNotContain(
            actualString: unstored.Message,
            expectedSubstring: ShaderClosureRefusedException.PackageAbsent
        );
    }
    [Fact]
    public async Task An_edited_source_misses_the_store_and_compiles_or_is_refused_by_name() {
        using var fixture = new Fixture(name: "transitive");
        var toolchain = Toolchain(fixture: fixture);
        var runner = new PackageRunner();
        var packager = new ShaderPackager(
            compiler: fixture.Compiler(
                runner: runner,
                toolchain: toolchain
            ),
            reflectDxil: false,
            store: fixture.Output(name: "store")
        );
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");

        Assert.Equal(
            actual: (await packager.StoreAsync(cancellationToken: Token, name: "graph", source: source)).Result.Status,
            expected: ShaderPipelineLoadStatus.Compiled
        );

        var stored = packager.KeyOf(
            name: "graph",
            source: source
        );

        File.AppendAllText(
            contents: "\n// edited\n",
            path: fixture.PathOf(logicalPath: "lib/math.hlsli")
        );
        Assert.NotEqual(
            actual: packager.KeyOf(name: "graph", source: source),
            expected: stored
        );

        // With a compiler, the edited source compiles as it would with no store.
        var runs = runner.CompileRuns;
        var compiled = packager.LoadSource(
            cancellationToken: Token,
            name: "graph",
            path: source
        );

        Assert.True(
            condition: (compiled.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: compiled.Message
        );
        Assert.StartsWith(
            actualString: compiled.Message,
            expectedStartString: "compiled: "
        );
        Assert.True(condition: (runner.CompileRuns > runs));

        // Without one, it is refused by name.
        DeleteCompiler(toolchain: toolchain);

        var refused = packager.LoadSource(
            cancellationToken: Token,
            name: "graph",
            path: source
        );

        Assert.Equal(
            actual: refused.Status,
            expected: ShaderPipelineLoadStatus.Unsupported
        );
        Assert.StartsWith(
            actualString: refused.Message,
            expectedStartString: $"[{ShaderClosureRefusedException.PackageAbsent}] "
        );
        Assert.Null(@object: refused.Pipeline);
    }
    [Fact]
    public async Task A_one_off_shader_is_stored_under_the_name_it_plans_with() {
        using var fixture = new Fixture(name: "image-only");
        var toolchain = Toolchain(fixture: fixture);
        var packager = new ShaderPackager(
            compiler: fixture.Compiler(
                runner: new PackageRunner(),
                toolchain: toolchain
            ),
            reflectDxil: false,
            store: fixture.Output(name: "store")
        );
        var source = fixture.PathOf(logicalPath: "image.hlsl");

        Assert.NotEqual(
            actual: packager.KeyOf(name: "left", source: source),
            expected: packager.KeyOf(name: "right", source: source)
        );

        var (stored, _) = await packager.StoreAsync(
            cancellationToken: Token,
            name: "left",
            source: source
        );

        Assert.Equal(
            actual: stored.Manifest!.Name,
            expected: "left"
        );
        Assert.Equal(
            actual: stored.Manifest.Passes.Select(selector: static pass => pass.Name),
            expected: ["left"]
        );

        DeleteCompiler(toolchain: toolchain);

        var left = packager.LoadSource(
            cancellationToken: Token,
            name: "left",
            path: source
        );
        var right = packager.LoadSource(
            cancellationToken: Token,
            name: "right",
            path: source
        );

        Assert.True(
            condition: (left.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: left.Message
        );
        Assert.Equal(
            actual: left.Pipeline!.Plan.Passes.Select(selector: static pass => pass.Name),
            expected: ["left"]
        );
        Assert.Equal(
            actual: right.Status,
            expected: ShaderPipelineLoadStatus.Unsupported
        );
        Assert.StartsWith(
            actualString: right.Message,
            expectedStartString: $"[{ShaderClosureRefusedException.PackageAbsent}] "
        );
    }
    [Fact]
    public async Task A_damaged_stored_package_is_refused_and_nothing_compiles_in_its_place() {
        using var fixture = new Fixture(name: "transitive");
        var runner = new PackageRunner();
        var packager = new ShaderPackager(
            compiler: fixture.Compiler(runner: runner),
            reflectDxil: false,
            store: fixture.Output(name: "store")
        );
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");
        var (stored, package) = await packager.StoreAsync(
            cancellationToken: Token,
            name: "graph",
            source: source
        );

        File.AppendAllText(
            contents: "\n// altered\n",
            path: Path.Combine(
                path1: package,
                path2: stored.Manifest!.Passes[0].Variants[0].Binaries[0].Path
            )
        );

        var runs = runner.CompileRuns;
        var loaded = packager.LoadSource(
            cancellationToken: Token,
            name: "graph",
            path: source
        );

        Assert.Equal(
            actual: loaded.Status,
            expected: ShaderPipelineLoadStatus.Failed
        );
        Assert.StartsWith(
            actualString: loaded.Message,
            expectedStartString: $"[{ShaderClosureRefusedException.PackageFilePin}] "
        );
        Assert.Equal(
            actual: runner.CompileRuns,
            expected: runs
        );
    }
}
