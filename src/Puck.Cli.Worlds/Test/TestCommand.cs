using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World;
using Puck.World.Transpiler.Lowering;

namespace Puck.Cli.Test;

/// <summary>
/// <c>puck test</c> — boots each authored test world through the real <c>Puck.World</c> executable, headless and
/// unpaced, to
/// the tick its own <c>schedule</c> section declares its state export at, then reads the verdict rows out of that
/// export: one line per verdict naming the gate and the values it saw, and exit 0 only when every verdict passes.
/// </summary>
/// <remarks>
/// Each collected world owns a numbered directory, independent of its filename, so parallel worlds never share
/// persistence, transcripts or verdict exports.
/// Each worker process boots its scenarios one after another, each in a fresh native host. With <c>--reproduce</c>,
/// recorded input must reproduce each authority's hashes, machine cores and final export in place.
/// </remarks>
public static partial class TestCommand {
    private const string ScratchPrefix = "puck-test-";


    private static int Run(string path, string? worldArtifact, string? keep, int jobs, bool reproduce, string? cacheDirectory, string? workers, string? record, string? compare) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            Console.Error.WriteLine(value: "ERROR: puck test must run inside the repository.");

            return 2;
        }

        if (!TryParseLegs(legs: out var legs, reason: out var legsReason, workers: workers)) {
            Console.Error.WriteLine(value: $"ERROR: {legsReason}");

            return 2;
        }

        if (keep is { }) {
            var kept = Path.GetFullPath(path: keep);

            try {
                _ = Directory.CreateDirectory(path: kept);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)) {
                Console.Error.WriteLine(value: $"ERROR: --keep {kept} could not be created: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

                return 2;
            }

            try {
                return RunIn(
                    cacheDirectory: cacheDirectory,
                    compare: compare,
                    jobs: jobs,
                    legs: legs,
                    path: path,
                    record: record,
                    repositoryRoot: repositoryRoot,
                    reproduce: reproduce,
                    runDirectory: kept,
                    worldArtifact: worldArtifact
                );
            } finally {
                Console.WriteLine(value: $"test: generated worlds, transcripts, exports and manifests kept under {kept}");
            }
        }

        using var run = RunDirectory.Create(prefix: ScratchPrefix);

        return run.Conclude(exitCode: RunIn(
            cacheDirectory: cacheDirectory,
            compare: compare,
            jobs: jobs,
            legs: legs,
            path: path,
            record: record,
            repositoryRoot: repositoryRoot,
            reproduce: reproduce,
            runDirectory: run.Path,
            worldArtifact: worldArtifact
        ));
    }
    // One run of the verb inside runDirectory: the --keep directory, or a run directory the caller concludes with the
    // exit code this returns.
    private static int RunIn(string path, string? worldArtifact, int jobs, bool reproduce, string repositoryRoot, string runDirectory, string? cacheDirectory, IReadOnlyList<TestLeg> legs, string? record, string? compare) {
        var preparation = new WorldBootWork();
        using var attribution = WorldBootWork.Attribute(work: preparation);

        if (!TryCollectWorlds(
            generatedDirectory: Path.Combine(
            path1: runDirectory,
            path2: "generated"
        ),
            path: path,
            reason: out var collectReason,
            worlds: out var worlds
        )) {
            Console.Error.WriteLine(value: $"ERROR: {collectReason}");

            return 2;
        }

        Console.WriteLine(value: $"test: preparation worlds={worlds.Count} compiles={preparation.Read(kind: WorldBootWork.Compiles)} compositions={preparation.Read(kind: WorldBootWork.Compositions)} shared={preparation.Read(kind: WorldBootWork.CompositionsShared)}");

        if (!TryResolveArtifact(
            artifact: out var artifact,
            lease: out var lease,
            repositoryRoot: repositoryRoot,
            runDirectory: runDirectory,
            worldArtifact: worldArtifact
        )) {
            return 2;
        }

        using var held = lease;

        var parallelism = Math.Clamp(
            value: jobs,
            min: 1,
            max: Math.Max(val1: 1, val2: worlds.Count)
        );

        var batches = Enumerable.Range(0, worlds.Count)
            .GroupBy(keySelector: index => (index % parallelism))
            .Select(selector: chunk => chunk.ToArray()).ToArray();
        var batchPassed = new bool[(batches.Length * legs.Count)];
        // A leg is recorded for comparison whenever something compares it: another worker count, or a recording.
        var recorded = ((legs.Count > 1) || (legs[0].Workers is not null) || (record is not null) || (compare is not null));

        // A run of several worlds is a sweep of World processes at several GB each, so it holds the machine's heavy lease; a single world
        // stays unleased. A --workers sweep re-runs the same worlds one count after another, never beside each other, so its legs add no
        // concurrency and do not count.
        using var heavy = ((worlds.Count > 1) ? AcquireHeavy(path: path, repositoryRoot: repositoryRoot, worlds: worlds.Count) : null);

        // Each worker count runs every scenario before the next count starts, so a count's wall time is its own.
        for (var legIndex = 0; (legIndex < legs.Count); legIndex++) {
            var leg = legs[legIndex];
            var offset = (legIndex * batches.Length);
            var started = Stopwatch.GetTimestamp();

            Parallel.For(fromInclusive: 0, toExclusive: batches.Length,
                parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                body: worker => batchPassed[(offset + worker)] = RunBatch(artifact: artifact!, chunk: worker, indices: batches[worker],
                    reproduce: reproduce, runDirectory: runDirectory, worlds: worlds, cacheDirectory: cacheDirectory, leg: leg, record: recorded,
                    verifyHash: ((legIndex == 0) && (leg.Workers is not null)), heavy: (heavy is not null)));
            if (leg.Workers is { }) {
                Console.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture,
                    handler: $"test: {leg.Label} ran {worlds.Count} world(s) in {Stopwatch.GetElapsedTime(startingTimestamp: started).TotalSeconds:F1} s"));
            }
        }
        var boots = 0;
        var ticks = 0UL;
        var refused = batchPassed.Any(predicate: static passed => !passed);
        var failed = false;
        var diverged = false;

        for (var index = 0; (index < worlds.Count); index++) {
            var worldDirectory = WorldDirectory(index: index, runDirectory: runDirectory);
            var verdict = RunWorld(reproduce: reproduce, test: worlds[index], output: Console.Out, error: Console.Error,
                boots: out var worldBoots, ticks: out var worldTicks, leg: legs[0].Name, worldDirectory: worldDirectory);

            boots += worldBoots;
            ticks += worldTicks;
            refused |= (verdict == 2);
            failed |= (verdict != 0);
            if (recorded && (verdict != 2)) {
                var compared = CompareAndRecord(compare: compare, error: Console.Error, legs: legs, output: Console.Out,
                    record: record, test: worlds[index], worldDirectory: worldDirectory);

                diverged |= (compared != 0);
            }
        }
        refused |= diverged;

        Console.WriteLine(value: $"test: work processes={batches.Length} host-boots={boots} world-runs={worlds.Count} exported-authority-ticks={ticks}");
        if (refused) {
            if (diverged) {
                Console.Error.WriteLine(value: "FAIL: a world's authority hashes, snapshot entries, exports or crossing log differed between worker counts or from its recording, or had no recording to compare.");
            }

            return 2;
        }

        if (failed) {
            Console.Error.WriteLine(value: "FAIL: one or more worlds did not pass — a failing verdict, or a step whose recorded outcome was not the one it declared.");

            return 1;
        }

        Console.WriteLine(value: (reproduce
            ? $"PASS: every verdict in {worlds.Count} test world(s) passed, and recorded inputs reproduced every authority from its checkpoint."
            : $"PASS: every verdict in {worlds.Count} test world(s) passed."
        ));
        if (legs.Count > 1) {
            Console.WriteLine(value: $"PASS: every world's authority hashes, snapshot entries, exports and crossing log agreed at {string.Join(separator: ", ", values: legs.Select(selector: static leg => leg.Label))}.");
        }
        if (compare is { }) {
            Console.WriteLine(value: $"PASS: every world matched its recording under {Path.GetFullPath(path: compare)}.");
        }

        return 0;
    }
    // Holds the shared heavy lease (HeavyLease) and says so: the lease prints its own line while it waits, and this one when it is held.
    private static CliLease AcquireHeavy(string path, string repositoryRoot, int worlds) {
        var started = Stopwatch.GetTimestamp();
        var lease = HeavyLease.Acquire(clock: TimeProvider.System, holder: $"puck test {Path.GetFileName(path: path)}", repositoryRoot: repositoryRoot);

        Console.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture,
            handler: $"test: holding the heavy lease as lane {CliLease.LaneOf(checkout: repositoryRoot)} for {worlds} worlds (waited {Stopwatch.GetElapsedTime(startingTimestamp: started).TotalSeconds:F1} s)"));

        return lease;
    }
    // The authored run supplies verdicts; reproduction replays captured input inside its worker.
    private static int RunWorld(TestWorld test, string worldDirectory, string leg, TextWriter output, TextWriter error, bool reproduce, out int boots, out ulong ticks) {
        boots = 0;
        ticks = 0;
        var name = test.Name;
        var world = test.Path;

        output.WriteLine(value: $"test {name}: {world}");
        output.WriteLine(value: $"  artifacts: {worldDirectory.Replace(newChar: '/', oldChar: '\\')}");
        if (!TryReadSchedule(reason: out var scheduleReason, schedule: out var schedule, world: world)) {
            error.WriteLine(value: $"ERROR: {scheduleReason}");
            return 2;
        }
        var legDirectory = Path.Combine(path1: worldDirectory, path2: leg);
        var bootCount = Path.Combine(path1: legDirectory, path2: "host-boots");

        if (File.Exists(path: bootCount)) { boots = int.Parse(File.ReadAllText(path: bootCount), CultureInfo.InvariantCulture); }
        if (!TryReadBatchLeg(error: error, legDirectory: legDirectory, reading: out var reading, world: world)) { return 2; }
        ticks = reading!.Worlds.Aggregate(0UL, (total, row) => (total + row.ExportTick));
        var verdict = TestReconciliation.Judge(name: name, reading: reading, reason: out var judged, schedule: schedule!);

        if (verdict == TestReconciliationVerdict.Unmeasured) { error.WriteLine(value: $"  REFUSED: {judged}"); return 2; }
        if (verdict == TestReconciliationVerdict.Unexpected) { error.WriteLine(value: $"  OUTCOME: {judged}"); }
        if (reproduce) {
            var proof = Path.Combine(path1: legDirectory, path2: AuthoritiesFile);

            if (!File.Exists(path: proof) || (JsonNode.Parse(File.ReadAllText(path: proof)) is not JsonArray { Count: > 0 } rows) ||
                rows.Any(predicate: row => (row?["divergence"]?.GetValue<int>() != -1)) ||
                reading.Worlds.Any(predicate: world => !rows.Any(predicate: row => (row?["world"]?.GetValue<string>() == world.World)))) {
                error.WriteLine(value: "  REFUSED: missing or divergent checkpoint reproduction.");
                return 2;
            }
        }
        var reported = Report(name, reading, test.Expectations, output, error);

        return ((reported != 0) ? reported : ((verdict == TestReconciliationVerdict.Unexpected) ? 1 : 0));
    }
    private static int Report(string name, TestReading reading, IReadOnlyList<WorldTestForeign> expectations, TextWriter output, TextWriter error) {
        // An expectation about a sibling document is judged by the instance a crossing started. A run that started none
        // has nothing to read it from, and the claim failed: the crossing it judges never happened.
        var unjudged = expectations
            .Where(predicate: expectation => !reading.Worlds.Any(predicate: world => (world.Document == expectation.Document)))
            .ToList();
        var total = (reading.Worlds.Sum(selector: static world => world.Verdicts.Count) + unjudged.Count);

        if (total == 0) {
            error.WriteLine(value: $"ERROR: {name} declares no verdict row — a test world with nothing to answer is a usage error, not a pass.");

            return 2;
        }

        var failures = unjudged.Count;

        foreach (var expectation in unjudged) {
            error.WriteLine(value: $"  {expectation.Document}/{expectation.Row}: fail gate=\"{expectation.Gate}\" — the run started no instance of '{expectation.Document}', so the crossing this claim judges never happened.");
        }

        foreach (var (world, verdict) in reading.Verdicts) {
            var line = $"  {world.Label}{verdict.Name}: {WorldVerdict.Describe(status: verdict.Judged)} gate=\"{verdict.Gate}\" saw=[{string.Join(
                separator: " ",
                values: verdict.Saw
            )}]{Stamp(verdict: verdict)}";

            if (WorldVerdict.IsPass(status: verdict.Judged)) {
                output.WriteLine(value: line);
            } else {
                failures++;

                error.WriteLine(value: line);
            }
        }

        foreach (var submission in reading.Submissions) {
            if (submission.Outcome != WorldScheduleSection.OutcomeSubmitted) {
                error.WriteLine(value: $"  submission tick={submission.Tick} {submission.Principal}: {submission.Outcome} {submission.Command}{((submission.Detail is { } detail)
                    ? $" — {detail}"
                    : string.Empty)}");
            }
        }

        foreach (var echo in reading.Echoes) {
            if (echo.Rejected) {
                error.WriteLine(value: $"  refusal: {echo.Message}");
            }
        }

        output.WriteLine(value: $"  {(total - failures)}/{total} verdict(s) passed at export tick {reading.ExportTick}{((reading.Worlds.Count > 1)
            ? $" across {reading.Worlds.Count} world(s)"
            : string.Empty)}.");

        return ((failures == 0)
            ? 0
            : 1
        );
    }
    // The firing stamp, named on the verdict's own line: the tick a rule wrote the row, or the status that reached a
    // verdict cell without one.
    private static string Stamp(TestVerdict verdict) => (verdict.Fired
        ? string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $" firedTick={verdict.FiredTick!.Value}"
        )
        : ((verdict.Status == WorldVerdict.NotEvaluated)
            ? string.Empty
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $" (status {verdict.Status} with no firing stamp — no rule effect ever wrote this row)"
            )
        )
    );
    // The export tick and the simulation rate come from the document's own text rather than a composed load: the
    // schedule and the rate are the two things this verb needs before it can boot anything, and composing a document
    // that names a basis is the host's job, not the runner's.
    private static bool TryReadSchedule(string world, out TestSchedule? schedule, out string? reason) {
        var rateHz = WorldDefinition.UnauthoredSimulationRateHz;

        schedule = null;
        reason = null;

        JsonNode? document;

        try {
            document = JsonNode.Parse(utf8Json: File.ReadAllBytes(path: world));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException)) {
            reason = $"{world} could not be read: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        if (document is not JsonObject root) {
            reason = $"{world} is not a JSON object.";

            return false;
        }

        if (root[propertyName: "simulation"]?[propertyName: "rateHz"] is { } authoredRate) {
            rateHz = authoredRate.GetValue<int>();
        }

        if (root[propertyName: "schedule"] is not JsonObject section) {
            reason = $"{world} authors no schedule section — puck test boots a world to the export tick its own schedule declares, so a world without one has nothing for this verb to do.";

            return false;
        }

        var settle = (section[propertyName: "settleTicks"]?.GetValue<int>() ?? 0);
        var declared = new List<TestScheduleRow>();
        var last = 0UL;

        if (section[propertyName: "rows"] is JsonArray rows) {
            foreach (var row in rows) {
                if (row is not JsonObject entry) {
                    continue;
                }

                var tick = (entry[propertyName: "tick"]?.GetValue<ulong>() ?? 0UL);

                if (!Enum.TryParse(
                    ignoreCase: true,
                    result: out WorldScheduleExpectation expect,
                    value: (entry[propertyName: "expect"]?.GetValue<string>() ?? nameof(WorldScheduleExpectation.Submitted))
                )) {
                    reason = $"{world} schedule.rows declares an expect the runner does not know — the host would have refused it at boot.";

                    return false;
                }

                // The submitted line, not the authored one: a row addressing a sibling world carries that world as
                // the trailing token its verb reads, and the manifest records what was submitted.
                declared.Add(item: new TestScheduleRow(
                    Command: WorldScheduleCommands.EffectiveCommand(row: new WorldScheduleRow(
                        Command: (entry[propertyName: "command"]?.GetValue<string>() ?? string.Empty),
                        Principal: (entry[propertyName: "principal"]?.GetValue<string>() ?? string.Empty),
                        Tick: tick,
                        World: entry[propertyName: "world"]?.GetValue<string>()
                    )),
                    Expect: expect,
                    Principal: (entry[propertyName: "principal"]?.GetValue<string>() ?? string.Empty),
                    Refusal: entry[propertyName: "refusal"]?.GetValue<string>(),
                    Tick: tick
                ));
                last = Math.Max(
                    val1: last,
                    val2: tick
                );
            }
        }

        schedule = new TestSchedule(
            ExportTick: (last + ((ulong)Math.Max(
                val1: settle,
                val2: 1
            ))),
            RateHz: rateHz,
            Rows: declared
        );

        return true;
    }
}
