using System.Globalization;

using Puck.Cli.Motion;

namespace Puck.Cli.Sweep;

/// <summary>Runs a plan's tools, signals and tour on one World and collects what each showed.</summary>
/// <param name="plan">The plan.</param>
/// <param name="world">The World.</param>
/// <param name="directory">The report's directory; answers, stills and sheets are written beneath it.</param>
/// <param name="baseline">The previous report, or <see langword="null"/>.</param>
/// <param name="progress">Where one-line progress goes.</param>
public sealed class SweepRun(SweepPlan plan, SweepWorld world, string directory, SweepBaseline? baseline, TextWriter progress) {
    // A wait over the attachment is bounded by its 120-second call ceiling, so longer waits repeat.
    private const int WaitSeconds = 110;
    private const int MaximumTextMetric = 2048;

    /// <summary>Gets what the leg noticed outside any signal.</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Waits until a windowed World's renderer is ready and its bakes have settled.</summary>
    /// <param name="deadline">The longest wait.</param>
    public void Settle(TimeSpan deadline) {
        var clock = System.Diagnostics.Stopwatch.StartNew();

        foreach (var form in new[] { "ready", "bakes" }) {
            while (true) {
                var answer = world.Run(script: $"world.wait {form} {WaitSeconds.ToString(provider: CultureInfo.InvariantCulture)}", timeoutSeconds: (WaitSeconds + 5));

                if (answer.Ok && !answer.Output.Contains(comparisonType: StringComparison.Ordinal, value: "not settled") && !answer.Output.Contains(comparisonType: StringComparison.Ordinal, value: "not ready")) {
                    break;
                }
                if (clock.Elapsed > deadline) {
                    Notes.Add(item: $"world.wait {form}: still waiting after {deadline.TotalMinutes:0} minutes: {answer.Output}");

                    break;
                }
            }
        }

        progress.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"sweep: {world.Leg} World {world.ProcessId} settled in {clock.Elapsed.TotalSeconds:0} s"));
    }
    /// <summary>Runs every tool for its bounded span.</summary>
    /// <returns>Each tool's result.</returns>
    public IReadOnlyList<SweepToolResult> Tools() {
        var results = new List<SweepToolResult>();

        foreach (var tool in plan.Tools) {
            var reasons = new List<string>();
            var status = SweepStatus.Clean;
            var ticks = 0;

            foreach (var line in (tool.Lines ?? [])) {
                var answer = world.Run(script: line);

                if (!answer.Ok) {
                    reasons.AddRange(collection: answer.Refusals);
                    status = SweepStatus.Red;
                }
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
                        status = SweepStatus.Worse(left: status, right: SweepStatus.Amber);

                        break;
                    }

                    _ = world.Run(script: $"world.wait {tool.EveryTicks.ToString(provider: CultureInfo.InvariantCulture)}", timeoutSeconds: 120);
                    ticks += tool.EveryTicks;
                }
            }

            progress.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"sweep: tool {tool.Name} {status} after {ticks} ticks"));
            results.Add(item: new SweepToolResult(Name: tool.Name, Reasons: reasons, Status: status, Ticks: ticks));
        }

        return results;
    }
    /// <summary>Reads every signal the World's leg reads.</summary>
    /// <returns>Each signal's reading.</returns>
    public IReadOnlyList<SweepSignalResult> Signals() {
        var results = new List<SweepSignalResult>();
        var answers = Path.Combine(path1: directory, path2: world.Leg);

        _ = Directory.CreateDirectory(path: answers);

        foreach (var signal in plan.Signals.Where(predicate: signal => signal.ReadsOn(leg: world.Leg))) {
            if (world.Gone) {
                Notes.Add(item: $"the World's attachment closed before the signal {signal.Name}; the signals after it were not read");

                break;
            }

            var answer = world.Run(script: signal.Line, timeoutSeconds: signal.TimeoutSeconds);
            var file = Path.Combine(path1: answers, path2: $"{signal.Name}.txt");

            File.WriteAllText(contents: answer.Output, path: file);

            var metrics = SweepSignals.Read(extra: signal.Metrics, output: answer.Output, parse: signal.Parse);
            var reasons = new List<string>();
            var status = (answer.Ok ? SweepStatus.Clean : SweepStatus.Red);

            reasons.AddRange(collection: answer.Refusals);

            if (metrics.TryGetValue(key: "error", value: out var error)) {
                reasons.Add(item: error);
                status = SweepStatus.Red;
            }

            status = Judge(metrics: metrics, reasons: reasons, rules: signal.AmberRules, status: status, when: SweepStatus.Amber);
            status = Judge(metrics: metrics, reasons: reasons, rules: signal.RedRules, status: status, when: SweepStatus.Red);

            // The answer's file keeps the whole text; the report keeps it only as a short text signal's value.
            if ((signal.Parse != "text") || (metrics["text"].Length > MaximumTextMetric)) {
                _ = metrics.Remove(key: "text");
            }

            var changes = (baseline?.Changes(leg: world.Leg, metrics: metrics, signal: signal, status: status) ?? []);

            progress.WriteLine(value: $"sweep: {world.Leg} {signal.Name} {status}");
            results.Add(item: new SweepSignalResult(
                Changes: changes,
                File: Path.GetRelativePath(path: file, relativeTo: directory).Replace(newChar: '/', oldChar: '\\'),
                Leg: world.Leg,
                Line: signal.Line,
                Metrics: metrics,
                Name: signal.Name,
                Reasons: reasons,
                Status: status
            ));
        }

        return results;
    }
    /// <summary>Runs the capture tour: every stop's lines, its settle wait and a settled capture, then each group's
    /// contact sheet and, with a baseline, each group's comparison sheet.</summary>
    /// <param name="names">The stops to run; every stop when empty.</param>
    /// <returns>The tour's stills and sheets.</returns>
    public SweepTourResult Tour(IReadOnlyCollection<string> names) {
        var tour = plan.Tour;
        var stills = new List<SweepStillResult>();
        var images = new Dictionary<string, MotionImage>(comparer: StringComparer.Ordinal);
        var folder = Path.Combine(path1: directory, path2: "tour");

        _ = Directory.CreateDirectory(path: folder);

        foreach (var line in (tour.Prelude ?? [])) {
            var answer = world.Run(script: line);

            if (!answer.Ok) {
                Notes.Add(item: $"tour prelude: {string.Join(separator: "; ", values: answer.Refusals)}");
            }
        }

        foreach (var stop in tour.Stops.Where(predicate: stop => ((names.Count == 0) || names.Contains(value: stop.Name)))) {
            if (world.Gone) {
                Notes.Add(item: $"the World's attachment closed before the stop {stop.Name}; the stops after it were not taken");

                break;
            }

            var reasons = new List<string>();
            var status = SweepStatus.Clean;

            foreach (var line in (tour.Reset ?? []).Concat(second: (stop.Lines ?? [])).Concat(second: (tour.Frame ?? []))) {
                var answer = world.Run(script: line, timeoutSeconds: 120);

                if (!answer.Ok) {
                    reasons.AddRange(collection: answer.Refusals);
                    status = SweepStatus.Red;
                }
            }

            _ = world.Run(script: $"world.wait {(stop.Settle ?? tour.Settle).ToString(provider: CultureInfo.InvariantCulture)}", timeoutSeconds: 120);

            var bakes = world.Run(script: "world.wait bakes 60", timeoutSeconds: 70);

            if (!bakes.Ok || bakes.Output.Contains(comparisonType: StringComparison.Ordinal, value: "not settled")) {
                reasons.Add(item: $"captured with bakes unsettled: {bakes.Output}");
                status = SweepStatus.Worse(left: status, right: SweepStatus.Amber);
            }

            var png = world.Capture(output: out var capture);
            string? relative = null;
            double? changed = null;

            if (png is null) {
                reasons.Add(item: $"capture refused: {capture}");
                status = SweepStatus.Red;
            } else {
                var file = Path.Combine(path1: folder, path2: $"{stop.Name}.png");

                File.WriteAllBytes(bytes: png, path: file);
                relative = Path.GetRelativePath(path: file, relativeTo: directory).Replace(newChar: '/', oldChar: '\\');
                images[stop.Name] = MotionSheets.Read(path: file);

                if (baseline?.StillFor(name: stop.Name) is { } previous) {
                    changed = ChangedFraction(after: images[stop.Name], before: MotionSheets.Read(path: previous), level: tour.LevelTolerance);

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

            foreach (var line in (stop.After ?? [])) {
                _ = world.Run(script: line, timeoutSeconds: 120);
            }

            progress.WriteLine(value: $"sweep: still {stop.Name} {status}");
            stills.Add(item: new SweepStillResult(Capture: capture.Trim(), ChangedFraction: changed, File: relative, Group: (stop.Group ?? "tour"), Name: stop.Name, Note: stop.Note, Reasons: reasons, Status: status));
        }

        return new SweepTourResult(
            AmberFraction: tour.AmberFraction,
            LevelTolerance: tour.LevelTolerance,
            Sheets: Sheets(folder: folder, images: images, stills: stills, tour: tour),
            Stills: stills
        );
    }

    // One sheet per group, and with a baseline one comparison sheet per group: the baseline's still above the current.
    private Dictionary<string, string> Sheets(string folder, IReadOnlyDictionary<string, MotionImage> images, IReadOnlyList<SweepStillResult> stills, SweepTour tour) {
        var sheets = new SortedDictionary<string, string>(comparer: StringComparer.Ordinal);
        var labelHeight = MotionSheets.LabelHeightFor();

        foreach (var group in stills.Where(predicate: still => images.ContainsKey(key: still.Name)).GroupBy(keySelector: static still => still.Group)) {
            var members = group.ToArray();
            var scaled = members.Select(selector: still => MotionSheets.ScaleToWidth(source: images[still.Name], width: tour.CellWidth)).ToArray();
            var cellHeight = scaled.Max(selector: static image => image.Height);
            var previous = members.Select(selector: still => ((baseline?.StillFor(name: still.Name) is { } path) ? MotionSheets.ScaleToWidth(source: MotionSheets.Read(path: path), width: tour.CellWidth) : null)).ToArray();
            var compare = previous.Any(predicate: static image => (image is not null));
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
                    frames.Add(item: [(previous[index] ?? Blank(height: cellHeight, width: tour.CellWidth)), scaled[index]]);
                    labels.Add(item: [$"{title}\n{((previous[index] is null) ? "no baseline still" : "baseline")}", current]);
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
    private static string Judge(IReadOnlyDictionary<string, string> metrics, List<string> reasons, IReadOnlyList<string> rules, string status, string when) {
        foreach (var text in rules) {
            if (SweepRule.TryParse(rule: out var rule, text: text) && (rule.Holds(metrics: metrics) == true)) {
                var value = (metrics.TryGetValue(key: rule.Metric, value: out var found) ? found : string.Empty);

                reasons.Add(item: $"{when}: {rule} ({rule.Metric}={((value.Length > 80) ? value[..80] : value)})");
                status = SweepStatus.Worse(left: status, right: when);
            }
        }

        return status;
    }
}
