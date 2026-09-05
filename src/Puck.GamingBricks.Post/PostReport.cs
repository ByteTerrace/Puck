using System.Globalization;
using System.Text;

namespace Puck.GamingBricks.Post;

/// <summary>The aggregate outcome of a battery run: the per-stage rows, the folded exit code, and the three files a
/// run leaves behind under its artifacts directory — a human-readable table (<c>post-report.txt</c>), a machine-readable
/// summary (<c>summary.json</c>), and a JUnit report naming every case (<c>results.junit.xml</c>).</summary>
public sealed class PostReport {
    /// <summary>Initializes a new instance of the <see cref="PostReport"/> class, folding the per-stage verdicts into an
    /// exit code (any infra → 2, else any fail → 1, else 0; skips are neutral).</summary>
    /// <param name="banner">The table's first line — the caller's own battery/machine identification.</param>
    /// <param name="results">The per-stage results, in run order.</param>
    /// <param name="duration">The whole run's wall-clock time.</param>
    /// <exception cref="ArgumentException"><paramref name="banner"/> is null or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="results"/> is <see langword="null"/>.</exception>
    public PostReport(string banner, IReadOnlyList<PostStageResult> results, TimeSpan duration) {
        ArgumentException.ThrowIfNullOrEmpty(argument: banner);
        ArgumentNullException.ThrowIfNull(argument: results);

        Banner = banner;
        Duration = duration;
        Results = results;

        var hasInfra = false;
        var hasFail = false;

        foreach (var result in results) {
            if (result.Outcome.Verdict == PostVerdict.Infra) {
                hasInfra = true;
            } else if (result.Outcome.Verdict == PostVerdict.Fail) {
                hasFail = true;
            }
        }

        ExitCode = (hasInfra
            ? 2
            : (hasFail
                ? 1
                : 0));
    }

    /// <summary>The table's first line — the caller's own battery/machine identification.</summary>
    public string Banner { get; }
    /// <summary>The whole run's wall-clock time.</summary>
    public TimeSpan Duration { get; }
    /// <summary>The process exit code folded from the per-stage verdicts.</summary>
    public int ExitCode { get; }
    /// <summary>The per-stage results, in run order.</summary>
    public IReadOnlyList<PostStageResult> Results { get; }

    /// <summary>Renders a duration for the console and the table: seconds to one decimal, right-aligned.</summary>
    /// <param name="duration">The duration to render.</param>
    /// <returns>The rendered text.</returns>
    public static string FormatDuration(TimeSpan duration) =>
        string.Create(
        provider: CultureInfo.InvariantCulture,
        initialBuffer: stackalloc char[16],
        $"{duration.TotalSeconds,7:0.0}s"
    );
    /// <summary>Renders the report as a fixed-width table.</summary>
    /// <returns>The table text.</returns>
    public string Render() {
        var builder = new StringBuilder();

        _ = builder.AppendLine(value: Banner);
        _ = builder.AppendLine(value: "================================================================================");

        if (Results.Count == 0) {
            _ = builder.AppendLine(value: "(no stages ran)");
        } else {
            var nameWidth = Results.Max(selector: static result => result.Name.Length);

            foreach (var result in Results) {
                _ = builder.AppendLine(value: $"[{result.Tier}] {VerdictToken(verdict: result.Outcome.Verdict)} {result.Name.PadRight(totalWidth: nameWidth)} {FormatDuration(duration: result.Duration)}  {result.Outcome.Detail}");
            }
        }

        _ = builder.AppendLine(value: "--------------------------------------------------------------------------------");
        _ = builder.AppendLine(value: $"{Summarize()} - {FormatDuration(duration: Duration).Trim()} - exit {ExitCode}");

        return builder.ToString();
    }
    /// <summary>Writes the report table to <c>post-report.txt</c>, the summary to <c>summary.json</c>, and the per-case
    /// rows to <c>results.junit.xml</c> under the artifacts directory, and echoes the table to the console.</summary>
    /// <param name="artifactsDirectory">The directory to write into (created if absent).</param>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is null or empty.</exception>
    public void Write(string artifactsDirectory) {
        ArgumentException.ThrowIfNullOrEmpty(argument: artifactsDirectory);

        _ = Directory.CreateDirectory(path: artifactsDirectory);

        var table = Render();

        File.WriteAllText(
            path: Path.Combine(
                path1: artifactsDirectory,
                path2: "post-report.txt"
            ),
            contents: table
        );
        PostReportFiles.WriteSummary(
            path: Path.Combine(
                path1: artifactsDirectory,
                path2: "summary.json"
            ),
            report: this
        );
        PostReportFiles.WriteJUnit(
            path: Path.Combine(
                path1: artifactsDirectory,
                path2: "results.junit.xml"
            ),
            report: this
        );
        Console.Out.Write(value: table);
    }

    private static string VerdictToken(PostVerdict verdict) {
        return verdict switch {
            PostVerdict.Pass => "PASS ",
            PostVerdict.Skip => "SKIP ",
            PostVerdict.Fail => "FAIL ",
            PostVerdict.Infra => "INFRA",
            _ => "?????",
        };
    }
    private string Summarize() {
        var pass = Results.Count(predicate: static result => (result.Outcome.Verdict == PostVerdict.Pass));
        var skip = Results.Count(predicate: static result => (result.Outcome.Verdict == PostVerdict.Skip));
        var fail = Results.Count(predicate: static result => (result.Outcome.Verdict == PostVerdict.Fail));
        var infra = Results.Count(predicate: static result => (result.Outcome.Verdict == PostVerdict.Infra));

        return $"{Results.Count} stage(s): {pass} pass, {fail} fail, {infra} infra, {skip} skip";
    }
}
