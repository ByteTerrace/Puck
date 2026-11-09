using System.Globalization;
using System.Text.RegularExpressions;

using Puck.Abstractions;

namespace Puck.Cli.Sweep;

/// <summary>The mesh audit of the prototypes a stop's captures frame (<c>puck mesh audit --cpu</c>): each prototype's
/// surface groups as metrics the tour's audit rules read (<see cref="SweepTour.Audit"/>), from the audit's own report.</summary>
public static partial class SweepMeshAudit {
    /// <summary>Gets the metric names a prototype's reading carries: the worst group's <c>score</c>, and its groups'
    /// summed <c>folds</c>, <c>flipped</c> triangles, <c>slivers</c>, <c>open</c> and <c>nonmanifold</c> edges and
    /// <c>uncovered</c> crossings.</summary>
    public static IReadOnlyList<string> Metrics { get; } = ["score", "folds", "flipped", "slivers", "open", "nonmanifold", "uncovered"];

    /// <summary>Audits prototypes in one run of this CLI's <c>puck mesh audit --cpu</c>, which bakes each group offline
    /// through the bake's own lattices and measures the mesh against its field.</summary>
    /// <param name="world">The world document or package.</param>
    /// <param name="prototypes">The prototypes.</param>
    /// <param name="repositoryRoot">The working directory.</param>
    /// <param name="budget">The sweep's budget, which bounds the run.</param>
    /// <param name="transcript">The file the audit's whole output is written to.</param>
    /// <returns>Each prototype's metrics (<see cref="Metrics"/>); a prototype the audit reported no surface group for
    /// is absent.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Run(string world, IReadOnlyCollection<string> prototypes, string repositoryRoot, SuiteBudget budget, string transcript) {
        var readings = new Dictionary<string, Dictionary<string, double>>(comparer: StringComparer.Ordinal);

        if (prototypes.Count > 0) {
            string[] arguments = [SweepCommand.CliEntry, "mesh", "audit", "--world", PuckPaths.Normalize(path: world), "--cpu", "--top", "1", "--worst", "0", "--prototype", .. prototypes];
            string output;

            try {
                var run = CliProcess.RunCaptured(
                    arguments: arguments,
                    cancellationToken: budget.Token,
                    fileName: "dotnet",
                    input: string.Empty,
                    started: static process => ChildProcessJob.Assign(process: process),
                    timeout: Timeout.InfiniteTimeSpan,
                    workingDirectory: repositoryRoot
                );

                output = (run.Stdout + run.Stderr);
            } catch (Exception exception) when ((exception is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)) {
                output = $"puck mesh audit did not run: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
            }

            File.WriteAllText(contents: output, path: transcript);
            Read(output: output, readings: readings);
        }

        return readings.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static entry => (IReadOnlyDictionary<string, string>)entry.Value.ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: static metric => metric.Value.ToString(provider: CultureInfo.InvariantCulture),
                keySelector: static metric => metric.Key
            ),
            keySelector: static entry => entry.Key
        );
    }

    // Each group line names its prototype and score; the topology line that follows it carries its defect counts.
    private static void Read(string output, Dictionary<string, Dictionary<string, double>> readings) {
        Dictionary<string, double>? current = null;

        foreach (var line in output.Split(separator: '\n')) {
            if (GroupPattern().Match(input: line) is { Success: true } group) {
                var prototype = group.Groups["prototype"].Value;

                if (!readings.TryGetValue(key: prototype, value: out current)) {
                    current = Metrics.ToDictionary(comparer: StringComparer.Ordinal, elementSelector: static _ => 0d, keySelector: static name => name);
                    readings[prototype] = current;
                }

                current["score"] = Math.Max(val1: current["score"], val2: double.Parse(provider: CultureInfo.InvariantCulture, s: group.Groups["score"].Value));

                continue;
            }
            if ((current is not null) && (TopologyPattern().Match(input: line) is { Success: true } topology)) {
                foreach (var name in Metrics.Skip(count: 1)) {
                    current[name] += double.Parse(provider: CultureInfo.InvariantCulture, s: topology.Groups[name].Value);
                }

                current = null;
            }
        }
    }

    [GeneratedRegex(pattern: @"^group \d+ instance \S+ .+? \((?<prototype>[^)]+)\): \d+ triangles, .*score (?<score>\d+(?:\.\d+)?)", options: RegexOptions.CultureInvariant)]
    private static partial Regex GroupPattern();
    [GeneratedRegex(pattern: @"topology: (?<folds>\d+) folds, (?<flipped>\d+) flipped, (?<slivers>\d+) slivers, (?<open>\d+) open edges, (?<nonmanifold>\d+) non-manifold edges; surface: \d+ crossings, (?<uncovered>\d+) uncovered", options: RegexOptions.CultureInvariant)]
    private static partial Regex TopologyPattern();
}
