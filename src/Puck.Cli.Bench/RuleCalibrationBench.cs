using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Puck.Cli.Determinism;
using Puck.Hosting;
using Puck.Maths;
using Puck.State.Rules;
using Puck.World;
using Puck.World.Server;

namespace Puck.Cli.Bench;

// puck bench rules: measures, on the machine it runs on, what each quantity the rule work sheet prices costs, and writes
// the calibration a device profile carries (render.devices[].rules). Each scenario is a world of compiled rules shaped to
// add one quantity to the one before it, run in process with every binding judged in place every tick (the sheet's worst
// case), and timed by the tick's own rule part (HostFrameWorkPart.TickRules). The units come from the evaluator's own
// spent counts at the sheet's prices, so the figures divide measured time by exactly what the sheet counts.
internal static class RuleCalibrationBench {
    private const string Verb = "bench rules";
    private const int RateHz = 30;
    // Sized so each scenario's rule pass is tens to hundreds of microseconds above the empty pass, and the scenario
    // worlds stay inside the uncalibrated budget their own admission prices them at.
    // 240 rules of 16 slots each stay under the document row limit (StateCapacity.MaxRows).
    private const int OnceRules = 240;
    private const int LongCheckTerms = 32;
    private const int SweepRules = 4;
    private const int SweepCells = 4096;
    private const int LongFiringEffects = 16;
    private const int GrowthArenas = 32;
    private const int GrowthFixedCells = 16_384;
    private const int GrowthDoublings = 16;

    private sealed record Scenario(string Name, string Source);
    // Per tick: the rule part's time and what the evaluator spent, the effect units split into doors and the rest.
    private readonly record struct Reading(double Picoseconds, double Setup, double Judged, double Check, double Firings, double Doors, double Elements);

    /// <summary>Creates the <c>rules</c> verb.</summary>
    /// <returns>The verb.</returns>
    public static Command Create() {
        var device = new Option<string?>(name: "--device") { Description = "The device profile the figures are for (render.devices' name, such as rtx4070); it is written into the calibration's build note." };
        var rounds = CliOptions.Count(defaultValue: 9, description: "Measured rounds, 3 to 99; every scenario runs once a round, interleaved, and each figure is read from the median round.", maximum: 99, minimum: 3, name: "--rounds");
        var ticks = CliOptions.Count(defaultValue: 60, description: "Ticks each scenario runs a round, 10 to 1000.", maximum: 1000, minimum: 10, name: "--ticks");
        var output = new Option<string?>(name: "--output") { Description = "Write the calibration as JSON (a RuleCostCalibration) to this file." };
        var emit = new Option<string?>(name: "--emit") { Description = "Write the scenario worlds' .puck sources into this directory and exit without measuring, so `puck compile --validate` can read them." };
        var command = new Command(
            description: "Measure what each quantity the rule work sheet prices costs on this machine, and print the calibration a device profile carries.",
            name: "rules"
        ) { device, rounds, ticks, output, emit };

        command.SetAction(action: parse => Run(
            device: parse.GetValue(option: device),
            emit: parse.GetValue(option: emit),
            output: parse.GetValue(option: output),
            rounds: parse.GetValue(option: rounds),
            ticks: parse.GetValue(option: ticks)
        ));
        command.Detail(detail: """
            Builds seven worlds of compiled rules in a scratch directory and runs each in this process with every binding
            judged in place every tick (WorldRuleHost.JudgesEveryBinding, the evaluation the sheet's worst case prices),
            interleaving the scenarios round by round:

              base         no rules: the rule pass's own fixed work
              check-short  240 rules evaluated once, a one-read gate that never holds
              check-long   the same rules with a 32-read gate: the difference is the check unit
              sweep        4 sweeps over 4096 cells each: the setup unit, beside the binding and check units
              fire-short   240 rules whose gate holds, one cell write each: the firing
              fire-long    the same rules with 16 writes each: the door (a write's fixed cost)
              fire-expr    the same 16 writes, each of a 32-read sum: the effect unit that is not a door

            Each figure is solved from the medians of the measured rounds against the evaluator's own spent counts
            (world.budget.rules --spent's units, the doors counted apart), so it is picoseconds per unit exactly as
            the sheet counts units.
            Arena growth is timed apart, over arenas whose growable row doubles to 65,536 cells beside a fixed
            16,384-cell row, against StateArena.ReservationWork. The host passes (flock affinities, decision perception)
            are not measured and take the check figure, which the build note says.

            Take the reading on a quiet machine (puck lease quiet), then paste the printed `rules { ... }` block into
            the device's profile in worlds/standard/quality.puck. Exit 0 when every figure was measured, 1 when a
            scenario failed to load or the rule part read no time.
            """);
        return command;
    }

    private static int Run(string? device, string? emit, string? output, int rounds, int ticks) {
        var scenarios = Scenarios();

        if (emit is not null) {
            Directory.CreateDirectory(path: emit);
            foreach (var scenario in scenarios) {
                File.WriteAllText(path: Path.Combine(path1: emit, path2: $"rule-calibration-{scenario.Name}.puck"), contents: scenario.Source);
            }
            Console.WriteLine(value: $"{Verb}: wrote {scenarios.Count} scenario worlds to {CliPaths.ToDisplay(fullPath: Path.GetFullPath(path: emit))}");
            return CliExit.Success;
        }

        using var directory = RunDirectory.Create(keepOnFailure: false, prefix: "puck-bench-rules-");
        var catalog = CliWorldVocabulary.EnsureInstalled();
        var servers = new List<WorldBenchServer>(capacity: scenarios.Count);

        try {
            foreach (var scenario in scenarios) {
                var path = Path.Combine(path1: directory.Path, path2: $"rule-calibration-{scenario.Name}.puck");

                File.WriteAllText(path: path, contents: scenario.Source);
                if (!WorldDefinitionLoader.TryLoadFile(catalog: catalog, catalogFingerprint: catalog.CompositionFingerprint, definition: out var definition, path: path, reason: out var reason)) {
                    return CliExit.Refuse(verb: Verb, what: $"scenario '{scenario.Name}'", why: $"did not load: {reason}");
                }

                var bench = WorldBenchServer.Boot(catalog: catalog, definition: definition!);

                servers.Add(item: bench);
                bench.Server.RuleHost.JudgesEveryBinding = true;
            }

            var step = FixedTickConversion.PerRate(ratePerSecond: RateHz);
            var work = new HostFrameWork();
            var times = new double[scenarios.Count][];
            var before = new WorldRuleSpent[scenarios.Count];

            for (var index = 0; (index < servers.Count); index++) {
                times[index] = new double[rounds];
                for (var tick = 0; (tick < ticks); tick++) { servers[index].Server.Advance(stepTicks: step); }
                before[index] = servers[index].Server.RuleHost.SpentTotals();
            }
            for (var round = 0; (round < rounds); round++) {
                for (var index = 0; (index < servers.Count); index++) {
                    var server = servers[index].Server;
                    var spent = 0L;

                    for (var tick = 0; (tick < ticks); tick++) {
                        using (work.Bind()) {
                            server.Advance(stepTicks: step);
                        }
                        spent += work.Times[(int)HostFrameWorkPart.TickRules];
                        work.Clear();
                    }
                    if (spent <= 0L) {
                        return CliExit.Refuse(verb: Verb, what: $"scenario '{scenarios[index].Name}'", why: "the tick's rule part read no time, so the tick did not run on this thread under the measurement");
                    }
                    times[index][round] = ((spent * 1e12) / (Stopwatch.Frequency * (double)ticks));
                }
            }

            var readings = new Reading[servers.Count];

            for (var index = 0; (index < servers.Count); index++) {
                var after = servers[index].Server.RuleHost.SpentTotals();
                var counted = (double)(after.Ticks - before[index].Ticks);
                var sorted = times[index].Order().ToArray();

                var doors = (after.Doors - before[index].Doors);

                readings[index] = new Reading(
                    Check: ((after.CheckUnits - before[index].CheckUnits) / counted),
                    Doors: (doors / counted),
                    Elements: (((after.EffectUnits - before[index].EffectUnits) - (doors * RuleWorkBudget.DoorUnits)) / counted),
                    Firings: ((after.Firings - before[index].Firings) / counted),
                    Judged: ((after.Judged - before[index].Judged) / counted),
                    Picoseconds: OrderStatistics.Quantile(ascending: sorted, fraction: 0.5),
                    Setup: ((after.SetupUnits - before[index].SetupUnits) / counted)
                );
            }

            return Report(device: device, output: output, readings: readings, rounds: rounds, scenarios: scenarios, ticks: ticks);
        } finally {
            foreach (var server in servers) { server.Dispose(); }
        }
    }

    private static int Report(IReadOnlyList<Scenario> scenarios, Reading[] readings, string? device, string? output, int rounds, int ticks) {
        Reading Delta(int index) => readings[index] with { Picoseconds = (readings[index].Picoseconds - readings[0].Picoseconds) };
        var (checkShort, checkLong, sweep, fireShort, fireLong, fireExpression) = (Delta(1), Delta(2), Delta(3), Delta(4), Delta(5), Delta(6));
        var clamped = new List<string>();

        // Each scenario adds one quantity to the one before it, so each figure is the extra time over the extra count,
        // once the figures already solved are taken out.
        var check = Figure(name: "check", value: ((checkLong.Picoseconds - checkShort.Picoseconds) / (checkLong.Check - checkShort.Check)));
        var binding = Figure(name: "binding", value: ((checkShort.Picoseconds - (check * checkShort.Check)) / checkShort.Judged));
        var setup = Figure(name: "setup", value: (((sweep.Picoseconds - (binding * sweep.Judged)) - (check * sweep.Check)) / sweep.Setup));
        // fire-expr differs from fire-long in its writes' sources alone, so its extra time is element work; fire-long
        // differs from fire-short in its writes, so what that leaves over the extra doors is the door.
        var effect = Figure(name: "effect", value: (((fireExpression.Picoseconds - fireLong.Picoseconds) - (check * (fireExpression.Check - fireLong.Check))) / (fireExpression.Elements - fireLong.Elements)));
        var door = Figure(name: "door", value: ((((fireLong.Picoseconds - fireShort.Picoseconds) - (check * (fireLong.Check - fireShort.Check))) - (effect * (fireLong.Elements - fireShort.Elements))) / (fireLong.Doors - fireShort.Doors)));
        var firing = Figure(name: "firing", value: (((((fireShort.Picoseconds - (binding * fireShort.Judged)) - (check * fireShort.Check)) - (door * fireShort.Doors)) - (effect * fireShort.Elements)) / fireShort.Firings));
        var growth = Figure(name: "growth", value: GrowthPicosecondsPerUnit());
        var build = string.Create(provider: CultureInfo.InvariantCulture, handler: $"{(device ?? "unnamed device")}; {BenchBuild.Of(entryAssembly: CliPaths.Tool).Version}; {DateTime.UtcNow:yyyy-MM-dd}; {rounds}x{ticks} ticks; host=check{((clamped.Count == 0) ? string.Empty : $"; clamped {string.Join(separator: ",", values: clamped)}")}");
        var calibration = new RuleCostCalibration(
            Binding: binding,
            Build: build,
            Check: check,
            Door: door,
            Effect: effect,
            Firing: firing,
            Growth: growth,
            Host: check,
            Setup: setup
        );

        for (var index = 0; (index < scenarios.Count); index++) {
            var reading = readings[index];

            Console.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"rules: {scenarios[index].Name,-12} {RuleTime.Milliseconds(picoseconds: (long)reading.Picoseconds),12} a tick; per tick setup={reading.Setup:0} judged={reading.Judged:0} check={reading.Check:0} firings={reading.Firings:0} doors={reading.Doors:0} elements={reading.Elements:0}"));
        }
        Console.WriteLine(value: string.Create(provider: CultureInfo.InvariantCulture, handler: $"rules: picoseconds per setup unit {setup}, binding {binding}, check unit {check}, firing {firing}, door {door}, effect unit {effect}, growth unit {growth}"));
        foreach (var name in clamped) {
            Console.Error.WriteLine(value: $"{Verb}: the {name} figure solved below zero or to no number and was taken as zero; the machine was not quiet, a scenario spent nothing of that quantity, or it is below this run's noise. Read it again with more --rounds.");
        }
        Console.WriteLine(value: calibration.Describe());
        if (output is not null) {
            File.WriteAllText(path: output, contents: JsonSerializer.Serialize(value: calibration, jsonTypeInfo: WorldJsonContext.Default.For<RuleCostCalibration>()), encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        return CliExit.Success;

        long Figure(string name, double value) {
            if (!double.IsFinite(d: value) || (value < 0D)) {
                clamped.Add(item: name);
                return 0L;
            }
            return (long)Math.Ceiling(a: value);
        }
    }

    // What one growth unit costs: arenas whose growable row doubles beside a fixed row, each growth timed against the
    // units StateArena charges it, and the median arena's ratio taken.
    private static double GrowthPicosecondsPerUnit() {
        var fixedCells = new StateCell[GrowthFixedCells];

        for (var index = 0; (index < fixedCells.Length); index++) {
            fixedCells[index] = new StateCell(Key: CellName.Parse(candidate: $"f{index.ToString(provider: CultureInfo.InvariantCulture)}"), Value: CellValue.Int(value: index));
        }

        var section = new StateSection(Rows: [
            new StateRow(Name: CellName.Parse(candidate: "fixed"), Kind: CellKind.Int, Capacity: GrowthFixedCells, Cells: fixedCells),
            new StateRow(Name: CellName.Parse(candidate: "growing"), Kind: CellKind.Int, Cells: [new StateCell(Key: CellName.Parse(candidate: "g0"), Value: CellValue.Int(value: 0L))]),
        ]);
        var catalog = StateCatalog.Compile(section: section);

        if (!catalog.TryResolve(handle: out var growing, lane: StateLane.Document, name: "growing")) {
            throw new InvalidOperationException(message: "The growth arena does not declare its growable row.");
        }

        var ratios = new double[GrowthArenas];

        for (var arenaIndex = -1; (arenaIndex < GrowthArenas); arenaIndex++) {
            var arena = new StateArena(catalog: catalog, options: null, section: section, time: ArenaTime.Origin);
            var charged = arena.ReservationWorkCharged;
            var elapsed = 0L;

            for (var doubling = 1; (doubling <= GrowthDoublings); doubling++) {
                var start = Stopwatch.GetTimestamp();

                if (!arena.TryReserveCells(reason: out var reason, required: (1 << doubling), rowOrdinal: growing.Ordinal)) {
                    throw new InvalidOperationException(message: $"The growth arena refused a reservation of {1 << doubling} cells: {reason}");
                }
                elapsed += (Stopwatch.GetTimestamp() - start);
            }

            // The first arena warms the growth path and is not counted.
            if (arenaIndex >= 0) {
                ratios[arenaIndex] = ((elapsed * 1e12) / (Stopwatch.Frequency * (double)(arena.ReservationWorkCharged - charged)));
            }
        }
        Array.Sort(array: ratios);
        return OrderStatistics.Quantile(ascending: ratios, fraction: 0.5);
    }

    private static IReadOnlyList<Scenario> Scenarios() {
        var longGate = string.Join(separator: " + ", values: Enumerable.Repeat(element: "zero", count: LongCheckTerms));

        return [
            new(Name: "base", Source: Document(name: "base", rules: string.Empty, state: "    slot zero = 0\n")),
            new(Name: "check-short", Source: OnceRuleDocument(name: "check-short", gate: "zero < 0", effects: 1, holds: false)),
            new(Name: "check-long", Source: OnceRuleDocument(name: "check-long", gate: $"({longGate}) < 0", effects: 1, holds: false)),
            new(Name: "sweep", Source: SweepDocument()),
            new(Name: "fire-short", Source: OnceRuleDocument(name: "fire-short", gate: "zero == 0", effects: 1, holds: true)),
            new(Name: "fire-long", Source: OnceRuleDocument(name: "fire-long", gate: "zero == 0", effects: LongFiringEffects, holds: true)),
            new(Name: "fire-expr", Source: OnceRuleDocument(name: "fire-expr", gate: "zero == 0", effects: LongFiringEffects, holds: true, source: $"{longGate} + $tick")),
        ];
    }
    // Rules evaluated once a tick, each writing its own slots: a gate that never holds charges its check alone, and one
    // that holds fires every tick, writing a value of the tick so no firing settles.
    private static string OnceRuleDocument(string name, string gate, int effects, bool holds, string source = "$tick") {
        var state = new StringBuilder(value: "    slot zero = 0\n");
        var rules = new StringBuilder();

        for (var rule = 0; (rule < OnceRules); rule++) {
            _ = rules.Append(provider: CultureInfo.InvariantCulture, handler: $"rule \"{name}-{rule}\" {{\n  when {gate}\n  mode: Level\n");
            for (var effect = 0; (effect < effects); effect++) {
                _ = state.Append(provider: CultureInfo.InvariantCulture, handler: $"    slot o{rule}x{effect} = 0\n");
                _ = rules.Append(provider: CultureInfo.InvariantCulture, handler: $"  o{rule}x{effect} = {(holds ? source : "1")}\n");
            }
            _ = rules.Append(value: "}\n\n");
        }
        return Document(name: name, rules: rules.ToString(), state: state.ToString());
    }
    // Sweeps over a full table: every key is snapshotted and judged, and the gate never holds.
    private static string SweepDocument() {
        var state = new StringBuilder(value: "    slot zero = 0\n");
        var rules = new StringBuilder();

        _ = state.Append(provider: CultureInfo.InvariantCulture, handler: $"    table cells capacity({SweepCells}) {{\n");
        for (var cell = 0; (cell < SweepCells); cell++) {
            _ = state.Append(provider: CultureInfo.InvariantCulture, handler: $"      c{cell} = 0\n");
        }
        _ = state.Append(value: "    }\n");
        for (var rule = 0; (rule < SweepRules); rule++) {
            _ = rules.Append(provider: CultureInfo.InvariantCulture, handler: $"rule \"sweep-{rule}\" {{\n  when cells[$each] < 0\n  forEach: cells\n  mode: Level\n  cells[$each] = 1\n}}\n\n");
        }
        return Document(name: "sweep", rules: rules.ToString(), state: state.ToString());
    }
    private static string Document(string name, string state, string rules) => string.Create(provider: CultureInfo.InvariantCulture, handler: $$"""
        // A rule calibration scenario (puck bench rules): {{name}}.
        schema: "puck.world.definition.v1"
        documentId: "rule-calibration-{{name}}"

        simulation {
          rateHz: {{RateHz}}
        }

        state {
          world {
        {{state}}  }
        }

        {{rules}}
        """);
}
