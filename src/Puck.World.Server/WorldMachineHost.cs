using System.Numerics;
using Puck.Abstractions.Machines;

namespace Puck.World.Server;

/// <summary>One declared screen's machine-side live state for the <c>screen.state</c> verb — whether a machine is
/// assigned, the engine that hosts it, the stepped-frame count, and the boot fault (a declared machine whose content
/// file was missing, an unresolved engine, rejected options), if any. Carries no GPU-facing fields (no image-view
/// handle, no light) — those are presentation reads over <see cref="WorldMachineHost.Handle"/>/
/// <see cref="WorldMachineHost.Light"/>, which <c>Puck.World.WorldScreenBinder</c> (a pure reader as of the
/// authoritative-machines campaign) composes into the same console line.</summary>
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
public readonly record struct WorldMachineState(bool Assigned, string? Engine, long FramesStepped,
    long PendingSteps, int MaximumPendingSteps, long BackpressureEvents, string? Fault);

/// <summary>
/// Owns every declared screen's live MACHINE — the authoritative-machines campaign's inversion (owner ruling,
/// 2026-08-03): booting, stepping, cable-linking, memory-peeking, and reconfiguring a deterministic
/// <see cref="IScreenMachine"/> are ALL server-side now, so ROM state IS sim state and a headless boot's cabinets run
/// exactly like a windowed one's. Camera/capture/window-capture/jumbotron-view/test-pattern screen sources are
/// deliberately OUTSIDE this type's concern — they stay genuinely presentation, composed by
/// <c>Puck.World.WorldScreenBinder</c>, which now reads THIS type's machine outputs (framebuffer handle, light,
/// audio) as a pure reader instead of owning any machine state itself. Screen index IS machine identity for
/// screen-hosted machines, matching the pre-existing document convention (<c>docs</c>'s "screens are
/// position-addressed").
/// </summary>
/// <remarks>Single-threaded, like every other simulation type here: constructed once at boot (or replay
/// rehydration), then only ever touched from <see cref="WorldServer.Step"/>'s tick thread (<see cref="Advance"/>) or
/// a synchronously-applied <see cref="WorldServer"/> screen-op apply (<see cref="TryInsert"/> and friends), so no
/// lock guards this state. Holds native machine resources (an <see cref="IScreenMachine"/> may own emulator-core
/// memory) — <see cref="Dispose"/> tears every booted machine and live link down; the composition root registers
/// this type as its OWN DI singleton (not a private field of <see cref="WorldServer"/>) precisely so the container
/// disposes it.</remarks>
public sealed class WorldMachineHost : IWorldMachineMemoryPeek, IDisposable {
    private readonly WorldExtensionRegistry<IScreenMachineEngine> m_engines;
    private readonly Dictionary<int, MachineSlot> m_slots = new();
    private readonly Dictionary<string, LinkEntry> m_links = new(comparer: StringComparer.Ordinal);
    private readonly List<int> m_reconcileRemovals = new();
    private string? m_documentDirectory;
    private bool m_documentDirectoryChanged;
    private bool m_disposed;

    /// <summary>Initializes the host over the world's declared screens: a booted machine for each declared machine
    /// screen whose content file exists and whose engine resolves (a missing file or unknown engine leaves the slot
    /// unbound with a visible fault — loud data, no crash).</summary>
    /// <param name="screens">The world's diegetic screens (<see cref="WorldDefinition.Screens"/>).</param>
    /// <param name="engines">The registered screen-machine engines (DI-collected) a declared or inserted machine
    /// resolves against.</param>
    /// <param name="documentPath">The world document path used to resolve declared relative content paths.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Two engines register one id — a composition-root error, thrown at boot
    /// rather than resolved last-writer-wins.</exception>
    public WorldMachineHost(IReadOnlyList<WorldScreen> screens, IEnumerable<IScreenMachineEngine> engines, string? documentPath = null) {
        ArgumentNullException.ThrowIfNull(argument: screens);
        ArgumentNullException.ThrowIfNull(argument: engines);

        // The same registry the load-time key check reads through WorldExtensionVocabularyHook, so a key that
        // validated is a key this host can resolve.
        m_engines = new WorldExtensionRegistry<IScreenMachineEngine>(extensions: engines, keyOf: static engine => engine.Id);
        m_documentDirectory = DocumentDirectory(documentPath: documentPath);

        foreach (var screen in screens) {
            var slot = new MachineSlot { Index = screen.Index, DeclaredSource = screen.Source, Magazine = screen.Magazine, SelectedEntry = (screen.Magazine?.Selected ?? 0) };

            if (screen.Source is WorldScreenSource.Machine machine) {
                BootDeclaredMachine(slot: slot, machine: machine);
            }

            m_slots[screen.Index] = slot;
        }
    }

    /// <summary>Moves declared relative machine content resolution to a new world document.</summary>
    /// <param name="documentPath">The installed world document path.</param>
    public void SetDocumentPath(string? documentPath) {
        var directory = DocumentDirectory(documentPath: documentPath);

        if (!string.Equals(a: directory, b: m_documentDirectory, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            m_documentDirectory = directory;
            m_documentDirectoryChanged = true;
        }
    }

    /// <summary>Gets the machine-lifecycle tap: invoked with <c>(index, faulted)</c> on every runtime machine boot outcome
    /// — <see langword="false"/> when a machine boots onto a slot, <see langword="true"/> when a boot attempt faults
    /// (missing content, unresolved engine, rejected options). Constructor-time declared boots precede any wiring and
    /// do not fire.</summary>
    public Action<int, bool>? MachineLifecycleTap { get; set; }

    /// <summary>Gets a value indicating whether ANY booted machine has ever had a step/segment actually submitted to it — set the instant
    /// <see cref="Advance"/> steps a machine (individually or through a live cable link), never cleared. The
    /// boot-anchored replay arm predicate <see cref="WorldServer.AnyMachineEverPumped"/> reads (mirroring
    /// <c>WorldAddonRuntime.AnyEverPumped</c>'s identical shape): offline replay reconstructs a machine's BOOT
    /// image, never its accumulated core state once real ticks have run it.</summary>
    public bool AnyEverPumped { get; private set; }

    /// <summary>Determines whether a machine is currently booted on the screen index.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public bool HasMachine(int index) => (m_slots.TryGetValue(key: index, value: out var slot) && (slot.Machine is not null));

    /// <summary>Determines whether a screen-machine engine is registered under <paramref name="engineId"/>.</summary>
    /// <param name="engineId">The candidate engine id.</param>
    public bool HasEngine(string engineId) => m_engines.IsRegistered(key: engineId);

    /// <summary>Returns the current same-device framebuffer image-view handle bound to a screen index, or 0 when unbound, not
    /// declared, or the machine has not published a frame yet — the presentation read.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public nint Handle(int index) => ((m_slots.TryGetValue(key: index, value: out var slot) && (slot.Machine is { } machine)) ? machine.NativeImageViewHandle : 0);

    /// <summary>Returns the room light a booted machine emits (its framebuffer average), or zero for no machine — the
    /// presentation read.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public Vector3 Light(int index) => ((m_slots.TryGetValue(key: index, value: out var slot) && (slot.Machine is { } machine)) ? machine.EmittedLight : Vector3.Zero);

    /// <summary>Returns the live machine on a screen index, for presentation's own frame-publish loop
    /// (<c>IScreenMachine.PublishFrame</c> is a GPU call this project never makes itself), or <see langword="null"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public IScreenMachine? MachineAt(int index) => (m_slots.TryGetValue(key: index, value: out var slot) ? slot.Machine : null);

    /// <summary>Gets every screen index currently carrying a booted machine — presentation's publish-loop enumeration.</summary>
    public IEnumerable<int> MachineScreenIndices {
        get {
            foreach (var (index, slot) in m_slots) {
                if (slot.Machine is not null) {
                    yield return index;
                }
            }
        }
    }

    /// <summary>Returns the live machine on a screen slot as its audio drain seam, or <see langword="null"/> when the slot
    /// carries no machine (or one without the capability).</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public IAudioMachine? AudioMachine(int index) =>
        ((m_slots.TryGetValue(key: index, value: out var slot) && (slot.Machine is IAudioMachine audio)) ? audio : null);

    /// <summary>Reads back the live machine insert on a screen index — its engine id, content path, and options — so
    /// <c>world.save</c> can fold a runtime <c>screen.insert</c> into that screen row's
    /// <see cref="WorldScreenSource.Machine"/> source.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="engine">The engine id that booted the live machine.</param>
    /// <param name="contentPath">The content file (a cartridge ROM) the live machine booted.</param>
    /// <param name="options">The options string the live machine booted with, or <see langword="null"/>.</param>
    public bool TryReadMachineInsert(int index, out string engine, out string contentPath, out string? options) {
        if (m_slots.TryGetValue(key: index, value: out var slot) &&
            (slot.Machine is not null) &&
            (slot.MachineContentPath is { } path) &&
            (slot.MachineSourceEngine is { } engineId)) {
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

    /// <summary>Returns the live state of a declared screen's machine for <c>screen.state</c>, or <see langword="null"/> when
    /// the index is not a declared screen.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public WorldMachineState? State(int index) {
        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
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
    public bool TryPeek(int screen, int address, out byte value) {
        var (ok, _) = TryPeekMessage(index: screen, address: address, value: out value);

        return ok;
    }

    /// <summary>Reads one memory byte from a screen's machine (the <c>screen.peek</c> read) — a side-effect-free host
    /// poll through the machine's optional <see cref="IMachineMemoryPeek"/> capability. Reports (loudly) whether a
    /// machine was present and whether it supports the peek.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="address">A machine-defined memory address.</param>
    /// <param name="value">The byte read, or 0 on failure.</param>
    /// <returns>A success flag and, on failure, a message.</returns>
    public (bool Ok, string Message) TryPeekMessage(int index, int address, out byte value) {
        value = 0;

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
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

    /// <summary>The CAS signature <see cref="TryBootMachine"/> records when it could not READ the content file at
    /// all (missing, unreadable) — distinct from any real <c>sha256-64/…</c> hash so it can never collide with one.
    /// A recorded op pinning this sentinel demands the SAME absence on replay; a file that has since appeared (or
    /// become readable) refuses BY NAME, exactly like a changed hash does.</summary>
    public const string ContentAbsentSignature = "absent";

    /// <summary>Boots (or live-swaps) a machine onto a declared screen from a content file path. Any existing machine
    /// on the slot is cleared and replaced. Fails loudly (a message, no crash) for an undeclared screen, an
    /// unresolved engine, an unreadable content file, or an options string the engine rejects.</summary>
    /// <param name="index">The engine screen-surface index (must be a declared screen).</param>
    /// <param name="contentPath">The content file (a cartridge ROM) to boot.</param>
    /// <param name="engineId">The screen-machine engine id, or <see langword="null"/> for the sole-registered default.</param>
    /// <param name="options">The engine-specific options string, or <see langword="null"/> for the engine's defaults.</param>
    /// <param name="expectedContentHash">REPLAY ONLY: the CAS pin (a real <c>sha256-64</c> hash, or
    /// <see cref="ContentAbsentSignature"/>) a recorded tape entry carries. When set, a fresh resolution of
    /// <paramref name="contentPath"/> that disagrees with it refuses BY NAME (<c>ScreenOpContentMismatch</c>) before
    /// anything else applies — the negative control a moved/edited/appeared/vanished ROM exercises.
    /// <see langword="null"/> (the default) is the LIVE path.</param>
    /// <returns>Whether the insert succeeded, a message describing the outcome, and the content signature actually
    /// observed (a real hash, <see cref="ContentAbsentSignature"/>, or <see langword="null"/> when content
    /// resolution was never attempted — an unresolved engine, which is not a file-state risk) — a live caller pins
    /// this onto the replay tape REGARDLESS of <c>Ok</c>, so a failed insert reproduces (or refuses by name) rather
    /// than silently retrying unpinned.</returns>
    public (bool Ok, string Message, string? ContentHash) TryInsert(int index, string contentPath, string? engineId, string? options, string? expectedContentHash = null) {
        if (m_disposed) {
            return (Ok: false, Message: "machine host disposed", ContentHash: null);
        }

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared", ContentHash: null);
        }

        return TryBootMachine(index: index, slot: slot, contentPath: contentPath, engineId: engineId, options: options, expectedContentHash: expectedContentHash, documentRelative: false);
    }

    // The shared boot sequence TryInsert and TrySelect's machine branch both funnel through. Order: read content
    // FIRST, producing a signature EVEN ON FAILURE (a real hash, or ContentAbsentSignature) -> compare against
    // expectedContentHash when replaying, refusing BY NAME on ANY disagreement (present-vs-absent in either
    // direction, or a changed hash) -> resolve engine (content is signed BEFORE this step, so an unresolved engine
    // still pins whatever it would have read — engine resolution failing is not a file-state exemption) ->
    // construct the machine, still reporting the signature even if construction itself throws (bad options) so a
    // content change between record and replay is caught even when the failure reason is downstream of the read.
    private (bool Ok, string Message, string? ContentHash) TryBootMachine(int index, MachineSlot slot, string contentPath, string? engineId, string? options, string? expectedContentHash, bool documentRelative) {
        if (!TryReadContent(contentPath: contentPath, documentRelative: documentRelative, content: out var content, fault: out var fault)) {
            const string signature = ContentAbsentSignature;

            if ((expectedContentHash is { } expectedAbsence) && !string.Equals(a: expectedAbsence, b: signature, comparisonType: StringComparison.Ordinal)) {
                return (Ok: false, Message: $"ScreenOpContentMismatch: '{contentPath}' {fault} now, but the recording pinned {expectedAbsence} — the file changed since it was captured", ContentHash: signature);
            }

            MachineLifecycleTap?.Invoke(arg1: index, arg2: true);

            return (Ok: false, Message: fault!, ContentHash: signature);
        }

        var contentHash = WorldDefinitionFileSource.ComputeContentHash(content: content);

        if ((expectedContentHash is { } expected) && !string.Equals(a: expected, b: contentHash, comparisonType: StringComparison.Ordinal)) {
            return (Ok: false, Message: $"ScreenOpContentMismatch: '{contentPath}' hashes to {contentHash}, the recording pinned {expected} — the file moved or changed since it was captured", ContentHash: contentHash);
        }

        if (!TryResolveEngine(engineId: engineId, engine: out var engine, error: out var engineError)) {
            MachineLifecycleTap?.Invoke(arg1: index, arg2: true);

            // Content was read and hashed before engine resolution, so this failure still pins the signature —
            // a replay whose file now hashes differently is caught here too, same as the construction-rejects
            // path below.
            return (Ok: false, Message: engineError, ContentHash: contentHash);
        }

        IScreenMachine created;

        try {
            created = engine.Create(options: options, contentBytes: content, savePath: null, audioSampleRate: WorldMachineAudioRate.SampleRate);
        } catch (ArgumentException exception) {
            MachineLifecycleTap?.Invoke(arg1: index, arg2: true);

            // Still pin the hash: content WAS read and hashed even though construction rejected it (bad options),
            // so a replay whose file now hashes differently is still caught, never silently retried unpinned.
            return (Ok: false, Message: exception.Message, ContentHash: contentHash);
        }

        LeaveLink(index: index);
        slot.ClearMachine();
        slot.Machine = created;
        slot.MachineEngine = engine.Id;
        slot.MachineContentPath = contentPath;
        slot.MachineSourceEngine = ((engineId is { Length: > 0 }) ? engineId : engine.Id);
        slot.MachineOptions = options;
        slot.MachineContentHash = contentHash;
        slot.DeclaredFault = null;
        slot.FramesStepped = 0;
        MachineLifecycleTap?.Invoke(arg1: index, arg2: false);

        return (Ok: true, Message: $"screen {index} booted {engine.Id} '{Path.GetFileName(path: contentPath)}'{(string.IsNullOrWhiteSpace(value: options) ? "" : $" ({options})")}", ContentHash: contentHash);
    }

    /// <summary>Ejects a screen's live machine. Fails for an undeclared screen or a slot with no machine to eject.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <returns>Whether the eject succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryEject(int index) {
        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
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

    /// <summary>Returns the screen's live magazine and 0-based selector, or <see langword="false"/> when the screen declares
    /// no magazine.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="selected">The live 0-based selector.</param>
    /// <param name="magazine">The screen's magazine.</param>
    public bool TryMagazine(int index, out int selected, out WorldScreenMagazine magazine) {
        if (m_slots.TryGetValue(key: index, value: out var slot) && (slot.Magazine is { } value)) {
            selected = slot.SelectedEntry;
            magazine = value;

            return true;
        }

        selected = 0;
        magazine = null!;

        return false;
    }

    /// <summary>Points the screen's magazine selector at <paramref name="entry"/>. When that entry is a
    /// <see cref="WorldScreenSource.Machine"/> row, boots it through the SAME <see cref="TryBootMachine"/> sequence
    /// <see cref="TryInsert"/> uses — CAS-pinned identically, since a magazine entry's document-declared path is not
    /// immune to on-disk drift either; for any other entry kind the selector still moves (so the pointer always
    /// tracks) but nothing boots here — a non-machine entry is presentation's own concern
    /// (<c>Puck.World.WorldScreenBinder</c> observes the moved selector and applies its camera/capture/view source
    /// itself; see the campaign's <c>STATE.md</c>). Fails for an undeclared screen, a screen with no magazine, an
    /// out-of-range entry, or — for a machine entry — whatever <see cref="TryBootMachine"/> refuses for; a failed
    /// boot always reports <c>Ok: false</c>, never a disguised success.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="entry">The 0-based magazine entry to select.</param>
    /// <param name="expectedContentHash">REPLAY ONLY: the CAS pin a recorded tape entry carries, threaded to
    /// <see cref="TryBootMachine"/> when <paramref name="entry"/> resolves to a Machine row. Ignored for any other
    /// entry kind (nothing there reads a file). <see langword="null"/> for the live path.</param>
    /// <returns>Whether the selection (and, for a machine entry, the boot) succeeded, a message, and — for a machine
    /// entry — the observed content signature (<see langword="null"/> for a non-machine entry, since nothing was
    /// read).</returns>
    public (bool Ok, string Message, string? ContentHash) TrySelect(int index, int entry, string? expectedContentHash = null) {
        if (m_disposed) {
            return (Ok: false, Message: "machine host disposed", ContentHash: null);
        }

        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared", ContentHash: null);
        }

        if (slot.Magazine is not { } magazine) {
            return (Ok: false, Message: $"screen {index} has no magazine", ContentHash: null);
        }

        if ((entry < 0) || (entry >= magazine.Entries.Count)) {
            return (Ok: false, Message: $"entry {entry} is outside 0..{(magazine.Entries.Count - 1)}", ContentHash: null);
        }

        var source = magazine.Entries[entry];
        slot.SelectedEntry = entry;

        if (source is WorldScreenSource.Machine { ContentPath: { Length: > 0 } path } machine) {
            var (ok, message, contentHash) = TryBootMachine(index: index, slot: slot, contentPath: path, engineId: machine.Engine, options: machine.Options, expectedContentHash: expectedContentHash, documentRelative: true);

            // A failed boot is a failed Select — never a disguised success. The selector POINTER still moves
            // (above) regardless: the pointer-always-moves contract holds independent of boot outcome.
            return (Ok: ok, Message: $"{index} entry {entry}/{magazine.Entries.Count} {(ok ? message : $"selected (boot failed — {message})")}", ContentHash: contentHash);
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

    /// <summary>Reconfigures a screen's live machine across the engine's options vocabulary (dmg↔cgb↔agb with no
    /// reboot). Fails for an undeclared screen, a slot with no machine, a machine without the reconfigure capability,
    /// or an options string the engine rejects.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="options">The engine-specific options string to retarget to.</param>
    /// <returns>Whether the reconfigure succeeded, and a message describing the outcome.</returns>
    public (bool Ok, string Message) TryReconfigure(int index, string? options) {
        if (m_slots.TryGetValue(key: index, value: out var slot) is false) {
            return (Ok: false, Message: $"no screen {index} declared");
        }

        if (slot.Machine is not { } machine) {
            return (Ok: false, Message: $"screen {index} has no machine to reconfigure");
        }

        if (machine is not IReconfigurableMachine reconfigurable) {
            return (Ok: false, Message: $"screen {index}'s machine does not support live reconfiguration");
        }

        var previous = reconfigurable.Options;

        if (!reconfigurable.TryReconfigure(options: options, out var reason)) {
            return (Ok: false, Message: $"{index} '{previous}' -> '{options}' rejected: {reason}");
        }

        slot.MachineOptions = reconfigurable.Options;

        return (Ok: true, Message: $"{index} '{previous}' -> '{reconfigurable.Options}' reconfigured{((reason.Length > 0) ? $" — {reason}" : string.Empty)}");
    }

    /// <summary>Reads a screen's machine's current options string, or <see langword="false"/> when the screen has no
    /// reconfigurable machine.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    /// <param name="options">The current options string.</param>
    public bool TryReadOptions(int index, out string options) {
        if (m_slots.TryGetValue(key: index, value: out var slot) && (slot.Machine is IReconfigurableMachine reconfigurable)) {
            options = reconfigurable.Options;

            return true;
        }

        options = string.Empty;

        return false;
    }

    /// <summary>Returns the cable link a screen currently belongs to (by name), or <see langword="null"/>.</summary>
    /// <param name="index">The engine screen-surface index.</param>
    public string? LinkOf(int index) => (m_slots.TryGetValue(key: index, value: out var slot) ? slot.LinkName : null);

    /// <summary>Establishes (or reports dormant) a runtime cable link over two or more declared screens. Every member
    /// must be a declared screen carrying a machine from the SAME engine, and that engine must implement
    /// <c>IMachineLinkingEngine</c>; a set that cannot be linked is recorded DORMANT with a reason (never a throw), so
    /// a later insert can re-establish it. Fails outright only for an undeclared screen, a member named twice, a member
    /// already in another link, or fewer than two members.</summary>
    /// <param name="name">The link's stable name.</param>
    /// <param name="members">The engine screen indices in cable order.</param>
    /// <returns>Whether the link row was recorded, and a message describing live/dormant state.</returns>
    public (bool Ok, string Message) TryLink(string name, IReadOnlyList<int> members) {
        if (m_disposed) {
            return (Ok: false, Message: "machine host disposed");
        }

        if ((members is null) || (members.Count < 2)) {
            return (Ok: false, Message: $"link '{name}' needs two or more screens");
        }

        var seen = new HashSet<int>();

        foreach (var member in members) {
            if (m_slots.TryGetValue(key: member, value: out var slot) is false) {
                return (Ok: false, Message: $"no screen {member} declared");
            }

            if (!seen.Add(item: member)) {
                return (Ok: false, Message: $"screen {member} is named twice in link '{name}'");
            }

            if ((slot.LinkName is { } existing) && !string.Equals(a: existing, b: name, comparisonType: StringComparison.Ordinal)) {
                return (Ok: false, Message: $"screen {member} is already in link '{existing}'");
            }
        }

        TeardownLink(name: name);

        var (link, reason) = TryEstablishLink(members: members);
        var entry = new LinkEntry { Name = name, Members = [.. members], Link = link, DormantReason = reason };

        m_links[name] = entry;

        foreach (var member in members) {
            m_slots[member].LinkName = name;
        }

        return (Ok: true, Message: DescribeLink(entry: entry));
    }

    /// <summary>Severs a runtime cable link by name. Fails when no link of that name is live.</summary>
    /// <param name="name">The link name.</param>
    /// <returns>Whether the link existed, and a message.</returns>
    public (bool Ok, string Message) TryUnlink(string name) {
        if (!m_links.ContainsKey(key: name)) {
            return (Ok: false, Message: $"no link '{name}'");
        }

        TeardownLink(name: name);

        return (Ok: true, Message: $"link '{name}' severed");
    }

    /// <summary>Reads a live link's member screens by name.</summary>
    /// <param name="name">The link name.</param>
    /// <param name="members">The member screen indices in cable order, on success.</param>
    /// <returns>Whether a link of that name is live.</returns>
    public bool TryReadLinkMembers(string name, out IReadOnlyList<int> members) {
        if (m_links.TryGetValue(key: name, value: out var entry)) {
            members = entry.Members;

            return true;
        }

        members = [];

        return false;
    }

    /// <summary>Returns the live cable-link set as document rows — the <c>world.save</c> fold of every established/runtime
    /// link back into the <c>Links</c> section (cable order preserved).</summary>
    public IReadOnlyList<WorldScreenLink> CaptureLinks() {
        if (m_links.Count == 0) {
            return [];
        }

        var captured = new List<WorldScreenLink>(capacity: m_links.Count);

        foreach (var entry in m_links.Values) {
            captured.Add(item: new WorldScreenLink(Name: entry.Name, Screens: [.. entry.Members]));
        }

        return captured;
    }

    /// <summary>Describes every live cable link in one line (the <c>screen.links</c> query), or <c>none</c>.</summary>
    public string DescribeLinks() {
        if (m_links.Count == 0) {
            return "none";
        }

        return string.Join(separator: "; ", values: m_links.Values.Select(selector: DescribeLink));
    }

    /// <summary>Reconciles the DECLARED cable links to a mutated <c>links</c> section — two-phase, atomic per call:
    /// every stale-or-member-changed declared link tears down FIRST, in full, before anything is (re-)established.
    /// Tearing down every stale/changed row before establishing anything means a member a changed link is
    /// RECLAIMING is always free by the time that link is (re-)established, so a plain re-shape (an ordinary,
    /// non-conflicting move) always succeeds; two declared links that genuinely both claim the same screen within
    /// the SAME reconcile is a real document error and fails loudly (see below) rather than resolving unpredictably
    /// by document order.</summary>
    /// <param name="links">The mutated declared link rows (the live definition's links).</param>
    public void ReconcileLinks(IReadOnlyList<WorldScreenLink> links) {
        if (m_disposed) {
            return;
        }

        var declaredNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        var toTeardown = new List<string>();

        foreach (var link in links) {
            _ = declaredNames.Add(item: link.Name);

            if (m_links.TryGetValue(key: link.Name, value: out var existing) && existing.Declared && !MembersMatch(entry: existing, members: link.Screens)) {
                toTeardown.Add(item: link.Name);
            }
        }

        foreach (var entry in m_links.Values) {
            if (entry.Declared && !declaredNames.Contains(item: entry.Name)) {
                toTeardown.Add(item: entry.Name);
            }
        }

        // Phase 1, complete before phase 2 starts: every stale-or-changed declared link is gone, so no established
        // link can be silently blocking a screen a phase-2 TryLink call legitimately needs.
        foreach (var name in toTeardown) {
            TeardownLink(name: name);
        }

        foreach (var link in links) {
            if (m_links.TryGetValue(key: link.Name, value: out var existing) && existing.Declared && MembersMatch(entry: existing, members: link.Screens)) {
                continue;
            }

            var (ok, message) = TryLink(name: link.Name, members: link.Screens);

            if (m_links.TryGetValue(key: link.Name, value: out var reconciled)) {
                reconciled.Declared = true;
            }

            // Establishment failure is surfaced loudly rather than discarded. A DORMANT link (Ok: true, no live
            // IMachineLink — mismatched engines, no machine yet) already reports through DescribeLink/screen.links;
            // this covers the harder failure TryLink returns Ok: false for (an undeclared screen, fewer than two
            // members, a duplicate member, or two declared rows racing for the same screen in one reconcile): that
            // outcome never reaches m_links, so screen.links would otherwise show nothing for a link the document
            // still declares, with no sign anything went wrong.
            if (!ok) {
                Console.Error.WriteLine(value: $"[world.link: '{link.Name}' failed to establish — {message}]");
            }
        }
    }

    /// <summary>Reconciles the host's machine slots to a mutated screen list — the live-application half of an
    /// <c>UpsertScreen</c>/<c>RemoveScreen</c> world mutation, called from <see cref="WorldServer"/>'s own Install
    /// path when the definition changes. REMOVALS are reconciled first: a slot whose index is no longer declared has
    /// its machine disposed and its entry dropped — the CALLER is responsible for the engagement-side admin cleanup
    /// (<see cref="WorldEngagement.DisengageScreen"/>) over the returned indices, since this type holds no grant-table
    /// reference by design. Then, for a declared index whose source CHANGED, machine boots/ejects; a non-machine
    /// source change is a no-op here (presentation applies it).</summary>
    /// <param name="screens">The mutated screen list (the live definition's screens).</param>
    /// <returns>The screen indices removed this call — feed each to <see cref="WorldEngagement.DisengageScreen"/>.</returns>
    public IReadOnlyList<int> ReconcileScreens(IReadOnlyList<WorldScreen> screens) {
        if (m_disposed) {
            return [];
        }

        m_reconcileRemovals.Clear();

        foreach (var index in m_slots.Keys) {
            if (!DeclaresIndex(screens: screens, index: index)) {
                m_reconcileRemovals.Add(item: index);
            }
        }

        foreach (var index in m_reconcileRemovals) {
            LeaveLink(index: index);

            if (m_slots.Remove(key: index, value: out var slot)) {
                slot.Machine?.Dispose();
            }
        }

        foreach (var screen in screens) {
            if (m_slots.TryGetValue(key: screen.Index, value: out var slot) is false) {
                // CREATE the slot, mirroring the constructor — this type carries no GPU provider key set (that
                // constraint is Puck.World.WorldScreenBinder's own, presentation-only, and does not apply here), so
                // there is no reason to permanently forget an index. Covers BOTH a genuinely-new index (never
                // declared at boot) and an index that was declared, removed (a RemoveScreen mutation's removal pass
                // above), and is now re-declared (a later UpsertScreen, or a world.reset/.load/.reload whose
                // definition still names it). DeclaredSource starts null so the Equals check below never
                // short-circuits a fresh slot.
                slot = new MachineSlot { Index = screen.Index, DeclaredSource = null };
                m_slots[screen.Index] = slot;
            }

            slot.Magazine = screen.Magazine;

            if (screen.Magazine is { } magazine) {
                slot.SelectedEntry = Math.Clamp(value: slot.SelectedEntry, min: 0, max: Math.Max(val1: 0, val2: (magazine.Entries.Count - 1)));
            } else {
                slot.SelectedEntry = 0;
            }

            if (!m_documentDirectoryChanged && Equals(objA: slot.DeclaredSource, objB: screen.Source)) {
                continue;
            }

            slot.DeclaredSource = screen.Source;

            switch (screen.Source) {
                case WorldScreenSource.Machine { ContentPath: { Length: > 0 } path } machine:
                    var (ok, message, _) = TryBootMachine(index: screen.Index, slot: slot, contentPath: path, engineId: machine.Engine, options: machine.Options, expectedContentHash: null, documentRelative: true);

                    Console.Error.WriteLine(value: $"[world.screen: {(ok ? message : $"{screen.Index} {message}")}]");

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

                        Console.Error.WriteLine(value: $"[world.screen: {(ejectOk ? ejectMessage : $"{screen.Index} {ejectMessage}")}]");
                    }

                    break;
            }
        }

        m_documentDirectoryChanged = false;

        return [.. m_reconcileRemovals];
    }

    /// <summary>Advances every booted machine by one host-owned fixed simulation step, fed by
    /// <paramref name="pads"/> — <see cref="WorldEngagement.BuildPadSnapshot"/>'s result, read DIRECTLY in-process
    /// (no client/wire round-trip; see <see cref="WorldServer.Step"/>'s call site, right after
    /// <see cref="WorldEngagement.FoldTick"/>). A live cable link steps as ONE unit with its members' merged pads in
    /// cable order; its member slots are then skipped below. The exact-rational T-cycle bridge (a machine's own
    /// internal tick-to-cycle conversion) is preserved verbatim: <paramref name="stepTicks"/> is forwarded exactly as
    /// the pre-inversion presentation-side <c>WorldScreenBinder.AdvanceMachines</c> forwarded it — cart RTC still
    /// derives from this tick budget, NEVER wall clock.</summary>
    /// <param name="stepTicks">The exact engine-tick budget of one fixed simulation step.</param>
    /// <param name="pads">This tick's per-screen merged engagement pad lane.</param>
    public void Advance(ulong stepTicks, ReadOnlyMemory<ScreenPadSnapshot> pads) {
        if (m_disposed) {
            return;
        }

        StepLiveLinks(stepTicks: stepTicks, pads: pads.Span);

        foreach (var slot in m_slots.Values) {
            if (slot.Machine is not { } machine) {
                continue;
            }

            if ((slot.LinkName is { } linkName) && m_links.TryGetValue(key: linkName, value: out var entry) && (entry.Link is not null)) {
                continue;
            }

            var input = EngagedPad(pads: pads.Span, screenIndex: slot.Index);

            AnyEverPumped = true;

            if (machine is IQueuedScreenMachine queued) {
                var submission = queued.Submit(deltaTicks: stepTicks, input: in input);

                if ((submission == QueuedMachineSubmission.Rejected) && machine.IsAssigned) {
                    throw new InvalidOperationException(
                        message: $"Screen {slot.Index}'s queued machine rejected an authoritative tick/input segment" +
                                 ((queued.QueueFault is { } fault) ? $" ({fault})." : ".")
                    );
                }

                slot.FramesStepped = queued.CompletedSteps;
            } else if (machine.Step(deltaTicks: stepTicks, input: in input)) {
                ++slot.FramesStepped;
            }
        }
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
                inputs[index] = EngagedPad(pads: pads, screenIndex: entry.Members[index]);
            }

            link.Step(deltaTicks: stepTicks, inputs: inputs);

            foreach (var member in entry.Members) {
                if (m_slots.TryGetValue(key: member, value: out var slot) && (slot.Machine is IQueuedScreenMachine queued)) {
                    slot.FramesStepped = queued.CompletedSteps;
                }
            }
        }
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

                if (m_engines.TryGet(key: id, extension: out var engine) && (engine is IMachineLinkingEngine linking)) {
                    linkingEngine = linking;
                } else {
                    return (Link: null, Reason: $"engine '{id}' has no linking capability");
                }
            } else if (!string.Equals(a: engineId, b: id, comparisonType: StringComparison.Ordinal)) {
                return (Link: null, Reason: $"mixed engines ('{engineId}' and '{id}') cannot be cable-linked");
            }

            machines.Add(item: machine);
        }

        return (linkingEngine!.TryLink(machines: machines, out var link, out var reason)
            ? (Link: link, Reason: null)
            : (Link: null, Reason: reason));
    }

    private void TeardownLink(string name) {
        if (!m_links.Remove(key: name, value: out var entry)) {
            return;
        }

        entry.Link?.Dispose();

        foreach (var member in entry.Members) {
            if (m_slots.TryGetValue(key: member, value: out var slot) && string.Equals(a: slot.LinkName, b: name, comparisonType: StringComparison.Ordinal)) {
                slot.LinkName = null;
            }
        }
    }

    private void LeaveLink(int index) {
        if (m_slots.TryGetValue(key: index, value: out var slot) && (slot.LinkName is { } name)) {
            TeardownLink(name: name);
        }
    }

    private static string DescribeLink(LinkEntry entry) {
        var members = string.Join(separator: "+", values: entry.Members);

        return ((entry.Link is { } link)
            ? $"{entry.Name} {members} live transfers={link.CompletedTransfers}"
            : $"{entry.Name} {members} dormant ({entry.DormantReason ?? "unestablishable"})");
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

    private static bool DeclaresIndex(IReadOnlyList<WorldScreen> screens, int index) {
        foreach (var screen in screens) {
            if (screen.Index == index) {
                return true;
            }
        }

        return false;
    }

    private void BootDeclaredMachine(MachineSlot slot, WorldScreenSource.Machine machine) {
        if (!m_engines.TryGet(key: machine.Engine, extension: out var engine)) {
            slot.DeclaredFault = $"no screen-machine engine '{machine.Engine}'";
            Console.Error.WriteLine(value: $"[world.screen: {slot.Index} {slot.DeclaredFault}]");

            return;
        }

        if (!TryReadContent(contentPath: machine.ContentPath, documentRelative: true, content: out var content, fault: out var fault)) {
            slot.DeclaredFault = fault;
            Console.Error.WriteLine(value: $"[world.screen: {slot.Index} {slot.DeclaredFault}]");

            return;
        }

        try {
            slot.Machine = engine.Create(options: machine.Options, contentBytes: content, savePath: null, audioSampleRate: WorldMachineAudioRate.SampleRate);
            slot.MachineEngine = engine.Id;
            slot.MachineContentPath = machine.ContentPath;
            slot.MachineSourceEngine = machine.Engine;
            slot.MachineOptions = machine.Options;
            slot.MachineContentHash = WorldDefinitionFileSource.ComputeContentHash(content: content);
        } catch (ArgumentException exception) {
            slot.DeclaredFault = exception.Message;
            Console.Error.WriteLine(value: $"[world.screen: {slot.Index} {slot.DeclaredFault}]");
        }
    }

    private bool TryResolveEngine(string? engineId, out IScreenMachineEngine engine, out string error) {
        if (engineId is { } id) {
            if (m_engines.TryGet(key: id, extension: out var named)) {
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
            : $"which engine? {m_engines.Count} registered — name one of: {string.Join(separator: ", ", values: m_engines.Keys)}");

        return false;
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
                ? Path.GetFullPath(path: Path.Combine(path1: directory, path2: contentPath))
                : Path.GetFullPath(path: contentPath));
        } catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) {
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

    private static string? DocumentDirectory(string? documentPath) => ((documentPath is { Length: > 0 } path)
        ? Path.GetDirectoryName(path: Path.GetFullPath(path: path))
        : null);

    // One cable link beside m_slots: its name, member screen indices (cable order), the live IMachineLink (null when
    // dormant), and the dormant reason.
    private sealed class LinkEntry {
        public required string Name { get; init; }
        public required int[] Members { get; init; }
        public IMachineLink? Link { get; set; }
        public string? DormantReason { get; set; }
        public bool Declared { get; set; }
    }

    // One declared screen's machine slot: the persistent declared source (so ReconcileScreens can diff it), the
    // magazine + live selector, and at most one booted machine plus the bookkeeping world.save/screen.state need.
    private sealed class MachineSlot {
        public required int Index { get; init; }
        public WorldScreenSource? DeclaredSource { get; set; }
        public WorldScreenMagazine? Magazine { get; set; }
        public int SelectedEntry { get; set; }
        public IScreenMachine? Machine { get; set; }
        public string? LinkName { get; set; }
        public string? MachineEngine { get; set; }
        public string? MachineContentPath { get; set; }
        public string? MachineSourceEngine { get; set; }
        public string? MachineOptions { get; set; }
        public string? MachineContentHash { get; set; }
        public string? DeclaredFault { get; set; }
        public long FramesStepped { get; set; }

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
