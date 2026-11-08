using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Abstractions;
using Puck.Cli.Affected;
using Puck.Cli.Bench;
using Puck.Confinement;

namespace Puck.Cli;

/// <summary>The shared queue of timing questions, drained under one priority quiet window.</summary>
public static class MeasureCommand {
    public static Command Create(TimeProvider clock, LeaseSettle settleWait) {
        var name = new Option<string>("--name") { Description = "The timing question's name.", Required = true };
        var before = new Option<string?>("--before-world-artifact") { Description = "Before Puck.World.dll; its entire output directory is copied into the request." };
        var after = new Option<string?>("--after-world-artifact") { Description = "After Puck.World.dll; its entire output directory is copied into the request." };
        var beforeCommit = new Option<string?>("--before-commit") { Description = "Commit to export and freeze with the existing World build store instead of --before-world-artifact." };
        var afterCommit = new Option<string?>("--after-commit") { Description = "Commit to export and freeze instead of --after-world-artifact." };
        var beforeTree = new Option<string?>("--before-input-tree") { Description = "Input tree paired with --before-world-artifact: worlds and plan inputs at their repository-relative paths. Copied when filed." };
        var afterTree = new Option<string?>("--after-input-tree") { Description = "Input tree paired with --after-world-artifact. Required for an artifact side; commit sides use their own whole tree." };
        var metrics = new Option<string[]>("--metric") { Description = "Numeric JSON pointer in the command's report, repeatable: /metrics/idle-tick/median for bench world; /metrics/startup/median for bench startup (/metrics/cold-bake/median with --bake); /metrics/boot.frames.p99/median for bench frames; /signals/0/metrics/<key> for sweep.", Required = true };
        var decides = new Option<string>("--decides") { Description = "What result decides the question, including units and tolerance. Kept with the answer for the lead's judgement.", Required = true };
        var command = CliOptions.ForwardedCommand(description: "A puck bench world, bench startup, bench frames or sweep command after -- (omit puck). Output and World artifact are supplied by the runner.");
        var request = new Command(description: "Freeze before and after builds and queue a timing question in the main checkout's ignored scratch.", name: "request") { name, before, after, beforeCommit, afterCommit, beforeTree, afterTree, metrics, decides, command };

        request.SetAction(async (parse, token) => {
            var root = RepositoryPaths.RequireRoot();
            var queue = await QueueAsync(root, token).ConfigureAwait(continueOnCapturedContext: false);
            var arguments = parse.GetValue(argument: command)!;

            ValidateCommand(arguments: arguments);
            var selected = parse.GetValue(option: metrics)!;

            if ((selected.Length == 0) || selected.Any(predicate: static metric => !metric.StartsWith(value: '/')) || (selected.Distinct(comparer: StringComparer.Ordinal).Count() != selected.Length)) {
                return CliExit.Refuse(verb: "measure request", what: "--metric", why: "use distinct JSON pointers starting with /.");
            }
            foreach (var metric in selected) { ValidateMetric(command: arguments, pointer: metric); }
            if (string.IsNullOrWhiteSpace(value: parse.GetValue(option: name)) || string.IsNullOrWhiteSpace(value: parse.GetValue(option: decides))) {
                return CliExit.Refuse(verb: "measure request", what: "question", why: "--name and --decides must be nonempty.");
            }
            var id = Guid.NewGuid().ToString(format: "N");
            var directory = Path.Combine(queue, id);

            Directory.CreateDirectory(path: directory);
            // request.json is the publication point: a half-frozen request is never runnable.
            var a = Freeze(root, directory, "before", parse.GetValue(option: before), parse.GetValue(option: beforeCommit), parse.GetValue(option: beforeTree));
            var b = Freeze(root, directory, "after", parse.GetValue(option: after), parse.GetValue(option: afterCommit), parse.GetValue(option: afterTree));

            arguments = FreezeCommand(arguments: arguments, checkout: root);
            var item = new MeasurementRequest(id, parse.GetValue(option: name)!, PuckPaths.Normalize(path: root), clock.GetUtcNow(), a, b, arguments, selected, parse.GetValue(option: decides)!, null);

            Write(Path.Combine(path1: directory, path2: "request.json"), item, MeasureJsonContext.Default.MeasurementRequest);
            Console.WriteLine(value: $"measure: queued {id} {item.Name}; frozen builds and request at {PuckPaths.Normalize(path: directory)}");
            return 0;
        });
        request.Detail(detail: "Use: puck measure request --name <question> --before-commit <revision> --after-commit <revision> --metric <JSON-pointer> --decides <criterion> -- bench world\nA commit side freezes its entire tree and build. An artifact side requires --before-input-tree or --after-input-tree to declare the worlds and plan inputs it pairs with; git input trees copy tracked and nonignored untracked files as they are on disk. Standalone trees copy their contents. Commands use repository-relative input paths and run inside each side's frozen tree, independently of later edits or checkout removal. Requests contain no timing claim.");
        var list = new Command(description: "List pending and answered timing questions, their criteria and answer files.", name: "list");

        list.SetAction(action: async (_, token) => {
            var queue = await QueueAsync(checkout: RepositoryPaths.RequireRoot(), token: token).ConfigureAwait(continueOnCapturedContext: false);

            foreach (var (_, item) in ReadQueue(queue: queue)) {
                Console.WriteLine(value: $"{item.Id} {((item.Failure is not null) ? "failed" : ((item.Report is null) ? "pending" : "answered"))} {item.Name}: {item.Decides}{((item.Report is null) ? "" : $"; {item.Report}")}{((item.Failure is null) ? "" : $"; {item.Failure}")}");
            }
            return 0;
        });
        var repeats = CliOptions.Count(defaultValue: 5, description: "Number of paired A/B rounds per request (1..100, default 5), run ABAB.", maximum: 100, minimum: 1, name: "--repeats");
        var wait = CliOptions.Count(name: "--wait", description: "Total seconds to acquire the priority quiet window (default 600).", minimum: 0, defaultValue: 600);
        var settle = CliOptions.Count(name: "--settle", description: "Idle seconds before measurement (default 30); zero explicitly skips settling.", minimum: 0, defaultValue: 30);
        var timeout = CliOptions.Count(defaultValue: 600, description: "Bound per command sample, including shutdown (1..7200, default 600).", maximum: 7200, minimum: 1, name: "--timeout-seconds");
        var window = CliOptions.Count(defaultValue: 600, description: "Window deadline in seconds, including acquisition and settling (1..7200, default 600). Stops the active sample and leaves incomplete requests pending.", maximum: 7200, minimum: 1, name: "--window-seconds");
        var holder = new Option<string>("--holder") { DefaultValueFactory = _ => "measure", Description = "Quiet lease holder/lane (default measure)." };
        var ids = new Argument<string[]>(name: "requests") { Arity = ArgumentArity.ZeroOrMore, Description = "Pending request IDs; omitted drains the pending snapshot in arrival order." };
        var run = new Command(description: "Drain queued questions in one bounded quiet window; report medians, IQR and paired after-minus-before differences.", name: "run") { repeats, wait, settle, timeout, window, holder, ids };

        run.SetAction(action: async (parse, token) => {
            var rounds = parse.GetValue(option: repeats);
            var waiting = parse.GetValue(option: wait);
            var settling = parse.GetValue(option: settle);
            var bound = parse.GetValue(option: timeout);
            var budget = parse.GetValue(option: window);

            if ((rounds is < 1 or > 100) || (waiting < 0) || (settling < 0) || (bound is < 1 or > 7200) || (budget is < 1 or > 7200)) {
                return CliExit.Refuse(verb: "measure run", what: "limits", why: "repeats must be 1..100, wait/settle nonnegative, timeout/window 1..7200 seconds.");
            }
            var checkout = RepositoryPaths.RequireRoot();
            var queue = await QueueAsync(checkout: checkout, token: token).ConfigureAwait(continueOnCapturedContext: false);

            Directory.CreateDirectory(path: queue);
            using var runner = FileLock.TryAcquire(path: Path.Combine(path1: queue, path2: "run.guard"));

            if (runner is null) { return CliExit.Refuse(verb: "measure run", what: "queue", why: "another measurement window is already draining it."); }
            var selected = (parse.GetValue(argument: ids) ?? []);
            var pending = ReadQueue(queue: queue).Where(predicate: row => ((row.Item.Report is null) && ((selected.Length == 0)
                ? (row.Item.Failure is null) : selected.Contains(row.Item.Id, StringComparer.Ordinal))))
                .Select(selector: row => (row.Path, Item: row.Item with { Failure = null })).ToArray();

            if (selected.Any(predicate: id => !pending.Any(predicate: row => (row.Item.Id == id)))) { return CliExit.Refuse(verb: "measure run", what: "requests", why: "an ID is missing or already answered."); }
            if (pending.Length == 0) { Console.WriteLine(value: "measure: no pending questions."); return 0; }
            var leases = await CliLease.RootAsync(cancellationToken: token, checkout: checkout).ConfigureAwait(continueOnCapturedContext: false);
            using var deadline = new OperationDeadline(TimeSpan.FromSeconds(seconds: budget), clock, token);

            try {
                using var quiet = await CliLease.AcquireQuietAsync(leases, parse.GetValue(option: holder)!, TimeSpan.FromSeconds(seconds: waiting), clock, checkout, deadline.Token, ProcessInspection.IsAncestor).ConfigureAwait(continueOnCapturedContext: false);

                if ((settling > 0) && (await settleWait(TimeSpan.FromSeconds(seconds: settling), TimeSpan.FromSeconds(value: (waiting + ((double)settling))), Console.Error, deadline.Token).ConfigureAwait(continueOnCapturedContext: false) is { } reason)) {
                    return CliExit.Refuse(verb: "measure run", what: "settle", why: reason);
                }
                return await DrainAsync(pending: pending, rounds: rounds, sampleAsync: (item, side, round, directory, cancellation) => SampleAsync(directory: directory, item: item, round: round, side: side, timeout: bound, token: cancellation), token: token, windowDeadline: deadline).ConfigureAwait(continueOnCapturedContext: false);
            } catch (OperationCanceledException) when (deadline.IsExpired && !token.IsCancellationRequested) {
                return CliExit.Success;
            } finally {
                // The quiet hold is disposed before announcing what the next window can pick up.
                if (deadline.IsExpired) { Console.WriteLine(value: "measure: window deadline reached; quiet priority released."); }
                var left = ReadQueue(queue: queue).Where(row => pending.Any(p => p.Item.Id == row.Item.Id) && row.Item.Report is null && row.Item.Failure is null).ToArray();
                Console.WriteLine(value: $"measure: {left.Length} request(s) left pending.");
                foreach (var (_, item) in left) { Console.WriteLine(value: $"measure: pending {item.Id} {item.Name}"); }
            }
        });
        run.Detail(detail: "Use: puck measure run [request-ids...] [--window-seconds 600] [--repeats 5] [--wait 600] [--settle 30]\nTakes one priority quiet lease for the pending snapshot. The window deadline includes acquisition and settling; expiry stops the active sample through process cleanup, releases quiet priority and lists pending requests. Incomplete requests stay pending with partial evidence retained for inspection; the next window retries them from the first pair. Each request runs before, after in repeated pairs. Failed commands or missing/nonfinite metrics mark that request failed and continue the queue; partial evidence stays. Explicitly name failed IDs to retry unchanged frozen inputs into a new answer directory; an unselected drain skips failures. Re-file to correct inputs. Reports record raw samples, median and IQR for before, after and paired after-minus-before differences. --decides remains a human criterion.");
        return new Command(description: "Queue frozen before/after timing questions and answer them in a priority quiet window.", name: "measure") { request, list, run };
    }
    // One request owns its failure boundary: only a complete set of paired samples can publish an answer.
    public static async Task<int> DrainAsync(IEnumerable<(string Path, MeasurementRequest Item)> pending, int rounds,
        Func<MeasurementRequest, string, int, string, CancellationToken, Task<MeasurementSample>> sampleAsync, CancellationToken token, OperationDeadline windowDeadline) {
        var failed = false;

        foreach (var (path, item) in pending) {
            token.ThrowIfCancellationRequested();
            if (windowDeadline.IsExpired) { break; }
            var directory = Path.Combine(path1: Path.GetDirectoryName(path: path)!, path2: "answers", path3: Guid.NewGuid().ToString(format: "N"));

            Directory.CreateDirectory(path: directory);
            var samples = new List<MeasurementSample>();
            var report = Path.Combine(path1: directory, path2: "report.json");

            try {
                for (var round = 0; (round < rounds); round++) {
                    foreach (var side in new[] { "before", "after" }) {
                        windowDeadline.Token.ThrowIfCancellationRequested();
                        var sample = await sampleAsync(item, side, round, directory, windowDeadline.Token).ConfigureAwait(continueOnCapturedContext: false);

                        samples.Add(item: sample);
                        Write(report, new MeasurementReport(item, rounds, samples.ToArray(), []), MeasureJsonContext.Default.MeasurementReport);
                    }
                }
                windowDeadline.Token.ThrowIfCancellationRequested();
                var summaries = item.Metrics.Select(selector: metric => Summarize(metric,
                    samples.Where(predicate: sample => (sample.Side == "before")).Select(selector: sample => sample.Metrics[metric]).ToArray(),
                    samples.Where(predicate: sample => (sample.Side == "after")).Select(selector: sample => sample.Metrics[metric]).ToArray())).ToArray();

                Write(report, new MeasurementReport(item, rounds, samples.ToArray(), summaries), MeasureJsonContext.Default.MeasurementReport);
                foreach (var metric in summaries) {
                    Console.WriteLine(value: string.Create(CultureInfo.InvariantCulture, $"measure {item.Name} {metric.Metric}: before median={metric.Before.Median:G6} IQR={metric.Before.Iqr:G6}; after median={metric.After.Median:G6} IQR={metric.After.Iqr:G6}; paired difference median={metric.Difference.Median:G6} IQR={metric.Difference.Iqr:G6}"));
                }
                Write(path, item with { Report = PuckPaths.Normalize(path: report) }, MeasureJsonContext.Default.MeasurementRequest);
                Console.WriteLine(value: $"measure: answered {item.Id}; {item.Decides}; report {PuckPaths.Normalize(path: report)}");
            } catch (OperationCanceledException) when (windowDeadline.IsExpired && !token.IsCancellationRequested) {
                Write(report, new MeasurementReport(item, rounds, samples.ToArray(), [], "Window deadline reached; request remains pending."), MeasureJsonContext.Default.MeasurementReport);
                Write(path, item, MeasureJsonContext.Default.MeasurementRequest);
                break;
            } catch (Exception error) when ((error is InvalidDataException or InvalidOperationException or ArgumentException or IOException)) {
                failed = true;
                var reason = $"measure {item.Name} ({item.Id}): {error.Message}";

                Write(report, new MeasurementReport(item, rounds, samples.ToArray(), [], reason), MeasureJsonContext.Default.MeasurementReport);
                Write(path, item with { Failure = reason }, MeasureJsonContext.Default.MeasurementRequest);
                Console.Error.WriteLine(value: $"measure: failed {reason}; evidence {PuckPaths.Normalize(path: report)}");
            }
        }
        return (failed ? CliExit.Refused : CliExit.Success);
    }

    private static async Task<string> QueueAsync(string checkout, CancellationToken token) => Path.Combine(path1: Path.GetDirectoryName(path: await CliLease.RootAsync(cancellationToken: token, checkout: checkout).ConfigureAwait(continueOnCapturedContext: false))!, path2: "scratch/measure");
    private static IEnumerable<(string Path, MeasurementRequest Item)> ReadQueue(string queue) => (!Directory.Exists(path: queue) ? [] :
        Directory.EnumerateDirectories(path: queue).Select(selector: directory => Path.Combine(path1: directory, path2: "request.json")).Where(predicate: File.Exists)
            .Select(path => (Path: path, Item: (JsonSerializer.Deserialize(File.ReadAllText(path: path), MeasureJsonContext.Default.MeasurementRequest) ?? throw new InvalidDataException(message: $"Empty request: {path}"))))
            .OrderBy(row => row.Item.Created).ThenBy(row => row.Item.Id, StringComparer.Ordinal));
    private static MeasurementSide Freeze(string checkout, string directory, string side, string? artifact, string? commit, string? inputTree) {
        if ((artifact is null) == (commit is null)) { throw new ArgumentException(message: $"Supply exactly one of --{side}-world-artifact and --{side}-commit."); }
        if ((commit is null) == (inputTree is null)) { throw new ArgumentException(message: $"An artifact side requires --{side}-input-tree; a commit side uses that commit's tree."); }
        var tree = FreezeTree(checkout, Path.Combine(path1: directory, path2: side, path3: "tree"), commit, inputTree);

        if (commit is not null) {
            if (!CliGit.TryResolveCommit(repository: checkout, resolved: out var revision, revision: commit)) { throw new ArgumentException(message: $"No commit {commit}."); }
            if (!WorldArtifactBuild.TryResolve("measure request", tree, new WorldArtifactStore(root: Path.Combine(path1: Path.GetDirectoryName(path: directory)!, path2: "build-store")), null,
                directory, TimeSpan.FromMinutes(minutes: 30), out var built, out var error,
                leaseRoot: () => CliLease.RootAsync(checkout).GetAwaiter().GetResult(), spirv: true)) { throw new InvalidOperationException(message: error); }
            using (built) { return new(CopyBuild(built.Path, directory, side), tree, revision); }
        }
        // Retain a managed-store input against pruning while copying it.
        if (!WorldArtifactBuild.TryResolveNamed(artifact, "measure request", checkout, directory, TimeSpan.FromMinutes(minutes: 30), out var source, out var lease, out var refusal)) { throw new ArgumentException(message: refusal); }
        using (lease) { return new(CopyBuild(directory: directory, side: side, source: source), tree, null); }
    }

    public static string FreezeTree(string checkout, string target, string? commit, string? inputTree) {
        if ((commit is null) == (inputTree is null)) { throw new ArgumentException(message: "Supply one commit or input tree."); }
        if (commit is not null) {
            if (!CliGit.TryResolveCommit(repository: checkout, resolved: out var revision, revision: commit)) { throw new ArgumentException(message: $"No commit {commit}."); }
            using var export = AffectedRevisionExport.Create(checkout, revision, ["."]);

            CliFiles.CopyDirectory(export.Root, target);
        } else {
            var source = PuckPaths.Normalize(path: inputTree!);

            if (!Directory.Exists(path: source)) { throw new ArgumentException(message: $"Input tree {source} does not exist."); }
            if (CliGit.HasOwnGitEntry(repositoryRoot: source)) {
                if (!CliGit.TryListFiles(paths: out var paths, reason: out var reason, repository: source)) { throw new InvalidOperationException(message: $"Cannot freeze input tree {source}: {reason}"); }
                CliFiles.CopyDirectory(source, target, paths.Distinct(comparer: StringComparer.Ordinal).Select(selector: p => Path.Combine(path1: source, path2: p)).Where(predicate: File.Exists));
            } else {
                if (ConfinedDirectory.Contains(root: source, path: target)) { throw new ArgumentException(message: "A standalone input tree cannot contain its frozen destination."); }
                CliFiles.CopyDirectory(source, target);
            }
        }
        return PuckPaths.Normalize(path: Path.GetFullPath(path: target));
    }
    public static string[] FreezeCommand(string checkout, string[] arguments) => arguments.Select(selector: argument => {
        var equals = (argument.StartsWith(comparisonType: StringComparison.Ordinal, value: "--") ? argument.IndexOf(value: '=') : -1);
        var value = ((equals >= 0) ? argument[(equals + 1)..] : argument);

        if (Path.IsPathRooted(path: value)) {
            if (!PuckPaths.IsWithin(path: value, root: checkout)) { throw new ArgumentException(message: $"Input {PuckPaths.Normalize(path: value)} is outside the requesting checkout; place inputs in the declared tree and use relative paths."); }
            value = Path.GetRelativePath(path: value, relativeTo: checkout).Replace(newChar: '/', oldChar: Path.DirectorySeparatorChar);
        } else if (!value.StartsWith(comparisonType: StringComparison.Ordinal, value: "--") && !PuckPaths.IsWithin(checkout, Path.GetFullPath(basePath: checkout, path: value))) {
            throw new ArgumentException(message: $"Input {value} escapes the frozen input tree.");
        }
        return ((equals >= 0) ? (argument[..(equals + 1)] + value) : value);
    }).ToArray();

    private static string CopyBuild(string source, string directory, string side) {
        var target = Path.Combine(path1: directory, path2: side, path3: "build");

        CliFiles.CopyDirectory(Path.GetDirectoryName(path: Path.GetFullPath(path: source))!, target);
        return PuckPaths.Normalize(path: Path.Combine(path1: target, path2: Path.GetFileName(path: source)));
    }
    private static void ValidateCommand(string[] arguments) {
        if (!(arguments is ["bench", "world", ..] or ["bench", "startup", ..] or ["bench", "frames", ..] or ["sweep", ..])) { throw new ArgumentException(message: "Measurement commands are bench world, bench startup, bench frames or sweep (including headless frame-signal plans)."); }
        if (arguments.Any(predicate: static value => ((value is "--world-artifact" or "--output") || value.StartsWith(comparisonType: StringComparison.Ordinal, value: "--world-artifact=") || value.StartsWith(comparisonType: StringComparison.Ordinal, value: "--output=")))) {
            throw new ArgumentException(message: "The measurement runner supplies --world-artifact and --output.");
        }
    }
    private static async Task<MeasurementSample> SampleAsync(MeasurementRequest item, string side, int round, string directory, int timeout, CancellationToken token) {
        var output = Path.Combine(path1: directory, path2: $"{round:D3}-{side}");

        Directory.CreateDirectory(path: output);
        var cli = typeof(MeasureCommand).Assembly.Location;
        var frozen = ((side == "before") ? item.Before : item.After);
        var run = await CliProcess.RunCapturedAsync("dotnet", [cli, .. item.Command, "--world-artifact", frozen.Artifact, "--output", output], input: string.Empty,
            workingDirectory: frozen.Tree, timeout: TimeSpan.FromSeconds(seconds: timeout), cancellationToken: token, started: process => {
                ChildProcessJob.Assign(process: process);
                Console.Error.WriteLine(value: $"measure: {item.Name} {side} #{(round + 1)} process {process.Id}");
            }).ConfigureAwait(continueOnCapturedContext: false);

        File.WriteAllText(Path.Combine(path1: output, path2: "command.stdout.log"), run.Stdout);
        File.WriteAllText(Path.Combine(path1: output, path2: "command.stderr.log"), run.Stderr);
        if ((run.ExitCode != 0) || run.TimedOut) { throw new InvalidOperationException(message: $"measure {item.Name} {side} #{(round + 1)}: command failed ({run.ExitCode}, timedOut={run.TimedOut}); {PuckPaths.Normalize(path: output)}"); }
        var reports = Directory.GetFiles(path: output, searchOption: SearchOption.AllDirectories, searchPattern: "report.json");

        if (reports.Length != 1) { throw new InvalidDataException(message: $"Expected one command report under {PuckPaths.Normalize(path: output)}; found {reports.Length}."); }
        using var json = JsonDocument.Parse(File.ReadAllText(path: reports[0]));
        var readings = item.Metrics.ToDictionary(metric => metric, metric => ReadMetric(json.RootElement, metric), StringComparer.Ordinal);

        Console.WriteLine(value: $"measure: {item.Name} {side} #{(round + 1)} read {readings.Count} metric(s).");
        return new(side, round, readings, PuckPaths.Normalize(path: reports[0]));
    }

    public static double ReadMetric(JsonElement root, string pointer) {
        var value = root;
        var resolved = pointer.StartsWith(value: '/');

        foreach (var part in pointer.Split('/').Skip(count: 1)) {
            var key = part.Replace(comparisonType: StringComparison.Ordinal, newValue: "/", oldValue: "~1").Replace(comparisonType: StringComparison.Ordinal, newValue: "~", oldValue: "~0");

            if ((value.ValueKind == JsonValueKind.Object) && value.TryGetProperty(propertyName: key, value: out var child)) { value = child; } else if ((value.ValueKind == JsonValueKind.Array) && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && (index < value.GetArrayLength())) { value = value[index]; } else { resolved = false; break; }
        }
        if (resolved && TryNumber(number: out var result, value: value)) { return result; }
        var schema = (root.TryGetProperty(propertyName: "schema", value: out var format) ? format.GetString() : "unspecified");

        throw new InvalidDataException(message: $"Metric {pointer} is missing or not finite numeric data in report schema {schema}; available pointers: {string.Join(separator: ", ", values: NumericPointers(root))}.");
    }

    private static bool TryNumber(JsonElement value, out double number) {
        number = double.NaN;
        return ((((value.ValueKind == JsonValueKind.Number) && value.TryGetDouble(value: out number)) ||
            ((value.ValueKind == JsonValueKind.String) && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))) && double.IsFinite(d: number));
    }
    private static IEnumerable<string> NumericPointers(JsonElement value, string path = "") {
        if (TryNumber(number: out _, value: value)) { yield return path; } else if (value.ValueKind == JsonValueKind.Object) {
            foreach (var field in value.EnumerateObject()) {
                foreach (var pointer in NumericPointers(field.Value, ((path + "/") + Escape(key: field.Name)))) { yield return pointer; }
            }
        } else if (value.ValueKind == JsonValueKind.Array) {
            var index = 0;

            foreach (var child in value.EnumerateArray()) {
                foreach (var pointer in NumericPointers(child, ((path + "/") + index.ToString(provider: CultureInfo.InvariantCulture)))) { yield return pointer; }
                index++;
            }
        }
    }
    private static string Escape(string key) => key.Replace(comparisonType: StringComparison.Ordinal, newValue: "~0", oldValue: "~").Replace(comparisonType: StringComparison.Ordinal, newValue: "~1", oldValue: "/");

    public static void ValidateMetric(string[] command, string pointer) {
        ValidateCommand(arguments: command);
        var parts = pointer.Split('/');

        if (command[0] == "bench") {
            var startupMetric = (command.Contains("--bake", StringComparer.Ordinal) ? "cold-bake" : "startup");
            // Dictionary keys are authored/runtime metric names. Derive numeric members from the report's serializer.
            var report = BenchReport.Start("schema", new BenchBuild(Path: "", Sha256: "", Version: ""), 1, new Dictionary<string, string>()) with {
                Metrics = ((command[1] == "frames") ? FrameBenchmarks.MetricSchema(arguments: command[2..]) :
                    new Dictionary<string, BenchMetric> { [((command[1] == "startup") ? startupMetric : "<name>")] = BenchMetric.Of(samples: [0], unit: "ms") }),
            };
            using var json = JsonDocument.Parse(report.ToJson());
            var available = NumericPointers(json.RootElement).Where(predicate: p => p.StartsWith(comparisonType: StringComparison.Ordinal, value: "/metrics/")).ToArray();
            var canonical = ((command[1] == "frames") ? pointer : ((parts.Length >= 3) ? ((("/metrics/" + ((command[1] == "world") ? "<name>" : parts[2])) + "/") + string.Join(separator: '/', values: parts.Skip(count: 3).Select(selector: p => (int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? "0" : p)))) : ""));

            if ((parts.Length >= 4) && (parts[0] == "") && (parts[1] == "metrics") && (parts[2].Length > 0) &&
                ((command[1] != "startup") || (parts[2] == startupMetric)) && available.Contains(canonical, StringComparer.Ordinal)) { return; }
            throw new InvalidDataException(message: $"Metric {pointer} is not numeric data in report schema {BenchReport.SchemaName}; available pointers: {string.Join(", ", available)}.");
        }
        if ((parts is ["", "signals", var index, "metrics", var key]) && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out _) && (key.Length > 0)) { return; }
        throw new InvalidDataException(message: $"Metric {pointer} is not a signal metric in report schema {Puck.Cli.Sweep.SweepReport.SchemaName}; available pointers: /signals/<index>/metrics/<name> (numeric readings from the plan).");
    }
    public static MeasurementMetric Summarize(string metric, double[] before, double[] after) {
        if ((before.Length == 0) || (before.Length != after.Length) || before.Concat(second: after).Any(predicate: value => !double.IsFinite(d: value))) { throw new ArgumentException(message: "Paired finite samples are required."); }
        return new(metric, Distribution(samples: before), Distribution(samples: after), Distribution(samples: before.Zip(resultSelector: (a, b) => (b - a), second: after).ToArray()));
    }

    private static MeasurementDistribution Distribution(double[] samples) {
        var ordered = samples.Order().ToArray();

        double Quantile(double fraction) => Puck.Cli.Bench.BenchMetric.Quantile(fraction: fraction, ordered: ordered);
        return new(Quantile(fraction: .5), (Quantile(fraction: .75) - Quantile(fraction: .25)));
    }
    private static void Write<T>(string path, T item, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) {
        Puck.Confinement.AtomicFile.WriteAllText(path: path, contents: (JsonSerializer.Serialize(jsonTypeInfo: type, value: item) + "\n"));
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
