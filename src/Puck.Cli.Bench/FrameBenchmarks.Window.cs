using System.Globalization;
using System.Text;
using Puck.Abstractions;
using Puck.Cli.Sweep;
using Puck.Commands;

namespace Puck.Cli.Bench;

// The window: a route stop's own timeline read as a player takes it. The World runs display-paced with presentation
// unlocked from ticks and nothing waited on or settled once the timeline is armed; the readings are console lines pinned to
// the ticks the window opens and closes at, so each is taken in the drain of its own tick, and the change across the window
// is the difference of the two answers.
public static partial class FrameBenchmarks {
    private const double WindowBudgetMilliseconds = 16.7;
    private const string WindowFrames = "host.frames 4096 16.7 8";

    // One reading taken at both ends of the window.
    private sealed record WindowRead(string Name, string Open, string Close, ReadingParse Parse);

    // A reading the plan names that no console line answers today, with the step of the build order that supplies it.
    private sealed record WindowAbsent(string Reading, string Supplied);

    private static readonly WindowRead[] WindowReads = [
        new("frames", "host.frames 1 16.7 0", WindowFrames, ReadingParse.Pairs),
        new("residency", "world.residency", "world.residency", ReadingParse.Residency),
        new("render.record", "world.counters render.record --json", "world.counters render.record --json", ReadingParse.Counters),
        new("gpu.queue", "world.counters gpu.queue --json", "world.counters gpu.queue --json", ReadingParse.Counters),
        new("sdf", "world.counters sdf --json", "world.counters sdf --json", ReadingParse.Counters),
        new("memory", "host.memory", "host.memory", ReadingParse.Memory),
        new("audio", "audio.state", "audio.state", ReadingParse.Pairs),
        new("body", "body.where 0", "body.where 0", ReadingParse.Text),
    ];

    // The residency's own landing times from the bake's start (a time not reached reads pending), which are the only
    // timing of "demand to first drawn" a console line answers today.
    private static readonly string[] WindowLandingKeys = ["near-landed-at", "near-resident-at", "near-half-at", "near-ninety-at", "all-landed", "all-resident"];

    private static readonly WindowAbsent[] WindowAbsents = [
        new("floor shown, floor pending and floor failed", "step 5"),
        new("fallback shown (a painting's fallback, a held image, haze nearer than authored)", "steps 3 and 5"),
        new("reach drawn in", "step 10"),
        new("lighting owed in frames a view shows", "step 9"),
        new("evictions and re-requests", "steps 5 and 8"),
        new("the latency of each stage from demand to first drawn (read, decode, bake, upload) with its tail", "step 10; the landing times above stand in until then"),
        new("each physical row's owners and residual (host.memory prints pools, segments and ledger rows, not the rows' owners)", "step 2"),
        new("the transfer's reserve and commit ticks", "the transfers packet (open-items: Transfers, federation and server)"),
        new("the seat's commands per tick across the crossing", "the transfers packet (open-items: Transfers, federation and server)"),
        new("the voices the mixer started and released", "the transfers packet (audio.state reads only the live voice count)"),
    ];

    // Everything a window needs of the run that owns it.
    private sealed record WindowRun(SweepWorld World, SweepTour Tour, string Root, string Directory, bool Headless, string[] Passes, Func<string, bool, string> Ask, Action<string, double> Add, List<KeyValuePair<string, string>> Rows, Action<string> Print);

    private static IEnumerable<string> WindowMetricNames(string stop) =>
        [.. FrameFields.Select(field => $"{stop}.window.frames.{field}"), $"{stop}.window.frames.over", $"{stop}.window.steps.shed"];

    private static string WindowUnit(string name) => ((name.EndsWith(".over", StringComparison.Ordinal) || name.EndsWith(".shed", StringComparison.Ordinal)) ? "count" : "ms");

    // Opens a World display-paced and ready. The renderer-ready wait is the only wait: the cold-bake wait the settled mode
    // takes is what the window leaves to the crossing.
    private static void BeginWindow(WindowRun run) {
        _ = run.Ask("world.rate resume", false);
        if (!run.Headless) {
            _ = run.Ask("world.lockstep off", false);
            _ = run.Ask("world.wait ready 60", false);
        }
        foreach (var line in run.Tour.Prelude ?? []) {
            if (line.TrimStart().StartsWith("world.lockstep ", StringComparison.Ordinal)) { continue; }
            _ = Lenient(run, line);
        }
    }

    // A line a headless World may refuse for want of a renderer; its answer is kept, never an error there.
    private static string Lenient(WindowRun run, string line) {
        if (!run.Headless) { return run.Ask(line, true); }
        var answer = SweepRun.RunTourLine(run.World, line);
        run.Rows.Add(new($"headless: {line}", answer.Output));
        return answer.Output;
    }

    private static void RunWindowStop(WindowRun run, SweepStop stop) {
        var tour = run.Tour;
        var reasons = new List<string>();
        _ = run.Ask("world.rate pause", false);
        foreach (var line in (tour.Reset ?? []).Concat(stop.Lines)) { _ = Lenient(run, line); }

        var played = stop;
        SweepRoute? route = null;
        if (stop.Route is { } reference) {
            if (!SweepRoute.TryLoad(reference, run.Root, out route, out var why)) { throw new InvalidOperationException(why); }
            played = route!.Played(stop);
        }
        var end = (played.Settle ?? tour.Settle);
        var opens = (stop.Opens ?? 0);
        if (opens >= end) { throw new InvalidOperationException($"{stop.Name}: the window opens at {opens}, not before its timeline's end at {end}."); }

        var folder = Path.Combine(run.Directory, stop.Name);
        _ = Directory.CreateDirectory(folder);
        var reads = new HashSet<string>(StringComparer.Ordinal);
        // The tour's own frame-history reset at the start is the window's to place: at its opening, after the opening reads.
        var steps = (played.Steps ?? []).Where(step => !((step.At == 0) && (step.Run == "host.frames reset"))).ToList();
        foreach (var read in WindowReads) {
            steps.Add(new SweepStep(opens, read.Open));
            _ = reads.Add(read.Open);
        }
        steps.Add(new SweepStep(opens, "host.frames reset"));

        // A burst is the stop's own presented capture: the frames the display showed across the span, read back whole.
        var bursts = new List<(SweepCapture Capture, string Line)>();
        foreach (var capture in stop.Captures ?? []) {
            if (capture.Presented == 0) { continue; }
            if ((capture.At < opens) || (capture.At >= end)) { throw new InvalidOperationException($"{stop.Name}: the burst '{capture.Still}' at {capture.At} lies outside its window ({opens}..{end})."); }
            var target = PuckPaths.Normalize(Path.Combine(folder, $"{capture.Still}.png"));
            // A headless World composes no frame to present, so its burst is only the body's pose at the burst's first tick.
            var line = (run.Headless ? "body.where 0" : $"world.screenshot {CommandEcho.Quote(target)} presented {capture.Presented.ToString(CultureInfo.InvariantCulture)}");
            bursts.Add((capture, line));
            if (!run.Headless) { steps.Add(new SweepStep(capture.At, line)); }
            steps.Add(new SweepStep(capture.At, "body.where 0"));
        }

        foreach (var read in WindowReads) {
            steps.Add(new SweepStep(end, read.Close));
            _ = reads.Add(read.Close);
        }
        if (run.Passes.Length > 0) {
            steps.Add(new SweepStep(end, "world.gpu-timing"));
            _ = reads.Add("world.gpu-timing");
            _ = run.Ask("world.gpu-timing off\nworld.gpu-timing on", false);
        }

        if (!SweepRun.PinStop(run.World, played with { Steps = steps }, tour, "world.rate pause", reasons, out var closes, out var expected)) {
            throw new InvalidOperationException(string.Join("; ", reasons));
        }
        // The window is the timeline: nothing here waits on bakes, residency or capture convergence before it, and the
        // World is neither paused nor locked to ticks while it runs.
        if (!SweepRun.WaitThrough(run.World, closes, reasons)) { throw new InvalidOperationException(string.Join("; ", reasons)); }
        if ((bursts.Count > 0) && !run.Headless) { _ = run.Ask("world.wait captures 60", false); }

        var log = run.Ask("world.at --json", false);
        var armed = expected.Where(step => !reads.Contains(step.Command)).ToArray();
        for (var attempt = 0; (attempt < 64) && !SweepEvidence.Settled(log, expected); attempt++) { log = run.Ask("world.at --json", false); }
        var scheduled = SweepEvidence.Scheduled(new SweepAnswer(true, log, []), armed);
        if (!scheduled.Ok) {
            var note = string.Join("; ", scheduled.Refusals);
            if (!run.Headless) { throw new InvalidOperationException($"{stop.Name}: the timeline did not run as armed: {note}"); }
            run.Rows.Add(new($"{stop.Name}: unrun in a headless World", note));
        }

        string? Answer(string command) => SweepRun.Ran(log, command, closes);
        string? OpenAnswer(string command) => SweepRun.Ran(log, command, closes - (ulong)(end - opens));

        var text = new StringBuilder();
        text.AppendLine(string.Create(CultureInfo.InvariantCulture, $"window {stop.Name}: ticks {opens}..{end} after the start tick, display-paced, lockstep off, nothing settled first{(route is null ? string.Empty : $" (route {route.Reference})")}"));
        var found = new HashSet<string>(StringComparer.Ordinal);
        string? frames = null;

        foreach (var read in WindowReads) {
            var before = ConsoleReadings.Values(read.Parse, OpenAnswer(read.Open), out var beforeNote);
            var after = ConsoleReadings.Values(read.Parse, Answer(read.Close), out var afterNote);
            if (read.Name == "frames") { frames = Answer(read.Close); }
            if (read.Parse == ReadingParse.Text) { text.AppendLine($"  {read.Name}: {Squash(OpenAnswer(read.Open))} -> {Squash(Answer(read.Close))}"); }
            if ((beforeNote ?? afterNote) is { } unread) { text.AppendLine($"  {read.Name}: unmeasured: {unread}"); }

            var keys = ConsoleReadings.Keys.Where(key => key.Read == read.Name).ToArray();
            if ((afterNote is not null) && (keys.Length > 0)) {
                text.AppendLine($"  {read.Name}: absent, so are {string.Join(", ", keys.Select(key => key.Key))}");
            } else {
                foreach (var key in keys) { WindowKeyLine(text, read, key, before, after, found); }
            }
            if (read.Name == "residency") {
                foreach (var landing in WindowLandingKeys) {
                    foreach (var name in after.Keys.Where(name => name.EndsWith("." + landing, StringComparison.Ordinal)).Order(StringComparer.Ordinal)) {
                        text.AppendLine($"  {name}: {before.GetValueOrDefault(name, "absent")} -> {after[name]} (from the bake's start)");
                    }
                }
            }
            if ((read.Parse != ReadingParse.Text) && (read.Name != "frames")) {
                var moved = after.Where(pair => (ConsoleReadings.Number().Match(pair.Value) is { Success: true } number) && before.TryGetValue(pair.Key, out var start) && (ConsoleReadings.Number().Match(start) is { Success: true } first) && (Parse(number.Value) != Parse(first.Value)) && !found.Contains(pair.Key)).Select(pair => $"{pair.Key} {Delta(before[pair.Key], pair.Value)}").ToArray();
                if (moved.Length > 0) { text.AppendLine($"  {read.Name}: also moved: {string.Join(", ", moved)}"); }
            }
        }

        if (frames is not null) {
            var header = SweepSignals.Read("frames", frames, null);
            foreach (var field in FrameFields) {
                if (!header.TryGetValue(field, out var value) || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) {
                    throw new InvalidDataException($"{stop.Name}: the window's host.frames lacks a finite {field}.");
                }
                run.Add($"{stop.Name}.window.frames.{field}", number);
            }
            if (header.TryGetValue("over", out var over) && double.TryParse(over, NumberStyles.Float, CultureInfo.InvariantCulture, out var overCount)) { run.Add($"{stop.Name}.window.frames.over", overCount); }
            var shed = ConsoleReadings.Values(WindowReads[0].Parse, OpenAnswer(WindowReads[0].Open), out _).GetValueOrDefault("steps.shed");
            var shedAfter = ConsoleReadings.Values(WindowReads[0].Parse, frames, out _).GetValueOrDefault("steps.shed");
            if ((shed is not null) && (shedAfter is not null)) { run.Add($"{stop.Name}.window.steps.shed", Parse(shedAfter) - Parse(shed)); }
            text.AppendLine($"  the slowest frames and what each did: the answer's tail, kept in {stop.Name}/frames.txt; {SweepSignals.Read("frames", frames, null).GetValueOrDefault("iterations", "?")} iterations read, budget {WindowBudgetMilliseconds:0.0} ms");
            File.WriteAllText(Path.Combine(folder, "frames.txt"), frames);
        } else {
            text.AppendLine("  frames: unmeasured: the window's host.frames has no answer");
        }
        if (run.Passes.Length > 0) {
            var gpu = ReadGpu(Answer("world.gpu-timing") ?? string.Empty, run.Passes);
            if (gpu is null) {
                text.AppendLine($"  gpu passes {string.Join(", ", run.Passes)}: unmeasured: no complete timestamp window");
            } else {
                foreach (var (pass, value) in gpu) {
                    run.Add($"{stop.Name}.gpu.{pass}", value);
                    text.AppendLine($"  gpu {pass}: {value:0.###} ms (rolling mean over the window)");
                }
            }
        }
        foreach (var (capture, line) in bursts) {
            var written = Directory.EnumerateFiles(folder, $"{capture.Still}*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            var pose = SweepRun.Ran(log, "body.where 0", closes - (ulong)(end - capture.At));
            text.AppendLine(run.Headless
                ? $"  presented burst '{capture.Still}' at {capture.At}: absent (a headless World composes no frame to present); the body at its first tick: {Squash(pose)}"
                : $"  presented burst '{capture.Still}' at {capture.At}: {written.Length} png(s) in {PuckPaths.Normalize(folder)} ({Squash(pose)}); per-frame pixel changes are on the World's stderr.log");
        }
        foreach (var absent in WindowAbsents) { text.AppendLine($"  absent: {absent.Reading} (supplied by {absent.Supplied})"); }

        var arrival = route?.Arrival(Answer("body.where 0") ?? string.Empty, out _);
        if ((arrival is { Count: > 0 }) && !run.Headless) {
            text.AppendLine($"  OFF COURSE: the arrival {string.Join("; ", arrival)}");
        }

        var report = text.ToString();
        File.WriteAllText(Path.Combine(folder, "window.txt"), report);
        run.Print(report);
        foreach (var line in report.TrimEnd().Split('\n')) { run.Rows.Add(new($"{stop.Name}", line.TrimEnd('\r'))); }
        if ((arrival is { Count: > 0 }) && !run.Headless) { throw new InvalidDataException($"{stop.Name}: off course, so the window did not walk the crossing: {string.Join("; ", arrival)}"); }

        static string Squash(string? answer) => ((answer is null) ? "no answer" : answer.Trim().ReplaceLineEndings(" "));
        static double Parse(string value) => double.Parse(ConsoleReadings.Number().Match(value).Value, CultureInfo.InvariantCulture);
        static string Delta(string before, string after) => string.Create(CultureInfo.InvariantCulture, $"{before} -> {after} ({(Parse(after) - Parse(before)):+0.###;-0.###;0})");
    }

    private static void WindowKeyLine(StringBuilder text, WindowRead read, ReadingKey key, SortedDictionary<string, string> before, SortedDictionary<string, string> after, HashSet<string> found) {
        var names = after.Keys.Where(name => (name == key.Key) || name.EndsWith("." + key.Key, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (names.Length == 0) {
            text.AppendLine($"  {key.Key}: absent from the {read.Name} answer ({key.Meaning})");
            return;
        }
        foreach (var name in names) {
            _ = found.Add(name);
            var start = before.GetValueOrDefault(name, "absent");
            var end = after[name];
            var number = ConsoleReadings.Number();
            var change = (number.IsMatch(start) && number.IsMatch(end))
                ? string.Create(CultureInfo.InvariantCulture, $" (change {(double.Parse(number.Match(end).Value, CultureInfo.InvariantCulture) - double.Parse(number.Match(start).Value, CultureInfo.InvariantCulture)):+0.###;-0.###;0})")
                : string.Empty;
            text.AppendLine($"  {name}: {start} -> {end}{change}");
        }
    }

}
