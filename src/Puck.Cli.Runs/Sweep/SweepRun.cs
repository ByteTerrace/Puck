using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Puck.Abstractions;
using Puck.Cli.Motion;
using Puck.Commands;

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
    // The most ticks one wait through a stop's timeline of stills holds.
    private const ulong RouteWaitTicks = 150UL;

    /// <summary>Gets what the leg noticed outside any signal.</summary>
    public List<string> Notes { get; } = [];
    /// <summary>Gets or initializes what route stops need beyond the World, or <see langword="null"/> on a leg that plays
    /// none: a route stop on such a leg is red.</summary>
    public SweepRouteContext? Routes { get; init; }

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

            var played = Played(stop: stop, tour: tour, folder: folder, reasons: reasons, route: out var route, captures: out var captures, own: out var own);
            var captureTick = 0UL;
            IReadOnlyList<(ulong Tick, string Command)> expected = [];
            var pinned = ((played is not null) && Pin(captureTick: out captureTick, expected: out expected, reasons: reasons, stop: played, tour: tour));

            if (!pinned) {
                status = SweepStatus.Red;
            }

            var capture = string.Empty;
            byte[]? png = null;

            if (pinned && (own is not null)) {
                // A timeline of stills takes its own still as its last pinned line, and the tour waits the timeline out:
                // its held stills can take any time to converge, which one capture call's deadline could not cover.
                if (!WaitThrough(reasons: reasons, tick: captureTick) || (world.Run(script: "world.wait captures 60", timeoutSeconds: 70) is not { Ok: true })) {
                    status = SweepStatus.Red;
                }

                capture = (Ran(command: own.Value.Line, log: world.Run(script: "world.at --json").Output, tick: captureTick) ?? "the stop's own still has no answer");
                png = (File.Exists(path: own.Value.File) ? File.ReadAllBytes(path: own.Value.File) : null);
            } else if (pinned) {
                png = world.Capture(output: out capture, tick: captureTick);
            }
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

            IReadOnlyDictionary<string, string>? frames = null;

            if (pinned) {
                var log = world.Run("world.at --json");
                var evidence = SweepEvidence.Scheduled(log, expected);

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
                    // A route arrives where its test says, or the segment is off course: the world moved under the program.
                    foreach (var failure in (route?.Arrival(where: pose.Output, unjudged: out _) ?? [])) {
                        reasons.Add(item: $"off course: the arrival {failure}");
                        status = SweepStatus.Red;
                    }
                }
                if (route is not null) {
                    var read = world.Run(script: $"host.frames {(played!.Settle ?? tour.Settle).ToString(provider: CultureInfo.InvariantCulture)}");

                    frames = (read.Ok ? SweepSignals.Read(extra: null, output: read.Output, parse: "frames") : null);
                }

                stills.AddRange(collection: Captured(captures: captures, images: images, log: log.Output, previous: previous, start: (captureTick - (ulong)(played!.Settle ?? tour.Settle)), stop: stop, tour: tour));

                // The held tick's exposure: a still that differs from its baseline's in every pixel is usually this, which a
                // meter reading that depends on the World's history moves. A World without a renderer has none to read.
                var metered = world.Run(script: "world.exposure");

                exposure = (metered.Ok ? SweepEvidence.ExposureReading(output: metered.Output) : null);
                if (changed > tour.AmberFraction) {
                    var note = SweepEvidence.ExposureNote(before: baseline?.ExposureFor(name: stop.Name), after: exposure);

                    if (note is not null) { reasons.Add(item: note); }
                }
            }

            // A stop that ended before its timeline did leaves lines armed; none may run into the next stop.
            foreach (var line in (stop.After ?? []).Prepend(element: "world.at clear")) {
                var answer = TourLine(line: line);

                if (!answer.Ok) {
                    reasons.Add(item: $"after the capture: {string.Join(separator: "; ", values: answer.Refusals)}");
                    status = SweepStatus.Worse(left: status, right: SweepStatus.Red);
                }
            }

            progress.WriteLine(value: $"sweep: still {stop.Name} {status}");
            stills.Add(item: new SweepStillResult(Capture: capture.Trim(), ChangedFraction: changed, File: relative, Group: (stop.Group ?? "tour"), Name: stop.Name, Note: stop.Note, Reasons: reasons, Status: status, Motion: motion, Exposure: exposure, Frames: frames));
        }

        return new SweepTourResult(
            AmberFraction: tour.AmberFraction,
            LevelTolerance: tour.LevelTolerance,
            Sheets: Sheets(folder: folder, images: images, previous: previous, stills: stills, tour: tour),
            Stills: stills
        );
    }

    // The stop as the tour plays it: a route stop's steps are its test's program and its capture the test's export tick,
    // and each capture adds a pose and camera read and a still at its offset, written under the stop's folder. A stop with
    // a timeline of stills takes its own still as a pinned line too (`own`). Null, with the reason added, when the route
    // cannot be played or a capture falls past the stop's own.
    private SweepStop? Played(SweepStop stop, SweepTour tour, string folder, List<string> reasons, out SweepRoute? route, out IReadOnlyList<(SweepCapture Capture, string File, string Line)> captures, out (string File, string Line)? own) {
        route = null;
        captures = [];
        own = null;

        var played = stop;

        if (stop.Route is { } reference) {
            if (Routes is null) {
                reasons.Add(item: $"the route {reference} needs a leg that plays routes");

                return null;
            }
            if (!SweepRoute.TryLoad(error: out var why, reference: reference, repositoryRoot: Routes.RepositoryRoot, route: out route)) {
                reasons.Add(item: why);

                return null;
            }

            // The tour's frame lines run first at the start tick, then the program, whose first line poses the body.
            played = stop with { Settle = route!.Duration, Steps = [.. route.Steps, .. (stop.Steps ?? []), new SweepStep(At: 0, Run: "host.frames reset")] };
        }

        var settle = (played.Settle ?? tour.Settle);
        var list = new List<(SweepCapture Capture, string File, string Line)>();
        var extra = new List<SweepStep>();

        foreach (var capture in (stop.Captures ?? [])) {
            if (capture.At > settle) {
                reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"the capture of {capture.Still} at {capture.At} falls after the stop's capture at {settle}"));

                return null;
            }

            var target = Path.Combine(path1: folder, path2: stop.Name, path3: $"{capture.Still}.png");
            // A presented burst writes <stem>-000.png onward; its first frame stands for it on the sheet.
            var file = ((capture.Presented > 1) ? Path.Combine(path1: folder, path2: stop.Name, path3: $"{capture.Still}-000.png") : target);
            var line = $"world.screenshot {CommandEcho.Quote(PuckPaths.Normalize(path: target))}{((capture.Presented > 0) ? $" presented {capture.Presented.ToString(provider: CultureInfo.InvariantCulture)}" : string.Empty)}";

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: file)!);
            File.Delete(path: file);
            list.Add(item: (capture, file, line));
            extra.Add(item: new SweepStep(At: capture.At, Run: "body.where 0"));
            // Which eye took the still: a still of a first-person route taken from the chase camera is no route still.
            extra.Add(item: new SweepStep(At: capture.At, Run: "world.view.camera 1 view,yaw,pitch"));
            extra.Add(item: new SweepStep(At: capture.At, Run: line));
        }

        captures = list;
        own = null;

        if ((list.Count == 0) && (route is null)) {
            return played with { Steps = [.. (played.Steps ?? []), .. extra] };
        }

        var still = Path.Combine(path1: folder, path2: $"{stop.Name}.png");
        var take = $"world.screenshot {CommandEcho.Quote(PuckPaths.Normalize(path: still))}";

        File.Delete(path: still);
        own = (still, take);

        return played with { Steps = [.. (played.Steps ?? []), .. extra, new SweepStep(At: settle, Run: take)] };
    }
    // Holds the tour until the World has completed a tick of a stop's timeline, a bounded wait at a time. A wait whose
    // call ends first is no stall while the World still advances: held stills converge at the machine's pace. False,
    // with the reason added, when a whole wait passed without a tick or the World's tick could not be read.
    private bool WaitThrough(ulong tick, List<string> reasons) {
        if (Completed(reasons: reasons) is not { } completed) {
            return false;
        }

        while (completed < tick) {
            var next = Math.Min(val1: tick, val2: (completed + RouteWaitTicks));
            var waited = world.Run(script: $"world.wait until {next.ToString(provider: CultureInfo.InvariantCulture)}", timeoutSeconds: 115);

            if (waited.Ok) {
                completed = next;

                continue;
            }
            if ((Stopped() is { } stopped) || (Completed(reasons: reasons) is not { } now) || (now <= completed)) {
                reasons.Add(item: $"the stop's timeline stalled before tick {next.ToString(provider: CultureInfo.InvariantCulture)}: {string.Join(separator: "; ", values: waited.Refusals)}");

                return false;
            }

            completed = now;
        }

        return true;
    }
    // The tick the World has completed, or null with the reason added when it could not be read.
    private ulong? Completed(List<string> reasons) {
        var read = world.Run(script: "world.at --json");

        try {
            using var document = JsonDocument.Parse(json: read.Output);

            return document.RootElement.GetProperty(propertyName: "tick").GetUInt64();
        } catch (Exception exception) when ((exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)) {
            reasons.Add(item: $"the stop's tick could not be read: {read.Output}");

            return null;
        }
    }
    // A stop's captures as stills: each beside its exhibit's text, the body's pose at its tick, whether the body stood at
    // the exhibit's spawn point, and the mesh audit's topology defects for the exhibit's prototype.
    private List<SweepStillResult> Captured(SweepStop stop, ulong start, IReadOnlyList<(SweepCapture Capture, string File, string Line)> captures, string log, Dictionary<string, MotionImage> images, Dictionary<string, MotionImage> previous, SweepTour tour) {
        var results = new List<SweepStillResult>();

        if (captures.Count == 0) {
            return results;
        }

        var spawns = world.Run(script: "world.row spawnPoints").Output;
        var exhibits = captures.Select(selector: entry => ((entry.Capture.Exhibit is { } id) ? SweepExhibit.Read(exhibit: id, spawns: spawns, world: world) : null)).ToArray();
        var audit = (((Routes is null) || (tour.Audit is null))
            ? new Dictionary<string, IReadOnlyDictionary<string, string>>(comparer: StringComparer.Ordinal)
            : SweepMeshAudit.Run(
                budget: Routes.Budget,
                prototypes: exhibits.Select(selector: static exhibit => exhibit?.Prototype).OfType<string>().Distinct(comparer: StringComparer.Ordinal).ToArray(),
                repositoryRoot: Routes.RepositoryRoot,
                transcript: Path.Combine(path1: directory, path2: "tour", path3: stop.Name, path4: "mesh-audit.txt"),
                world: Routes.World
            ));

        for (var index = 0; (index < captures.Count); index++) {
            var (capture, file, line) = captures[index];
            var exhibit = exhibits[index];
            var name = $"{stop.Name}-{capture.Still}";
            var reasons = new List<string>();
            var status = SweepStatus.Clean;
            var where = Ran(command: "body.where 0", log: log, tick: (start + (ulong)capture.At));
            var shot = Ran(command: line, log: log, tick: (start + (ulong)capture.At));
            var eye = Ran(command: "world.view.camera 1 view,yaw,pitch", log: log, tick: (start + (ulong)capture.At));
            string? relative = null;
            double? changed = null;
            IReadOnlyDictionary<string, string>? metrics = null;

            if (File.Exists(path: file)) {
                relative = Path.GetRelativePath(path: file, relativeTo: directory).Replace(newChar: '/', oldChar: '\\');
                images[name] = MotionSheets.Read(path: file);

                if ((baseline?.StillFor(name: name) is { } earlier) && (MotionSheets.Read(path: earlier) is var before)) {
                    previous[name] = before;
                    changed = ChangedFraction(after: images[name], before: before, level: tour.LevelTolerance);

                    if ((changed is null) || (changed > tour.AmberFraction)) {
                        reasons.Add(item: ((changed is null) ? "the baseline still has another size" : string.Create(provider: CultureInfo.InvariantCulture, handler: $"{changed:P2} of its pixels changed beyond {tour.LevelTolerance} levels")));
                        status = SweepStatus.Worse(left: status, right: SweepStatus.Amber);
                    }
                }
            } else {
                reasons.Add(item: $"no still was written: {shot ?? "the capture line has no answer"}");
                status = SweepStatus.Red;
            }

            if ((exhibit is not null) && (exhibit.Prototype is null)) {
                reasons.Add(item: $"the World names no placement '{capture.Exhibit}'");
                status = SweepStatus.Red;
            }
            if ((where is null) || !MotionRig.TryPoseOf(answer: where, position: out var position, yawDegrees: out _)) {
                reasons.Add(item: "the body's pose at the capture has no answer");
                status = SweepStatus.Red;
            } else if (exhibit?.Spawn is { } spawn) {
                var distance = double.Hypot(x: (position.X - spawn.X), y: (position.Z - spawn.Z));

                if (distance > capture.Tolerance) {
                    reasons.Add(item: string.Create(provider: CultureInfo.InvariantCulture, handler: $"off course: the body stood {distance:0.00} m from {capture.Exhibit}'s spawn point, beyond {capture.Tolerance:0.##} m"));
                    status = SweepStatus.Red;
                }
            }
            if ((tour.Audit is { } rules) && (exhibit?.Prototype is { } prototype)) {
                if (!audit.TryGetValue(key: prototype, value: out metrics)) {
                    reasons.Add(item: $"unmeasured: the mesh audit reported no surface for {prototype}");
                    status = SweepStatus.Worse(left: status, right: SweepStatus.Unknown);
                } else {
                    var judged = new List<string>();

                    status = SweepEvidence.Judge(amber: rules.AmberRules, metrics: metrics, reasons: judged, red: rules.RedRules, status: status);
                    reasons.AddRange(collection: judged.Select(selector: static reason => $"mesh audit {reason}"));
                }
            }

            progress.WriteLine(value: $"sweep: still {name} {status}");
            results.Add(item: new SweepStillResult(
                Audit: metrics,
                Capture: (shot ?? string.Empty).Trim(),
                ChangedFraction: changed,
                Exhibit: capture.Exhibit,
                File: relative,
                Group: (stop.Group ?? "tour"),
                Motion: string.Join(separator: '\n', values: new[] { where, eye }.OfType<string>().Select(selector: static answer => answer.Trim())),
                Name: name,
                Note: stop.Note,
                Reasons: reasons,
                Status: status,
                Text: exhibit?.Text
            ));
        }

        return results;
    }
    // The answer a pinned line gave, found in the world.at log by its tick and command.
    private static string? Ran(string log, string command, ulong tick) {
        try {
            using var document = JsonDocument.Parse(json: log);

            if (!document.RootElement.TryGetProperty(propertyName: "ran", value: out var ran) || (ran.ValueKind != JsonValueKind.Array)) {
                return null;
            }

            return ran.EnumerateArray()
                .Where(predicate: row => (row.TryGetProperty(propertyName: "command", value: out var text) && (text.GetString() == command)
                    && row.TryGetProperty(propertyName: "tick", value: out var at) && at.TryGetUInt64(value: out var value) && (value == tick)))
                .Select(selector: static row => (row.TryGetProperty(propertyName: "output", value: out var output) ? output.GetString() : null))
                .FirstOrDefault();
        } catch (Exception exception) when ((exception is JsonException or InvalidOperationException)) {
            return null;
        }
    }
    // Arms the stop's pinned steps from a start tick a lead after the World's completed tick, and names the tick the
    // capture is pinned to. False, with the reason added, when the World's tick could not be read or the steps were refused.
    private bool Pin(SweepStop stop, SweepTour tour, List<string> reasons, out ulong captureTick, out IReadOnlyList<(ulong Tick, string Command)> expected) =>
        PinStop(world, stop, tour, "world.rate pause", reasons, out captureTick, out expected);

    /// <summary>Arms a stop's authored framing and steps, followed by a final line at its settle tick, and resumes it.</summary>
    public static bool PinStop(SweepWorld world, SweepStop stop, SweepTour tour, string finalLine, List<string> reasons,
        out ulong captureTick, out IReadOnlyList<(ulong Tick, string Command)> expected) {
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

        steps = [.. steps, new SweepStep(At: (stop.Settle ?? tour.Settle), Run: finalLine)];
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
    private SweepAnswer TourLine(string line) => RunTourLine(world, line);

    /// <summary>Runs authored tour lines with bounded tick waits, temporarily resuming a paused world for a wait.</summary>
    public static SweepAnswer RunTourLine(SweepWorld world, string line) {
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

            // The comparison keeps a colour of its own: the stored status stays what the signal's rules said, so a report
            // recorded against a baseline never carries this comparison's red into the next one.
            var changes = (baseline?.Changes(leg: world.Leg, metrics: metrics, signal: (signal with { Name = name }), status: status) ?? []);
            var result = new SweepSignalResult(
                Changes: changes,
                Comparison: ((baseline is null) ? null : ((changes.Count > 0) ? SweepStatus.Red : SweepStatus.Clean)),
                File: Path.GetRelativePath(path: file, relativeTo: directory).Replace(newChar: '/', oldChar: '\\'),
                Leg: world.Leg,
                Line: signal.Line,
                Metrics: metrics,
                Name: name,
                Reasons: reasons,
                Status: status
            );

            progress.WriteLine(value: $"sweep: {world.Leg} {name} {result.Colour}");
            results.Add(item: result);
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
