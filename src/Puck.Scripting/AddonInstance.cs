using Puck.Assets;

using Wasmtime;

using Module = Wasmtime.Module;

namespace Puck.Scripting;

/// <summary>
/// One addon's live host state: a single <c>Store</c>+<c>Instance</c> compiled from a cached module, the four
/// pure getters cached once, and the reusable decode buffer. Touched only from the single sim-tick thread. A
/// trap or protocol violation drives it into a sticky <see cref="AddonState.Faulted"/> state, skipped every
/// subsequent tick until <see cref="Enable"/> disposes and re-instantiates a fresh <c>Store</c> — a clean,
/// deterministic reset to the module's defined initial state.
/// </summary>
public sealed class AddonInstance : IDisposable {
    // A hard per-store linear-memory ceiling (256 wasm pages), enforced via the runtime store limiter the WS1
    // spike confirmed on Wasmtime 44.0.0. Trusted, path-declared authors; generous but bounded.
    private const long MaxMemoryBytes = (256L * 65536L);

    private readonly AddonCommand[] m_decoded;
    private readonly ScriptingEngine? m_engine;
    private readonly long m_fuelPerTick;
    private readonly AssetContentHash m_hash;
    private readonly Module? m_module;
    private readonly string m_name;
    private readonly int? m_slot;
    private int m_cap;
    private int m_commandsPtr;
    private bool m_disposed;
    private AddonFault m_fault;
    private int m_lastCount;
    private ulong m_lastFuelConsumed;
    private Memory? m_memory;
    private Func<int>? m_onTick;
    private int m_snapshotPtr;
    private AddonState m_state;
    private Store? m_store;

    /// <summary>Initializes and instantiates an addon from a compiled module and its load request.</summary>
    /// <param name="engine">The engine the store is created against.</param>
    /// <param name="moduleInfo">The compiled module and its content identity.</param>
    /// <param name="descriptor">The neutral load request (name, slot, fuel budget).</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/> or <paramref name="moduleInfo"/> is <see langword="null"/>.</exception>
    public AddonInstance(ScriptingEngine engine, ScriptingModuleInfo moduleInfo, in AddonDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(argument: engine);
        ArgumentNullException.ThrowIfNull(argument: moduleInfo);

        m_decoded = new AddonCommand[AddonAbi.MaxCommandRecords];
        m_engine = engine;
        m_fault = AddonFault.None;
        m_fuelPerTick = (descriptor.FuelPerTick ?? AddonAbi.DefaultFuelPerTick);
        m_hash = moduleInfo.ContentHash;
        m_module = moduleInfo.Module;
        m_name = descriptor.Name;
        m_slot = descriptor.Slot;

        Instantiate();
    }

    // The load-failure path: no module was read or compiled (missing file, empty, bad bytes). The addon exists
    // in a sticky faulted state so the run never crashes on a bad addon; there is nothing to revive.
    internal AddonInstance(in AddonDescriptor descriptor, AssetContentHash hash, AddonFault fault) {
        m_decoded = new AddonCommand[AddonAbi.MaxCommandRecords];
        m_engine = null;
        m_fault = fault;
        m_fuelPerTick = (descriptor.FuelPerTick ?? AddonAbi.DefaultFuelPerTick);
        m_hash = hash;
        m_module = null;
        m_name = descriptor.Name;
        m_slot = descriptor.Slot;
        m_state = AddonState.Faulted;
    }

    /// <summary>Gets the decoded command records produced by the most recent successful tick. Read synchronously,
    /// immediately after <see cref="Tick"/>, before the next call.</summary>
    public ReadOnlySpan<AddonCommand> Commands => m_decoded.AsSpan(
        length: m_lastCount,
        start: 0
    );
    /// <summary>Gets the sticky fault detail; <see cref="AddonFault.None"/> when healthy.</summary>
    public AddonFault Fault => m_fault;
    /// <summary>Gets the per-tick fuel budget this addon runs under.</summary>
    public long FuelPerTick => m_fuelPerTick;
    /// <summary>Gets the content identity of the addon's module.</summary>
    public AssetContentHash Hash => m_hash;
    /// <summary>Gets the fuel consumed by the most recent tick.</summary>
    public ulong LastFuelConsumed => m_lastFuelConsumed;
    /// <summary>Gets the addon's identifying name.</summary>
    public string Name => m_name;
    /// <summary>Gets the roster slot the addon drives exclusively; <see langword="null"/> means unassigned.</summary>
    public int? Slot => m_slot;
    /// <summary>Gets the current lifecycle state.</summary>
    public AddonState State => m_state;

    /// <summary>Re-enables the addon by disposing any prior store and instantiating a fresh one from the cached
    /// module — a clean reset to the module's initial state. A load-faulted addon (no module) stays faulted.</summary>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public void Enable() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if ((m_engine is null) || (m_module is null)) {
            return;
        }

        Instantiate();
    }

    /// <summary>Administratively disables the addon; it is skipped every tick until re-enabled.</summary>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public void Disable() {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        m_lastCount = 0;
        m_state = AddonState.Disabled;
    }

    /// <summary>Drives the addon once: writes the snapshot, sets the fuel budget, invokes <c>puck_on_tick</c>,
    /// and decodes the returned command records. Faults are sticky; a faulted or disabled addon short-circuits.</summary>
    /// <param name="snapshot">The per-tick snapshot the guest reads.</param>
    /// <returns>The tick outcome; on success read <see cref="Commands"/> for the decoded records.</returns>
    public AddonTickResult Tick(in AddonSnapshot snapshot) {
        if (m_state != AddonState.Enabled) {
            return AddonTickResult.Faulted(fault: m_fault);
        }

        var memory = m_memory!;
        var onTick = m_onTick!;
        var store = m_store!;

        AddonSnapshotWriter.Write(
            destination: memory.GetSpan(address: m_snapshotPtr, length: AddonAbi.SnapshotBytes),
            snapshot: in snapshot
        );
        store.Fuel = (ulong)m_fuelPerTick;

        int count;

        try {
            count = onTick();
        } catch (TrapException trap) {
            var kind = Classify(code: trap.Type);
            var consumed = FuelConsumed();

            m_lastFuelConsumed = consumed;
            SetFault(
                kind: kind,
                reason: TrapReason(
                    kind: kind,
                    tick: snapshot.Tick,
                    trap: trap
                )
            );
            GC.KeepAlive(obj: store);
            return AddonTickResult.Faulted(fault: m_fault, fuelConsumed: consumed);
        }

        GC.KeepAlive(obj: store);

        var fuelConsumed = FuelConsumed();

        m_lastFuelConsumed = fuelConsumed;

        if ((uint)count > (uint)m_cap) {
            SetFault(kind: AddonFaultKind.DecodeError, reason: $"DecodeError — puck_on_tick returned {count}, cap {m_cap}");
            return AddonTickResult.Faulted(fault: m_fault, fuelConsumed: fuelConsumed);
        }

        var records = memory.GetSpan(address: m_commandsPtr, length: (count * AddonAbi.CommandRecordBytes));

        if (!AddonCommandReader.TryDecode(count: count, destination: m_decoded, errorIndex: out var errorIndex, source: records)) {
            SetFault(kind: AddonFaultKind.DecodeError, reason: DecodeReason(errorIndex: errorIndex, records: records));
            return AddonTickResult.Faulted(fault: m_fault, fuelConsumed: fuelConsumed);
        }

        m_lastCount = count;
        return AddonTickResult.Ok(commandCount: count, fuelConsumed: fuelConsumed);
    }

    /// <summary>Disposes the store and its native resources. The compiled module is owned by the loader cache.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        DisposeStore();
        GC.SuppressFinalize(obj: this);
    }

    private static AddonFaultKind Classify(TrapCode code) {
        return code switch {
            TrapCode.OutOfFuel => AddonFaultKind.OutOfFuel,
            TrapCode.StackOverflow => AddonFaultKind.StackOverflow,
            TrapCode.MemoryOutOfBounds => AddonFaultKind.MemoryOutOfBounds,
            TrapCode.Unreachable => AddonFaultKind.Unreachable,
            _ => AddonFaultKind.Trap,
        };
    }
    private static string DecodeReason(int errorIndex, ReadOnlySpan<byte> records) {
        var start = (errorIndex * AddonAbi.CommandRecordBytes);

        if ((errorIndex < 0) || ((start + AddonAbi.CommandRecordBytes) > records.Length)) {
            return $"DecodeError — record {errorIndex} malformed";
        }

        var record = records.Slice(
            length: AddonAbi.CommandRecordBytes,
            start: start
        );

        return $"DecodeError — record {errorIndex} {AddonCommandReader.DescribeError(record: record)}";
    }
    private static bool RegionsFit(int cap, int commandsPtr, long memoryLength, int snapshotPtr, out string error) {
        var snapshotEnd = ((long)snapshotPtr + AddonAbi.SnapshotBytes);

        if ((snapshotPtr < 0) || (snapshotEnd > memoryLength)) {
            error = $"snapshot region [{snapshotPtr}, {snapshotEnd}) exceeds memory {memoryLength}";
            return false;
        }

        var commandsEnd = ((long)commandsPtr + ((long)cap * AddonAbi.CommandRecordBytes));

        if ((commandsPtr < 0) || (commandsEnd > memoryLength)) {
            error = $"commands region [{commandsPtr}, {commandsEnd}) exceeds memory {memoryLength}";
            return false;
        }

        if ((snapshotPtr < commandsEnd) && (commandsPtr < snapshotEnd)) {
            error = $"snapshot region [{snapshotPtr}, {snapshotEnd}) overlaps commands region [{commandsPtr}, {commandsEnd})";
            return false;
        }

        error = "";
        return true;
    }
    private void DisposeStore() {
        m_memory = null;
        m_onTick = null;

        if (m_store is not null) {
            m_store.Dispose();
            m_store = null;
        }
    }
    private void SetFault(AddonFaultKind kind, string reason) {
        m_fault = new AddonFault(Detail: $"addon {m_name}: {reason}", Kind: kind);
        m_lastCount = 0;
        m_state = AddonState.Faulted;
    }
    private ulong FuelConsumed() {
        var budget = (ulong)m_fuelPerTick;
        var remaining = (m_store?.Fuel ?? budget);

        return ((budget >= remaining) ? (budget - remaining) : 0UL);
    }
    private void Instantiate() {
        DisposeStore();

        m_lastCount = 0;
        m_lastFuelConsumed = 0UL;

        if ((m_engine is null) || (m_module is null)) {
            return;
        }

        if (!AddonModuleValidator.TryValidate(error: out var exportError, module: m_module)) {
            SetFault(kind: AddonFaultKind.BadExport, reason: $"BadExport — {exportError}");
            return;
        }

        Store? store = null;

        try {
            store = new Store(engine: m_engine.Engine);

            store.SetLimits(memorySize: MaxMemoryBytes);
            store.Fuel = (ulong)m_fuelPerTick;

            var instance = new Instance(store: store, module: m_module);

            if (!TryHandshake(instance: instance, store: store)) {
                store.Dispose();
                return;
            }

            m_fault = AddonFault.None;
            m_state = AddonState.Enabled;
            m_store = store;
        } catch (TrapException trap) {
            var kind = Classify(code: trap.Type);

            store?.Dispose();
            SetFault(kind: kind, reason: $"{kind} during instantiation ({trap.Type})");
        } catch (WasmtimeException error) {
            store?.Dispose();
            SetFault(kind: AddonFaultKind.BadExport, reason: $"BadExport — {error.Message}");
        }
    }
    private bool TryHandshake(Instance instance, Store store) {
        var version = instance.GetFunction<int>(name: AddonAbi.Exports.AbiVersion)!();

        GC.KeepAlive(obj: store);

        if (version != AddonAbi.AbiVersion) {
            SetFault(kind: AddonFaultKind.AbiMismatch, reason: $"AbiMismatch — guest ABI {version}, host speaks puck.addon.v1");
            return false;
        }

        var init = instance.GetAction(name: AddonAbi.Exports.Init);

        if (init is not null) {
            init();
            GC.KeepAlive(obj: store);
        }

        var snapshotPtr = instance.GetFunction<int>(name: AddonAbi.Exports.SnapshotPtr)!();
        var commandsPtr = instance.GetFunction<int>(name: AddonAbi.Exports.CommandsPtr)!();
        var cap = instance.GetFunction<int>(name: AddonAbi.Exports.CommandsCap)!();

        GC.KeepAlive(obj: store);

        if ((cap < 0) || (cap > AddonAbi.MaxCommandRecords)) {
            SetFault(kind: AddonFaultKind.BadExport, reason: $"BadExport — puck_commands_cap {cap} out of range [0, {AddonAbi.MaxCommandRecords}]");
            return false;
        }

        var memory = instance.GetMemory(name: AddonAbi.Exports.Memory)!;

        if (!RegionsFit(cap: cap, commandsPtr: commandsPtr, error: out var boundsError, memoryLength: memory.GetLength(), snapshotPtr: snapshotPtr)) {
            SetFault(kind: AddonFaultKind.BadExport, reason: $"BadExport — {boundsError}");
            return false;
        }

        m_cap = cap;
        m_commandsPtr = commandsPtr;
        m_memory = memory;
        m_onTick = instance.GetFunction<int>(name: AddonAbi.Exports.OnTick)!;
        m_snapshotPtr = snapshotPtr;
        return true;
    }
    private string TrapReason(AddonFaultKind kind, ulong tick, TrapException trap) {
        if (kind == AddonFaultKind.OutOfFuel) {
            return $"OutOfFuel at tick {tick} — disabled; 'addon enable {m_name}' to retry";
        }

        return $"{kind} at tick {tick} ({trap.Type})";
    }
}
