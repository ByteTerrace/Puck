using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;
using Puck.World;

namespace Puck.Cli.Counters;

/// <summary>
/// The counted-cost ceilings the counters workload is held to (<c>puck.counters.ceilings.v1</c>). Every GPU submission
/// count of a render node, in a pass or outside every pass, that is deterministic or per-backend-deterministic is held
/// under a ceiling: <see cref="Record"/> takes each count's reading as its ceiling, so a count that read zero is a required
/// zero, and <see cref="Check"/> fails a count over its ceiling, a required zero broken and a count no ceiling was recorded
/// for, naming its backend, kind, pass and node. A per-backend-deterministic count is judged only on the device its
/// backend's ceilings were recorded on; elsewhere it is reported as not judged.
/// </summary>
internal static class CountersCeilings {
    /// <summary>The ceilings the workload is held to, repository-relative.</summary>
    public const string CeilingsPath = "tests/Puck.Counters/counters.ceilings.json";

    private const string Outside = "outside";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly HashSet<string> SubmissionKinds = [.. GpuWork.SubmissionKinds.ToArray().Select(selector: static kind => kind.Name)];

    /// <summary>What a check found: the counts that failed their ceilings, and the notes about counts it did not judge.</summary>
    /// <param name="Failures">One line per failure, naming the backend, class, kind, pass and node.</param>
    /// <param name="Notes">One line per backend some of whose counts were not judged, and why.</param>
    public sealed record Verdict(IReadOnlyList<string> Failures, IReadOnlyList<string> Notes);

    /// <summary>Indicates whether a count is held under a ceiling: a GPU submission kind of a render node, in a pass or
    /// outside every pass, whose class two runs are held to.</summary>
    /// <param name="count">The count.</param>
    /// <returns><see langword="true"/> when a ceiling holds the count.</returns>
    public static bool IsCeiled(WorldCount count) => (
        string.Equals(a: count.Source, b: GpuWorkReport.Section, comparisonType: StringComparison.Ordinal) &&
        (count.Node is not null) &&
        SubmissionKinds.Contains(item: count.Kind) &&
        (count.Class is WorkClass.Deterministic or WorkClass.PerBackendDeterministic)
    );
    /// <summary>Records a report's counts as ceilings: each backend's run on its device, every ceiled count's reading its
    /// ceiling, and for every pass that did not execute a required zero for each submission kind, which the pass reads
    /// once it does.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The ceilings.</returns>
    public static WorldCountersCeilings Record(WorldCountersReport report) => new(
        Runs: [.. report.Runs.Select(selector: static run => new WorldCountersCeilingRun(
            Backend: run.Backend,
            Ceilings: [
                .. run.Counts.Where(predicate: IsCeiled).Select(selector: static count => new WorldCountCeiling(
                    Detail: count.Detail,
                    Ceiling: count.Value,
                    Class: count.Class,
                    Kind: count.Kind,
                    Node: count.Node!,
                    Pass: count.Pass
                )),
                .. run.Passes.Where(predicate: static pass => (pass.State != GpuPassState.Executed)).SelectMany(selector: static pass => GpuWork.SubmissionKinds.ToArray().Select(selector: kind => new WorldCountCeiling(
                    Detail: pass.Detail,
                    Ceiling: 0L,
                    Class: CountersReading.ClassIn(kind: kind.Class, pass: pass.Class),
                    Kind: kind.Name,
                    Node: pass.Node,
                    Pass: pass.Label
                ))),
            ],
            Device: run.Device,
            Height: run.Height,
            Width: run.Width
        ))],
        Script: report.Script,
        Workload: report.Workload
    );
    /// <summary>Holds a report to ceilings, backend by backend. Every recorded ceiling must have been measured: its count
    /// read, of the class it was recorded as, or its pass present and not executed, which reads zero; and every ceiled
    /// count read must have a ceiling. Only then is each value compared, a per-backend-deterministic one only on the device
    /// the backend's ceilings were recorded on.</summary>
    /// <param name="report">The report.</param>
    /// <param name="ceilings">The ceilings.</param>
    /// <returns>The failures and the notes; no failure when every expectation was measured and every judged count is
    /// within its ceiling.</returns>
    public static Verdict Check(WorldCountersReport report, WorldCountersCeilings ceilings) {
        var failures = new List<string>();
        var notes = new List<string>();

        if (
            !string.Equals(a: report.Workload, b: ceilings.Workload, comparisonType: StringComparison.Ordinal) ||
            !string.Equals(a: report.Script, b: ceilings.Script, comparisonType: StringComparison.Ordinal)
        ) {
            failures.Add(item: $"the ceilings hold {ceilings.Workload} run by {ceilings.Script}, not {report.Workload} run by {report.Script}");

            return new Verdict(
                Failures: failures,
                Notes: notes
            );
        }

        foreach (var recorded in ceilings.Runs) {
            if (!report.Runs.Any(predicate: run => string.Equals(a: run.Backend, b: recorded.Backend, comparisonType: StringComparison.Ordinal))) {
                failures.Add(item: $"{recorded.Backend}: the ceilings were recorded for the backend, which the run did not measure");
            }
        }

        foreach (var run in report.Runs) {
            var recorded = ceilings.Runs.FirstOrDefault(predicate: candidate => string.Equals(a: candidate.Backend, b: run.Backend, comparisonType: StringComparison.Ordinal));

            if (recorded is null) {
                failures.Add(item: $"{run.Backend}: no ceilings were recorded for the backend");

                continue;
            }
            if ((recorded.Width, recorded.Height) != (run.Width, run.Height)) {
                failures.Add(item: $"{run.Backend}: the ceilings were recorded at {recorded.Width}x{recorded.Height}, the run is {run.Width}x{run.Height}");

                continue;
            }

            var judged = (recorded.Device == run.Device);
            var measured = new Dictionary<(string Node, string? Pass, string Kind, string? Detail), WorldCount>();
            var passes = run.Passes.ToDictionary(
                elementSelector: static pass => pass.State,
                keySelector: static pass => (pass.Node, pass.Label, pass.Detail)
            );
            var unjudged = 0;

            foreach (var count in run.Counts.Where(predicate: static count => (
                string.Equals(a: count.Source, b: GpuWorkReport.Section, comparisonType: StringComparison.Ordinal) &&
                (count.Node is not null) &&
                SubmissionKinds.Contains(item: count.Kind)
            ))) {
                measured[(count.Node!, count.Pass, count.Kind, count.Detail)] = count;
            }

            foreach (var ceiling in recorded.Ceilings) {
                var where = $"{run.Backend}: {EnumWireName<WorkClass>.Of(value: ceiling.Class)} kind={ceiling.Kind} pass={(ceiling.Pass ?? Outside)}{CountersComparison.DetailText(detail: ceiling.Detail)} node={ceiling.Node}";

                if (!measured.Remove(
                    key: (ceiling.Node, ceiling.Pass, ceiling.Kind, ceiling.Detail),
                    value: out var count
                )) {
                    // A pass that did not execute reads zero, which any ceiling holds.
                    if (
                        (ceiling.Pass is null) ||
                        !passes.TryGetValue(key: (ceiling.Node, ceiling.Pass, ceiling.Detail), value: out var state) ||
                        (state == GpuPassState.Executed)
                    ) {
                        failures.Add(item: $"{where} was recorded but not measured");
                    }

                    continue;
                }
                if (count.Class != ceiling.Class) {
                    failures.Add(item: $"{where} was measured as {EnumWireName<WorkClass>.Of(value: count.Class)}");

                    continue;
                }
                if ((ceiling.Class == WorkClass.PerBackendDeterministic) && !judged) {
                    unjudged++;

                    continue;
                }
                if ((ceiling.Ceiling == 0L) && (count.Value != 0L)) {
                    failures.Add(item: $"{where} breaks its required zero: reads {Spell(value: count.Value)}");
                } else if (count.Value > ceiling.Ceiling) {
                    failures.Add(item: $"{where} is over its ceiling: reads {Spell(value: count.Value)}, ceiling {Spell(value: ceiling.Ceiling)}");
                }
            }

            foreach (var count in measured.Values.Where(predicate: IsCeiled)) {
                failures.Add(item: $"{run.Backend}: {EnumWireName<WorkClass>.Of(value: count.Class)} kind={count.Kind} pass={(count.Pass ?? Outside)}{CountersComparison.DetailText(detail: count.Detail)} node={count.Node} reads {Spell(value: count.Value)} with no ceiling recorded");
            }

            if (unjudged > 0) {
                notes.Add(item: $"{run.Backend}: {unjudged} per-backend-deterministic count(s) not judged: the ceilings were recorded on {Describe(device: recorded.Device)}, this run's device is {Describe(device: run.Device)}");
            }
        }

        return new Verdict(
            Failures: failures,
            Notes: notes
        );
    }
    /// <summary>Reads a ceilings document, refusing one that is not a well-formed <c>puck.counters.ceilings.v1</c>
    /// document.</summary>
    /// <param name="path">The document's path.</param>
    /// <param name="ceilings">The ceilings, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the file is not a ceilings document, or empty.</param>
    /// <returns><see langword="true"/> when the file is a ceilings document.</returns>
    public static bool TryRead(string path, [NotNullWhen(returnValue: true)] out WorldCountersCeilings? ceilings, out string reason) {
        ceilings = null;

        try {
            ceilings = JsonSerializer.Deserialize(
                jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings,
                utf8Json: File.ReadAllBytes(path: path)
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            reason = $"unreadable: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        } catch (JsonException exception) {
            reason = $"not a {WorldCountersCeilings.SchemaVersion} document: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (ceilings is null) {
            reason = $"not a {WorldCountersCeilings.SchemaVersion} document: the document is null";

            return false;
        }
        if (!string.Equals(
            a: ceilings.Schema,
            b: WorldCountersCeilings.SchemaVersion,
            comparisonType: StringComparison.Ordinal
        )) {
            reason = $"a foreign document: its schema is '{ceilings.Schema}', not '{WorldCountersCeilings.SchemaVersion}'";
            ceilings = null;

            return false;
        }

        reason = string.Empty;

        return true;
    }
    /// <summary>Writes a ceilings document as indented UTF-8 JSON ending in one line feed.</summary>
    /// <param name="path">The file to write.</param>
    /// <param name="ceilings">The ceilings.</param>
    public static void Write(string path, WorldCountersCeilings ceilings) {
        var directory = Path.GetDirectoryName(path: path);

        if (directory is { Length: > 0 }) {
            _ = Directory.CreateDirectory(path: directory);
        }

        File.WriteAllText(
            contents: $"{JsonSerializer.Serialize(value: ceilings, jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings)}\n",
            encoding: Utf8,
            path: path
        );
    }

    private static string Describe(GpuDeviceIdentity device) =>
        $"{device.AdapterName} (driver {device.DriverVersion})";
    private static string Spell(long value) =>
        value.ToString(provider: CultureInfo.InvariantCulture);
}
