using Puck.Cli.Affected;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>The one run-directory policy, <see cref="RunDirectory"/>: a run that passes leaves no directory behind, a
/// run that fails or never concludes keeps its own directory and names its absolute path, a directory that holds no
/// evidence is deleted whatever the verdict, and the age sweep removes only stale directories of its own prefix. A
/// recording's inner canary transcript lives in the recording's run directory, so a failed inner run keeps it.</summary>
public sealed class RunDirectoryLawTests {
    // A prefix no other law or run shares, so a sweep in one law never reaches another's directories.
    private static string UniquePrefix(string role) => $"puck-run-directory-law-{Guid.NewGuid():N}-{role}-";

    [Fact]
    public void APassingRunLeavesNoDirectory() {
        using var report = new StringWriter();
        string path;

        using (var run = RunDirectory.Create(
            prefix: UniquePrefix(role: "pass"),
            report: report
        )) {
            path = run.Path;
            File.WriteAllText(
                contents: "transcript",
                path: Path.Combine(
                    path1: Directory.CreateDirectory(path: Path.Combine(
                        path1: path,
                        path2: "nested"
                    )).FullName,
                    path2: "transcript.txt"
                )
            );
            Assert.Equal(
                actual: run.Conclude(exitCode: CliExit.Success),
                expected: CliExit.Success
            );
        }

        Assert.False(condition: Directory.Exists(path: path));
        Assert.Equal(
            actual: report.ToString(),
            expected: string.Empty
        );
    }
    [Fact]
    public void AFailingRunKeepsItsDirectoryAndNamesItsAbsolutePath() {
        using var report = new StringWriter();
        var run = RunDirectory.Create(
            prefix: UniquePrefix(role: "fail"),
            report: report
        );

        try {
            var evidence = Path.Combine(
                path1: run.Path,
                path2: "stderr.log"
            );

            File.WriteAllText(
                contents: "the leg refused",
                path: evidence
            );
            Assert.Equal(
                actual: run.Conclude(exitCode: CliExit.Failed),
                expected: CliExit.Failed
            );
            // Disposal after the verdict changes nothing: the first conclusion is the run's.
            run.Dispose();

            Assert.True(condition: Path.IsPathFullyQualified(path: run.Path));
            Assert.True(condition: File.Exists(path: evidence));
            Assert.Equal(
                actual: report.ToString(),
                expected: $"run directory kept: {run.Path}{Environment.NewLine}"
            );
        } finally {
            RunDirectory.Delete(path: run.Path);
        }
    }
    [Fact]
    public void ARunThatEndsWithoutAVerdictKeepsItsDirectoryAndNamesIt() {
        using var report = new StringWriter();
        var path = string.Empty;

        try {
            try {
                using var run = RunDirectory.Create(
                    prefix: UniquePrefix(role: "crash"),
                    report: report
                );

                path = run.Path;

                throw new InvalidOperationException(message: "the run stopped before its verdict");
            } catch (InvalidOperationException) {
                // The run's own failure; what the law reads is the directory it left.
            }

            Assert.True(condition: Directory.Exists(path: path));
            Assert.Equal(
                actual: report.ToString(),
                expected: (RunDirectory.KeptLine(path: path) + Environment.NewLine)
            );
        } finally {
            RunDirectory.Delete(path: path);
        }
    }
    [Fact]
    public void ADirectoryThatHoldsNoEvidenceIsDeletedWhateverTheVerdict() {
        using var report = new StringWriter();
        string path;

        using (var run = RunDirectory.Create(
            keepOnFailure: false,
            prefix: UniquePrefix(role: "staging"),
            report: report
        )) {
            path = run.Path;
            run.Conclude(passed: false);
        }

        Assert.False(condition: Directory.Exists(path: path));
        Assert.Equal(
            actual: report.ToString(),
            expected: string.Empty
        );
    }
    [Fact]
    public void TheAgeSweepRemovesOnlyStaleDirectoriesOfItsOwnPrefix() {
        var family = UniquePrefix(role: "sweep");
        var stranger = UniquePrefix(role: "stranger");
        var stale = RunDirectory.CreatePath(prefix: family);
        var fresh = RunDirectory.CreatePath(prefix: family);
        var staleStranger = RunDirectory.CreatePath(prefix: stranger);
        var aged = ((DateTime.UtcNow - RunDirectory.StaleAge) - TimeSpan.FromHours(value: 1));

        try {
            Directory.SetCreationTimeUtc(
                creationTimeUtc: aged,
                path: stale
            );
            Directory.SetCreationTimeUtc(
                creationTimeUtc: aged,
                path: staleStranger
            );

            Assert.Equal(
                actual: RunDirectory.Sweep(
                    age: RunDirectory.StaleAge,
                    prefix: family
                ),
                expected: 1
            );
            Assert.False(condition: Directory.Exists(path: stale));
            Assert.True(condition: Directory.Exists(path: fresh));
            Assert.True(condition: Directory.Exists(path: staleStranger));
        } finally {
            RunDirectory.Delete(path: stale);
            RunDirectory.Delete(path: fresh);
            RunDirectory.Delete(path: staleStranger);
        }
    }
    [Fact]
    public void TheFirstCreationOfAPrefixSweepsItsStaleDirectories() {
        var family = UniquePrefix(role: "first");
        // A longer prefix of the same family, created first, so the family itself has not been swept yet.
        var leftover = RunDirectory.CreatePath(prefix: (family + "crashed-"));
        string? created = null;

        try {
            Directory.SetCreationTimeUtc(
                creationTimeUtc: ((DateTime.UtcNow - RunDirectory.StaleAge) - TimeSpan.FromHours(value: 1)),
                path: leftover
            );

            created = RunDirectory.CreatePath(prefix: family);

            Assert.False(condition: Directory.Exists(path: leftover));
            Assert.True(condition: Directory.Exists(path: created));
        } finally {
            RunDirectory.Delete(path: leftover);

            if (created is not null) {
                RunDirectory.Delete(path: created);
            }
        }
    }
    [Fact]
    public void ARecordingWhoseCanaryRunFailedKeepsItsTranscriptInItsNamedDirectory() {
        using var report = new StringWriter();
        var run = RunDirectory.Create(
            prefix: UniquePrefix(role: "affected"),
            report: report
        );

        try {
            var transcript = AffectedCoverage.KeepCanaryTranscript(
                directory: run.Path,
                run: new CliProcessResult(
                    ExitCode: CliExit.Refused,
                    OutputLines: [],
                    Stderr: "ERROR: a canary leg failed before it could report\n",
                    Stdout: "canary: selected 1 proof(s).\n",
                    TimedOut: false
                )
            );

            run.Conclude(passed: false);

            Assert.Equal(
                actual: transcript,
                expected: Path.Combine(
                    path1: run.Path,
                    path2: AffectedCoverage.CanaryTranscriptName
                )
            );
            Assert.Equal(
                actual: File.ReadAllText(path: transcript),
                expected: "exit 2\n--- stdout\ncanary: selected 1 proof(s).\n\n--- stderr\nERROR: a canary leg failed before it could report\n\n"
            );
            Assert.Equal(
                actual: report.ToString(),
                expected: (RunDirectory.KeptLine(path: run.Path) + Environment.NewLine)
            );
        } finally {
            RunDirectory.Delete(path: run.Path);
        }
    }
    [Fact]
    public void ALawsDirectoryOutlivesItsDisposalUntilTheLawsVerdict() {
        var directory = new TemporaryDirectory(prefix: UniquePrefix(role: "law"));

        directory.Dispose();

        // The verdict does not exist yet, so the directory waits for it; this law passes, so it is then deleted.
        Assert.True(condition: Directory.Exists(path: directory.RootPath));
    }
}
