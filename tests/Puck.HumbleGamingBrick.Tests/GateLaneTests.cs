using System.Diagnostics;
using System.Reflection;

namespace Puck.HumbleGamingBrick.Tests;

/// <summary>Runs the battery's gate lane the way a build agent does — the real executable, its exit code, and the files
/// it leaves behind — so <c>dotnet test</c> over the solution covers the emulator. Corpus stages skip when the corpora
/// have not been fetched into the local cache; a skip is not a pass on those rows, only the self-contained stages
/// are then proven.</summary>
public sealed class GateLaneTests {
    [Fact]
    public async Task GateLaneIsGreen() {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = (typeof(GateLaneTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug");
        var artifacts = Path.Combine(
            path1: Path.GetTempPath(),
            path2: "puck-gb-post-gate",
            path3: Guid.NewGuid().ToString(format: "N")
        );
        var startInfo = new ProcessStartInfo {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot,
        };

        foreach (var argument in new[] {
            "run",
            "--project",
            Path.Combine(
                path1: "src",
                path2: "Puck.HumbleGamingBrick.Post"
            ),
            "--configuration",
            configuration,
            "--no-build",
            "--",
            "--lane",
            "gate",
            "--artifacts",
            artifacts,
        }) {
            startInfo.ArgumentList.Add(item: argument);
        }

        using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: "dotnet did not start"));

        var cancellation = TestContext.Current.CancellationToken;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken: cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken: cancellation);

        await process.WaitForExitAsync(cancellationToken: cancellation);

        var report = Path.Combine(
            path1: artifacts,
            path2: "post-report.txt"
        );

        Assert.True(
            condition: (process.ExitCode == 0),
            userMessage: $"gate lane exited {process.ExitCode}\n{(File.Exists(path: report) ? await File.ReadAllTextAsync(path: report, cancellationToken: cancellation) : await stdout)}\n{await stderr}"
        );
        Assert.True(condition: File.Exists(path: Path.Combine(
            path1: artifacts,
            path2: "results.junit.xml"
        )));
        Assert.True(condition: File.Exists(path: Path.Combine(
            path1: artifacts,
            path2: "Expectations.candidate.json"
        )));
    }

    private static string FindRepositoryRoot() {
        var directory = new DirectoryInfo(path: AppContext.BaseDirectory);

        while (directory is not null) {
            if (File.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: "Puck.slnx"
            ))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(message: "Puck.slnx not found above the test assembly");
    }
}
