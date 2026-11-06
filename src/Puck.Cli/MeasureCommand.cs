using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions;
using Puck.Cli.Affected;

namespace Puck.Cli;

/// <summary>The shared queue of timing questions, drained under one priority quiet window.</summary>
public static class MeasureCommand {
    public static Command Create(TimeProvider clock, LeaseSettle settleWait) {
        var name = new Option<string>("--name") { Required = true, Description = "The timing question's name." };
        var before = new Option<string?>("--before-world-artifact") { Description = "Before Puck.World.dll; its entire output directory is copied into the request." };
        var after = new Option<string?>("--after-world-artifact") { Description = "After Puck.World.dll; its entire output directory is copied into the request." };
        var beforeCommit = new Option<string?>("--before-commit") { Description = "Commit to export and freeze with the existing World build store instead of --before-world-artifact." };
        var afterCommit = new Option<string?>("--after-commit") { Description = "Commit to export and freeze instead of --after-world-artifact." };
        var metrics = new Option<string[]>("--metric") { Required = true, Description = "Numeric JSON pointer in the command's report, repeatable: /Summary/MedianMilliseconds for bench startup; /signals/0/metrics/<key> for sweep." };
        var decides = new Option<string>("--decides") { Required = true, Description = "What result decides the question, including units and tolerance. Kept with the answer for the lead's judgement." };
        var command = CliOptions.Forwarded(name: "command", description: "A puck bench startup or sweep command after -- (omit puck). Sweep plans also measure headless host.frames signals. Output and World artifact are supplied by the runner.");
        command.Arity = ArgumentArity.OneOrMore;
        var request = new Command("request", "Freeze before and after builds and queue a timing question in the main checkout's ignored scratch.") { name, before, after, beforeCommit, afterCommit, metrics, decides, command };
        request.SetAction(async (parse, token) => {
            var root = RepositoryPaths.RequireRoot();
            var queue = await QueueAsync(root, token).ConfigureAwait(false);
            var arguments = parse.GetValue(command)!;
            ValidateCommand(arguments);
            var selected = parse.GetValue(metrics)!;
            if (selected.Length == 0 || selected.Any(static metric => !metric.StartsWith('/')) || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length) {
                return CliExit.Refuse("measure request", "--metric", "use distinct JSON pointers starting with /.");
            }
            if (string.IsNullOrWhiteSpace(parse.GetValue(name)) || string.IsNullOrWhiteSpace(parse.GetValue(decides))) {
                return CliExit.Refuse("measure request", "question", "--name and --decides must be nonempty.");
            }
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(queue, id);
            Directory.CreateDirectory(directory);
            // request.json is the publication point: a half-frozen request is never runnable.
            var a = Freeze(root, directory, "before", parse.GetValue(before), parse.GetValue(beforeCommit));
            var b = Freeze(root, directory, "after", parse.GetValue(after), parse.GetValue(afterCommit));
            var item = new MeasurementRequest(id, parse.GetValue(name)!, PuckPaths.Normalize(root), clock.GetUtcNow(), a, b, arguments, selected, parse.GetValue(decides)!, null);
            Write(Path.Combine(directory, "request.json"), item, MeasureJsonContext.Default.MeasurementRequest);
            Console.WriteLine($"measure: queued {id} {item.Name}; frozen builds and request at {PuckPaths.Normalize(directory)}");
            return 0;
        });
        request.Detail("Use: puck measure request --name <question> --before-world-artifact <dll> --after-world-artifact <dll> --metric <JSON-pointer> --decides <criterion> -- bench startup <world> --headless --samples 1\nUse --before-commit and/or --after-commit instead to export and build that revision now. The request copies each build's complete output, so rebuilding or pruning the source build cannot change a queued measurement. Commands run from the requesting checkout; keep its world and plan inputs unchanged until answered. Requests contain no timing claim.");
        var list = new Command("list", "List pending and answered timing questions, their criteria and answer files.");
        list.SetAction(async (_, token) => {
            var queue = await QueueAsync(RepositoryPaths.RequireRoot(), token).ConfigureAwait(false);
            foreach (var (_, item) in ReadQueue(queue)) {
                Console.WriteLine($"{item.Id} {(item.Report is null ? "pending" : "answered")} {item.Name}: {item.Decides}{(item.Report is null ? "" : $"; {item.Report}")}");
            }
            return 0;
        });
        var repeats = new Option<int>("--repeats") { DefaultValueFactory = _ => 5, Description = "Number of paired A/B rounds per request (1..100, default 5), run ABAB." };
        var wait = new Option<int>("--wait") { DefaultValueFactory = _ => 600, Description = "Total seconds to acquire the priority quiet window (default 600)." };
        var settle = new Option<int>("--settle") { DefaultValueFactory = _ => 30, Description = "Idle seconds before measurement (default 30); zero explicitly skips settling." };
        var timeout = new Option<int>("--timeout-seconds") { DefaultValueFactory = _ => 600, Description = "Bound per command sample, including shutdown (1..7200, default 600)." };
        var holder = new Option<string>("--holder") { DefaultValueFactory = _ => "measure", Description = "Quiet lease holder/lane (default measure)." };
        var ids = new Argument<string[]>("requests") { Arity = ArgumentArity.ZeroOrMore, Description = "Pending request IDs; omitted drains the pending snapshot in arrival order." };
        var run = new Command("run", "Drain queued questions in one quiet window; report medians, IQR and paired after-minus-before differences.") { repeats, wait, settle, timeout, holder, ids };
        run.SetAction(async (parse, token) => {
            var rounds = parse.GetValue(repeats);
            var waiting = parse.GetValue(wait);
            var settling = parse.GetValue(settle);
            var bound = parse.GetValue(timeout);
            if (rounds is < 1 or > 100 || waiting < 0 || settling < 0 || bound is < 1 or > 7200) {
                return CliExit.Refuse("measure run", "limits", "repeats must be 1..100, wait/settle nonnegative, timeout 1..7200 seconds.");
            }
            var checkout = RepositoryPaths.RequireRoot();
            var queue = await QueueAsync(checkout, token).ConfigureAwait(false);
            Directory.CreateDirectory(queue);
            using var runner = FileLock.TryAcquire(Path.Combine(queue, "run.guard"));
            if (runner is null) { return CliExit.Refuse("measure run", "queue", "another measurement window is already draining it."); }
            var selected = parse.GetValue(ids) ?? [];
            var pending = ReadQueue(queue).Where(row => row.Item.Report is null && (selected.Length == 0 || selected.Contains(row.Item.Id, StringComparer.Ordinal))).ToArray();
            if (selected.Any(id => !pending.Any(row => row.Item.Id == id))) { return CliExit.Refuse("measure run", "requests", "an ID is missing or already answered."); }
            if (pending.Length == 0) { Console.WriteLine("measure: no pending questions."); return 0; }
            var leases = await CliLease.RootAsync(checkout, token).ConfigureAwait(false);
            using var quiet = await CliLease.AcquireQuietAsync(leases, parse.GetValue(holder)!, TimeSpan.FromSeconds(waiting), clock, checkout, token, ProcessInspection.IsAncestor).ConfigureAwait(false);
            if (settling > 0 && await settleWait(TimeSpan.FromSeconds(settling), TimeSpan.FromSeconds(waiting + (double)settling), Console.Error, token).ConfigureAwait(false) is { } reason) {
                return CliExit.Refuse("measure run", "settle", reason);
            }
            foreach (var (path, item) in pending) {
                token.ThrowIfCancellationRequested();
                var directory = Path.Combine(Path.GetDirectoryName(path)!, "answers", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var samples = new List<MeasurementSample>();
                var report = Path.Combine(directory, "report.json");
                for (var round = 0; round < rounds; round++) {
                    foreach (var side in new[] { "before", "after" }) {
                        var sample = await SampleAsync(item, side, round, directory, bound, token).ConfigureAwait(false);
                        samples.Add(sample);
                        Write(report, new MeasurementReport(item, rounds, samples.ToArray(), []), MeasureJsonContext.Default.MeasurementReport);
                    }
                }
                var summaries = item.Metrics.Select(metric => Summarize(metric,
                    samples.Where(sample => sample.Side == "before").Select(sample => sample.Metrics[metric]).ToArray(),
                    samples.Where(sample => sample.Side == "after").Select(sample => sample.Metrics[metric]).ToArray())).ToArray();
                Write(report, new MeasurementReport(item, rounds, samples.ToArray(), summaries), MeasureJsonContext.Default.MeasurementReport);
                foreach (var metric in summaries) {
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"measure {item.Name} {metric.Metric}: before median={metric.Before.Median:G6} IQR={metric.Before.Iqr:G6}; after median={metric.After.Median:G6} IQR={metric.After.Iqr:G6}; paired difference median={metric.Difference.Median:G6} IQR={metric.Difference.Iqr:G6}"));
                }
                Write(path, item with { Report = PuckPaths.Normalize(report) }, MeasureJsonContext.Default.MeasurementRequest);
                Console.WriteLine($"measure: answered {item.Id}; {item.Decides}; report {PuckPaths.Normalize(report)}");
            }
            return 0;
        });
        run.Detail("Use: puck measure run [request-ids...] [--repeats 5] [--wait 600] [--settle 30]\nTakes one priority quiet lease for the pending snapshot. Existing holders finish; later grants wait for the measurement window. Each request runs before, after, before, after in fresh invocations of the existing bench startup or sweep harness. No command wall clock is measured here. Missing/nonfinite metrics or failed commands refuse the answer; partial evidence stays and the request remains pending. A completed report records raw samples, median and IQR for before, after and the paired after-minus-before difference. --decides is a human criterion, never an automatic acceptance guess. New requests wait for the next window.");
        return new Command("measure", "Queue frozen before/after timing questions and answer them in a priority quiet window.") { request, list, run };
    }

    private static async Task<string> QueueAsync(string checkout, CancellationToken token) => Path.Combine(Path.GetDirectoryName(await CliLease.RootAsync(checkout, token).ConfigureAwait(false))!, "scratch/measure");

    private static IEnumerable<(string Path, MeasurementRequest Item)> ReadQueue(string queue) => !Directory.Exists(queue) ? [] :
        Directory.EnumerateDirectories(queue).Select(directory => Path.Combine(directory, "request.json")).Where(File.Exists)
            .Select(path => (Path: path, Item: JsonSerializer.Deserialize(File.ReadAllText(path), MeasureJsonContext.Default.MeasurementRequest) ?? throw new InvalidDataException($"Empty request: {path}")))
            .OrderBy(row => row.Item.Created).ThenBy(row => row.Item.Id, StringComparer.Ordinal);

    private static string Freeze(string checkout, string directory, string side, string? artifact, string? commit) {
        if ((artifact is null) == (commit is null)) { throw new ArgumentException($"Supply exactly one of --{side}-world-artifact and --{side}-commit."); }
        if (commit is not null) {
            if (!CliGit.TryResolveCommit(checkout, commit, out var revision)) { throw new ArgumentException($"No commit {commit}."); }
            using var export = AffectedRevisionExport.Create(checkout, revision, ["."]);
            if (!WorldArtifactBuild.TryResolve("measure request", export.Root, new WorldArtifactStore(Path.Combine(Path.GetDirectoryName(directory)!, "build-store")), null,
                directory, TimeSpan.FromMinutes(30), out var built, out var error,
                leaseRoot: () => CliLease.RootAsync(checkout).GetAwaiter().GetResult(), spirv: true)) { throw new InvalidOperationException(error); }
            using (built) { return CopyBuild(built.Path, directory, side); }
        }
        // Retain a managed-store input against pruning while copying it.
        if (!WorldArtifactBuild.TryResolveNamed(artifact, "measure request", checkout, directory, TimeSpan.FromMinutes(30), out var source, out var lease, out var refusal)) { throw new ArgumentException(refusal); }
        using (lease) { return CopyBuild(source, directory, side); }
    }

    private static string CopyBuild(string source, string directory, string side) {
        var target = Path.Combine(directory, side);
        CliFiles.CopyDirectory(Path.GetDirectoryName(Path.GetFullPath(source))!, target);
        return PuckPaths.Normalize(Path.Combine(target, Path.GetFileName(source)));
    }

    private static void ValidateCommand(string[] arguments) {
        if (!(arguments is ["bench", "startup", ..] or ["sweep", ..])) { throw new ArgumentException("Measurement commands are bench startup or sweep (including headless frame-signal plans)."); }
        if (arguments.Any(static value => value is "--world-artifact" or "--output" || value.StartsWith("--world-artifact=", StringComparison.Ordinal) || value.StartsWith("--output=", StringComparison.Ordinal))) {
            throw new ArgumentException("The measurement runner supplies --world-artifact and --output.");
        }
    }

    private static async Task<MeasurementSample> SampleAsync(MeasurementRequest item, string side, int round, string directory, int timeout, CancellationToken token) {
        var output = Path.Combine(directory, $"{round:D3}-{side}");
        Directory.CreateDirectory(output);
        var cli = typeof(MeasureCommand).Assembly.Location;
        var run = await CliProcess.RunAsync("dotnet", [cli, .. item.Command, "--world-artifact", (side == "before" ? item.Before : item.After), "--output", output],
            workingDirectory: item.Checkout, timeout: TimeSpan.FromSeconds(timeout), cancellationToken: token).ConfigureAwait(false);
        File.WriteAllText(Path.Combine(output, "command.stdout.log"), run.Stdout);
        File.WriteAllText(Path.Combine(output, "command.stderr.log"), run.Stderr);
        if (run.ExitCode != 0 || run.TimedOut) { throw new InvalidOperationException($"measure {item.Name} {side} #{round + 1}: command failed ({run.ExitCode}, timedOut={run.TimedOut}); {PuckPaths.Normalize(output)}"); }
        var reports = Directory.GetFiles(output, "report.json", SearchOption.AllDirectories);
        if (reports.Length != 1) { throw new InvalidDataException($"Expected one command report under {output}; found {reports.Length}."); }
        using var json = JsonDocument.Parse(File.ReadAllText(reports[0]));
        var readings = item.Metrics.ToDictionary(metric => metric, metric => ReadMetric(json.RootElement, metric), StringComparer.Ordinal);
        Console.WriteLine($"measure: {item.Name} {side} #{round + 1} read {readings.Count} metric(s).");
        return new(side, round, readings, PuckPaths.Normalize(reports[0]));
    }

    public static double ReadMetric(JsonElement root, string pointer) {
        var value = root;
        foreach (var part in pointer.Split('/').Skip(1)) {
            var key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(key, CultureInfo.InvariantCulture)] : value.GetProperty(key);
        }
        var result = value.ValueKind == JsonValueKind.String ? double.Parse(value.GetString()!, CultureInfo.InvariantCulture) : value.GetDouble();
        if (!double.IsFinite(result)) { throw new InvalidDataException($"Metric {pointer} is not finite."); }
        return result;
    }

    public static MeasurementMetric Summarize(string metric, double[] before, double[] after) {
        if (before.Length == 0 || before.Length != after.Length || before.Concat(after).Any(value => !double.IsFinite(value))) { throw new ArgumentException("Paired finite samples are required."); }
        return new(metric, Distribution(before), Distribution(after), Distribution(before.Zip(after, (a, b) => b - a).ToArray()));
    }
    private static MeasurementDistribution Distribution(double[] samples) {
        var ordered = samples.Order().ToArray();
        double Quantile(double fraction) {
            var position = (ordered.Length - 1) * fraction;
            var lower = (int)Math.Floor(position);
            var upper = (int)Math.Ceiling(position);
            return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
        }
        return new(Quantile(.5), Quantile(.75) - Quantile(.25));
    }
    private static void Write<T>(string path, T item, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) {
        var temporary = path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(item, type) + "\n");
        File.Move(temporary, path, true);
    }
}

public sealed record MeasurementRequest(string Id, string Name, string Checkout, DateTimeOffset Created, string Before, string After, string[] Command, string[] Metrics, string Decides, string? Report);
public sealed record MeasurementSample(string Side, int Round, Dictionary<string, double> Metrics, string SourceReport);
public sealed record MeasurementDistribution(double Median, double Iqr);
public sealed record MeasurementMetric(string Metric, MeasurementDistribution Before, MeasurementDistribution After, MeasurementDistribution Difference);
public sealed record MeasurementReport(MeasurementRequest Request, int Repeats, MeasurementSample[] Samples, MeasurementMetric[] Metrics);

[JsonSerializable(typeof(MeasurementRequest))]
[JsonSerializable(typeof(MeasurementReport))]
[JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n")]
internal sealed partial class MeasureJsonContext : JsonSerializerContext;
