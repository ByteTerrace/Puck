using Puck.Testing;
using Xunit;

namespace Puck.Cli.Shaders.Tests;

/// <summary>Exercises the shipped MSBuild targets in isolated projects: the real build host, the real shader build in
/// the generator, and a CPU-only stand-in for DXC that records every compile it runs and can hold or fail one. Each
/// fixture compiles into a shader cache of its own unless a law shares one on purpose.</summary>
public abstract class ShaderBuildTargetsLaws {
    // A build over two sources and a shared include, one input changed, then the pack check and the rebuild.
    private protected static void ChangedInput(string change, int expectedCompiles, bool checkRefuses) {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"shared.hlsli\"\nfirst source");
        fixture.Write(path: "Assets/Shaders/c.comp.hlsl", text: "independent source");
        fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "shared declaration");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());

        var bytecode = fixture.PathOf(path: "Assets/Shaders/a.comp.spv");
        var sidecar = (bytecode + ".hash");
        string[] properties = [];

        switch (change) {
            case "source": fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"shared.hlsli\"\nchanged source"); break;
            case "include": fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "changed declaration"); break;
            case "command":
                // Another DXC is another toolchain: every output compiles again, though a pack's check, which holds the
                // published bytes to their sources and options, needs no toolchain and still passes.
                var alternate = fixture.PathOf(path: ("another-dxc" + Path.GetExtension(path: fixture.CompilerPath)));
                File.Copy(sourceFileName: fixture.CompilerPath, destFileName: alternate);
                if (!OperatingSystem.IsWindows()) {
                    File.SetUnixFileMode(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, path: alternate);
                }
                properties = [$"DxcCommand={Puck.Abstractions.PuckPaths.Normalize(path: alternate)}"];
                break;
            case "bytecode": File.WriteAllText(contents: "corrupted compiled bytes", path: bytecode); break;
            case "sidecar": File.AppendAllText(path: sidecar, contents: File.ReadAllText(path: sidecar)); break;
            case "bytecode-missing": File.Delete(path: bytecode); break;
            case "sidecar-missing": File.Delete(path: sidecar); break;
            default: throw new ArgumentOutOfRangeException(paramName: nameof(change));
        }

        var check = fixture.Run(target: "CollectShaderBytecode", properties: properties);

        Assert.Equal(expected: checkRefuses, actual: (check.ExitCode != 0));
        Assert.Equal(expected: 2, actual: fixture.Compiles());
        fixture.RequireSuccess(run: fixture.Run(target: "Build", properties: properties));
        Assert.Equal(expected: expectedCompiles, actual: fixture.Compiles());
        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode", properties: properties));
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
    }
    private protected static T WaitFor<T>(Func<T?> find) where T : class {
        T? found = null;

        TestLiveness.Until(
            reason: () => "The build never reached the awaited point.",
            step: () => ((found = find()) is not null)
        );

        return found!;
    }
    private protected static void RequireWaiting(Task<CliProcessResult> process, ManualResetEventSlim waiting) {
        _ = WaitFor(find: () => ((waiting.IsSet || process.IsCompleted) ? "the lock wait or process exit is observed" : null));
        Assert.True(condition: waiting.IsSet, userMessage: "The process ended without observing contention on the source tree's publication lock.");
    }
}
/// <summary>The shipped shader targets publish from the cache, recompile only what an edited include reaches,
/// compile again into an empty cache, gate a pack on the publication lock and the commit record, and remove only the
/// bytecode they wrote; a design-time build runs none of it.</summary>
public sealed class ShaderBuildTargetsLawTests : ShaderBuildTargetsLaws {
    [Fact]
    public void AWarmBuildOfAnotherCheckoutPublishesFromTheCacheAndRunsNoCompiler() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-targets-");
        string[] cache = [$"PuckShaderCacheDirectory={Path.Combine(path1: scratch.RootPath, path2: "cache")}"];
        var first = new ShaderBuildFixture(root: Path.Combine(path1: scratch.RootPath, path2: "first"));
        var second = new ShaderBuildFixture(root: Path.Combine(path1: scratch.RootPath, path2: "elsewhere", path3: "second"));

        foreach (var fixture in ((ReadOnlySpan<ShaderBuildFixture>)[first, second])) {
            fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "shared declaration");
            fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"shared.hlsli\"\nfirst source");
            fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "second source");
            fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        }

        // One DXC for both checkouts: the toolchain is part of every key, the checkout is not.
        string[] shared = [.. cache, $"DxcCommand={first.CompilerPath}"];

        first.RequireSuccess(run: first.Run(properties: shared, target: "Build"));
        Assert.Equal(expected: 2, actual: first.Compiles());

        var warm = second.Run(properties: shared, target: "Build");

        second.RequireSuccess(run: warm);
        Assert.Equal(expected: 0, actual: second.Compiles());
        Assert.Contains(expectedSubstring: "2 shader output(s): 0 current, 2 published from the cache, 0 compiled, in ", actualString: warm.Stdout);
        foreach (var output in ((ReadOnlySpan<string>)["Assets/Shaders/a.comp.spv", "Assets/Shaders/b.comp.spv"])) {
            Assert.Equal(expected: File.ReadAllBytes(path: first.PathOf(path: output)), actual: File.ReadAllBytes(path: second.PathOf(path: output)));
        }
        second.RequireSuccess(run: second.Run(properties: shared, target: "CollectShaderBytecode"));
    }
    [Fact]
    public void EditingOneIncludeRecompilesOnlyTheOutputsWhoseClosureHoldsIt() {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "shared declaration");
        fixture.Write(path: "Assets/Shaders/a-only.hlsli", text: "a's declaration");
        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"a-only.hlsli\"\n#include \"shared.hlsli\"\nfirst source");
        fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "#include \"shared.hlsli\"\nsecond source");
        fixture.Write(path: "Assets/Shaders/c.comp.hlsl", text: "independent source");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 3, actual: fixture.Compiles());

        fixture.Write(path: "Assets/Shaders/a-only.hlsli", text: "a's changed declaration");
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: ["a.comp.hlsl"], actual: fixture.CompiledSources().Skip(count: 3));

        fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "changed shared declaration");
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: ["a.comp.hlsl", "b.comp.hlsl"], actual: fixture.CompiledSources().Skip(count: 4).Order(comparer: StringComparer.Ordinal));
    }
    [Fact]
    public void AnEmptyCacheCompilesEveryOutputAgainHoweverCurrentTheTree() {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "first source");
        fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "second source");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());

        fixture.RequireSuccess(run: fixture.Run(properties: [$"PuckShaderCacheDirectory={fixture.PathOf(path: "fresh-cache")}"], target: "Build"));
        Assert.Equal(expected: 4, actual: fixture.Compiles());
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ANoBuildPackWaitsForThePublicationLockAndRequiresTheCommitRecord(bool commitSidecar) {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "source");
        fixture.ShaderProject(body: """
            <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
            <Target Name="ResolveProjectReferences" />
            <Target Name="_GetPackageFiles">
              <WriteLinesToFile File="packed.txt" Lines="@(Content->'%(FullPath)')" Overwrite="true" />
            </Target>
            """);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        if (!commitSidecar) {
            // A publication cut short leaves the bytecode without its commit record.
            File.Delete(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash"));
        }

        using var waiting = new ManualResetEventSlim();
        Task<CliProcessResult> pack;

        using (new FileStream(access: FileAccess.ReadWrite, mode: FileMode.Open, path: fixture.PathOf(path: "obj/shader-publish.lock"), share: FileShare.None)) {
            pack = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(properties: ["NoBuild=true"], target: "_GetPackageFiles", waiting: waiting));
            RequireWaiting(process: pack, waiting: waiting);
            Assert.False(condition: pack.IsCompleted, userMessage: "A pack read the published shaders while their publication lock was held.");
        }

        var result = await pack;

        if (commitSidecar) {
            fixture.RequireSuccess(run: result);
            Assert.Equal(expected: [Path.GetFullPath(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv"))], actual: File.ReadAllLines(path: fixture.PathOf(path: "packed.txt")));
        } else {
            Assert.NotEqual(expected: 0, actual: result.ExitCode);
            Assert.Contains(expectedSubstring: "has no '.hash' sidecar", actualString: result.Stdout);
            Assert.False(condition: File.Exists(path: fixture.PathOf(path: "packed.txt")));
        }
        Assert.Equal(expected: 1, actual: fixture.Compiles());
    }
    [Fact]
    public async Task PublishersWithDifferentIntermediateDirectoriesShareTheSourceTreesLock() {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "source");
        fixture.Write(path: "obj/shader-publish.lock", text: "");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        using var firstWaiting = new ManualResetEventSlim();
        using var secondWaiting = new ManualResetEventSlim();
        Task<CliProcessResult> first;
        Task<CliProcessResult> second;

        using (new FileStream(path: fixture.PathOf(path: "obj/shader-publish.lock"), mode: FileMode.Open, access: FileAccess.ReadWrite, share: FileShare.None)) {
            first = Task.Run(function: () => fixture.Run(properties: ["BaseIntermediateOutputPath=obj/first/"], target: "Build", waiting: firstWaiting), cancellationToken: TestContext.Current.CancellationToken);
            second = Task.Run(function: () => fixture.Run(properties: ["BaseIntermediateOutputPath=obj/second/"], target: "Build", waiting: secondWaiting), cancellationToken: TestContext.Current.CancellationToken);
            RequireWaiting(process: first, waiting: firstWaiting);
            RequireWaiting(process: second, waiting: secondWaiting);
            Assert.False(condition: (first.IsCompleted || second.IsCompleted));
            Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv")));
        }

        fixture.RequireSuccess(run: await first);
        fixture.RequireSuccess(run: await second);
        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode"));
    }
    [Fact]
    public void ABuildRemovesTheBytecodeItWroteForADeletedSourceAndRefusesBytecodeItDidNotWrite() {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "first source");
        fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "second source");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));

        File.Delete(path: fixture.PathOf(path: "Assets/Shaders/b.comp.hlsl"));
        var removed = fixture.Run(target: "Build");

        fixture.RequireSuccess(run: removed);
        Assert.Contains(expectedSubstring: "Removed orphaned shader bytecode 'Assets/Shaders/b.comp.spv'", actualString: removed.Stdout);
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/b.comp.spv")));
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/b.comp.spv.hash")));

        fixture.Write(path: "Assets/Shaders/foreign.comp.spv", text: "bytecode no build wrote");
        var refused = fixture.Run(target: "Build");

        Assert.NotEqual(expected: 0, actual: refused.ExitCode);
        Assert.Contains(expectedSubstring: "Shader bytecode 'Assets/Shaders/foreign.comp.spv' has no matching HLSL source", actualString: refused.Stdout);
        Assert.True(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/foreign.comp.spv")));
    }
    [Fact]
    public void AnIncludeRestoredByAReferenceIsHashedAfterReferencesAndInvalidatesOnlyItsReaders() {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"generated.hlsli\"\nsource a");
        fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "source b");
        fixture.Write(path: "declaration.txt", text: "generated declaration");
        fixture.ShaderProject(body: """
            <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
            <Target Name="ResolveProjectReferences">
              <ReadLinesFromFile File="declaration.txt"><Output TaskParameter="Lines" ItemName="Declaration" /></ReadLinesFromFile>
              <WriteLinesToFile File="Assets/Shaders/generated.hlsli" Lines="@(Declaration)" WriteOnlyWhenDifferent="true" Overwrite="true" />
            </Target>
            """);

        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());
        var committed = File.ReadAllText(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash"));

        File.Delete(path: fixture.PathOf(path: "Assets/Shaders/generated.hlsli"));
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());
        Assert.Equal(expected: committed, actual: File.ReadAllText(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash")));
        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode"));

        fixture.Write(path: "declaration.txt", text: "changed generated declaration");
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: ["a.comp.hlsl"], actual: fixture.CompiledSources().Skip(count: 2));
        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode"));
    }
    // A design-time build (Roslyn's MSBuildWorkspace under docfx, an IDE, the CLI's analysis verbs) builds no project
    // reference, so the generator may not exist; it runs no shader build and writes no bytecode. A real build after it
    // still compiles every output.
    [Fact]
    public void ADesignTimeBuildRunsNoShaderBuild() {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "first source");
        fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "second source");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);

        var design = fixture.Run(properties: ["DesignTimeBuild=true", "BuildProjectReferences=false", "BuildingProject=false", $"PuckShaderBuildTool={fixture.PathOf(path: "absent/Puck.Shaders.Generator.dll")}"], target: "Build");

        fixture.RequireSuccess(run: design);
        Assert.DoesNotContain(expectedSubstring: "shader output(s)", actualString: design.Stdout);
        Assert.Equal(expected: 0, actual: fixture.Compiles());
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.PathOf(path: "Assets/Shaders"), searchPattern: "*.spv"));

        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());
    }
    [Fact]
    public void DeletingTheLastStageSourceStillSweepsItsPublishedBytecode() {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "only source");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        File.Delete(path: fixture.PathOf(path: "Assets/Shaders/a.comp.hlsl"));
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));

        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv")), userMessage: "Deleting the final stage source skipped the orphan sweep.");
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash")));
        Assert.Equal(expected: 1, actual: fixture.Compiles());
    }
    [Fact]
    public void ANoBuildPackRefusesAMissingDirect3D11EntryAndCollectsExistingEntriesOnce() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "Direct3D 11 kernels are shipped by Windows builds.");
        }

        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Probes/probe.hlsl", text: "[numthreads(1,1,1)] void first() {} [numthreads(1,1,1)] void second() {}");
        fixture.ShaderProject(body: """
            <ItemGroup><Direct3D11KernelSource Include="Assets/Probes/probe.hlsl" Entries="first;second" /></ItemGroup>
            <Target Name="ResolveProjectReferences" />
            <Target Name="AssignTargetPaths" />
            <Target Name="_GetPackageFiles">
              <WriteLinesToFile File="packed.txt" Lines="@(Content->'%(Filename)%(Extension)')" Overwrite="true" />
            </Target>
            """, buildDependencies: "AssignTargetPaths");

        var pack = fixture.Run(properties: ["NoBuild=true"], target: "_GetPackageFiles");

        Assert.NotEqual(expected: 0, actual: pack.ExitCode);
        Assert.Contains(expectedSubstring: "Build normally before packing", actualString: pack.Stdout);
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.dxbc", searchOption: SearchOption.AllDirectories));

        fixture.Write(path: "Assets/Probes/probe.first.dxbc", text: "first compiled entry");
        fixture.Write(path: "Assets/Probes/probe.second.dxbc", text: "second compiled entry");
        var settled = File.GetLastWriteTimeUtc(path: fixture.PathOf(path: "Assets/Probes/probe.first.dxbc"));

        fixture.RequireSuccess(run: fixture.Run(target: "Build;_GetPackageFiles"));
        Assert.Equal(expected: ["probe.first.dxbc", "probe.second.dxbc"], actual: File.ReadAllLines(path: fixture.PathOf(path: "packed.txt")));
        Assert.Equal(expected: settled, actual: File.GetLastWriteTimeUtc(path: fixture.PathOf(path: "Assets/Probes/probe.first.dxbc")));
    }
}
/// <summary>A changed source, include or compiler compiles only what it changed, and a pack that skips the build
/// refuses a changed source or include.</summary>
public sealed class ShaderSourceChangeLawTests : ShaderBuildTargetsLaws {
    [InlineData("source", 3, true)]
    [InlineData("include", 3, true)]
    [InlineData("command", 4, false)]
    [Theory]
    public void AChangedInputCompilesOnlyWhatItChangedAndAPackThatSkipsTheBuildRefusesIt(string change, int expectedCompiles, bool checkRefuses) =>
        ChangedInput(change: change, checkRefuses: checkRefuses, expectedCompiles: expectedCompiles);
}
/// <summary>Changed or missing bytecode or sidecars compile again only their own output, and a pack that skips the
/// build refuses them.</summary>
public sealed class ShaderOutputChangeLawTests : ShaderBuildTargetsLaws {
    [InlineData("bytecode", 2, true)]
    [InlineData("sidecar", 2, true)]
    [InlineData("bytecode-missing", 2, true)]
    [InlineData("sidecar-missing", 2, true)]
    [Theory]
    public void AChangedInputCompilesOnlyWhatItChangedAndAPackThatSkipsTheBuildRefusesIt(string change, int expectedCompiles, bool checkRefuses) =>
        ChangedInput(change: change, checkRefuses: checkRefuses, expectedCompiles: expectedCompiles);
}
