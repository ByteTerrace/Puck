using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.Cli.Qualification;

/// <summary>One cell of the stability matrix: a workload at one resolution on one backend, with its threshold row.</summary>
/// <param name="Workload">The workload.</param>
/// <param name="Resolution">The offscreen output extent.</param>
/// <param name="Backend">The backend.</param>
/// <param name="Threshold">The cell's threshold row.</param>
internal sealed record QualificationCell(
    QualificationWorkload Workload,
    QualificationResolution Resolution,
    string Backend,
    QualificationThreshold Threshold
) {
    /// <summary>Gets the cell's name, <c>workload/WIDTHxHEIGHT/backend</c>.</summary>
    public string Id => $"{Workload.Name}/{Resolution.Label}/{Backend}";
    /// <summary>Gets the name of the cell's directory inside a run.</summary>
    public string DirectoryName => $"{Workload.Name}-{Resolution.Label}-{Backend}";
}
/// <summary>The readings one cell's script produces, which the verdict holds the transcript to.</summary>
/// <param name="CountersReadings">The <c>world.counters --json</c> responses, in order.</param>
/// <param name="SoakWindows">The pairs of readings, by index, across which no GPU object may be created.</param>
/// <param name="Inspections">The <c>pipeline.inspect</c> responses, each taken once the instance has settled.</param>
/// <param name="Releases">The <c>pipeline.inspect</c> refusals that prove an unloaded instance was released; the
/// runner's <c>wire.errors</c> counts these and nothing else as rejected.</param>
/// <param name="ArmedWaits">The <c>pipeline.wait</c> commands the script arms, each of which must report
/// <c>reached</c>.</param>
/// <param name="WorldReloads">The <c>world.reload</c> commands the script makes, each of which must answer that it
/// applied.</param>
internal sealed record QualificationExpectation(
    int CountersReadings,
    IReadOnlyList<(int Before, int After)> SoakWindows,
    int Inspections,
    int Releases,
    int ArmedWaits,
    int WorldReloads
);
/// <summary>A cell's console script and what it must produce.</summary>
/// <param name="Text">The script, one command per line, each ending in a line feed.</param>
/// <param name="Expectation">The readings the script produces.</param>
internal sealed record QualificationScript(string Text, QualificationExpectation Expectation);
/// <summary>What a pipeline workload's world authors for its instance: the layout row showing it and the source its
/// row loads, both read from the world document inside the package.</summary>
/// <param name="Layout">The <c>views.layouts</c> row, as authored.</param>
/// <param name="Source">The <c>views.pipelines</c> row's source, relative to the world document.</param>
internal sealed record QualificationPipelineRows(JsonObject Layout, string Source);
/// <summary>
/// Expands a release profile into its stability matrix and writes what each cell runs: the overlay document that boots
/// the workload's world offscreen at the cell's extent, and the console script that warms it up, soaks it, reloads it
/// and churns its pipeline the profile's number of times. The script is a pure function of the cell and the rows the
/// world authors, so a law can read it without a device.
/// </summary>
internal static class QualificationPlan {
    /// <summary>The seconds a <c>pipeline.wait</c> may take, the longest the World accepts, of presentation time.</summary>
    public const int WaitSeconds = 40;
    /// <summary>The seconds the script's <c>world.wait ready</c> may wait for the engine's pipeline set to build and
    /// produce its first frame; every workload's timeout outlasts it.</summary>
    public const int ReadySeconds = 180;

    private const string WorldSuffix = ".world.json";

    /// <summary>Expands the profile's stability matrix: backend by backend in the profile's order, then workload, then
    /// resolution.</summary>
    /// <param name="profile">A validated profile.</param>
    /// <returns>Every cell, each with its threshold row.</returns>
    public static IReadOnlyList<QualificationCell> Cells(ReleaseProfile profile) {
        var cells = new List<QualificationCell>();

        foreach (var backend in profile.Backends) {
            foreach (var workload in profile.Workloads) {
                foreach (var resolution in profile.Resolutions) {
                    cells.Add(item: new QualificationCell(
                        Backend: backend,
                        Resolution: resolution,
                        Threshold: profile.Thresholds.Single(predicate: threshold => (
                            string.Equals(a: threshold.Workload, b: workload.Name, comparisonType: StringComparison.Ordinal) &&
                            string.Equals(a: threshold.Backend, b: backend, comparisonType: StringComparison.Ordinal) &&
                            (threshold.Width == resolution.Width) &&
                            (threshold.Height == resolution.Height)
                        )),
                        Workload: workload
                    ));
                }
            }
        }

        return cells;
    }
    /// <summary>Gets the file name of a cell's overlay document, written beside the workload's world.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The file name.</returns>
    public static string OverlayFileName(QualificationCell cell) =>
        $"qualify-{cell.Workload.Name}-{cell.Resolution.Label}{WorldSuffix}";
    /// <summary>Writes a cell's overlay document: the workload's world as its basis, presented offscreen at the cell's
    /// extent. Every other member, relative paths included, stays the world's own, since the overlay sits beside it.</summary>
    /// <param name="cell">The cell.</param>
    /// <returns>The overlay's JSON text.</returns>
    public static string Overlay(QualificationCell cell) {
        var world = cell.Workload.World;
        var basis = Path.GetFileName(path: world)[..^WorldSuffix.Length];
        var overlay = new JsonObject {
            ["schema"] = "puck.world.definition.v1",
            ["basis"] = basis,
            ["host"] = new JsonObject {
                ["presentation"] = "offscreen",
                ["width"] = cell.Resolution.Width,
                ["height"] = cell.Resolution.Height,
            },
        };

        return $"{overlay.ToJsonString(options: new JsonSerializerOptions { WriteIndented = true })}\n";
    }
    /// <summary>Reads what a pipeline workload's world authors for its instance.</summary>
    /// <param name="worldText">The world document's JSON text.</param>
    /// <param name="pipeline">The workload's pipeline.</param>
    /// <param name="rows">The rows, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why the rows cannot be read, or empty.</param>
    /// <returns><see langword="true"/> when the world authors the layout row and a pipeline row with a source.</returns>
    public static bool TryReadRows(string worldText, QualificationPipeline pipeline, [NotNullWhen(returnValue: true)] out QualificationPipelineRows? rows, out string reason) {
        rows = null;

        JsonNode? root;

        try {
            root = JsonNode.Parse(json: worldText);
        } catch (JsonException exception) {
            reason = $"the world is not JSON: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return false;
        }

        var views = root?["views"];
        var layout = Row(
            name: pipeline.Layout,
            rows: views?["layouts"]
        );
        var source = (Row(
            name: pipeline.Instance,
            rows: views?["pipelines"]
        )?["source"] as JsonValue);

        if (layout is null) {
            reason = $"the world authors no views.layouts row named '{pipeline.Layout}'";

            return false;
        }
        if ((source is null) || !source.TryGetValue<string>(value: out var sourceText)) {
            reason = $"the world authors no views.pipelines row named '{pipeline.Instance}' with a source";

            return false;
        }

        rows = new QualificationPipelineRows(
            Layout: layout,
            Source: sourceText
        );
        reason = string.Empty;

        return true;
    }
    /// <summary>Writes a cell's console script.</summary>
    /// <param name="cell">The cell.</param>
    /// <param name="rows">What the world authors for the cell's pipeline instance, or <see langword="null"/> when the
    /// workload churns none.</param>
    /// <param name="script">The script, or <see langword="null"/> when the method returns <see langword="false"/>.</param>
    /// <param name="reason">Why no script can be written, or empty.</param>
    /// <returns><see langword="true"/> when the script is written.</returns>
    /// <exception cref="ArgumentException">The workload churns a pipeline and <paramref name="rows"/> is
    /// <see langword="null"/>.</exception>
    public static bool TryWrite(QualificationCell cell, QualificationPipelineRows? rows, [NotNullWhen(returnValue: true)] out QualificationScript? script, out string reason) {
        script = null;

        var workload = cell.Workload;
        var text = new StringBuilder();
        var counters = 0;
        var windows = new List<(int, int)>();
        var inspections = 0;
        var releases = 0;
        var waits = 0;
        var worldReloads = 0;

        void Line(string line) => text.Append(value: line).Append(value: '\n');
        void Counters() {
            Line(line: "world.counters --json");
            counters++;
        }
        void Soak(string why) {
            Line(line: $"# {why}: no GPU object is created across {workload.SoakTicks} ticks.");
            Counters();
            Line(line: $"world.wait {Number(value: workload.SoakTicks)}");
            Counters();
            windows.Add(item: ((counters - 2), (counters - 1)));
        }

        Line(line: $"# puck qualify: {cell.Id}, generated from the release profile. Lengths are ticks and frames, never time.");
        Line(line: "# Every frame renders every pass, so a soak exercises the whole frame.");
        Line(line: "world.cadence off");
        Line(line: "# A clean install builds the engine's pipelines on a cold driver cache: warm up from readiness, not boot.");
        Line(line: $"world.wait ready {Number(value: ReadySeconds)}");

        if (workload.Pipeline is not { } pipeline) {
            Line(line: $"world.wait {Number(value: workload.WarmupTicks)}");
            Soak(why: "Steady state");

            if (workload.WorldReloads > 0) {
                for (var reload = 1; (reload <= workload.WorldReloads); reload++) {
                    Line(line: $"# World reload {Number(value: reload)} of {Number(value: workload.WorldReloads)}.");
                    Line(line: "world.reload");
                    worldReloads++;
                    Line(line: $"world.wait {Number(value: workload.WarmupTicks)}");
                }

                Soak(why: "After the reloads");
            }
        } else {
            if (rows is null) {
                throw new ArgumentException(
                    message: $"The workload '{workload.Name}' churns a pipeline, so its rows are required.",
                    paramName: nameof(rows)
                );
            }
            if (!TryLayouts(
                full: out var full,
                half: out var half,
                halfExtent: out var halfExtent,
                instance: pipeline.Instance,
                layout: rows.Layout,
                reason: out reason,
                resolution: cell.Resolution,
                unloaded: out var unloaded,
                fullExtent: out var fullExtent
            )) {
                return false;
            }

            var name = pipeline.Instance;

            void Wait(string phase) {
                Line(line: $"pipeline.wait {name} {phase} {Number(value: WaitSeconds)}");
                waits++;
            }
            void Settle(string why) {
                Line(line: $"# {why}: after a reset and {pipeline.SettleFrames} counted submissions the instance owns exactly its installed graph.");
                Line(line: $"pipeline.reset {name}");
                Wait(phase: $"counted {Number(value: pipeline.SettleFrames)}");
                Line(line: $"pipeline.inspect {name}");
                inspections++;
            }

            Wait(phase: "compiled");
            Wait(phase: "installed");
            Line(line: $"world.wait {Number(value: workload.WarmupTicks)}");
            Settle(why: "Warmed up");
            Soak(why: "Steady state");
            Settle(why: "Soaked");

            for (var reload = 1; (reload <= pipeline.Reloads); reload++) {
                Line(line: $"# Reload {Number(value: reload)} of {Number(value: pipeline.Reloads)}: the source recompiles and a candidate replaces the installed graph.");
                Line(line: $"pipeline.reload {name}");
                Wait(phase: "installed");
                Settle(why: "Reloaded");
            }
            for (var resize = 1; (resize <= pipeline.Resizes); resize++) {
                Line(line: $"# Resize {Number(value: resize)} of {Number(value: pipeline.Resizes)}: the slot shrinks to half on each axis, then returns.");
                Line(line: $"world.row.set views.layouts {half}");
                Wait(phase: $"resized {Number(value: halfExtent.Width)} {Number(value: halfExtent.Height)}");
                Settle(why: "Shrunk");
                Line(line: $"world.row.set views.layouts {full}");
                Wait(phase: $"resized {Number(value: fullExtent.Width)} {Number(value: fullExtent.Height)}");
                Settle(why: "Restored");
            }
            for (var load = 1; (load <= pipeline.Loads); load++) {
                Line(line: $"# Unload and load {Number(value: load)} of {Number(value: pipeline.Loads)}: the slot stops showing the instance and its row is removed, which releases it; the refused inspection proves the release.");
                Line(line: $"world.row.set views.layouts {unloaded}");
                Line(line: $"world.row.remove views.pipelines {name}");
                Line(line: $"pipeline.inspect {name}");
                releases++;
                Line(line: $"pipeline.load {name} {rows.Source}");
                Line(line: $"world.row.set views.layouts {full}");
                Wait(phase: "installed");
                Settle(why: "Loaded again");
            }

            Soak(why: "After the churn");
        }

        script = new QualificationScript(
            Expectation: new QualificationExpectation(
                ArmedWaits: waits,
                CountersReadings: counters,
                Inspections: inspections,
                Releases: releases,
                SoakWindows: windows,
                WorldReloads: worldReloads
            ),
            Text: text.ToString()
        );
        reason = string.Empty;

        return true;
    }

    private static JsonObject? Row(JsonNode? rows, string name) =>
        (rows as JsonArray)?.OfType<JsonObject>().FirstOrDefault(predicate: row => (((row["name"] as JsonValue)?.TryGetValue<string>(value: out var rowName) == true) && string.Equals(
            a: rowName,
            b: name,
            comparisonType: StringComparison.Ordinal
        )));
    // The layout row three ways, each compact on one line for world.row.set: as authored, with the instance's slot at
    // half its extent on each axis, and with the slot showing nothing. The slot's extent is its fraction of the output,
    // which must land on whole pixels both at full size and at half, so the resized waits name exact extents.
    private static bool TryLayouts(JsonObject layout, string instance, QualificationResolution resolution, out string full, out string half, out string unloaded, out (int Width, int Height) fullExtent, out (int Width, int Height) halfExtent, out string reason) {
        full = layout.ToJsonString();
        half = string.Empty;
        unloaded = string.Empty;
        fullExtent = default;
        halfExtent = default;

        var slots = (layout["slots"] as JsonArray);
        var index = -1;

        for (var candidate = 0; (candidate < (slots?.Count ?? 0)); candidate++) {
            if (((slots![candidate]?["pipeline"] as JsonValue)?.TryGetValue<string>(value: out var shown) == true) && string.Equals(
                a: shown,
                b: instance,
                comparisonType: StringComparison.Ordinal
            )) {
                index = candidate;

                break;
            }
        }

        if (index < 0) {
            reason = $"no slot of the layout shows the pipeline '{instance}'";

            return false;
        }

        var slot = slots![index]!.AsObject();

        if (
            !TryFraction(node: slot["width"], value: out var width) ||
            !TryFraction(node: slot["height"], value: out var height)
        ) {
            reason = $"the slot showing '{instance}' has no numeric width and height";

            return false;
        }

        var fullWidth = (width * resolution.Width);
        var fullHeight = (height * resolution.Height);

        if (
            !IsWhole(value: fullWidth) ||
            !IsWhole(value: fullHeight) ||
            !IsWhole(value: (fullWidth / 2d)) ||
            !IsWhole(value: (fullHeight / 2d)) ||
            (fullWidth < 2d) ||
            (fullHeight < 2d)
        ) {
            reason = $"the slot showing '{instance}' spans {fullWidth}x{fullHeight} pixels at {resolution.Label}, which does not halve to whole pixels";

            return false;
        }

        fullExtent = (((int)fullWidth), ((int)fullHeight));
        halfExtent = ((fullExtent.Width / 2), (fullExtent.Height / 2));

        var halved = layout.DeepClone().AsObject();
        var halvedSlot = halved["slots"]![index]!.AsObject();

        halvedSlot["width"] = (width / 2d);
        halvedSlot["height"] = (height / 2d);
        half = halved.ToJsonString();

        var emptied = layout.DeepClone().AsObject();

        emptied["slots"]![index]!.AsObject()["pipeline"] = null;
        unloaded = emptied.ToJsonString();
        reason = string.Empty;

        return true;
    }
    private static bool TryFraction(JsonNode? node, out double value) {
        value = 0d;

        return ((node is JsonValue number) && number.TryGetValue<double>(value: out value) && double.IsFinite(d: value) && (value > 0d));
    }
    private static bool IsWhole(double value) => (value == Math.Floor(d: value));
    private static string Number(int value) => value.ToString(provider: CultureInfo.InvariantCulture);
}
