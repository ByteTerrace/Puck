using Puck.Testing;
using Xunit;

namespace Puck.Cli.Shaders.Tests;

/// <summary>Exercises the shipped MSBuild targets in isolated projects: the real build host, the real shader build in
/// the generator, and a CPU-only stand-in for DXC that records every compile it runs and can hold or fail one. Each
/// fixture compiles into a shader cache of its own unless a law shares one on purpose.</summary>
public abstract class ShaderBuildTargetsLaws {
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
/// <summary>The shipped shader targets compile again the outputs of a backend the last build left off.</summary>
public sealed class ShaderBuildTargetsLawTests : ShaderBuildTargetsLaws {
    [Fact]
    public void ABuildThatEnablesABackendTheLastBuildLeftOffCompilesThatBackendsOlderOutputs() {
        using var fixture = new ShaderBuildFixture();
        // Both backends for the one compute source, whatever the host: DXIL stands in for a Windows host's.
        string[] dxil = ["PuckComputeShaderDxilEnabled=true", "_PuckShaderHostBuildsDxil=true"];

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "first source");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        fixture.RequireSuccess(run: fixture.Run(properties: [.. dxil, "PuckShaderSpirvEnabled=true"], target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());

        // A DXIL-only build of an edit leaves the SPIR-V output older than its source.
        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "edited source");
        fixture.RequireSuccess(run: fixture.Run(properties: [.. dxil, "PuckShaderSpirvEnabled=false"], target: "Build"));
        Assert.Equal(expected: 3, actual: fixture.Compiles());

        fixture.RequireSuccess(run: fixture.Run(properties: [.. dxil, "PuckShaderSpirvEnabled=true"], target: "Build"));
        Assert.Equal(expected: 4, actual: fixture.Compiles());
        fixture.RequireSuccess(run: fixture.Run(properties: [.. dxil, "PuckShaderSpirvEnabled=true"], target: "CollectShaderBytecode"));
    }
}
/// <summary>The shipped shader targets gate a pack on the publication lock and on the commit record.</summary>
public sealed class ShaderBuildPackLockLawTests : ShaderBuildTargetsLaws {
    [Fact]
    public async Task ANoBuildPackWaitsForThePublicationLockAndRequiresTheCommitRecord() {
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
        var sidecar = fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash");
        var backup = (sidecar + ".bak");

        // A publication cut short leaves the bytecode without its commit record.
        File.Move(sourceFileName: sidecar, destFileName: backup);
        using (var waiting = new ManualResetEventSlim()) {
            Task<CliProcessResult> refusedPack;

            using (new FileStream(access: FileAccess.ReadWrite, mode: FileMode.Open, path: fixture.PathOf(path: "obj/shader-publish.lock"), share: FileShare.None)) {
                refusedPack = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(properties: ["NoBuild=true"], target: "_GetPackageFiles", waiting: waiting));
                RequireWaiting(process: refusedPack, waiting: waiting);
                Assert.False(condition: refusedPack.IsCompleted, userMessage: "A pack read the published shaders while their publication lock was held.");
            }

            var refused = await refusedPack;

            Assert.NotEqual(expected: 0, actual: refused.ExitCode);
            Assert.Contains(expectedSubstring: "has no '.hash' sidecar", actualString: refused.Stdout);
            Assert.False(condition: File.Exists(path: fixture.PathOf(path: "packed.txt")));
        }

        File.Move(sourceFileName: backup, destFileName: sidecar);
        using (var waiting = new ManualResetEventSlim()) {
            Task<CliProcessResult> validPack;

            using (new FileStream(access: FileAccess.ReadWrite, mode: FileMode.Open, path: fixture.PathOf(path: "obj/shader-publish.lock"), share: FileShare.None)) {
                validPack = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(properties: ["NoBuild=true"], target: "_GetPackageFiles", waiting: waiting));
                RequireWaiting(process: validPack, waiting: waiting);
                Assert.False(condition: validPack.IsCompleted, userMessage: "A pack read the published shaders while their publication lock was held.");
            }

            fixture.RequireSuccess(run: await validPack);
            Assert.Equal(expected: [Path.GetFullPath(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv"))], actual: File.ReadAllLines(path: fixture.PathOf(path: "packed.txt")));
        }
        Assert.Equal(expected: 1, actual: fixture.Compiles());
    }
}
/// <summary>Publishers with different intermediate directories share the source tree's publication lock.</summary>
public sealed class ShaderBuildPublishContentionLawTests : ShaderBuildTargetsLaws {
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
}
/// <summary>The shipped shader targets run no shader build in a design-time build, remove the bytecode they wrote when
/// a source or the last stage source is deleted, and refuse bytecode they did not write.</summary>
public sealed class ShaderBuildBytecodeLawTests : ShaderBuildTargetsLaws {
    [Fact]
    public void ADesignTimeBuildRunsNoShaderBuildAndNormalBuildsSweepDeletedSourcesWhileRefusingForeignBytecode() {
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

        File.Delete(path: fixture.PathOf(path: "Assets/Shaders/b.comp.hlsl"));
        var removed = fixture.Run(target: "Build");

        fixture.RequireSuccess(run: removed);
        Assert.Contains(expectedSubstring: "Removed orphaned shader bytecode 'Assets/Shaders/b.comp.spv'", actualString: removed.Stdout);
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/b.comp.spv")));
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/b.comp.spv.hash")));

        File.Delete(path: fixture.PathOf(path: "Assets/Shaders/a.comp.hlsl"));
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv")), userMessage: "Deleting the final stage source skipped the orphan sweep.");
        Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash")));
        Assert.Equal(expected: 2, actual: fixture.Compiles());

        fixture.Write(path: "Assets/Shaders/foreign.comp.spv", text: "bytecode no build wrote");
        var refused = fixture.Run(target: "Build");

        Assert.NotEqual(expected: 0, actual: refused.ExitCode);
        Assert.Contains(expectedSubstring: "Shader bytecode 'Assets/Shaders/foreign.comp.spv' has no matching HLSL source", actualString: refused.Stdout);
        Assert.True(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/foreign.comp.spv")));
    }
}
/// <summary>An include restored by a reference is hashed after references resolve and invalidates only its readers.</summary>
public sealed class ShaderBuildGeneratedIncludeLawTests : ShaderBuildTargetsLaws {
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

        fixture.Write(path: "declaration.txt", text: "changed generated declaration");
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: ["a.comp.hlsl"], actual: fixture.CompiledSources().Skip(count: 2));
        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode"));
    }
}
/// <summary>A no-build pack refuses a missing Direct3D 11 entry and collects existing entries once.</summary>
public sealed class ShaderBuildDirect3D11PackLawTests : ShaderBuildTargetsLaws {
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
/// <summary>A changed source or include compiles only what it changed, and a pack that skips the build refuses it.</summary>
public sealed class ShaderSourceChangeLawTests : ShaderBuildTargetsLaws {
    [Fact]
    public void AChangedSourceOrIncludeCompilesOnlyWhatItChangedAndAPackThatSkipsTheBuildRefusesIt() {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"shared.hlsli\"\nfirst source");
        fixture.Write(path: "Assets/Shaders/c.comp.hlsl", text: "independent source");
        fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "shared declaration");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"shared.hlsli\"\nchanged source");
        Assert.NotEqual(expected: 0, actual: fixture.Run(target: "CollectShaderBytecode").ExitCode);
        Assert.Equal(expected: 2, actual: fixture.Compiles());
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 3, actual: fixture.Compiles());

        fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "changed declaration");
        Assert.NotEqual(expected: 0, actual: fixture.Run(target: "CollectShaderBytecode").ExitCode);
        Assert.Equal(expected: 3, actual: fixture.Compiles());
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 4, actual: fixture.Compiles());
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
    }
}
/// <summary>Changed bytecode is published again from the cache, compiling nothing, and a pack that skips the build
/// refuses it; another DXC toolchain recompiles every output while the pack check still passes.</summary>
public sealed class ShaderOutputChangeLawTests : ShaderBuildTargetsLaws {
    [Fact]
    public void ChangedBytecodeIsPublishedAgainAndAnotherToolchainRecompilesEveryOutput() {
        using var fixture = new ShaderBuildFixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"shared.hlsli\"\nfirst source");
        fixture.Write(path: "Assets/Shaders/c.comp.hlsl", text: "independent source");
        fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "shared declaration");
        fixture.ShaderProject(body: ShaderBuildFixture.Sources);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());

        File.WriteAllText(contents: "corrupted compiled bytes", path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv"));
        Assert.NotEqual(expected: 0, actual: fixture.Run(target: "CollectShaderBytecode").ExitCode);
        Assert.Equal(expected: 2, actual: fixture.Compiles());
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());

        // Another DXC is another toolchain: every output compiles again, though a pack's check, which holds the
        // published bytes to their sources and options, needs no toolchain and still passes.
        var alternate = fixture.PathOf(path: ("another-dxc" + Path.GetExtension(path: fixture.CompilerPath)));
        File.Copy(sourceFileName: fixture.CompilerPath, destFileName: alternate);
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, path: alternate);
        }
        string[] properties = [$"DxcCommand={Puck.Abstractions.PuckPaths.Normalize(path: alternate)}"];

        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode", properties: properties));
        Assert.Equal(expected: 2, actual: fixture.Compiles());
        fixture.RequireSuccess(run: fixture.Run(target: "Build", properties: properties));
        Assert.Equal(expected: 4, actual: fixture.Compiles());
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
    }
}
