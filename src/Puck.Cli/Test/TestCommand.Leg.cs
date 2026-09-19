using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Puck.World;

namespace Puck.Cli.Test;

internal static partial class TestCommand {
    // The wall-clock net a stalled leg is allowed: the export tick's real-time duration at the document's rate,
    // plus a fixed allowance for process start-up and shutdown. Healthy legs run unpaced and finish much sooner;
    // retaining the real-time bound leaves complex worlds room to execute without making a hung host unbounded.
    private static TimeSpan LegBudget(ulong exportTick, int rateHz) => TimeSpan.FromSeconds(value: (20.0 + ((rateHz > 0)
        ? (exportTick / ((double)rateHz))
        : 0.0
    )));

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
    private static bool TryResolveArtifact(string? worldArtifact, string repositoryRoot, string runDirectory, out string? artifact) {
        if (worldArtifact is { }) {
            artifact = Path.GetFullPath(path: worldArtifact);

            if (!File.Exists(path: artifact)) {
                Console.Error.WriteLine(value: $"ERROR: --world-artifact {artifact} does not exist.");

                return false;
            }

            return true;
        }

        if (!WorldArtifactBuild.TryBuild(
            artifact: out var built,
            build: out var build,
            error: out var buildError,
            outputDirectory: Path.Combine(
                path1: runDirectory,
                path2: "build"
            ),
            repositoryRoot: repositoryRoot,
            timeout: BuildBudget,
            verb: "test"
        )) {
            artifact = built;
            Console.Error.WriteLine(value: $"ERROR: {buildError}");

            if (build is not null) {
                Console.Error.WriteLine(value: build.Stdout);
                Console.Error.WriteLine(value: build.Stderr);
            }

            return false;
        }

        artifact = built;

        return true;
    }
    // One leg: boot the real executable headless against its own state and schedule directories, fence past the
    // export tick, quit, then read what the world wrote.
    private static bool TryRunLeg(string world, string artifact, string legDirectory, ulong exportTick, int rateHz, TextWriter error, out TestReading? reading) {
        reading = null;

        var scheduleDirectory = Path.Combine(
            path1: legDirectory,
            path2: "out"
        );
        var script = new StringBuilder();

        _ = script.Append(value: "world.wait ").Append(value: (exportTick + 2UL).ToString(provider: CultureInfo.InvariantCulture)).Append(value: Environment.NewLine);
        _ = script.Append(value: "wire.errors").Append(value: Environment.NewLine);
        _ = script.Append(value: "quit").Append(value: Environment.NewLine);

        _ = Directory.CreateDirectory(path: legDirectory);

        CliProcessResult process;

        try {
            process = CliProcess.RunCaptured(
                fileName: "dotnet",
                arguments: [
                    artifact,
                    "--world", world,
                    "--headless", "true",
                    "--unpaced", "true",
                    "--exit-after-seconds", ((int)LegBudget(
                        exportTick: exportTick,
                        rateHz: rateHz
                    ).TotalSeconds).ToString(provider: CultureInfo.InvariantCulture),
                    "--state-dir", Path.Combine(
                        path1: legDirectory,
                        path2: "state"
                    ),
                    "--schedule-dir", scheduleDirectory,
                ],
                input: script.ToString(),
                timeout: (LegBudget(
                    exportTick: exportTick,
                    rateHz: rateHz
                ) + TimeSpan.FromSeconds(value: 30))
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            error.WriteLine(value: $"ERROR: could not start the leg for {world}: {exception.Message.ReplaceLineEndings(replacementText: " ")}");

            return false;
        }

        File.WriteAllText(
            contents: process.Stdout,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            path: Path.Combine(
                path1: legDirectory,
                path2: "stdout.log"
            )
        );
        File.WriteAllText(
            contents: process.Stderr,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            path: Path.Combine(
                path1: legDirectory,
                path2: "stderr.log"
            )
        );

        if (
            process.TimedOut ||
            (process.ExitCode != 0)
        ) {
            error.WriteLine(value: $"ERROR: the leg for {world} {(process.TimedOut
                ? "timed out"
                : $"exited {process.ExitCode.ToString(provider: CultureInfo.InvariantCulture)}")}; transcripts under {legDirectory}.");
            // The transcripts are removed with the run directory unless --keep, so the one line that says WHY has to
            // travel now rather than living in a directory the finally block is about to delete.
            error.WriteLine(value: Tail(text: process.Stderr));

            return false;
        }

        return TryRead(
            error: error,
            reading: out reading,
            scheduleDirectory: scheduleDirectory,
            world: world
        );
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

        var exportTick = (manifest[propertyName: "exportTick"]?.GetValue<ulong>() ?? 0UL);
        var declaredTick = (export[propertyName: "tick"]?.GetValue<ulong>() ?? 0UL);

        if (exportTick != declaredTick) {
            error.WriteLine(value: $"ERROR: the leg for {world} recorded export tick {exportTick} but its export carries tick {declaredTick}.");

            return false;
        }

        reading = new TestReading(
            Echoes: ReadEchoes(manifest: manifest),
            ExportBytes: exportBytes,
            ExportTick: exportTick,
            ManifestBytes: manifestBytes,
            Submissions: ReadSubmissions(manifest: manifest),
            Truncated: (manifest[propertyName: "truncated"]?.GetValue<bool>() ?? false),
            Verdicts: ReadVerdicts(export: export)
        );

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

            foreach (var cell in (row[propertyName: "cells"] as JsonArray ?? [])) {
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
                        kind: CellKind.Int,
                        raw: value
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

                foreach (var cell in (witness[propertyName: "cells"] as JsonArray ?? [])) {
                    if (cell?[propertyName: "key"]?.GetValue<string>() is not { } key) {
                        continue;
                    }

                    saw.Add(item: WorldVerdict.DescribeSeen(
                        key: key,
                        kind: kind,
                        raw: ReadResolved(
                            export: export,
                            key: key,
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
    private static long ReadResolved(JsonObject export, string row, string key) {
        if (export[propertyName: "resolved"] is not JsonArray rows) {
            return WorldVerdict.NotEvaluated;
        }

        foreach (var node in rows) {
            if (
                (node is not JsonObject resolved) ||
                (resolved[propertyName: "row"]?.GetValue<string>() != row)
            ) {
                continue;
            }

            foreach (var cell in (resolved[propertyName: "cells"] as JsonArray ?? [])) {
                if (
                    (cell is JsonObject value) &&
                    (value[propertyName: "key"]?.GetValue<string>() == key)
                ) {
                    return (value[propertyName: "value"]?.GetValue<long>() ?? WorldVerdict.NotEvaluated);
                }
            }
        }

        return WorldVerdict.NotEvaluated;
    }

    /// <summary>Creates the <c>test</c> verb.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var pathArgument = new Argument<string>(name: "path") {
            Description = "A .puck source whose `test` blocks generate test worlds, a test-world document, or a directory of either.",
        };
        var hostOption = new Option<TestHost>(name: "--host") {
            DefaultValueFactory = static _ => TestHost.Server,
            Description = "Which host boots the worlds. Only `server` — the real Puck.World executable, headless — is implemented; `browser` is refused by name.",
        };
        var worldArtifactOption = new Option<string?>(name: "--world-artifact") {
            DefaultValueFactory = static _ => null,
            Description = "Boot this already-built Puck.World.dll instead of building src/Puck.World into the run's own output. A test/ops override: a caller that already built the executable does not pay for a second build.",
        };
        var keepOption = new Option<string?>(name: "--keep") {
            DefaultValueFactory = static _ => null,
            Description = "Run in this directory instead of a scratch one and keep it: every generated test world, transcript, export and manifest, so a failing test is a world the ordinary tools can open.",
        };
        var reproduceOption = new Option<bool>(name: "--reproduce") {
            DefaultValueFactory = static _ => false,
            Description = "Run every test world a second time and require byte-identical state exports and schedule manifests. Use for determinism qualification; ordinary authored tests run once.",
        };
        var jobsOption = new Option<int>(name: "--jobs") {
            DefaultValueFactory = static _ => Math.Min(
                val1: 8,
                val2: Math.Max(
                    val1: 1,
                    val2: (Environment.ProcessorCount / 2)
                )
            ),
            Description = "Maximum test worlds to run concurrently. Output remains in authored order.",
        };
        var command = new Command(
            description: """
            Boots each test world through the real Puck.World executable, headless and unpaced, to the tick its own
            schedule section declares its state export at, then reads that export's verdict rows: one line per verdict
            naming the gate and the values it saw. A `.puck` source is compiled first and the worlds its `test`
            blocks generate are what run. --reproduce adds a second run and requires byte-identical output. Exit
            codes are 0 for every verdict passing, 1 for a failing verdict, and 2 for usage, a world declaring no
            schedule or no verdict row, a source that does not compile, a build or boot refusal, or a reproduction
            mismatch.
            """,
            name: "test"
        ) {
            hostOption,
            jobsOption,
            keepOption,
            pathArgument,
            reproduceOption,
            worldArtifactOption,
        };

        command.SetAction(action: parseResult => Run(
            host: parseResult.GetValue(option: hostOption),
            jobs: parseResult.GetValue(option: jobsOption),
            keep: parseResult.GetValue(option: keepOption),
            path: (parseResult.GetValue(argument: pathArgument) ?? string.Empty),
            reproduce: parseResult.GetValue(option: reproduceOption),
            worldArtifact: parseResult.GetValue(option: worldArtifactOption)
        ));

        return command;
    }
}
