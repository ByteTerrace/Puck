using Puck.Cli.Locks;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Gate.Tests;

/// <summary>A scratch solution of three projects under the tree's restore settings (lock files on, every restore
/// locked): one whose lock file matches it, one whose lock file still lists a package the project dropped, and one with
/// no lock file. NuGet runs for real, offline: none of the projects references a package.</summary>
internal static class LockScratch {
    public const string Clean = "src/Clean/packages.lock.json";
    public const string Drifted = "src/Drifted/packages.lock.json";
    public const string Unlocked = "src/Unlocked/packages.lock.json";

    private const string Matching = """
        {
          "version": 1,
          "dependencies": {
            "net10.0": {}
          }
        }
        """;
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
            <RestoreLockedMode>true</RestoreLockedMode>
          </PropertyGroup>
        </Project>
        """;
    private const string Stale = """
        {
          "version": 1,
          "dependencies": {
            "net10.0": {
              "Dropped.Package": {
                "type": "Direct",
                "requested": "[1.0.0, )",
                "resolved": "1.0.0",
                "contentHash": "AA=="
              }
            }
          }
        }
        """;

    public static void Write(Action<string, string> write) {
        write("Puck.slnx", """
            <Solution>
              <Folder Name="/src/">
                <Project Path="src/Clean/Clean.csproj" />
                <Project Path="src/Drifted/Drifted.csproj" />
                <Project Path="src/Unlocked/Unlocked.csproj" />
              </Folder>
            </Solution>
            """);
        foreach (var name in ((string[])["Clean", "Drifted", "Unlocked"])) { write($"src/{name}/{name}.csproj", Project); }
        write(Clean, Matching);
        write(Drifted, Stale);
    }
    /// <summary>Runs <c>dotnet</c> in <paramref name="repositoryRoot"/>, as the gate's process runner does.</summary>
    public static GateStepResult Dotnet(string repositoryRoot, IReadOnlyList<string> arguments) {
        var run = CliProcess.RunCaptured(arguments: arguments, fileName: "dotnet", input: string.Empty, timeout: TimeSpan.FromMinutes(minutes: 5), workingDirectory: repositoryRoot);

        Assert.False(condition: run.TimedOut, userMessage: $"dotnet {string.Join(separator: ' ', values: arguments)} timed out");
        return new GateStepResult(ExitCode: run.ExitCode, Output: string.Join(separator: '\n', values: run.OutputLines.Select(selector: static line => line.Line)));
    }
}

/// <summary>CONTRACT UNDER TEST: <see cref="GateRun"/> restores the solution locked before anything builds, and the
/// solution build restores nothing, so no unlocked restore can rewrite a lock file the check passed. A lock file that no
/// longer matches its project, or a project with none, stops the gate before the build, named by its project, and the
/// check rewrites and creates no lock file.</summary>
public sealed class GateLockFileLawTests : GateRunLaws {
    [Fact]
    public void TheLockedRestoreRunsBeforeAnythingBuildsAndTheSolutionBuildRestoresNothing() {
        using var branches = new Branches();
        using var directory = new TemporaryDirectory(prefix: "puck-gate-locks-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty));

        Assert.Equal(expected: CliExit.Success, actual: Gate(branches: branches, directory: directory, runner: runner).ExitCode);
        Assert.Equal(expected: "run locks", actual: runner.Events[0]);
        var restore = Assert.Single(collection: runner.Restores);

        Assert.Equal(expected: ["restore", "Puck.slnx", "--locked-mode", "--force"], actual: restore[..4]);
        Assert.Contains(collection: restore, expected: CliOptions.NoNodeReuse);
        Assert.Contains(collection: runner.Builds.Single(predicate: static build => (build[1] == "Puck.slnx")), expected: "--no-restore");
    }
    [Fact]
    public void ADriftedOrMissingLockFileStopsTheGateBeforeTheBuildByProjectAndNoLockFileIsWritten() {
        using var branches = new Branches();

        LockScratch.Write(write: branches.Checkout.Write);
        var root = branches.Checkout.Root;
        var clean = File.ReadAllBytes(path: Path.Combine(path1: root, path2: LockScratch.Clean));
        var drifted = File.ReadAllBytes(path: Path.Combine(path1: root, path2: LockScratch.Drifted));
        using var directory = new TemporaryDirectory(prefix: "puck-gate-locks-law-");
        var runner = new FakeRunner(build: new GateStepResult(ExitCode: 0, Output: string.Empty)) { Restore = LockScratch.Dotnet };

        var (exitCode, output, _) = Gate(branches: branches, directory: directory, runner: runner);

        Assert.Equal(actual: exitCode, expected: CliExit.Failed);
        Assert.Contains(actualString: output, expectedSubstring: "gate: locks FAILED (exit 1, ");
        Assert.Contains(actualString: output, expectedSubstring: "  src/Drifted/Drifted.csproj: NU1004 The package references have changed for net10.0.");
        Assert.Contains(actualString: output, expectedSubstring: "  src/Unlocked/Unlocked.csproj: has no packages.lock.json");
        Assert.Contains(actualString: output, expectedSubstring: "puck locks");
        Assert.DoesNotContain(actualString: output, expectedSubstring: "src/Clean/");
        Assert.DoesNotContain(collection: runner.Events, expected: "run build");
        Assert.False(condition: runner.Copied);
        Assert.Equal(expected: clean, actual: File.ReadAllBytes(path: Path.Combine(path1: root, path2: LockScratch.Clean)));
        Assert.Equal(expected: drifted, actual: File.ReadAllBytes(path: Path.Combine(path1: root, path2: LockScratch.Drifted)));
        Assert.False(condition: File.Exists(path: Path.Combine(path1: root, path2: LockScratch.Unlocked)), userMessage: "the check left a lock file it created");
    }
}
/// <summary>CONTRACT UNDER TEST: <see cref="LockFiles.Record"/>, the explicit way a lock file changes, restores unlocked
/// against the tree's locked default, rewrites exactly the lock files that no longer match their projects, creates a
/// missing one, and names each; afterwards the locked check passes.</summary>
public sealed class LockFileRecordLawTests {
    [Fact]
    public void RecordingRewritesTheDriftedLockFileCreatesTheMissingOneAndNamesEach() {
        using var directory = new TemporaryDirectory(prefix: "puck-locks-record-law-");

        LockScratch.Write(write: (name, text) => directory.WriteText(name: name, text: text));
        var root = directory.RootPath;
        var clean = File.ReadAllBytes(path: Path.Combine(path1: root, path2: LockScratch.Clean));
        var result = LockFiles.Record(changed: out var changed, repositoryRoot: root, restore: arguments => LockScratch.Dotnet(arguments: arguments, repositoryRoot: root), update: false);

        Assert.True(condition: (result.ExitCode == 0), userMessage: result.Output);
        Assert.Equal(actual: changed, expected: [LockScratch.Drifted, LockScratch.Unlocked]);
        Assert.Equal(expected: clean, actual: File.ReadAllBytes(path: Path.Combine(path1: root, path2: LockScratch.Clean)));
        Assert.DoesNotContain(expectedSubstring: "Dropped.Package", actualString: File.ReadAllText(path: Path.Combine(path1: root, path2: LockScratch.Drifted)));
        var check = LockFiles.Check(repositoryRoot: root, restore: arguments => LockScratch.Dotnet(arguments: arguments, repositoryRoot: root));

        Assert.True(condition: (check.ExitCode == CliExit.Success), userMessage: string.Join(separator: '\n', values: check.Report));
    }
}
