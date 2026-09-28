using System.Text.Json;
using Puck.Hosting;
using Xunit;

namespace Puck.Cli.Tests;

public sealed class CliScratchDirectoriesLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task AScratchProjectResolvesTheRepositorySdk(bool nested) {
        var directory = CliScratchDirectories.CreateProject(prefix: "puck-sdk-law-");

        try {
            var pin = File.ReadAllBytes(path: RepositoryPaths.Resolve(relativePath: "global.json"));

            Assert.Equal(expected: pin, actual: File.ReadAllBytes(path: Path.Combine(path1: directory, path2: "global.json")));
            using var policy = JsonDocument.Parse(utf8Json: pin);
            var version = policy.RootElement.GetProperty(propertyName: "sdk").GetProperty(propertyName: "version").GetString();
            var projectDirectory = (nested ? Path.Combine(path1: directory, path2: "nested/project") : directory);

            Directory.CreateDirectory(path: projectDirectory);
            File.WriteAllText(
                path: Path.Combine(path1: projectDirectory, path2: "Probe.csproj"),
                contents: "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"
            );
            var result = await ChildProcess.RunAsync(
                fileName: "dotnet",
                arguments: ["--version"],
                workingDirectory: projectDirectory,
                timeout: TimeSpan.FromMinutes(minutes: 1),
                cancellationToken: TestContext.Current.CancellationToken
            );

            Assert.False(condition: result.TimedOut);
            Assert.Equal(expected: 0, actual: result.ExitCode);
            Assert.Equal(expected: version, actual: result.Stdout.Trim());
        } finally {
            Directory.Delete(path: directory, recursive: true);
        }
    }
}
