using Puck.Commands;
using Puck.Maths;
using Puck.Scripting;

namespace Puck.Post;

/// <summary>
/// Tier-A stage. Proves the WASM addon host's engine contract — the <c>puck.addon.v1</c> ABI — is deterministic and
/// self-guarding, entirely on the CPU (Wasmtime is one runtime regardless of GPU backend, so this is a same-process,
/// run-twice proof, never cross-backend). Over nine inline WAT fixtures compiled through <see cref="ScriptingFixtures"/>
/// it asserts: the command stream and per-tick fuel trace are bit-identical run-to-run (determinism); a position
/// marshals in and a derived value marshals out with the exact expected clamp (walker); a script's output actually
/// reaches the trace (echo differs from silent); a runaway loop halts <see cref="AddonFaultKind.OutOfFuel"/> at the
/// identical derived point on both runs; fuel accounting straddles an exact, calibrated budget; the four malformed
/// modules each fault their guard deterministically; and the pinned Wasmtime major matches the fuel-codegen contract.
/// </summary>
internal sealed class ScriptingDeterminismStage : IPostStage {
    // The pinned Wasmtime major the fuel-exhaustion codegen is locked to. Fuel is charged at basic-block granularity
    // (upstream #4109), so a major bump can shift where a runaway halts and break stored replays — the gate fails on
    // drift rather than let a silent restore change fuel timing.
    private const int PinnedMajor = 44;
    private const int Ticks = 600;

    // The exact fuel budget at which the fuel-boundary loop first completes under the pinned engine — a CALIBRATED
    // constant, not a portable count (fuel is charged in basic-block lumps per #4109, so this number is only
    // meaningful for Wasmtime 44.x; note it is BELOW the fuel consumed at a large budget, since the trap fires when a
    // block's lump would overrun, not after the last unit). The leg re-derives this exact boundary by a deterministic
    // budget search each run and fails with the correct number if it ever drifts, so the constant can never rot
    // silently. The proof is equality across the two runs and across the B / B−1 boundary, never this absolute value.
    private const long FuelBoundaryBudget = 5998L;

    /// <inheritdoc/>
    public string Name => "scripting-determinism";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        ScriptingEngine engine;

        try {
            engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        } catch (Exception error) when (IsRuntimeUnavailable(error: error)) {
            return PostStageOutcome.Skip(detail: $"the Wasmtime native runtime is unavailable on this platform: {error.Message}");
        }

        using (engine) {
            ScriptingFixtureModules fixtures;

            try {
                fixtures = ScriptingFixtures.Compile(engine: engine);
            } catch (Exception error) when (IsRuntimeUnavailable(error: error)) {
                return PostStageOutcome.Skip(detail: $"the Wasmtime native runtime is unavailable on this platform: {error.Message}");
            }

            var failure =
                (LegDeterminism(engine: engine, fixtures: fixtures)
                ?? (LegWalkerValues(engine: engine, info: fixtures.Walker)
                ?? (LegBaselineDiff(engine: engine, fixtures: fixtures)
                ?? (LegRunaway(engine: engine, info: fixtures.Runaway)
                ?? (LegFuelBoundary(engine: engine, info: fixtures.FuelBoundary)
                ?? (LegMalformed(engine: engine, fixtures: fixtures)
                ?? LegVersionPin()))))));

            if (failure is not null) {
                return PostStageOutcome.Fail(detail: failure);
            }

            var echo = RunFixture(engine: engine, info: fixtures.Echo, budget: AddonAbi.DefaultFuelPerTick);
            var finalHash = echo.CommandHashes[^1];
            var artifact = WriteSmoke(context: context, finalHash: finalHash);

            return PostStageOutcome.Pass(
                artifactPath: artifact,
                detail: $"9 fixtures, {Ticks} ticks, final cmd-hash 0x{finalHash:X16}, fuel-stable, wasmtime {PinnedMajor}"
            );
        }
    }

    // The per-tick snapshot generator: a deterministic local position circling on integer ramps (no RNG, no wall
    // clock). Z sweeps through the walker's clamp band — passing the target near the midpoint — so the walker fixture
    // exercises all three clamp branches; X and Y are sawtooths the position-blind fixtures ignore.
    private static AddonSnapshot BuildSnapshot(ulong tick) {
        var index = (long)tick;

        return new AddonSnapshot(
            Buttons: 0u,
            PosLocalX: (((index * 997L) % 131072L) - 65536L),
            PosLocalY: (((index * 613L) % 131072L) - 65536L),
            PosLocalZ: (ScriptingFixtures.WalkerTargetZ + ((index - 300L) * 1500L)),
            Tick: tick
        );
    }
    private static long Clamp(long value, long min, long max) {
        return ((value < min) ? min : ((value > max) ? max : value));
    }

    // Folds one tick's decoded records into a stable digest, LSB-first over each record's padId/phase/valueX/valueY.
    // The record count leads so an empty tick and a one-record tick can never collide.
    private static ulong CommandHash(ReadOnlySpan<AddonCommand> commands) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: (uint)commands.Length);

        for (var index = 0; (index < commands.Length); ++index) {
            var command = commands[index];

            hash.Add(value: (uint)command.PadId);
            hash.Add(value: (uint)command.Phase);
            hash.Add(value: command.ValueX);
            hash.Add(value: command.ValueY);
        }

        return hash.Value;
    }
    private static AddonDescriptor Descriptor(string name, long budget) {
        return new AddonDescriptor(
            Enabled: true,
            FuelPerTick: budget,
            ModuleHash: null,
            ModulePath: name,
            Name: name,
            Slot: null
        );
    }

    // Constructs an addon expected to fault at LOAD (before any tick) and asserts the kind, then that a faulted addon
    // is never ticked.
    private static string? ExpectLoadFault(ScriptingEngine engine, ScriptingModuleInfo info, AddonFaultKind expected, string label) {
        using var instance = new AddonInstance(descriptor: Descriptor(name: label, budget: AddonAbi.DefaultFuelPerTick), engine: engine, moduleInfo: info);

        if (instance.State != AddonState.Faulted) {
            return $"{label}: expected a {expected} load fault, but the addon loaded {instance.State}";
        }

        if (instance.Fault.Kind != expected) {
            return $"{label}: expected {expected}, got {instance.Fault.Kind}";
        }

        var snapshot = BuildSnapshot(tick: 0UL);

        if (instance.Tick(snapshot: in snapshot).Status != AddonTickStatus.Faulted) {
            return $"{label}: a load-faulted addon was ticked";
        }

        return null;
    }

    // Constructs an addon that loads healthy but faults on its first tick, and asserts the tick-time guard kind.
    private static string? ExpectTickFault(ScriptingEngine engine, ScriptingModuleInfo info, AddonFaultKind expected, string label) {
        using var instance = new AddonInstance(descriptor: Descriptor(name: label, budget: AddonAbi.DefaultFuelPerTick), engine: engine, moduleInfo: info);

        if (instance.State != AddonState.Enabled) {
            return $"{label}: expected a healthy load, got {instance.State} ({instance.Fault.Kind})";
        }

        var snapshot = BuildSnapshot(tick: 0UL);
        var result = instance.Tick(snapshot: in snapshot);

        if (result.Status != AddonTickStatus.Faulted) {
            return $"{label}: expected a {expected} tick fault, got {result.Status}";
        }

        if (result.Fault.Kind != expected) {
            return $"{label}: expected {expected}, got {result.Fault.Kind}";
        }

        return null;
    }
    private static bool IsRuntimeUnavailable(Exception error) {
        return ((error is DllNotFoundException or BadImageFormatException)
            || (error is TypeInitializationException { InnerException: DllNotFoundException or BadImageFormatException }));
    }
    private static string? LegBaselineDiff(ScriptingEngine engine, ScriptingFixtureModules fixtures) {
        var echo = RunFixture(engine: engine, info: fixtures.Echo, budget: AddonAbi.DefaultFuelPerTick);
        var silent = RunFixture(engine: engine, info: fixtures.Silent, budget: AddonAbi.DefaultFuelPerTick);

        if (HashTrace.FirstDivergence(left: echo.CommandHashes, right: silent.CommandHashes) < 0) {
            return "ineffective: the echo and silent command traces are identical — the guest's output never reached the trace";
        }

        return null;
    }
    private static string? LegDeterminism(ScriptingEngine engine, ScriptingFixtureModules fixtures) {
        return (CompareRuns(engine: engine, info: fixtures.Echo, label: "echo")
            ?? CompareRuns(engine: engine, info: fixtures.Walker, label: "walker"));
    }
    private static string? CompareRuns(ScriptingEngine engine, ScriptingModuleInfo info, string label) {
        var first = RunFixture(engine: engine, info: info, budget: AddonAbi.DefaultFuelPerTick);
        var second = RunFixture(engine: engine, info: info, budget: AddonAbi.DefaultFuelPerTick);

        if (first.FirstFaultTick >= 0) {
            return $"{label} unexpectedly faulted at tick {first.FirstFaultTick} ({first.FirstFaultKind})";
        }

        var divergence = HashTrace.FirstDivergence(left: first.CommandHashes, right: second.CommandHashes);

        if (divergence >= 0) {
            return $"non-deterministic: {label} produced a different command stream at tick {divergence}";
        }

        if (!first.FuelConsumed.AsSpan().SequenceEqual(other: second.FuelConsumed)) {
            return $"non-deterministic: {label} consumed different fuel between two runs";
        }

        return null;
    }

    // Calibrated fuel accounting. A deterministic budget search finds B, the exact budget at which the fixed loop
    // first completes; at B it completes on both runs with identical consumption, and at B−1 it runs out of fuel on
    // both runs — equality across the two runs and across the boundary, never a portable absolute count.
    private static string? LegFuelBoundary(ScriptingEngine engine, ScriptingModuleInfo info) {
        var probe = RunOnce(engine: engine, info: info, budget: AddonAbi.DefaultFuelPerTick);

        if (probe.Status != AddonTickStatus.Ok) {
            return $"fuel-boundary: the probe run did not complete ({probe.Status} {probe.Fault.Kind})";
        }

        // The smallest budget in (0, consumed] at which the loop completes — the true Ok/OutOfFuel boundary.
        var boundary = FindOkBoundary(engine: engine, high: (long)probe.FuelConsumed, info: info);

        if (boundary != FuelBoundaryBudget) {
            return $"fuel-boundary calibration drift: the loop's Ok/OutOfFuel boundary is {boundary}, but FuelBoundaryBudget is {FuelBoundaryBudget}; recalibrate the constant to {boundary}";
        }

        var okFirst = RunOnce(engine: engine, info: info, budget: FuelBoundaryBudget);
        var okSecond = RunOnce(engine: engine, info: info, budget: FuelBoundaryBudget);

        if ((okFirst.Status != AddonTickStatus.Ok) || (okSecond.Status != AddonTickStatus.Ok)) {
            return $"fuel-boundary: at budget {FuelBoundaryBudget} the loop did not complete ({okFirst.Status} / {okSecond.Status})";
        }

        if (okFirst.FuelConsumed != okSecond.FuelConsumed) {
            return $"fuel-boundary: consumption at budget {FuelBoundaryBudget} differed between runs ({okFirst.FuelConsumed} vs {okSecond.FuelConsumed})";
        }

        var lowFirst = RunOnce(engine: engine, info: info, budget: (FuelBoundaryBudget - 1L));
        var lowSecond = RunOnce(engine: engine, info: info, budget: (FuelBoundaryBudget - 1L));

        if ((lowFirst.Fault.Kind != AddonFaultKind.OutOfFuel) || (lowSecond.Fault.Kind != AddonFaultKind.OutOfFuel)) {
            return $"fuel-boundary: at budget {(FuelBoundaryBudget - 1L)} expected OutOfFuel on both runs, got {lowFirst.Fault.Kind} / {lowSecond.Fault.Kind}";
        }

        if (lowFirst.FuelConsumed != lowSecond.FuelConsumed) {
            return $"fuel-boundary: consumption at budget {(FuelBoundaryBudget - 1L)} differed between runs ({lowFirst.FuelConsumed} vs {lowSecond.FuelConsumed})";
        }

        return null;
    }

    // Binary-searches the smallest budget at which the fixture completes (Ok). `high` is a known-Ok budget (the fuel
    // consumed at a large budget); budget 0 always traps, so the search brackets a monotonic Ok/OutOfFuel boundary.
    private static long FindOkBoundary(ScriptingEngine engine, ScriptingModuleInfo info, long high) {
        var low = 0L;
        var top = high;

        while ((top - low) > 1L) {
            var mid = (low + ((top - low) / 2L));

            if (RunOnce(engine: engine, info: info, budget: mid).Status == AddonTickStatus.Ok) {
                top = mid;
            } else {
                low = mid;
            }
        }

        return top;
    }
    private static string? LegMalformed(ScriptingEngine engine, ScriptingFixtureModules fixtures) {
        return (ExpectLoadFault(engine: engine, expected: AddonFaultKind.BadExport, info: fixtures.BadExport, label: "bad-export")
            ?? (ExpectLoadFault(engine: engine, expected: AddonFaultKind.AbiMismatch, info: fixtures.AbiMismatch, label: "abi-mismatch")
            ?? (ExpectTickFault(engine: engine, expected: AddonFaultKind.DecodeError, info: fixtures.BadDecode, label: "bad-decode")
            ?? ExpectTickFault(engine: engine, expected: AddonFaultKind.DecodeError, info: fixtures.BadReserved, label: "bad-reserved"))));
    }

    // Drives the runaway loop twice: it must trap OutOfFuel on the first tick, consume its whole budget, then
    // short-circuit every subsequent tick (no hang) — identically on both runs.
    private static string? LegRunaway(ScriptingEngine engine, ScriptingModuleInfo info) {
        const long Budget = AddonAbi.DefaultFuelPerTick;

        var first = RunFixture(engine: engine, info: info, budget: Budget);
        var second = RunFixture(engine: engine, info: info, budget: Budget);

        if ((first.FirstFaultKind != AddonFaultKind.OutOfFuel) || (first.FirstFaultTick != 0)) {
            return $"runaway did not halt OutOfFuel on the first tick: {first.FirstFaultKind} at tick {first.FirstFaultTick}";
        }

        if (first.FuelConsumed[0] != (ulong)Budget) {
            return $"runaway did not consume its whole budget: {first.FuelConsumed[0]} of {Budget}";
        }

        if (second.FirstFaultKind != AddonFaultKind.OutOfFuel) {
            return $"runaway second run did not fault OutOfFuel: {second.FirstFaultKind}";
        }

        if (HashTrace.FirstDivergence(left: first.CommandHashes, right: second.CommandHashes) >= 0) {
            return "runaway halted at a different point between two runs (command trace)";
        }

        if (!first.FuelConsumed.AsSpan().SequenceEqual(other: second.FuelConsumed)) {
            return "runaway consumed different fuel between two runs";
        }

        return null;
    }
    private static string? LegVersionPin() {
        var version = ScriptingEngine.PinnedWasmtimeVersion;

        if (!Version.TryParse(input: version, result: out var parsed) || (parsed.Major != PinnedMajor)) {
            return $"wasmtime version drift: the pinned engine reports {version}, the gate expects major {PinnedMajor} (fuel timing is codegen-locked to it)";
        }

        return null;
    }
    private static string? LegWalkerValues(ScriptingEngine engine, ScriptingModuleInfo info) {
        using var instance = new AddonInstance(descriptor: Descriptor(name: info.Path, budget: AddonAbi.DefaultFuelPerTick), engine: engine, moduleInfo: info);

        for (var tick = 0; (tick < Ticks); ++tick) {
            var snapshot = BuildSnapshot(tick: (ulong)tick);
            var result = instance.Tick(snapshot: in snapshot);

            if (result.Status != AddonTickStatus.Ok) {
                return $"walker faulted at tick {tick} ({result.Fault.Kind})";
            }

            var commands = instance.Commands;

            if (commands.Length != 1) {
                return $"walker returned {commands.Length} records at tick {tick}, expected 1";
            }

            var command = commands[0];
            var expected = Clamp(max: AddonAbi.One, min: -AddonAbi.One, value: (ScriptingFixtures.WalkerTargetZ - snapshot.PosLocalZ));

            if ((command.PadId != PadCommandId.Move) || (command.ValueX != 0L) || (command.ValueY != expected)) {
                return $"walker record mismatch at tick {tick}: padId {command.PadId} x {command.ValueX} y {command.ValueY}, expected padId {PadCommandId.Move} x 0 y {expected}";
            }
        }

        return null;
    }

    // Runs one fixture over `Ticks` ticks against a fresh store, folding each tick's decoded records and fuel into
    // parallel traces and recording the first fault (if any).
    private static FixtureTrace RunFixture(ScriptingEngine engine, ScriptingModuleInfo info, long budget) {
        using var instance = new AddonInstance(descriptor: Descriptor(name: info.Path, budget: budget), engine: engine, moduleInfo: info);

        var commandHashes = new ulong[Ticks];
        var fuel = new ulong[Ticks];
        var faultKind = AddonFaultKind.None;
        var faultTick = -1;

        for (var tick = 0; (tick < Ticks); ++tick) {
            var snapshot = BuildSnapshot(tick: (ulong)tick);
            var result = instance.Tick(snapshot: in snapshot);

            fuel[tick] = result.FuelConsumed;
            commandHashes[tick] = CommandHash(commands: instance.Commands);

            if ((result.Status == AddonTickStatus.Faulted) && (faultTick < 0)) {
                faultKind = result.Fault.Kind;
                faultTick = tick;
            }
        }

        return new FixtureTrace(
            CommandHashes: commandHashes,
            FirstFaultKind: faultKind,
            FirstFaultTick: faultTick,
            FuelConsumed: fuel
        );
    }
    private static AddonTickResult RunOnce(ScriptingEngine engine, ScriptingModuleInfo info, long budget) {
        using var instance = new AddonInstance(descriptor: Descriptor(name: info.Path, budget: budget), engine: engine, moduleInfo: info);
        var snapshot = BuildSnapshot(tick: 0UL);

        return instance.Tick(snapshot: in snapshot);
    }
    private static string WriteSmoke(PostContext context, ulong finalHash) {
        _ = Directory.CreateDirectory(path: context.ArtifactsDirectory);

        var path = Path.Combine(path1: context.ArtifactsDirectory, path2: "scripting-determinism.txt");

        File.WriteAllText(
            contents: $"scripting-determinism: 9 fixtures, {Ticks} ticks, run twice\nfinal echo cmd-hash: 0x{finalHash:X16}\nfuel-boundary budget: {FuelBoundaryBudget} (calibrated, wasmtime {PinnedMajor}.x)\nwasmtime pinned version: {ScriptingEngine.PinnedWasmtimeVersion}\n",
            path: path
        );

        return path;
    }

    // One fixture run's parallel traces plus its first fault, if any.
    private readonly record struct FixtureTrace(ulong[] CommandHashes, ulong[] FuelConsumed, AddonFaultKind FirstFaultKind, int FirstFaultTick);
}
