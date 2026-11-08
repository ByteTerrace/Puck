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
    public const string TapeName = "motion-route";
    public const string TapeFile = "motion-route.puckreplay";

    /// <summary>Checks every requested tick/command multiplicity against the complete process receipt stream.</summary>
    public static IReadOnlyList<string> CheckExecutionReceipts(IReadOnlyList<(ulong Tick, string Line)> expected, IReadOnlyList<MotionPinnedAnswer> actual) {
        var problems = new List<string>();
        foreach (var answer in actual) { if (answer.Refused) { problems.Add($"Tick {answer.Tick}: {answer.Line}: {answer.Answer}"); } }
        var expectedRows = expected.GroupBy(row => (row.Tick, row.Line)).ToDictionary(group => group.Key, group => group.Count());
        var actualRows = actual.GroupBy(row => (row.Tick, row.Line)).ToDictionary(group => group.Key, group => group.Count());
        foreach (var (key, count) in expectedRows) {
            if (actualRows.GetValueOrDefault(key) != count) { problems.Add($"Expected {count} execution receipt(s) at tick {key.Tick} for '{key.Line}', found {actualRows.GetValueOrDefault(key)}."); }
        }
        foreach (var key in actualRows.Keys) { if (!expectedRows.ContainsKey(key)) { problems.Add($"Unexpected execution receipt at tick {key.Tick}: {key.Line}."); } }
        return problems;
    }

    public static MotionRouteLeg Execute(MotionRoutePlan plan, string artifact, string world, string repository,
        string directory, WorldBackendPreference? backend, string? cache, TimeSpan deadline, string? tape, string? buildSourceKey) {
        artifact = artifact.Replace('\\', '/');
        directory = directory.Replace('\\', '/');
        Directory.CreateDirectory(directory);
        // Reusing an output folder must never reuse an authority catalog, machine id or checkpoint.
        var state = Path.Combine(directory, "state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(state);
        if (tape is not null) {
            Directory.CreateDirectory(Path.Combine(state, "Replays"));
            File.Copy(tape, Path.Combine(state, "Replays", TapeFile), overwrite: true);
        }
        var problems = new List<string>();
        var timer = Stopwatch.StartNew();
        var replayStatus = string.Empty;
        var trace = string.Empty;
        string? postWindowImage = null;
        var recording = tape is null;
        var before = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var after = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var beforeChecks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var afterChecks = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var directReceipts = new List<MotionRouteReceipt>();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var artifactRoot = Path.GetDirectoryName(artifact)!;
        foreach (var file in Directory.EnumerateFiles(artifactRoot, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).ToLowerInvariant() is ".dll" or ".spv" or ".dxil" or ".dxbc" or ".hlsl" or ".hlsli" ||
                path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)) {
            using var stream = File.OpenRead(file);
            files[Path.GetRelativePath(artifactRoot, file).Replace('\\', '/')] = Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        var hash = files[Path.GetFileName(artifact)];
        WorldProcess started;
        using (var launch = WorldLaunch.Admit(request: new WorldLaunchRequest(Artifact: artifact, Step: "motion route") {
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
        using var controlBudget = new SuiteBudget(TimeSpan.FromTicks(Math.Max(1, (deadline - timer.Elapsed).Ticks)), CancellationToken.None);
        MotionScriptResult Run(params string[] lines) {
            var remaining = deadline - timer.Elapsed;
            if (remaining <= TimeSpan.Zero) { throw new TimeoutException("The route deadline elapsed."); }
            return process.RunScript(lines, remaining);
        }
        try {
            if (!SweepWorld.TryAttach(process, TimeSpan.FromSeconds(120), controlBudget, out var attached, out var attachmentError)) {
                throw new InvalidDataException(attachmentError);
            }
            using var control = attached!; // Non-owning: WorldProcess remains the only process lifetime owner.
            SweepAnswer Direct(string stage, string script, int timeoutSeconds = 120) {
                var answer = control.Run(script, timeoutSeconds);
                directReceipts.Add(new(stage, script, answer));
                if (!answer.Ok || answer.Unmeasured) {
                    throw new InvalidDataException($"{stage}: {script}: {string.Join("; ", answer.Refusals)} {answer.Output}");
                }
                return answer;
            }

            // Record every bootstrap submission before it can reach tick one. During replay, only movement/command
            // inputs are masked: presentation composition/session controls are deliberately reapplied as authored.
            // Setup is for presentation controls; authority mutations belong in the recorded inputSteps.
            if (recording) { _ = Direct("record", $"replay.record {TapeName}"); }
            else { _ = Direct("restore", $"replay.drive {TapeName} to {plan.LastTick + 2}\nworld.rate pause"); }
            if (Run().Tick != 0) { throw new InvalidDataException("Bootstrap did not remain at tick zero."); }

            // The startup flags establish time zero before the first produced frame. No screenshot or capture
            // convergence occurs here or in the route. A readiness failure is a refused workload, not a warm run.
            void Ready() {
                var mark = process.LineCount;
                var ready = Run("world.wait bakes 600", "world.wait ready 600");
                // stdout and stderr have independent pumps. Wait for each readiness narration itself rather than
                // assuming that reading the stdout barrier has already drained the other pipe.
                var baked = process.WaitFor(mark, row => row.Text.StartsWith("[bakes:", StringComparison.Ordinal), deadline - timer.Elapsed);
                var rendered = process.WaitFor(mark, row => row.Text.StartsWith("[engine:", StringComparison.Ordinal), deadline - timer.Elapsed);
                if (ready.Tick != 0 || baked is null || rendered is null ||
                    !baked.Text.StartsWith("[bakes: settled", StringComparison.Ordinal) ||
                    !rendered.Text.StartsWith("[engine: ready", StringComparison.Ordinal)) {
                    throw new InvalidDataException("The fresh World did not remain at tick zero with bakes and renderer ready.");
                }
            }
            Ready();
            foreach (var line in plan.Setup) { _ = Direct("setup", line); }
            if (Run().Tick != 0) { throw new InvalidDataException("Route setup advanced the paused authority."); }
            Ready();
            var pinned = new List<(ulong Tick, string Line)>();
            // Hold authority while geometry produced by warmup inputs lands. Both metric endpoints then run in the
            // same tick-command phase; opening reads precede every authored command at the first measured tick.
            pinned.Add(((ulong)plan.WarmupTicks, "world.rate pause"));
            foreach (var metric in plan.Metrics) { pinned.Add(((ulong)plan.WarmupTicks + 1UL, metric.Run)); }
            if (recording) { pinned.AddRange(MotionRoutePlan.Expand(plan.InputSteps)); }
            pinned.AddRange(MotionRoutePlan.Expand(plan.PresentationSteps));
            // Keep recording endpoints too: a later replay refusal must not discard the usable workload's
            // counted work and memory observations. Recording and replay timings remain distinct phases.
            foreach (var metric in plan.Metrics) {
                pinned.Add(((ulong)plan.LastTick + 1UL, metric.Run));
            }
            foreach (var check in plan.Checks) {
                pinned.Add(((ulong)plan.LastTick + 1UL, check.Line));
            }
            // Keep a finite drive alive through the full observed interval so replay.status still exposes its
            // per-tick authoritative verdict. No extra authority step can escape this pause.
            var stop = plan.LastTick + (recording ? 2 : 1);
            pinned.Add(((ulong)stop, "world.rate pause"));
            var ordered = pinned.OrderBy(static row => row.Tick).ToArray();
            // Each atomic one-row ArmLine fits the existing control limits for ordinary route commands. Batch
            // a bounded number outside the measured window; SweepEvidence checks every command's typed receipt.
            // The final process log separately proves execution, without world.at's 256-entry read-back retention.
            foreach (var batch in ordered.Chunk(32)) {
                var script = string.Join("\n", batch.Select(row => MotionPlan.ArmLine(1, [row])));
                _ = Direct("arm", script);
            }
            if (Run().Tick != 0) { throw new InvalidDataException("Arming advanced the paused authority."); }
            _ = Direct("warmup-resume", "world.rate resume");
            _ = Direct("warmup-wait", $"world.wait until {plan.WarmupTicks}", 600);
            if (Run().Tick != (ulong)plan.WarmupTicks) { throw new InvalidDataException("The warmup did not stop at its authority boundary."); }
            _ = Direct("warmup-paused", "world.rate pause");
            var resident = Direct("warmup-residency", "world.wait residency 600", 600);
            if (!resident.Output.StartsWith("[residency: settled", StringComparison.Ordinal) || Run().Tick != (ulong)plan.WarmupTicks) {
                throw new InvalidDataException("The warmup did not remain paused with its current geometry resident.");
            }
            foreach (var check in plan.Checks) { beforeChecks[check.Name] = Direct("before-check", check.Line).Output; }
            _ = Direct("trace-begin", $"world.view.trace begin {plan.WarmupTicks + 1} {plan.LastTick} {plan.WarmupTicks + 1}");
            _ = Direct("measured-resume", "world.rate resume");
            var finished = Run($"world.wait until {stop}");
            if (finished.Tick != (ulong)stop) { throw new InvalidDataException($"The route ended at tick {finished.Tick}, not {stop}."); }
            if (recording) {
                trace = Direct("trace-read", "world.view.trace --json").Output;
                var stopped = Direct("record-stop", "replay.stop");
                if (!stopped.Output.Contains("faithful capture", StringComparison.Ordinal) ||
                    stopped.Output.Contains("MISMATCH", StringComparison.Ordinal)) {
                    problems.Add("The original recorded authority did not reproduce at replay.stop.");
                }
                var generated = Path.Combine(state, "Replays", TapeFile);
                using (var stream = File.OpenRead(generated)) {
                    var snapshot = WorldReplaySnapshot.Read(stream);
                    if (snapshot.TickCount != stop || snapshot.RecordedAuthoritativeHashes.Length != stop) {
                        problems.Add($"The tape recorded {snapshot.TickCount} ticks instead of {stop}.");
                    }
                }
                File.Copy(generated, Path.Combine(directory, TapeFile), overwrite: true);
            } else {
                replayStatus = Direct("status", "replay.status").Output;
                trace = Direct("trace-read", "world.view.trace --json").Output;
                if (!replayStatus.Contains("no divergence", StringComparison.Ordinal) ||
                    !replayStatus.Contains($"tick {stop} of {plan.LastTick + 2}", StringComparison.Ordinal)) {
                    problems.Add($"The finite replay did not verify every consumed authority tick: {replayStatus}");
                }
                if (trace.Length == 0) { problems.Add("The World returned no presentation trace."); }

                // The finite authority pause, all metric endpoints and trace snapshot are already behind us.
                // End observation explicitly, then use the ordinary presented-capture path: no temporal settling,
                // no additional authority ticks, and no screenshot work enters the measured interval.
                var image = Path.Combine(directory, "post-window-" + Guid.NewGuid().ToString("N") + ".png").Replace('\\', '/');
                _ = Direct("trace-end", "world.view.trace end");
                _ = Direct("capture", $"world.screenshot {CommandEcho.Quote(image)} presented 1");
                var completed = Direct("capture-wait", "world.wait captures 60");
                if (completed.Output.StartsWith("[captures: settled", StringComparison.Ordinal) &&
                    File.Exists(image) && new FileInfo(image).Length > 0) {
                    postWindowImage = image;
                } else {
                    problems.Add($"Post-window presented PNG did not complete outside measurement: {image}; {completed.Output}.");
                }
            }
            _ = process.Send("quit");
            if (process.WaitForExit(TimeSpan.FromSeconds(60)) is not 0) { problems.Add("The route World did not exit cleanly."); }
            // JoinPumps above closes both output streams before collecting pinned answers.
            var executed = new List<MotionPinnedAnswer>();
            foreach (var row in process.LinesFrom(0)) {
                if (!MotionTranscript.TryParsePinned(row.Text, out var answer)) { continue; }
                executed.Add(answer);
                foreach (var metric in plan.Metrics) {
                    if (answer.Line != metric.Run) { continue; }
                    // Endpoint metrics were armed first. An authored command with the same spelling later in that
                    // drain still needs its own receipt, but must not replace the opening reading.
                    if (answer.Tick == ((ulong)plan.WarmupTicks + 1UL)) { _ = before.TryAdd(metric.Name, answer.Answer); }
                    if (answer.Tick == ((ulong)plan.LastTick + 1UL)) { _ = after.TryAdd(metric.Name, answer.Answer); }
                }
                foreach (var check in plan.Checks) {
                    if (answer.Line != check.Line) { continue; }
                    if (answer.Tick == ((ulong)plan.LastTick + 1UL)) { afterChecks[check.Name] = answer.Answer; }
                }
            }
            problems.AddRange(CheckExecutionReceipts(ordered, executed));
            foreach (var check in plan.Checks) {
                foreach (var readings in new[] { beforeChecks, afterChecks }) {
                    var answer = readings.TryGetValue(check.Name, out var output)
                        ? new SweepAnswer(true, output, []) : null;
                    var evidence = SweepEvidence.Check(answer, check);
                    if (evidence.Status != SweepStatus.Clean) { problems.Add($"Setup check '{check.Name}' failed: {string.Join("; ", evidence.Reasons)}"); }
                }
            }
            foreach (var metric in plan.Metrics) {
                if (!before.ContainsKey(metric.Name) || !after.ContainsKey(metric.Name)) { problems.Add($"Missing endpoint reading '{metric.Name}'."); }
            }
        } catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or JsonException) {
            problems.Add(exception.Message);
            process.Kill();
        }
        return new(artifact, hash, directory, recording, files, buildSourceKey, replayStatus, trace, before, after, problems, timer.Elapsed.TotalSeconds) {
            PostWindowImage = postWindowImage,
            DirectReceipts = directReceipts,
            BeforeChecks = beforeChecks,
            AfterChecks = afterChecks,
        };
    }
}
