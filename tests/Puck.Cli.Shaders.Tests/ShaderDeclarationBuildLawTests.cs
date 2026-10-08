using System.Xml.Linq;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Shaders.Tests;

/// <summary>Runs the generator project's actual targets over a scratch checkout, using the already-built host.
/// Generated files remain inputs to reconciliation even when every assembly and an old stamp are unchanged.</summary>
public sealed class ShaderDeclarationBuildLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void EveryBuildRepairsDriftAndAMissingDeclarationRegardlessOfTheContinuousIntegrationProperty(bool continuousIntegration) {
        WithProject(action: (root, project, declaration) => {
            var path = Path.Combine(path1: root, path2: declaration.Path);

            File.WriteAllText(contents: "drift", path: path);
            RequireSuccess(run: Run(continuousIntegration: continuousIntegration, project: project, target: "Build"));
            Assert.Equal(expected: declaration.Generate(), actual: File.ReadAllText(path: path));
            var settled = File.GetLastWriteTimeUtc(path: path);

            RequireSuccess(run: Run(continuousIntegration: continuousIntegration, project: project, target: "Build"));
            Assert.Equal(expected: settled, actual: File.GetLastWriteTimeUtc(path: path));

            Directory.Delete(path: Path.GetDirectoryName(path: path)!, recursive: true);
            RequireSuccess(run: Run(continuousIntegration: continuousIntegration, project: project, target: "Build"));
            Assert.Equal(expected: declaration.Generate(), actual: File.ReadAllText(path: path));
        });
    }

    private static void WithProject(Action<string, string, ShaderDeclaration> action) {
        using var scratch = new TemporaryDirectory(prefix: "puck-declaration-build-");
        var root = scratch.RootPath;

        CliScratchDirectories.PinSdk(directory: root);

        var declarations = ShaderDeclarations.Of(files: ShaderDeclarations.InterfaceFiles(repositoryRoot: RepositoryPaths.RequireRoot()), packages: RenderGraphPackageCatalog.Engine, problems: []);

        foreach (var declaration in declarations) {
            var path = Path.Combine(path1: root, path2: declaration.Path);

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            File.WriteAllText(path: path, contents: declaration.Generate());
        }

        var directory = Path.Combine(path1: root, path2: "src/Puck.Shaders.Generator");
        var intermediate = Directory.CreateDirectory(path: Path.Combine(path1: directory, path2: "obj"));
        var stamp = Path.Combine(path1: intermediate.FullName, path2: "declarations.stamp");

        File.WriteAllText(contents: string.Empty, path: stamp);
        File.SetLastWriteTimeUtc(path: stamp, lastWriteTimeUtc: new DateTime(day: 1, hour: 0, kind: DateTimeKind.Utc, minute: 0, month: 1, second: 0, year: 2099));

        var configuration = new DirectoryInfo(path: AppContext.BaseDirectory).Parent!.Name;
        var host = RepositoryPaths.Resolve(relativePath: $"src/Puck.Shaders.Generator/bin/{configuration}/net10.0/Puck.Shaders.Generator.dll");
        var targets = XDocument.Load(uri: RepositoryPaths.Resolve(relativePath: "src/Puck.Shaders.Generator/Puck.Shaders.Generator.csproj")).Root!.Elements(name: "Target");
        var project = new XElement(name: "Project", content: [
            new XElement(name: "PropertyGroup", content: [
                new XElement(content: host, name: "TargetPath"),
                new XElement(name: "IntermediateOutputPath", content: (intermediate.FullName + "/")),
            ]),
            new XElement(name: "Target", content: new XAttribute(name: "Name", value: "Build")),
            .. targets.Select(selector: static target => new XElement(other: target)),
        ]);
        var projectPath = Path.Combine(path1: directory, path2: "fixture.proj");

        File.WriteAllText(path: projectPath, contents: project.ToString());
        action(root, projectPath, declarations.Single(predicate: static declaration => declaration.Path.EndsWith(comparisonType: StringComparison.Ordinal, value: SdfIsaHlsl.FileName)));
    }
    private static CliProcessResult Run(string project, string target, bool continuousIntegration) => CliProcess.RunCaptured(
        arguments: ["msbuild", "--disable-build-servers", project, "-nologo", "-v:q", $"-t:{target}", $"-p:ContinuousIntegrationBuild={continuousIntegration}"],
        cancellationToken: TestContext.Current.CancellationToken,
        fileName: "dotnet",
        input: string.Empty,
        timeout: TimeSpan.FromMinutes(value: 2),
        workingDirectory: Path.GetDirectoryName(path: project)
    );
    private static void RequireSuccess(CliProcessResult run) => Assert.True(condition: (run.ExitCode == 0), userMessage: $"{run.Stdout}\n{run.Stderr}");
}
