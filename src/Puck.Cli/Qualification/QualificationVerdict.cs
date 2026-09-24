using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Documents;
using Puck.Cli.Canary;
using Puck.Cli.Counters;
using Puck.World;

namespace Puck.Cli.Qualification;

/// <summary>What a qualification check concluded about one cell.</summary>
[JsonConverter(typeof(StrictEnumConverter<QualificationOutcome>))]
internal enum QualificationOutcome {
    /// <summary>Every check the cell makes held.</summary>
    Pass,
    /// <summary>A check observed the package misbehave.</summary>
    Fail,
    /// <summary>The cell could not run here: the machine lacks the backend or device, a shader tool the profile lets a
    /// World find is absent, or the run's own infrastructure refused.</summary>
    Blocked,
}
/// <summary>One settled <c>pipeline.inspect</c> response.</summary>
/// <param name="Owned">The bytes the instance owns: its installed graph plus replaced objects and held images still
/// waiting to retire.</param>
/// <param name="Steady">The installed graph's steady-state bytes.</param>
/// <param name="Peak">The peak a reload of the installed graph would reach from what the instance owns now.</param>
/// <param name="Budget">The budget a replacement's peak is refused against.</param>
internal sealed record QualificationInspection(long Owned, long Steady, long Peak, long Budget);
/// <summary>One <c>pipeline.wait</c> outcome.</summary>
/// <param name="Phase">The phase waited for, as the World spells it: <c>installed</c>, <c>counted 4</c>, …</param>
/// <param name="Outcome"><c>reached</c>, <c>failed</c>, <c>unsupported</c> or <c>timed out</c>.</param>
internal sealed record QualificationWait(string Phase, string Outcome);
/// <summary>Everything a cell's transcript says, read without judging it.</summary>
/// <param name="Counters">The <c>world.counters --json</c> readings, in order.</param>
/// <param name="CountersRefusals">Why each reading that could not be read was refused.</param>
/// <param name="Inspections">The <c>pipeline.inspect</c> responses, in order.</param>
/// <param name="Releases">The inspections refused because the instance is no longer rendered.</param>
/// <param name="Waits">The <c>pipeline.wait</c> outcomes, in order.</param>
/// <param name="ValidationMessages">Every validation-layer line: a <c>[vulkan-debug] validation</c> message or any
/// <c>[d3d12-debug]</c> message.</param>
/// <param name="CandidateRefusals">Every pipeline candidate the instance refused.</param>
/// <param name="CompilerAbsent">The line saying a pipeline could not compile because a shader tool is absent, or
/// <see langword="null"/>.</param>
/// <param name="MemoryProfile">The device's memory profile as the first inspection echoes it, or
/// <see langword="null"/>.</param>
internal sealed record QualificationReadings(
    IReadOnlyList<WorldCountersRun> Counters,
    IReadOnlyList<string> CountersRefusals,
    IReadOnlyList<QualificationInspection> Inspections,
    int Releases,
    IReadOnlyList<QualificationWait> Waits,
    IReadOnlyList<string> ValidationMessages,
    IReadOnlyList<string> CandidateRefusals,
    string? CompilerAbsent,
    string? MemoryProfile
) {
    /// <summary>Gets the most bytes the instance owned or planned to own at any inspection, or <see langword="null"/>
    /// when nothing was inspected.</summary>
    public long? PeakOwnedPipelineBytes => ((Inspections.Count == 0)
        ? null
        : Inspections.Max(selector: static inspection => Math.Max(
            val1: inspection.Owned,
            val2: inspection.Peak
        )));
}
/// <summary>A cell's verdict: its outcome and every finding behind it, one line each.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Findings">Why the cell failed or was blocked; empty for a pass.</param>
internal sealed record QualificationVerdict(QualificationOutcome Outcome, IReadOnlyList<string> Findings);
/// <summary>
/// Reads a cell's transcript and judges it against what its script produces and the cell's threshold. The judgement
/// is a pure function of the readings, so a law can hold it to fake ones.
/// </summary>
internal static partial class QualificationJudge {
    private const string CandidateRefusedInfix = " GPU candidate refused: ";
    private const string CreatedKindPrefix = "gpu.created.";
    private const string Direct3D12DebugPrefix = "[d3d12-debug] ";
    private const string VulkanValidationPrefix = "[vulkan-debug] validation ";

    // "[pipeline.inspect: <name>; owned=N bytes; steady=N bytes; peak=N bytes; budget=N bytes; …", the record's first
    // line (ShaderPipelineRenderNode.TryAppendInspection).
    [GeneratedRegex(pattern: @"^\[pipeline\.inspect: [^;]+; owned=(?<owned>\d+) bytes; steady=(?<steady>\d+) bytes; peak=(?<peak>\d+) bytes; budget=(?<budget>\d+) bytes; ")]
    private static partial Regex Inspection();

    /// <summary>Reads a cell's transcript.</summary>
    /// <param name="cell">The cell, whose backend and extent every counters reading must report.</param>
    /// <param name="process">The finished World process.</param>
    /// <param name="compiler">The shader toolchain identity recorded with each counters reading.</param>
    /// <returns>The readings.</returns>
    public static QualificationReadings Read(QualificationCell cell, CliProcessResult process, string compiler) {
        var counters = new List<WorldCountersRun>();
        var refusals = new List<string>();
        var inspections = new List<QualificationInspection>();
        var waits = new List<QualificationWait>();
        var validation = new List<string>();
        var candidates = new List<string>();
        var releases = 0;
        string? compilerAbsent = null;
        string? memory = null;
        var released = ((cell.Workload.Pipeline is { } pipeline)
            ? $"[pipeline.inspect: '{pipeline.Instance}' has no rendered pipeline instance]"
            : null);

        foreach (var output in process.OutputLines) {
            var line = output.Line;

            if (output.Stream == CliProcessOutputStream.Stdout) {
                if (CountersReading.IsReading(line: line)) {
                    if (CountersReading.TryReadLine(
                        backend: cell.Backend,
                        compiler: compiler,
                        height: cell.Resolution.Height,
                        line: line,
                        reason: out var reason,
                        run: out var run,
                        width: cell.Resolution.Width
                    )) {
                        counters.Add(item: run);
                    } else {
                        refusals.Add(item: reason);
                    }
                } else if (Inspection().Match(input: line) is { Success: true } inspection) {
                    inspections.Add(item: new QualificationInspection(
                        Budget: Bytes(group: "budget", match: inspection),
                        Owned: Bytes(group: "owned", match: inspection),
                        Peak: Bytes(group: "peak", match: inspection),
                        Steady: Bytes(group: "steady", match: inspection)
                    ));
                } else if ((memory is null) && line.TrimStart().StartsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: "memory: "
                )) {
                    memory = line.Trim();
                }

                continue;
            }

            if (CanaryCommand.PipelineWaitOutcome().Match(input: line) is { Success: true } wait) {
                waits.Add(item: new QualificationWait(
                    Outcome: wait.Groups["outcome"].Value,
                    Phase: wait.Groups["phase"].Value
                ));
            }
            if (line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: VulkanValidationPrefix
            ) || line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: Direct3D12DebugPrefix
            )) {
                validation.Add(item: line);
            }
            if (line.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "[pipeline: "
            ) && line.Contains(
                comparisonType: StringComparison.Ordinal,
                value: CandidateRefusedInfix
            )) {
                candidates.Add(item: line);
            }
            if ((compilerAbsent is null) && CanaryCommand.PipelineUnsupported().IsMatch(input: line)) {
                compilerAbsent = line;
            }
            if ((released is not null) && string.Equals(
                a: line,
                b: released,
                comparisonType: StringComparison.Ordinal
            )) {
                releases++;
            }
        }

        return new QualificationReadings(
            CandidateRefusals: candidates,
            CompilerAbsent: compilerAbsent,
            Counters: counters,
            CountersRefusals: refusals,
            Inspections: inspections,
            MemoryProfile: memory,
            Releases: releases,
            ValidationMessages: validation,
            Waits: waits
        );
    }
    /// <summary>Judges a cell.</summary>
    /// <param name="cell">The cell, whose threshold row holds its memory limit.</param>
    /// <param name="expectation">What the cell's script produces.</param>
    /// <param name="leg">How the leg ended.</param>
    /// <param name="legDetail">The leg's one line of detail when it did not complete.</param>
    /// <param name="readings">The transcript's readings, or <see langword="null"/> when no World process ran.</param>
    /// <param name="debugLayers">Whether the cell's backend ran with its validation layer on.</param>
    /// <param name="compiler">Where the profile lets a World find a shader compiler.</param>
    /// <returns>The verdict.</returns>
    public static QualificationVerdict Judge(QualificationCell cell, QualificationExpectation expectation, WorldOffscreenLegStatus leg, string legDetail, QualificationReadings? readings, bool debugLayers, ReleaseCompilerDiscovery compiler) {
        switch (leg) {
            case WorldOffscreenLegStatus.NotRun:
            case WorldOffscreenLegStatus.Unsupported:
                return new QualificationVerdict(
                    Findings: [legDetail],
                    Outcome: QualificationOutcome.Blocked
                );
        }

        var compilerAbsent = readings?.CompilerAbsent;

        if (
            (compilerAbsent is null) &&
            (readings?.Waits.FirstOrDefault(predicate: static wait => (wait.Outcome == "unsupported")) is { } unsupported)
        ) {
            compilerAbsent = $"pipeline.wait {unsupported.Phase}: unsupported";
        }
        if (compilerAbsent is not null) {
            return ((compiler == ReleaseCompilerDiscovery.Path)
                ? new QualificationVerdict(
                    Findings: [$"a shader tool is absent: {compilerAbsent}"],
                    Outcome: QualificationOutcome.Blocked
                )
                : new QualificationVerdict(
                    Findings: [$"the World asked for a shader compiler, which the profile's compiler policy None withholds: {compilerAbsent}"],
                    Outcome: QualificationOutcome.Fail
                ));
        }

        var findings = new List<string>();

        if (leg != WorldOffscreenLegStatus.Completed) {
            findings.Add(item: legDetail);
        }
        if (readings is not null) {
            Judge(
                cell: cell,
                debugLayers: debugLayers,
                expectation: expectation,
                findings: findings,
                readings: readings
            );
        }

        return new QualificationVerdict(
            Findings: findings,
            Outcome: ((findings.Count == 0)
                ? QualificationOutcome.Pass
                : QualificationOutcome.Fail
            )
        );
    }
    /// <summary>Names every GPU object created between two readings: each <c>gpu.created.*</c> count that rose, or that
    /// the later reading has and the earlier one lacks.</summary>
    /// <param name="before">The earlier reading.</param>
    /// <param name="after">The later reading.</param>
    /// <returns>One line per count that rose, naming its source, node and kind; empty when nothing was created.</returns>
    public static IReadOnlyList<string> Created(WorldCountersRun before, WorldCountersRun after) {
        var earlier = before.Counts.Where(predicate: IsCreated).ToDictionary(
            elementSelector: static count => count.Value,
            keySelector: Key
        );
        var created = new List<string>();

        foreach (var count in after.Counts.Where(predicate: IsCreated)) {
            var was = earlier.GetValueOrDefault(key: Key(count: count));

            if (count.Value > was) {
                created.Add(item: $"{Key(count: count)} {was.ToString(provider: CultureInfo.InvariantCulture)} -> {count.Value.ToString(provider: CultureInfo.InvariantCulture)}");
            }
        }

        return created;
    }

    private static void Judge(QualificationCell cell, QualificationExpectation expectation, QualificationReadings readings, bool debugLayers, List<string> findings) {
        if (readings.Waits.Count != expectation.ArmedWaits) {
            findings.Add(item: $"{readings.Waits.Count} pipeline.wait outcome(s) for the {expectation.ArmedWaits} the script arms");
        }
        foreach (var wait in readings.Waits.Where(predicate: static wait => (wait.Outcome != "reached"))) {
            findings.Add(item: $"pipeline.wait {wait.Phase}: {wait.Outcome}");
        }
        foreach (var refusal in readings.CountersRefusals) {
            findings.Add(item: $"a world.counters reading was refused: {refusal}");
        }
        if (readings.Counters.Count != expectation.CountersReadings) {
            findings.Add(item: $"{readings.Counters.Count} world.counters reading(s) for the {expectation.CountersReadings} the script takes");
        } else {
            for (var window = 0; (window < expectation.SoakWindows.Count); window++) {
                var (before, after) = expectation.SoakWindows[window];

                foreach (var created in Created(
                    after: readings.Counters[after],
                    before: readings.Counters[before]
                )) {
                    findings.Add(item: $"soak window {(window + 1)} created a GPU object: {created}");
                }
            }
        }
        if (readings.Inspections.Count != expectation.Inspections) {
            findings.Add(item: $"{readings.Inspections.Count} pipeline.inspect response(s) for the {expectation.Inspections} the script takes");
        }
        for (var index = 0; (index < readings.Inspections.Count); index++) {
            var inspection = readings.Inspections[index];

            if (inspection.Owned != inspection.Steady) {
                findings.Add(item: $"inspection {(index + 1)}: the settled instance owns {inspection.Owned} bytes, not exactly its installed graph's {inspection.Steady}");
            }
        }
        if (
            (cell.Threshold.PeakOwnedPipelineBytes is { } limit) &&
            (readings.PeakOwnedPipelineBytes is { } peak) &&
            (peak > limit)
        ) {
            findings.Add(item: $"the pipeline instance owned or planned {peak} bytes, over the cell's threshold of {limit}");
        }
        if (readings.Releases != expectation.Releases) {
            findings.Add(item: $"{readings.Releases} unload(s) released the instance, of the {expectation.Releases} the script makes");
        }
        foreach (var candidate in readings.CandidateRefusals) {
            findings.Add(item: candidate);
        }
        if (
            debugLayers &&
            (readings.ValidationMessages.Count != 0)
        ) {
            findings.Add(item: $"{readings.ValidationMessages.Count} validation-layer message(s), the first: {readings.ValidationMessages[0]}");
        }
    }
    private static long Bytes(Match match, string group) => long.Parse(
        provider: CultureInfo.InvariantCulture,
        s: match.Groups[group].Value
    );
    private static bool IsCreated(WorldCount count) => (
        (count.Class != WorkClass.Pacing) &&
        count.Kind.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: CreatedKindPrefix
        )
    );
    private static string Key(WorldCount count) => $"{count.Source}{((count.Node is null) ? string.Empty : $"/{count.Node}")} {count.Kind}";
}
