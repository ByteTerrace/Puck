using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World;

namespace Puck.Cli.Test;

public static partial class TestCommand {
    // A leg is bounded by its progress, never by how long its ticks take: a host that keeps advancing is never cut
    // short, however loaded the machine, and one that stops advancing is stopped. The script waits in steps of one
    // simulated second, and the World answers each step as it starts it, once the step before it has released, so each
    // answer is a tick the run reached. The first answer must come within StartupBudget of launch, which covers process
    // start-up, the world's load and composition and the host's start; each later one within ProgressBudget of the one
    // before it.
    private static readonly TimeSpan StartupBudget = TimeSpan.FromMinutes(value: 3);
    private static readonly TimeSpan ProgressBudget = TimeSpan.FromMinutes(value: 1);

    private const string WaitAnswer = "[world.wait:";

    // The ticks each step of the leg's script waits: one simulated second at the document's rate.
    private static ulong StepTicks(int rateHz) => ((ulong)Math.Max(
        val1: 1,
        val2: rateHz
    ));

    // The longest a leg's watchdog lets it run: its start-up, then every step and the quit at the progress bound. The
    // World is handed it as its own exit, so a host whose runner has gone stops, and never sooner than the runner would
    // have stopped it.
    internal static TimeSpan LegCeiling(ulong exportTick, int rateHz) {
        var steps = (((exportTick + 2UL) + (StepTicks(rateHz: rateHz) - 1UL)) / StepTicks(rateHz: rateHz));

        return (StartupBudget + (ProgressBudget * (steps + 1UL)));
    }

    // The last few lines of a failing leg's stderr: the boot or validation refusal sits at the end, and the whole
    // transcript would bury it.
    private static string Tail(string text, int lines = 12) {
        var kept = text
            .ReplaceLineEndings(replacementText: "\n")
            .Split(separator: '\n')
            .Where(predicate: static line => (line.Trim().Length != 0))
            .TakeLast(count: lines);

        return string.Join(
            separator: Environment.NewLine,
            values: kept
        );
    }
    // The World every leg boots: the --world-artifact the caller named, or the build of this checkout's sources, reused
    // when an earlier run built it and leased until every leg has exited.
    private static bool TryResolveArtifact(string? worldArtifact, string repositoryRoot, string runDirectory, out string? artifact, out WorldBuild? lease) {
        if (WorldLaunch.TryResolve(
            build: out lease,
            error: out var error,
            logDirectory: runDirectory,
            named: worldArtifact,
            repositoryRoot: repositoryRoot,
            spirv: false,
            verb: "test"
        )) {
            artifact = lease.Path;

            return true;
        }

        artifact = null;

        Console.Error.WriteLine(value: $"ERROR: {error}");

        return false;
    }

    private static bool TryRead(string world, string scheduleDirectory, TextWriter error, out TestReading? reading) {
        reading = null;

        var manifestPath = Path.Combine(
            path1: scheduleDirectory,
            path2: WorldScheduleSection.ManifestFileName
        );
        var exportPath = Path.Combine(
            path1: scheduleDirectory,
            path2: WorldScheduleSection.ExportFileName
        );

        if (
            !File.Exists(path: manifestPath) ||
            !File.Exists(path: exportPath)
        ) {
            error.WriteLine(value: $"ERROR: the leg for {world} wrote no {WorldScheduleSection.ManifestFileName}/{WorldScheduleSection.ExportFileName} into {scheduleDirectory} — the run never reached its export tick.");

            return false;
        }

        var exportBytes = File.ReadAllBytes(path: exportPath);
        var manifestBytes = File.ReadAllBytes(path: manifestPath);

        JsonObject manifest;
        JsonObject export;

        try {
            manifest = (JsonNode.Parse(utf8Json: manifestBytes) as JsonObject)!;
            export = (JsonNode.Parse(utf8Json: exportBytes) as JsonObject)!;
        } catch (JsonException exception) {
            error.WriteLine(value: $"ERROR: the leg for {world} wrote malformed JSON into {scheduleDirectory}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return false;
        }

        if ((manifest is null) || (export is null)) {
            error.WriteLine(value: $"ERROR: the leg for {world} wrote a non-object document into {scheduleDirectory}.");

            return false;
        }

        if (manifest[propertyName: "schema"]?.GetValue<string>() != WorldScheduleSection.ManifestSchemaId) {
            error.WriteLine(value: $"ERROR: the leg for {world} did not write a {WorldScheduleSection.ManifestSchemaId} test-run manifest.");
            return false;
        }

        var exportTick = (manifest[propertyName: "exportTick"]?.GetValue<ulong>() ?? 0UL);
        var declaredTick = (export[propertyName: "tick"]?.GetValue<ulong>() ?? 0UL);

        if (exportTick != declaredTick) {
            error.WriteLine(value: $"ERROR: the leg for {world} recorded export tick {exportTick} but its export carries tick {declaredTick}.");

            return false;
        }

        if (!TryReadWorlds(
            error: error,
            manifest: manifest,
            scheduleDirectory: scheduleDirectory,
            world: world,
            worlds: out var worlds
        )) {
            return false;
        }

        reading = new TestReading(
            Echoes: ReadEchoes(manifest: manifest),
            ExportBytes: exportBytes,
            ExportTick: exportTick,
            ManifestBytes: manifestBytes,
            Submissions: ReadSubmissions(manifest: manifest),
            Truncated: (manifest[propertyName: "truncated"]?.GetValue<bool>() ?? false),
            Worlds: worlds!
        );

        return true;
    }
    // Every world the run exported, in the manifest's own order. The booted world is always listed too.
    // A sibling the run could not start refuses the leg by name —
    // a world the document armed and the host never ran measured nothing, which is a usage refusal rather than a
    // verdict.
    private static bool TryReadWorlds(string world, string scheduleDirectory, JsonObject manifest, TextWriter error, out IReadOnlyList<TestWorldExport>? worlds) {
        var read = new List<TestWorldExport>();

        worlds = null;

        if ((manifest[propertyName: "worlds"] is not JsonArray rows) || (rows.Count == 0)) {
            error.WriteLine(value: $"ERROR: the leg for {world} wrote no world exports in its test-run manifest.");
            return false;
        }

        foreach (var node in rows) {
            if (
                (node is not JsonObject entry) ||
                (entry[propertyName: "world"]?.GetValue<string>() is not { } name)
            ) {
                continue;
            }

            if (entry[propertyName: "started"]?.GetValue<bool>() != true) {
                error.WriteLine(value: $"ERROR: the leg for {world} armed '{name}' and the host did not start it — {(entry[propertyName: "reason"]?.GetValue<string>() ?? "no reason recorded")}.");

                return false;
            }

            var file = (entry[propertyName: "export"]?.GetValue<string>() ?? WorldScheduleSection.ExportFileNameFor(world: name));
            var path = Path.Combine(
                path1: scheduleDirectory,
                path2: file
            );

            if (!File.Exists(path: path)) {
                error.WriteLine(value: $"ERROR: the leg for {world} recorded an export for '{name}' at {file} and wrote none.");

                return false;
            }

            var bytes = File.ReadAllBytes(path: path);

            JsonObject? document;

            try {
                document = (JsonNode.Parse(utf8Json: bytes) as JsonObject);
            } catch (JsonException exception) {
                error.WriteLine(value: $"ERROR: the leg for {world} wrote malformed JSON for '{name}': {exception.Message.ReplaceLineEndings(replacementText: " ")}");

                return false;
            }

            if (document is null) {
                error.WriteLine(value: $"ERROR: the leg for {world} wrote a non-object export for '{name}'.");

                return false;
            }
            read.Add(item: new TestWorldExport(
                Document: entry[propertyName: "document"]?.GetValue<string>(),
                ExportBytes: bytes,
                ExportTick: (document[propertyName: "tick"]?.GetValue<ulong>() ?? 0UL),
                Verdicts: ReadVerdicts(export: document),
                World: name
            ));
        }

        worlds = read;

        return true;
    }
    private static IReadOnlyList<TestEcho> ReadEchoes(JsonObject manifest) {
        var echoes = new List<TestEcho>();

        if (manifest[propertyName: "echoes"] is JsonArray rows) {
            foreach (var row in rows) {
                if (row is JsonObject echo) {
                    echoes.Add(item: new TestEcho(
                        Message: (echo[propertyName: "message"]?.GetValue<string>() ?? string.Empty),
                        Rejected: (echo[propertyName: "rejected"]?.GetValue<bool>() ?? false)
                    ));
                }
            }
        }

        return echoes;
    }
    private static IReadOnlyList<TestSubmission> ReadSubmissions(JsonObject manifest) {
        var submissions = new List<TestSubmission>();

        if (manifest[propertyName: "submissions"] is JsonArray rows) {
            foreach (var row in rows) {
                if (row is JsonObject submission) {
                    submissions.Add(item: new TestSubmission(
                        Command: (submission[propertyName: "command"]?.GetValue<string>() ?? string.Empty),
                        Detail: submission[propertyName: "detail"]?.GetValue<string>(),
                        Outcome: (submission[propertyName: "outcome"]?.GetValue<string>() ?? string.Empty),
                        Principal: (submission[propertyName: "principal"]?.GetValue<string>() ?? string.Empty),
                        Tick: (submission[propertyName: "tick"]?.GetValue<ulong>() ?? 0UL)
                    ));
                }
            }
        }

        return submissions;
    }
    // The declaration comes from the export's own `state` member (the live document the run held) and the values
    // from its `resolved` member, so a verdict never written by the last tick reads its authored status code and
    // fails by name rather than going missing.
    private static IReadOnlyList<TestVerdict> ReadVerdicts(JsonObject export) {
        var verdicts = new List<TestVerdict>();

        if (export[propertyName: "state"]?[propertyName: "world"] is not JsonArray rows) {
            return verdicts;
        }

        foreach (var node in rows) {
            if (
                (node is not JsonObject row) ||
                (row[propertyName: "verdict"] is not JsonObject verdict) ||
                (row[propertyName: "name"]?.GetValue<string>() is not { } name)
            ) {
                continue;
            }

            var statusKey = (verdict[propertyName: "status"]?.GetValue<string>() ?? string.Empty);
            var saw = new List<string>();
            var status = WorldVerdict.NotEvaluated;
            ulong? firedTick = null;

            foreach (var cell in ((row[propertyName: "cells"] as JsonArray) ?? [])) {
                if (
                    (cell is not JsonObject declared) ||
                    (declared[propertyName: "key"]?.GetValue<string>() is not { } key)
                ) {
                    continue;
                }

                var value = ReadResolved(
                    export: export,
                    key: key,
                    row: name
                );

                if (key == WorldVerdict.FiredTickKey.Value) {
                    firedTick = ((ulong)Math.Max(
                        val1: 0L,
                        val2: value
                    ));
                } else if (key == statusKey) {
                    status = value;
                } else {
                    saw.Add(item: WorldVerdict.DescribeSeen(
                        key: key,
                        value: CellValue.Int(value: value)
                    ));
                }
            }

            // A witness holds what the gate saw of rows of its own kind, so it reads on after the verdict's cells.
            foreach (var witness in rows.OfType<JsonObject>()) {
                if (
                    (witness[propertyName: "witness"]?.GetValue<string>() != name) ||
                    (witness[propertyName: "name"]?.GetValue<string>() is not { } witnessName) ||
                    !Enum.TryParse<CellKind>(
                        result: out var kind,
                        value: witness[propertyName: "kind"]?.GetValue<string>()
                    )
                ) {
                    continue;
                }

                foreach (var cell in ((witness[propertyName: "cells"] as JsonArray) ?? [])) {
                    if (cell?[propertyName: "key"]?.GetValue<string>() is not { } key) {
                        continue;
                    }

                    saw.Add(item: WorldVerdict.DescribeSeen(
                        key: key,
                        value: ReadResolvedValue(
                            export: export,
                            key: key,
                            kind: kind,
                            row: witnessName
                        )
                    ));
                }
            }

            verdicts.Add(item: new TestVerdict(
                FiredTick: firedTick,
                Gate: (verdict[propertyName: "gate"]?.GetValue<string>() ?? string.Empty),
                Name: name,
                Saw: saw,
                Status: status
            ));
        }

        return verdicts;
    }
    private static long ReadResolved(JsonObject export, string row, string key) =>
        (FindResolvedCell(export: export, key: key, row: row)?["value"]?.GetValue<long>() ?? WorldVerdict.NotEvaluated);
    private static CellValue ReadResolvedValue(JsonObject export, string row, string key, CellKind kind) {
        var cell = FindResolvedCell(export: export, key: key, row: row);

        if (kind == CellKind.Text) { return CellValue.Text(value: cell?["text"]?.GetValue<string>()); }
        var raw = (cell?["value"]?.GetValue<long>() ?? 0L);

        return kind switch {
            CellKind.Bool => CellValue.Bool(value: (raw != 0)),
            CellKind.Fixed => CellValue.Fixed(rawBits: raw),
            _ => CellValue.Int(value: raw),
        };
    }
    private static JsonObject? FindResolvedCell(JsonObject export, string row, string key) {
        if (export[propertyName: "resolved"] is not JsonArray rows) {
            return null;
        }

        foreach (var node in rows) {
            if (
                (node is not JsonObject resolved) ||
                (resolved[propertyName: "row"]?.GetValue<string>() != row)
            ) {
                continue;
            }

            foreach (var cell in ((resolved[propertyName: "cells"] as JsonArray) ?? [])) {
                if (
                    (cell is JsonObject value) &&
                    (value[propertyName: "key"]?.GetValue<string>() == key)
                ) {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>Creates the <c>test</c> verb.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var filterOption = new Option<string[]>("--filter") { Description = "Run only worlds whose generated name or authored test name matches a glob (* and ?); repeat to select alternatives. Applies to reproduce, record and compare; reports the skipped count. No matches refuses." };
        var pathArgument = new Argument<string>(name: "path") {
            Description = "A .puck source whose `test` blocks generate test worlds, a test-world document, or a directory of either.",
        };
        var worldArtifactOption = new Option<string?>(name: "--world-artifact") {
            DefaultValueFactory = static _ => null,
            Description = "Boot this already-built Puck.World.dll instead of the build of src/Puck.World kept for this checkout's sources. A test/ops override: a caller that already built the executable neither builds nor keys one.",
        };
        var keepOption = new Option<string?>(name: "--keep") {
            DefaultValueFactory = static _ => null,
            Description = "Run in this directory instead of a scratch one and keep it: every generated test world, transcript, export and manifest, so a failing test is a world the ordinary tools can open.",
        };
        var cacheOption = new Option<string?>(name: "--cache-dir") {
            Description = "Override the device-cache root shared by all workers and runs. Absent uses the ordinary per-user Puck caches.",
        };
        var reproduceOption = new Option<bool>(name: "--reproduce") {
            DefaultValueFactory = static _ => false,
            Description = "Restore each scenario's starting checkpoint in place and replay its recorded input, requiring identical per-tick authority hashes, machine cores and final state exports without another boot.",
        };
        var jobsOption = CliOptions.Jobs(defaultValue: Math.Clamp(value: (Environment.ProcessorCount / 2), min: 1, max: 2), description: "Maximum concurrent worker processes (default: at most 2). Each worker process stays alive across its scenarios and boots each one fresh. Output remains in authored order.");
        var workersOption = new Option<string?>(name: "--workers") {
            DefaultValueFactory = static _ => null,
            Description = "Run every scenario once per worker count in this comma-separated list (Puck.World --workers; 0 is the machine's logical processor count), each a full authored host run, and require every count's per-tick authority hashes, delivered snapshot entries, exports and crossing log to match. Example: --workers 1,2,0.",
        };
        var recordOption = new Option<string?>(name: "--record") {
            DefaultValueFactory = static _ => null,
            Description = "Write each world's per-tick authority hashes, delivered snapshot entries, exports and crossing log (from the first worker count) under <dir>/<world>, replacing an earlier recording. A lane-local before-and-after oracle: keep it in scratch, never commit it.",
        };
        var compareOption = new Option<string?>(name: "--compare") {
            DefaultValueFactory = static _ => null,
            Description = "Require each world's per-tick authority hashes, delivered snapshot entries, exports and crossing log to match the recording --record wrote under <dir>/<world>.",
        };
        var jsonOption = CliOptions.Json(description: "Write one JSON object per world (world, path, verdict, comparison, hostBoots, exportedTicks, artifacts) and one of totals (worlds, verdict, diverged, workProcesses, hostBoots, exportedTicks); every text line goes to standard error.");
        var command = new Command(
            description: "Boot each test world through the real Puck.World executable and judge its verdict rows.",
            name: "test"
        ) {
            jobsOption,
            keepOption,
            cacheOption,
            pathArgument,
            reproduceOption,
            worldArtifactOption,
            workersOption,
            recordOption,
            compareOption,
            filterOption,
            jsonOption,
        };

        command.Detail(detail: """
            Boots each test world through the real Puck.World executable, headless and unpaced, to the tick its own
            schedule section declares its state export at, then reads that export's verdict rows: one line per verdict
            naming the gate and the values it saw. A `.puck` source is compiled first and the worlds its `test`
            blocks generate are what run. --reproduce restores the starting checkpoint and replays recorded input.
            --workers runs every scenario at each worker count and requires the counts to agree; --record and --compare
            keep a run's hashes, snapshots, exports and crossing log and compare a later run against them. Exit codes are 0 for
            every verdict passing; 1 for a failing verdict, a reproduction that diverged, or worker counts or a recording
            that disagree; and 2 for usage, a world declaring no schedule or no verdict row, a source that does not
            compile, a build or boot refusal, a leg that did not finish, or a missing recording.
            """);

        command.SetAction(action: parseResult => Run(
            filters: parseResult.GetValue(filterOption) ?? [],
            cacheDirectory: parseResult.GetValue(option: cacheOption),
            compare: parseResult.GetValue(option: compareOption),
            record: parseResult.GetValue(option: recordOption),
            workers: parseResult.GetValue(option: workersOption),
            jobs: parseResult.GetValue(option: jobsOption),
            json: parseResult.GetValue(option: jsonOption),
            keep: parseResult.GetValue(option: keepOption),
            path: (parseResult.GetValue(argument: pathArgument) ?? string.Empty),
            reproduce: parseResult.GetValue(option: reproduceOption),
            worldArtifact: parseResult.GetValue(option: worldArtifactOption)
        ));

        return command;
    }
}
