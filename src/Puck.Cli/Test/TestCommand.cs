using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World;

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
/// With <c>--reproduce</c>, every world runs twice into sibling directories and a world whose two exports differ
/// byte for byte is refused. Ordinary authored tests run once; reproducibility qualification is an explicit tier.
/// </remarks>
internal static partial class TestCommand {
    private const string ScratchPrefix = "puck-test-";
    private const string WorldDocumentSuffix = ".world.json";

    private static readonly TimeSpan BuildBudget = TimeSpan.FromSeconds(value: 600);

    private static int Run(string path, TestHost host, string? worldArtifact, string? keep, int jobs, bool reproduce) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            Console.Error.WriteLine(value: "ERROR: puck test must run inside the repository.");

            return 2;
        }

        if (host == TestHost.Browser) {
            Console.Error.WriteLine(value: "ERROR: --host browser is not implemented: the browser engine host has no command ingress, no acting principal and no authoritative server, so it cannot submit a scheduled command at all. Cross-host determinism there is an open gap, not a leg this verb can run.");

            return 2;
        }

        string runDirectory;

        if (keep is { }) {
            runDirectory = Path.GetFullPath(path: keep);

            try {
                _ = Directory.CreateDirectory(path: runDirectory);
            } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)) {
                Console.Error.WriteLine(value: $"ERROR: --keep {runDirectory} could not be created: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

                return 2;
            }
        } else {
            CliScratchDirectories.SweepScratch(scratchPrefix: ScratchPrefix);
            runDirectory = CliScratchDirectories.CreateRunDirectory(scratchPrefix: ScratchPrefix);
        }

        try {
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

            if (!TryResolveArtifact(
                artifact: out var artifact,
                repositoryRoot: repositoryRoot,
                runDirectory: runDirectory,
                worldArtifact: worldArtifact
            )) {
                return 2;
            }

            var results = new TestWorldRun[worlds.Count];
            var parallelism = Math.Clamp(
                value: jobs,
                min: 1,
                max: Math.Max(1, worlds.Count)
            );

            Parallel.For(
                fromInclusive: 0,
                toExclusive: worlds.Count,
                parallelOptions: new ParallelOptions { MaxDegreeOfParallelism = parallelism },
                body: index => results[index] = RunWorldCaptured(
                    artifact: artifact!,
                    reproduce: reproduce,
                    world: worlds[index],
                    worldDirectory: Path.Combine(
                        path1: runDirectory,
                        path2: "worlds",
                        path3: index.ToString(format: "D6", provider: CultureInfo.InvariantCulture)
                    )
                )
            );

            foreach (var result in results) {
                Console.Out.Write(value: result.Output);
                Console.Error.Write(value: result.Error);
            }

            if (results.Any(result => result.Verdict == 2)) {
                return 2;
            }

            var failed = results.Any(result => result.Verdict != 0);

            if (failed) {
                Console.Error.WriteLine(value: "FAIL: one or more worlds did not pass — a failing verdict, or a step whose recorded outcome was not the one it declared.");

                return 1;
            }

            Console.WriteLine(value: (reproduce
                ? $"PASS: every verdict in {worlds.Count} test world(s) passed, and each world's two runs exported identical bytes."
                : $"PASS: every verdict in {worlds.Count} test world(s) passed."
            ));

            return 0;
        } finally {
            if (keep is { }) {
                Console.WriteLine(value: $"test: generated worlds, transcripts, exports and manifests kept under {runDirectory}");
            } else {
                try {
                    Directory.Delete(
                        path: runDirectory,
                        recursive: true
                    );
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
                    Console.Error.WriteLine(value: $"test: the run directory {runDirectory} could not be removed: {exception.Message}");
                }
            }
        }
    }
    private static TestWorldRun RunWorldCaptured(string world, string artifact, string worldDirectory, bool reproduce) {
        using var output = new StringWriter(formatProvider: CultureInfo.InvariantCulture);
        using var error = new StringWriter(formatProvider: CultureInfo.InvariantCulture);
        var verdict = RunWorld(
            artifact: artifact,
            error: error,
            output: output,
            reproduce: reproduce,
            world: world,
            worldDirectory: worldDirectory
        );

        return new TestWorldRun(
            Error: error.ToString(),
            Output: output.ToString(),
            Verdict: verdict
        );
    }
    // One world: one ordinary run, or two plus a byte comparison for explicit reproducibility qualification.
    private static int RunWorld(string world, string artifact, string worldDirectory, TextWriter output, TextWriter error, bool reproduce) {
        var name = Path.GetFileNameWithoutExtension(path: Path.GetFileNameWithoutExtension(path: world));

        output.WriteLine(value: $"test {name}: {world}");
        output.WriteLine(value: $"  artifacts: {worldDirectory.Replace(oldChar: '\\', newChar: '/')}");

        if (!TryReadSchedule(
            reason: out var scheduleReason,
            schedule: out var schedule,
            world: world
        )) {
            error.WriteLine(value: $"ERROR: {scheduleReason}");

            return 2;
        }

        TestReading? first = null;
        string? unexpected = null;

        var runCount = (reproduce ? 2 : 1);

        for (var run = 1; (run <= runCount); run++) {
            if (!TryRunLeg(
                artifact: artifact,
                exportTick: schedule!.ExportTick,
                legDirectory: Path.Combine(
                path1: worldDirectory,
                path2: $"run{run.ToString(provider: CultureInfo.InvariantCulture)}"
            ),
                rateHz: schedule.RateHz,
                reading: out var reading,
                error: error,
                world: world
            )) {
                return 2;
            }

            // Reaching the authored tick, accounting for every declared row, and every row answering what it declared
            // are part of the verdict, and they are checked on each requested leg: two identically truncated runs
            // reproduce each other perfectly.
            switch (TestReconciliation.Judge(
                name: name,
                reading: reading!,
                reason: out var judged,
                schedule: schedule
            )) {
                case TestReconciliationVerdict.Unmeasured:
                    error.WriteLine(value: $"  REFUSED: {judged}");

                    return 2;
                case TestReconciliationVerdict.Unexpected:
                    if (unexpected is null) {
                        unexpected = judged;

                        error.WriteLine(value: $"  OUTCOME: {judged}");
                    }

                    break;
                default:
                    break;
            }

            if (run == 1) {
                first = reading;

                continue;
            }

            if (!first!.ExportBytes.AsSpan().SequenceEqual(other: reading!.ExportBytes.AsSpan())) {
                error.WriteLine(value: $"  REFUSED: the two runs of {name} exported different bytes at tick {schedule.ExportTick} — the world does not reproduce, so its verdicts say nothing.");

                return 2;
            }

            if (!first.ManifestBytes.AsSpan().SequenceEqual(other: reading.ManifestBytes.AsSpan())) {
                error.WriteLine(value: $"  REFUSED: the two runs of {name} recorded different {WorldScheduleSection.ManifestFileName} bytes — what the run did is not reproducible, so the refusals it reports are evidence of nothing.");

                return 2;
            }
        }

        var reported = Report(
            error: error,
            name: name,
            output: output,
            reading: first!
        );

        return ((reported != 0)
            ? reported
            : ((unexpected is null)
                ? 0
                : 1
            )
        );
    }
    private static int Report(string name, TestReading reading, TextWriter output, TextWriter error) {
        if (reading.Verdicts.Count == 0) {
            error.WriteLine(value: $"ERROR: {name} declares no verdict row — a test world with nothing to answer is a usage error, not a pass.");

            return 2;
        }

        var failures = 0;

        foreach (var verdict in reading.Verdicts) {
            var line = $"  {verdict.Name}: {WorldVerdict.Describe(status: verdict.Judged)} gate=\"{verdict.Gate}\" saw=[{string.Join(
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

        output.WriteLine(value: $"  {reading.Verdicts.Count - failures}/{reading.Verdicts.Count} verdict(s) passed at export tick {reading.ExportTick}.");

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

    private sealed record TestWorldRun(int Verdict, string Output, string Error);
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

                declared.Add(item: new TestScheduleRow(
                    Command: (entry[propertyName: "command"]?.GetValue<string>() ?? string.Empty),
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
