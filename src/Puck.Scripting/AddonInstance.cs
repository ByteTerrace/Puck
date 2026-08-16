using Puck.Assets;

using Wasmtime;

using Module = Wasmtime.Module;

namespace Puck.Scripting;

/// <summary>
/// One addon's live host state: a single <c>Store</c>+<c>Instance</c> compiled from a cached module, the ring and
/// channel-table geometry cached at handshake, and the reusable decode buffer. Touched only from the single sim-tick
/// thread. A trap or protocol violation drives it into a sticky <see cref="AddonState.Faulted"/> state, skipped every
/// subsequent tick until <see cref="Enable"/> disposes and re-instantiates a fresh <c>Store</c> — a clean,
/// deterministic reset to the module's defined initial state.
/// </summary>
/// <remarks>
/// <para><b>Mounting is two-phase.</b> Construction (and <see cref="Enable"/>) runs the handshake — export
/// validation, channel-descriptor decode, channel-name-table decode, and every region bounds/overlap check —
/// without calling <c>puck_init</c>. The consumer runs its own gates (attenuation, quota, disclosure) against the
/// decoded declarations and then calls <see cref="Admit"/>, which runs the guest's optional <c>puck_init</c> under the
/// fuel budget and makes the instance tickable. A guest therefore cannot emit anything before every mount-time gate is
/// in place: output cells only ever cross when a <c>puck_on_tick</c> return announces them, and
/// <see cref="Tick"/> refuses to run unadmitted.</para>
/// </remarks>
public sealed class AddonInstance : IDisposable {
    // A hard per-store linear-memory ceiling (256 wasm pages), enforced via the runtime store limiter the WS1
    // spike confirmed on Wasmtime 44.0.0. Trusted, path-declared authors; generous but bounded.
    private const long MaxMemoryBytes = (256L * 65536L);

    private readonly AddonChannelBinding[] m_channelBindings;
    private readonly IAddonChannelResolver? m_channelResolver;
    private readonly AddonChannelDescriptor[] m_channels;
    private readonly AddonOutCell[] m_decoded;
    private readonly ScriptingEngine? m_engine;
    private readonly long m_fuelPerTick;
    private readonly AssetContentHash m_hash;
    private readonly Module? m_module;
    private readonly string m_name;

    private bool m_admitted;
    private int m_channelBindingCount;
    private int m_channelCount;
    private bool m_disposed;
    private AddonFault m_fault;
    private int m_generation;
    private int m_inCap;
    private int m_inPtr;
    private Action? m_initAction;
    private int m_lastCount;
    private ulong m_lastFuelConsumed;
    private Memory? m_memory;
    private Func<int, int>? m_onTick;
    private int m_outCap;
    private int m_outPtr;
    private AddonState m_state;
    private Store? m_store;

    /// <summary>Initializes and instantiates an addon from a compiled module and its load request, running the
    /// handshake but not <c>puck_init</c> — see the type remarks for the two-phase mount contract.</summary>
    /// <param name="engine">The engine the store is created against.</param>
    /// <param name="moduleInfo">The compiled module and its content identity.</param>
    /// <param name="channelResolver">The host channel table the input channel's declared names are resolved against.</param>
    /// <param name="descriptor">The neutral load request (name, fuel budget).</param>
    /// <exception cref="ArgumentNullException"><paramref name="engine"/>, <paramref name="moduleInfo"/>, or <paramref name="channelResolver"/> is <see langword="null"/>.</exception>
    public AddonInstance(ScriptingEngine engine, ScriptingModuleInfo moduleInfo, IAddonChannelResolver channelResolver, in AddonDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(argument: engine);
        ArgumentNullException.ThrowIfNull(argument: moduleInfo);
        ArgumentNullException.ThrowIfNull(argument: channelResolver);

        m_channelBindings = new AddonChannelBinding[AddonAbi.MaxChannelNames];
        m_channels = new AddonChannelDescriptor[AddonAbi.MaxChannels];
        m_channelResolver = channelResolver;
        m_decoded = new AddonOutCell[AddonAbi.MaxOutCells];
        m_engine = engine;
        m_fault = AddonFault.None;
        m_fuelPerTick = (descriptor.FuelPerTick ?? AddonAbi.DefaultFuelPerTick);
        m_hash = moduleInfo.ContentHash;
        m_module = moduleInfo.Module;
        m_name = descriptor.Name;

        Instantiate();
    }

    // The load-failure path: no module was read or compiled (missing file, empty, bad bytes). The addon exists
    // in a sticky faulted state so the run never crashes on a bad addon; there is nothing to revive — and
    // nothing to hand a channel resolver to: with no engine/module, Instantiate short-circuits before the
    // handshake's channel-name-table decode ever needs one.
    internal AddonInstance(in AddonDescriptor descriptor, AssetContentHash hash, AddonFault fault) {
        m_channelBindings = [];
        m_channels = [];
        m_decoded = [];
        m_engine = null;
        m_fault = fault;
        m_fuelPerTick = (descriptor.FuelPerTick ?? AddonAbi.DefaultFuelPerTick);
        m_hash = hash;
        m_module = null;
        m_name = descriptor.Name;
        m_state = AddonState.Faulted;
    }

    /// <summary>Gets whether <see cref="Admit"/> has run for the current store — the gate <see cref="Tick"/>
    /// requires. Reset by <see cref="Enable"/>'s re-instantiation.</summary>
    public bool Admitted => m_admitted;
    /// <summary>Gets the input channel's resolved channel-name bindings (empty when the guest declares no input
    /// channel), indexed by the guest's own declared ordinal.</summary>
    public ReadOnlySpan<AddonChannelBinding> ChannelBindings => m_channelBindings.AsSpan(
        length: m_channelBindingCount,
        start: 0
    );
    /// <summary>Gets the channel descriptors the guest declared, decoded once at handshake.</summary>
    public ReadOnlySpan<AddonChannelDescriptor> Channels => m_channels.AsSpan(
        length: m_channelCount,
        start: 0
    );
    /// <summary>Gets the sticky fault detail; <see cref="AddonFault.None"/> when healthy.</summary>
    public AddonFault Fault => m_fault;
    /// <summary>Gets the per-tick fuel budget this addon runs under.</summary>
    public long FuelPerTick => m_fuelPerTick;
    /// <summary>Gets a counter incremented every time <see cref="Instantiate"/> creates a store — at construction and
    /// at every <see cref="Enable"/> — so a consumer can detect that a fresh store now stands behind this object
    /// (linear memory wiped, every guest-learned handle gone) regardless of which lifecycle verb caused it. This
    /// object's own reference identity does not change across <see cref="Enable"/>, which is why that alone cannot
    /// serve as the signal.</summary>
    public int Generation => m_generation;
    /// <summary>Gets the content identity of the addon's module.</summary>
    public AssetContentHash Hash => m_hash;
    /// <summary>Gets the guest's declared input-ring capacity in cells — the host-side ceiling on how many cells one
    /// <see cref="Tick"/> batch may carry.</summary>
    public int InputCellCapacity => m_inCap;
    /// <summary>Gets the fuel consumed by the most recent tick (or by <see cref="Admit"/>'s <c>puck_init</c> before
    /// the first tick).</summary>
    public ulong LastFuelConsumed => m_lastFuelConsumed;
    /// <summary>Gets the addon's identifying name.</summary>
    public string Name => m_name;
    /// <summary>Gets the structurally-decoded output cells produced by the most recent successful tick. Read
    /// synchronously, immediately after <see cref="Tick"/>, before the next call. Structure only — verb, payload, and
    /// handle validation belong to the consumer's vocabulary layer.</summary>
    public ReadOnlySpan<AddonOutCell> OutCells => m_decoded.AsSpan(
        length: m_lastCount,
        start: 0
    );
    /// <summary>Gets the current lifecycle state.</summary>
    public AddonState State => m_state;

    private static AddonFaultKind Classify(TrapCode code) {
        return code switch {
            TrapCode.OutOfFuel => AddonFaultKind.OutOfFuel,
            TrapCode.StackOverflow => AddonFaultKind.StackOverflow,
            TrapCode.MemoryOutOfBounds => AddonFaultKind.MemoryOutOfBounds,
            TrapCode.Unreachable => AddonFaultKind.Unreachable,
            _ => AddonFaultKind.Trap,
        };
    }
    private void DisposeStore() {
        m_admitted = false;
        m_initAction = null;
        m_memory = null;
        m_onTick = null;

        if (m_store is not null) {
            m_store.Dispose();
            m_store = null;
        }
    }
    private ulong FuelConsumed() {
        var budget = ((ulong)m_fuelPerTick);
        var remaining = (m_store?.Fuel ?? budget);

        return ((budget >= remaining)
            ? (budget - remaining)
            : 0UL
        );
    }
    private void Instantiate() {
        ++m_generation;

        DisposeStore();

        m_channelBindingCount = 0;
        m_channelCount = 0;
        m_lastCount = 0;
        m_lastFuelConsumed = 0UL;

        if (
            (m_engine is null) ||
            (m_module is null)
        ) {
            return;
        }

        if (!AddonModuleValidator.TryValidate(
            error: out var exportError,
            module: m_module
        )) {
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — {exportError}"
            );
            return;
        }

        Store? store = null;

        try {
            store = new Store(engine: m_engine.Engine);

            store.SetLimits(memorySize: MaxMemoryBytes);
            store.Fuel = ((ulong)m_fuelPerTick);

            var instance = new Instance(
                store: store,
                module: m_module
            );

            if (!TryHandshake(
                instance: instance,
                store: store
            )) {
                store.Dispose();
                return;
            }

            m_fault = AddonFault.None;
            m_state = AddonState.Enabled;
            m_store = store;
        } catch (TrapException trap) {
            var kind = Classify(code: trap.Type);

            store?.Dispose();
            SetFault(
                kind: kind,
                reason: $"{kind} during instantiation ({trap.Type})"
            );
        } catch (WasmtimeException error) {
            store?.Dispose();
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — {error.Message}"
            );
        }
    }
    private static bool RangeFits(long length, int start, long memoryLength, string name, out string error) {
        var end = (((long)start) + length);

        if (
            (start < 0) ||
            (end > memoryLength)
        ) {
            error = $"{name} region [{start}, {end}) exceeds memory {memoryLength}";
            return false;
        }

        error = "";
        return true;
    }
    private void SetFault(AddonFaultKind kind, string reason) {
        m_fault = new AddonFault(
            Detail: $"addon {m_name}: {reason}",
            Kind: kind
        );
        m_lastCount = 0;
        m_state = AddonState.Faulted;
    }
    private string TrapReason(AddonFaultKind kind, TrapException trap) {
        if (kind == AddonFaultKind.OutOfFuel) {
            return $"OutOfFuel — disabled; 'world.addon.enable {m_name}' to retry (re-instantiates and re-admits; a genuine spin loop will exhaust fuel again)";
        }

        return $"{kind} ({trap.Type})";
    }
    // The handshake: version, geometry, channel table, channel-name table, bounds — everything EXCEPT
    // puck_init, which Admit runs after the consumer's gates (the mount-order contract in the type remarks).
    private bool TryHandshake(Instance instance, Store store) {
        var version = instance.GetFunction<int>(name: AddonAbi.Exports.AbiVersion)!();

        GC.KeepAlive(obj: store);

        if (version != AddonAbi.AbiVersion) {
            SetFault(
                kind: AddonFaultKind.AbiMismatch,
                reason: $"AbiMismatch — guest ABI {version}, host speaks ABI {AddonAbi.AbiVersion}"
            );
            return false;
        }

        var channelsPtr = instance.GetFunction<int>(name: AddonAbi.Exports.ChannelsPtr)!();
        var channelsCount = instance.GetFunction<int>(name: AddonAbi.Exports.ChannelsCount)!();
        var outPtr = instance.GetFunction<int>(name: AddonAbi.Exports.OutPtr)!();
        var outCap = instance.GetFunction<int>(name: AddonAbi.Exports.OutCap)!();
        var inPtr = instance.GetFunction<int>(name: AddonAbi.Exports.InPtr)!();
        var inCap = instance.GetFunction<int>(name: AddonAbi.Exports.InCap)!();

        GC.KeepAlive(obj: store);

        if (
            (channelsCount < 1) ||
            (channelsCount > AddonAbi.MaxChannels)
        ) {
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — puck_channels_count {channelsCount} out of range [1, {AddonAbi.MaxChannels}]"
            );
            return false;
        }

        if (
            (outCap < 0) ||
            (outCap > AddonAbi.MaxOutCells)
        ) {
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — puck_out_cap {outCap} out of range [0, {AddonAbi.MaxOutCells}]"
            );
            return false;
        }

        if (
            (inCap < 1) ||
            (inCap > AddonAbi.MaxInCells)
        ) {
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — puck_in_cap {inCap} out of range [1, {AddonAbi.MaxInCells}]"
            );
            return false;
        }

        // The ring-geometry relation: every refusable act needs a same-tick verdict slot in the guest's OWN
        // declared input capacity, so a guest's out-cap must never outrun (in-cap - 1) — the budget MergeAnswers
        // charges disclosures and answers against.
        if ((inCap - 1) < outCap) {
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — puck_in_cap {inCap} - 1 must be >= puck_out_cap {outCap} (every refusable act needs a same-tick verdict slot)"
            );
            return false;
        }

        var memory = instance.GetMemory(name: AddonAbi.Exports.Memory)!;
        var memoryLength = memory.GetLength();
        var channelsLength = (((long)channelsCount) * AddonAbi.ChannelDescriptorBytes);

        if (!RangeFits(
            error: out var boundsError,
            length: channelsLength,
            memoryLength: memoryLength,
            name: "channels",
            start: channelsPtr
        )) {
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — {boundsError}"
            );
            return false;
        }

        var channelTable = memory.GetSpan(
            address: channelsPtr,
            length: ((int)channelsLength)
        );

        if (!AddonChannelTableReader.TryDecode(
            count: channelsCount,
            destination: m_channels,
            error: out var channelError,
            source: channelTable
        )) {
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — {channelError}"
            );
            return false;
        }

        m_channelCount = channelsCount;

        // Region accounting for the overlap sweep: the channel table, both rings, and (when declared) the input
        // channel's declared channel-name table must all fit and be pairwise disjoint — a guest aliasing two
        // regions is a guest whose writes mean two things at once.
        var regions = new (long Start, long End, string Name)[4];
        var regionCount = 0;

        regions[regionCount++] = (channelsPtr, (channelsPtr + channelsLength), "channels");

        var outLength = (((long)outCap) * AddonAbi.OutCellBytes);

        if (!RangeFits(
            error: out boundsError,
            length: outLength,
            memoryLength: memoryLength,
            name: "out ring",
            start: outPtr
        )) {
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — {boundsError}"
            );
            return false;
        }

        regions[regionCount++] = (outPtr, (outPtr + outLength), "out ring");

        var inLength = (((long)inCap) * AddonAbi.InCellBytes);

        if (!RangeFits(
            error: out boundsError,
            length: inLength,
            memoryLength: memoryLength,
            name: "in ring",
            start: inPtr
        )) {
            SetFault(
                kind: AddonFaultKind.BadExport,
                reason: $"BadExport — {boundsError}"
            );
            return false;
        }

        regions[regionCount++] = (inPtr, (inPtr + inLength), "in ring");

        // The input channel's declared channel-name table, decoded through the consumer-supplied resolver — the
        // channel table is lane knowledge this core deliberately does not reference (see IAddonChannelResolver).
        // Entries are variable-length (a length byte plus that many UTF-8 bytes), so unlike every other region
        // here the table's byte length is not known before decode: the bounds check below only proves the table
        // STARTS inside memory, and the reader itself bounds every read against the remainder, reporting how many
        // bytes it actually consumed for the overlap sweep.
        for (var index = 0; (index < channelsCount); ++index) {
            var channel = m_channels[index];

            if (channel.Kind != AddonChannelKind.Input) {
                continue;
            }

            if (!RangeFits(
                error: out boundsError,
                length: 0,
                memoryLength: memoryLength,
                name: "channel names",
                start: ((int)channel.VerbTablePtr)
            )) {
                SetFault(
                    kind: AddonFaultKind.BadExport,
                    reason: $"BadExport — {boundsError}"
                );
                return false;
            }

            var nameTableSpan = memory.GetSpan(
                address: ((int)channel.VerbTablePtr),
                length: ((int)(memoryLength - channel.VerbTablePtr))
            );

            if (!AddonChannelNameTableReader.TryDecode(
                consumedBytes: out var consumedBytes,
                count: channel.VerbCount,
                destination: m_channelBindings,
                error: out var channelNameError,
                errorIndex: out _,
                resolver: m_channelResolver!,
                source: nameTableSpan
            )) {
                SetFault(
                    kind: AddonFaultKind.BadExport,
                    reason: $"BadExport — {channelNameError}"
                );
                return false;
            }

            regions[regionCount++] = (channel.VerbTablePtr, (channel.VerbTablePtr + consumedBytes), "channel names");

            m_channelBindingCount = channel.VerbCount;
        }

        for (var first = 0; (first < regionCount); ++first) {
            for (var second = (first + 1); (second < regionCount); ++second) {
                if (
                    (regions[first].Start < regions[second].End) &&
                    (regions[second].Start < regions[first].End)
                ) {
                    SetFault(
                        kind: AddonFaultKind.BadExport,
                        reason: $"BadExport — {regions[first].Name} region [{regions[first].Start}, {regions[first].End}) overlaps {regions[second].Name} region [{regions[second].Start}, {regions[second].End})"
                    );
                    return false;
                }
            }
        }

        m_inCap = inCap;
        m_inPtr = inPtr;
        m_initAction = instance.GetAction(name: AddonAbi.Exports.Init);
        m_memory = memory;
        m_onTick = instance.GetFunction<int, int>(name: AddonAbi.Exports.OnTick)!;
        m_outCap = outCap;
        m_outPtr = outPtr;
        return true;
    }

    /// <summary>Admits the instance to the tick set: runs the guest's optional <c>puck_init</c> under the fuel
    /// budget and marks the instance tickable. Called exactly once per store, after the consumer's own mount gates
    /// (attenuation, quota, disclosure) — see the type remarks. A trap during <c>puck_init</c> faults the instance
    /// exactly like a tick trap. No-op on a faulted or disabled instance.</summary>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    /// <exception cref="InvalidOperationException">The instance is already admitted.</exception>
    public void Admit() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (m_state != AddonState.Enabled) {
            return;
        }

        if (m_admitted) {
            throw new InvalidOperationException(message: $"addon {m_name} is already admitted");
        }

        var store = m_store!;

        store.Fuel = ((ulong)m_fuelPerTick);

        try {
            var init = m_initAction;

            if (init is not null) {
                init();
            }

            GC.KeepAlive(obj: store);
        } catch (TrapException trap) {
            var kind = Classify(code: trap.Type);

            m_lastFuelConsumed = FuelConsumed();
            SetFault(
                kind: kind,
                reason: $"{kind} during puck_init ({trap.Type})"
            );
            return;
        }

        m_lastFuelConsumed = FuelConsumed();
        m_admitted = true;
    }
    /// <summary>Administratively disables the addon; it is skipped every tick until re-enabled.</summary>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public void Disable() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        m_lastCount = 0;
        m_state = AddonState.Disabled;
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
    /// <summary>Re-enables the addon by disposing any prior store and instantiating a fresh one from the cached
    /// module — a clean reset to the module's initial state, back to the unadmitted phase (the consumer re-runs its
    /// gates and calls <see cref="Admit"/> again). A load-faulted addon (no module) stays faulted.</summary>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public void Enable() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (
            (m_engine is null) ||
            (m_module is null)
        ) {
            return;
        }

        Instantiate();
    }
    /// <summary>Escalates a consumer-layer protocol violation (a verb outside the declared vocabulary, a payload
    /// outside its domain) into the same sticky fault a structural decode error produces — the whole-batch-refused,
    /// nothing-committed posture. The caller supplies the attributed reason, offending ordinal included.</summary>
    /// <param name="reason">The refusal reason, naming the offending cell ordinal.</param>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public void FaultProtocol(string reason) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        SetFault(
            kind: AddonFaultKind.DecodeError,
            reason: $"DecodeError — {reason}"
        );
    }
    /// <summary>Drives the addon once: writes the input-cell batch into the guest's input ring, sets the fuel
    /// budget, invokes <c>puck_on_tick(count)</c>, and structurally decodes the returned output cells. Faults are
    /// sticky; a faulted or disabled addon short-circuits.</summary>
    /// <param name="input">The host-composed input batch, at most <see cref="InputCellCapacity"/> cells — the
    /// caller owns the budget rule and staying within it.</param>
    /// <returns>The tick outcome; on success read <see cref="OutCells"/> for the decoded batch.</returns>
    /// <exception cref="InvalidOperationException">The instance was never admitted — a host sequencing bug, never a
    /// guest fault.</exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> exceeds the guest's declared input capacity — a
    /// host budget bug, never a guest fault.</exception>
    public AddonTickResult Tick(ReadOnlySpan<AddonInCell> input) {
        if (m_state != AddonState.Enabled) {
            return AddonTickResult.Faulted(fault: m_fault);
        }

        if (!m_admitted) {
            throw new InvalidOperationException(message: $"addon {m_name} ticked before Admit — the mount sequence was not honored");
        }

        if (input.Length > m_inCap) {
            throw new ArgumentException(
                message: $"input batch {input.Length} exceeds the guest's declared capacity {m_inCap} — the caller owns the budget rule",
                paramName: nameof(input)
            );
        }

        var memory = m_memory!;
        var onTick = m_onTick!;
        var store = m_store!;
        var inRegion = memory.GetSpan(
            address: m_inPtr,
            length: (input.Length * AddonAbi.InCellBytes)
        );

        for (var index = 0; (index < input.Length); ++index) {
            AddonInCellWriter.Write(
                destination: inRegion.Slice(
                    length: AddonAbi.InCellBytes,
                    start: (index * AddonAbi.InCellBytes)
                ),
                cell: in input[index]
            );
        }

        // Zeroed before every tick so a guest that under-writes its returned count can only ever replay an all-zero
        // cell — which Kind = 0 makes MALFORMED under this ABI, so a lying count faults instead of replaying a
        // benign stale record. Nothing else clears this region.
        memory.GetSpan(
            address: m_outPtr,
            length: (m_outCap * AddonAbi.OutCellBytes)
        ).Clear();
        store.Fuel = ((ulong)m_fuelPerTick);

        int count;

        try {
            count = onTick(arg: input.Length);
        } catch (TrapException trap) {
            var kind = Classify(code: trap.Type);
            var consumed = FuelConsumed();

            m_lastFuelConsumed = consumed;
            SetFault(
                kind: kind,
                reason: TrapReason(
                    kind: kind,
                    trap: trap
                )
            );
            GC.KeepAlive(obj: store);
            return AddonTickResult.Faulted(
                fault: m_fault,
                fuelConsumed: consumed
            );
        }

        GC.KeepAlive(obj: store);

        var fuelConsumed = FuelConsumed();

        m_lastFuelConsumed = fuelConsumed;

        if (((uint)count) > ((uint)m_outCap)) {
            SetFault(
                kind: AddonFaultKind.DecodeError,
                reason: $"DecodeError — puck_on_tick returned {count}, cap {m_outCap}"
            );
            return AddonTickResult.Faulted(
                fault: m_fault,
                fuelConsumed: fuelConsumed
            );
        }

        var cells = memory.GetSpan(
            address: m_outPtr,
            length: (count * AddonAbi.OutCellBytes)
        );

        if (!AddonOutCellReader.TryDecode(
            channelCount: m_channelCount,
            count: count,
            destination: m_decoded,
            error: out var decodeError,
            errorIndex: out var errorIndex,
            source: cells
        )) {
            SetFault(
                kind: AddonFaultKind.DecodeError,
                reason: $"DecodeError — cell {errorIndex} {decodeError}"
            );
            return AddonTickResult.Faulted(
                fault: m_fault,
                fuelConsumed: fuelConsumed
            );
        }

        m_lastCount = count;
        return AddonTickResult.Ok(
            cellCount: count,
            fuelConsumed: fuelConsumed
        );
    }
    /// <summary>Reads <paramref name="length"/> bytes at <paramref name="pointer"/> out of the guest's linear
    /// memory into <paramref name="destination"/>, immediately — no live span is ever handed back, so nothing
    /// outlives this call that could observe a later guest write. The addon mutation seam's stage-5 pointer-safety
    /// copy: both <paramref name="pointer"/> and <paramref name="length"/> are guest-supplied and cross the ABI as
    /// signed <c>i64</c> wire lanes reinterpreted unsigned, so every bound is checked before any read — negative,
    /// over-capacity, and overflowing-end are all refused the identical way, never truncated into something that
    /// happens to fit.</summary>
    /// <param name="pointer">The guest-memory byte offset (an unsigned value carried in a signed wire lane).</param>
    /// <param name="length">The byte count to copy (an unsigned value carried in a signed wire lane); must not
    /// exceed <paramref name="destination"/>'s length.</param>
    /// <param name="destination">The host-owned buffer to copy into.</param>
    /// <param name="error">The refusal reason, on failure; empty on success.</param>
    /// <returns><see langword="true"/> when the whole range was in bounds and copied.</returns>
    /// <exception cref="ObjectDisposedException">The instance has been disposed.</exception>
    public bool TryCopyMemory(long pointer, long length, Span<byte> destination, out string error) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (
            (pointer < 0L) ||
            (pointer > uint.MaxValue)
        ) {
            error = $"pointer {pointer} is outside the guest's addressable range";
            return false;
        }

        if (
            (length < 0L) ||
            (length > destination.Length)
        ) {
            error = $"length {length} exceeds the destination capacity {destination.Length}";
            return false;
        }

        if (m_memory is not { } memory) {
            error = "no guest memory is mounted";
            return false;
        }

        var memoryLength = memory.GetLength();
        // Overflow-checked end: pointer is bounded to uint.MaxValue and length to destination.Length above, so this
        // sum cannot overflow long — the check exists to name the SPECIFIC out-of-bounds range in the refusal, not
        // to guard arithmetic that could wrap.
        var end = (pointer + length);

        if (end > memoryLength) {
            error = $"[{pointer}, {end}) exceeds guest memory {memoryLength}";
            return false;
        }

        memory.GetSpan(
            address: ((int)pointer),
            length: ((int)length)
        ).CopyTo(destination: destination[..((int)length)]);
        error = "";
        return true;
    }
}
