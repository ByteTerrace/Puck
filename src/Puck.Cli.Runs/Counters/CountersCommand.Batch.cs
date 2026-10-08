using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Puck.Abstractions;
using Puck.World;

namespace Puck.Cli.Counters;

public sealed record CollectedCountersObservation(PreparedCountersGroup Group, PreparedCountersObservation Observation,
    string Backend, IndexedCountersReading Reading, int CompletionLine, string Completion);

internal sealed record CountersBatchEvidence(string Manifest, string ManifestHash, WorldCountersRevision Revision,
    IReadOnlyList<CountersBatchObservationEvidence> Observations);
internal sealed record CountersBatchObservationEvidence(string Name, string Group, string Backend, string Workload,
    string Prelude, string PreludeHash, string Script, string ScriptHash, int Ordinal, ulong Tick, string Method,
    string Transcript, int ResponseLine, string CompletionTranscript, int CompletionLine, string Completion, string Report);

public static partial class CountersCommand {
    private static readonly TimeSpan BatchBudget = TimeSpan.FromMinutes(minutes: 15);

    private static int RunBatch(string manifestPath, string? output, bool check, bool record, IReadOnlyList<string?> conflicts) {
        var clock = Stopwatch.StartNew();

        if (conflicts.Any(predicate: value => (value is not null)) || (check && record)) {
            return CliExit.Refuse(verb: Verb, what: "--batch", why: "a batch owns its workload, scripts and ceilings; --world, --script, --report and --ceilings refuse beside it, and --check and --record cannot be combined");
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) { return CliExit.Refused; }
        using var scratch = RunDirectory.Create(prefix: "puck-counters-batch-");
        var products = ((output is null) ? scratch.Path : Path.GetFullPath(path: output));

        Console.Error.WriteLine(value: $"{Verb}: batch artifacts {CliPaths.ToDisplay(fullPath: scratch.Path)}");
        try {
            var batch = CountersBatchInput.Read(output: products, path: manifestPath);
            var checks = new Dictionary<string, WorldCountersCeilings>(comparer: PuckPaths.Comparer);

            foreach (var observation in batch.Groups.SelectMany(selector: group => group.Observations)) {
                WorldCountersCeilings? existing = null;

                if ((check || (record && File.Exists(path: observation.CeilingsPath)))
                    && !CountersCeilings.TryRead(observation.CeilingsPath, out existing, out var reason)) {
                    return CliExit.Refuse(verb: Verb, what: observation.CeilingsPath, why: reason);
                }
                if (check) { checks.Add(key: observation.CeilingsPath, value: existing!); }
            }
            if (!Puck.Cli.Determinism.DeterminismRecorder.TryLoadWorld(batch.WorldPath, out var authored, out _, out var error)) {
                return CliExit.Refuse(verb: Verb, what: "the batch workload", why: error.ReplaceLineEndings(replacementText: " "));
            }
            var width = authored!.Host.Width;
            var height = authored.Host.Height;
            var compiler = new Puck.Shaders.ShaderToolchain().Identity;

            if (!WorldOffscreenLeg.TryResolveWorld(Verb, repositoryRoot, scratch.Path,
                CliProcess.RemainingBudget(budget: BatchBudget, clock: clock), out var artifact)) { return CliExit.Refused; }
            using var lease = artifact;
            var revision = new WorldCountersRevision(Commit: (CliGit.TryResolveCommit(repository: repositoryRoot, resolved: out var commit, revision: "HEAD") ? commit : "unknown"), SourceState: artifact.Key);

            if (!TryCollectBatch(batch, width, height, compiler, (group, backend) => {
                var directory = Path.Combine(path1: scratch.Path, path2: group.Name);

                Directory.CreateDirectory(path: directory);
                File.WriteAllText(Path.Combine(path1: directory, path2: "executed.script.txt"), group.Script, Utf8);
                var exit = WorldOffscreenLeg.Run(Verb, artifact.Path, batch.WorldPath, backend, group.Script, [], directory,
                    exitAfterSeconds: 900, budget: BatchBudget, suiteClock: clock, process: out var process);

                return ((exit == CliExit.Success) ? process : null);
            }, out var collected, out var collectionReason)) {
                return CliExit.Refuse(verb: Verb, what: "the batch observations", why: collectionReason);
            }
            var evidence = new List<CountersBatchObservationEvidence>();
            var reports = new List<(PreparedCountersObservation Observation, WorldCountersReport Report)>();

            foreach (var group in batch.Groups) {
                foreach (var observation in group.Observations) {
                    if (CliProcess.RemainingBudget(budget: BatchBudget, clock: clock) <= TimeSpan.Zero) {
                        return CliExit.Refuse(verb: Verb, what: "--batch", why: "the fifteen-minute phase cap was exhausted before all products were written");
                    }
                    var pair = collected.Where(predicate: item => ReferenceEquals(objA: item.Observation, objB: observation)).ToArray();
                    var report = new WorldCountersReport(revision,
                        CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: batch.WorldPath),
                        CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: observation.ScriptPath),
                        pair.Select(selector: item => item.Reading.Run).ToArray());

                    Directory.CreateDirectory(path: Path.GetDirectoryName(path: observation.ReportPath)!);
                    WriteReport(path: observation.ReportPath, report: report);
                    reports.Add(item: (observation, report));
                    foreach (var item in pair) {
                        var legReport = Path.Combine(path1: products, path2: "observations", path3: $"{observation.Definition.Name}-{item.Backend}.json");

                        Directory.CreateDirectory(path: Path.GetDirectoryName(path: legReport)!);
                        WriteReport(path: legReport, report: report with { Runs = [item.Reading.Run] });
                        evidence.Add(item: new CountersBatchObservationEvidence(observation.Definition.Name, group.Name, item.Backend,
                            report.Workload, CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: group.PreludePath), Hash(text: group.Prelude),
                            report.Script, Hash(text: observation.Script), item.Reading.Ordinal, item.Reading.Tick, item.Reading.Method,
                            CliPaths.ToDisplay(fullPath: Path.Combine(path1: scratch.Path, path2: group.Name, path3: $"{item.Backend}-stdout.log")),
                            item.Reading.Line, CliPaths.ToDisplay(fullPath: Path.Combine(path1: scratch.Path, path2: group.Name, path3: $"{item.Backend}-stderr.log")),
                            item.CompletionLine, item.Completion, CliPaths.ToDisplay(fullPath: legReport)));
                    }
                    Console.Out.WriteLine(value: $"{Verb}: report {CliPaths.ToDisplay(fullPath: observation.ReportPath)}");
                }
            }
            var evidencePath = Path.Combine(path1: products, path2: "batch.observations.json");

            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new CountersBatchEvidence(
                CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: Path.GetFullPath(path: manifestPath)), batch.ManifestHash, revision, evidence), CountersBatchInput.Json), Utf8);
            Console.Out.WriteLine(value: $"{Verb}: {evidence.Count} backend observations, {reports.Count} paired reports; provenance {CliPaths.ToDisplay(fullPath: evidencePath)}");
            // A mismatch anywhere keeps all actual reports but records no batch ceilings.
            var differences = reports.SelectMany(selector: item => CountersComparison.AcrossBackends(left: item.Report.Runs[0], right: item.Report.Runs[1])).ToArray();

            if (differences.Length != 0) { return Report(differences: differences); }
            var result = CliExit.Success;

            foreach (var (observation, report) in reports) {
                if (CliProcess.RemainingBudget(budget: BatchBudget, clock: clock) <= TimeSpan.Zero) {
                    return CliExit.Refuse(verb: Verb, what: "--batch", why: "the fifteen-minute phase cap was exhausted before all reports were judged");
                }
                var verdict = Judge(report, (check ? checks[observation.CeilingsPath] : null), observation.CeilingsPath, record);

                if (verdict != CliExit.Success) { result = verdict; }
            }
            return result;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException
            or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException)) {
            return CliExit.Refuse(verb: Verb, what: "--batch", why: exception.Message.ReplaceLineEndings(replacementText: " "));
        } finally {
            // Transcript paths are part of every observation's provenance, even when outputs were requested elsewhere.
            scratch.Conclude(passed: false);
        }
    }

    /// <summary>Runs each ordered group/backend exactly once and refuses the whole collection if any leg or observation
    /// is unusable. The delegate is the existing offscreen leg boundary; tests supply actual-shaped transcripts.</summary>
    public static bool TryCollectBatch(PreparedCountersBatch batch, int width, int height, string compiler,
        Func<PreparedCountersGroup, string, CliProcessResult?> runLeg,
        [NotNullWhen(true)] out IReadOnlyList<CollectedCountersObservation>? observations, out string reason) {
        observations = null;
        if ((batch.Groups.Count == 0) || batch.Groups.Any(predicate: group => ((group.Observations.Count == 0) || (group.Completion is not ("indirect" or "engine"))))) {
            reason = "a batch must contain nonempty observation groups";
            return false;
        }
        var collected = new List<CollectedCountersObservation>();

        foreach (var group in batch.Groups) {
            foreach (var backend in WorldOffscreenLeg.Backends) {
                var process = runLeg(group, backend);

                if ((process is null) || process.TimedOut || (process.ExitCode != 0)) {
                    reason = $"group {group.Name} on {backend} did not complete successfully; retained transcripts are diagnostic evidence, not counters products";
                    return false;
                }
                var stdout = process.OutputLines.Where(predicate: line => (line.Stream == CliProcessOutputStream.Stdout))
                    .Select(selector: line => line.Line).ToArray();
                var stderr = process.OutputLines.Where(predicate: line => (line.Stream == CliProcessOutputStream.Stderr))
                    .Select(selector: line => line.Line).ToArray();
                var completionPrefix = ((group.Completion == "engine") ? "[engine: ready at tick " : "[indirect: settled at tick ");
                var failurePrefix = ((group.Completion == "engine") ? "[engine: not ready" : "[indirect: not settled");
                var completions = stderr.Select(selector: (line, index) => (Line: line, Number: (index + 1)))
                    .Where(predicate: item => item.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: completionPrefix)).ToArray();

                if ((completions.Length != group.Observations.Count)
                    || stderr.Any(predicate: line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: failurePrefix))) {
                    reason = $"group {group.Name} on {backend}: expected {group.Observations.Count} {group.Completion} completion verdicts, found {completions.Length}";
                    return false;
                }
                if (!CountersReading.TryReadIndexed(stdout, group.Observations.Select(selector: item => item.Definition.Method).ToArray(),
                    backend, width, height, compiler, out var readings, out reason)) {
                    reason = $"group {group.Name} on {backend}: {reason}";
                    return false;
                }
                for (var index = 0; (index < readings.Count); index++) {
                    var completed = ((group.Completion == "engine")
                        ? CountersReading.TryReadEngineCompletion(line: completions[index].Line, tick: out var warmTick)
                        : CountersReading.TryReadIndirectCompletion(line: completions[index].Line, tick: out warmTick));

                    if (!completed || (warmTick > (ulong.MaxValue - 120)) || (readings[index].Tick < (warmTick + 120))) {
                        reason = $"group {group.Name} on {backend}: observation {(index + 1)} lacks its trustworthy {group.Completion} completion before its active input";
                        return false;
                    }
                    collected.Add(item: new CollectedCountersObservation(group, group.Observations[index], backend, readings[index],
                        completions[index].Number, completions[index].Line));
                }
            }
        }
        observations = collected;
        reason = string.Empty;
        return true;
    }

    private static string Hash(string text) => Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: text)));
}
