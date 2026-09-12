using System.Diagnostics;

using Puck.Hosting;
using Puck.World;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.Cli.Bench;

// puck bench world: the numbers this wave's brief quotes as throwaway probes, made a lane. The steady-state tick
// and Klondike-deal rows are far past what an iteration-based BenchmarkDotNet job could amortize honestly — this
// lane is a plain stopwatch harness instead: build once, measure the steady state, print one row per number.
internal static class WorldBenchmarks {
    // Same window HandleTickPathLawTests uses: warm past JIT/first-tick transients, then sample enough ticks that
    // the median is the steady-state claim and the widest tick is a visible outlier, not the headline.
    private const int WarmupTicks = 60;
    private const int SampleTicks = 120;
    private const string KlondikeFixtureRelativePath = "src/Puck.Cli/Bench/klondike.fixture.world.json";
    private const string ShippedWorldRelativePath = "src/Puck.World/Assets/worlds/puck.world.json";

    public static int Run() {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) {
            return 2;
        }

        var rows = new List<(string Name, string Value)>();

        try {
            MeasureShippedWorld(root: root, rows: rows);
            MeasureKlondikeDeal(root: root, rows: rows);
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"ERROR: {exception.Message}");

            return 1;
        }

        PrintTable(rows: rows);

        return 0;
    }

    private static void MeasureShippedWorld(string root, List<(string Name, string Value)> rows) {
        var path = Path.Combine(path1: root, path2: ShippedWorldRelativePath);

        // The validator asks which screen-machine engines ship and which compile a cartridge, and the answers come
        // from a registry the shipped brick extensions must be fed into first. CliWorldVocabulary is the CLI's one
        // installer and does both.
        CliWorldVocabulary.EnsureInstalled();

        // The island proves its seams against the shard documents beside it, read the way the host reads them.
        var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => (Path.GetDirectoryName(path: path) ?? string.Empty));

        if (!WorldDefinitionLoader.TryLoadFile(path: path, definition: out var definition, reason: out var reason, neighbours: neighbours)) {
            throw new InvalidOperationException(message: $"could not load the shipped world at {path}: {reason}");
        }

        var constructionTimer = Stopwatch.StartNew();

        using var bench = WorldBenchServer.Boot(definition: definition!);

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
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var timestampBefore = Stopwatch.GetTimestamp();

            server.Advance(stepTicks: stepTicks);

            elapsedTicksSamples[tick] = (Stopwatch.GetTimestamp() - timestampBefore);
            allocationSamples[tick] = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        }

        Array.Sort(array: allocationSamples);
        Array.Sort(array: elapsedTicksSamples);

        var medianAllocation = allocationSamples[(allocationSamples.Length / 2)];
        var medianElapsedMs = ((elapsedTicksSamples[(elapsedTicksSamples.Length / 2)] * 1000.0) / Stopwatch.Frequency);

        rows.Add(item: ("shipped world: idle tick (median)", $"{medianElapsedMs:F3} ms"));
        rows.Add(item: ("shipped world: quiet-tick allocation (median)", $"{medianAllocation:N0} bytes"));
    }

    private static void MeasureKlondikeDeal(string root, List<(string Name, string Value)> rows) {
        var path = Path.Combine(path1: root, path2: KlondikeFixtureRelativePath);
        var definition = WorldDefinitionSerialization.Deserialize(utf8Json: File.ReadAllBytes(path: path));

        using var bench = WorldBenchServer.Boot(definition: definition);

        var server = bench.Server;
        var stepTicks = EngineTicks.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));
        const string game = "solitaireKlondike";

        // The authored option row the deal reads before dealing (draw-1 vs draw-3) — settled before the window
        // opens so only the deal itself is measured.
        server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: game, Key: "option", Value: 1, Kind: WorldDocumentWriteKind.Set));
        server.Advance(stepTicks: stepTicks);

        var applied = 0;

        server.MutationJournalTap = (_, _) => applied++;

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();

        RequestDeal(server: server, game: game);

        var ticks = SettleUntilQuiet(server: server, game: game, stepTicks: stepTicks);

        stopwatch.Stop();

        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);

        if (ReadCell(server: server, row: game, key: "result") != 1) {
            throw new InvalidOperationException(message: "solitaireKlondike deal did not report success");
        }

        var perMutation = ((applied > 0) ? (allocated / (double)applied) : 0.0);
        var perTickMs = (stopwatch.Elapsed.TotalMilliseconds / ticks);

        rows.Add(item: ("klondike deal: per-tick time", $"{perTickMs:F3} ms ({ticks} ticks)"));
        rows.Add(item: ("klondike deal: per-mutation allocation", $"{perMutation:F0} bytes ({applied} mutations)"));
    }

    // Mirrors tests/Puck.World.Tests/SolitaireFixtures.cs's Request: enqueues the one action a scripted deal is,
    // keyed by the game's own incrementing request counter.
    private static void RequestDeal(WorldServer server, string game) {
        var request = (ReadCell(server: server, row: game, key: "request") + 1);

        foreach (var (key, value) in new (string Key, long Value)[] { ("action", 1), ("from", -1), ("to", -1), ("card", -1), ("request", request) }) {
            server.EnqueueMutation(mutation: new WorldMutation.UpsertStateCell(Principal: WorldPrincipal.Console, Row: game, Key: key, Value: value, Kind: WorldDocumentWriteKind.Set));
        }
    }

    // Mirrors SolitaireFixtures.Settle: steps until the game's own request/applied/stage/busy cells agree the
    // action landed, returning how many ticks that took.
    private static int SettleUntilQuiet(WorldServer server, string game, ulong stepTicks, int maxTicks = 240) {
        for (var tick = 0; (tick < maxTicks); tick++) {
            server.Advance(stepTicks: stepTicks);

            if ((ReadCell(server: server, row: game, key: "request") == ReadCell(server: server, row: game, key: "applied"))
                && (ReadCell(server: server, row: game, key: "stage") == 0)
                && (ReadCell(server: server, row: game, key: "busy") == 0)) {
                return (tick + 1);
            }
        }

        throw new InvalidOperationException(message: $"{game} did not settle within {maxTicks} ticks");
    }

    private static long ReadCell(WorldServer server, string row, string key) =>
        server.Definition.State.Single(predicate: candidate => (candidate.Name.Value == row)).Cells!.Single(predicate: cell => (cell.Key.Value == key)).Value;

    private static void PrintTable(List<(string Name, string Value)> rows) {
        var nameWidth = rows.Max(selector: row => row.Name.Length);

        foreach (var (name, value) in rows) {
            Console.WriteLine(value: $"{name.PadRight(totalWidth: nameWidth)}  {value}");
        }
    }
}
