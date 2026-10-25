using System.Numerics;
using Puck.Abstractions.Machines;
using Puck.World.Machines;

namespace Puck.World.Server;

/// <summary>
/// Owns every declared named machine's live runtime: booting, stepping, memory-peeking, and provider operations are
/// server-side, so machine state is simulation state and a headless boot runs exactly like a windowed one. Physical
/// screens are consumers only: producer, jumbotron-view, probe and session sources remain genuinely
/// presentation, composed by <c>Puck.World.WorldScreenBinder</c>, which reads this type's named machine outputs
/// (framebuffer handle, light, audio) as a pure reader. A screen owns no machine: its slot keeps the declared source,
/// its magazine selector and its cable link, and resolves to the named machine it shows. Engines and content providers come from this host's
/// <see cref="WorldMachineCatalog"/>; the host carries no dependency on their implementations.
/// <see cref="WorldServer"/> and every other <c>Puck.World.Server</c> type reach a booted machine only through
/// <see cref="IWorldMachineHost"/>.
/// </summary>
/// <remarks>Single-threaded, like every other simulation type here: constructed once at boot (or replay
/// rehydration), then only ever touched from <see cref="WorldServer.Step"/>'s tick thread (<see cref="Advance"/>) or
/// a synchronously-applied <see cref="WorldServer"/> screen-op or provider-operation apply, so no lock guards this
/// state. Holds native machine resources (an <see cref="IMachineRuntime"/> may own emulator-core
/// memory) — <see cref="Dispose"/> tears every booted machine and live link down; the composition root registers
/// this type as its own DI singleton (not a private field of <see cref="WorldServer"/>) precisely so the container
/// disposes it.</remarks>
public sealed partial class WorldMachineHost : IWorldMachineHost {
    /// <summary>Gets the host's optional completion policy, read at each step. When true, each machine finishes the
    /// tick through its synchronous advance path before presentation can read its video; otherwise queued machines
    /// submit asynchronously. This changes completion timing, never the tick budget or input.</summary>
    public Func<bool>? SynchronousSteps { get; init; }

    private readonly IMachineContentAdmissionPolicy m_contentAdmissionPolicy;

    /// <summary>Gets this host's immutable registration catalog.</summary>
    public WorldMachineCatalog Catalog { get; }
    /// <inheritdoc/>
    public IMachineValidationCatalog ValidationCatalog => Catalog;

    private bool m_disposed;
    private string? m_documentDirectory;
    private bool m_documentDirectoryChanged;

    private readonly Dictionary<int, MachineSlot> m_slots = new();
    private readonly Dictionary<string, LinkEntry> m_links = new(comparer: StringComparer.Ordinal);
    private readonly List<int> m_reconcileRemovals = new();

    private readonly WorldOutputHub? m_narrationHub;

    /// <summary>Initializes the host over the world's declared screens using the registered engines.</summary>
    /// <param name="screens">The world's diegetic screens, retained for temporary screen-operation forwarding.</param>
    /// <param name="engines">The registered screen-machine engines (DI-collected) a declared or inserted machine
    /// resolves against.</param>
    /// <param name="narrationHub">The hub this host's narration is delivered through, or <see langword="null"/> to
    /// leave it undelivered — this host carries no single owning server of its own.</param>
    /// <param name="contentAdmissionPolicy">The host-selected policy for prepared content and auxiliary assets, or
    /// <see langword="null"/> to use the local open policy.</param>
    public WorldMachineHost(IReadOnlyList<WorldScreen> screens, IEnumerable<IMachineEngine> engines, WorldOutputHub? narrationHub = null,
        IMachineContentAdmissionPolicy? contentAdmissionPolicy = null)
        : this(
        screens: screens,
        engines: engines,
        compilers: null,
        narrationHub: narrationHub,
        contentAdmissionPolicy: contentAdmissionPolicy
    ) { }
    /// <summary>Initializes the host over the world's declared screens and the named machine catalog. Named machine
    /// rows are prepared by <see cref="TryPrepare"/>; screen consumers do not construct runtimes.</summary>
    /// <param name="screens">The world's diegetic screens, retained for temporary screen-operation forwarding.</param>
    /// <param name="engines">The registered screen-machine engines (DI-collected) a declared or inserted machine
    /// resolves against.</param>
    /// <param name="compilers">The content providers selected for this host, or null for none.</param>
    /// <param name="narrationHub">The hub this host's narration is delivered through, or <see langword="null"/> to
    /// leave it undelivered — this host carries no single owning server of its own.</param>
    /// <param name="contentAdmissionPolicy">The host-selected policy for prepared content and auxiliary assets, or
    /// <see langword="null"/> to use the local open policy.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Two engines register one id — a composition-root error, thrown at boot
    /// rather than resolved last-writer-wins.</exception>
    public WorldMachineHost(IReadOnlyList<WorldScreen> screens, IEnumerable<IMachineEngine> engines, IEnumerable<IMachineContentProvider>? compilers, WorldOutputHub? narrationHub = null,
        IMachineContentAdmissionPolicy? contentAdmissionPolicy = null)
        : this(
        screens,
        new WorldMachineCatalog(
            contentProviders: compilers,
            engines: engines
        ),
        narrationHub,
        contentAdmissionPolicy
    ) { }
    /// <summary>Initializes a host with the exact catalog selected by its composition root.</summary>
    /// <param name="screens">The authored screen declarations.</param>
    /// <param name="catalog">The immutable engine and content-provider registrations.</param>
    /// <param name="narrationHub">The optional diagnostic output hub.</param>
    /// <param name="contentAdmissionPolicy">The host-selected policy for prepared content and auxiliary assets, or
    /// <see langword="null"/> to use the local open policy.</param>
    public WorldMachineHost(IReadOnlyList<WorldScreen> screens, WorldMachineCatalog catalog, WorldOutputHub? narrationHub = null,
        IMachineContentAdmissionPolicy? contentAdmissionPolicy = null) {
        ArgumentNullException.ThrowIfNull(argument: screens);
        ArgumentNullException.ThrowIfNull(argument: catalog);

        Catalog = catalog;
        m_narrationHub = narrationHub;
        m_contentAdmissionPolicy = (contentAdmissionPolicy ?? MachineContentAdmissionPolicy.Open(assetAdmission: MachineAssetAdmission.Allow));

        foreach (var screen in screens) {
            var slot = new MachineSlot { DeclaredSource = screen.Source, Index = screen.Index, Magazine = screen.Magazine, Route = screen.Route, SelectedEntry = (screen.Magazine?.Selected ?? 0) };

            m_slots[screen.Index] = slot;
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
            if (
                (entry.Link is not { } link) ||
                !LinkCanAdvance(entry: entry)
            ) {
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
                    value: out var namedSlot
                ) &&
                    (namedSlot.DeclaredSource is WorldScreenSource.Machine source) &&
                    m_instances.TryGetValue(
                    key: source.Instance,
                    value: out var instance
                )
                ) {
                    instance.Lease.CompletedSteps++;
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
    // Providers own format recognition, parsing, compilation, and exported addresses. The host pins the input
    // bytes and the executable image without depending on a provider's source-document or compiler types.
    private bool TryResolveContent(IMachineEngine engine, string contentPath, string? documentDirectory, byte[] content, out byte[] bytes, out WorldMachineCartridge? cartridge, out PreparedMachineContent? compilation, out string? fault) {
        _ = Catalog.ContentProviders.TryGetValue(
            key: engine.Id,
            value: out var compiler
        );

        if (
            (compiler is not null) &&
            compiler.Recognizes(contentPath: contentPath)
        ) {
            try {
                WorldBootWork.Count(kind: WorldBootWork.AssetLoads);
                compilation = compiler.Prepare(content: content, contentPath: WorldDocumentPaths.Resolve(documentDirectory: documentDirectory, path: contentPath));
                bytes = compilation.Image;
                cartridge = new WorldMachineCartridge(
                    Path: contentPath,
                    SourceHash: compilation.SourceHash,
                    RomHash: WorldDefinitionFileSource.ComputeContentHash(content: bytes)
                );
                fault = null;

                return true;
            } catch (MachineContentException exception) {
                bytes = [];
                cartridge = null;
                compilation = null;
                fault = $"content '{contentPath}' refused: {exception.Message}";

                return false;
            }
        }

        if (!Catalog.RequiresPreparation(contentPath: contentPath)) {
            bytes = content;
            cartridge = null;
            compilation = null;
            fault = null;

            return true;
        }

        bytes = [];
        cartridge = null;
        compilation = null;
        fault = $"content '{contentPath}' needs a content provider, and engine '{engine.Id}' recognizes none";

        return false;
    }
    private (IMachineLink? Link, string? Reason) TryEstablishLink(IReadOnlyList<int> members) {
        var machines = new List<IMachineRuntime>(capacity: members.Count);
        IMachineLinkingEngine? linkingEngine = null;
        string? engineId = null;

        foreach (var member in members) {
            var (machine, id) = ResolveMachine(screenIndex: member);
            if (machine is null) {
                return (Link: null, Reason: $"screen {member} has no machine");
            }
            if (id is null) {
                return (Link: null, Reason: $"screen {member}'s machine has no engine identity");
            }

            if (engineId is null) {
                engineId = id;

                if (
                    Catalog.Engines.TryGetValue(
                    key: id,
                    value: out var engine
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
    private (IMachineRuntime? Runtime, string? Engine) ResolveMachine(int screenIndex) {
        if (!m_slots.TryGetValue(
            key: screenIndex,
            value: out var slot
        )) {
            return (null, null);
        }
        return (((slot.DeclaredSource is WorldScreenSource.Machine source) && m_instances.TryGetValue(
            key: source.Instance,
            value: out var instance
        ))
            ? (instance.Lease.Runtime, instance.Declaration.Engine)
            : (null, null)
        );
    }

    private readonly HashSet<string> m_linkedInstances = new(comparer: StringComparer.Ordinal);

    private HashSet<string> LinkedInstances() {
        var result = m_linkedInstances;

        result.Clear();

        foreach (var entry in m_links.Values) {
            if (entry.Link is null) {
                continue;
            }
            foreach (var member in entry.Members) {
                if (
                    m_slots.TryGetValue(
                    key: member,
                    value: out var slot
                ) &&
                    (slot.DeclaredSource is WorldScreenSource.Machine source)
                ) {
                    result.Add(item: source.Instance);
                }
            }
        }
        return result;
    }
    private bool TryGetLiveLinkForInstance(string instance, out string linkName) {
        foreach (var entry in m_links.Values) {
            if (entry.Link is null) {
                continue;
            }
            foreach (var member in entry.Members) {
                if (
                    m_slots.TryGetValue(
                    key: member,
                    value: out var slot
                ) &&
                    (slot.DeclaredSource is WorldScreenSource.Machine source) &&
                    string.Equals(
                    a: source.Instance,
                    b: instance,
                    comparisonType: StringComparison.Ordinal
                )
                ) {
                    linkName = entry.Name;
                    return true;
                }
            }
        }
        linkName = string.Empty;
        return false;
    }
    private bool LinkCanAdvance(LinkEntry entry) {
        foreach (var member in entry.Members) {
            if (
                m_slots.TryGetValue(
                key: member,
                value: out var slot
            ) &&
                (slot.DeclaredSource is WorldScreenSource.Machine source) &&
                m_instances.TryGetValue(
                key: source.Instance,
                value: out var instance
            ) &&
                !instance.Declaration.Running
            ) {
                return false;
            }
        }
        return true;
    }
    private (bool Ok, string Message) ValidateLinkMembers(string name, IReadOnlyList<int> members) {
        var named = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var member in members) {
            if (!m_slots.TryGetValue(
                key: member,
                value: out var slot
            )) {
                return (false, $"no screen {member} declared");
            }
            if (slot.DeclaredSource is not WorldScreenSource.Machine source) {
                continue;
            }
            if (!named.Add(item: source.Instance)) {
                return (false, $"machine '{source.Instance}' is named more than once in link '{name}'");
            }
            if (!m_instances.TryGetValue(
                key: source.Instance,
                value: out var instance
            )) {
                return (false, $"machine '{source.Instance}' is not live");
            }
            if (!instance.Declaration.Running) {
                return (false, $"machine '{source.Instance}' is stopped and cannot join link '{name}'");
            }
        }
        return (true, string.Empty);
    }
    // A document-relative path resolves beside documentDirectory: the directory of the document that declares it.
    private static bool TryReadContent(string contentPath, string? documentDirectory, out byte[] content, out string? fault) {
        if (string.IsNullOrEmpty(value: contentPath)) {
            content = [];
            fault = "no content configured";

            return false;
        }
        if (!WorldDocumentPaths.TryResolve(
            documentDirectory: documentDirectory,
            path: contentPath,
            reason: out var unresolved,
            resolved: out var resolvedPath
        )) {
            content = [];
            fault = $"content {unresolved}";

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

    /// <inheritdoc/>
    public void Advance(ulong stepTicks, ReadOnlyMemory<ScreenPadSnapshot> pads) {
        if (m_disposed) {
            return;
        }

        StepLiveLinks(
            stepTicks: stepTicks,
            pads: pads.Span
        );
        AdvanceInstances(
            stepTicks,
            pads.Span,
            LinkedInstances()
        );
    }
    /// <inheritdoc/>
    public IAudioMachine? AudioMachine(int index) =>
        (((ResolveMachine(screenIndex: index).Runtime is IMachineAudioOutputs outputs) && outputs.AudioOutputs.TryGetValue(
            key: "audio",
            value: out var audio
        ))
            ? audio
            : null
        );
    /// <inheritdoc/>
    public long? InstrumentTicksPerBeat(string instance) =>
        ((m_instances.TryGetValue(
            key: instance,
            value: out var entry
        ) && (entry.Lease.Runtime is IInstrumentClockSource instrument) && (instrument.TicksPerBeat > 0))
            ? instrument.TicksPerBeat
            : null
        );
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

        DisposeInstances();
    }
    /// <inheritdoc/>
    public bool HasEngine(string engineId) => Catalog.Engines.ContainsKey(key: engineId);
    /// <inheritdoc/>
    public bool HasMachine(int index) => (ResolveMachine(screenIndex: index).Runtime is not null);
    /// <inheritdoc/>
    public Vector3 Light(int index) => (VideoOutput(index: index)?.EmittedLight ?? Vector3.Zero);
    /// <inheritdoc/>
    public IMachineVideoOutput? VideoOutput(int index) {
        if (!m_slots.TryGetValue(
            key: index,
            value: out var slot
        )) {
            return null;
        }
        return ((slot.DeclaredSource is WorldScreenSource.Machine source)
            ? VideoOutput(
                instance: source.Instance,
                output: source.Output
            )
            : null
        );
    }
    /// <inheritdoc/>
    public string? LinkOf(int index) => (m_slots.TryGetValue(
        key: index,
        value: out var slot
    )
        ? slot.LinkName
        : null
    );
    /// <inheritdoc/>
    public IMachineRuntime? MachineAt(int index) => ResolveMachine(screenIndex: index).Runtime;
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
        var refusedNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var toTeardown = new List<string>();

        foreach (var link in links) {
            _ = declaredNames.Add(item: link.Name);
            var validation = ValidateLinkMembers(
                link.Name,
                link.Screens
            );

            if (!validation.Ok) {
                refusedNames.Add(item: link.Name);
                if (m_narrationHub is { HasNarrationSink: true }) {
                    m_narrationHub.Narrate(
                        channel: "world.link",
                        text: $"[world.link: '{link.Name}' refused — {validation.Message}]"
                    );
                }
            }

            if (
                !refusedNames.Contains(item: link.Name) &&
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
            if (!refusedNames.Contains(item: name)) {
                TeardownLink(name: name);
            }
        }

        foreach (var link in links) {
            if (refusedNames.Contains(item: link.Name)) {
                continue;
            }
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

            _ = m_slots.Remove(key: index);
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
            slot.Route = screen.Route;

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

            if (
                (slot.DeclaredSource is WorldScreenSource.Machine) &&
                !Equals(
                objA: slot.DeclaredSource,
                objB: screen.Source
            )
            ) {
                // A named display retarget leaves the cable's old runtime before the new source is published.
                LeaveLink(index: screen.Index);
            }

            // A named machine source is a presentation consumer: the named instance plan owns boot, replacement,
            // generation and retirement, and changing a display reference never touches it.
            slot.DeclaredSource = screen.Source;
        }

        m_documentDirectoryChanged = false;

        return [.. m_reconcileRemovals];
    }
    /// <inheritdoc/>
    public WorldMachineState? State(int index) {
        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return null;
        }

        if (
            (slot.DeclaredSource is WorldScreenSource.Machine source) &&
            m_instances.TryGetValue(
            key: source.Instance,
            value: out var instance
        )
        ) {
            var lease = instance.Lease;
            var namedQueued = (lease.Runtime as IQueuedMachineRuntime);
            WorldMachineCartridge? cartridge = null;

            foreach (var asset in lease.Assets.Values) {
                if (asset.PreparedContent is { } content) {
                    cartridge = new WorldMachineCartridge(
                        asset.Path,
                        content.SourceHash,
                        WorldDefinitionFileSource.ComputeContentHash(content: asset.Image.Span)
                    );
                    break;
                }
            }
            return new WorldMachineState(
                true,
                instance.Declaration.Engine,
                (namedQueued?.CompletedSteps ?? lease.CompletedSteps),
                (namedQueued?.PendingSteps ?? 0),
                (namedQueued?.MaximumPendingSteps ?? 0),
                (namedQueued?.BackpressureEvents ?? 0),
                namedQueued?.QueueFault,
                cartridge,
                MountedContent(lease: lease)
            );
        }

        return new WorldMachineState(
            Assigned: false,
            BackpressureEvents: 0L,
            Cartridge: null,
            Content: null,
            Engine: null,
            Fault: null,
            FramesStepped: 0L,
            MaximumPendingSteps: 0,
            PendingSteps: 0L
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

        var memberValidation = ValidateLinkMembers(
            members: members,
            name: name
        );

        if (!memberValidation.Ok) {
            return memberValidation;
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

        var machine = ResolveMachine(screenIndex: index).Runtime;

        if (machine is null) {
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
    /// <summary>Points the screen's magazine selector at <paramref name="entry"/>. Selection boots nothing: a
    /// <see cref="WorldScreenSource.Machine"/> entry selects a named machine that already runs, and any other entry is
    /// presentation's own concern (<c>Puck.World.WorldScreenBinder</c> observes the moved selector and applies its
    /// source itself). Fails for an undeclared screen, a screen with no magazine, or an out-of-range entry.</summary>
    /// <inheritdoc/>
    public (bool Ok, string Message) TrySelect(int index, int entry) {
        if (m_disposed) {
            return (Ok: false, Message: "machine host disposed");
        }

        if (m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (slot.Magazine is not { } magazine) {
            return (Ok: false, Message: $"screen {index} has no magazine");
        }

        if (
            (entry < 0) ||
            (entry >= magazine.Entries.Count)
        ) {
            return (Ok: false, Message: $"entry {entry} is outside 0..{(magazine.Entries.Count - 1)}");
        }

        var source = magazine.Entries[entry];

        slot.SelectedEntry = entry;

        if (source is WorldScreenSource.Machine machine) {
            // A named source selects an existing producer. Selection never boots, replaces, or ejects the named
            // instance; the subsequent source migration updates the presentation consumer through its normal path.
            return (Ok: true, Message: $"{index} entry {entry}/{magazine.Entries.Count} selected machine {machine.Instance}:{machine.Output}");
        }

        // Non-machine entry: the selector moved, and presentation applies its own source.
        return (Ok: true, Message: $"{index} entry {entry}/{magazine.Entries.Count} selected (no machine — presentation applies its own source)");
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
    public bool TryResolveSymbol(int index, string symbol, out int address) {
        if (
            m_slots.TryGetValue(
            key: index,
            value: out var slot
        ) &&
            (slot.DeclaredSource is WorldScreenSource.Machine source)
        ) {
            return TryResolveSymbol(
                source.Instance,
                symbol,
                out address
            );
        }

        address = 0;

        return false;
    }

    /// <summary>The prepare/commit plan for screen machine updates.</summary>
    public sealed class PreparedMachinePlan : IWorldMachinePreparedPlan {
        internal IReadOnlyList<WorldScreen> CandidateScreens { get; }
        internal PreparedInstances Instances { get; }

        /// <inheritdoc/>
        public int MachineCount { get; }

        internal PreparedMachinePlan(IReadOnlyList<WorldScreen> candidateScreens, int machineCount, PreparedInstances instances) {
            CandidateScreens = candidateScreens;
            MachineCount = (machineCount + instances.Candidate.Count);
            Instances = instances;
        }

        /// <inheritdoc/>
        public void Dispose() => Instances.Dispose();
    }

    /// <inheritdoc/>
    public bool TryPrepare(WorldDefinition? current, WorldDefinition candidate, out IWorldMachinePreparedPlan? plan, out string? reason,
        WorldDefinitionAdmission? admission = null) {
        ArgumentNullException.ThrowIfNull(argument: candidate);

        if ((admission is not null) && !admission.AppliesTo(definition: candidate, machines: Catalog)) {
            plan = null;
            reason = "the admission result belongs to a different definition or machine catalog";
            return false;
        }
        if ((admission is null) && !WorldDefinitionValidator.TryValidateLocally(
            definition: candidate,
            machines: Catalog,
            reason: out var validationReason
        )) {
            plan = null;
            reason = validationReason;
            return false;
        }

        if (!TryPrepareInstances(
            candidate: candidate,
            current: current,
            prepared: out var instances,
            reason: out reason
        )) {
            plan = null;
            return false;
        }
        plan = new PreparedMachinePlan(
            candidateScreens: candidate.Screens,
            machineCount: 0,
            instances!
        );
        reason = null;

        return true;
    }
    /// <inheritdoc/>
    public void Commit(IWorldMachinePreparedPlan plan) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        if (plan is PreparedMachinePlan prepared) {
            CommitInstances(plan: prepared.Instances);
            _ = ReconcileScreens(screens: prepared.CandidateScreens);
        }
    }
    /// <inheritdoc/>
    public void Finish(IWorldMachinePreparedPlan plan) {
        ArgumentNullException.ThrowIfNull(argument: plan);
        if (plan is PreparedMachinePlan prepared) {
            prepared.Instances.Dispose();
        }
    }

    /// <inheritdoc/>
    public bool AnyEverPumped { get; private set; }
    /// <inheritdoc/>
    public IEnumerable<int> MachineScreenIndices {
        get {
            foreach (var (index, slot) in m_slots) {
                if (ResolveMachine(screenIndex: index).Runtime is not null) {
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
    // One declared screen's slot: the persistent declared source (so ReconcileScreens can diff it), the magazine and
    // its live selector, and the cable link the screen belongs to.
    private sealed class MachineSlot {
        public WorldScreenSource? DeclaredSource { get; set; }
        public required int Index { get; init; }
        public string? LinkName { get; set; }
        public WorldScreenMagazine? Magazine { get; set; }
        public WorldScreenRoute Route { get; set; }
        public int SelectedEntry { get; set; }
    }
}
