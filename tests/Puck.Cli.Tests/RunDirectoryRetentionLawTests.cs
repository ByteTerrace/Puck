using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>What the run-directory policy (<see cref="RunDirectory"/>) leaves behind: a directory is named for the
/// process that owns it; a failing run that keeps its directory trims its kind to the newest four directories whose
/// owners have finished; and no sweep removes a directory whose owner still runs, however many or however old. A
/// directory is made to look finished by dating it before this process started, which is how a reused process id
/// reads.</summary>
public sealed class RunDirectoryRetentionLawTests {
    // The policy's cap, spelled here so the law reads the same against any policy.
    private const int Kept = 4;

    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    // A kind no other law or run shares, under the family every run directory belongs to.
    private static string UniqueKind(string role) => $"puck-run-retention-law-{Guid.NewGuid():N}-{role}-";
    // A directory of the kind owned by this process's id but created before this process started: a finished owner
    // whose id was reused. Older directories are dated further back.
    private static string Finished(string kind, int age) {
        var path = Directory.CreateDirectory(path: Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"{kind}{Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture)}-{Path.GetRandomFileName()}"
        )).FullName;

        Directory.SetCreationTimeUtc(creationTimeUtc: (ProcessStart - TimeSpan.FromMinutes(minutes: (10 + age))), path: path);

        return path;
    }
    private static string[] Remaining(string kind) => [.. Directory.EnumerateDirectories(path: Path.GetTempPath(), searchPattern: $"{kind}*").Order(comparer: StringComparer.Ordinal)];
    private static void Clean(string kind) {
        foreach (var directory in Remaining(kind: kind)) {
            RunDirectory.Delete(path: directory);
        }
    }

    [Fact]
    public void ARunDirectoryIsNamedForItsKindAndTheProcessThatOwnsIt() {
        var kind = UniqueKind(role: "name");

        try {
            var path = RunDirectory.CreatePath(prefix: kind);
            var name = Path.GetFileName(path: path);

            Assert.StartsWith(expectedStartString: $"{kind}{Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture)}-", actualString: name, comparisonType: StringComparison.Ordinal);
            Assert.DoesNotContain(expectedSubstring: "-", actualString: name[((kind.Length + Environment.ProcessId.ToString(provider: CultureInfo.InvariantCulture).Length) + 1)..], comparisonType: StringComparison.Ordinal);
        } finally {
            Clean(kind: kind);
        }
    }
    [Fact]
    public void AKeptFailureTrimsItsKindToTheNewestFinishedDirectories() {
        var kind = UniqueKind(role: "trim");
        var finished = Enumerable.Range(count: (Kept + 3), start: 0).Select(selector: age => Finished(age: age, kind: kind)).ToArray();

        try {
            using var report = new StringWriter();
            var failing = RunDirectory.CreatePath(prefix: kind);

            RunDirectory.Conclude(passed: false, path: failing, report: report);

            // The failing run's own directory belongs to a running process, so it stays beside the newest finished ones.
            Assert.Equal(
                actual: Remaining(kind: kind),
                expected: [.. finished.Take(count: Kept).Append(element: failing).Order(comparer: StringComparer.Ordinal)]
            );
        } finally {
            Clean(kind: kind);
        }
    }
    [Fact]
    public void NoTrimOrSweepRemovesADirectoryWhoseOwnerStillRuns() {
        var kind = UniqueKind(role: "live");
        var live = Enumerable.Range(count: (Kept + 3), start: 0).Select(selector: _ => RunDirectory.CreatePath(prefix: kind)).ToArray();

        try {
            using var report = new StringWriter();

            RunDirectory.Conclude(passed: false, path: live[0], report: report);
            _ = RunDirectory.Sweep(age: TimeSpan.Zero, prefix: kind);

            // However many there are and however old the sweep calls them, this process still owns every one.
            Assert.Equal(actual: Remaining(kind: kind), expected: [.. live.Order(comparer: StringComparer.Ordinal)]);
        } finally {
            Clean(kind: kind);
        }
    }
}
