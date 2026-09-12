using System.Numerics;
using Puck.Abstractions.Machines;

namespace Puck.World.Server;

/// <summary>One declared screen's machine-side live state for the <c>screen.state</c> verb — whether a machine is
/// assigned, the engine that hosts it, the stepped-frame count, and the boot fault (a declared machine whose content
/// file was missing, an unresolved engine, rejected options), if any. Carries no GPU-facing fields (no image-view
/// handle, no light) — those are presentation reads over <see cref="IWorldMachineHost.Handle"/>/
/// <see cref="IWorldMachineHost.Light"/>, which <c>Puck.World.WorldScreenBinder</c> (a pure reader) composes into the
/// same console line.</summary>
/// <param name="Assigned">Whether a machine is booted on the screen.</param>
/// <param name="Engine">The screen-machine engine id hosting the machine (meaningful only when <paramref name="Assigned"/>).</param>
/// <param name="FramesStepped">How many frames the machine has stepped since it booted.</param>
/// <param name="PendingSteps">Accepted queued-machine steps not yet completed; zero for synchronous machines.</param>
/// <param name="MaximumPendingSteps">The queued machine's finite pending-segment capacity; zero for synchronous
/// machines.</param>
/// <param name="BackpressureEvents">How many queued submissions waited for capacity since the current content was
/// loaded; zero for synchronous machines.</param>
/// <param name="Fault">A slot's live fault (a missing content file, an unresolved engine, rejected options), or
/// <see langword="null"/>.</param>
/// <param name="Cartridge">The cartridge document the live machine's content was compiled from, or
/// <see langword="null"/> when the content booted as read (a ROM image) or no machine is assigned.</param>
public readonly record struct WorldMachineState(bool Assigned, string? Engine, long FramesStepped,
    long PendingSteps, int MaximumPendingSteps, long BackpressureEvents, string? Fault, WorldMachineCartridge? Cartridge = null);

/// <summary>A compiled cartridge document behind a booted machine — the source the host compiled at bind and the
/// two hashes that pin it, echoed by <c>screen.state</c> as <c>cartridge &lt;path&gt; hash &lt;source&gt; rom
/// &lt;rom&gt;</c>. The file's own content pin (the replay CAS signature) is separate: it covers the bytes read
/// off disk, while <see cref="SourceHash"/> is the document's canonical identity and <see cref="RomHash"/> the
/// compiled image's, so two spellings of one canonical document boot one ROM and say so.</summary>
/// <param name="Path">The content path as declared or inserted.</param>
/// <param name="SourceHash">The canonical source hash the forge computed.</param>
/// <param name="RomHash">The compiled image's content hash, in the same <c>sha256-64/</c> form as the file pin.</param>
public readonly record struct WorldMachineCartridge(string Path, string SourceHash, string RomHash);

/// <summary>
/// The seam <see cref="WorldServer"/> — and every replay/instance-host caller that boots a shadow world of its own —
/// pumps every declared screen's machine through, mirroring <see cref="IWorldAddonHost"/>'s own shape for the WASM
/// guest seam. <c>Puck.World.Addons.Machines.WorldMachineHost</c> is the one implementation; it is constructed from
/// the composition root (where the concrete emulator/instrument engines are known) and handed to
/// <see cref="WorldServer"/> as a peer singleton, never built by this project directly — the whole reason this
/// interface exists is that <c>Puck.World.Server</c> must stay free of the emulator cores and the renderer's
/// projects (a machine is a mounted guest, like a WASM addon), so a browser or silo build of Server needs neither.
/// Callers should not need to reach past this interface into the concrete host.
/// </summary>
public interface IWorldMachineHost : IWorldExtensionRuntime, IWorldMachineMemoryPeek {
    /// <summary>Gets the same host-local catalog used for machine construction and document admission.</summary>
    IMachineValidationCatalog ValidationCatalog { get; }
    /// <summary>Gets the declared machine names, including instances without display consumers.</summary>
    IEnumerable<string> InstanceNames { get; }
    /// <summary>Reads execution and generation state by instance identity.</summary>
    /// <param name="name">The authored instance name.</param>
    WorldMachineInstanceState? InstanceState(string name);
    /// <summary>Resolves one named instance's video output without creating or advancing it.</summary>
    /// <param name="instance">The authored instance name.</param>
    /// <param name="output">The provider's output name.</param>
    IMachineVideoOutput? VideoOutput(string instance, string output);
    /// <summary>Resolves one named audio stream; consumers of that stream share a single drain.</summary>
    /// <param name="instance">The authored instance name.</param>
    /// <param name="output">The provider's output name.</param>
    IAudioMachine? AudioOutput(string instance, string output);
    /// <summary>Captures the host-owned current named declarations in stable instance order.</summary>
    IReadOnlyList<WorldMachine> CaptureInstances();
    /// <summary>Stages one provider operation without changing the live runtime or declaration.</summary>
    bool TryPrepareOperation(string instance, ulong expectedGeneration, MachineOperationRequest request,
        out IWorldMachineOperationPreparedPlan? plan, out MachineOperationResult refusal);
    /// <summary>Applies a prepared operation at the host barrier and adopts its canonical declaration on success.</summary>
    MachineOperationResult TryCommitOperation(IWorldMachineOperationPreparedPlan plan);
    /// <summary>Observes coherent hardware state with explicit availability and no side effects.</summary>
    /// <param name="instance">The authored instance name.</param>
    /// <param name="address">The provider space, unsigned address, and access width.</param>
    MachineAccessResult Inspect(string instance, MachineMemoryAddress address);
    /// <summary>Resolves a validated binding's raw or exported-symbol address.</summary>
    /// <param name="instance">The authored instance.</param>
    /// <param name="binding">The named binding within the instance.</param>
    /// <param name="address">The resolved scalar address, on success.</param>
    bool TryBindingAddress(string instance, string binding, out MachineMemoryAddress address);
    /// <summary>Resolves a prepared content symbol on a named machine to its bus address.</summary>
    /// <param name="instance">The named machine instance.</param>
    /// <param name="symbol">The exported content symbol.</param>
    /// <param name="address">The resolved bus address.</param>
    /// <returns><see langword="true"/> when the symbol is available on the live instance.</returns>
    bool TryResolveSymbol(string instance, string symbol, out int address);
    /// <summary>Applies an already authorized write, refusing stale instance generations. External callers must
    /// use the ordered authority door; the server's deterministic bindings use this execution seam directly.</summary>
    /// <param name="instance">The target instance.</param>
    /// <param name="generation">The expected incarnation.</param>
    /// <param name="address">The resolved scalar address.</param>
    /// <param name="value">The converted scalar bit pattern.</param>
    /// <param name="mode">Patch or bus semantics.</param>
    MachineAccessResult WriteHardware(string instance, ulong generation, MachineMemoryAddress address, ulong value, MachineAccessMode mode);
    /// <summary>A machine's core is re-executed at the same tick boundaries off the same pinned content and pad
    /// inputs, exactly like a WASM guest — see <see cref="IWorldExtensionRuntime.ReplayPolicy"/>.</summary>
    WorldExtensionReplayPolicy IWorldExtensionRuntime.ReplayPolicy => WorldExtensionReplayPolicy.Recomputed;

    /// <summary>Gets a value indicating whether any booted machine has ever had a step/segment actually submitted to it — set the
    /// instant <see cref="Advance"/> steps a machine (individually or through a live cable link), never cleared. The
    /// boot-anchored replay arm predicate <see cref="WorldServer.AnyMachineEverPumped"/> reads (mirroring
    /// <c>WorldAddonRuntime.AnyEverPumped</c>'s identical shape): offline replay reconstructs a machine's boot
    /// image, never its accumulated core state once real ticks have run it.</summary>
    bool AnyEverPumped { get; }
    /// <summary>Gets or sets the machine-lifecycle tap: invoked with <c>(index, faulted)</c> on every runtime machine boot outcome
    /// — <see langword="false"/> when a machine boots onto a slot, <see langword="true"/> when a boot attempt faults
    /// (missing content, unresolved engine, rejected options). Constructor-time declared boots precede any wiring and
    /// do not fire.</summary>
    Action<int, bool>? MachineLifecycleTap { get; set; }
    /// <summary>Gets every screen index currently carrying a booted machine — presentation's publish-loop enumeration.</summary>
    IEnumerable<int> MachineScreenIndices { get; }

    /// <summary>Advances every booted machine by one host-owned fixed simulation step, fed by
    /// <paramref name="pads"/> — <see cref="WorldEngagement.BuildPadSnapshot"/>'s result, read directly in-process
    /// (no client/wire round-trip; see <see cref="WorldServer.Step"/>'s call site, right after
    /// <see cref="WorldEngagement.FoldTick"/>). A live cable link steps as one unit with its members' merged pads in
    /// cable order. The exact-rational T-cycle bridge (a machine's own internal tick-to-cycle conversion) is
    /// preserved verbatim: <paramref name="stepTicks"/> is forwarded to the machine exactly as received — cart RTC
    /// still derives from this tick budget, never wall clock.</summary>
    /// <param name="stepTicks">The exact engine-tick budget of one fixed simulation step.</param>
    /// <param name="pads">This tick's per-screen merged engagement pad lane.</param>
    void Advance(ulong stepTicks, ReadOnlyMemory<ScreenPadSnapshot> pads);
    /// <summary>Returns the live machine resolved from a screen's named producer or legacy slot as its audio drain seam,
    /// or <see langword="null"/> when no machine (or no capability) is available.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    IAudioMachine? AudioMachine(int index);
    /// <summary>Returns the live cable-link set as derived groups (cable order preserved) — the <c>world.save</c>
    /// fold source: each group folds back into its member screens rows' machine-source cable ports.</summary>
    IReadOnlyList<WorldMachineCableGroup> CaptureLinks();
    /// <summary>Describes every live cable link in one line (the <c>screen.links</c> query), or <c>none</c>.</summary>
    string DescribeLinks();
    /// <summary>Returns the current same-device framebuffer image-view handle bound to a screen index, or 0 when unbound, not
    /// declared, or the machine has not published a frame yet — the presentation read.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    nint Handle(int index);
    /// <summary>Determines whether a screen-machine engine is registered under <paramref name="engineId"/>.</summary>
    /// <param name="engineId">The candidate engine id.</param>
    bool HasEngine(string engineId);
    /// <summary>Determines whether the screen index resolves to a live named producer or legacy screen-owned machine.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    bool HasMachine(int index);
    /// <summary>Returns the live machine's authored tempo, in engine ticks per beat, when the screen slot carries a
    /// booted machine with the <see cref="IInstrumentClockSource"/> capability — <see langword="null"/> for an empty
    /// slot, a machine without the capability, or a capability reporting zero (no content loaded).</summary>
    /// <param name="index">The engine screen-surface index.</param>
    long? InstrumentTicksPerBeat(int index);
    /// <summary>Returns the live named machine's authored tempo, in engine ticks per beat, when it exposes the
    /// <see cref="IInstrumentClockSource"/> capability. The lookup is independent of display consumers.</summary>
    /// <param name="instance">The authored machine instance name.</param>
    long? InstrumentTicksPerBeat(string instance);
    /// <summary>Returns the room light a booted machine emits (its framebuffer average), or zero for no machine — the
    /// presentation read.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    Vector3 Light(int index);
    /// <summary>Returns the cable link a screen currently belongs to (by name), or <see langword="null"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    string? LinkOf(int index);
    /// <summary>Returns the runtime currently bound to a screen index, or null.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    IMachineRuntime? MachineAt(int index);
    /// <summary>Returns the optional video output selected by a screen, or null when no signal is available.</summary>
    /// <param name="index">The screen's derived render slot.</param>
    IMachineVideoOutput? VideoOutput(int index);
    /// <summary>Reconciles the declared cable links to a mutated <c>links</c> section.</summary>
    /// <param name="links">The declared cable groups, derived from the live definition's machine sources
    /// (<c>WorldDefinition.MachineCableGroups()</c>).</param>
    void ReconcileLinks(IReadOnlyList<WorldMachineCableGroup> links);
    /// <summary>Prepares the machine-runtime delta between <paramref name="current"/> and <paramref name="candidate"/>.</summary>
    /// <param name="current">The current live definition, or <see langword="null"/> at boot.</param>
    /// <param name="candidate">The candidate definition to prepare against.</param>
    /// <param name="plan">The prepared plan on success; must be disposed if not committed.</param>
    /// <param name="reason">A refusal reason on failure.</param>
    /// <returns><see langword="true"/> when preparation succeeded.</returns>
    bool TryPrepare(WorldDefinition? current, WorldDefinition candidate, out IWorldMachinePreparedPlan? plan, out string? reason);
    /// <summary>Commits a previously prepared plan.</summary>
    /// <param name="plan">The prepared plan.</param>
    void Commit(IWorldMachinePreparedPlan plan);
    /// <summary>Finishes and publishes a committed plan.</summary>
    /// <param name="plan">The committed plan.</param>
    void Finish(IWorldMachinePreparedPlan plan);
    /// <summary>Reconciles the host's machine slots to a mutated screen list — the live-application half of an
    /// <c>UpsertScreen</c>/<c>RemoveScreen</c> world mutation, called from <see cref="WorldServer"/>'s own Install
    /// path when the definition changes.</summary>
    /// <param name="screens">The mutated screen list (the live definition's screens).</param>
    /// <returns>The screen indices removed this call — feed each to <see cref="WorldEngagement.DissolveScreen"/>.</returns>
    IReadOnlyList<int> ReconcileScreens(IReadOnlyList<WorldScreen> screens);
    /// <summary>Resolves a state-symbol name on the machine at <paramref name="index"/> to its bus address.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="symbol">The symbol name.</param>
    /// <param name="address">The resolved bus address.</param>
    /// <returns><see langword="true"/> when the symbol was found on the booted machine's cartridge.</returns>
    bool TryResolveSymbol(int index, string symbol, out int address);
    /// <summary>Moves declared relative machine content resolution to a new world document.</summary>
    /// <param name="documentPath">The installed world document path.</param>
    void SetDocumentPath(string? documentPath);
    /// <summary>Returns the live state of a declared screen's machine for <c>screen.state</c>, or <see langword="null"/> when
    /// the index is not a declared screen.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    WorldMachineState? State(int index);
    /// <summary>Ejects a screen's live machine. Fails for an undeclared screen or a slot with no machine to eject.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <returns>Whether the eject succeeded, and a message describing the outcome.</returns>
    (bool Ok, string Message) TryEject(int index);
    /// <summary>Boots (or live-swaps) a machine onto a declared screen from a content file path. Any existing machine
    /// on the slot is cleared and replaced. Fails loudly (a message, no crash) for an undeclared screen, an
    /// unresolved engine, an unreadable content file, or an options string the engine rejects.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="contentPath">The content file (a cartridge ROM) to boot.</param>
    /// <param name="engineId">The screen-machine engine id, or <see langword="null"/> for the sole-registered default.</param>
    /// <param name="options">The engine-specific options string, or <see langword="null"/> for the engine's defaults.</param>
    /// <param name="expectedContentHash">Replay only: the CAS pin a recorded tape entry carries. <see langword="null"/>
    /// (the default) is the live path.</param>
    /// <returns>Whether the insert succeeded, a message describing the outcome, and the content signature actually
    /// observed.</returns>
    (bool Ok, string Message, string? ContentHash) TryInsert(int index, string contentPath, string? engineId, string? options, string? expectedContentHash = null);
    /// <summary>Establishes (or reports dormant) a runtime cable link over two or more declared screens.</summary>
    /// <param name="name">The link's stable name.</param>
    /// <param name="members">The engine screen indices in cable order.</param>
    /// <returns>Whether the link row was recorded, and a message describing live/dormant state.</returns>
    (bool Ok, string Message) TryLink(string name, IReadOnlyList<int> members);
    /// <summary>Returns the screen's live magazine and 0-based selector, or <see langword="false"/> when the screen declares
    /// no magazine.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="selected">The live 0-based selector.</param>
    /// <param name="magazine">The screen's magazine.</param>
    bool TryMagazine(int index, out int selected, out WorldScreenMagazine magazine);
    /// <summary>Reads one memory byte from a screen's machine (the <c>screen.peek</c> read) — a side-effect-free host
    /// poll through the machine's optional <see cref="IMachineMemoryPeek"/> capability.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="address">A machine-defined memory address.</param>
    /// <param name="value">The byte read, or 0 on failure.</param>
    /// <returns>A success flag and, on failure, a message.</returns>
    (bool Ok, string Message) TryPeekMessage(int index, int address, out byte value);
    /// <summary>Forces one memory byte into a screen's machine — a <c>screens[].memory</c> write binding's poke
    /// (<c>WorldServer.MachineMemory.cs</c>), through the same host poll <see cref="TryPeekMessage"/> uses rather
    /// than a direct machine reference. A value outside the machine's writable space, or an unassigned machine, is
    /// a silent no-op at the machine's own <see cref="IMachineMemoryPeek.PokeByte"/> — this seam reports success only
    /// when a machine is present to receive the write, never whether the byte actually landed inside that machine's
    /// own writable range.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="address">A machine-defined memory address.</param>
    /// <param name="value">The byte to store.</param>
    /// <returns>A success flag and, on failure, a message.</returns>
    (bool Ok, string Message) TryPokeMessage(int index, int address, byte value);
    /// <summary>Reads a live link's member screens by name.</summary>
    /// <param name="name">The link name.</param>
    /// <param name="members">The member screen indices in cable order, on success.</param>
    /// <returns>Whether a link of that name is live.</returns>
    bool TryReadLinkMembers(string name, out IReadOnlyList<int> members);
    /// <summary>Reads back the live machine insert on a screen index — its engine id, content path, and options — so
    /// <c>world.save</c> can fold a runtime <c>screen.insert</c> into that screen row's machine source.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="engine">The engine id that booted the live machine.</param>
    /// <param name="contentPath">The content file (a cartridge ROM) the live machine booted.</param>
    /// <param name="options">The options string the live machine booted with, or <see langword="null"/>.</param>
    bool TryReadMachineInsert(int index, out string engine, out string contentPath, out string? options);
    /// <summary>Reads a screen's machine's current options string, or <see langword="false"/> when the screen has no
    /// reconfigurable machine.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="options">The current options string.</param>
    bool TryReadOptions(int index, out string options);
    /// <summary>Reconfigures a screen's live machine across the engine's options vocabulary (dmg↔cgb↔agb with no
    /// reboot).</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="options">The engine-specific options string to retarget to.</param>
    /// <returns>Whether the reconfigure succeeded, and a message describing the outcome.</returns>
    (bool Ok, string Message) TryReconfigure(int index, string? options);
    /// <summary>Points the screen's magazine selector at <paramref name="entry"/>, booting it when it names a machine
    /// row.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="entry">The 0-based magazine entry to select.</param>
    /// <param name="expectedContentHash">Replay only: the CAS pin a recorded tape entry carries. <see langword="null"/>
    /// for the live path.</param>
    /// <returns>Whether the selection (and, for a machine entry, the boot) succeeded, a message, and — for a machine
    /// entry — the observed content signature.</returns>
    (bool Ok, string Message, string? ContentHash) TrySelect(int index, int entry, string? expectedContentHash = null);
    /// <summary>Severs a runtime cable link by name. Fails when no link of that name is live.</summary>
    /// <param name="name">The link name.</param>
    /// <returns>Whether the link existed, and a message.</returns>
    (bool Ok, string Message) TryUnlink(string name);
}
