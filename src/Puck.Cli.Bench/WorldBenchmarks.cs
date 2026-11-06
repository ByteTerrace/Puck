using Puck.Cli.Determinism;
using System.Diagnostics;

using Puck.Abstractions;
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

    private static void MeasureWorld(string path, BenchRows rows) {
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
        using var addons = Puck.World.Addons.WorldAddonRuntime.Create(definition: bench.Server.Definition, server: bench.Server);

        rows.Add(item: ("world: server construction", $"{constructionTimer.Elapsed.TotalSeconds:F1} s"));
        rows.Metric(name: "server-construction", unit: "ms", value: constructionTimer.Elapsed.TotalMilliseconds);

        MeasureGallery(catalog: catalog, definition: definition!, rows: rows);

        var server = bench.Server;

        rows.Add(item: ("world: arena storage", $"{server.Arena.Bytes:N0} bytes"));
        if (!server.TryCaptureCheckpoint(Puck.World.Server.WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var checkpointReason)) {
            throw new InvalidOperationException(message: checkpointReason);
        }
        rows.Add(item: ("world: authority checkpoint", $"{Puck.World.Server.WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!).Length:N0} bytes (before guests and machines step)"));
        var stepTicks = FixedTickConversion.PerRate(ratePerSecond: ((uint)definition!.SimulationRateHz));

        for (var tick = 0; (tick < WarmupTicks); tick++) {
            server.Advance(stepTicks: stepTicks);
        }

        var allocationSamples = new long[SampleTicks];
        var elapsedTicksSamples = new long[SampleTicks];
        var countsBefore = ReadTickWork();
        long simulatedBodyVisits = 0, dynamicPairVisits = 0, eventOverlapTests = 0, regionBodyTests = 0;

        for (var tick = 0; (tick < SampleTicks); tick++) {
            var timestampBefore = Stopwatch.GetTimestamp();

            allocationSamples[tick] = AllocationWindow.Total(window: () => server.Advance(stepTicks: stepTicks));
            elapsedTicksSamples[tick] = (Stopwatch.GetTimestamp() - timestampBefore);
            simulatedBodyVisits += server.Population.SimulatedBodyVisits;
            dynamicPairVisits += server.Population.DynamicContactPairVisits;
            eventOverlapTests += server.Events.CollisionOverlapTests;
            regionBodyTests += server.Events.RegionBodyTests;
        }

        var countsAfter = ReadTickWork();

        Array.Sort(array: allocationSamples);
        Array.Sort(array: elapsedTicksSamples);

        var medianAllocation = allocationSamples[(allocationSamples.Length / 2)];
        var medianElapsedMs = ((elapsedTicksSamples[(elapsedTicksSamples.Length / 2)] * 1000.0) / Stopwatch.Frequency);

        rows.Add(item: ("world: idle tick (median)", $"{medianElapsedMs:F3} ms"));
        rows.Metric(name: "idle-tick", unit: "ms", value: medianElapsedMs);
        rows.Add(item: ("world: quiet-tick allocation (median)", $"{medianAllocation:N0} bytes"));
        rows.Metric(name: "quiet-tick-allocation", unit: "bytes", value: medianAllocation);
        rows.Add(item: ("world: sleeping rigid bodies", $"{server.Population.SleepingRigidBodies}"));
        rows.Add(item: ("world: simulated body visits / tick", $"{(simulatedBodyVisits / ((double)SampleTicks)):F1}"));
        rows.Add(item: ("world: dynamic broadphase pair visits / tick", $"{(dynamicPairVisits / ((double)SampleTicks)):F1}"));
        rows.Add(item: ("world: event overlap / region body tests / tick", $"{(eventOverlapTests / ((double)SampleTicks)):F1} / {(regionBodyTests / ((double)SampleTicks)):F1}"));

        // The body and contact work behind the tick, as deterministic counts: what a timing row is read beside.
        for (var kind = 0; (kind < TickWork.Length); kind++) {
            rows.Add(item: ($"world: {TickWork[kind].Kind.Name} per tick", $"{((countsAfter[kind] - countsBefore[kind]) / ((double)SampleTicks)):F1}"));
            rows.Metric(name: $"work.{TickWork[kind].Kind.Name}", unit: "per tick", value: ((countsAfter[kind] - countsBefore[kind]) / ((double)SampleTicks)));
        }

        MeasureValueWrite(
            rows: rows,
            server: server,
            stepTicks: stepTicks
        );
        MeasureCellBurst(rows: rows, server: server, stepTicks: stepTicks);
        MeasureResponseChange(rows: rows, server: server, stepTicks: stepTicks);
        MeasureSceneEmission(path: path, rows: rows, server: server, stepTicks: stepTicks);
        MeasureMachines(
            bench: bench,
            definition: definition,
            rows: rows,
            stepTicks: stepTicks
        );
    }
    // The live simulation workload, including the guests and the console's edit echoes. The other rows isolate
    // server operations; every benchmark host mounts the document's declared guests.
    private static void MeasureGallery(WorldDefinition definition, Puck.World.Machines.WorldMachineCatalog catalog, BenchRows rows) {
        using var bench = WorldBenchServer.Boot(catalog: catalog, definition: definition);
        var server = bench.Server;

        server.Population.ActivateSeat(profile: null, slot: 0);
        using var addons = Puck.World.Addons.WorldAddonRuntime.Create(server.Definition, server: server);
        var tape = new Puck.Hosting.ConsoleTape(store: new Puck.Hosting.ConsoleTapeStore());

        server.EchoTap = echo => tape.RecordEcho(message: echo.Message, refused: echo.Rejected);
        var step = FixedTickConversion.PerRate(ratePerSecond: ((uint)definition.SimulationRateHz));
        Action advance = () => server.Advance(stepTicks: step);
        var period = ((long)((((ulong)Stopwatch.Frequency) * step) / FixedTickConversion.TicksPerSecond));
        var due = Stopwatch.GetTimestamp();

        void Pace() {
            while (Stopwatch.GetTimestamp() < due) { Thread.Sleep(millisecondsTimeout: 1); }
            due += period;
        }
        for (var tick = 0; (tick < WarmupTicks); tick++) { Pace(); advance(); }
        var standing = new long[SampleTicks];

        for (var tick = 0; (tick < standing.Length); tick++) {
            Pace();
            standing[tick] = AllocationWindow.Total(window: advance);
        }
        Array.Sort(array: standing);
        rows.Add(item: ("world: addons standing tick allocation (median / mean)", $"{standing[(standing.Length / 2)]:N0} / {standing.Average():N0} bytes"));
        // player.move 1 0 is camera-relative in the live host. With the recipe's 90-degree heading and the
        // chase camera's zero orbit, right is world -Z: the wire carries forward=1 and faceZ=-1.
        var roles = server.Population.Channels.RoleOrdinals;
        var intent = default(Puck.World.Protocol.PlayerIntent).WithChannel(ordinal: roles.MoveAdvance, value: FixedQ4816.One).WithChannel(ordinal: roles.FaceZ, value: -FixedQ4816.One);

        for (var walk = 0; (walk < 3); walk++) {
            var body = server.Body(index: 0)!;

            server.ApplyCommand(new Puck.World.Protocol.WorldCommand.SnapPose(Puck.Commands.Principal.Console, 0,
                new System.Numerics.Vector3(x: 30, y: body.FixedPosition.ToVector3().Y, z: 22), (MathF.PI / 2), 0, 0, Puck.World.Protocol.SnapPoseMode.Pose));
            var samples = new long[60];

            for (var tick = 0; (tick < samples.Length); tick++) {
                Pace();
                if (!server.Input.TryPublishSnapshot(Puck.Commands.Principal.Console, 0, "bench-walk", intent, server.NextInputTick,
                    out var inputOwner, out var input, out var reason)) { throw new InvalidOperationException(message: reason); }
                // A queued publication owns its arrays; local snapshot producers otherwise return borrowed scratch.
                input = input with { Contributions = input.Contributions.ToArray(), Events = input.Events.ToArray() };
                server.EnqueueIntent(submission: new Puck.World.Protocol.IntentSubmission(server.NextInputTick, 0, intent,
                    Puck.Commands.Principal.Console, input, InputOwner: inputOwner));
                samples[tick] = AllocationWindow.Total(window: advance);
            }
            Array.Sort(array: samples);
            rows.Add(item: ($"world: addons gallery walk {(walk + 1)} tick allocation (median / mean)", $"{samples[(samples.Length / 2)]:N0} / {samples.Average():N0} bytes"));
        }
        rows.Add(item: ("world: measured addons", string.Join(separator: ", ", values: addons.Receipts.Select(selector: receipt => receipt.Name))));
    }
    private static void MeasureCellBurst(Puck.World.Server.WorldServer server, ulong stepTicks, BenchRows rows) {
        // The same typed write door the scope uses, including live edit authority and budgets.
        // Resolve addresses before the measured window; each tick writes seven summary cells and six histories.
        var scope = WorldDefinitionRows.FindStateRow(server.Definition.State, "scope");

        if (scope is null) { rows.Add(item: ("world: 13-cell guest burst", "no scope rows")); return; }
        var principal = Puck.Commands.Principal.Addon(name: "kinematics-scope");
        var addresses = new List<(Puck.State.StateHandle Row, Puck.State.CellKey Key)>();

        foreach (var cell in scope.Cells!) {
            server.Arena.Catalog.TryResolve(handle: out var row, lane: Puck.State.StateLane.Document, name: "scope");
            server.Arena.Keys.TryResolve(cell.Key, out var key);
            addresses.Add(item: (row, key));
        }
        foreach (var name in new[] { "scopeHeight", "scopeVelocity", "scopeFacts", "scopeContacts", "scopeTicks", "scopeEvents" }) {
            server.Arena.Catalog.TryResolve(handle: out var row, lane: Puck.State.StateLane.Document, name: name);
            server.Arena.Keys.TryResolve(Puck.State.CellName.Parse(candidate: "0"), out var key);
            addresses.Add(item: (row, key));
        }
        var samples = new long[SampleTicks];
        var allocations = new long[SampleTicks];
        var current = 0;
        Action advance = () => {
            for (var i = 0; (i < addresses.Count); i++) {
                var address = addresses[i];

                if (server.WriteStateCell(principal, address.Row, address.Key, current) != Puck.World.Server.WorldCellWriteVerdict.Applied) {
                    throw new InvalidOperationException(message: $"guest burst write {i} refused");
                }
            }
            server.Advance(stepTicks: stepTicks);
        };
        var period = ((long)((((ulong)Stopwatch.Frequency) * stepTicks) / FixedTickConversion.TicksPerSecond));
        var due = Stopwatch.GetTimestamp();

        for (var tick = 0; (tick < (WarmupTicks + SampleTicks)); tick++) {
            while (Stopwatch.GetTimestamp() < due) { Thread.Sleep(millisecondsTimeout: 1); }
            due += period;
            current = tick;
            var start = Stopwatch.GetTimestamp();
            var bytes = AllocationWindow.Total(window: advance);

            if (tick >= WarmupTicks) {
                samples[(tick - WarmupTicks)] = (Stopwatch.GetTimestamp() - start);
                allocations[(tick - WarmupTicks)] = bytes;
            }
        }
        Array.Sort(array: allocations);
        rows.Add(item: ("world: 13-cell guest burst tick (median)", $"{MedianMilliseconds(samples: samples):F3} ms"));
        rows.Metric(name: "cell-burst-tick", unit: "ms", value: MedianMilliseconds(samples: samples));
        rows.Add(item: ("world: 13-cell guest burst tick allocation (median)", $"{allocations[(SampleTicks / 2)]:N0} bytes"));
    }
    // gallery.lights.all: every hall light cell written in one tick, which moves the hall's responding rows (floors,
    // roof, switches) to their lit or dark creations in that tick. The tick is timed and metered beside the population
    // revisions it moved: a response change moves no body and leaves the revision, so a client composes its program
    // once, on the values the change delivers.
    private static void MeasureResponseChange(Puck.World.Server.WorldServer server, ulong stepTicks, BenchRows rows) {
        if (WorldDefinitionRows.FindStateRow(server.Definition.State, "hallLights") is not { Cells: { Count: > 0 } cells }) {
            rows.Add(item: ("world: response change", "no hallLights row"));

            return;
        }

        const int Changes = 24;
        var samples = new long[Changes];
        var allocations = new long[Changes];
        var revisions = new long[Changes];

        // Paced like the game, so a screen machine's full window does not stand in for the swap's own cost.
        var period = ((long)((((ulong)Stopwatch.Frequency) * stepTicks) / FixedTickConversion.TicksPerSecond));
        var due = Stopwatch.GetTimestamp();

        void Advance() {
            while (Stopwatch.GetTimestamp() < due) {
                Thread.Sleep(millisecondsTimeout: 1);
            }

            due += period;
            server.Advance(stepTicks: stepTicks);
        }
        for (var change = 0; (change < (Changes + 4)); change++) {
            var value = (change % 2);

            foreach (var cell in cells) {
                server.EnqueueMutation(mutation: new Puck.World.Protocol.WorldMutation.UpsertStateCell(Puck.Commands.Principal.World, "hallLights", cell.Key.Value, value, Puck.World.Protocol.WorldDocumentWriteKind.Set));
            }

            var before = server.Definition;
            var revision = server.Population.Revision;

            while (Stopwatch.GetTimestamp() < due) {
                Thread.Sleep(millisecondsTimeout: 1);
            }

            due += period;
            var start = Stopwatch.GetTimestamp();
            var bytes = AllocationWindow.Total(window: () => server.Advance(stepTicks: stepTicks));
            var elapsed = (Stopwatch.GetTimestamp() - start);

            if (WorldPlacementResponse.CompareHoldings(after: server.Definition, before: before) == WorldHoldingChange.None) { throw new InvalidOperationException(message: "a hall light change moved no responding row"); }
            for (var tick = 0; (tick < 4); tick++) {
                Advance();
            }
            if (change >= 4) {
                samples[(change - 4)] = elapsed;
                allocations[(change - 4)] = bytes;
                revisions[(change - 4)] = (server.Population.Revision - revision);
            }
        }
        Array.Sort(array: allocations);
        Array.Sort(array: revisions);
        rows.Add(item: ("world: response change tick (median, gallery.lights.all)", $"{MedianMilliseconds(samples: samples):F3} ms"));
        rows.Metric(name: "response-change-tick", unit: "ms", value: MedianMilliseconds(samples: samples));
        rows.Add(item: ("world: response change tick allocation (median)", $"{allocations[(Changes / 2)]:N0} bytes"));
        rows.Add(item: ("world: response change population revisions moved (median)", $"{revisions[(Changes / 2)]}"));
    }
    // The program a client composes on the frame thread (WorldSceneEmitter.Emit, the work SdfCompositionFrameSource
    // hands its background build): composed again with nothing changed, and after each hall light change delivers the
    // rows holding their new masks.
    private static void MeasureSceneEmission(string path, Puck.World.Server.WorldServer server, ulong stepTicks, BenchRows rows) {
        if (WorldDefinitionRows.FindStateRow(server.Definition.State, "hallLights") is not { Cells: { Count: > 0 } cells }) {
            rows.Add(item: ("world: scene emission", "no hallLights row"));

            return;
        }

        var inert = new Puck.Cli.Mesh.RestScene.Inert();
        var routes = new Puck.World.Client.WorldSeatAuthorityRouter();
        var client = new Puck.World.Client.WorldClient(
            composition: new Puck.World.Client.WorldCompositionState(),
            definition: server.Definition,
            roster: new Puck.World.Client.PlayerRoster(definition: server.Definition, link: inert, seatBindings: new Puck.World.WorldSeatBindings(definition: server.Definition)),
            seatRouter: routes
        );
        var emitter = new Puck.World.Client.WorldSceneEmitter(
            animator: new Puck.World.Client.WorldCreationInstances(),
            audio: inert,
            client: client,
            continuum: new Puck.World.Client.WorldContinuum(adjacencies: inert, client: client, routes: routes),
            text: new Puck.World.Client.WorldTextCatalog(source: new WorldDefinitionSource(Definition: server.Definition, SourcePath: path))
        );
        var context = new Puck.SdfVm.SdfEmitContext(Probe: false, Time: 0f, RenderOrigin: System.Numerics.Vector3.Zero, ParkPosition: System.Numerics.Vector3.Zero, SlotBase: 0, InterpolationAlpha: 1f);
        const int Samples = 16;
        var steady = new long[Samples];
        var steadyBytes = new long[Samples];
        var changed = new long[Samples];
        var changedBytes = new long[Samples];

        // Each builder is sized from the one before it, as the frame source sizes a live rebuild's from the program
        // it replaces.
        var (instructions, instances, materials, polygons) = (0, 0, 0, 0);

        void Emit(long[] times, long[] bytes, int sample) {
            var builder = new Puck.SignedDistance.SdfProgramBuilder(convexPolygonCapacity: polygons, instanceCapacity: instances, instructionCapacity: instructions, materialCapacity: materials);
            var start = Stopwatch.GetTimestamp();

            bytes[sample] = AllocationWindow.Total(window: () => emitter.Emit(builder: builder, context: in context));
            times[sample] = (Stopwatch.GetTimestamp() - start);
            (instructions, instances, materials, polygons) = (builder.InstructionCount, builder.InstanceCount, builder.MaterialCount, builder.ConvexPolygonCount);
        }

        emitter.Tick(deltaSeconds: 0f);
        for (var warm = 0; (warm < 4); warm++) {
            Emit(bytes: steadyBytes, sample: 0, times: steady);
        }
        for (var sample = 0; (sample < Samples); sample++) {
            Emit(bytes: steadyBytes, sample: sample, times: steady);
        }
        for (var change = 0; (change < (Samples + 4)); change++) {
            foreach (var cell in cells) {
                server.EnqueueMutation(mutation: new Puck.World.Protocol.WorldMutation.UpsertStateCell(Puck.Commands.Principal.World, "hallLights", cell.Key.Value, (change % 2), Puck.World.Protocol.WorldDocumentWriteKind.Set));
            }

            server.Advance(stepTicks: stepTicks);
            client.DeliverState(definition: server.Definition, stamp: new Puck.World.Protocol.WorldStateStamp(Tick: 0UL, EngineTick: 0UL, MovedRows: default, Everything: false), version: server.DocumentVersion);
            Emit(times: changed, bytes: changedBytes, sample: Math.Max(val1: 0, val2: (change - 4)));
        }
        Array.Sort(array: steadyBytes);
        Array.Sort(array: changedBytes);
        rows.Add(item: ("world: scene emission, nothing changed (median)", $"{MedianMilliseconds(samples: steady):F3} ms, {steadyBytes[(Samples / 2)]:N0} bytes"));
        rows.Add(item: ("world: scene emission after a hall light change (median)", $"{MedianMilliseconds(samples: changed):F3} ms, {changedBytes[(Samples / 2)]:N0} bytes"));
        rows.Metric(name: "scene-emission-steady", unit: "ms", value: MedianMilliseconds(samples: steady));
        rows.Metric(name: "scene-emission-changed", unit: "ms", value: MedianMilliseconds(samples: changed));
    }
    // An authored whole-row edit that moves one cell's value: the tick that drains it, timed and metered beside
    // the arena versions and installed rows it replaces. Bound addon writes use the separate typed burst above.
    private static void MeasureValueWrite(Puck.World.Server.WorldServer server, ulong stepTicks, BenchRows rows) {
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

            var installed = server.Configuration.State;
            var current = WorldDefinitionRows.FindStateRow(rows: server.ExportDefinition().State, name: row.Name.Value)!;
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
            var after = server.Configuration.State;

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
        rows.Metric(name: "value-write-tick", unit: "ms", value: MedianMilliseconds(samples: elapsedTicksSamples));
        rows.Add(item: ("world: state value write tick allocation (median)", $"{allocationSamples[(allocationSamples.Length / 2)]:N0} bytes"));
        rows.Add(item: ("world: state value write tick rows moved (median)", $"{versionsMoved[(versionsMoved.Length / 2)]} arena versions, {rowsReplaced[(rowsReplaced.Length / 2)]} installed rows of {server.Definition.State.Count}"));
    }
    // The screen machines emulate on worker threads the tick only submits to; a tick waits on one only when that
    // machine's pending window is full. The back-to-back window above is bounded by the slowest machine once the
    // windows fill, so the tick is also timed paced at the simulation rate, as the game runs it, beside the count of
    // submissions that waited. Each machine's own tick of emulation is then timed alone, synchronously.
    private static void MeasureMachines(WorldBenchServer bench, WorldDefinition definition, ulong stepTicks, BenchRows rows) {
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
        rows.Metric(name: "paced-tick", unit: "ms", value: MedianMilliseconds(samples: pacedSamples));

        foreach (var (screen, runtime) in machines) {
            var samples = new long[MachineSampleTicks];

            for (var tick = 0; (tick < MachineSampleTicks); tick++) {
                var timestampBefore = Stopwatch.GetTimestamp();

                _ = runtime.Advance(deltaTicks: stepTicks);
                samples[tick] = (Stopwatch.GetTimestamp() - timestampBefore);
            }

            rows.Add(item: ($"world: screen {screen} machine ({bench.Machines.State(index: screen)?.Engine}), one tick alone (median)", $"{MedianMilliseconds(samples: samples):F3} ms ({MachineSampleTicks} synchronous ticks)"));
            rows.Metric(name: $"machine-{screen}-tick", unit: "ms", value: MedianMilliseconds(samples: samples));
        }
    }
    private static double MedianMilliseconds(long[] samples) {
        Array.Sort(array: samples);

        return ((samples[(samples.Length / 2)] * 1000.0) / Stopwatch.Frequency);
    }

    // The deterministic work behind a tick, in report order: the bodies the population steps and the parked ones a field
    // change woke or left asleep, the analytic solver's work, the certified sweep's work and the immutable field's bounds
    // work it spends.
    // Kinds belong to the measured source, not to the CLI's build. A frozen build can declare a different set;
    // enumerating it also includes newly added counters without maintaining a second list beside the sources.
    private static readonly (IWorkCounterSource Source, WorkKind Kind)[] TickWork = new IWorkCounterSource[] {
        Puck.World.Server.WorldBodyWork.Process,
        FixedStaticContactWork.Process,
        FixedContactSweepWork.Process,
        SdfBoundsCache.ProcessWork,
        SdfFieldEvaluator.PointWork,
    }.SelectMany(source => source.WorkKinds.ToArray().Where(kind => kind.Class == WorkClass.Deterministic).Select(kind => (Source: source, Kind: kind))).ToArray();

    private static long[] ReadTickWork() => Array.ConvertAll(array: TickWork, converter: static entry =>
        (entry.Source.TryRead(kind: entry.Kind, value: out var value) ? value : throw new InvalidOperationException(message: $"{entry.Source.Name} does not count {entry.Kind.Name}.")));
    private static void PrintTable(IReadOnlyList<KeyValuePair<string, string>> rows, TextWriter writer) {
        if (rows.Count == 0) { return; }
        var nameWidth = rows.Max(selector: row => row.Key.Length);

        foreach (var (name, value) in rows) {
            writer.WriteLine(value: $"{name.PadRight(totalWidth: nameWidth)}  {value}");
        }
    }

    /// <summary>Measures one world <paramref name="repeats"/> times with the engine assemblies of this assembly's load
    /// context, and returns the last finished repeat's rows and every metric's samples as JSON text, which crosses a load
    /// context unchanged: <c>{"rows":[[name,text]...],"metrics":{name:{"unit":u,"samples":[...]}},"repeats":n,"error":e}</c>.
    /// A repeat that fails stops the run; its partial readings are discarded and its message is the error.</summary>
    /// <param name="path">The world document's full path.</param>
    /// <param name="repeats">How many times to measure it, at least one.</param>
    /// <returns>The JSON payload.</returns>
    public static string MeasureRepeats(string path, int repeats) {
        var samples = new SortedDictionary<string, (string Unit, List<double> Values)>(comparer: StringComparer.Ordinal);
        BenchRows? last = null;
        string? error = null;
        var finished = 0;

        for (; (finished < repeats) && (error is null); finished++) {
            var rows = new BenchRows();

            try {
                MeasureWorld(path: path, rows: rows);
            } catch (Exception exception) {
                error = exception.GetBaseException().Message;
                break;
            }
            last = rows;
            foreach (var (name, (unit, value)) in rows.Metrics) {
                if (!samples.TryGetValue(key: name, value: out var series)) { samples[name] = series = (unit, []); }
                series.Values.Add(item: value);
            }
        }

        var metrics = new System.Text.Json.Nodes.JsonObject();

        foreach (var (name, (unit, values)) in samples) {
            metrics[name] = new System.Text.Json.Nodes.JsonObject { ["unit"] = unit, ["samples"] = new System.Text.Json.Nodes.JsonArray(items: [.. values.Select(selector: static value => (System.Text.Json.Nodes.JsonNode?)value)]) };
        }
        return new System.Text.Json.Nodes.JsonObject {
            // Where the measured server actually loaded from, read inside the context that ran it.
            ["engine"] = PuckPaths.Normalize(path: typeof(Puck.World.Server.WorldServer).Assembly.Location),
            ["error"] = error,
            ["metrics"] = metrics,
            ["repeats"] = finished,
            ["rows"] = new System.Text.Json.Nodes.JsonArray(items: [.. (last?.Table ?? []).Select(selector: static row => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonArray(row.Name, row.Value))]),
        }.ToJsonString();
    }

    /// <summary>Measures one world's server tick path, prints one row per number, and records the run as a
    /// <see cref="BenchReport"/>.</summary>
    /// <param name="world">The world's asset package folder, <c>.puck</c> source or <c>.world.json</c> document, or
    /// <see langword="null"/> for <see cref="DefaultWorldRelativePath"/> under the repository root.</param>
    /// <param name="repeats">How many times the whole lane runs; every metric is summarized over them.</param>
    /// <param name="artifact">A World build's <c>Puck.World.dll</c> whose engine assemblies are measured in place of
    /// this CLI's own, or <see langword="null"/> to measure the CLI's.</param>
    /// <param name="output">The parent of a unique run directory that receives <c>report.json</c>, or
    /// <see langword="null"/> to write none.</param>
    /// <param name="json">Whether the report goes to standard output, with the table on standard error.</param>
    /// <param name="sampleHost">The host sampler, or <see langword="null"/> to record no host load.</param>
    /// <returns>0 when every repeat measured, 1 when the world could not load or run, 2 for invalid arguments, no
    /// repository root for the default world, or a package that names no entry.</returns>
    public static int Run(string? world, int repeats, string? artifact, string? output, bool json, BenchHostSample? sampleHost) {
        if (repeats is < 1 or > 100) {
            return CliExit.Refuse(verb: "bench world", what: "--repeats", why: "must be 1 to 100.");
        }
        if ((artifact is not null) && !File.Exists(path: artifact)) {
            return CliExit.Refuse(verb: "bench world", what: artifact, why: "names no World build (a Puck.World.dll).");
        }

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

        var build = BenchBuild.Of(entryAssembly: (artifact ?? CliPaths.Tool));
        var start = BenchReport.Start(build: build, repeats: repeats, settings: new Dictionary<string, string> {
            ["world"] = CliPaths.ToDisplay(fullPath: path),
        }, verb: "bench world");
        string payload;
        BenchHostLoad? load;

        using (var host = new BenchHostRecorder(sample: sampleHost)) {
            // A build's engine runs in its own load context, where no admission is installed: admit the run once here.
            if (artifact is not null) {
                WorldAdmission.WaitInProcess(step: "bench world");
            }
            payload = ((artifact is null) ? MeasureRepeats(path: path, repeats: repeats) : BenchArtifactContext.MeasureRepeats(artifact: artifact, path: path, repeats: repeats));
            load = host.Finish();
        }

        var answer = System.Text.Json.Nodes.JsonNode.Parse(json: payload)!;
        var error = answer["error"]?.GetValue<string>();
        var rows = answer["rows"]!.AsArray().Select(selector: static row => new KeyValuePair<string, string>(row![0]!.GetValue<string>(), row[1]!.GetValue<string>())).ToArray();
        var metrics = new SortedDictionary<string, BenchMetric>(comparer: StringComparer.Ordinal);

        foreach (var (name, metric) in answer["metrics"]!.AsObject()) {
            metrics[name] = BenchMetric.Of(samples: [.. metric!["samples"]!.AsArray().Select(selector: static sample => sample!.GetValue<double>())], unit: metric["unit"]!.GetValue<string>());
        }

        var report = start with {
            Complete = ((error is null) && (answer["repeats"]!.GetValue<int>() == repeats)),
            HostLoad = load,
            Metrics = metrics,
            Rows = rows,
            Settings = new Dictionary<string, string>(collection: start.Settings) { ["engine"] = answer["engine"]!.GetValue<string>() },
        };

        if (output is not null) {
            var run = Path.Combine(path1: Path.GetFullPath(path: output), path2: Guid.NewGuid().ToString(format: "N"));

            Directory.CreateDirectory(path: run);
            Console.Error.WriteLine(value: $"bench world: report {PuckPaths.Normalize(path: report.Write(directory: run))}");
        }

        var table = (json ? Console.Error : Console.Out);

        PrintTable(rows: rows, writer: table);
        if (repeats > 1) {
            foreach (var line in report.MetricLines()) { table.WriteLine(value: line); }
        }
        if (json) { Console.Out.WriteLine(value: report.ToJson()); }
        if (error is not null) {
            Console.Error.WriteLine(value: $"ERROR: {error}");

            return 1;
        }

        return 0;
    }
}
/// <summary>The rows <c>bench world</c> prints, and beside them the numeric metrics its report summarizes.</summary>
internal sealed class BenchRows {
    /// <summary>Gets the printed rows in order.</summary>
    public List<(string Name, string Value)> Table { get; } = [];
    /// <summary>Gets each metric's unit and value for this repeat.</summary>
    public Dictionary<string, (string Unit, double Value)> Metrics { get; } = new(comparer: StringComparer.Ordinal);

    /// <summary>Adds a printed row.</summary>
    /// <param name="item">The row's name and text.</param>
    public void Add((string Name, string Value) item) => Table.Add(item: item);
    /// <summary>Records a metric for this repeat.</summary>
    /// <param name="name">The metric's name in the report.</param>
    /// <param name="unit">Its unit.</param>
    /// <param name="value">Its value.</param>
    public void Metric(string name, string unit, double value) => Metrics[name] = (unit, value);
}
