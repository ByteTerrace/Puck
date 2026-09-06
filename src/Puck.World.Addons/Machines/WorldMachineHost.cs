using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.Audio.Mixing;

namespace Puck.World.Server;

/// <summary>
/// Owns every declared screen's live machine: booting, stepping, cable-linking, memory-peeking, and reconfiguring a
/// deterministic <see cref="IScreenMachine"/> are all server-side, so ROM state is sim state and a headless boot's
/// cabinets run exactly like a windowed one's. Camera/capture/window-capture/jumbotron-view/test-pattern screen
/// sources are deliberately outside this type's concern — they stay genuinely presentation, composed by
/// <c>Puck.World.WorldScreenBinder</c>, which reads this type's machine outputs (framebuffer handle, light, audio)
/// as a pure reader, not an owner of machine state. Screen index is machine identity for screen-hosted machines,
/// matching the document convention (<c>docs</c>'s "screens are position-addressed"). The concrete engines (the
/// emulator cores, the Tune instrument) are named only here and in <see cref="Puck.World.WorldScreenMachineEngines"/>
/// — <see cref="WorldServer"/> and every other <c>Puck.World.Server</c> type reach a booted machine only through
/// <see cref="IWorldMachineHost"/>.
/// </summary>
/// <remarks>Single-threaded, like every other simulation type here: constructed once at boot (or replay
/// rehydration), then only ever touched from <see cref="WorldServer.Step"/>'s tick thread (<see cref="Advance"/>) or
/// a synchronously-applied <see cref="WorldServer"/> screen-op apply (<see cref="TryInsert"/> and friends), so no
/// lock guards this state. Holds native machine resources (an <see cref="IScreenMachine"/> may own emulator-core
/// memory) — <see cref="Dispose"/> tears every booted machine and live link down; the composition root registers
/// this type as its own DI singleton (not a private field of <see cref="WorldServer"/>) precisely so the container
/// disposes it.</remarks>
public sealed class WorldMachineHost : IWorldMachineHost {
    /// <summary>The CAS signature <see cref="TryBootMachine"/> records when it could not read the content file at
    /// all (missing, unreadable) — distinct from any real <c>sha256-64/…</c> hash so it can never collide with one.
    /// A recorded op pinning this sentinel demands the same absence on replay; a file that has since appeared (or
    /// become readable) refuses by name, exactly like a changed hash does.</summary>
    public const string ContentAbsentSignature = "absent";

    private readonly WorldExtensionRegistry<IScreenMachineEngine> m_engines;

    private bool m_disposed;
    private string? m_documentDirectory;
    private bool m_documentDirectoryChanged;

    private readonly Dictionary<int, MachineSlot> m_slots = new();
    private readonly Dictionary<string, LinkEntry> m_links = new(comparer: StringComparer.Ordinal);
    private readonly List<int> m_reconcileRemovals = new();
    private readonly WorldOutputHub? m_narrationHub;

    /// <summary>Initializes the host over the world's declared screens: a booted machine for each declared machine
    /// screen whose content file exists and whose engine resolves (a missing file or unknown engine leaves the slot
    /// unbound with a visible fault — loud data, no crash).</summary>
    /// <param name="screens">The world's diegetic screens (<see cref="WorldDefinition.Screens"/>).</param>
    /// <param name="engines">The registered screen-machine engines (DI-collected) a declared or inserted machine
    /// resolves against.</param>
    /// <param name="documentPath">The world document path used to resolve declared relative content paths.</param>
    /// <param name="narrationHub">The hub this host's narration is delivered through, or <see langword="null"/> to
    /// leave it undelivered — this host carries no single owning server of its own.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Two engines register one id — a composition-root error, thrown at boot
    /// rather than resolved last-writer-wins.</exception>
    public WorldMachineHost(IReadOnlyList<WorldScreen> screens, IEnumerable<IScreenMachineEngine> engines, string? documentPath = null, WorldOutputHub? narrationHub = null) {
        ArgumentNullException.ThrowIfNull(argument: screens);
        ArgumentNullException.ThrowIfNull(argument: engines);

        m_narrationHub = narrationHub;

        // The same registry the load-time key check reads through WorldExtensionVocabularyHook, so a key that
        // validated is a key this host can resolve.
        m_engines = new WorldExtensionRegistry<IScreenMachineEngine>(
            extensions: engines,
            keyOf: static engine => engine.Id
        );
        m_documentDirectory = DocumentDirectory(documentPath: documentPath);

        foreach (var screen in screens) {
            var slot = new MachineSlot { DeclaredSource = screen.Source, Index = screen.Index, Magazine = screen.Magazine, SelectedEntry = (screen.Magazine?.Selected ?? 0) };

            if (screen.Source is WorldScreenSource.Machine machine) {
                BootDeclaredMachine(
                    machine: machine,
                    slot: slot
                );
            }

            m_slots[screen.Index] = slot;
        }
    }

    private void BootDeclaredMachine(MachineSlot slot, WorldScreenSource.Machine machine) {
        if (!m_engines.TryGet(
            key: machine.Engine,
            extension: out var engine
        )) {
            slot.DeclaredFault = $"no screen-machine engine '{machine.Engine}'";
            if (m_narrationHub is { HasNarrationSink: true }) {
                m_narrationHub?.Narrate(
                    channel: "world.screen",
                    text: $"[world.screen: {slot.Index} {slot.DeclaredFault}]"
                );
            }

            return;
        }

        if (!TryReadContent(
            contentPath: machine.ContentPath,
            documentRelative: true,
            content: out var content,
            fault: out var fault
        )) {
            slot.DeclaredFault = fault;
            if (m_narrationHub is { HasNarrationSink: true }) {
                m_narrationHub?.Narrate(
                    channel: "world.screen",
                    text: $"[world.screen: {slot.Index} {slot.DeclaredFault}]"
                );
            }

            return;
        }

        try {
            slot.Machine = engine.Create(
                options: machine.Options,
                contentBytes: content,
                savePath: null,
                audioSampleRate: MachineAudioRate.SampleRate
            );
            slot.MachineEngine = engine.Id;
            slot.MachineContentPath = machine.ContentPath;
            slot.MachineSourceEngine = machine.Engine;
            slot.MachineOptions = machine.Options;
            slot.MachineContentHash = WorldDefinitionFileSource.ComputeContentHash(content: content);
        } catch (ArgumentException exception) {
            slot.DeclaredFault = exception.Message;
            if (m_narrationHub is { HasNarrationSink: true }) {
                m_narrationHub?.Narrate(
                    channel: "world.screen",
                    text: $"[world.screen: {slot.Index} {slot.DeclaredFault}]"
                );
            }
        }
    }
    private static bool DeclaresIndex(IReadOnlyList<WorldScreen> screens, int index) {
        foreach (var screen in screens) {
            if (screen.Index == index) {
                return true;
            }
        }

        return false;
    }
    private static string DescribeLink(LinkEntry entry) {
        var members = string.Join(
            separator: "+",
            values: entry.Members
        );

        return ((entry.Link is { } link)
            ? $"{entry.Name} {members} live transfers={link.CompletedTransfers}"
            : $"{entry.Name} {members} dormant ({(entry.DormantReason ?? "unestablishable")})"
        );
    }
    private static string? DocumentDirectory(string? documentPath) => ((documentPath is { Length: > 0 } path)
        ? Path.GetDirectoryName(path: Path.GetFullPath(path: path))
        : null
    );
    // The sparse pad lookup: WorldEngagement.BuildPadSnapshot() carries one entry per screen with at least one
    // player engaged, so a linear scan over the (typically tiny) active set costs nothing — the same shape the
    // pre-inversion WorldClient.EngagedPad used over the wire lane.
    private static MachinePadState EngagedPad(ReadOnlySpan<ScreenPadSnapshot> pads, int screenIndex) {
        foreach (ref readonly var pad in pads) {
            if (pad.ScreenIndex == screenIndex) {
                return pad.Pad;
            }
        }

        return MachinePadState.Neutral;
    }
    private void LeaveLink(int index) {
        if (
            m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) &&
            (slot.LinkName is { } name)
        ) {
            TeardownLink(name: name);
        }
    }
    private static bool MembersMatch(LinkEntry entry, IReadOnlyList<int> members) {
        if (entry.Members.Length != members.Count) {
            return false;
        }

        for (var index = 0; (index < members.Count); index++) {
            if (entry.Members[index] != members[index]) {
                return false;
            }
        }

        return true;
    }
    private void StepLiveLinks(ulong stepTicks, ReadOnlySpan<ScreenPadSnapshot> pads) {
        if (m_links.Count == 0) {
            return;
        }

        foreach (var entry in m_links.Values) {
            if (entry.Link is not { } link) {
                continue;
            }

            AnyEverPumped = true;

            var inputs = new MachinePadState[entry.Members.Length];

            for (var index = 0; (index < entry.Members.Length); index++) {
                inputs[index] = EngagedPad(
                    pads: pads,
                    screenIndex: entry.Members[index]
                );
            }

            link.Step(
                deltaTicks: stepTicks,
                inputs: inputs
            );

            foreach (var member in entry.Members) {
                if (
                    m_slots.TryGetValue(
                    key: member,
                    value: out var slot
                ) &&
                    (slot.Machine is IQueuedScreenMachine queued)
                ) {
                    slot.FramesStepped = queued.CompletedSteps;
                }
            }
        }
    }
    private void TeardownLink(string name) {
        if (!m_links.Remove(
            key: name,
            value: out var entry
        )) {
            return;
        }

        entry.Link?.Dispose();

        foreach (var member in entry.Members) {
            if (
                m_slots.TryGetValue(
                key: member,
                value: out var slot
            ) &&
                string.Equals(
                a: slot.LinkName,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                slot.LinkName = null;
            }
        }
    }
    // The shared boot sequence TryInsert and TrySelect's machine branch both funnel through. Order: read content
    // FIRST, producing a signature EVEN ON FAILURE (a real hash, or ContentAbsentSignature) -> compare against
    // expectedContentHash when replaying, refusing BY NAME on ANY disagreement (present-vs-absent in either
    // direction, or a changed hash) -> resolve engine (content is signed BEFORE this step, so an unresolved engine
    // still pins whatever it would have read — engine resolution failing is not a file-state exemption) ->
    // construct the machine, still reporting the signature even if construction itself throws (bad options) so a
    // content change between record and replay is caught even when the failure reason is downstream of the read.
    private (bool Ok, string Message, string? ContentHash) TryBootMachine(int index, MachineSlot slot, string contentPath, string? engineId, string? options, string? expectedContentHash, bool documentRelative) {
        if (!TryReadContent(
            content: out var content,
            contentPath: contentPath,
            documentRelative: documentRelative,
            fault: out var fault
        )) {
            const string Signature = ContentAbsentSignature;

            if (
                (expectedContentHash is { } expectedAbsence) &&
                !string.Equals(
                a: expectedAbsence,
                b: Signature,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return (Ok: false, Message: $"ScreenOpContentMismatch: '{contentPath}' {fault} now, but the recording pinned {expectedAbsence} — the file changed since it was captured", ContentHash: Signature);
            }

            MachineLifecycleTap?.Invoke(
                arg1: index,
                arg2: true
            );

            return (Ok: false, Message: fault!, ContentHash: Signature);
        }

        var contentHash = WorldDefinitionFileSource.ComputeContentHash(content: content);

        if (
            (expectedContentHash is { } expected) &&
            !string.Equals(
            a: expected,
            b: contentHash,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return (Ok: false, Message: $"ScreenOpContentMismatch: '{contentPath}' hashes to {contentHash}, the recording pinned {expected} — the file moved or changed since it was captured", ContentHash: contentHash);
        }

        if (!TryResolveEngine(
            engine: out var engine,
            engineId: engineId,
            error: out var engineError
        )) {
            MachineLifecycleTap?.Invoke(
                arg1: index,
                arg2: true
            );

            // Content was read and hashed before engine resolution, so this failure still pins the signature —
            // a replay whose file now hashes differently is caught here too, same as the construction-rejects
            // path below.
            return (Ok: false, Message: engineError, ContentHash: contentHash);
        }

        IScreenMachine created;

        try {
            created = engine.Create(
                audioSampleRate: MachineAudioRate.SampleRate,
                contentBytes: content,
                options: options,
                savePath: null
            );
        } catch (ArgumentException exception) {
            MachineLifecycleTap?.Invoke(
                arg1: index,
                arg2: true
            );

            // Still pin the hash: content WAS read and hashed even though construction rejected it (bad options),
            // so a replay whose file now hashes differently is still caught, never silently retried unpinned.
            return (Ok: false, Message: exception.Message, ContentHash: contentHash);
        }

        LeaveLink(index: index);
        slot.ClearMachine();
        slot.Machine = created;
        slot.MachineEngine = engine.Id;
        slot.MachineContentPath = contentPath;
        slot.MachineSourceEngine = ((engineId is { Length: > 0 })
            ? engineId
            : engine.Id
        );
        slot.MachineOptions = options;
        slot.MachineContentHash = contentHash;
        slot.DeclaredFault = null;
        slot.FramesStepped = 0;
        MachineLifecycleTap?.Invoke(
            arg1: index,
            arg2: false
        );

        return (Ok: true, Message: $"screen {index} booted {engine.Id} '{Path.GetFileName(path: contentPath)}'{(string.IsNullOrWhiteSpace(value: options)
            ? ""
            : $" ({options})")}", ContentHash: contentHash);
    }
    private (IMachineLink? Link, string? Reason) TryEstablishLink(IReadOnlyList<int> members) {
        var machines = new List<IScreenMachine>(capacity: members.Count);
        IMachineLinkingEngine? linkingEngine = null;
        string? engineId = null;

        foreach (var member in members) {
            var slot = m_slots[member];

            if (slot.Machine is not { } machine) {
                return (Link: null, Reason: $"screen {member} has no machine");
            }

            if (slot.MachineEngine is not { } id) {
                return (Link: null, Reason: $"screen {member}'s machine has no engine identity");
            }

            if (engineId is null) {
                engineId = id;

                if (
                    m_engines.TryGet(
                    extension: out var engine,
                    key: id
                ) &&
                    (engine is IMachineLinkingEngine linking)
                ) {
                    linkingEngine = linking;
                } else {
                    return (Link: null, Reason: $"engine '{id}' has no linking capability");
                }
            } else if (!string.Equals(
                a: engineId,
                b: id,
                comparisonType: StringComparison.Ordinal
            )) {
                return (Link: null, Reason: $"mixed engines ('{engineId}' and '{id}') cannot be cable-linked");
            }

            machines.Add(item: machine);
        }

        return (linkingEngine!.TryLink(
            machines: machines,
            out var link,
            out var reason
        )
            ? (Link: link, Reason: null)
            : (Link: null, Reason: reason)
        );
    }
    private bool TryReadContent(string contentPath, bool documentRelative, out byte[] content, out string? fault) {
        if (string.IsNullOrEmpty(value: contentPath)) {
            content = [];
            fault = "no content configured";

            return false;
        }

        string resolvedPath;

        try {
            resolvedPath = ((documentRelative && !Path.IsPathFullyQualified(path: contentPath) && (m_documentDirectory is { } directory))
                ? Path.GetFullPath(path: Path.Combine(
                    path1: directory,
                    path2: contentPath
                ))
                : Path.GetFullPath(path: contentPath)
            );
        } catch (Exception exception) when ((exception is ArgumentException or NotSupportedException or PathTooLongException)) {
            content = [];
            fault = $"content '{contentPath}' cannot be resolved ({exception.Message})";

            return false;
        }

        if (!File.Exists(path: resolvedPath)) {
            content = [];
            fault = $"content '{contentPath}' not found at '{resolvedPath}'";

            return false;
        }

        try {
            content = File.ReadAllBytes(path: resolvedPath);
            fault = null;

            return true;
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            content = [];
            fault = $"content '{contentPath}' at '{resolvedPath}' unreadable ({exception.Message})";

            return false;
        }
    }
    private bool TryResolveEngine(string? engineId, out IScreenMachineEngine engine, out string error) {
        if (engineId is { } id) {
            if (m_engines.TryGet(
                extension: out var named,
                key: id
            )) {
                engine = named;
                error = "";

                return true;
            }

            engine = null!;
            error = $"no screen-machine engine '{id}'";

            return false;
        }

        if (m_engines.Count == 1) {
            engine = m_engines.Values.First();
            error = "";

            return true;
        }

        engine = null!;
        error = ((m_engines.Count == 0)
            ? "no screen-machine engine registered"
            : $"which engine? {m_engines.Count} registered — name one of: {string.Join(
                separator: ", ",
                values: m_engines.Keys
            )}"
        );

        return false;
    }

    /// <inheritdoc/>
    public void Advance(ulong stepTicks, ReadOnlyMemory<ScreenPadSnapshot> pads) {
        if (m_disposed) {
            return;
        }

        StepLiveLinks(
            stepTicks: stepTicks,
            pads: pads.Span
        );

        foreach (var slot in m_slots.Values) {
            if (slot.Machine is not { } machine) {
                continue;
            }

            if (
                (slot.LinkName is { } linkName) &&
                m_links.TryGetValue(
                key: linkName,
                value: out var entry
            ) &&
                (entry.Link is not null)
            ) {
                continue;
            }

            var input = EngagedPad(
                pads: pads.Span,
                screenIndex: slot.Index
            );

            AnyEverPumped = true;

            if (machine is IQueuedScreenMachine queued) {
                var submission = queued.Submit(
                    deltaTicks: stepTicks,
                    input: in input
                );

                if (
                    (submission == QueuedMachineSubmission.Rejected) &&
                    machine.IsAssigned
                ) {
                    throw new InvalidOperationException(message: ($"Screen {slot.Index}'s queued machine rejected an authoritative tick/input segment" +
                                 ((queued.QueueFault is { } fault)
                        ? $" ({fault})."
                        : ".")));
                }

                slot.FramesStepped = queued.CompletedSteps;
            } else if (machine.Step(
                deltaTicks: stepTicks,
                input: in input
            )) {
                ++slot.FramesStepped;
            }
        }
    }
    /// <inheritdoc/>
    public IAudioMachine? AudioMachine(int index) =>
        ((m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) && (slot.Machine is IAudioMachine audio))
            ? audio
            : null
        );
    /// <inheritdoc/>
    public long? InstrumentTicksPerBeat(int index) =>
        ((m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) && (slot.Machine is IInstrumentClockSource instrument) && (instrument.TicksPerBeat > 0))
            ? instrument.TicksPerBeat
            : null
        );
    /// <inheritdoc/>
    public IReadOnlyList<WorldMachineCableGroup> CaptureLinks() {
        if (m_links.Count == 0) {
            return [];
        }

        var captured = new List<WorldMachineCableGroup>(capacity: m_links.Count);

        foreach (var entry in m_links.Values) {
            captured.Add(item: new WorldMachineCableGroup(
                Name: entry.Name,
                Screens: [.. entry.Members]
            ));
        }

        return captured;
    }
    /// <inheritdoc/>
    public string DescribeLinks() {
        if (m_links.Count == 0) {
            return "none";
        }

        return string.Join(
            separator: "; ",
            values: m_links.Values.Select(selector: DescribeLink)
        );
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var entry in m_links.Values) {
            entry.Link?.Dispose();
        }

        m_links.Clear();

        foreach (var slot in m_slots.Values) {
            slot.Machine?.Dispose();
        }
    }
    /// <inheritdoc/>
    public nint Handle(int index) => ((m_slots.TryGetValue(
        key: index,
        value: out var slot
    ) && (slot.Machine is { } machine))
        ? machine.NativeImageViewHandle
        : 0
    );
    /// <inheritdoc/>
    public bool HasEngine(string engineId) => m_engines.IsRegistered(key: engineId);
    /// <inheritdoc/>
    public bool HasMachine(int index) => (m_slots.TryGetValue(
        key: index,
        value: out var slot
    ) && (slot.Machine is not null));
    /// <inheritdoc/>
    public Vector3 Light(int index) => ((m_slots.TryGetValue(
        key: index,
        value: out var slot
    ) && (slot.Machine is { } machine))
        ? machine.EmittedLight
        : Vector3.Zero
    );
    /// <inheritdoc/>
    public string? LinkOf(int index) => (m_slots.TryGetValue(
        key: index,
        value: out var slot
    )
        ? slot.LinkName
        : null
    );
    /// <inheritdoc/>
    public IScreenMachine? MachineAt(int index) => (m_slots.TryGetValue(
        key: index,
        value: out var slot
    )
        ? slot.Machine
        : null
    );
    /// <summary>Reconciles the declared cable links to a mutated <c>links</c> section — two-phase, atomic per call:
    /// every stale-or-member-changed declared link tears down first, in full, before anything is (re-)established.
    /// Tearing down every stale/changed row before establishing anything means a member a changed link is
    /// reclaiming is always free by the time that link is (re-)established, so a plain re-shape (an ordinary,
    /// non-conflicting move) always succeeds; two declared links that genuinely both claim the same screen within
    /// the same reconcile is a real document error and fails loudly (see below) rather than resolving unpredictably
    /// by document order.</summary>
    /// <inheritdoc/>
    public void ReconcileLinks(IReadOnlyList<WorldMachineCableGroup> links) {
        if (m_disposed) {
            return;
        }

        var declaredNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var toTeardown = new List<string>();

        foreach (var link in links) {
            _ = declaredNames.Add(item: link.Name);

            if (
                m_links.TryGetValue(
                key: link.Name,
                value: out var existing
            ) &&
                existing.Declared &&
                !MembersMatch(
                entry: existing,
                members: link.Screens
            )
            ) {
                toTeardown.Add(item: link.Name);
            }
        }

        foreach (var entry in m_links.Values) {
            if (
                entry.Declared &&
                !declaredNames.Contains(item: entry.Name)
            ) {
                toTeardown.Add(item: entry.Name);
            }
        }

        // Phase 1, complete before phase 2 starts: every stale-or-changed declared link is gone, so no established
        // link can be silently blocking a screen a phase-2 TryLink call legitimately needs.
        foreach (var name in toTeardown) {
            TeardownLink(name: name);
        }

        foreach (var link in links) {
            if (
                m_links.TryGetValue(
                key: link.Name,
                value: out var existing
            ) &&
                existing.Declared &&
                MembersMatch(
                entry: existing,
                members: link.Screens
            )
            ) {
                continue;
            }

            var (ok, message) = TryLink(
                name: link.Name,
                members: link.Screens
            );

            if (m_links.TryGetValue(
                key: link.Name,
                value: out var reconciled
            )) {
                reconciled.Declared = true;
            }

            // Establishment failure is surfaced loudly rather than discarded. A DORMANT link (Ok: true, no live
            // IMachineLink — mismatched engines, no machine yet) already reports through DescribeLink/screen.links;
            // this covers the harder failure TryLink returns Ok: false for (an undeclared screen, fewer than two
            // members, a duplicate member, or two declared rows racing for the same screen in one reconcile): that
            // outcome never reaches m_links, so screen.links would otherwise show nothing for a link the document
            // still declares, with no sign anything went wrong.
            if (!ok) {
                if (m_narrationHub is { HasNarrationSink: true }) {
                    m_narrationHub?.Narrate(
                        channel: "world.link",
                        text: $"[world.link: '{link.Name}' failed to establish — {message}]"
                    );
                }
            }
        }
    }
    /// <summary>Reconciles the host's machine slots to a mutated screen list — the live-application half of an
    /// <c>UpsertScreen</c>/<c>RemoveScreen</c> world mutation, called from <see cref="WorldServer"/>'s own Install
    /// path when the definition changes. Removals are reconciled first: a slot whose index is no longer declared has
    /// its machine disposed and its entry dropped — the caller is responsible for the engagement-side admin cleanup
    /// (<see cref="WorldEngagement.DissolveScreen"/>) over the returned indices, since this type holds no grant-table
    /// reference by design. Then, for a declared index whose source changed, machine boots/ejects; a non-machine
    /// source change is a no-op here (presentation applies it).</summary>
    /// <inheritdoc/>
    public IReadOnlyList<int> ReconcileScreens(IReadOnlyList<WorldScreen> screens) {
        if (m_disposed) {
            return [];
        }

        m_reconcileRemovals.Clear();

        foreach (var index in m_slots.Keys) {
            if (!DeclaresIndex(
                index: index,
                screens: screens
            )) {
                m_reconcileRemovals.Add(item: index);
            }
        }

        foreach (var index in m_reconcileRemovals) {
            LeaveLink(index: index);

            if (m_slots.Remove(
                key: index,
                value: out var slot
            )) {
                slot.Machine?.Dispose();
            }
        }

        foreach (var screen in screens) {
            if (m_slots.TryGetValue(
                key: screen.Index,
                value: out var slot
            ) is false) {
                // CREATE the slot, mirroring the constructor — this type carries no GPU provider key set (that
                // constraint is Puck.World.WorldScreenBinder's own, presentation-only, and does not apply here), so
                // there is no reason to permanently forget an index. Covers BOTH a genuinely-new index (never
                // declared at boot) and an index that was declared, removed (a RemoveScreen mutation's removal pass
                // above), and is now re-declared (a later UpsertScreen, or a world.reset/.load/.reload whose
                // definition still names it). DeclaredSource starts null so the Equals check below never
                // short-circuits a fresh slot.
                slot = new MachineSlot { DeclaredSource = null, Index = screen.Index };
                m_slots[screen.Index] = slot;
            }

            slot.Magazine = screen.Magazine;

            if (screen.Magazine is { } magazine) {
                slot.SelectedEntry = Math.Clamp(
                    value: slot.SelectedEntry,
                    min: 0,
                    max: Math.Max(
                        val1: 0,
                        val2: (magazine.Entries.Count - 1)
                    )
                );
            } else {
                slot.SelectedEntry = 0;
            }

            if (
                !m_documentDirectoryChanged &&
                Equals(
                objA: slot.DeclaredSource,
                objB: screen.Source
            )
            ) {
                continue;
            }

            slot.DeclaredSource = screen.Source;

            switch (screen.Source) {
                case WorldScreenSource.Machine { ContentPath: { Length: > 0 } path } machine:
                    var (ok, message, _) = TryBootMachine(
                        index: screen.Index,
                        slot: slot,
                        contentPath: path,
                        engineId: machine.Engine,
                        options: machine.Options,
                        expectedContentHash: null,
                        documentRelative: true
                    );

                    if (m_narrationHub is { HasNarrationSink: true }) {
                        m_narrationHub?.Narrate(
                            channel: "world.screen",
                            text: $"[world.screen: {(ok
                                ? message
                                : $"{screen.Index} {message}")}]"
                        );
                    }

                    break;
                case WorldScreenSource.Machine:
                    // Unconfigured machine row (no content path) — applies at next boot, matching TryInsert's own
                    // "no content path" refusal shape for a bare declared row.
                    break;
                default:
                    // A non-machine declared source: if this slot carried a machine, eject it (the declared source no
                    // longer names one); presentation applies its own source through the ordinary reconcile path.
                    if (slot.Machine is not null) {
                        var (ejectOk, ejectMessage) = TryEject(index: screen.Index);

                        if (m_narrationHub is { HasNarrationSink: true }) {
                            m_narrationHub?.Narrate(
                                channel: "world.screen",
                                text: $"[world.screen: {(ejectOk
                                    ? ejectMessage
                                    : $"{screen.Index} {ejectMessage}")}]"
                            );
                        }
                    }

                    break;
            }
        }

        m_documentDirectoryChanged = false;

        return [.. m_reconcileRemovals];
    }
    /// <inheritdoc/>
    public void SetDocumentPath(string? documentPath) {
        var directory = DocumentDirectory(documentPath: documentPath);

        if (!string.Equals(
            a: directory,
            b: m_documentDirectory,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            m_documentDirectory = directory;
            m_documentDirectoryChanged = true;
        }
    }
    /// <inheritdoc/>
    public WorldMachineState? State(int index) {
        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return null;
        }

        var queued = (slot.Machine as IQueuedScreenMachine);

        return new WorldMachineState(
            Assigned: (slot.Machine is not null),
            Engine: slot.MachineEngine,
            FramesStepped: (queued?.CompletedSteps ?? slot.FramesStepped),
            PendingSteps: (queued?.PendingSteps ?? 0L),
            MaximumPendingSteps: (queued?.MaximumPendingSteps ?? 0),
            BackpressureEvents: (queued?.BackpressureEvents ?? 0L),
            Fault: (queued?.QueueFault ?? slot.DeclaredFault)
        );
    }
    /// <inheritdoc/>
    public (bool Ok, string Message) TryEject(int index) {
        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (slot.Machine is null) {
            return (Ok: false, Message: $"screen {index} has no machine to eject");
        }

        LeaveLink(index: index);
        slot.ClearMachine();
        slot.FramesStepped = 0;

        return (Ok: true, Message: $"screen {index} ejected");
    }
    /// <inheritdoc/>
    public (bool Ok, string Message, string? ContentHash) TryInsert(int index, string contentPath, string? engineId, string? options, string? expectedContentHash = null) {
        if (m_disposed) {
            return (Ok: false, Message: "machine host disposed", ContentHash: null);
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared", ContentHash: null);
        }

        return TryBootMachine(
            contentPath: contentPath,
            documentRelative: false,
            engineId: engineId,
            expectedContentHash: expectedContentHash,
            index: index,
            options: options,
            slot: slot
        );
    }
    /// <inheritdoc/>
    public (bool Ok, string Message) TryLink(string name, IReadOnlyList<int> members) {
        if (m_disposed) {
            return (Ok: false, Message: "machine host disposed");
        }

        if (
            (members is null) ||
            (members.Count < 2)
        ) {
            return (Ok: false, Message: $"link '{name}' needs two or more screens");
        }

        var seen = new HashSet<int>();

        foreach (var member in members) {
            if (m_slots.TryGetValue(
                key: member,
                value: out var slot
            ) is false) {
                return (Ok: false, Message: $"no screen {member} declared");
            }

            if (!seen.Add(item: member)) {
                return (Ok: false, Message: $"screen {member} is named twice in link '{name}'");
            }

            if (
                (slot.LinkName is { } existing) &&
                !string.Equals(
                a: existing,
                b: name,
                comparisonType: StringComparison.Ordinal
            )
            ) {
                return (Ok: false, Message: $"screen {member} is already in link '{existing}'");
            }
        }

        TeardownLink(name: name);

        var (link, reason) = TryEstablishLink(members: members);
        var entry = new LinkEntry { DormantReason = reason, Link = link, Members = [.. members], Name = name };

        m_links[name] = entry;

        foreach (var member in members) {
            m_slots[member].LinkName = name;
        }

        return (Ok: true, Message: DescribeLink(entry: entry));
    }
    /// <inheritdoc/>
    public bool TryMagazine(int index, out int selected, out WorldScreenMagazine magazine) {
        if (
            m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) &&
            (slot.Magazine is { } value)
        ) {
            selected = slot.SelectedEntry;
            magazine = value;

            return true;
        }

        selected = 0;
        magazine = null!;

        return false;
    }
    /// <inheritdoc/>
    public bool TryPeek(int screen, int address, out byte value) {
        var (ok, _) = TryPeekMessage(
            address: address,
            index: screen,
            value: out value
        );

        return ok;
    }
    /// <inheritdoc/>
    public (bool Ok, string Message) TryPeekMessage(int index, int address, out byte value) {
        value = 0;

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (slot.Machine is not { } machine) {
            return (Ok: false, Message: $"screen {index} has no machine to read");
        }

        if (machine is not IMachineMemoryPeek peek) {
            return (Ok: false, Message: $"screen {index}'s machine does not support memory peek");
        }

        value = peek.PeekByte(address: address);

        return (Ok: true, Message: "");
    }
    /// <inheritdoc/>
    public bool TryReadLinkMembers(string name, out IReadOnlyList<int> members) {
        if (m_links.TryGetValue(
            key: name,
            value: out var entry
        )) {
            members = entry.Members;

            return true;
        }

        members = [];

        return false;
    }
    /// <inheritdoc/>
    public bool TryReadMachineInsert(int index, out string engine, out string contentPath, out string? options) {
        if (
            m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) &&
            (slot.Machine is not null) &&
            (slot.MachineContentPath is { } path) &&
            (slot.MachineSourceEngine is { } engineId)
        ) {
            engine = engineId;
            contentPath = path;
            options = slot.MachineOptions;

            return true;
        }

        engine = string.Empty;
        contentPath = string.Empty;
        options = null;

        return false;
    }
    /// <inheritdoc/>
    public bool TryReadOptions(int index, out string options) {
        if (
            m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) &&
            (slot.Machine is IReconfigurableMachine reconfigurable)
        ) {
            options = reconfigurable.Options;

            return true;
        }

        options = string.Empty;

        return false;
    }
    /// <inheritdoc/>
    public (bool Ok, string Message) TryReconfigure(int index, string? options) {
        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (slot.Machine is not { } machine) {
            return (Ok: false, Message: $"screen {index} has no machine to reconfigure");
        }

        if (machine is not IReconfigurableMachine reconfigurable) {
            return (Ok: false, Message: $"screen {index}'s machine does not support live reconfiguration");
        }

        var previous = reconfigurable.Options;

        if (!reconfigurable.TryReconfigure(
            options: options,
            out var reason
        )) {
            return (Ok: false, Message: $"{index} '{previous}' -> '{options}' rejected: {reason}");
        }

        slot.MachineOptions = reconfigurable.Options;

        return (Ok: true, Message: $"{index} '{previous}' -> '{reconfigurable.Options}' reconfigured{((reason.Length > 0)
            ? $" — {reason}"
            : string.Empty)}");
    }
    /// <summary>Points the screen's magazine selector at <paramref name="entry"/>. When that entry is a
    /// <see cref="WorldScreenSource.Machine"/> row, boots it through the same <see cref="TryBootMachine"/> sequence
    /// <see cref="TryInsert"/> uses — CAS-pinned identically, since a magazine entry's document-declared path is not
    /// immune to on-disk drift either; for any other entry kind the selector still moves (so the pointer always
    /// tracks) but nothing boots here — a non-machine entry is presentation's own concern
    /// (<c>Puck.World.WorldScreenBinder</c> observes the moved selector and applies its camera/capture/view source
    /// itself). Fails for an undeclared screen, a screen with no magazine, an
    /// out-of-range entry, or — for a machine entry — whatever <see cref="TryBootMachine"/> refuses for; a failed
    /// boot always reports <c>Ok: false</c>, never a disguised success.</summary>
    /// <inheritdoc/>
    public (bool Ok, string Message, string? ContentHash) TrySelect(int index, int entry, string? expectedContentHash = null) {
        if (m_disposed) {
            return (Ok: false, Message: "machine host disposed", ContentHash: null);
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared", ContentHash: null);
        }

        if (slot.Magazine is not { } magazine) {
            return (Ok: false, Message: $"screen {index} has no magazine", ContentHash: null);
        }

        if (
            (entry < 0) ||
            (entry >= magazine.Entries.Count)
        ) {
            return (Ok: false, Message: $"entry {entry} is outside 0..{(magazine.Entries.Count - 1)}", ContentHash: null);
        }

        var source = magazine.Entries[entry];

        slot.SelectedEntry = entry;

        if (source is WorldScreenSource.Machine { ContentPath: { Length: > 0 } path } machine) {
            var (ok, message, contentHash) = TryBootMachine(
                index: index,
                slot: slot,
                contentPath: path,
                engineId: machine.Engine,
                options: machine.Options,
                expectedContentHash: expectedContentHash,
                documentRelative: true
            );

            // A failed boot is a failed Select — never a disguised success. The selector POINTER still moves
            // (above) regardless: the pointer-always-moves contract holds independent of boot outcome.
            return (Ok: ok, Message: $"{index} entry {entry}/{magazine.Entries.Count} {(ok
                ? message
                : $"selected (boot failed — {message})")}", ContentHash: contentHash);
        }

        // Non-machine entry (or an unconfigured machine row): the selector moved; any existing machine on the slot
        // is cleared so the non-machine entry can take over presentation-side (mirroring TryEject's own clear).
        // Nothing here reads a file, so no CAS pin applies regardless of expectedContentHash.
        if (slot.Machine is not null) {
            LeaveLink(index: index);
            slot.ClearMachine();
        }

        return (Ok: true, Message: $"{index} entry {entry}/{magazine.Entries.Count} selected (no machine — presentation applies its own source)", ContentHash: null);
    }
    /// <inheritdoc/>
    public (bool Ok, string Message) TryUnlink(string name) {
        if (!m_links.ContainsKey(key: name)) {
            return (Ok: false, Message: $"no link '{name}'");
        }

        TeardownLink(name: name);

        return (Ok: true, Message: $"link '{name}' severed");
    }

    /// <inheritdoc/>
    public bool AnyEverPumped { get; private set; }
    /// <inheritdoc/>
    public Action<int, bool>? MachineLifecycleTap { get; set; }
    /// <inheritdoc/>
    public IEnumerable<int> MachineScreenIndices {
        get {
            foreach (var (index, slot) in m_slots) {
                if (slot.Machine is not null) {
                    yield return index;
                }
            }
        }
    }

    // One cable link beside m_slots: its name, member screen indices (cable order), the live IMachineLink (null when
    // dormant), and the dormant reason.
    private sealed class LinkEntry {
        public bool Declared { get; set; }
        public string? DormantReason { get; set; }
        public IMachineLink? Link { get; set; }
        public required int[] Members { get; init; }
        public required string Name { get; init; }
    }
    // One declared screen's machine slot: the persistent declared source (so ReconcileScreens can diff it), the
    // magazine + live selector, and at most one booted machine plus the bookkeeping world.save/screen.state need.
    private sealed class MachineSlot {
        public string? DeclaredFault { get; set; }
        public WorldScreenSource? DeclaredSource { get; set; }
        public long FramesStepped { get; set; }
        public required int Index { get; init; }
        public string? LinkName { get; set; }
        public IScreenMachine? Machine { get; set; }
        public string? MachineContentHash { get; set; }
        public string? MachineContentPath { get; set; }
        public string? MachineEngine { get; set; }
        public string? MachineOptions { get; set; }
        public string? MachineSourceEngine { get; set; }
        public WorldScreenMagazine? Magazine { get; set; }
        public int SelectedEntry { get; set; }

        public void ClearMachine() {
            Machine?.Dispose();
            Machine = null;
            MachineEngine = null;
            MachineContentPath = null;
            MachineSourceEngine = null;
            MachineOptions = null;
            MachineContentHash = null;
            DeclaredFault = null;
        }
    }
}
