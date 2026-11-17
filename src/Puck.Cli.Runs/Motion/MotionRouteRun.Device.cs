using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.World;
using Puck.Commands;
using Puck.Cli.Sweep;

namespace Puck.Cli.Motion;

/// <summary>One direct control request's existing typed receipt, kept with its stage and submitted script.</summary>
public sealed record MotionRouteReceipt(string Stage, string Script, SweepAnswer Result);
/// <summary>One fresh route process's evidence, including raw endpoint answers and the actual semantic tick trace.</summary>
public sealed record MotionRouteLeg(string Artifact, string ArtifactSha256, string Directory, bool Recording,
    IReadOnlyDictionary<string, string> BuildFiles, string? BuildSourceKey, string ReplayStatus, string Trace, IReadOnlyDictionary<string, string> Before,
    IReadOnlyDictionary<string, string> After, IReadOnlyList<string> Problems, double ProcessSeconds) {
    /// <summary>The completed presented PNG taken after the measured trace and metric endpoints, or null when absent.</summary>
    public string? PostWindowImage { get; init; }

    public IReadOnlyList<MotionRouteReceipt> DirectReceipts { get; init; } = [];
    public IReadOnlyDictionary<string, string> BeforeChecks { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> AfterChecks { get; init; } = new Dictionary<string, string>();
}
/// <summary>Runs one finite route through the existing World process, script queue and canonical authority tape.</summary>
public static class MotionRouteRun {
    public const string TapeFile = "motion-route.puckreplay";
    public const string TapeName = "motion-route";

    /// <summary>Checks every requested tick/command multiplicity against the complete process receipt stream.</summary>
    public static IReadOnlyList<string> CheckExecutionReceipts(IReadOnlyList<(ulong Tick, string Line)> expected, IReadOnlyList<MotionPinnedAnswer> actual) {
        var problems = new List<string>();

        foreach (var answer in actual) { if (answer.Refused) { problems.Add(item: $"Tick {answer.Tick}: {answer.Line}: {answer.Answer}"); } }
        var expectedRows = expected.GroupBy(keySelector: row => (row.Tick, row.Line)).ToDictionary(group => group.Key, group => group.Count());
        var actualRows = actual.GroupBy(keySelector: row => (row.Tick, row.Line)).ToDictionary(group => group.Key, group => group.Count());

        foreach (var (key, count) in expectedRows) {
            if (actualRows.GetValueOrDefault(key: key) != count) { problems.Add(item: $"Expected {count} execution receipt(s) at tick {key.Tick} for '{key.Line}', found {actualRows.GetValueOrDefault(key: key)}."); }
        }
        foreach (var key in actualRows.Keys) { if (!expectedRows.ContainsKey(key: key)) { problems.Add(item: $"Unexpected execution receipt at tick {key.Tick}: {key.Line}."); } }
        return problems;
    }
    public static MotionRouteLeg Execute(MotionRoutePlan plan, string artifact, string world, string repository,
        string directory, WorldBackendPreference? backend, string? cache, TimeSpan deadline, string? tape, string? buildSourceKey, CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        artifact = Puck.Abstractions.PuckPaths.Slashed(path: artifact);
        directory = Puck.Abstractions.PuckPaths.Slashed(path: directory);
        Directory.CreateDirectory(path: directory);
        // Reusing an output folder must never reuse an authority catalog, machine id or checkpoint.
        var state = Path.Combine(path1: directory, path2: ("state-" + Guid.NewGuid().ToString(format: "N")));

        Directory.CreateDirectory(path: state);
        if (tape is not null) {
            Directory.CreateDirectory(path: Path.Combine(path1: state, path2: "Replays"));
            File.Copy(tape, Path.Combine(path1: state, path2: "Replays", path3: TapeFile), overwrite: true);
        }
        var problems = new List<string>();
        var timer = Stopwatch.StartNew();
        var replayStatus = string.Empty;
        var trace = string.Empty;
        string? postWindowImage = null;
        var recording = (tape is null);
        var before = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var after = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var beforeChecks = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var afterChecks = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var directReceipts = new List<MotionRouteReceipt>();
        var files = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var artifactRoot = Path.GetDirectoryName(path: artifact)!;

        foreach (var file in Directory.EnumerateFiles(path: artifactRoot, searchOption: SearchOption.AllDirectories, searchPattern: "*")
            .Where(predicate: path => ((Path.GetExtension(path: path).ToLowerInvariant() is ".dll" or ".spv" or ".dxil" or ".dxbc" or ".hlsl" or ".hlsli") ||
                path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".runtimeconfig.json") || path.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".deps.json")))
            .Order(comparer: StringComparer.Ordinal)) {
            using var stream = File.OpenRead(path: file);

            files[Puck.Abstractions.PuckPaths.Relative(path: file, relativeTo: artifactRoot)] = Convert.ToHexStringLower(inArray: SHA256.HashData(source: stream));
        }
        var hash = files[Path.GetFileName(path: artifact)];
        WorldProcess started;

        using (var launch = WorldLaunch.Admit(cancellationToken: cancellationToken, request: new WorldLaunchRequest(Artifact: artifact, Step: "motion route") {
            Arguments = ["--paused", "--lockstep", "--budgeted-lighting", "--control", .. ((cache is null) ? Array.Empty<string>() : ["--cache-dir", cache])],
            Backend = backend,
            ExitAfterSeconds = WorldLaunch.Backstop(deadline: deadline),
            Shape = WorldShape.Document,
            StateDirectory = state,
            WorkingDirectory = repository,
            World = world,
        })) {
            started = launch.Start(logDirectory: directory);
        }
        using var process = started;
        using var controlBudget = new SuiteBudget(TimeSpan.FromTicks(value: Math.Max(val1: 1, val2: (deadline - timer.Elapsed).Ticks)), cancellationToken);

        MotionScriptResult Run(params string[] lines) {
            var remaining = (deadline - timer.Elapsed);

            if (remaining <= TimeSpan.Zero) { throw new TimeoutException(message: "The route deadline elapsed."); }
            return process.RunScript(cancellationToken: cancellationToken, lines: lines, timeout: remaining);
        }
        try {
            if (!SweepWorld.TryAttach(process, TimeSpan.FromSeconds(seconds: 120), controlBudget, out var attached, out var attachmentError)) {
                throw new InvalidDataException(message: attachmentError);
            }
            using var control = attached!; // Non-owning: WorldProcess remains the only process lifetime owner.

            SweepAnswer Direct(string stage, string script, int timeoutSeconds = 120) {
                var answer = control.Run(script: script, timeoutSeconds: timeoutSeconds);

                cancellationToken.ThrowIfCancellationRequested();
                directReceipts.Add(item: new(Result: answer, Script: script, Stage: stage));
                if (!answer.Ok || answer.Unmeasured) {
                    throw new InvalidDataException(message: $"{stage}: {script}: {string.Join(separator: "; ", values: answer.Refusals)} {answer.Output}");
                }
                return answer;
            }

            // Record every bootstrap submission before it can reach tick one. During replay, only movement/command
            // inputs are masked: presentation composition/session controls are deliberately reapplied as authored.
            // Setup is for presentation controls; authority mutations belong in the recorded inputSteps.
            if (recording) { _ = Direct("record", $"replay.record {TapeName}"); } else { _ = Direct("restore", $"replay.drive {TapeName} to {(plan.LastTick + 2)}\nworld.rate pause"); }
            if (Run().Tick != 0) { throw new InvalidDataException(message: "Bootstrap did not remain at tick zero."); }

            // The startup flags establish time zero before the first produced frame. No screenshot or capture
            // convergence occurs here or in the route. A readiness failure is a refused workload, not a warm run.
            void Ready() {
                var mark = process.LineCount;
                var ready = Run("world.wait bakes 600", "world.wait ready 600");
                // stdout and stderr have independent pumps. Wait for each readiness narration itself rather than
                // assuming that reading the stdout barrier has already drained the other pipe.
                var baked = process.WaitFor(mark, row => row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[bakes:"), (deadline - timer.Elapsed));
                var rendered = process.WaitFor(mark, row => row.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[engine:"), (deadline - timer.Elapsed));

                if ((ready.Tick != 0) || (baked is null) || (rendered is null) ||
                    !baked.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[bakes: settled") ||
                    !rendered.Text.StartsWith(comparisonType: StringComparison.Ordinal, value: "[engine: ready")) {
                    throw new InvalidDataException(message: "The fresh World did not remain at tick zero with bakes and renderer ready.");
                }
            }
            Ready();
            foreach (var line in plan.Setup) { _ = Direct("setup", line); }
            if (Run().Tick != 0) { throw new InvalidDataException(message: "Route setup advanced the paused authority."); }
            Ready();
            var pinned = new List<(ulong Tick, string Line)>();
            // Hold authority while geometry produced by warmup inputs lands. Both metric endpoints then run in the
            // same tick-command phase; opening reads precede every authored command at the first measured tick.
            pinned.Add(item: (((ulong)plan.WarmupTicks), "world.rate pause"));
            foreach (var metric in plan.Metrics) { pinned.Add(item: ((((ulong)plan.WarmupTicks) + 1UL), metric.Run)); }
            if (recording) { pinned.AddRange(collection: MotionRoutePlan.Expand(steps: plan.InputSteps)); }
            pinned.AddRange(collection: MotionRoutePlan.Expand(steps: plan.PresentationSteps));
            // Keep recording endpoints too: a later replay refusal must not discard the usable workload's
            // counted work and memory observations. Recording and replay timings remain distinct phases.
            foreach (var metric in plan.Metrics) {
                pinned.Add(item: ((((ulong)plan.LastTick) + 1UL), metric.Run));
            }
            foreach (var check in plan.Checks) {
                pinned.Add(item: ((((ulong)plan.LastTick) + 1UL), check.Line));
            }
            // Keep a finite drive alive through the full observed interval so replay.status still exposes its
            // per-tick authoritative verdict. No extra authority step can escape this pause.
            var stop = (plan.LastTick + (recording ? 2 : 1));

            pinned.Add(item: (((ulong)stop), "world.rate pause"));
            var ordered = pinned.OrderBy(keySelector: static row => row.Tick).ToArray();
            // Each atomic one-row ArmLine fits the existing control limits for ordinary route commands. Batch
            // a bounded number outside the measured window; SweepEvidence checks every command's typed receipt.
            // The final process log separately proves execution, without world.at's 256-entry read-back retention.
            foreach (var batch in ordered.Chunk(size: 32)) {
                var script = string.Join(separator: "\n", values: batch.Select(selector: row => MotionPlan.ArmLine(lines: [row], start: 1)));

                _ = Direct("arm", script);
            }
            if (Run().Tick != 0) { throw new InvalidDataException(message: "Arming advanced the paused authority."); }
            _ = Direct("warmup-resume", "world.rate resume");
            _ = Direct("warmup-wait", $"world.wait until {plan.WarmupTicks}", 600);
            if (Run().Tick != ((ulong)plan.WarmupTicks)) { throw new InvalidDataException(message: "The warmup did not stop at its authority boundary."); }
            _ = Direct("warmup-paused", "world.rate pause");
            var resident = Direct(script: "world.wait residency 600", stage: "warmup-residency", timeoutSeconds: 600);

            if (!resident.Output.StartsWith(comparisonType: StringComparison.Ordinal, value: "[residency: settled") || (Run().Tick != ((ulong)plan.WarmupTicks))) {
                throw new InvalidDataException(message: "The warmup did not remain paused with its current geometry resident.");
            }
            foreach (var check in plan.Checks) { beforeChecks[check.Name] = Direct("before-check", check.Line).Output; }
            _ = Direct("trace-begin", $"world.view.trace begin {(plan.WarmupTicks + 1)} {plan.LastTick} {(plan.WarmupTicks + 1)}");
            _ = Direct("measured-resume", "world.rate resume");
            var finished = Run($"world.wait until {stop}");

            if (finished.Tick != ((ulong)stop)) { throw new InvalidDataException(message: $"The route ended at tick {finished.Tick}, not {stop}."); }
            if (recording) {
                trace = Direct("trace-read", "world.view.trace --json").Output;
                var stopped = Direct("record-stop", "replay.stop");

                if (!stopped.Output.Contains(comparisonType: StringComparison.Ordinal, value: "faithful capture") ||
                    stopped.Output.Contains(comparisonType: StringComparison.Ordinal, value: "MISMATCH")) {
                    problems.Add(item: "The original recorded authority did not reproduce at replay.stop.");
                }
                var generated = Path.Combine(path1: state, path2: "Replays", path3: TapeFile);

                using (var stream = File.OpenRead(path: generated)) {
                    var snapshot = WorldReplaySnapshot.Read(stream: stream);

                    if ((snapshot.TickCount != stop) || (snapshot.RecordedAuthoritativeHashes.Length != stop)) {
                        problems.Add(item: $"The tape recorded {snapshot.TickCount} ticks instead of {stop}.");
                    }
                }
                File.Copy(generated, Path.Combine(path1: directory, path2: TapeFile), overwrite: true);
            } else {
                replayStatus = Direct("status", "replay.status").Output;
                trace = Direct("trace-read", "world.view.trace --json").Output;
                if (!replayStatus.Contains(comparisonType: StringComparison.Ordinal, value: "no divergence") ||
                    !replayStatus.Contains($"tick {stop} of {(plan.LastTick + 2)}", StringComparison.Ordinal)) {
                    problems.Add(item: $"The finite replay did not verify every consumed authority tick: {replayStatus}");
                }
                if (trace.Length == 0) { problems.Add(item: "The World returned no presentation trace."); }

                // The finite authority pause, all metric endpoints and trace snapshot are already behind us.
                // End observation explicitly, then use the ordinary presented-capture path: no temporal settling,
                // no additional authority ticks, and no screenshot work enters the measured interval.
                var image = Puck.Abstractions.PuckPaths.Slashed(path: Path.Combine(path1: directory, path2: (("post-window-" + Guid.NewGuid().ToString(format: "N")) + ".png")));

                _ = Direct("trace-end", "world.view.trace end");
                _ = Direct("capture", $"world.screenshot {CommandEcho.Quote(value: image)} presented 1");
                var completed = Direct("capture-wait", "world.wait captures 60");

                if (completed.Output.StartsWith(comparisonType: StringComparison.Ordinal, value: "[captures: settled") &&
                    File.Exists(path: image) && (new FileInfo(fileName: image).Length > 0)) {
                    postWindowImage = image;
                } else {
                    problems.Add(item: $"Post-window presented PNG did not complete outside measurement: {image}; {completed.Output}.");
                }
            }
            _ = process.Send(line: "quit");
            if (process.WaitForExit(TimeSpan.FromSeconds(seconds: 60), cancellation: cancellationToken) is not 0) { problems.Add(item: "The route World did not exit cleanly."); }
            // JoinPumps above closes both output streams before collecting pinned answers.
            var executed = new List<MotionPinnedAnswer>();

            foreach (var row in process.LinesFrom(from: 0)) {
                if (!MotionTranscript.TryParsePinned(row.Text, out var answer)) { continue; }
                executed.Add(item: answer);
                foreach (var metric in plan.Metrics) {
                    if (answer.Line != metric.Run) { continue; }
                    // Endpoint metrics were armed first. An authored command with the same spelling later in that
                    // drain still needs its own receipt, but must not replace the opening reading.
                    if (answer.Tick == (((ulong)plan.WarmupTicks) + 1UL)) { _ = before.TryAdd(key: metric.Name, value: answer.Answer); }
                    if (answer.Tick == (((ulong)plan.LastTick) + 1UL)) { _ = after.TryAdd(key: metric.Name, value: answer.Answer); }
                }
                foreach (var check in plan.Checks) {
                    if (answer.Line != check.Line) { continue; }
                    if (answer.Tick == (((ulong)plan.LastTick) + 1UL)) { afterChecks[check.Name] = answer.Answer; }
                }
            }
            problems.AddRange(collection: CheckExecutionReceipts(actual: executed, expected: ordered));
            foreach (var check in plan.Checks) {
                foreach (var readings in new[] { beforeChecks, afterChecks }) {
                    var answer = (readings.TryGetValue(key: check.Name, value: out var output)
                        ? new SweepAnswer(true, output, []) : null);
                    var evidence = SweepEvidence.Check(answer: answer, signal: check);

                    if (evidence.Status != SweepStatus.Clean) { problems.Add(item: $"Setup check '{check.Name}' failed: {string.Join(separator: "; ", values: evidence.Reasons)}"); }
                }
            }
            foreach (var metric in plan.Metrics) {
                if (!before.ContainsKey(key: metric.Name) || !after.ContainsKey(key: metric.Name)) { problems.Add(item: $"Missing endpoint reading '{metric.Name}'."); }
            }
        } catch (Exception exception) when ((exception is IOException or InvalidDataException or TimeoutException or JsonException)) {
            problems.Add(item: exception.Message);
            process.Kill();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(artifact, hash, directory, recording, files, buildSourceKey, replayStatus, trace, before, after, problems, timer.Elapsed.TotalSeconds) {
            AfterChecks = afterChecks,
            BeforeChecks = beforeChecks,
            DirectReceipts = directReceipts,
            PostWindowImage = postWindowImage,
        };
    }
}
