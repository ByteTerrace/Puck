using Puck.Testing;
using Xunit;

namespace Puck.Cli.Core.Tests;

/// <summary>Projects the CLI launches share restoring builds and project-named logs with concise failure reasons.</summary>
public sealed class CliProjectBuildLawTests {
    [Fact]
    public void AStubBuildFailureQuotesItsFirstErrorAndNamesItsProjectLog() {
        using var directory = new TemporaryDirectory();

        File.Copy(sourceFileName: RepositoryPaths.Resolve(relativePath: "global.json"), destFileName: directory.PathOf(name: "global.json"));
        _ = directory.WriteText(name: "Directory.Build.props", text: "<Project />\n");
        _ = directory.WriteText(name: "Directory.Build.targets", text: "<Project />\n");
        const string Project = "Puck.Launcher.Stub/Puck.Launcher.Stub.csproj";

        _ = directory.WriteText(name: Project, text: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>\n");
        _ = directory.WriteText(name: "Puck.Launcher.Stub/Failure.cs", text: "#error STUB_BUILD_FAILURE\n");
        var logDirectory = directory.PathOf(name: "run");
        var outputDirectory = directory.PathOf(name: "run/output");

        Assert.False(condition: CliProjectBuild.TryBuild(
            build: out var build,
            error: out var error,
            logDirectory: logDirectory,
            outputDirectory: outputDirectory,
            project: Project,
            repositoryRoot: directory.RootPath,
            timeout: TimeSpan.FromMinutes(value: 3)
        ));
        Assert.NotNull(@object: build);
        Assert.False(condition: build.TimedOut);
        Assert.StartsWith(actualString: error, expectedStartString: "the Puck.Launcher.Stub build exited 1. First errors:");
        Assert.Contains(actualString: error, expectedSubstring: "error CS1029: #error: 'STUB_BUILD_FAILURE'");
        var log = Path.Combine(path1: logDirectory, path2: "Puck.Launcher.Stub.build.log");

        Assert.Contains(expectedSubstring: CliPaths.ToDisplay(fullPath: log), actualString: error);
        Assert.Equal(expected: (build.Stdout + ((build.Stderr.Length == 0) ? string.Empty : $"{Environment.NewLine}--- stderr ---{Environment.NewLine}{build.Stderr}")), actual: File.ReadAllText(path: log));
        Assert.False(condition: File.Exists(path: Path.Combine(path1: outputDirectory, path2: "Puck.Launcher.Stub.build.log")));
    }
    [Fact]
    public void AProjectAddedToTheClosureSinceItsLastRestoreIsRestoredBeforeTheBuild() {
        using var directory = new TemporaryDirectory();
        var budget = TimeSpan.FromMinutes(value: 3);

        File.Copy(
            destFileName: directory.PathOf(name: "global.json"),
            sourceFileName: RepositoryPaths.Resolve(relativePath: "global.json")
        );
        _ = directory.WriteText(
            name: "Directory.Build.props",
            text: "<Project />\n"
        );
        _ = directory.WriteText(
            name: "Directory.Build.targets",
            text: "<Project />\n"
        );

        foreach (var library in ((string[])["Library", "Added"])) {
            _ = directory.WriteText(
                name: $"{library}/{library}.csproj",
                text: "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n"
            );
            _ = directory.WriteText(
                name: $"{library}/{library}.cs",
                text: $"namespace {library};\n\npublic static class Value {{\n    public const int One = 1;\n}}\n"
            );
        }

        WriteApplication(
            directory: directory,
            libraries: ["Library"]
        );
        Assert.True(
            condition: CliProjectBuild.TryBuild(
            build: out _,
            error: out var error,
            outputDirectory: directory.PathOf(name: "out-first"),
            project: "Application/Application.csproj",
            logDirectory: directory.PathOf(name: "logs"),
            repositoryRoot: directory.RootPath,
            timeout: budget
        ),
            userMessage: error
        );

        // The application's assets file now predates its reference to Added, which has never been restored.
        WriteApplication(
            directory: directory,
            libraries: ["Library", "Added"]
        );
        Assert.True(
            condition: CliProjectBuild.TryBuild(
            build: out var second,
            error: out error,
            outputDirectory: directory.PathOf(name: "out-second"),
            project: "Application/Application.csproj",
            logDirectory: directory.PathOf(name: "logs"),
            repositoryRoot: directory.RootPath,
            timeout: budget
        ),
            userMessage: $"{error}\n{second?.Stdout}"
        );
    }

    private static void WriteApplication(TemporaryDirectory directory, IReadOnlyList<string> libraries) {
        var references = string.Concat(values: libraries.Select(selector: static library => $"    <ProjectReference Include=\"../{library}/{library}.csproj\" />\n"));

        _ = directory.WriteText(
            name: "Application/Application.csproj",
            text: $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n{references}  </ItemGroup>\n</Project>\n"
        );
        _ = directory.WriteText(
            name: "Application/Program.cs",
            text: $"return {string.Join(separator: " + ", values: libraries.Select(selector: static library => $"{library}.Value.One"))} - {libraries.Count};\n"
        );
    }
}
