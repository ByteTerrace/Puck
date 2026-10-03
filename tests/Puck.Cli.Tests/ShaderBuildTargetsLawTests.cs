using System.Xml.Linq;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Exercises the shipped MSBuild targets in isolated projects. A CPU-only stand-in for DXC writes a complete
/// output and can fail a later batch; the real sidecar tasks, incremental gates and pack collection still run.</summary>
public sealed class ShaderBuildTargetsLawTests {
    private const string FakeDxc = """
        <UsingTask TaskName="Exec" Override="true" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
          <ParameterGroup>
            <Command Required="true" />
            <ConsoleToMSBuild ParameterType="System.Boolean" />
            <IgnoreExitCode ParameterType="System.Boolean" />
            <StandardErrorImportance />
            <StandardOutputImportance />
            <ExitCode ParameterType="System.Int32" Output="true" />
          </ParameterGroup>
          <Task><Code Type="Fragment" Language="cs"><![CDATA[
            ExitCode = 0;
            if (Command.Contains("--version")) { return true; }
            var output = Command.Split('"')[3];
            File.WriteAllText(output, "compiled bytecode");
            File.AppendAllText("compiles.txt", output + "\n");
            if (Command.Contains("b.comp.hlsl")) {
                ExitCode = 1;
                Log.LogError("deliberate compiler failure");
                return false;
            }
            return true;
          ]]></Code></Task>
        </UsingTask>
        """;
    // A stand-in for DXC whose output names the process that wrote it, so two builds of one source publish different
    // bytes.
    private const string ProcessDxc = """
        <UsingTask TaskName="Exec" Override="true" TaskFactory="RoslynCodeTaskFactory" AssemblyFile="$(MSBuildToolsPath)/Microsoft.Build.Tasks.Core.dll">
          <ParameterGroup>
            <Command Required="true" />
            <ConsoleToMSBuild ParameterType="System.Boolean" />
            <IgnoreExitCode ParameterType="System.Boolean" />
            <StandardErrorImportance />
            <StandardOutputImportance />
            <ExitCode ParameterType="System.Int32" Output="true" />
          </ParameterGroup>
          <Task><Code Type="Fragment" Language="cs"><![CDATA[
            ExitCode = 0;
            if (Command.Contains("--version")) { return true; }
            File.WriteAllText(Command.Split('"')[3], "compiled by process " + System.Diagnostics.Process.GetCurrentProcess().Id);
            return true;
          ]]></Code></Task>
        </UsingTask>
        """;

    [Fact]
    public async Task TwoInterleavedPublishersLeaveABytecodeAndSidecarOfOneGeneration() {
        if (!OperatingSystem.IsWindows()) {
            Assert.Skip(reason: "Stalling a rename by denying delete sharing requires Windows mandatory file sharing.");
        }

        using var fixture = new Fixture();
        var directory = fixture.PathOf(path: "Assets/Shaders");

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "source");
        // A source newer than every output keeps the incremental gate open, so each build compiles and publishes.
        File.SetLastWriteTimeUtc(lastWriteTimeUtc: new DateTime(day: 1, hour: 0, kind: DateTimeKind.Utc, minute: 0, month: 1, second: 0, year: 2099), path: fixture.PathOf(path: "Assets/Shaders/a.comp.hlsl"));
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

        // Publisher B compiles meanwhile. Once its compiler output exists it either publishes its whole pair at once or
        // waits for A to finish; either way A is then let go.
        var second = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(target: "Build"));

        _ = WaitFor(find: () => Directory.EnumerateFiles(path: directory, searchPattern: "a.comp.spv.*.tmp").FirstOrDefault(predicate: static path => !path.Contains(comparisonType: StringComparison.Ordinal, value: ".hash.")));
        _ = await Task.WhenAny(task1: second, task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TimeSpan.FromSeconds(value: 3)));
        heldTemporary.Dispose();
        fixture.RequireSuccess(run: await first);
        fixture.RequireSuccess(run: await second);

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
        var first = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(properties: ["BaseIntermediateOutputPath=obj/first/"], target: "Build"));
        var second = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(properties: ["BaseIntermediateOutputPath=obj/second/"], target: "Build"));

        try {
            // Both compilers finish while publication is held, including on Linux. Moving managed intermediates does
            // not create a second lock for the same source-tree outputs, and the lock is never held during compilation.
            _ = WaitFor(find: () => (((first.IsCompleted || second.IsCompleted) || (Directory.EnumerateFiles(path: fixture.PathOf(path: "Assets/Shaders"), searchPattern: "a.comp.spv.*.tmp").Count() == 2)) ? "publishers reached publication" : null));
            // A publisher that took a lock of its own would finish within this wait; one sharing the held lock cannot.
            _ = await Task.WhenAny(task1: Task.WhenAll(first, second), task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TimeSpan.FromSeconds(value: 2)));
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
        var pack = Task.Run(cancellationToken: TestContext.Current.CancellationToken, function: () => fixture.Run(properties: ["NoBuild=true"], target: "_GetPackageFiles"));

        try {
            _ = WaitFor(find: () => (File.Exists(path: fixture.PathOf(path: "collecting.txt")) ? "collecting" : null));
            _ = await Task.WhenAny(task1: pack, task2: Task.Delay(cancellationToken: TestContext.Current.CancellationToken, delay: TimeSpan.FromSeconds(value: 1)));
            Assert.False(condition: pack.IsCompleted, userMessage: "Collection inspected missing outputs without waiting for the publisher's lock.");

            // This process owns the publication lock. It materializes the first bytecode, then either commits its
            // sidecar or simulates a publisher cut short before writing the commit record.
            fixture.Write(path: "Assets/Shaders/a.comp.spv", text: "bytecode");
            if (commitSidecar) {
                var sourceHash = Convert.ToHexStringLower(bytes: System.Security.Cryptography.SHA256.HashData(source: System.Text.Encoding.UTF8.GetBytes(s: "source")));
                var bytecodeHash = Convert.ToHexStringLower(bytes: System.Security.Cryptography.SHA256.HashData(source: System.Text.Encoding.UTF8.GetBytes(s: "bytecode")));

                fixture.Write(path: "Assets/Shaders/a.comp.spv.hash", text: $"source:{sourceHash}\nbytecode:{bytecodeHash}\n");
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
    }
    [Fact]
    public void AnIncludeRestoredByAReferenceIsAnInputOnTheFirstBuildAndTheNextBuildSkipsCompilation() {
        using var fixture = new Fixture();

        fixture.Write(path: "Assets/Shaders/a.comp.hlsl", text: "source");
        fixture.ShaderProject(body: """
            <ItemGroup>
              <ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" />
              <ShaderInclude Include="Assets/Shaders/*.hlsli" />
            </ItemGroup>
            <Target Name="ResolveProjectReferences">
              <WriteLinesToFile File="Assets/Shaders/generated.hlsli" Lines="generated declaration" WriteOnlyWhenDifferent="true" Overwrite="true" />
            </Target>
            """);

        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        var bytecode = fixture.PathOf(path: "Assets/Shaders/a.comp.spv");
        var settled = File.GetLastWriteTimeUtc(path: bytecode);

        fixture.RequireSuccess(run: fixture.Run(target: "Build"));
        Assert.Single(collection: File.ReadAllLines(path: fixture.PathOf(path: "compiles.txt")));
        Assert.Equal(expected: settled, actual: File.GetLastWriteTimeUtc(path: bytecode));
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
            step: () => (found = find()) is not null
        );

        return found!;
    }

    private sealed class Fixture : IDisposable {
        private readonly TemporaryDirectory m_directory = new(prefix: "puck-shader-targets-");

        public Fixture() => CliScratchDirectories.PinSdk(directory: Root);

        public string Root => m_directory.RootPath;

        public string PathOf(string path) => Path.Combine(path1: Root, path2: path);
        public void Write(string path, string text) {
            var fullPath = PathOf(path: path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: fullPath)!);
            File.WriteAllText(contents: text, path: fullPath);
        }
        public void ShaderProject(string body, string buildDependencies = "ResolveProjectReferences", string dxc = FakeDxc) {
            var project = XElement.Parse(text: $"<Project>{body}</Project>");

            project.AddFirst(content: XElement.Parse(text: "<PropertyGroup><PuckComputeShaderDxilEnabled>false</PuckComputeShaderDxilEnabled></PropertyGroup>"));
            project.Add(content: new XElement(name: "Import", content: new XAttribute(name: "Project", value: RepositoryPaths.Resolve(relativePath: "build/Shaders.targets"))));
            project.Add(content: XElement.Parse(text: dxc));
            project.Add(content: new XElement(name: "Target", content: [new XAttribute(name: "Name", value: "Build"), new XAttribute(name: "DependsOnTargets", value: buildDependencies)]));
            Write(path: "fixture.proj", text: project.ToString());
        }
        public CliProcessResult Run(string target, string[]? properties = null) => CliProcess.RunCaptured(
            arguments: ["msbuild", "--disable-build-servers", PathOf(path: "fixture.proj"), "-nologo", "-v:q", $"-t:{target}", .. (properties ?? []).Select(selector: static property => $"-p:{property}")],
            cancellationToken: TestContext.Current.CancellationToken,
            fileName: "dotnet",
            input: string.Empty,
            timeout: TimeSpan.FromMinutes(value: 2),
            workingDirectory: Root
        );
        public void RequireSuccess(CliProcessResult run) => Assert.True(condition: (run.ExitCode == 0), userMessage: $"{run.Stdout}\n{run.Stderr}");
        public void Dispose() => m_directory.Dispose();
    }
}
