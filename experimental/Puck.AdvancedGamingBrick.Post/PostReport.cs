using System.Text;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>The aggregate outcome of a battery run: the per-stage rows, the folded exit code, and a human-readable table
/// written to the artifacts directory and echoed to the console.</summary>
internal sealed class PostReport {
    /// <summary>Initializes a new instance of the <see cref="PostReport"/> class, folding the per-stage verdicts into an
    /// exit code (any infra → 2, else any fail → 1, else 0; skips are neutral).</summary>
    /// <param name="results">The per-stage results, in run order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="results"/> is <see langword="null"/>.</exception>
    public PostReport(IReadOnlyList<PostStageResult> results) {
        ArgumentNullException.ThrowIfNull(argument: results);

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

        ExitCode = (hasInfra ? 2 : (hasFail ? 1 : 0));
    }

    /// <summary>The process exit code folded from the per-stage verdicts.</summary>
    public int ExitCode { get; }

    /// <summary>The per-stage results, in run order.</summary>
    public IReadOnlyList<PostStageResult> Results { get; }

    /// <summary>Renders the report as a fixed-width table.</summary>
    /// <returns>The table text.</returns>
    public string Render() {
        var builder = new StringBuilder();

        _ = builder.AppendLine(value: "Puck.AdvancedGamingBrick.Post - Game Boy Advance machine power-on self-test");
        _ = builder.AppendLine(value: "================================================================================");

        if (Results.Count == 0) {
            _ = builder.AppendLine(value: "(no stages ran)");
        } else {
            var nameWidth = Results.Max(selector: static result => result.Name.Length);

            foreach (var result in Results) {
                _ = builder.AppendLine(value: $"[{result.Tier}] {VerdictToken(verdict: result.Outcome.Verdict)} {result.Name.PadRight(totalWidth: nameWidth)}  {result.Outcome.Detail}");
            }
        }

        _ = builder.AppendLine(value: "--------------------------------------------------------------------------------");
        _ = builder.AppendLine(value: $"{Summarize()} - exit {ExitCode}");

        return builder.ToString();
    }

    /// <summary>Writes the report table to <c>post-report.txt</c> under the artifacts directory and echoes it to the
    /// console.</summary>
    /// <param name="artifactsDirectory">The directory to write the report into (created if absent).</param>
    /// <exception cref="ArgumentException"><paramref name="artifactsDirectory"/> is null or empty.</exception>
    public void Write(string artifactsDirectory) {
        ArgumentException.ThrowIfNullOrEmpty(argument: artifactsDirectory);

        _ = Directory.CreateDirectory(path: artifactsDirectory);

        var table = Render();

        File.WriteAllText(path: Path.Combine(path1: artifactsDirectory, path2: "post-report.txt"), contents: table);
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
        var pass = Results.Count(predicate: static result => result.Outcome.Verdict == PostVerdict.Pass);
        var skip = Results.Count(predicate: static result => result.Outcome.Verdict == PostVerdict.Skip);
        var fail = Results.Count(predicate: static result => result.Outcome.Verdict == PostVerdict.Fail);
        var infra = Results.Count(predicate: static result => result.Outcome.Verdict == PostVerdict.Infra);

        return $"{Results.Count} stage(s): {pass} pass, {fail} fail, {infra} infra, {skip} skip";
    }
}
