using Puck.Cli.Determinism;
using System.Diagnostics;

using Puck.Abstractions.Counting;
using Puck.Abstractions.Machines;
using Puck.Maths;
using Puck.Physics;
using Puck.SignedDistance.Queries;
using Puck.World;

namespace Puck.Cli.Bench;

// puck bench world: the server tick path of one world, measured with a plain stopwatch harness. Server construction
// and the steady-state tick are far past what an iteration-based BenchmarkDotNet job could amortize honestly, so the
// lane builds once, measures the steady state, and prints one row per number.
internal static class WorldBenchmarks {
    /// <summary>The world measured when none is named: the debug world's asset package, repository-relative.</summary>
    public const string DefaultWorldRelativePath = "worlds/debug";

    private const int MachineSampleTicks = 60;
    private const int PacedSampleTicks = 60;
    private const int SampleTicks = 120;
    // Same window HandleTickPathLawTests uses: warm past JIT/first-tick transients, then sample enough ticks that
    // the median is the steady-state claim and the widest tick is a visible outlier, not the headline.
    private const int WarmupTicks = 60;

    private static void MeasureWorld(string path, List<(string Name, string Value)> rows) {
        // The validator asks which screen-machine engines ship and which compile a cartridge, and the answers come
        // from a registry the shipped brick extensions must be fed into first. CliWorldVocabulary is the CLI's one
        // installer and does both.
        var catalog = CliWorldVocabulary.EnsureInstalled();

        // A world proves its seams against the neighbour documents beside it, read the way the host reads them.
        var neighbours = new WorldFileNeighbourResolver(baseDirectory: () => (Path.GetDirectoryName(path: path) ?? string.Empty));

        if (!WorldDefinitionLoader.TryLoadFile(
            path: path,
            definition: out var definition,
            reason: out var reason,
            neighbours: neighbours,
            catalog: catalog,
            catalogFingerprint: catalog.CompositionFingerprint
        )) {
            throw new InvalidOperationException(message: $"could not load the world at {path}: {reason}");
        }

        var constructionTimer = Stopwatch.StartNew();

        using var bench = WorldBenchServer.Boot(
            catalog: catalog,
            definition: definition!
        );

        constructionTimer.Stop();
        rows.Add(item: ("world: server construction", $"{constructionTimer.Elapsed.TotalSeconds:F1} s"));

        var server = bench.Server;
        var stepTicks = FixedTickConversion.PerRate(ratePerSecond: ((uint)definition!.SimulationRateHz));

        for (var tick = 0; (tick < WarmupTicks); tick++) {
            server.Advance(stepTicks: stepTicks);
        }

        var allocationSamples = new long[SampleTicks];
        var elapsedTicksSamples = new long[SampleTicks];
        var countsBefore = ReadContactWork();

        for (var tick = 0; (tick < SampleTicks); tick++) {
            var timestampBefore = Stopwatch.GetTimestamp();

            allocationSamples[tick] = AllocationWindow.Total(window: () => server.Advance(stepTicks: stepTicks));
            elapsedTicksSamples[tick] = (Stopwatch.GetTimestamp() - timestampBefore);
        }

        var countsAfter = ReadContactWork();

        Array.Sort(array: allocationSamples);
        Array.Sort(array: elapsedTicksSamples);

        var medianAllocation = allocationSamples[(allocationSamples.Length / 2)];
        var medianElapsedMs = ((elapsedTicksSamples[(elapsedTicksSamples.Length / 2)] * 1000.0) / Stopwatch.Frequency);

        rows.Add(item: ("world: idle tick (median)", $"{medianElapsedMs:F3} ms"));
        rows.Add(item: ("world: quiet-tick allocation (median)", $"{medianAllocation:N0} bytes"));

        // The contact work behind the tick, as deterministic counts: what a timing row is read beside.
        for (var kind = 0; (kind < ContactWorkKinds.Length); kind++) {
            rows.Add(item: ($"world: {ContactWorkKinds[kind].Name} per tick", $"{((countsAfter[kind] - countsBefore[kind]) / ((double)SampleTicks)):F1}"));
        }

        MeasureValueWrite(
            rows: rows,
            server: server,
            stepTicks: stepTicks
        );
        MeasureCellBurst(rows: rows, server: server, stepTicks: stepTicks);
        MeasureMachines(
            bench: bench,
            definition: definition,
            rows: rows,
            stepTicks: stepTicks
        );
    }
    private static void MeasureCellBurst(Puck.World.Server.WorldServer server, ulong stepTicks, List<(string Name, string Value)> rows) {
        // The debug scope's thirteen writes, using its real actor and row grants. Decode/fuel is outside this
        // host-cost window, just as construction of the whole-row mutation is outside MeasureValueWrite's window.
        var scope = WorldDefinitionRows.FindStateRow(server.Definition.State, "scope");

        if (scope is null) { rows.Add(item: ("world: 13-cell guest burst", "no scope rows")); return; }
        var principal = Puck.Commands.Principal.Addon(name: "kinematics-scope");
        var samples = new long[SampleTicks];
        var allocations = new long[SampleTicks];
        var applied = 0;

        server.MutationJournalTap += (_, _, mutation) => {
            if (mutation.Principal == principal) { applied++; }
        };
        var period = ((long)((((ulong)Stopwatch.Frequency) * stepTicks) / FixedTickConversion.TicksPerSecond));
        var due = Stopwatch.GetTimestamp();

        for (var tick = 0; (tick < (WarmupTicks + SampleTicks)); tick++) {
            while (Stopwatch.GetTimestamp() < due) { Thread.Sleep(millisecondsTimeout: 1); }
            due += period;
            var writes = new List<Puck.World.Protocol.WorldMutation.UpsertStateCell>();

            foreach (var cell in scope.Cells!) {
                writes.Add(item: new(principal, "scope", cell.Key.Value, ((cell.Key.Value == "enabled") ? 0 : tick), Puck.World.Protocol.WorldDocumentWriteKind.Set));
            }
            foreach (var name in new[] { "scopeHeight", "scopeVelocity", "scopeFacts", "scopeContacts", "scopeTicks", "scopeEvents" }) {
                writes.Add(item: new(principal, name, "0", tick, Puck.World.Protocol.WorldDocumentWriteKind.Set));
            }
            foreach (var write in writes) {
                server.EnqueueMutation(write, sourceAddonInstanceId: long.MaxValue);
            }
            var before = applied;
            var start = Stopwatch.GetTimestamp();
            var bytes = AllocationWindow.Total(window: () => server.Advance(stepTicks: stepTicks));

            if ((applied - before) != 13) { throw new InvalidOperationException(message: $"guest burst applied {(applied - before)} of 13 writes"); }
            if (tick >= WarmupTicks) {
                samples[(tick - WarmupTicks)] = (Stopwatch.GetTimestamp() - start);
                allocations[(tick - WarmupTicks)] = bytes;
            }
        }
        Array.Sort(array: allocations);
        rows.Add(item: ("world: 13-cell guest burst tick (median)", $"{MedianMilliseconds(samples: samples):F3} ms"));
        rows.Add(item: ("world: 13-cell guest burst tick allocation (median)", $"{allocations[(SampleTicks / 2)]:N0} bytes"));
    }
    // A whole-row write that moves one cell's value and nothing else, as an addon's UpsertStateRow does (the gallery
    // guide's): the tick that drains it, timed and metered, beside the arena rows whose version it moved and the
    // installed rows it replaced, which a quiet tick's own rule writes add to.
    private static void MeasureValueWrite(Puck.World.Server.WorldServer server, ulong stepTicks, List<(string Name, string Value)> rows) {
        var row = server.Definition.State.FirstOrDefault(predicate: candidate => (
            (candidate.Shape == Puck.State.RowShape.Slot) &&
            (candidate.Kind == Puck.State.CellKind.Int) &&
            !candidate.IsRuleWritten &&
            (candidate.Dynamics is null) &&
            (candidate.Cycle is null) &&
            (candidate.Advance is null) &&
            (candidate.Cells is { Count: 1 } cells) &&
            (cells[0].Dynamics is null) &&
            (cells[0].Cycle is null) &&
            (cells[0].Advance is null)
        ));

        if (row is null) {
            rows.Add(item: ("world: state value write", "no plain integer slot row to write"));

            return;
        }

        var allocationSamples = new long[SampleTicks];
        var elapsedTicksSamples = new long[SampleTicks];
        var versionsMoved = new long[SampleTicks];
        var rowsReplaced = new long[SampleTicks];
        var versions = new ulong[server.Arena.Layout.RowCount];
        // Paced like the game, so a screen machine's full window does not stand in for the write's own cost.
        var period = ((long)((((ulong)Stopwatch.Frequency) * stepTicks) / FixedTickConversion.TicksPerSecond));
        var due = Stopwatch.GetTimestamp();

        for (var tick = 0; (tick < (WarmupTicks + SampleTicks)); tick++) {
            while (Stopwatch.GetTimestamp() < due) {
                Thread.Sleep(millisecondsTimeout: 1);
            }

            due += period;

            var installed = server.Definition.State;
            var current = WorldDefinitionRows.FindStateRow(rows: installed, name: row.Name.Value)!;
            var cell = current.Cells![0];
            var written = (current with { Cells = [(cell with { Value = Puck.State.CellValue.Int(value: (cell.Value.Raw + 1L)) })] });

            for (var ordinal = 0; (ordinal < versions.Length); ordinal++) {
                versions[ordinal] = server.Arena.RowVersion(rowOrdinal: ordinal);
            }

            server.EnqueueMutation(mutation: new Puck.World.Protocol.WorldMutation.UpsertStateRow(Principal: Puck.Commands.Principal.World, Row: written));

            var timestampBefore = Stopwatch.GetTimestamp();
            var allocated = AllocationWindow.Total(window: () => server.Advance(stepTicks: stepTicks));
            var elapsed = (Stopwatch.GetTimestamp() - timestampBefore);

            if (tick < WarmupTicks) {
                continue;
            }

            var sample = (tick - WarmupTicks);
            var after = server.Definition.State;

            allocationSamples[sample] = allocated;
            elapsedTicksSamples[sample] = elapsed;

            for (var ordinal = 0; (ordinal < versions.Length); ordinal++) {
                versionsMoved[sample] += ((versions[ordinal] != server.Arena.RowVersion(rowOrdinal: ordinal)) ? 1L : 0L);
            }
            for (var index = 0; (index < Math.Min(val1: after.Count, val2: installed.Count)); index++) {
                rowsReplaced[sample] += (ReferenceEquals(objA: after[index], objB: installed[index]) ? 0L : 1L);
            }
        }

        Array.Sort(array: allocationSamples);
        Array.Sort(array: versionsMoved);
        Array.Sort(array: rowsReplaced);
        rows.Add(item: ($"world: state value write tick (median, row '{row.Name.Value}')", $"{MedianMilliseconds(samples: elapsedTicksSamples):F3} ms"));
        rows.Add(item: ("world: state value write tick allocation (median)", $"{allocationSamples[(allocationSamples.Length / 2)]:N0} bytes"));
        rows.Add(item: ("world: state value write tick rows moved (median)", $"{versionsMoved[(versionsMoved.Length / 2)]} arena versions, {rowsReplaced[(rowsReplaced.Length / 2)]} installed rows of {server.Definition.State.Count}"));
    }
    // The screen machines emulate on worker threads the tick only submits to; a tick waits on one only when that
    // machine's pending window is full. The back-to-back window above is bounded by the slowest machine once the
    // windows fill, so the tick is also timed paced at the simulation rate, as the game runs it, beside the count of
    // submissions that waited. Each machine's own tick of emulation is then timed alone, synchronously.
    private static void MeasureMachines(WorldBenchServer bench, WorldDefinition definition, ulong stepTicks, List<(string Name, string Value)> rows) {
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
        var period = ((long)((((ulong)Stopwatch.Frequency) * stepTicks) / FixedTickConversion.TicksPerSecond));
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

        rows.Add(item: ("world: paced tick (median)", $"{MedianMilliseconds(samples: pacedSamples):F3} ms ({PacedSampleTicks} ticks at {definition.SimulationRateHz} Hz; {(Backpressure() - waitsBefore)} waits on {machines.Count} machines)"));

        foreach (var (screen, runtime) in machines) {
            var samples = new long[MachineSampleTicks];

            for (var tick = 0; (tick < MachineSampleTicks); tick++) {
                var timestampBefore = Stopwatch.GetTimestamp();

                _ = runtime.Advance(deltaTicks: stepTicks);
                samples[tick] = (Stopwatch.GetTimestamp() - timestampBefore);
            }

            rows.Add(item: ($"world: screen {screen} machine ({bench.Machines.State(index: screen)?.Engine}), one tick alone (median)", $"{MedianMilliseconds(samples: samples):F3} ms ({MachineSampleTicks} synchronous ticks)"));
        }
    }
    private static double MedianMilliseconds(long[] samples) {
        Array.Sort(array: samples);

        return ((samples[(samples.Length / 2)] * 1000.0) / Stopwatch.Frequency);
    }

    // The certified sweep's work and the immutable field's bounds work it spends, in report order.
    private static readonly WorkKind[] ContactWorkKinds = [
        FixedContactSweepWork.Sweeps,
        FixedContactSweepWork.BoundsQueries,
        SdfBoundsCache.Evaluations,
        SdfBoundsCache.Instructions,
        SdfBoundsCache.Rotations,
        SdfFieldEvaluator.PointEvaluations,
        SdfFieldEvaluator.PointInstructions,
    ];

    private static long[] ReadContactWork() => [
        FixedContactSweepWork.Process.Read(kind: FixedContactSweepWork.Sweeps),
        FixedContactSweepWork.Process.Read(kind: FixedContactSweepWork.BoundsQueries),
        SdfBoundsCache.ProcessWork.Read(kind: SdfBoundsCache.Evaluations),
        SdfBoundsCache.ProcessWork.Read(kind: SdfBoundsCache.Instructions),
        SdfBoundsCache.ProcessWork.Read(kind: SdfBoundsCache.Rotations),
        SdfFieldEvaluator.PointWork.Read(kind: SdfFieldEvaluator.PointEvaluations),
        SdfFieldEvaluator.PointWork.Read(kind: SdfFieldEvaluator.PointInstructions),
    ];
    private static void PrintTable(List<(string Name, string Value)> rows) {
        var nameWidth = rows.Max(selector: row => row.Name.Length);

        foreach (var (name, value) in rows) {
            Console.WriteLine(value: $"{name.PadRight(totalWidth: nameWidth)}  {value}");
        }
    }

    /// <summary>Measures one world's server tick path and prints one row per number.</summary>
    /// <param name="world">The world's asset package folder, <c>.puck</c> source or <c>.world.json</c> document, or
    /// <see langword="null"/> for <see cref="DefaultWorldRelativePath"/> under the repository root.</param>
    /// <returns>0 when every row was measured, 1 when the world could not load or run, 2 when no repository root was
    /// found for the default world or a package names no entry.</returns>
    public static int Run(string? world) {
        string path;

        if (world is not null) {
            path = Path.GetFullPath(path: world);
        } else if (CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root)) {
            path = Path.Combine(
                path1: root,
                path2: DefaultWorldRelativePath
            );
        } else {
            return 2;
        }

        if (Puck.World.WorldPackageManifest.IsPackagePath(path: path)) {
            if (!Puck.World.WorldPackageManifest.TryResolveEntry(
                entryPath: out var entryPath,
                manifest: out _,
                path: path,
                reason: out var reason
            )) {
                Console.Error.WriteLine(value: $"ERROR: {reason}");

                return 2;
            }

            path = entryPath;
        }

        var rows = new List<(string Name, string Value)>();

        try {
            MeasureWorld(
                path: path,
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
