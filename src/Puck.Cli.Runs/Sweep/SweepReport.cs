using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Cli.Sweep;

/// <summary>The colours a sweep gives a signal, a tool or a still, worst last. Silence is never success: whatever the
/// sweep could not measure is <see cref="Unknown"/> or <see cref="Red"/>, and both fail the run.</summary>
public static class SweepStatus {
    /// <summary>Nothing a rule names holds.</summary>
    public const string Clean = "clean";
    /// <summary>Nothing was asked of it: the scenarios when no file was named.</summary>
    public const string Skipped = "skipped";
    /// <summary>An amber rule holds, or a still changed beyond the tolerance.</summary>
    public const string Amber = "amber";
    /// <summary>The sweep could not measure it: the read did not happen, the World's attachment closed, the answer
    /// held no metric a rule reads, or a tool did not finish. It fails the run.</summary>
    public const string Unknown = "unmeasured";
    /// <summary>A red rule holds, a line was refused, or a capture failed.</summary>
    public const string Red = "red";

    /// <summary>Returns the worse of two colours.</summary>
    /// <param name="left">The first colour.</param>
    /// <param name="right">The second colour.</param>
    /// <returns>The worse colour.</returns>
    public static string Worse(string left, string right) => ((Rank(status: left) >= Rank(status: right)) ? left : right);
    /// <summary>Returns a colour's place in clean, amber, unknown, red.</summary>
    /// <param name="status">The colour.</param>
    /// <returns>Zero for clean up to three for red.</returns>
    public static int Rank(string status) => status switch {
        Red => 3,
        Unknown => 2,
        Amber => 1,
        _ => 0,
    };
    /// <summary>Returns whether a colour fails the run: red, or whatever the sweep could not measure.</summary>
    /// <param name="status">The colour.</param>
    /// <returns><see langword="true"/> for red and unknown.</returns>
    public static bool Fails(string status) => (Rank(status: status) >= Rank(status: Unknown));
}
/// <summary>One World a sweep drove.</summary>
/// <param name="Leg">The leg.</param>
/// <param name="ProcessId">The World's process id.</param>
/// <param name="Booted">Whether the sweep booted it.</param>
/// <param name="Logs">The directory holding its transcripts, when the sweep booted it.</param>
/// <param name="Notes">What the leg noticed outside any signal.</param>
public sealed record SweepLegResult(string Leg, int ProcessId, bool Booted, string? Logs, IReadOnlyList<string> Notes);
/// <summary>One tool's run.</summary>
/// <param name="Name">The tool.</param>
/// <param name="Status">Its colour.</param>
/// <param name="Ticks">The ticks it was given.</param>
/// <param name="Reasons">Why it is not clean.</param>
public sealed record SweepToolResult(string Name, string Status, int Ticks, IReadOnlyList<string> Reasons);
/// <summary>One signal's reading.</summary>
/// <param name="Name">The signal.</param>
/// <param name="Leg">The leg it was read on.</param>
/// <param name="Line">The line it read.</param>
/// <param name="Status">Its colour by its own rules and refusals alone, never the baseline comparison's: a baseline
/// reads this colour, so a report recorded against one carries no comparison red into the next.</param>
/// <param name="Reasons">The rules that hold and the refusals.</param>
/// <param name="Metrics">The metrics, by name.</param>
/// <param name="File">The answer's file, relative to the report.</param>
/// <param name="Changes">What changed against the baseline.</param>
/// <param name="Comparison">The baseline comparison's colour: red when <paramref name="Changes"/> names anything,
/// clean when it names nothing, and <see langword="null"/> when the sweep had no baseline.</param>
public sealed record SweepSignalResult(string Name, string Leg, string Line, string Status, IReadOnlyList<string> Reasons, IReadOnlyDictionary<string, string> Metrics, string File, IReadOnlyList<string> Changes, string? Comparison = null) {
    /// <summary>Gets the signal's colour in this run: the worse of its rules' colour and the comparison's.</summary>
    [JsonIgnore]
    public string Colour => SweepStatus.Worse(left: Status, right: (Comparison ?? SweepStatus.Clean));
}
/// <summary>One tour still.</summary>
/// <param name="Name">The stop.</param>
/// <param name="Group">Its contact sheet.</param>
/// <param name="Note">What it shows.</param>
/// <param name="File">The still, relative to the report, or <see langword="null"/> when no capture was taken.</param>
/// <param name="Capture">The capture's answer: its tick, state hash and pose.</param>
/// <param name="Status">Its colour.</param>
/// <param name="Reasons">Why it is not clean.</param>
/// <param name="ChangedFraction">The fraction of its pixels changed against the baseline beyond the level tolerance,
/// or <see langword="null"/> without a comparable baseline still.</param>
/// <param name="Motion">The held body's authoritative pose and presentation rig at capture, when measured.</param>
/// <param name="Exposure">The first view's exposure at capture (<c>ev100=… meter=… source=…</c>), when measured: a whole-image
/// brightness difference against a baseline still is this value differing, not the scene.</param>
/// <param name="Exhibit">The exhibit a capture inside a stop's timeline frames, or <see langword="null"/> for a stop's own still.</param>
/// <param name="Text">The text the exhibit's creation carries as authored (a plinth's label and plaque): what the
/// still should show and what to look for, for a reviewer to set beside the pixels.</param>
/// <param name="Audit">The mesh audit's reading of the exhibit's prototype: its score and topology defects.</param>
/// <param name="Frames">The <c>host.frames</c> reading over a route stop's timeline, reported and never judged: lockstep
/// and settled captures hold frames, so it is no frame-time measurement.</param>
public sealed record SweepStillResult(string Name, string Group, string? Note, string? File, string Capture, string Status, IReadOnlyList<string> Reasons, double? ChangedFraction, string? Motion = null, string? Exposure = null, string? Exhibit = null, IReadOnlyList<string>? Text = null, string? Audit = null, IReadOnlyDictionary<string, string>? Frames = null);
/// <summary>The tour's stills and sheets.</summary>
/// <param name="LevelTolerance">The channel difference beyond which a pixel counts as changed.</param>
/// <param name="AmberFraction">The changed fraction beyond which a still is amber.</param>
/// <param name="Sheets">Each group's contact sheet, relative to the report; a <c>compare-</c> sheet sets the
/// baseline's still above the current one.</param>
/// <param name="Stills">The stills in tour order.</param>
public sealed record SweepTourResult(int LevelTolerance, double AmberFraction, IReadOnlyDictionary<string, string> Sheets, IReadOnlyList<SweepStillResult> Stills);
/// <summary>The scenario verdicts.</summary>
/// <param name="Status">Their colour.</param>
/// <param name="Command">The command that runs them.</param>
/// <param name="Reasons">Each failing or unrun world.</param>
/// <param name="Worlds">Each test world with its passed and total verdicts.</param>
public sealed record SweepScenarioResult(string Status, string Command, IReadOnlyList<string> Reasons, IReadOnlyDictionary<string, string> Worlds);
/// <summary>A sweep's report: <c>report.json</c> beside the answers, stills and sheets it names.</summary>
/// <param name="Schema">The report's format, <see cref="SweepReport.SchemaName"/>.</param>
/// <param name="Commit">The commit the sweep ran on.</param>
/// <param name="World">The world it swept.</param>
/// <param name="Started">When it started, UTC.</param>
/// <param name="Seconds">How long it took.</param>
/// <param name="Baseline">The report it was compared with, or <see langword="null"/>.</param>
/// <param name="Legs">The Worlds it drove.</param>
/// <param name="Tools">The tools it ran.</param>
/// <param name="Signals">The signals it read.</param>
/// <param name="Scenarios">The scenario verdicts.</param>
/// <param name="Tour">The tour, or <see langword="null"/> when no windowed leg ran.</param>
public sealed record SweepReport(
    string Schema,
    string Commit,
    string World,
    DateTimeOffset Started,
    double Seconds,
    string? Baseline,
    IReadOnlyList<SweepLegResult> Legs,
    IReadOnlyList<SweepToolResult> Tools,
    IReadOnlyList<SweepSignalResult> Signals,
    SweepScenarioResult Scenarios,
    SweepTourResult? Tour
) {
    /// <summary>The report format's name.</summary>
    public const string SchemaName = "puck.sweep.v1";
    /// <summary>The report's file name in the output directory.</summary>
    public const string FileName = "report.json";

    private static readonly JsonSerializerOptions Options = new() {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>Gets the worst colour of the report.</summary>
    [JsonIgnore]
    public string Status => Everything().Aggregate(func: SweepStatus.Worse, seed: SweepStatus.Clean);
    /// <summary>Gets whether anything is red or could not be measured, which fails the run.</summary>
    [JsonIgnore]
    public bool Failed => SweepStatus.Fails(status: Status);

    /// <summary>Writes the report.</summary>
    /// <param name="path">The file.</param>
    public void Write(string path) => File.WriteAllText(contents: JsonSerializer.Serialize(options: Options, value: this), path: path);
    /// <summary>Formats the summary a terminal prints: every signal, tool, still and the scenarios with their colour.</summary>
    /// <returns>The summary.</returns>
    public string Summary() {
        var text = new StringBuilder();
        var counts = Everything().GroupBy(keySelector: static status => status).ToDictionary(elementSelector: static group => group.Count(), keySelector: static group => group.Key);

        _ = text.AppendLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"sweep {Status}: {counts.GetValueOrDefault(key: SweepStatus.Red)} red, {counts.GetValueOrDefault(key: SweepStatus.Amber)} amber, {counts.GetValueOrDefault(key: SweepStatus.Unknown)} unmeasured, {counts.GetValueOrDefault(key: SweepStatus.Clean)} clean in {Seconds:0} s at {Commit}{((Baseline is null) ? string.Empty : $", against {Baseline}")}"));

        foreach (var tool in Tools) {
            _ = text.AppendLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  {tool.Status,-10} tool {tool.Name} ({tool.Ticks} ticks){Join(items: tool.Reasons)}"));
        }
        foreach (var signal in Signals) {
            _ = text.AppendLine(value: $"  {signal.Colour,-10} {signal.Leg} {signal.Name}{Join(items: signal.Reasons)}{((signal.Changes.Count == 0) ? string.Empty : $" | changed: {string.Join(separator: "; ", values: signal.Changes)}")}");
        }

        _ = text.AppendLine(value: $"  {Scenarios.Status,-10} scenarios{Join(items: Scenarios.Reasons)}");

        if (Tour is { } tour) {
            foreach (var still in tour.Stills) {
                _ = text.AppendLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  {still.Status,-10} still {still.Name}{((still.ChangedFraction is { } fraction) ? $" ({fraction:P2} changed)" : string.Empty)}{Join(items: still.Reasons)}"));
            }
            foreach (var (group, sheet) in tour.Sheets) {
                _ = text.AppendLine(value: $"  sheet {group}: {sheet}");
            }
        }

        return text.ToString();
    }

    private IEnumerable<string> Everything() => (((Tools.Count == 0) && (Signals.Count == 0) && ((Tour?.Stills.Count ?? 0) == 0) && (Scenarios.Status == SweepStatus.Skipped)) ? new[] { SweepStatus.Unknown } : [])
        .Concat(second: Tools.Select(selector: static tool => tool.Status))
        .Concat(second: Signals.Select(selector: static signal => signal.Colour))
        .Concat(second: [Scenarios.Status])
        .Concat(second: (Tour?.Stills.Select(selector: static still => still.Status) ?? []));
    private static string Join(IReadOnlyList<string> items) => ((items.Count == 0) ? string.Empty : $": {string.Join(separator: "; ", values: items)}");
}
/// <summary>A previous report a sweep compares with: its signals' metrics and its stills' files.</summary>
public sealed class SweepBaseline {
    private readonly Dictionary<string, Dictionary<string, string>> m_metrics = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, string> m_status = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, string> m_stills = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, string> m_exposures = new(comparer: StringComparer.Ordinal);

    private SweepBaseline(string path) {
        Path = path;
    }

    /// <summary>Gets the baseline report's path.</summary>
    public string Path { get; }

    /// <summary>Reads a previous report.</summary>
    /// <param name="path">The report file.</param>
    /// <param name="baseline">The baseline.</param>
    /// <param name="error">Why the file is not a sweep report.</param>
    /// <returns><see langword="true"/> when the file is a sweep report.</returns>
    public static bool TryRead(string path, out SweepBaseline? baseline, out string error) {
        baseline = null;

        try {
            using var document = JsonDocument.Parse(json: File.ReadAllText(path: path));
            var root = document.RootElement;

            if (!root.TryGetProperty(propertyName: "schema", value: out var schema) || (schema.GetString() != SweepReport.SchemaName)) {
                error = $"{path} is not a {SweepReport.SchemaName} report";

                return false;
            }

            var read = new SweepBaseline(path: System.IO.Path.GetFullPath(path: path));
            var directory = (System.IO.Path.GetDirectoryName(path: read.Path) ?? ".");

            if (root.TryGetProperty(propertyName: "signals", value: out var signals)) {
                foreach (var signal in signals.EnumerateArray()) {
                    var key = Key(leg: signal.GetProperty(propertyName: "leg").GetString()!, name: signal.GetProperty(propertyName: "name").GetString()!);
                    var metrics = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

                    foreach (var metric in signal.GetProperty(propertyName: "metrics").EnumerateObject()) {
                        metrics[metric.Name] = (metric.Value.GetString() ?? string.Empty);
                    }

                    read.m_metrics[key] = metrics;
                    read.m_status[key] = (signal.GetProperty(propertyName: "status").GetString() ?? SweepStatus.Unknown);
                }
            }
            if (root.TryGetProperty(propertyName: "tour", value: out var tour) && tour.TryGetProperty(propertyName: "stills", value: out var stills)) {
                foreach (var still in stills.EnumerateArray()) {
                    if (still.TryGetProperty(propertyName: "file", value: out var file) && (file.GetString() is { } relative)) {
                        read.m_stills[still.GetProperty(propertyName: "name").GetString()!] = System.IO.Path.GetFullPath(path: System.IO.Path.Combine(path1: directory, path2: relative));
                    }
                    if (still.TryGetProperty(propertyName: "exposure", value: out var exposure) && (exposure.GetString() is { } reading)) {
                        read.m_exposures[still.GetProperty(propertyName: "name").GetString()!] = reading;
                    }
                }
            }

            baseline = read;
            error = string.Empty;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)) {
            error = $"{path} could not be read as a sweep report: {exception.Message}";

            return false;
        }
    }
    /// <summary>Returns the baseline's still for a stop.</summary>
    /// <param name="name">The stop.</param>
    /// <returns>The still's path, or <see langword="null"/> when the baseline has none.</returns>
    public string? StillFor(string name) => ((m_stills.TryGetValue(key: name, value: out var path) && File.Exists(path: path)) ? path : null);
    /// <summary>Returns the exposure the baseline's still for a stop was taken at.</summary>
    /// <param name="name">The stop.</param>
    /// <returns>The reading (<see cref="SweepStillResult.Exposure"/>), or <see langword="null"/> when the baseline has none.</returns>
    public string? ExposureFor(string name) => m_exposures.GetValueOrDefault(key: name);
    /// <summary>Names signals lost from a leg that this run selected.</summary>
    public IEnumerable<SweepSignalResult> MissingSignals(IReadOnlyList<SweepSignalResult> current, IReadOnlyList<SweepLegResult> legs) {
        var present = current.Select(selector: static signal => Key(leg: signal.Leg, name: signal.Name)).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var leg in legs.Select(selector: static leg => leg.Leg).Distinct()) {
            foreach (var key in m_metrics.Keys.Where(predicate: key => (key.StartsWith(comparisonType: StringComparison.Ordinal, value: (leg + "/")) && !present.Contains(item: key)))) {
                yield return new SweepSignalResult(key[(leg.Length + 1)..], leg, string.Empty, SweepStatus.Unknown,
                    ["baseline signal missing from this run"], new Dictionary<string, string>(), string.Empty, ["signal gone"], SweepStatus.Red);
            }
        }
    }
    /// <summary>Names what changed in a signal against the baseline, by the signal's compare modes.</summary>
    /// <param name="leg">The leg.</param>
    /// <param name="signal">The signal.</param>
    /// <param name="status">The signal's colour now.</param>
    /// <param name="metrics">Its metrics now.</param>
    /// <returns>One line per change: a colour, a metric that appeared, went or moved beyond its tolerance.</returns>
    public IReadOnlyList<string> Changes(string leg, SweepSignal signal, string status, IReadOnlyDictionary<string, string> metrics) {
        var key = Key(leg: leg, name: signal.Name);

        if (!m_metrics.TryGetValue(key: key, value: out var before)) {
            return ["new signal"];
        }

        var changes = new List<string>();

        if (m_status[key] != status) {
            changes.Add(item: $"{m_status[key]} -> {status}");
        }

        foreach (var name in before.Keys.Union(second: metrics.Keys).Order(comparer: StringComparer.Ordinal)) {
            var mode = ModeFor(compare: signal.Compare, metric: name);

            if (mode == "none") {
                continue;
            }

            var had = before.TryGetValue(key: name, value: out var old);
            var has = metrics.TryGetValue(key: name, value: out var now);

            if (!had || !has) {
                changes.Add(item: (had ? $"{name} gone (was {old})" : $"{name} new ({now})"));

                continue;
            }
            if (old == now) {
                continue;
            }
            if (mode.StartsWith(comparisonType: StringComparison.Ordinal, value: "relative:") && SweepRule.TryNumber(number: out var fraction, text: mode["relative:".Length..]) && SweepRule.TryNumber(number: out var a, text: old!) && SweepRule.TryNumber(number: out var b, text: now!)) {
                if (Math.Abs(value: (b - a)) <= (fraction * Math.Max(val1: Math.Abs(value: a), val2: 1e-9))) {
                    continue;
                }
            }

            changes.Add(item: $"{name} {old} -> {now}");
        }

        return changes;
    }

    // A metric's compare mode: its own name, then the longest prefix entry ending in '*', then none.
    private static string ModeFor(IReadOnlyDictionary<string, string>? compare, string metric) {
        if (compare is null) {
            return "none";
        }
        if (compare.TryGetValue(key: metric, value: out var exact)) {
            return exact;
        }

        return (compare.Where(predicate: entry => (entry.Key.EndsWith(value: '*') && metric.StartsWith(comparisonType: StringComparison.Ordinal, value: entry.Key[..^1])))
            .OrderByDescending(keySelector: static entry => entry.Key.Length)
            .Select(selector: static entry => entry.Value)
            .FirstOrDefault() ?? "none");
    }
    private static string Key(string leg, string name) => $"{leg}/{name}";
}
