using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Puck.Cli.Motion;

namespace Puck.Cli.Sweep;

/// <summary>Runs a plan's tools, signals and tour on one World and collects what each showed. Silence is never
/// success: a read the World did not answer, a metric a rule reads that the answer lacks, and a tool or stop the sweep
/// could not reach are each reported as unmeasured or red, never clean.</summary>
/// <param name="plan">The plan.</param>
/// <param name="world">The World.</param>
/// <param name="directory">The report's directory; answers, stills and sheets are written beneath it.</param>
/// <param name="baseline">The previous report, or <see langword="null"/>.</param>
/// <param name="progress">Where one-line progress goes.</param>
public sealed class SweepRun(SweepPlan plan, SweepWorld world, string directory, SweepBaseline? baseline, TextWriter progress) {
    // A wait over the attachment is bounded by its 120-second call ceiling, so longer waits repeat.
    private const int WaitSeconds = 110;
    private const int MaximumTextMetric = 2048;
    // The ticks between the tick a stop's lines and bakes settled at and the start of its pinned timeline: the room a
    // call needs to arm the timeline and the capture before the World reaches them.
    private const int LeadTicks = 30;
    // The most ticks one tour wait call holds: a second of the default rate.
    private const ulong WaitChunkTicks = 30UL;
    // The suffix of a failure signal read again after the tour.
    private const string RecheckSuffix = ".after-tour";

    /// <summary>Gets what the leg noticed outside any signal.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Waits until a windowed World's renderer is ready and its bakes have settled.</summary>
    /// <param name="deadline">The longest wait.</param>
    /// <returns><see langword="null"/> when the World settled; otherwise a red signal named <c>settle</c> that says
    /// why it did not: the attachment closed, the sweep's time passed, the World refused the wait, or it was not
    /// ready within the deadline.</returns>
    public SweepSignalResult? Settle(TimeSpan deadline) {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        string? failure = null;

        foreach (var form in new[] { "ready", "bakes" }) {
            while (failure is null) {
                var answer = world.Run(script: $"world.wait {form} {WaitSeconds.ToString(provider: CultureInfo.InvariantCulture)}", timeoutSeconds: (WaitSeconds + 5));

                if (answer.Ok) {
                    break;
                }

                var unsettled = (answer.Output.Contains(comparisonType: StringComparison.Ordinal, value: "not settled") || answer.Output.Contains(comparisonType: StringComparison.Ordinal, value: "not ready"));

                if (!unsettled || world.Gone || world.Budget.Expired) {
                    failure = $"world.wait {form}: {answer.Output}";
                } else if (clock.Elapsed > deadline) {
                    failure = $"world.wait {form}: still waiting after {deadline.TotalMinutes:0.##} minutes: {answer.Output}";
                }
            }
        }

        progress.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"sweep: {world.Leg} World {world.ProcessId} {((failure is null) ? "settled" : "did not settle")} in {clock.Elapsed.TotalSeconds:0} s"));

        return ((failure is null)
            ? null
            : new SweepSignalResult(Changes: [], File: string.Empty, Leg: world.Leg, Line: "world.wait ready, world.wait bakes", Metrics: new Dictionary<string, string>(), Name: "settle", Reasons: [failure], Status: SweepStatus.Red));
    }
    /// <summary>Runs every tool for its bounded span. The World is paused first and stepped only inside the calls
    /// that wait for ticks, and stays paused after, so every tool starts and ends on ticks the plan fixes and every
    /// signal that follows reads one tick.</summary>
    /// <returns>Each tool's result.</returns>
    public IReadOnlyList<SweepToolResult> Tools() {
        var results = new List<SweepToolResult>();
        var held = world.Run(script: "world.rate pause");

        if (!held.Ok) {
            Notes.Add(item: $"the World was not paused before the tools: {string.Join(separator: "; ", values: held.Refusals)}");
            if (Stopped() is null) {
                results.Add(item: new SweepToolResult(Name: "pause", Status: SweepStatus.Red, Ticks: 0, Reasons: held.Refusals));
            }
        }

        foreach (var tool in plan.Tools) {
            var reasons = new List<string>();
            var status = SweepStatus.Clean;
            var ticks = 0;

            if (Stopped() is { } stopped) {
                results.Add(item: new SweepToolResult(Name: tool.Name, Reasons: [$"not run: {stopped}"], Status: SweepStatus.Unknown, Ticks: 0));

                continue;
            }

            // The World is held paused between calls and runs only inside them, so the same plan takes the same ticks
            // however long the coordinator takes between calls.
            var begun = world.Run(script: string.Join(separator: '\n', values: ["world.rate resume", .. (tool.Lines ?? []), "world.wait 2", "world.rate pause"]), timeoutSeconds: 120);

            if (!begun.Ok) {
                reasons.AddRange(collection: begun.Refusals);
                status = SweepStatus.Red;
            }

            if (tool.Until is { } until) {
                while (true) {
                    var read = world.Run(script: until.Line);
                    var metrics = SweepSignals.Read(extra: null, output: read.Output, parse: (until.Line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.state ") ? "cells" : "words"));

                    if (!read.Ok) {
                        reasons.AddRange(collection: read.Refusals);
                        status = SweepStatus.Red;

                        break;
                    }
                    if (metrics.TryGetValue(key: until.Metric, value: out var value) && until.In.Contains(value: value)) {
                        if (until.Red?.Contains(value: value) == true) {
                            reasons.Add(item: $"ended with {until.Metric}={value}: {read.Output.Trim()}");
                            status = SweepStatus.Red;
                        }

                        break;
                    }
                    if (ticks >= tool.MaxTicks) {
                        reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"not done after {ticks} ticks: {until.Metric}={(metrics.TryGetValue(key: until.Metric, value: out var last) ? last : "absent")}"));
                        status = SweepStatus.Worse(left: status, right: SweepStatus.Unknown);

                        break;
                    }

                    var waited = world.Run(script: $"world.rate resume\nworld.wait {tool.EveryTicks.ToString(provider: CultureInfo.InvariantCulture)}\nworld.rate pause", timeoutSeconds: 120);

                    if (!waited.Ok) {
                        reasons.AddRange(collection: waited.Refusals);
                        status = SweepStatus.Red;

                        break;
                    }

                    ticks += tool.EveryTicks;
                }
            }

            progress.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"sweep: tool {tool.Name} {status} after {ticks} ticks"));
            results.Add(item: new SweepToolResult(Name: tool.Name, Reasons: reasons, Status: status, Ticks: ticks));
        }

        return results;
    }
    /// <summary>Reads every signal the World's leg reads. A signal the sweep could not read is reported unmeasured.</summary>
    /// <returns>Each signal's reading.</returns>
    public IReadOnlyList<SweepSignalResult> Signals() => Read(signals: plan.Signals.Where(predicate: signal => signal.ReadsOn(leg: world.Leg)), suffix: string.Empty);
    /// <summary>Reads the failure signals again, as <c>&lt;name&gt;.after-tour</c>, once a workload or the tour has run.</summary>
    /// <returns>Each failure signal's second reading.</returns>
    public IReadOnlyList<SweepSignalResult> Recheck() => Read(signals: plan.Signals.Where(predicate: signal => (signal.Recheck && signal.ReadsOn(leg: world.Leg))), suffix: RecheckSuffix);
    /// <summary>Runs the capture tour: every stop's lines, its bakes, its pinned timeline and a settled capture at its
    /// named tick, then each group's contact sheet and, with a baseline, each group's comparison sheet. A stop the
    /// sweep could not reach is reported unmeasured.</summary>
    /// <param name="names">The stops to run; every stop when empty.</param>
    /// <returns>The tour's stills and sheets.</returns>
    public SweepTourResult Tour(IReadOnlyCollection<string> names) {
        var tour = plan.Tour;
        var stills = new List<SweepStillResult>();
        var images = new Dictionary<string, MotionImage>(comparer: StringComparer.Ordinal);
        var previous = new Dictionary<string, MotionImage>(comparer: StringComparer.Ordinal);
        var folder = Path.Combine(path1: directory, path2: "tour");
        var prelude = new List<string>();

        _ = Directory.CreateDirectory(path: folder);

        foreach (var line in new[] { "world.rate pause", "world.lockstep on" }.Concat(second: (tour.Prelude ?? []))) {
            var answer = TourLine(line: line);

            if (!answer.Ok) {
                prelude.Add(item: $"tour prelude refused: {string.Join(separator: "; ", values: answer.Refusals)}");
            }
        }

        foreach (var stop in tour.Stops.Where(predicate: stop => ((names.Count == 0) || names.Contains(value: stop.Name)))) {
            var reasons = new List<string>(collection: prelude);
            var status = ((prelude.Count == 0) ? SweepStatus.Clean : SweepStatus.Red);

            if (Stopped() is { } stopped) {
                stills.Add(item: new SweepStillResult(Capture: string.Empty, ChangedFraction: null, File: null, Group: (stop.Group ?? "tour"), Name: stop.Name, Note: stop.Note, Reasons: [$"not taken: {stopped}"], Status: SweepStatus.Unknown));

                continue;
            }

            foreach (var line in (tour.Reset ?? []).Concat(second: (stop.Lines ?? []))) {
                var answer = TourLine(line: line);

                if (!answer.Ok) {
                    reasons.AddRange(collection: answer.Refusals);
                    status = SweepStatus.Red;
                }
            }

            // Authority stays held while bakes settle. Only the pinned timeline advances the stop's clock.
            var bakes = world.Run(script: "world.wait bakes 60", timeoutSeconds: 70);

            if (!bakes.Ok) {
                reasons.Add(item: $"captured with bakes unsettled: {bakes.Output}");
                status = SweepStatus.Worse(left: status, right: SweepStatus.Red);
            }

            var pinned = Pin(captureTick: out var captureTick, expected: out var expected, reasons: reasons, stop: stop, tour: tour);

            if (!pinned) {
                status = SweepStatus.Red;
            }

            var capture = string.Empty;
            var png = (pinned ? world.Capture(output: out capture, tick: captureTick) : null);
            string? motion = null;
            string? relative = null;
            string? exposure = null;
            double? changed = null;

            if (pinned && (png is null)) {
                reasons.Add(item: $"capture refused: {capture}");
                status = SweepStatus.Red;
            } else if (png is not null) {
                var file = Path.Combine(path1: folder, path2: $"{stop.Name}.png");
                var before = ((baseline?.StillFor(name: stop.Name) is { } earlier) ? MotionSheets.Read(path: earlier) : null);

                // The baseline still is read before this run writes its own, so a run never compares an image with itself.
                File.WriteAllBytes(bytes: png, path: file);
                relative = Path.GetRelativePath(path: file, relativeTo: directory).Replace(newChar: '/', oldChar: '\\');
                images[stop.Name] = MotionSheets.Read(path: file);

                if (before is not null) {
                    previous[stop.Name] = before;
                    changed = ChangedFraction(after: images[stop.Name], before: before, level: tour.LevelTolerance);

                    if (changed is null) {
                        reasons.Add(item: "the baseline still has another size");
                        status = SweepStatus.Worse(left: status, right: SweepStatus.Amber);
                    } else if (changed > tour.AmberFraction) {
                        reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"{changed:P2} of its pixels changed beyond {tour.LevelTolerance} levels"));
                        status = SweepStatus.Worse(left: status, right: SweepStatus.Amber);
                    }
                } else if (baseline is not null) {
                    reasons.Add(item: "new still");
                }
            }

            if (pinned) {
                var evidence = SweepEvidence.Scheduled(world.Run("world.at --json"), expected);

                if (!evidence.Ok) {
                    reasons.AddRange(collection: evidence.Refusals);
                    status = SweepStatus.Red;
                }
                var pose = world.Run(script: "body.where 0");
                var rig = world.Run(script: "body.rig 0");
                if (!pose.Ok || !rig.Ok) {
                    reasons.AddRange(collection: pose.Refusals.Concat(second: rig.Refusals));
                    status = SweepStatus.Red;
                } else {
                    motion = $"{pose.Output.Trim()}\n{rig.Output.Trim()}";
                    if ((stop.ExpectedMotion is { } pattern) && !Regex.IsMatch(input: motion, pattern: pattern, options: RegexOptions.CultureInvariant, matchTimeout: TimeSpan.FromSeconds(value: 2))) {
                        reasons.Add(item: $"motion did not match '{pattern}': {pose.Output.Trim()}");
                        status = SweepStatus.Red;
                    }
                }

                // The held tick's exposure: a still that differs from its baseline's in every pixel is usually this, which a
                // meter reading that depends on the World's history moves. A World without a renderer has none to read.
                var metered = world.Run(script: "world.exposure");

                exposure = (metered.Ok ? SweepEvidence.ExposureReading(output: metered.Output) : null);
                if (changed > tour.AmberFraction) {
                    var note = SweepEvidence.ExposureNote(before: baseline?.ExposureFor(name: stop.Name), after: exposure);

                    if (note is not null) { reasons.Add(item: note); }
                }
            }

            foreach (var line in (stop.After ?? [])) {
                var answer = TourLine(line: line);

                if (!answer.Ok) {
                    reasons.Add(item: $"after the capture: {string.Join(separator: "; ", values: answer.Refusals)}");
                    status = SweepStatus.Worse(left: status, right: SweepStatus.Red);
                }
            }

            progress.WriteLine(value: $"sweep: still {stop.Name} {status}");
            stills.Add(item: new SweepStillResult(Capture: capture.Trim(), ChangedFraction: changed, File: relative, Group: (stop.Group ?? "tour"), Name: stop.Name, Note: stop.Note, Reasons: reasons, Status: status, Motion: motion, Exposure: exposure));
        }

        return new SweepTourResult(
            AmberFraction: tour.AmberFraction,
            LevelTolerance: tour.LevelTolerance,
            Sheets: Sheets(folder: folder, images: images, previous: previous, stills: stills, tour: tour),
            Stills: stills
        );
    }

    // Arms the stop's pinned steps from a start tick a lead after the World's completed tick, and names the tick the
    // capture is pinned to. False, with the reason added, when the World's tick could not be read or the steps were refused.
    private bool Pin(SweepStop stop, SweepTour tour, List<string> reasons, out ulong captureTick, out IReadOnlyList<(ulong Tick, string Command)> expected) {
        captureTick = 0UL;
        expected = [];

        var read = world.Run(script: "world.at --json");
        ulong completed;

        try {
            using var document = JsonDocument.Parse(json: read.Output);

            if (!read.Ok || !document.RootElement.TryGetProperty(propertyName: "tick", value: out var tick) || !tick.TryGetUInt64(value: out completed)) {
                reasons.Add(item: $"the stop's tick could not be read: {read.Output}");

                return false;
            }
        } catch (JsonException) {
            reasons.Add(item: $"the stop's tick could not be read: {read.Output}");

            return false;
        }

        var start = (completed + LeadTicks);

        var steps = (tour.Frame ?? []).Select(selector: line => new SweepStep(At: 0, Run: line)).Concat(second: (stop.Steps ?? [])).ToArray();

        steps = [.. steps, new SweepStep(At: (stop.Settle ?? tour.Settle), Run: "world.rate pause")];
        expected = steps.Select(selector: step => ((start + ((ulong)step.At)), step.Run)).ToArray();
        {
            var script = JsonSerializer.Serialize(value: new { start, steps = steps.Select(selector: static step => new { at = step.At, run = step.Run }) });
            var armed = world.Run(script: $"world.at {script}\nworld.rate resume");

            if (!armed.Ok) {
                reasons.AddRange(collection: armed.Refusals);

                return false;
            }
        }

        captureTick = (start + ((ulong)(stop.Settle ?? tour.Settle)));

        return true;
    }
    // Why the World can no longer be driven, or null while it can.
    private string? Stopped() => (world.Gone ? "the World's attachment closed" : world.Budget.Reason);
    // Tick waits advance a bounded interval; readiness waits and coordinator round trips leave authority held.
    private SweepAnswer TourLine(string line) {
        if (!TryTickWait(line: line, ticks: out var ticks) || (ticks <= WaitChunkTicks)) {
            return world.Run(script: TickWaits(script: line), timeoutSeconds: 120);
        }

        // A tick wait is refused when the World's rate cannot complete it before its call's answer is due, and a loaded
        // machine's rate falls with the load, so a long wait is several calls of a few ticks each, each with a deadline of its own.
        var answer = new SweepAnswer(Ok: true, Output: string.Empty, Refusals: []);

        for (var remaining = ticks; (remaining > 0UL) && answer.Ok; remaining -= Math.Min(val1: remaining, val2: WaitChunkTicks)) {
            answer = world.Run(script: $"world.rate resume\nworld.wait {Math.Min(val1: remaining, val2: WaitChunkTicks).ToString(provider: CultureInfo.InvariantCulture)}\nworld.rate pause", timeoutSeconds: 120);
        }

        return answer;
    }
    private static bool TryTickWait(string line, out ulong ticks) {
        ticks = 0UL;

        return (line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.wait ") && ulong.TryParse(provider: CultureInfo.InvariantCulture, result: out ticks, s: line[11..], style: NumberStyles.None));
    }
    private static string TickWaits(string script) => string.Join(separator: '\n', values: script.Split('\n').Select(selector: static line =>
        ((line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.wait ") && (ulong.TryParse(line[11..], out _) || line.StartsWith(comparisonType: StringComparison.Ordinal, value: "world.wait until ")))
            ? $"world.rate resume\n{line}\nworld.rate pause" : line)));
    // Whether a signal's answer says its read is unfinished: its pending rule holds over the metrics.
    private static bool Pending(SweepSignal signal, IReadOnlyDictionary<string, string> metrics) =>
        ((signal.Pending is { } text) && SweepRule.TryParse(rule: out var rule, text: text) && (rule.Holds(metrics: metrics) == true));
    private List<SweepSignalResult> Read(IEnumerable<SweepSignal> signals, string suffix) {
        var results = new List<SweepSignalResult>();
        var answers = Path.Combine(path1: directory, path2: world.Leg);

        _ = Directory.CreateDirectory(path: answers);

        foreach (var signal in signals) {
            var name = (signal.Name + suffix);

            if (Stopped() is { } stopped) {
                progress.WriteLine(value: $"sweep: {world.Leg} {name} {SweepStatus.Unknown}");
                results.Add(item: new SweepSignalResult(Changes: [], File: string.Empty, Leg: world.Leg, Line: signal.Line, Metrics: new Dictionary<string, string>(), Name: name, Reasons: [$"not read: {stopped}"], Status: SweepStatus.Unknown));

                continue;
            }

            var script = ((world.Leg == SweepLegs.Windowed) ? TickWaits(script: signal.Line) : signal.Line);
            var answer = world.Run(script: script, timeoutSeconds: signal.TimeoutSeconds);
            var (status, reasons, metrics) = SweepEvidence.Check(answer: answer, signal: signal);

            // A line whose work outlasts one call answers "unfinished" and resumes on the next ask, each ask under a
            // deadline of its own; the read is whole only once the pending rule stops holding.
            for (var asks = 1; (Pending(signal: signal, metrics: metrics) && (asks < signal.MaxAsks) && (Stopped() is null)); asks++) {
                answer = world.Run(script: script, timeoutSeconds: signal.TimeoutSeconds);
                (status, reasons, metrics) = SweepEvidence.Check(answer: answer, signal: signal);
            }
            if (Pending(signal: signal, metrics: metrics)) {
                reasons.Add(item: $"unmeasured: still pending after {signal.MaxAsks} ask(s): {signal.Pending}");
                status = SweepStatus.Worse(left: status, right: SweepStatus.Unknown);
            }

            var file = Path.Combine(path1: answers, path2: $"{name}.txt");

            File.WriteAllText(contents: answer.Output, path: file);

            // The answer's file keeps the whole text; the report keeps it only as a short text signal's value.
            if ((signal.Parse != "text") || ((metrics.GetValueOrDefault(key: "text")?.Length ?? 0) > MaximumTextMetric)) {
                _ = metrics.Remove(key: "text");
            }

            var changes = (baseline?.Changes(leg: world.Leg, metrics: metrics, signal: (signal with { Name = name }), status: status) ?? []);

            if (changes.Count > 0) {
                reasons.AddRange(collection: changes.Select(selector: static change => $"baseline: {change}"));
                status = SweepStatus.Worse(left: status, right: SweepStatus.Red);
            }

            progress.WriteLine(value: $"sweep: {world.Leg} {name} {status}");
            results.Add(item: new SweepSignalResult(
                Changes: changes,
                File: Path.GetRelativePath(path: file, relativeTo: directory).Replace(newChar: '/', oldChar: '\\'),
                Leg: world.Leg,
                Line: signal.Line,
                Metrics: metrics,
                Name: name,
                Reasons: reasons,
                Status: status
            ));
        }

        return results;
    }
    // One sheet per group, and with a baseline one comparison sheet per group: the baseline's still above the current.
    // The baseline's images are the ones read before this run wrote its own.
    private Dictionary<string, string> Sheets(string folder, IReadOnlyDictionary<string, MotionImage> images, IReadOnlyDictionary<string, MotionImage> previous, IReadOnlyList<SweepStillResult> stills, SweepTour tour) {
        var sheets = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var labelHeight = MotionSheets.LabelHeightFor();

        foreach (var group in stills.Where(predicate: still => images.ContainsKey(key: still.Name)).GroupBy(keySelector: static still => still.Group)) {
            var members = group.ToArray();
            var scaled = members.Select(selector: still => MotionSheets.ScaleToWidth(source: images[still.Name], width: tour.CellWidth)).ToArray();
            var cellHeight = scaled.Max(selector: static image => image.Height);
            var earlier = members.Select(selector: still => (previous.TryGetValue(key: still.Name, value: out var image) ? MotionSheets.ScaleToWidth(source: image, width: tour.CellWidth) : null)).ToArray();
            var compare = earlier.Any(predicate: static image => (image is not null));
            var cameras = (compare ? 2 : 1);
            var layout = MotionSheetLayout.Plan(cameras: cameras, cellHeight: cellHeight, cellWidth: tour.CellWidth, columns: Math.Max(val1: 1, val2: tour.Columns), frames: members.Length, labelHeight: labelHeight);
            var frames = new List<IReadOnlyList<MotionImage>>();
            var labels = new List<IReadOnlyList<string>>();
            var characters = MotionSheets.CharactersThatFit(scale: MotionSheets.LabelScale, width: tour.CellWidth);
            var detail = MotionSheets.CharactersThatFit(scale: 1, width: tour.CellWidth);

            for (var index = 0; (index < members.Length); index++) {
                var title = Fit(text: members[index].Name, width: characters);
                var current = $"{title}\n{Fit(text: $"{members[index].Status} {members[index].Capture}", width: detail)}";

                if (compare) {
                    frames.Add(item: [(earlier[index] ?? Blank(height: cellHeight, width: tour.CellWidth)), scaled[index]]);
                    labels.Add(item: [$"{title}\n{((earlier[index] is null) ? "no baseline still" : "baseline")}", current]);
                } else {
                    frames.Add(item: [scaled[index]]);
                    labels.Add(item: [current]);
                }
            }

            var name = (compare ? $"compare-{group.Key}" : $"sheet-{group.Key}");
            var file = Path.Combine(path1: folder, path2: $"{name}.png");

            MotionSheets.Write(image: MotionSheets.Compose(frames: frames, labels: labels, layout: layout), path: file);
            sheets[group.Key] = Path.GetRelativePath(path: file, relativeTo: directory).Replace(newChar: '/', oldChar: '\\');
        }

        return new Dictionary<string, string>(dictionary: sheets, comparer: StringComparer.Ordinal);
    }
    private static string Fit(string text, int width) => ((text.Length <= width) ? text : text[..Math.Max(val1: 0, val2: width)]);
    private static MotionImage Blank(int width, int height) => new(Height: height, Rgba: new byte[((width * height) * 4)], Width: width);
    private static double? ChangedFraction(MotionImage before, MotionImage after, int level) {
        if ((before.Width != after.Width) || (before.Height != after.Height)) {
            return null;
        }

        var changed = 0L;
        var pixels = (before.Width * before.Height);

        for (var pixel = 0; (pixel < pixels); pixel++) {
            for (var channel = 0; (channel < 3); channel++) {
                if (Math.Abs(value: (before.Rgba[((pixel * 4) + channel)] - after.Rgba[((pixel * 4) + channel)])) > level) {
                    changed++;

                    break;
                }
            }
        }

        return (changed / ((double)pixels));
    }
}
