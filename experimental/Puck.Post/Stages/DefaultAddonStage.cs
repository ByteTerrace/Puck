using Puck.Assets;
using Puck.Commands;
using Puck.Maths;
using Puck.Scripting;

using Wasmtime;

using Module = Wasmtime.Module;

namespace Puck.Post;

/// <summary>
/// Tier-A stage. Proves the COMMITTED shipped default addon artifact
/// (<c>src/Puck.World/Assets/addons/puck-addon-default.wasm</c>) is a valid, self-contained, deterministic
/// <c>puck.addon.v1</c> module — reading it through <see cref="WasmModuleLoader"/> exactly the way a real run
/// document loads any addon, then asserting: it compiles and <see cref="AddonModuleValidator"/> accepts it;
/// it declares zero imports (asserted here directly, not only through the validator, because self-containment is
/// the property the whole design rests on); its export surface covers the full ABI (<c>memory</c>,
/// <c>puck_abi_version</c>, <c>puck_sources_ptr</c>, <c>puck_sources_count</c>, <c>puck_snapshot_ptr</c>,
/// <c>puck_commands_ptr</c>, <c>puck_commands_cap</c>, <c>puck_on_tick</c>, with <c>puck_init</c> optional) with
/// the exact required signatures; its declared source table is non-empty and every declared id resolves against
/// <see cref="Puck.Input.InputSources"/> (asserted here directly, reading <c>puck_sources_ptr</c>/
/// <c>puck_sources_count</c> and decoding the table through <see cref="AddonSourceTableReader"/>, not only
/// inferred from a successful handshake); it instantiates and completes the ABI handshake into
/// <see cref="AddonState.Enabled"/>; and, driven over <see cref="Ticks"/> ticks by a deterministic snapshot
/// generator against two fresh stores, its command-hash and per-tick fuel traces are bit-identical between the
/// two runs and it never faults <see cref="AddonFaultKind.OutOfFuel"/> under the default per-tick budget.
///
/// This stage proves the artifact IS a valid, self-contained, deterministic addon module — nothing about how it
/// came to be. It does NOT prove those bytes were built from the current <c>wasm/</c> Rust source: POST
/// deliberately never invokes a Rust toolchain, so the committed <c>.wasm</c>'s provenance is out of its reach.
/// That is instead covered by rebuilding through <c>wasm/build.ps1</c> (or <c>build.sh</c>) and diffing the
/// output, <c>cargo test</c> run from <c>wasm/</c> for the Rust port's own correctness, and the <c>wasm-stdlib</c>
/// stage for the generated fixed-point sources the addon links against.
/// </summary>
internal sealed class DefaultAddonStage : IPostStage {
    private const int Ticks = 400;
    private const string RelativeArtifactPath = "src/Puck.World/Assets/addons/puck-addon-default.wasm";

    /// <inheritdoc/>
    public string Name => "default-addon";

    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var repositoryRoot = FindRepositoryRoot();

        if (repositoryRoot is null) {
            return PostStageOutcome.Fail(detail: "could not locate the repository root (a directory containing docs\\examples and schema\\run.schema.json) above the base or current directory");
        }

        var modulePath = Path.Combine(path1: repositoryRoot, path2: RelativeArtifactPath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar));

        if (!File.Exists(path: modulePath)) {
            return PostStageOutcome.Fail(detail: $"the committed default addon artifact is missing at {modulePath}; build it with wasm/build.ps1 (or wasm/build.sh) from the wasm/ Cargo workspace, then commit target/wasm32-unknown-unknown/release/puck_addon_default.wasm at that path");
        }

        ScriptingEngine engine;

        try {
            engine = new ScriptingEngine(options: ScriptingEngineOptions.Deterministic);
        } catch (Exception error) when (IsRuntimeUnavailable(error: error)) {
            return PostStageOutcome.Skip(detail: $"the Wasmtime native runtime is unavailable on this platform: {error.Message}");
        }

        using (engine) {
            ScriptingModuleInfo moduleInfo;

            try {
                moduleInfo = new WasmModuleLoader(assetSource: new FileSystemAssetSource(), engine: engine).Load(path: modulePath);
            } catch (Exception error) when (IsRuntimeUnavailable(error: error)) {
                return PostStageOutcome.Skip(detail: $"the Wasmtime native runtime is unavailable on this platform: {error.Message}");
            } catch (Exception error) when (error is InvalidDataException or WasmtimeException) {
                return PostStageOutcome.Fail(detail: $"the committed default addon artifact at {modulePath} failed to load or compile: {error.Message}");
            }

            var module = moduleInfo.Module;

            if (!AddonModuleValidator.TryValidate(error: out var validationError, module: module)) {
                return PostStageOutcome.Fail(detail: $"the committed default addon artifact at {modulePath} failed ABI validation: {validationError}");
            }

            var failure =
                (LegZeroImports(module: module)
                ?? (LegExportSurface(module: module)
                ?? (LegSourceTable(engine: engine, module: module)
                ?? (LegHandshake(engine: engine, moduleInfo: moduleInfo)
                ?? LegDeterministicTicks(engine: engine, moduleInfo: moduleInfo)))));

            if (failure is not null) {
                return PostStageOutcome.Fail(detail: failure);
            }

            return PostStageOutcome.Pass(detail: $"puck-addon-default.wasm ({moduleInfo.ByteLength} bytes): zero imports, full ABI surface incl. source table, declared sources non-empty and resolve, handshakes Enabled, {Ticks} ticks x2 fuel/command-hash identical, never OutOfFuel at the default budget — proves the committed artifact is a valid self-contained deterministic addon, NOT that it was built from the current wasm/ source");
        }
    }

    // The per-tick snapshot generator: deterministic integer ramps (no RNG, no wall clock), the same shape
    // ScriptingDeterminismStage drives its fixtures with, sized generically (no fixture-specific target) since this
    // stage exercises the real shipped addon rather than a hand-authored fixture.
    private static AddonSnapshot BuildSnapshot(ulong tick) {
        var index = (long)tick;

        return new AddonSnapshot(
            Buttons: 0u,
            PosLocalX: (((index * 997L) % 131072L) - 65536L),
            PosLocalY: (((index * 613L) % 131072L) - 65536L),
            PosLocalZ: (((index * 401L) % 131072L) - 65536L),
            Tick: tick
        );
    }

    // Folds one tick's decoded records into a stable digest, LSB-first over each record's sourceIndex/phase/
    // valueX/valueY. The record count leads so an empty tick and a one-record tick can never collide. Mirrors
    // ScriptingDeterminismStage.CommandHash exactly.
    private static ulong CommandHash(ReadOnlySpan<AddonCommand> commands) {
        var hash = Fnv1aHash.Create();

        hash.Add(value: (uint)commands.Length);

        for (var index = 0; (index < commands.Length); ++index) {
            var command = commands[index];

            hash.Add(value: (uint)command.SourceIndex);
            hash.Add(value: (uint)command.Phase);
            hash.Add(value: command.ValueX);
            hash.Add(value: command.ValueY);
        }

        return hash.Value;
    }
    private static AddonDescriptor Descriptor() {
        return new AddonDescriptor(
            Enabled: true,
            FuelPerTick: AddonAbi.DefaultFuelPerTick,
            ModuleHash: null,
            ModulePath: "puck-addon-default",
            Name: "puck-addon-default",
            Slot: null
        );
    }
    private static bool IsRuntimeUnavailable(Exception error) {
        return ((error is DllNotFoundException or BadImageFormatException)
            || (error is TypeInitializationException { InnerException: DllNotFoundException or BadImageFormatException }));
    }
    private static string? FindFunction(IReadOnlyList<Export> exports, string name, out FunctionExport? function) {
        foreach (var export in exports) {
            if ((export is FunctionExport candidate) && string.Equals(a: export.Name, b: name, comparisonType: StringComparison.Ordinal)) {
                function = candidate;
                return null;
            }
        }

        function = null;
        return $"required export '{name}' is missing";
    }

    // Walks up from the app base and the working directory to the checkout root. Mirrors
    // RunDocumentStage.FindRepositoryRoot / WasmStdlibStage.FindRepositoryRoot exactly (same anchors, same walk).
    private static string? FindRepositoryRoot() {
        foreach (var anchor in (string?[])[AppContext.BaseDirectory, Environment.CurrentDirectory]) {
            for (var directory = anchor; (directory is not null); directory = Path.GetDirectoryName(path: directory)) {
                if (Directory.Exists(path: Path.Combine(path1: directory, path2: "docs", path3: "examples")) && File.Exists(path: Path.Combine(path1: directory, path2: "schema", path3: "run.schema.json"))) {
                    return directory;
                }
            }
        }

        return null;
    }

    // Drives the module over Ticks ticks against a fresh store, folding each tick's decoded records and fuel into
    // parallel traces and recording the first fault (if any). Mirrors ScriptingDeterminismStage.RunFixture exactly.
    private static FixtureTrace RunTicks(ScriptingEngine engine, ScriptingModuleInfo moduleInfo) {
        using var instance = new AddonInstance(descriptor: Descriptor(), engine: engine, moduleInfo: moduleInfo);

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

    // Leg: the addon must never exhaust its fuel budget (or fault for any other reason) driving the deterministic
    // snapshot generator, and its command-hash and fuel traces must be bit-identical between two runs against
    // fresh stores.
    private static string? LegDeterministicTicks(ScriptingEngine engine, ScriptingModuleInfo moduleInfo) {
        var first = RunTicks(engine: engine, moduleInfo: moduleInfo);

        if (first.FirstFaultKind == AddonFaultKind.OutOfFuel) {
            return $"the committed default addon exhausted its {AddonAbi.DefaultFuelPerTick}-fuel default per-tick budget at tick {first.FirstFaultTick} — it must stay inside the default budget";
        }

        if (first.FirstFaultTick >= 0) {
            return $"the committed default addon faulted at tick {first.FirstFaultTick} ({first.FirstFaultKind}) driving the deterministic snapshot generator";
        }

        var second = RunTicks(engine: engine, moduleInfo: moduleInfo);
        var divergence = HashTrace.FirstDivergence(left: first.CommandHashes, right: second.CommandHashes);

        if (divergence >= 0) {
            return $"non-deterministic: the committed default addon produced a different command stream at tick {divergence} between two runs against fresh stores";
        }

        if (!first.FuelConsumed.AsSpan().SequenceEqual(other: second.FuelConsumed)) {
            return "non-deterministic: the committed default addon consumed different fuel between two runs against fresh stores";
        }

        return null;
    }

    // Leg: the export surface required for the puck.addon.v1 ABI is present with the exact signature the host
    // binds against — asserted explicitly here (not only inside AddonModuleValidator) so the full required surface
    // is visible in the stage itself. puck_init is optional but, if present, must be ()->().
    private static string? LegExportSurface(Module module) {
        var exports = module.Exports;
        var hasMemory = false;

        foreach (var export in exports) {
            if ((export is MemoryExport) && string.Equals(a: export.Name, b: AddonAbi.Exports.Memory, comparisonType: StringComparison.Ordinal)) {
                hasMemory = true;
                break;
            }
        }

        if (!hasMemory) {
            return $"the committed default addon is missing the required '{AddonAbi.Exports.Memory}' memory export";
        }

        foreach (var name in (string[])[AddonAbi.Exports.AbiVersion, AddonAbi.Exports.SourcesPtr, AddonAbi.Exports.SourcesCount, AddonAbi.Exports.SnapshotPtr, AddonAbi.Exports.CommandsPtr, AddonAbi.Exports.CommandsCap, AddonAbi.Exports.OnTick]) {
            var missing = FindFunction(exports: exports, function: out var function, name: name);

            if (missing is not null) {
                return $"the committed default addon is missing the required export '{name}' (() -> i32)";
            }

            if ((function!.Parameters.Count != 0) || (function.Results.Count != 1) || (function.Results[0] != ValueKind.Int32)) {
                return $"the committed default addon's '{name}' export has the wrong signature; expected () -> i32";
            }
        }

        _ = FindFunction(exports: exports, function: out var init, name: AddonAbi.Exports.Init);

        if ((init is not null) && ((init.Parameters.Count != 0) || (init.Results.Count != 0))) {
            return $"the committed default addon's optional '{AddonAbi.Exports.Init}' export is present but not () -> ()";
        }

        return null;
    }

    // Leg: constructing an AddonInstance from the compiled module must complete the ABI handshake and land in
    // AddonState.Enabled, never a sticky fault.
    private static string? LegHandshake(ScriptingEngine engine, ScriptingModuleInfo moduleInfo) {
        using var instance = new AddonInstance(descriptor: Descriptor(), engine: engine, moduleInfo: moduleInfo);

        if (instance.State != AddonState.Enabled) {
            return $"the committed default addon failed to instantiate and handshake: state {instance.State}, fault {instance.Fault.Kind} ({instance.Fault.Detail})";
        }

        return null;
    }

    // Leg: the declared source table must be non-empty and every declared id must resolve against
    // Puck.Input.InputSources. Asserted directly here — a fresh instantiation reading puck_sources_ptr/
    // puck_sources_count and decoding the table through AddonSourceTableReader — rather than only inferred from
    // LegHandshake landing in AddonState.Enabled, so the property the default addon's provider-neutral input
    // surface rests on is visible in the stage itself.
    private static string? LegSourceTable(ScriptingEngine engine, Module module) {
        using var store = new Store(engine: engine.Engine);

        store.Fuel = (ulong)AddonAbi.DefaultFuelPerTick;

        var instance = new Instance(store: store, module: module);
        var sourcesPtr = instance.GetFunction<int>(name: AddonAbi.Exports.SourcesPtr)!();
        var sourceCount = instance.GetFunction<int>(name: AddonAbi.Exports.SourcesCount)!();

        GC.KeepAlive(obj: store);

        if (sourceCount <= 0) {
            return $"the committed default addon declares {sourceCount} sources — it must declare a non-empty source table";
        }

        if (sourceCount > AddonAbi.MaxSources) {
            return $"the committed default addon declares {sourceCount} sources, exceeding the {AddonAbi.MaxSources} maximum";
        }

        var memory = instance.GetMemory(name: AddonAbi.Exports.Memory)!;
        var sourceTableLength = (sourceCount * AddonAbi.SourceSlotBytes);
        var sourceTableEnd = (sourcesPtr + sourceTableLength);

        if ((sourcesPtr < 0) || (sourceTableEnd > memory.GetLength())) {
            return $"the committed default addon's source table [{sourcesPtr}, {sourceTableEnd}) exceeds memory {memory.GetLength()}";
        }

        var sourceTable = memory.GetSpan(address: sourcesPtr, length: sourceTableLength);
        var destination = new AddonSourceDeclaration[sourceCount];

        GC.KeepAlive(obj: store);

        if (!AddonSourceTableReader.TryDecode(count: sourceCount, destination: destination, error: out var error, errorIndex: out var errorIndex, source: sourceTable)) {
            return $"the committed default addon's declared source table failed validation at slot {errorIndex}: {error}";
        }

        return null;
    }

    // Leg: the module must declare zero imports. Asserted directly against the compiled module — not only via
    // AddonModuleValidator — because self-containment (running in any wasm runtime, with no host-supplied
    // functions at all) is the property the whole puck.addon.v1 design rests on.
    private static string? LegZeroImports(Module module) {
        var imports = module.Imports;

        if (imports.Count != 0) {
            var offender = imports[0];

            return $"the committed default addon declares {imports.Count} import(s) — first '{offender.ModuleName}::{offender.Name}' — a puck.addon.v1 module must be self-contained with zero imports";
        }

        return null;
    }

    // One tick-drive's parallel traces plus its first fault, if any. Mirrors ScriptingDeterminismStage.FixtureTrace.
    private readonly record struct FixtureTrace(ulong[] CommandHashes, ulong[] FuelConsumed, AddonFaultKind FirstFaultKind, int FirstFaultTick);
}
