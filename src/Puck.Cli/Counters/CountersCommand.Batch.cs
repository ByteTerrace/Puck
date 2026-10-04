using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Puck.Abstractions;
using Puck.World;

namespace Puck.Cli.Counters;

internal sealed record CollectedCountersObservation(PreparedCountersGroup Group, PreparedCountersObservation Observation,
    string Backend, IndexedCountersReading Reading, int CompletionLine, string Completion);
internal sealed record CountersBatchEvidence(string Manifest, string ManifestHash, WorldCountersRevision Revision,
    IReadOnlyList<CountersBatchObservationEvidence> Observations);
internal sealed record CountersBatchObservationEvidence(string Name, string Group, string Backend, string Workload,
    string Prelude, string PreludeHash, string Script, string ScriptHash, int Ordinal, ulong Tick, string Method,
    string Transcript, int ResponseLine, string CompletionTranscript, int CompletionLine, string Completion, string Report);

internal static partial class CountersCommand {
    private static readonly TimeSpan BatchBudget = TimeSpan.FromMinutes(15);

    private static int RunBatch(string manifestPath, string? output, bool check, bool record, IReadOnlyList<string?> conflicts) {
        var clock = Stopwatch.StartNew();
        if (conflicts.Any(value => value is not null) || (check && record)) {
            return CliExit.Refuse(Verb, "--batch", "a batch owns its workload, scripts and ceilings; --world, --script, --report and --ceilings refuse beside it, and --check and --record cannot be combined");
        }
        if (!CliPaths.TryGetRepositoryRoot(out var repositoryRoot)) { return CliExit.Refused; }
        using var scratch = RunDirectory.Create(prefix: "puck-counters-batch-");
        var products = output is null ? scratch.Path : Path.GetFullPath(output);
        Console.Error.WriteLine($"{Verb}: batch artifacts {CliPaths.ToDisplay(scratch.Path)}");
        try {
            var batch = CountersBatchInput.Read(manifestPath, products);
            var checks = new Dictionary<string, WorldCountersCeilings>(PuckPaths.Comparer);
            foreach (var observation in batch.Groups.SelectMany(group => group.Observations)) {
                WorldCountersCeilings? existing = null;
                if ((check || (record && File.Exists(observation.CeilingsPath)))
                    && !CountersCeilings.TryRead(observation.CeilingsPath, out existing, out var reason)) {
                    return CliExit.Refuse(Verb, observation.CeilingsPath, reason);
                }
                if (check) { checks.Add(observation.CeilingsPath, existing!); }
            }
            if (!Puck.Cli.Determinism.DeterminismRecorder.TryLoadWorld(batch.WorldPath, out var authored, out _, out var error)) {
                return CliExit.Refuse(Verb, "the batch workload", error.ReplaceLineEndings(" "));
            }
            var width = authored!.Host.Width;
            var height = authored.Host.Height;
            var compiler = new Puck.Shaders.ShaderToolchain().Identity;
            if (!WorldOffscreenLeg.TryResolveWorld(Verb, repositoryRoot, scratch.Path,
                CliProcess.RemainingBudget(clock, BatchBudget), out var artifact)) { return CliExit.Refused; }
            using var lease = artifact;
            var revision = new WorldCountersRevision(CliGit.TryResolveCommit(repositoryRoot, "HEAD", out var commit) ? commit : "unknown", artifact.Key);
            if (!TryCollectBatch(batch, width, height, compiler, (group, backend) => {
                var directory = Path.Combine(scratch.Path, group.Name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "executed.script.txt"), group.Script, Utf8);
                var exit = WorldOffscreenLeg.Run(Verb, artifact.Path, batch.WorldPath, backend, group.Script, [], directory,
                    exitAfterSeconds: 900, budget: BatchBudget, suiteClock: clock, process: out var process);
                return exit == CliExit.Success ? process : null;
            }, out var collected, out var collectionReason)) {
                return CliExit.Refuse(Verb, "the batch observations", collectionReason);
            }
            var evidence = new List<CountersBatchObservationEvidence>();
            var reports = new List<(PreparedCountersObservation Observation, WorldCountersReport Report)>();
            foreach (var group in batch.Groups) {
                foreach (var observation in group.Observations) {
                    if (CliProcess.RemainingBudget(clock, BatchBudget) <= TimeSpan.Zero) {
                        return CliExit.Refuse(Verb, "--batch", "the fifteen-minute phase cap was exhausted before all products were written");
                    }
                    var pair = collected.Where(item => ReferenceEquals(item.Observation, observation)).ToArray();
                    var report = new WorldCountersReport(revision,
                        CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: batch.WorldPath),
                        CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: observation.ScriptPath),
                        pair.Select(item => item.Reading.Run).ToArray());
                    Directory.CreateDirectory(Path.GetDirectoryName(observation.ReportPath)!);
                    WriteReport(observation.ReportPath, report);
                    reports.Add((observation, report));
                    foreach (var item in pair) {
                        var legReport = Path.Combine(products, "observations", $"{observation.Definition.Name}-{item.Backend}.json");
                        Directory.CreateDirectory(Path.GetDirectoryName(legReport)!);
                        WriteReport(legReport, report with { Runs = [item.Reading.Run] });
                        evidence.Add(new CountersBatchObservationEvidence(observation.Definition.Name, group.Name, item.Backend,
                            report.Workload, CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: group.PreludePath), Hash(group.Prelude),
                            report.Script, Hash(observation.Script), item.Reading.Ordinal, item.Reading.Tick, item.Reading.Method,
                            CliPaths.ToDisplay(Path.Combine(scratch.Path, group.Name, $"{item.Backend}-stdout.log")),
                            item.Reading.Line, CliPaths.ToDisplay(Path.Combine(scratch.Path, group.Name, $"{item.Backend}-stderr.log")),
                            item.CompletionLine, item.Completion, CliPaths.ToDisplay(legReport)));
                    }
                    Console.Out.WriteLine($"{Verb}: report {CliPaths.ToDisplay(observation.ReportPath)}");
                }
            }
            var evidencePath = Path.Combine(products, "batch.observations.json");
            File.WriteAllText(evidencePath, JsonSerializer.Serialize(new CountersBatchEvidence(
                CliPaths.ToDisplay(relativeTo: repositoryRoot, fullPath: Path.GetFullPath(manifestPath)), batch.ManifestHash, revision, evidence), CountersBatchInput.Json), Utf8);
            Console.Out.WriteLine($"{Verb}: {evidence.Count} backend observations, {reports.Count} paired reports; provenance {CliPaths.ToDisplay(evidencePath)}");
            // A mismatch anywhere keeps all actual reports but records no batch ceilings.
            var differences = reports.SelectMany(item => CountersComparison.AcrossBackends(item.Report.Runs[0], item.Report.Runs[1])).ToArray();
            if (differences.Length != 0) { return Report(differences); }
            var result = CliExit.Success;
            foreach (var (observation, report) in reports) {
                if (CliProcess.RemainingBudget(clock, BatchBudget) <= TimeSpan.Zero) {
                    return CliExit.Refuse(Verb, "--batch", "the fifteen-minute phase cap was exhausted before all reports were judged");
                }
                var verdict = Judge(report, check ? checks[observation.CeilingsPath] : null, observation.CeilingsPath, record);
                if (verdict != CliExit.Success) { result = verdict; }
            }
            return result;
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or FormatException or ArgumentException or InvalidOperationException or KeyNotFoundException) {
            return CliExit.Refuse(Verb, "--batch", exception.Message.ReplaceLineEndings(" "));
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
        if (batch.Groups.Count == 0 || batch.Groups.Any(group => group.Observations.Count == 0)) {
            reason = "a batch must contain nonempty observation groups";
            return false;
        }
        var collected = new List<CollectedCountersObservation>();
        foreach (var group in batch.Groups) {
            foreach (var backend in WorldOffscreenLeg.Backends) {
                var process = runLeg(group, backend);
                if (process is null || process.TimedOut || process.ExitCode != 0) {
                    reason = $"group {group.Name} on {backend} did not complete successfully; retained transcripts are diagnostic evidence, not counters products";
                    return false;
                }
                var stdout = process.OutputLines.Where(line => line.Stream == CliProcessOutputStream.Stdout)
                    .Select(line => line.Line).ToArray();
                var stderr = process.OutputLines.Where(line => line.Stream == CliProcessOutputStream.Stderr)
                    .Select(line => line.Line).ToArray();
                var completions = stderr.Select((line, index) => (Line: line, Number: index + 1))
                    .Where(item => item.Line.StartsWith("[indirect: settled at tick ", StringComparison.Ordinal)).ToArray();
                if (completions.Length != group.Observations.Count
                    || stderr.Any(line => line.StartsWith("[indirect: not settled", StringComparison.Ordinal))) {
                    reason = $"group {group.Name} on {backend}: expected {group.Observations.Count} current-source indirect completion verdicts, found {completions.Length}";
                    return false;
                }
                if (!CountersReading.TryReadIndexed(stdout, group.Observations.Select(item => item.Definition.Method).ToArray(),
                    backend, width, height, compiler, out var readings, out reason)) {
                    reason = $"group {group.Name} on {backend}: {reason}";
                    return false;
                }
                for (var index = 0; index < readings.Count; index++) {
                    if (!CountersReading.TryReadIndirectCompletion(completions[index].Line, out var warmTick)
                        || warmTick > ulong.MaxValue - 120 || readings[index].Tick < warmTick + 120) {
                        reason = $"group {group.Name} on {backend}: observation {index + 1} lacks a trustworthy fenced warm-up before its active input";
                        return false;
                    }
                    collected.Add(new CollectedCountersObservation(group, group.Observations[index], backend, readings[index],
                        completions[index].Number, completions[index].Line));
                }
            }
        }
        observations = collected;
        reason = string.Empty;
        return true;
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
