using System.Xml.Linq;
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
              <PuckWriteShaderHashSidecars BytecodeFiles="@(Cached)" Token="missing-output" />
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

    private sealed class Fixture : IDisposable {
        public string Root { get; } = CliScratchDirectories.CreateProject(prefix: "puck-shader-targets-");

        public string PathOf(string path) => Path.Combine(path1: Root, path2: path);
        public void Write(string path, string text) {
            var fullPath = PathOf(path: path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: fullPath)!);
            File.WriteAllText(contents: text, path: fullPath);
        }
        public void ShaderProject(string body, string buildDependencies = "ResolveProjectReferences") {
            var project = XElement.Parse(text: $"<Project>{body}</Project>");

            project.AddFirst(content: XElement.Parse(text: "<PropertyGroup><PuckComputeShaderDxilEnabled>false</PuckComputeShaderDxilEnabled></PropertyGroup>"));
            project.Add(content: new XElement(name: "Import", content: new XAttribute(name: "Project", value: RepositoryPaths.Resolve(relativePath: "build/Shaders.targets"))));
            project.Add(content: XElement.Parse(text: FakeDxc));
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
        public void Dispose() => CliScratchDirectories.TryDelete(path: Root);
    }
}
