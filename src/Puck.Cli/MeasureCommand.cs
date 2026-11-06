using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions;
using Puck.Cli.Affected;
using Puck.Cli.Bench;

namespace Puck.Cli;

/// <summary>The shared queue of timing questions, drained under one priority quiet window.</summary>
public static class MeasureCommand {
    public static Command Create(TimeProvider clock, LeaseSettle settleWait) {
        var name = new Option<string>("--name") { Required = true, Description = "The timing question's name." };
        var before = new Option<string?>("--before-world-artifact") { Description = "Before Puck.World.dll; its entire output directory is copied into the request." };
        var after = new Option<string?>("--after-world-artifact") { Description = "After Puck.World.dll; its entire output directory is copied into the request." };
        var beforeCommit = new Option<string?>("--before-commit") { Description = "Commit to export and freeze with the existing World build store instead of --before-world-artifact." };
        var afterCommit = new Option<string?>("--after-commit") { Description = "Commit to export and freeze instead of --after-world-artifact." };
        var beforeTree = new Option<string?>("--before-input-tree") { Description = "Input tree paired with --before-world-artifact: worlds and plan inputs at their repository-relative paths. Copied when filed." };
        var afterTree = new Option<string?>("--after-input-tree") { Description = "Input tree paired with --after-world-artifact. Required for an artifact side; commit sides use their own whole tree." };
        var metrics = new Option<string[]>("--metric") { Required = true, Description = "Numeric JSON pointer in the command's report, repeatable: /metrics/idle-tick/median for bench world; /metrics/startup/median for bench startup; /signals/0/metrics/<key> for sweep." };
        var decides = new Option<string>("--decides") { Required = true, Description = "What result decides the question, including units and tolerance. Kept with the answer for the lead's judgement." };
        var command = CliOptions.Forwarded(name: "command", description: "A puck bench world, bench startup or sweep command after -- (omit puck). Sweep plans also measure headless host.frames signals. Output and World artifact are supplied by the runner.");
        command.Arity = ArgumentArity.OneOrMore;
        var request = new Command("request", "Freeze before and after builds and queue a timing question in the main checkout's ignored scratch.") { name, before, after, beforeCommit, afterCommit, beforeTree, afterTree, metrics, decides, command };
        request.SetAction(async (parse, token) => {
            var root = RepositoryPaths.RequireRoot();
            var queue = await QueueAsync(root, token).ConfigureAwait(false);
            var arguments = parse.GetValue(command)!;
            ValidateCommand(arguments);
            var selected = parse.GetValue(metrics)!;
            if (selected.Length == 0 || selected.Any(static metric => !metric.StartsWith('/')) || selected.Distinct(StringComparer.Ordinal).Count() != selected.Length) {
                return CliExit.Refuse("measure request", "--metric", "use distinct JSON pointers starting with /.");
            }
            foreach (var metric in selected) { ValidateMetric(arguments, metric); }
            if (string.IsNullOrWhiteSpace(parse.GetValue(name)) || string.IsNullOrWhiteSpace(parse.GetValue(decides))) {
                return CliExit.Refuse("measure request", "question", "--name and --decides must be nonempty.");
            }
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(queue, id);
            Directory.CreateDirectory(directory);
            // request.json is the publication point: a half-frozen request is never runnable.
            var a = Freeze(root, directory, "before", parse.GetValue(before), parse.GetValue(beforeCommit), parse.GetValue(beforeTree));
            var b = Freeze(root, directory, "after", parse.GetValue(after), parse.GetValue(afterCommit), parse.GetValue(afterTree));
            arguments = FreezeCommand(root, arguments);
            var item = new MeasurementRequest(id, parse.GetValue(name)!, PuckPaths.Normalize(root), clock.GetUtcNow(), a, b, arguments, selected, parse.GetValue(decides)!, null);
            Write(Path.Combine(directory, "request.json"), item, MeasureJsonContext.Default.MeasurementRequest);
            Console.WriteLine($"measure: queued {id} {item.Name}; frozen builds and request at {PuckPaths.Normalize(directory)}");
            return 0;
        });
        request.Detail("Use: puck measure request --name <question> --before-commit <revision> --after-commit <revision> --metric <JSON-pointer> --decides <criterion> -- bench world\nA commit side freezes its entire tree and build. An artifact side requires --before-input-tree or --after-input-tree to declare the worlds and plan inputs it pairs with; git input trees copy tracked and nonignored untracked files as they are on disk. Standalone trees copy their contents. Commands use repository-relative input paths and run inside each side's frozen tree, independently of later edits or checkout removal. Requests contain no timing claim.");
        var list = new Command("list", "List pending and answered timing questions, their criteria and answer files.");
        list.SetAction(async (_, token) => {
            var queue = await QueueAsync(RepositoryPaths.RequireRoot(), token).ConfigureAwait(false);
            foreach (var (_, item) in ReadQueue(queue)) {
                Console.WriteLine($"{item.Id} {(item.Failure is not null ? "failed" : item.Report is null ? "pending" : "answered")} {item.Name}: {item.Decides}{(item.Report is null ? "" : $"; {item.Report}")}{(item.Failure is null ? "" : $"; {item.Failure}")}");
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
            var pending = ReadQueue(queue).Where(row => row.Item.Report is null && row.Item.Failure is null && (selected.Length == 0 || selected.Contains(row.Item.Id, StringComparer.Ordinal))).ToArray();
            if (selected.Any(id => !pending.Any(row => row.Item.Id == id))) { return CliExit.Refuse("measure run", "requests", "an ID is missing, failed or already answered."); }
            if (pending.Length == 0) { Console.WriteLine("measure: no pending questions."); return 0; }
            var leases = await CliLease.RootAsync(checkout, token).ConfigureAwait(false);
            using var quiet = await CliLease.AcquireQuietAsync(leases, parse.GetValue(holder)!, TimeSpan.FromSeconds(waiting), clock, checkout, token, ProcessInspection.IsAncestor).ConfigureAwait(false);
            if (settling > 0 && await settleWait(TimeSpan.FromSeconds(settling), TimeSpan.FromSeconds(waiting + (double)settling), Console.Error, token).ConfigureAwait(false) is { } reason) {
                return CliExit.Refuse("measure run", "settle", reason);
            }
            return await DrainAsync(pending, rounds, (item, side, round, directory, cancellation) => SampleAsync(item, side, round, directory, bound, cancellation), token).ConfigureAwait(false);
        });
        run.Detail("Use: puck measure run [request-ids...] [--repeats 5] [--wait 600] [--settle 30]\nTakes one priority quiet lease for the pending snapshot. Each request runs before, after in repeated pairs. Failed commands or missing/nonfinite metrics mark that request failed and continue the queue; partial evidence stays. Re-file a corrected request. Reports record raw samples, median and IQR for before, after and paired after-minus-before differences. --decides remains a human criterion.");
        return new Command("measure", "Queue frozen before/after timing questions and answer them in a priority quiet window.") { request, list, run };
    }

    // One request owns its failure boundary: only a complete set of paired samples can publish an answer.
    public static async Task<int> DrainAsync(IEnumerable<(string Path, MeasurementRequest Item)> pending, int rounds,
        Func<MeasurementRequest, string, int, string, CancellationToken, Task<MeasurementSample>> sampleAsync, CancellationToken token) {
        var failed = false;
        foreach (var (path, item) in pending) {
            token.ThrowIfCancellationRequested();
            var directory = Path.Combine(Path.GetDirectoryName(path)!, "answers", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var samples = new List<MeasurementSample>();
            var report = Path.Combine(directory, "report.json");
            try {
                for (var round = 0; round < rounds; round++) {
                    foreach (var side in new[] { "before", "after" }) {
                        var sample = await sampleAsync(item, side, round, directory, token).ConfigureAwait(false);
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
            } catch (Exception error) when (error is InvalidDataException or InvalidOperationException or ArgumentException or IOException) {
                failed = true;
                var reason = $"measure {item.Name} ({item.Id}): {error.Message}";
                Write(report, new MeasurementReport(item, rounds, samples.ToArray(), [], reason), MeasureJsonContext.Default.MeasurementReport);
                Write(path, item with { Failure = reason }, MeasureJsonContext.Default.MeasurementRequest);
                Console.Error.WriteLine($"measure: failed {reason}; evidence {PuckPaths.Normalize(report)}");
            }
        }
        return failed ? CliExit.Refused : CliExit.Success;
    }

    private static async Task<string> QueueAsync(string checkout, CancellationToken token) => Path.Combine(Path.GetDirectoryName(await CliLease.RootAsync(checkout, token).ConfigureAwait(false))!, "scratch/measure");

    private static IEnumerable<(string Path, MeasurementRequest Item)> ReadQueue(string queue) => !Directory.Exists(queue) ? [] :
        Directory.EnumerateDirectories(queue).Select(directory => Path.Combine(directory, "request.json")).Where(File.Exists)
            .Select(path => (Path: path, Item: JsonSerializer.Deserialize(File.ReadAllText(path), MeasureJsonContext.Default.MeasurementRequest) ?? throw new InvalidDataException($"Empty request: {path}")))
            .OrderBy(row => row.Item.Created).ThenBy(row => row.Item.Id, StringComparer.Ordinal);

    private static MeasurementSide Freeze(string checkout, string directory, string side, string? artifact, string? commit, string? inputTree) {
        if ((artifact is null) == (commit is null)) { throw new ArgumentException($"Supply exactly one of --{side}-world-artifact and --{side}-commit."); }
        if ((commit is null) == (inputTree is null)) { throw new ArgumentException($"An artifact side requires --{side}-input-tree; a commit side uses that commit's tree."); }
        var tree = FreezeTree(checkout, Path.Combine(directory, side, "tree"), commit, inputTree);
        if (commit is not null) {
            if (!CliGit.TryResolveCommit(checkout, commit, out var revision)) { throw new ArgumentException($"No commit {commit}."); }
            if (!WorldArtifactBuild.TryResolve("measure request", tree, new WorldArtifactStore(Path.Combine(Path.GetDirectoryName(directory)!, "build-store")), null,
                directory, TimeSpan.FromMinutes(30), out var built, out var error,
                leaseRoot: () => CliLease.RootAsync(checkout).GetAwaiter().GetResult(), spirv: true)) { throw new InvalidOperationException(error); }
            using (built) { return new(CopyBuild(built.Path, directory, side), tree, revision); }
        }
        // Retain a managed-store input against pruning while copying it.
        if (!WorldArtifactBuild.TryResolveNamed(artifact, "measure request", checkout, directory, TimeSpan.FromMinutes(30), out var source, out var lease, out var refusal)) { throw new ArgumentException(refusal); }
        using (lease) { return new(CopyBuild(source, directory, side), tree, null); }
    }

    public static string FreezeTree(string checkout, string target, string? commit, string? inputTree) {
        if ((commit is null) == (inputTree is null)) { throw new ArgumentException("Supply one commit or input tree."); }
        if (commit is not null) {
            if (!CliGit.TryResolveCommit(checkout, commit, out var revision)) { throw new ArgumentException($"No commit {commit}."); }
            using var export = AffectedRevisionExport.Create(checkout, revision, ["."]);
            CliFiles.CopyDirectory(export.Root, target);
        } else {
            var source = Path.GetFullPath(inputTree!);
            if (!Directory.Exists(source)) { throw new ArgumentException($"Input tree {source} does not exist."); }
            if (File.Exists(Path.Combine(source, ".git")) || Directory.Exists(Path.Combine(source, ".git"))) {
                var listing = CliGit.Run(source, "ls-files", "-z", "--cached", "--others", "--exclude-standard");
                if (listing.ExitCode != 0) { throw new InvalidOperationException($"Cannot freeze input tree {source}: {listing.Stderr}"); }
                var paths = listing.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
                CliFiles.CopyDirectory(source, target, paths.Distinct(StringComparer.Ordinal).Select(p => Path.Combine(source, p)).Where(File.Exists));
            } else {
                if (Inside(source, target)) { throw new ArgumentException("A standalone input tree cannot contain its frozen destination."); }
                CliFiles.CopyDirectory(source, target);
            }
        }
        return PuckPaths.Normalize(Path.GetFullPath(target));
    }

    private static bool Inside(string root, string path) {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    public static string[] FreezeCommand(string checkout, string[] arguments) => arguments.Select(argument => {
        var equals = argument.StartsWith("--", StringComparison.Ordinal) ? argument.IndexOf('=') : -1;
        var value = equals >= 0 ? argument[(equals + 1)..] : argument;
        if (Path.IsPathRooted(value)) {
            if (!Inside(checkout, value)) { throw new ArgumentException($"Input {value} is outside the requesting checkout; place inputs in the declared tree and use relative paths."); }
            value = Path.GetRelativePath(checkout, value).Replace(Path.DirectorySeparatorChar, '/');
        } else if (!value.StartsWith("--", StringComparison.Ordinal) && !Inside(checkout, Path.GetFullPath(value, checkout))) {
            throw new ArgumentException($"Input {value} escapes the frozen input tree.");
        }
        return equals >= 0 ? argument[..(equals + 1)] + value : value;
    }).ToArray();

    private static string CopyBuild(string source, string directory, string side) {
        var target = Path.Combine(directory, side, "build");
        CliFiles.CopyDirectory(Path.GetDirectoryName(Path.GetFullPath(source))!, target);
        return PuckPaths.Normalize(Path.Combine(target, Path.GetFileName(source)));
    }

    private static void ValidateCommand(string[] arguments) {
        if (!(arguments is ["bench", "world", ..] or ["bench", "startup", ..] or ["sweep", ..])) { throw new ArgumentException("Measurement commands are bench world, bench startup or sweep (including headless frame-signal plans)."); }
        if (arguments.Any(static value => value is "--world-artifact" or "--output" || value.StartsWith("--world-artifact=", StringComparison.Ordinal) || value.StartsWith("--output=", StringComparison.Ordinal))) {
            throw new ArgumentException("The measurement runner supplies --world-artifact and --output.");
        }
    }

    private static async Task<MeasurementSample> SampleAsync(MeasurementRequest item, string side, int round, string directory, int timeout, CancellationToken token) {
        var output = Path.Combine(directory, $"{round:D3}-{side}");
        Directory.CreateDirectory(output);
        var cli = typeof(MeasureCommand).Assembly.Location;
        var frozen = side == "before" ? item.Before : item.After;
        var run = await CliProcess.RunAsync("dotnet", [cli, .. item.Command, "--world-artifact", frozen.Artifact, "--output", output],
            workingDirectory: frozen.Tree, timeout: TimeSpan.FromSeconds(timeout), cancellationToken: token).ConfigureAwait(false);
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
        var resolved = pointer.StartsWith('/');
        foreach (var part in pointer.Split('/').Skip(1)) {
            var key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var child)) { value = child; }
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < value.GetArrayLength()) { value = value[index]; }
            else { resolved = false; break; }
        }
        if (resolved && TryNumber(value, out var result)) { return result; }
        var schema = root.TryGetProperty("schema", out var format) ? format.GetString() : "unspecified";
        throw new InvalidDataException($"Metric {pointer} is missing or not finite numeric data in report schema {schema}; available pointers: {string.Join(", ", NumericPointers(root))}.");
    }

    private static bool TryNumber(JsonElement value, out double number) {
        number = double.NaN;
        return ((value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number)) ||
            (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))) && double.IsFinite(number);
    }

    private static IEnumerable<string> NumericPointers(JsonElement value, string path = "") {
        if (TryNumber(value, out _)) { yield return path; }
        else if (value.ValueKind == JsonValueKind.Object) {
            foreach (var field in value.EnumerateObject()) {
                foreach (var pointer in NumericPointers(field.Value, path + "/" + Escape(field.Name))) { yield return pointer; }
            }
        } else if (value.ValueKind == JsonValueKind.Array) {
            var index = 0;
            foreach (var child in value.EnumerateArray()) {
                foreach (var pointer in NumericPointers(child, path + "/" + index.ToString(CultureInfo.InvariantCulture))) { yield return pointer; }
                index++;
            }
        }
    }

    private static string Escape(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    public static void ValidateMetric(string[] command, string pointer) {
        ValidateCommand(command);
        var parts = pointer.Split('/');
        if (command[0] == "bench") {
            // Dictionary keys are authored/runtime metric names. Derive numeric members from the report's serializer.
            var report = BenchReport.Start("schema", new BenchBuild("", "", ""), 1, new Dictionary<string, string>()) with {
                Metrics = new Dictionary<string, BenchMetric> { ["<name>"] = BenchMetric.Of("ms", [0]) },
            };
            using var json = JsonDocument.Parse(report.ToJson());
            var available = NumericPointers(json.RootElement).Where(p => p.StartsWith("/metrics/", StringComparison.Ordinal)).ToArray();
            var canonical = parts.Length >= 3 ? "/metrics/<name>/" + string.Join('/', parts.Skip(3).Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? "0" : p)) : "";
            if (parts.Length >= 4 && parts[0] == "" && parts[1] == "metrics" && parts[2].Length > 0 &&
                (command[1] != "startup" || parts[2] == "startup") && available.Contains(canonical, StringComparer.Ordinal)) { return; }
            throw new InvalidDataException($"Metric {pointer} is not numeric data in report schema {BenchReport.SchemaName}; available pointers: {string.Join(", ", available.Select(p => command[1] == "startup" ? p.Replace("<name>", "startup", StringComparison.Ordinal) : p))}.");
        }
        if (parts is ["", "signals", var index, "metrics", var key] && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out _) && key.Length > 0) { return; }
        throw new InvalidDataException($"Metric {pointer} is not a signal metric in report schema {Puck.Cli.Sweep.SweepReport.SchemaName}; available pointers: /signals/<index>/metrics/<name> (numeric readings from the plan).");
    }

    public static MeasurementMetric Summarize(string metric, double[] before, double[] after) {
        if (before.Length == 0 || before.Length != after.Length || before.Concat(after).Any(value => !double.IsFinite(value))) { throw new ArgumentException("Paired finite samples are required."); }
        return new(metric, Distribution(before), Distribution(after), Distribution(before.Zip(after, (a, b) => b - a).ToArray()));
    }
    private static MeasurementDistribution Distribution(double[] samples) {
        var ordered = samples.Order().ToArray();
        double Quantile(double fraction) => Puck.Cli.Bench.BenchMetric.Quantile(fraction: fraction, ordered: ordered);
        return new(Quantile(.5), Quantile(.75) - Quantile(.25));
    }
    private static void Write<T>(string path, T item, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) {
        var temporary = path + ".writing";
        File.WriteAllText(temporary, JsonSerializer.Serialize(item, type) + "\n");
        File.Move(temporary, path, true);
    }
}

public sealed record MeasurementSide(string Artifact, string Tree, string? Revision);
public sealed record MeasurementRequest(string Id, string Name, string Checkout, DateTimeOffset Created, MeasurementSide Before, MeasurementSide After, string[] Command, string[] Metrics, string Decides, string? Report, string? Failure = null);
public sealed record MeasurementSample(string Side, int Round, Dictionary<string, double> Metrics, string SourceReport);
public sealed record MeasurementDistribution(double Median, double Iqr);
public sealed record MeasurementMetric(string Metric, MeasurementDistribution Before, MeasurementDistribution After, MeasurementDistribution Difference);
public sealed record MeasurementReport(MeasurementRequest Request, int Repeats, MeasurementSample[] Samples, MeasurementMetric[] Metrics, string? Failure = null);

[JsonSerializable(typeof(MeasurementRequest))]
[JsonSerializable(typeof(MeasurementReport))]
[JsonSourceGenerationOptions(WriteIndented = true, NewLine = "\n")]
internal sealed partial class MeasureJsonContext : JsonSerializerContext;
