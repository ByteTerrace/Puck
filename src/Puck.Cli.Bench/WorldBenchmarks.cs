using Puck.Cli.Determinism;
using Puck.Commands;
using System.Diagnostics;

using Puck.Abstractions.Counting;
using Puck.Abstractions.Machines;
using Puck.Hosting;
using Puck.World;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.Cli.Bench;

// puck bench world: the numbers this wave's brief quotes as throwaway probes, made a lane. The steady-state tick
// and Klondike-deal rows are far past what an iteration-based BenchmarkDotNet job could amortize honestly — this
// lane is a plain stopwatch harness instead: build once, measure the steady state, print one row per number.
internal static class WorldBenchmarks {
    private const string KlondikeFixtureRelativePath = "src/Puck.Cli.Bench/klondike.fixture.puck";
    private const int MachineSampleTicks = 60;
    private const int PacedSampleTicks = 60;
    private const int SampleTicks = 120;
    private const string ShippedWorldRelativePath = "src/Puck.World/Assets/worlds/puck.world.json";
    // Same window HandleTickPathLawTests uses: warm past JIT/first-tick transients, then sample enough ticks that
    // the median is the steady-state claim and the widest tick is a visible outlier, not the headline.
    private const int WarmupTicks = 60;

    private static void MeasureKlondikeDeal(string root, List<(string Name, string Value)> rows) {
        var path = Path.Combine(
            path1: root,
            path2: KlondikeFixtureRelativePath
        );
        var catalog = CliWorldVocabulary.EnsureInstalled();

        if (!WorldDefinitionLoader.TryLoadFile(
            path: path,
            definition: out var loaded,
            reason: out var reason,
            neighbours: new WorldFileNeighbourResolver(baseDirectory: () => (Path.GetDirectoryName(path: path) ?? string.Empty)),
            catalog: catalog,
            catalogFingerprint: catalog.CompositionFingerprint
        )) {
            throw new InvalidOperationException(message: $"could not load the klondike fixture at {path}: {reason}");
        }

        var definition = loaded!;

        using var bench = WorldBenchServer.Boot(catalog: catalog, definition: definition);

        var server = bench.Server;
        var stepTicks = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));
        const string Game = "solitaireKlondike";

        // The authored option row the deal reads before dealing (draw-1 vs draw-3) — settled before the window
        // opens so only the deal itself is measured.
        server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
            Principal: Principal.Console,
            Row: Game,
            Key: "option",
            Value: 1,
            Kind: WorldDocumentWriteKind.Set
        ));
        server.Advance(stepTicks: stepTicks);

        var applied = 0;

        server.MutationJournalTap = (_, _, _) => applied++;

        var ticks = 0;
        var stopwatch = Stopwatch.StartNew();
        var allocated = AllocationWindow.Total(window: () => {
            RequestDeal(
                game: Game,
                server: server
            );
            ticks = SettleUntilQuiet(
                server: server,
                game: Game,
                stepTicks: stepTicks
            );
        });

        stopwatch.Stop();

        if (ReadCell(
            key: "result",
            row: Game,
            server: server
        ) != 1) {
            throw new InvalidOperationException(message: "solitaireKlondike deal did not report success");
        }

        var perMutation = ((applied > 0)
            ? (allocated / ((double)applied))
            : 0.0
        );
        var perTickMs = (stopwatch.Elapsed.TotalMilliseconds / ticks);

        rows.Add(item: ("klondike deal: per-tick time", $"{perTickMs:F3} ms ({ticks} ticks)"));
        rows.Add(item: ("klondike deal: per-mutation allocation", $"{perMutation:F0} bytes ({applied} mutations)"));
    }
    private static void MeasureShippedWorld(string root, List<(string Name, string Value)> rows) {
        var path = Path.Combine(
            path1: root,
            path2: ShippedWorldRelativePath
        );

        // The validator asks which screen-machine engines ship and which compile a cartridge, and the answers come
        // from a registry the shipped brick extensions must be fed into first. CliWorldVocabulary is the CLI's one
        // installer and does both.
        var catalog = CliWorldVocabulary.EnsureInstalled();

        // The island proves its seams against the shard documents beside it, read the way the host reads them.
        var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => (Path.GetDirectoryName(path: path) ?? string.Empty));

        if (!WorldDefinitionLoader.TryLoadFile(
            path: path,
            definition: out var definition,
            reason: out var reason,
            neighbours: neighbours,
            catalog: catalog,
            catalogFingerprint: catalog.CompositionFingerprint
        )) {
            throw new InvalidOperationException(message: $"could not load the shipped world at {path}: {reason}");
        }

        var constructionTimer = Stopwatch.StartNew();

        using var bench = WorldBenchServer.Boot(
            catalog: catalog,
            definition: definition!
        );

        constructionTimer.Stop();
        rows.Add(item: ("server construction (shipped world)", $"{constructionTimer.Elapsed.TotalSeconds:F1} s"));

        var server = bench.Server;
        var stepTicks = EngineTicks.PerRate(ratePerSecond: ((uint)definition!.SimulationRateHz));

        for (var tick = 0; (tick < WarmupTicks); tick++) {
            server.Advance(stepTicks: stepTicks);
        }

        var allocationSamples = new long[SampleTicks];
        var elapsedTicksSamples = new long[SampleTicks];

        for (var tick = 0; (tick < SampleTicks); tick++) {
            var timestampBefore = Stopwatch.GetTimestamp();

            allocationSamples[tick] = AllocationWindow.Total(window: () => server.Advance(stepTicks: stepTicks));
            elapsedTicksSamples[tick] = (Stopwatch.GetTimestamp() - timestampBefore);
        }

        Array.Sort(array: allocationSamples);
        Array.Sort(array: elapsedTicksSamples);

        var medianAllocation = allocationSamples[(allocationSamples.Length / 2)];
        var medianElapsedMs = ((elapsedTicksSamples[(elapsedTicksSamples.Length / 2)] * 1000.0) / Stopwatch.Frequency);

        rows.Add(item: ("shipped world: idle tick (median)", $"{medianElapsedMs:F3} ms"));
        rows.Add(item: ("shipped world: quiet-tick allocation (median)", $"{medianAllocation:N0} bytes"));
        MeasureShippedMachines(
            bench: bench,
            definition: definition,
            rows: rows,
            stepTicks: stepTicks
        );
    }
    // The screen machines emulate on worker threads the tick only submits to; a tick waits on one only when that
    // machine's pending window is full. The back-to-back window above is bounded by the slowest machine once the
    // windows fill, so the tick is also timed paced at the simulation rate, as the game runs it, beside the count of
    // submissions that waited. Each machine's own tick of emulation is then timed alone, synchronously.
    private static void MeasureShippedMachines(WorldBenchServer bench, WorldDefinition definition, ulong stepTicks, List<(string Name, string Value)> rows) {
        var machines = new List<(int Screen, IMachineRuntime Runtime)>();

        foreach (var screen in definition.Screens) {
            if (
                (bench.Machines.MachineAt(index: screen.Index) is { } runtime) &&
                !machines.Exists(match: entry => ReferenceEquals(
                    objA: entry.Runtime,
                    objB: runtime
                ))
            ) {
                machines.Add(item: (screen.Index, runtime));
            }
        }

        long Backpressure() =>
            machines.Sum(selector: entry => (bench.Machines.State(index: entry.Screen)?.BackpressureEvents ?? 0L));

        var server = bench.Server;
        var waitsBefore = Backpressure();
        var period = ((long)((((ulong)Stopwatch.Frequency) * stepTicks) / EngineTicks.PerSecond));
        var pacedSamples = new long[PacedSampleTicks];
        var due = Stopwatch.GetTimestamp();

        for (var tick = 0; (tick < PacedSampleTicks); tick++) {
            while (Stopwatch.GetTimestamp() < due) {
                Thread.Sleep(millisecondsTimeout: 1);
            }

            due += period;

            var timestampBefore = Stopwatch.GetTimestamp();

            server.Advance(stepTicks: stepTicks);
            pacedSamples[tick] = (Stopwatch.GetTimestamp() - timestampBefore);
        }

        rows.Add(item: ("shipped world: paced tick (median)", $"{MedianMilliseconds(samples: pacedSamples):F3} ms ({PacedSampleTicks} ticks at {definition.SimulationRateHz} Hz; {(Backpressure() - waitsBefore)} waits on {machines.Count} machines)"));

        foreach (var (screen, runtime) in machines) {
            var samples = new long[MachineSampleTicks];

            for (var tick = 0; (tick < MachineSampleTicks); tick++) {
                var timestampBefore = Stopwatch.GetTimestamp();

                _ = runtime.Advance(deltaTicks: stepTicks);
                samples[tick] = (Stopwatch.GetTimestamp() - timestampBefore);
            }

            rows.Add(item: ($"shipped world: screen {screen} machine ({bench.Machines.State(index: screen)?.Engine}), one tick alone (median)", $"{MedianMilliseconds(samples: samples):F3} ms ({MachineSampleTicks} synchronous ticks)"));
        }
    }
    private static double MedianMilliseconds(long[] samples) {
        Array.Sort(array: samples);

        return ((samples[(samples.Length / 2)] * 1000.0) / Stopwatch.Frequency);
    }
    private static void PrintTable(List<(string Name, string Value)> rows) {
        var nameWidth = rows.Max(selector: row => row.Name.Length);

        foreach (var (name, value) in rows) {
            Console.WriteLine(value: $"{name.PadRight(totalWidth: nameWidth)}  {value}");
        }
    }
    private static long ReadCell(WorldServer server, string row, string key) =>
        server.Definition.State.Single(predicate: candidate => (candidate.Name.Value == row)).Cells!.Single(predicate: cell => (cell.Key.Value == key)).Value.Raw;
    // Mirrors tests/Shared/World/SolitaireFixtures.cs's Request: enqueues the one action a scripted deal is,
    // keyed by the game's own incrementing request counter.
    private static void RequestDeal(WorldServer server, string game) {
        var request = (ReadCell(
            key: "request",
            row: game,
            server: server
        ) + 1);

        foreach (var (key, value) in new (string Key, long Value)[] { ("action", 1), ("from", -1), ("to", -1), ("card", -1), ("request", request) }) {
            server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(
                Principal: Principal.Console,
                Row: game,
                Key: key,
                Value: value,
                Kind: WorldDocumentWriteKind.Set
            ));
        }
    }
    // Mirrors SolitaireFixtures.Settle: steps until the game's own request/applied/stage/busy cells agree the
    // action landed, returning how many ticks that took.
    private static int SettleUntilQuiet(WorldServer server, string game, ulong stepTicks, int maxTicks = 240) {
        for (var tick = 0; (tick < maxTicks); tick++) {
            server.Advance(stepTicks: stepTicks);

            if (
                (ReadCell(
                key: "request",
                row: game,
                server: server
            ) == ReadCell(
                key: "applied",
                row: game,
                server: server
            )) &&
                (ReadCell(
                key: "stage",
                row: game,
                server: server
            ) == 0) &&
                (ReadCell(
                key: "busy",
                row: game,
                server: server
            ) == 0)
            ) {
                return (tick + 1);
            }
        }

        throw new InvalidOperationException(message: $"{game} did not settle within {maxTicks} ticks");
    }

    public static int Run() {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) {
            return 2;
        }

        var rows = new List<(string Name, string Value)>();

        try {
            MeasureShippedWorld(
                root: root,
                rows: rows
            );
            MeasureKlondikeDeal(
                root: root,
                rows: rows
            );
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"ERROR: {exception.Message}");

            return 1;
        }

        PrintTable(rows: rows);

        return 0;
    }
}
