using System.Globalization;
using System.Text.Json;

namespace Puck.Bench;

/// <summary>
/// Writes the <c>puck.bench.v1</c> JSON report file (plan §8) for one completed, scored run/leg — the second half of
/// the everything-as-data output contract (the first half is <see cref="BenchConsoleFormatter"/>'s stdout tables).
/// Always-on: <see cref="BenchRuntime"/> wires this to <see cref="BenchRuntime.RunCompleted"/> at construction, so a
/// report lands for every clean run with no opt-in. A refused/aborted outcome (<see cref="BenchRunOutcome.Succeeded"/>
/// <see langword="false"/>) writes nothing — the honest-refusal rule (§4 rule 4) means there is nothing to report.
/// </summary>
public static class BenchReportWriter {
    /// <summary>The report directory, created on demand under the process's current working directory.</summary>
    public const string ReportDirectoryName = "bench-reports";

    /// <summary>Writes the report file for a completed run/leg. A no-op (returns <see langword="null"/>) for a
    /// refused/aborted outcome — nothing is reported, per the honest-refusal rule.</summary>
    /// <param name="outcome">The completed run/leg outcome.</param>
    /// <param name="host">The host facts to stamp into the report.</param>
    /// <param name="featureSwitches">Every registered switch's value at completion time (empty when no registry is
    /// attached).</param>
    /// <returns>The report file's path (relative to the current directory), or <see langword="null"/> when nothing
    /// was written.</returns>
    public static string? Write(BenchRunOutcome outcome, BenchHostInfo host, IReadOnlyDictionary<string, string> featureSwitches) {
        ArgumentNullException.ThrowIfNull(argument: outcome);
        ArgumentNullException.ThrowIfNull(argument: host);
        ArgumentNullException.ThrowIfNull(argument: featureSwitches);

        if (!outcome.Succeeded) {
            return null;
        }

        var document = BenchReportDocument.FromOutcome(featureSwitches: featureSwitches, host: host, outcome: outcome);
        var json = JsonSerializer.Serialize(value: document, jsonTypeInfo: BenchReportJsonContext.Default.BenchReportDocument);

        Directory.CreateDirectory(path: ReportDirectoryName);

        var path = Path.Combine(path1: ReportDirectoryName, path2: BuildFileName(outcome: outcome));

        File.WriteAllText(path: path, contents: json);

        return path;
    }

    // The local-time filename (the payload's own startedAtUtc carries UTC, per plan §8): a sweep leg gets its swept
    // value appended so consecutive legs (which can complete within the same second) never collide or overwrite one
    // another.
    private static string BuildFileName(BenchRunOutcome outcome) {
        var timestamp = DateTime.Now.ToString(format: "yyyyMMdd-HHmmss", provider: CultureInfo.InvariantCulture);
        var suffix = (((outcome.SwitchName is { } name) && (outcome.SwitchValue is { } value))
            ? $"-{Sanitize(token: name)}-{Sanitize(token: value)}"
            : string.Empty);

        return $"puck-bench-{timestamp}{suffix}.json";
    }

    // Filenames must not carry a switch's dotted/comma vocabulary verbatim.
    private static string Sanitize(string token) {
        Span<char> buffer = stackalloc char[token.Length];

        for (var index = 0; (index < token.Length); index++) {
            var ch = token[index];

            buffer[index] = (char.IsLetterOrDigit(c: ch) ? ch : '_');
        }

        return new string(value: buffer);
    }
}
