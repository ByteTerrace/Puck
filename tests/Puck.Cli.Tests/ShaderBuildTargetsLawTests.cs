using System.Xml.Linq;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Exercises the shipped MSBuild targets in isolated projects. A CPU-only stand-in for DXC writes a complete
/// output and can fail a later batch; the real sidecar tasks, incremental gates and pack collection still run.</summary>
public sealed partial class ShaderBuildTargetsLawTests {
    private const string FakeDxc = "normal";
    private const string ProcessDxc = "process";

    [Fact]
    public async Task TwoInterleavedPublishersLeaveABytecodeAndSidecarOfOneGeneration() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "Stalling a rename by denying delete sharing requires Windows mandatory file sharing.");
        }

        using var fixture = new Fixture();
        var directory = fixture.PathOf(path: "Assets/Shaders");

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "source");
        fixture.Write(path: "Assets/Shaders/a.comp.spv", text: "earlier bytecode");
        fixture.Write(path: "Assets/Shaders/a.comp.spv.hash", text: "earlier sidecar");
        fixture.ShaderProject(body: """
            <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
            <Target Name="ResolveProjectReferences" />
            """, dxc: ProcessDxc);

        // Publisher A stalls holding its new sidecar: first the old sidecar is held open so A cannot replace or remove
        // it, then A's own temporary sidecar is held so A cannot move it in.
        using var heldSidecar = new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash"), share: FileShare.Read);
        var first = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(target: "Build"));
        using var heldTemporary = WaitFor(find: () => {
            var stalledSidecar = Directory.EnumerateFiles(path: directory, searchPattern: "a.comp.spv.hash.*.tmp").SingleOrDefault();

            if (stalledSidecar is null) {
                return null;
            }

            try {
                // Denying write sharing succeeds only after A closes its writer; keep this same handle to stall rename.
                return new FileStream(access: FileAccess.Read, mode: FileMode.Open, path: stalledSidecar, share: FileShare.Read);
            } catch (IOException) {
                return null;
            }
        });

        heldSidecar.Dispose();
        _ = WaitFor(find: () => (!File.Exists(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash")) ? "the old commit record was removed" : null));

        // Publisher B compiles meanwhile. Once its compiler output exists it either publishes its whole pair at once or
        // waits for A to finish; either way A is then let go.
        using var secondWaiting = new ManualResetEventSlim();
        var second = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(target: "Build", waiting: secondWaiting));

        _ = WaitFor(find: () => Directory.EnumerateFiles(path: directory, searchPattern: "a.comp.spv.*.tmp").FirstOrDefault(predicate: static path => !path.Contains(comparisonType: StringComparison.Ordinal, value: ".hash.")));
        try {
            RequireWaiting(process: second, waiting: secondWaiting);
        } finally {
            heldTemporary.Dispose();
            fixture.RequireSuccess(run: await first);
            fixture.RequireSuccess(run: await second);
        }

        // The pair on disk is one generation's: the freshness check holds the bytecode to its sidecar.
        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode"));
    }
    [Fact]
    public async Task PublishersWithDifferentIntermediateDirectoriesShareTheSourceTreesLock() {
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "source");
        fixture.Write(path: "obj/shader-publish.lock", text: string.Empty);
        fixture.ShaderProject(body: """
            <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
            <Target Name="ResolveProjectReferences" />
            """, dxc: ProcessDxc);

        using var heldLock = new FileStream(access: FileAccess.ReadWrite, mode: FileMode.Open, path: fixture.PathOf(path: "obj/shader-publish.lock"), share: FileShare.None);
        using var firstWaiting = new ManualResetEventSlim();
        using var secondWaiting = new ManualResetEventSlim();
        var first = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(properties: ["BaseIntermediateOutputPath=obj/first/"], target: "Build", waiting: firstWaiting));
        var second = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(properties: ["BaseIntermediateOutputPath=obj/second/"], target: "Build", waiting: secondWaiting));

        try {
            // Both compilers finish while publication is held, including on Linux. Moving managed intermediates does
            // not create a second lock for the same source-tree outputs, and the lock is never held during compilation.
            _ = WaitFor(find: () => (((first.IsCompleted || second.IsCompleted) || (Directory.EnumerateFiles(path: fixture.PathOf(path: "Assets/Shaders"), searchPattern: "a.comp.spv.*.tmp").Count() == 2)) ? "publishers reached publication" : null));
            RequireWaiting(process: first, waiting: firstWaiting);
            RequireWaiting(process: second, waiting: secondWaiting);
            Assert.False(condition: (first.IsCompleted || second.IsCompleted), userMessage: "A publisher finished while the source tree's publication lock was held.");
            Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv")));
            Assert.False(condition: File.Exists(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash")));
        } finally {
            heldLock.Dispose();
            fixture.RequireSuccess(run: await first);
            fixture.RequireSuccess(run: await second);
        }

        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode"));
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task ANoBuildPackWaitsForTheFirstPublicationBeforeCollectingAndRequiresItsCommitRecord(bool commitSidecar) {
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "source");
        fixture.Write(path: "obj/shader-publish.lock", text: string.Empty);
        fixture.ShaderProject(body: """
            <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
            <Target Name="CollectionStarted" BeforeTargets="CollectShaderBytecode">
              <WriteLinesToFile File="collecting.txt" Lines="collecting" Overwrite="true" />
            </Target>
            <Target Name="_GetPackageFiles">
              <WriteLinesToFile File="packed.txt" Lines="@(Content->'%(Identity)')" Overwrite="true" />
            </Target>
            """);

        using var heldLock = new FileStream(access: FileAccess.ReadWrite, mode: FileMode.Open, path: fixture.PathOf(path: "obj/shader-publish.lock"), share: FileShare.None);
        using var waiting = new ManualResetEventSlim();
        var pack = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(properties: ["NoBuild=true", "PuckDxcComputeSpirv=fixture-recipe"], target: "_GetPackageFiles", waiting: waiting));

        try {
            _ = WaitFor(find: () => (File.Exists(path: fixture.PathOf(path: "collecting.txt")) ? "collecting" : null));
            RequireWaiting(process: pack, waiting: waiting);
            Assert.False(condition: pack.IsCompleted, userMessage: "Collection inspected missing outputs without waiting for the publisher's lock.");

            // This process owns the publication lock. It materializes the first bytecode, then either commits its
            // sidecar or simulates a publisher cut short before writing the commit record.
            fixture.Write(path: "Assets/Shaders/a.comp.spv", text: "bytecode");
            if (commitSidecar) {
                var sourceHash = Convert.ToHexStringLower(bytes: System.Security.Cryptography.SHA256.HashData(source: System.Text.Encoding.UTF8.GetBytes(s: "source")));
                var bytecodeHash = Convert.ToHexStringLower(bytes: System.Security.Cryptography.SHA256.HashData(source: System.Text.Encoding.UTF8.GetBytes(s: "bytecode")));
                var recipeHash = Convert.ToHexStringLower(bytes: System.Security.Cryptography.SHA256.HashData(source: System.Text.Encoding.UTF8.GetBytes(s: $".spv\n\"{fixture.CompilerPath}\" fixture-recipe")));

                fixture.Write(path: "Assets/Shaders/a.comp.spv.hash", text: $"source:{sourceHash}\nrecipe:{recipeHash}\nbytecode:{bytecodeHash}\n");
            }
        } finally {
            heldLock.Dispose();
            _ = await pack;
        }

        var result = await pack;

        if (commitSidecar) {
            fixture.RequireSuccess(run: result);
            Assert.Equal(expected: ["Assets/Shaders/a.comp.spv"], actual: File.ReadAllLines(path: fixture.PathOf(path: "packed.txt")));
        } else {
            Assert.NotEqual(expected: 0, actual: result.ExitCode);
            Assert.Contains(expectedSubstring: "has no '.hash' sidecar", actualString: result.Stdout);
            Assert.False(condition: File.Exists(path: fixture.PathOf(path: "packed.txt")));
        }
    }
    [Fact]
    public void AFailedLaterCompileRemovesEveryTemporaryFromThatInvocation() {
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "first source");
        fixture.Write(path: "Assets/Shaders/b.comp.hlsl", text: "failing source");
        fixture.ShaderProject(body: """
            <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
            <Target Name="ResolveProjectReferences" />
            """);

        var build = fixture.Run(target: "Build");

        Assert.NotEqual(expected: 0, actual: build.ExitCode);
        Assert.Contains(expectedSubstring: "deliberate compiler failure", actualString: build.Stdout);
        Assert.Equal(expected: 2, actual: File.ReadAllLines(path: fixture.PathOf(path: "compiles.txt")).Length);
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.spv", searchOption: SearchOption.AllDirectories));
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.hash", searchOption: SearchOption.AllDirectories));
    }
    [Fact]
    public void AnIncludeRestoredByAReferenceIsAnInputOnTheFirstBuildAndTheNextBuildSkipsCompilation() {
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "source");
        fixture.Write(path: "Assets/Shaders/conventional.hlsli", text: "conventional declaration");
        fixture.Write(path: "Shared/outside.hlsli", text: "explicit outside declaration");
        fixture.ShaderProject(body: """
            <ItemGroup>
              <ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" />
              <ShaderInclude Include="Assets/Shaders/*.hlsli" />
              <ShaderInclude Include="Shared/outside.hlsli" />
            </ItemGroup>
            <Target Name="ResolveProjectReferences">
              <WriteLinesToFile File="Assets/Shaders/generated.hlsli" Lines="generated declaration" WriteOnlyWhenDifferent="true" Overwrite="true" />
            </Target>
            """);

        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        var bytecode = fixture.PathOf(path: "Assets/Shaders/a.comp.spv");
        var settled = File.GetLastWriteTimeUtc(path: bytecode);
        var sidecar = fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash");
        var committed = File.ReadAllText(path: sidecar);

        // A fresh no-build evaluation must use the publisher's include order, including the explicit outside row.
        fixture.RequireSuccess(run: fixture.Run(target: "CollectShaderBytecode"));
        Assert.Equal(expected: committed, actual: File.ReadAllText(path: sidecar));
        Assert.Single(collection: File.ReadAllLines(path: fixture.PathOf(path: "compiles.txt")));
        Assert.Equal(expected: settled, actual: File.GetLastWriteTimeUtc(path: bytecode));

        // Restore identical tracked inputs with newer timestamps, as a persistent proof clone can do. Content is
        // unchanged, so neither compilation nor publication may run again, even with inputs newer than every output.
        var rewritten = File.GetLastWriteTimeUtc(path: sidecar).AddSeconds(value: 10);
        foreach (var path in new[] { "fixture.proj", "Assets/Shaders/a.comp.hlsl", "Assets/Shaders/conventional.hlsli", "Assets/Shaders/generated.hlsli", "Shared/outside.hlsli" }) {
            fixture.Write(path: path, text: File.ReadAllText(path: fixture.PathOf(path: path)));
            File.SetLastWriteTimeUtc(path: fixture.PathOf(path: path), lastWriteTimeUtc: rewritten);
        }
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Single(collection: File.ReadAllLines(path: fixture.PathOf(path: "compiles.txt")));
        Assert.Equal(expected: settled, actual: File.GetLastWriteTimeUtc(path: bytecode));
        Assert.Equal(expected: committed, actual: File.ReadAllText(path: sidecar));
    }
    [InlineData("source", 3)]
    [InlineData("include", 4)]
    [InlineData("options", 4)]
    [InlineData("command", 4)]
    [InlineData("bytecode", 3)]
    [InlineData("sidecar", 3)]
    [InlineData("recipe-missing", 3)]
    [InlineData("bytecode-missing", 3)]
    [InlineData("sidecar-missing", 3)]
    [Theory]
    public void ChangedContentOrRecipeRecompilesOnlyInvalidPairs(string change, int expectedCompiles) {
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "first source");
        fixture.Write(path: "Assets/Shaders/c.comp.hlsl", text: "independent source");
        fixture.Write(path: "Assets/Shaders/shared.hlsli", text: "shared declaration");
        fixture.ShaderProject(body: """
            <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
            <Target Name="ResolveProjectReferences" />
            """);
        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Equal(expected: 2, actual: File.ReadAllLines(path: fixture.PathOf(path: "compiles.txt")).Length);

        var source = fixture.PathOf(path: "Assets/Shaders/a.comp.hlsl");
        var include = fixture.PathOf(path: "Assets/Shaders/shared.hlsli");
        var bytecode = fixture.PathOf(path: "Assets/Shaders/a.comp.spv");
        var sidecar = (bytecode + ".hash");
        string[] properties = [];

        switch (change) {
            case "source": File.WriteAllText(path: source, contents: "changed source"); break;
            case "include": File.WriteAllText(path: include, contents: "changed declaration"); break;
            case "options": properties = ["PuckDxcComputeSpirv=-spirv -O3 -T cs_6_6 -E main -D CHANGED_RECIPE=1"]; break;
            case "command": properties = ["DxcCommand=another-dxc"]; break;
            case "bytecode": File.WriteAllText(path: bytecode, contents: "corrupted compiled bytes"); break;
            case "sidecar": File.AppendAllText(path: sidecar, contents: File.ReadAllText(path: sidecar)); break;
            case "recipe-missing": File.WriteAllLines(path: sidecar, contents: File.ReadAllLines(path: sidecar).Where(predicate: static line => !line.StartsWith(value: "recipe:", comparisonType: StringComparison.Ordinal))); break;
            case "bytecode-missing": File.Delete(path: bytecode); break;
            case "sidecar-missing": File.Delete(path: sidecar); break;
            default: throw new ArgumentOutOfRangeException(paramName: nameof(change));
        }
        // Old input times cannot make changed bytes fresh; collection must refuse before a compiler repairs them.
        foreach (var path in new[] { source, include }) {
            File.SetLastWriteTimeUtc(path: path, lastWriteTimeUtc: DateTime.UnixEpoch);
        }
        var collect = fixture.Run(target: "CollectShaderBytecode", properties: properties);

        Assert.NotEqual(expected: 0, actual: collect.ExitCode);
        Assert.Equal(expected: 2, actual: File.ReadAllLines(path: fixture.PathOf(path: "compiles.txt")).Length);
        fixture.RequireSuccess(run: fixture.Run(target: "Build", properties: properties));
        Assert.Equal(expected: expectedCompiles, actual: File.ReadAllLines(path: fixture.PathOf(path: "compiles.txt")).Length);
        Assert.Empty(collection: Directory.EnumerateFiles(path: fixture.Root, searchPattern: "*.tmp", searchOption: SearchOption.AllDirectories));
    }
    [Fact]
    public void ACompilerThatProducesNoOutputCannotBlessCachedBytecodeWithANewSidecar() {
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "changed source");
        fixture.Write(path: "Assets/Shaders/a.comp.spv", text: "cached bytecode");
        fixture.Write(path: "Assets/Shaders/a.comp.spv.hash", text: "original sidecar");
        fixture.ShaderProject(body: """
            <ItemGroup>
              <Cached Include="Assets/Shaders/a.comp.spv" SourcePath="$(MSBuildProjectDirectory)/Assets/Shaders/a.comp.hlsl" />
            </ItemGroup>
            <Target Name="Publish">
              <PuckWriteShaderHashSidecars BytecodeFiles="@(Cached)" LockFile="$(_PuckShaderPublishLock)" Token="missing-output" />
            </Target>
            """);

        var publish = fixture.Run(target: "Publish");

        Assert.NotEqual(expected: 0, actual: publish.ExitCode);
        Assert.Contains(expectedSubstring: "produced no temporary bytecode", actualString: publish.Stdout);
        Assert.Equal(expected: "cached bytecode", actual: File.ReadAllText(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv")));
        Assert.Equal(expected: "original sidecar", actual: File.ReadAllText(path: fixture.PathOf(path: "Assets/Shaders/a.comp.spv.hash")));
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

    private static T WaitFor<T>(Func<T?> find) where T : class {
        T? found = null;

        TestLiveness.Until(
            reason: () => "The publisher never reached the awaited point.",
            step: () => ((found = find()) is not null)
        );

        return found!;
    }
    private static void RequireWaiting(Task<CliProcessResult> process, ManualResetEventSlim waiting) {
        _ = WaitFor(find: () => ((waiting.IsSet || process.IsCompleted) ? "the lock wait or process exit is observed" : null));
        Assert.True(condition: waiting.IsSet, userMessage: "The process ended without observing contention on the source tree's publication lock.");
    }

    internal sealed partial class Fixture : IDisposable {
        private readonly TemporaryDirectory? m_directory;

        public Fixture(string? root = null) {
            m_directory = ((root is null) ? new TemporaryDirectory(prefix: "puck-shader-targets-") : null);
            Root = (root ?? m_directory!.RootPath);
            _ = Directory.CreateDirectory(path: Root);
            _ = Directory.CreateDirectory(path: PathOf(path: "started"));
            var pin = PathOf(path: "global.json");

            if (File.Exists(path: pin)) {
                // A reused proof tree already carries its source checkout's pin; prove it agrees before building.
                Assert.Equal(expected: File.ReadAllBytes(path: RepositoryPaths.Resolve(relativePath: "global.json")), actual: File.ReadAllBytes(path: pin));
            } else {
                CliScratchDirectories.PinSdk(directory: Root);
            }
        }

        public string Root { get; }
        public string CompilerPath { get; private set; } = "";

        public string PathOf(string path) => Path.Combine(path1: Root, path2: path);
        public void Write(string path, string text) {
            var fullPath = PathOf(path: path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: fullPath)!);
            File.WriteAllText(contents: text, path: fullPath);
        }
        public void ShaderProject(string body, string buildDependencies = "ResolveProjectReferences", string dxc = FakeDxc) {
            var project = XElement.Parse(text: $"<Project>{body}</Project>");

            WriteCompiler(mode: dxc);
            // Recipe item metadata is evaluated by the import, so the compiler must be selected before it.
            project.AddFirst(content: new XElement("PropertyGroup", new XElement("PuckComputeShaderDxilEnabled", "false"), new XElement("DxcCommand", CompilerPath)));
            project.Add(content: new XElement(name: "Import", content: new XAttribute(name: "Project", value: RepositoryPaths.Resolve(relativePath: "build/Shaders.targets"))));
            project.Add(content: new XElement(name: "Target", content: [new XAttribute(name: "Name", value: "Build"), new XAttribute(name: "DependsOnTargets", value: buildDependencies)]));
            Write(path: "fixture.proj", text: project.ToString());
        }
        public CliProcessResult Run(string target, string[]? properties = null, ManualResetEventSlim? waiting = null) => CliProcess.RunCaptured(
            arguments: ["msbuild", "--disable-build-servers", PathOf(path: "fixture.proj"), "-nologo", "-v:n", "-m:4", "-nodeReuse:false", $"-t:{target}", .. (properties ?? []).Select(selector: static property => $"-p:{property}")],
            cancellationToken: TestContext.Current.CancellationToken,
            fileName: "dotnet",
            input: string.Empty,
            onOutput: output => {
                if (output.Line.Contains(comparisonType: StringComparison.Ordinal, value: "Waiting for another build's shader publication to finish") &&
                    output.Line.Contains(value: Path.GetFullPath(path: PathOf(path: "obj/shader-publish.lock")), comparisonType: StringComparison.Ordinal)) {
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
