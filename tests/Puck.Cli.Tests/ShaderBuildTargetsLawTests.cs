using System.Reflection;
using System.Xml.Linq;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Exercises the shipped MSBuild targets in isolated projects: the real build host, the real shader build in
/// the generator, and a CPU-only stand-in for DXC that records every compile it runs and can hold or fail one. Each
/// fixture compiles into a shader cache of its own unless a law shares one on purpose.</summary>
public sealed partial class ShaderBuildTargetsLawTests {
    private const string FakeDxc = "normal";

    [Fact]
    public void AWarmBuildOfAnotherCheckoutPublishesFromTheCacheAndRunsNoCompiler() {
        using var scratch = new TemporaryDirectory(prefix: "puck-shader-targets-");
        string[] cache = [$"PuckShaderCacheDirectory={Path.Combine(path1: scratch.RootPath, path2: "cache")}"];
        var first = new Fixture(root: Path.Combine(path1: scratch.RootPath, path2: "first"));
        var second = new Fixture(root: Path.Combine(path1: scratch.RootPath, path2: "elsewhere", path3: "second"));

        foreach (var fixture in ((ReadOnlySpan<Fixture>)[first, second])) {
            fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "shared declaration");
            fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"shared.hlsli\"\nfirst source");
            fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "second source");
            fixture.ShaderProject(body: Sources);
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
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "shared declaration");
        fixture.Write(path: "Assets/Shaders/a-only.hlsli", text: "a's declaration");
        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"a-only.hlsli\"\n#include \"shared.hlsli\"\nfirst source");
        fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "#include \"shared.hlsli\"\nsecond source");
        fixture.Write(path: "Assets/Shaders/c.comp.hlsl", text: "independent source");
        fixture.ShaderProject(body: Sources);
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
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "first source");
        fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "second source");
        fixture.ShaderProject(body: Sources);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: fixture.Compiles());

        fixture.RequireSuccess(run: fixture.Run(properties: [$"PuckShaderCacheDirectory={fixture.PathOf(path: "fresh-cache")}"], target: "Build"));
        Assert.Equal(expected: 4, actual: fixture.Compiles());
    }
    [InlineData("source", 3, true)]
    [InlineData("include", 3, true)]
    [InlineData("command", 4, false)]
    [InlineData("bytecode", 2, true)]
    [InlineData("sidecar", 2, true)]
    [InlineData("bytecode-missing", 2, true)]
    [InlineData("sidecar-missing", 2, true)]
    [Theory]
    public void AChangedInputCompilesOnlyWhatItChangedAndAPackThatSkipsTheBuildRefusesIt(string change, int expectedCompiles, bool checkRefuses) {
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "#include \"shared.hlsli\"\nfirst source");
        fixture.Write(path: "Assets/Shaders/c.comp.hlsl", text: "independent source");
        fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "shared declaration");
        fixture.ShaderProject(body: Sources);
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
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ANoBuildPackWaitsForThePublicationLockAndRequiresTheCommitRecord(bool commitSidecar) {
        using var fixture = new Fixture();

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
    public void ABuildRemovesTheBytecodeItWroteForADeletedSourceAndRefusesBytecodeItDidNotWrite() {
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "first source");
        fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "second source");
        fixture.ShaderProject(body: Sources);
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
    public void ANoBuildPackRefusesAMissingDirect3D11EntryAndCollectsExistingEntriesOnce() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "Direct3D 11 kernels are shipped by Windows builds.");
        }

        using var fixture = new Fixture();

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

    private const string Sources = """
        <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
        <Target Name="ResolveProjectReferences" />
        """;

    private static T WaitFor<T>(Func<T?> find) where T : class {
        T? found = null;

        TestLiveness.Until(
            reason: () => "The build never reached the awaited point.",
            step: () => ((found = find()) is not null)
        );

        return found!;
    }
    private static void RequireWaiting(Task<CliProcessResult> process, ManualResetEventSlim waiting) {
        _ = WaitFor(find: () => ((waiting.IsSet || process.IsCompleted) ? "the lock wait or process exit is observed" : null));
        Assert.True(condition: waiting.IsSet, userMessage: "The process ended without observing contention on the source tree's publication lock.");
    }

    internal sealed partial class Fixture : IDisposable {
        // The generator the targets run is the one this law's own build produced, in its configuration.
        private static readonly string Configuration = (typeof(Fixture).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release");

        private readonly TemporaryDirectory? m_directory;

        public Fixture(string? root = null) {
            m_directory = ((root is null) ? new TemporaryDirectory(prefix: "puck-shader-targets-") : null);
            Root = (root ?? m_directory!.RootPath);
            _ = Directory.CreateDirectory(path: Root);
            _ = Directory.CreateDirectory(path: PathOf(path: "started"));
            CliScratchDirectories.PinSdk(directory: Root);
        }

        public string Root { get; }
        public string CompilerPath { get; private set; } = "";

        public string PathOf(string path) => Path.Combine(path1: Root, path2: path);
        public void Write(string path, string text) {
            var fullPath = PathOf(path: path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: fullPath)!);
            File.WriteAllText(contents: text, path: fullPath);
        }
        // The compiles the fake DXC finished in this fixture.
        public int Compiles() => CompiledSources().Length;
        // The stage source each finished compile read, by file name, in completion order.
        public string[] CompiledSources() => (File.Exists(path: PathOf(path: "compiles.txt"))
            ? [.. File.ReadAllLines(path: PathOf(path: "compiles.txt")).Where(predicate: static line => (line.Length != 0)).Select(selector: static line => Path.GetFileName(path: line.Trim()))]
            : []);
        public void ShaderProject(string body, string buildDependencies = "ResolveProjectReferences", string dxc = FakeDxc) {
            var project = XElement.Parse(text: $"<Project>{body}</Project>");

            WriteCompiler(mode: dxc);
            // The output items are evaluated by the import, so the compiler, the backends and the fixture's own cache are
            // selected before it.
            project.AddFirst(content: new XElement("PropertyGroup",
                new XElement(content: "false", name: "PuckComputeShaderDxilEnabled"),
                new XElement("DxcCommand", CompilerPath),
                new XElement("PuckShaderCacheDirectory", PathOf(path: "cache"))));
            project.Add(content: new XElement(name: "Import", content: new XAttribute(name: "Project", value: RepositoryPaths.Resolve(relativePath: "build/Shaders.targets"))));
            project.Add(content: new XElement(name: "Target", content: [new XAttribute(name: "Name", value: "Build"), new XAttribute(name: "DependsOnTargets", value: buildDependencies)]));
            Write(path: "fixture.proj", text: project.ToString());
        }
        public CliProcessResult Run(string target, string[]? properties = null, ManualResetEventSlim? waiting = null) => CliProcess.RunCaptured(
            arguments: ["msbuild", "--disable-build-servers", PathOf(path: "fixture.proj"), "-nologo", "-v:n", "-m:4", "-nodeReuse:false", $"-t:{target}", $"-p:Configuration={Configuration}", .. (properties ?? []).Select(selector: static property => $"-p:{property}")],
            cancellationToken: TestContext.Current.CancellationToken,
            fileName: "dotnet",
            input: string.Empty,
            onOutput: output => {
                if (output.Line.Contains(comparisonType: StringComparison.Ordinal, value: "Waiting for another build's shader publication to finish") &&
                    output.Line.Contains(value: Path.GetFullPath(path: PathOf(path: "obj/shader-publish.lock")), comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    waiting?.Set();
                }
            },
            timeout: TimeSpan.FromMinutes(value: 2),
            workingDirectory: Root
        );
        public void RequireSuccess(CliProcessResult run) => Assert.True(condition: (run.ExitCode == 0), userMessage: $"{run.Stdout}\n{run.Stderr}");
        public void Dispose() => m_directory?.Dispose();
    }
}
