using Puck.Cli.Laws;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>Both CLI build routes recover a diagnosed corrupt reference once, without touching unrelated outputs.</summary>
public sealed class ReferenceAssemblyRecoveryLawTests {
    private static CliProcessResult Result(int exit, string text) => new(ExitCode: exit, OutputLines: [], Stderr: "", Stdout: text, TimedOut: false);

    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void ADiagnosedCorruptReferenceIsRepairedOnceAndItsFailureIsRetained(bool proofRunner, bool truncatedImage) {
        using var directory = new TemporaryDirectory(prefix: "puck-ref-recovery-law-");
        var reference = directory.PathOf(name: "Library/obj/Release/net10.0/ref/Library.dll");

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: reference)!);
        File.WriteAllBytes(reference, (truncatedImage ? File.ReadAllBytes(path: typeof(CliProjectBuild).Assembly.Location)[..128] : new byte[128]));
        var unrelated = directory.WriteText(name: "Unrelated/obj/keep", text: "unchanged");
        var logs = directory.PathOf(name: "logs");
        var diagnostic = $"CSC : error CS0009: Metadata file '{reference}' could not be opened -- PE image doesn't contain managed metadata.";
        var attempts = 0;
        var outcome = Run(proofRunner, directory.RootPath, logs, (_, remaining) => {
            Assert.True(condition: (remaining > TimeSpan.Zero));
            ++attempts;
            if (proofRunner) { File.WriteAllText(Path.Combine(path1: logs, path2: "build.counts"), "1\n0\n2\n"); }
            if (attempts == 1) { return Result(exit: 1, text: diagnostic); }
            Assert.False(condition: Directory.Exists(path: Path.GetDirectoryName(path: reference)));
            return Result(exit: 0, text: "Build succeeded.");
        });

        Assert.True(condition: outcome);
        Assert.Equal(actual: attempts, expected: 2);
        Assert.Equal("unchanged", File.ReadAllText(path: unrelated));
        var kept = Directory.GetFiles(path: logs, searchPattern: "*reference-recovery.build.log").Single();

        Assert.Contains(diagnostic, File.ReadAllText(path: kept));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ASecondCorruptReferenceFailureNeverStartsAThirdBuild(bool proofRunner) {
        using var directory = new TemporaryDirectory(prefix: "puck-ref-recovery-law-");
        var reference = directory.PathOf(name: "Library/obj/Release/net10.0/ref/Library.dll");

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: reference)!);
        File.WriteAllBytes(bytes: new byte[128], path: reference);
        var attempts = 0;

        Assert.False(condition: Run(proofRunner, directory.RootPath, directory.PathOf(name: "logs"), (_, _) => {
            ++attempts;
            return Result(exit: 1, text: $"CSC : error CS0009: Metadata file '{reference}' could not be opened -- invalid image.");
        }));
        Assert.Equal(actual: attempts, expected: 2);
    }
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [Theory]
    public void AnOutsideOrManagedReferenceIsPreservedWithoutRetry(bool proofRunner, bool managed) {
        using var directory = new TemporaryDirectory(prefix: "puck-ref-recovery-law-");
        using var outside = new TemporaryDirectory(prefix: "puck-ref-outside-law-");
        var reference = (managed ? directory : outside).PathOf(name: "Library/obj/Release/net10.0/ref/Library.dll");

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: reference)!);
        if (managed) { File.Copy(typeof(CliProjectBuild).Assembly.Location, reference); } else { File.WriteAllBytes(bytes: new byte[128], path: reference); }
        var before = File.ReadAllBytes(path: reference);
        var attempts = 0;

        Assert.False(condition: Run(proofRunner, directory.RootPath, directory.PathOf(name: "logs"), (_, _) => {
            ++attempts;
            return Result(exit: 1, text: $"CSC : error CS0009: Metadata file '{reference}' could not be opened -- invalid image.");
        }));
        Assert.Equal(actual: attempts, expected: 1);
        Assert.Equal(before, File.ReadAllBytes(path: reference));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ALinkedReferenceDirectoryNeverDeletesItsTargetOrRetries(bool proofRunner) {
        using var directory = new TemporaryDirectory(prefix: "puck-ref-recovery-law-");
        using var target = new TemporaryDirectory(prefix: "puck-ref-target-law-");
        var original = target.WriteText(name: "Library.dll", text: "corrupt reference");
        var linked = directory.PathOf(name: "Library/obj/Release/net10.0/ref");

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: linked)!);
        DirectoryLinks.Create(link: linked, target: target.RootPath);
        try {
            var attempts = 0;

            Assert.False(condition: Run(proofRunner, directory.RootPath, directory.PathOf(name: "logs"), (_, _) => {
                ++attempts;
                return Result(exit: 1, text: $"CSC : error CS0009: Metadata file '{Path.Combine(path1: linked, path2: "Library.dll")}' could not be opened -- invalid image.");
            }));
            Assert.Equal(actual: attempts, expected: 1);
            Assert.Equal("corrupt reference", File.ReadAllText(path: original));
            Assert.True(condition: Directory.Exists(path: linked));
        } finally {
            DirectoryLinks.Remove(link: linked);
        }
    }

    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [Theory]
    public void ARecoveryNeedsWorkCountsFromEveryBuildAttempt(int missingReport) {
        using var directory = new TemporaryDirectory(prefix: "puck-ref-counts-law-");
        var reference = directory.PathOf(name: "Library/obj/Release/net10.0/ref/Library.dll");
        Directory.CreateDirectory(path: Path.GetDirectoryName(path: reference)!);
        File.WriteAllBytes(bytes: new byte[128], path: reference);
        var logs = directory.PathOf(name: "logs");
        var attempts = 0;
        var result = new DotnetLawRunner(buildRunner: (_, _) => {
            ++attempts;
            if (attempts != missingReport) {
                File.WriteAllText(Path.Combine(path1: logs, path2: "build.counts"), "1\n2\n3\n");
            }
            return (attempts == 1)
                ? Result(exit: 1, text: $"CSC : error CS0009: Metadata file '{reference}' could not be opened -- invalid image.")
                : Result(exit: 0, text: "Build succeeded.");
        }).Build(tree: directory.RootPath, project: "Application.csproj", logDirectory: logs,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expected: 2, actual: attempts);
        Assert.Equal(expected: (missingReport == 0), actual: result.Succeeded);
        if (missingReport == 0) {
            Assert.Equal(expected: new LawBuildCounts(Compiled: 2, UpToDate: 4, Targets: 6), actual: result.Counts);
            Assert.Empty(collection: result.Errors);
        } else {
            Assert.Contains(actualString: Assert.Single(collection: result.Errors), expectedSubstring: "no valid work-count report");
        }
    }

    private static bool Run(bool proofRunner, string tree, string logs, Func<IReadOnlyList<string>, TimeSpan, CliProcessResult> runner) {
        if (proofRunner) {
            return new DotnetLawRunner(buildRunner: runner).Build(tree: tree, project: "Application.csproj", logDirectory: logs,
                cancellationToken: TestContext.Current.CancellationToken).Succeeded;
        }
        return CliProjectBuild.TryBuild(repositoryRoot: tree, project: "Application.csproj", outputDirectory: Path.Combine(path1: tree, path2: "out"),
            logDirectory: logs, timeout: TimeSpan.FromMinutes(minutes: 1), build: out _, error: out _, runner: runner);
    }
}
