using Puck.Testing;

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
    public async Task A_store_refuses_a_linked_package_directory_without_removing_or_writing_through_it() {
        using var fixture = new Fixture(name: "transitive");
        var outside = fixture.Output(name: "outside");
        var store = fixture.Output(name: "store");
        var runner = new PackageRunner();
        var packager = new ShaderPackager(
            compiler: fixture.Compiler(runner: runner, toolchain: Toolchain(fixture: fixture)),
            reflectDxil: false,
            store: store
        );
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");
        var package = ShaderPackager.StorePathOf(
            key: packager.KeyOf(name: "graph", source: source),
            store: store
        );
        var sentinel = Path.Combine(path1: outside, path2: "foreign.hlsl");

        // A package directory with no manifest, reached through a link that leads out of the store: recovery would
        // remove whatever the link leads to.
        Directory.CreateDirectory(path: outside);
        Directory.CreateDirectory(path: store);
        File.WriteAllText(contents: "keep this file", path: sentinel);
        DirectoryLinks.Create(link: package, target: outside);

        try {
            var (result, _) = await packager.StoreAsync(cancellationToken: Token, name: "graph", source: source);

            AssertRefused(code: ShaderClosureRefusedException.PackageOutput, result: result);
            Assert.Equal(actual: File.ReadAllText(path: sentinel), expected: "keep this file");
            Assert.True(condition: ((File.GetAttributes(path: package) & FileAttributes.ReparsePoint) != 0));
            Assert.Equal(actual: runner.CompileRuns, expected: 0);
            Assert.False(condition: File.Exists(path: Path.Combine(path1: outside, path2: ShaderPackageManifest.FileName)));
        } finally {
            DirectoryLinks.Remove(link: package);
        }
    }
    [InlineData("store")]
    [InlineData("ancestor")]
    [Theory]
    public async Task A_store_reached_through_a_link_publishes_its_package_inside_it(string linked) {
        using var fixture = new Fixture(name: "transitive");
        var outside = fixture.Output(name: "outside");
        var store = fixture.Output(name: ((linked == "ancestor") ? "link/store" : "store"));
        var link = ((linked == "ancestor") ? fixture.Output(name: "link") : store);
        var packager = new ShaderPackager(
            compiler: fixture.Compiler(runner: new PackageRunner(), toolchain: Toolchain(fixture: fixture)),
            reflectDxil: false,
            store: store
        );
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");
        var name = Path.GetFileName(path: ShaderPackager.StorePathOf(key: packager.KeyOf(name: "graph", source: source), store: store));
        var reached = ((linked == "ancestor") ? Path.Combine(path1: outside, path2: "store", path3: name) : Path.Combine(path1: outside, path2: name));

        // The store, or a directory above it, is a link (a redirected profile, a junctioned build tree, a platform's
        // linked temporary root), and the store already holds the package's directory with no manifest: the store is
        // wherever the link leads, so its abandoned publication is recovered there.
        Directory.CreateDirectory(path: reached);
        File.WriteAllText(contents: "abandoned", path: Path.Combine(path1: reached, path2: "partial.hlsl"));
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: link)!);
        DirectoryLinks.Create(link: link, target: outside);

        try {
            var (result, _) = await packager.StoreAsync(cancellationToken: Token, name: "graph", source: source);

            Assert.True(condition: (result.Status == ShaderPipelineLoadStatus.Compiled), userMessage: result.Message);
            Assert.True(condition: File.Exists(path: Path.Combine(path1: reached, path2: ShaderPackageManifest.FileName)));
            Assert.False(condition: File.Exists(path: Path.Combine(path1: reached, path2: "partial.hlsl")));
            Assert.True(condition: ((File.GetAttributes(path: link) & FileAttributes.ReparsePoint) != 0));
        } finally {
            DirectoryLinks.Remove(link: link);
        }
    }
    [Fact]
    public async Task A_writer_recovering_an_abandoned_package_never_removes_one_another_writer_published() {
        using var fixture = new Fixture(name: "transitive");

        var (_, toolchain, source, package) = await StrandedPackage(fixture: fixture);
        var store = Path.GetDirectoryName(path: package)!;
        // Writer A finds the package directory with no manifest. While it is about to remove it, writer B runs whole,
        // and then A loses its compiler, so whatever A removes it cannot rebuild.
        var paused = 0;
        var other = ((Task<(ShaderPackageResult Result, string Package)>?)null);
        var aToolchain = fixture.Output(name: "toolchain-a");

        Directory.CreateDirectory(path: aToolchain);
        foreach (var tool in ((string[])["dxc", "dxc.exe"])) {
            File.WriteAllBytes(bytes: [], path: Path.Combine(path1: aToolchain, path2: tool));
        }

        var writerB = new ShaderPackager(
            compiler: fixture.Compiler(runner: new PackageRunner(), toolchain: toolchain),
            reflectDxil: false,
            store: store
        );
        var writerA = new ShaderPackager(
            compiler: fixture.Compiler(runner: new PackageRunner(), toolchain: aToolchain),
            reflectDxil: false,
            removingAbandoned: abandoned => {
                if (Interlocked.Exchange(location1: ref paused, value: 1) != 0) {
                    return;
                }

                other = Task.Run(cancellationToken: Token, function: () => writerB.StoreAsync(cancellationToken: Token, name: "graph", source: source));
                // An unguarded writer B publishes within this wait; one waiting for A's lock cannot.
                _ = Task.WhenAny(task1: other, task2: Task.Delay(cancellationToken: Token, delay: TimeSpan.FromSeconds(value: 3))).GetAwaiter().GetResult();
                DeleteCompiler(toolchain: aToolchain);
            },
            store: store
        );

        _ = await writerA.StoreAsync(cancellationToken: Token, name: "graph", source: source);

        Assert.NotNull(@object: other);

        var (published, _) = await other!;

        Assert.True(condition: (published.Status == ShaderPipelineLoadStatus.Compiled), userMessage: published.Message);
        Assert.Equal(
            actual: ShaderPackager.KeyOf(manifest: ShaderPackager.Open(package: package)),
            expected: writerB.KeyOf(name: "graph", source: source)
        );
    }

    // A store holding a published package whose manifest, the commit record a publication writes last, is gone: what a
    // clean or rebuild that stopped part way leaves.
    private static async Task<(ShaderPackager Packager, string Toolchain, string Source, string Package)> StrandedPackage(Fixture fixture) {
        var toolchain = Toolchain(fixture: fixture);
        var packager = new ShaderPackager(
            compiler: fixture.Compiler(
                runner: new PackageRunner(),
                toolchain: toolchain
            ),
            reflectDxil: false,
            store: fixture.Output(name: "store")
        );
        var source = fixture.PathOf(logicalPath: "transitive.graph.json");

        var (first, package) = await packager.StoreAsync(
            cancellationToken: Token,
            name: "graph",
            source: source
        );

        Assert.True(
            condition: (first.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: first.Message
        );
        File.Delete(path: Path.Combine(
            path1: package,
            path2: ShaderPackageManifest.FileName
        ));
        Assert.NotEmpty(collection: Directory.EnumerateFiles(path: package, searchOption: SearchOption.AllDirectories, searchPattern: "*"));

        return (packager, toolchain, source, package);
    }

    [Fact]
    public async Task A_store_directory_with_no_manifest_is_replaced_by_the_next_store() {
        using var fixture = new Fixture(name: "transitive");

        var (packager, _, source, package) = await StrandedPackage(fixture: fixture);

        // An interrupted publication's staging sibling sits beside it too; it never names a package.
        Directory.CreateDirectory(path: (package + ".partial-abandoned"));
        File.WriteAllText(
            contents: "a staged file",
            path: Path.Combine(
                path1: (package + ".partial-abandoned"),
                path2: "staged.hlsl"
            )
        );

        var (next, again) = await packager.StoreAsync(
            cancellationToken: Token,
            name: "graph",
            source: source
        );

        Assert.True(
            condition: (next.Status == ShaderPipelineLoadStatus.Compiled),
            userMessage: next.Message
        );
        Assert.Equal(
            actual: again,
            expected: package
        );
        Assert.Equal(
            actual: ShaderPackager.KeyOf(manifest: ShaderPackager.Open(package: package)),
            expected: packager.KeyOf(name: "graph", source: source)
        );
        Assert.Equal(
            actual: File.ReadAllText(path: Path.Combine(path1: (package + ".partial-abandoned"), path2: "staged.hlsl")),
            expected: "a staged file"
        );
    }
    [Fact]
    public async Task A_store_directory_with_no_manifest_is_a_miss_never_a_package() {
        using var fixture = new Fixture(name: "transitive");

        var (packager, toolchain, source, _) = await StrandedPackage(fixture: fixture);

        // With no compiler, the remains are refused as absent, never loaded as a package.
        DeleteCompiler(toolchain: toolchain);

        var missed = packager.LoadSource(
            cancellationToken: Token,
            name: "graph",
            path: source
        );

        Assert.Contains(
            actualString: missed.Message,
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
