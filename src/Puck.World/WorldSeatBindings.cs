using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The World-side per-seat <see cref="IInputBindings"/> the <see cref="InputRouter"/> resolves through. It holds one
/// <see cref="PagedInputBindings"/> per local seat, each compiled from that
/// seat's composed document (engine default ⊕ world overlays ⊕ the seat's profile bindings ⊕ its live session
/// rebinds). Composition and compilation happen only on a CHANGE (a profile selection, a rebind, an overlay mutation)
/// — never per frame; the per-signal resolve path stays the existing paged lookups. A seat's runtime MODE is NOT a
/// layer: every group (play, editor) is always compiled in, and the seat's ACTIVE group derives as
/// <c>first matching context row's group (document order) ?? the seat's requested group (<see cref="SetActiveGroup"/>
/// — the mode) ?? the profile's default group</c>, applied as a pointer-level switch on the compiled profile. Context
/// rows are the composed document's <c>contexts</c> section keyed on the published per-seat family states
/// (<see cref="WorldContextFamilies"/>) pushed in through <see cref="SetContextState"/>; across families the first
/// matching row wins and a shadowed later match is reported, never silent
/// (<see cref="DescribeContextDerivation"/> — the <c>player.bindings</c> read-back).
/// </summary>
/// <remarks>Single-threaded, like every input-fold type here: recomposition runs on the launcher's window-pump thread
/// (a verb handler, a roster mutation, or the post-step overlay sync), and <see cref="Resolve(int, in InputSignal)"/>
/// runs on the same thread inside the router's snapshot fold. No lock guards this state. Constructed early in
/// composition (before the container is built) from the engine default and the boot world definition — both
/// pure/available there; the per-seat profile and session layers start null (every seat inherits the engine default at boot), and
/// the roster/verbs push them in as they change. Chord-command edges (<see cref="IChordEdgeSource"/>) forward to the
/// resolving seat's paged bindings.</remarks>
internal sealed class WorldSeatBindings : IInputBindings, IChordEdgeSource {
    // The admitted context families this resolver tracks per seat, in WorldContextFamilies.Families order, and each
    // seat's boot state per family (nothing joined, nothing engaged, until the post-step sync publishes otherwise).
    private static readonly string[] s_contextFamilies = [.. WorldContextFamilies.Families];
    private static readonly string[] s_contextBootStates = [WorldContextFamilies.RosterUnjoined, WorldContextFamilies.EngagementNone];
    private readonly BindingProfileDocument m_engineDefault;
    private readonly PagedInputBindings[] m_seats;
    private readonly BindingProfileDocument?[] m_profileBindings;
    private readonly BindingProfileDocument?[] m_sessionRebinds;
    // A seat's control feel travels with the same profile its bindings do, so the two are delivered together through
    // SetProfileLayers and this type owns the push. It does not own the STATE — WorldSeatFeel is the shared store the
    // orbit, the frame source, and world.view.orbit all read.
    private readonly WorldSeatFeel m_seatFeel;
    // Per seat: [family index] → the family's current published state; the composed document's context rows; and the
    // seat's REQUESTED group (the mode pointer — what context rows override).
    private readonly string[][] m_contextStates;
    private readonly IReadOnlyList<BindingContextDefinition>[] m_seatContexts;
    private readonly string?[] m_requestedGroups;
    private IReadOnlyList<WorldBindingOverlay> m_overlays;
    // The exact channel row list Channels was compiled from — the second half of SyncDefinition's change test, so a
    // channels-only mutation (which leaves the overlay list reference-equal) still re-derives the table.
    private IReadOnlyList<WorldChannel> m_channelSource;

    /// <summary>The number of local seats this router resolves for.</summary>
    public const int SeatCount = WorldPopulation.LocalSeatCount;

    /// <summary>The channel vocabulary these seats' bindings resolve against — the BOOT instance's live table,
    /// re-derived by <see cref="SyncDefinition"/> whenever an applied mutation swaps the definition, so a seat is
    /// never linted against a channel set the world has since changed. The one table the seat-side vocabulary
    /// callers read; there is deliberately no process-global alternative to reach for.</summary>
    public WorldChannelTable Channels { get; private set; }

    /// <summary>Initializes a new instance over the engine-default document and the boot world definition. Every
    /// seat starts compiled from the composed base (default ⊕ overlays); profile and session layers are null.</summary>
    /// <param name="engineDefault">The engine-default binding document (layer 0).</param>
    /// <param name="definition">The boot world definition, supplying both the binding overlays (layer 1..) and the
    /// channel table those overlays name.</param>
    /// <param name="seatFeel">The shared per-seat control-feel store this type pushes a selected profile's feel into.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldSeatBindings(BindingProfileDocument engineDefault, WorldDefinition definition, WorldSeatFeel seatFeel) {
        ArgumentNullException.ThrowIfNull(argument: engineDefault);
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: seatFeel);

        m_seatFeel = seatFeel;
        m_engineDefault = engineDefault;
        m_overlays = (definition.BindingOverlays ?? []);
        m_channelSource = definition.Channels;
        Channels = WorldChannelTable.Compile(channels: definition.Channels);
        m_profileBindings = new BindingProfileDocument?[SeatCount];
        m_sessionRebinds = new BindingProfileDocument?[SeatCount];
        m_contextStates = new string[SeatCount][];
        m_seatContexts = new IReadOnlyList<BindingContextDefinition>[SeatCount];
        m_requestedGroups = new string?[SeatCount];

        var seedDocument = ComposeSeat(slot: 0);
        var seedBase = BindingProfile.Compile(document: seedDocument);

        m_seats = new PagedInputBindings[SeatCount];

        for (var slot = 0; (slot < SeatCount); slot++) {
            m_seats[slot] = new PagedInputBindings(profile: seedBase);
            m_contextStates[slot] = [.. s_contextBootStates];
            m_seatContexts[slot] = (seedDocument.Contexts ?? []);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) {
        return (((uint)slot < SeatCount) ? m_seats[slot].Resolve(slot: slot, source: source) : null);
    }

    /// <inheritdoc/>
    public IReadOnlyList<CommandBinding>? Resolve(int slot, in InputSignal signal) {
        return (((uint)slot < SeatCount) ? m_seats[slot].Resolve(slot: slot, signal: in signal) : null);
    }

    /// <inheritdoc/>
    public ReadOnlySpan<BindingChordEdge> DrainChordEdges(int slot) {
        return (((uint)slot < SeatCount) ? m_seats[slot].DrainChordEdges(slot: slot) : []);
    }

    /// <inheritdoc/>
    public IReadOnlyList<(int Slot, BindingChordEdge Edge)> DrainScheduledEdges() {
        // Scheduled edges (a Tapped row activator's deferred release) are not seat-scoped at the CALL site — the
        // router asks once per tick, not once per seat — so this aggregates every seat's own PagedInputBindings.
        // Most ticks every seat's list is empty; allocate only when at least one seat actually has something due.
        List<(int Slot, BindingChordEdge Edge)>? due = null;

        for (var slot = 0; (slot < SeatCount); slot++) {
            var seatDue = m_seats[slot].DrainScheduledEdges();

            if (seatDue.Count == 0) {
                continue;
            }

            (due ??= []).AddRange(collection: seatDue);
        }

        return (((IReadOnlyList<(int Slot, BindingChordEdge Edge)>?)due) ?? []);
    }

    /// <summary>Delivers everything a selected profile carries to a seat — its binding layer and its control feel —
    /// and recomposes that seat. Called by the roster on a profile selection / join / live identity switch. ONE door
    /// for the whole profile rather than one per layer: a seat is handed a coherent set at a single moment, and a
    /// layer added later cannot be delivered at some call sites and forgotten at others.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="bindings">The profile's binding section, or <see langword="null"/> for the engine default.</param>
    /// <param name="seatLook">The profile's control feel, or <see langword="null"/> to keep the world's own.</param>
    public void SetProfileLayers(int slot, BindingProfileDocument? bindings, WorldSeatLook? seatLook) {
        if ((uint)slot >= SeatCount) {
            return;
        }

        m_profileBindings[slot] = bindings;
        m_seatFeel.SetProfileLook(slot: slot, look: seatLook);
        RecomposeSeat(slot: slot);
    }

    /// <summary>Sets a seat's live session-rebind layer and recomposes that seat — the <c>player.bind</c> path. The layer
    /// is unsaved until <c>identity.bindings.save</c> folds it into the seat's identity; passing null clears it.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="rebinds">The session rebind document, or <see langword="null"/> to clear it.</param>
    public void SetSessionRebind(int slot, BindingProfileDocument? rebinds) {
        if ((uint)slot >= SeatCount) {
            return;
        }

        m_sessionRebinds[slot] = rebinds;
        RecomposeSeat(slot: slot);
    }

    /// <summary>Sets a seat's REQUESTED page group — the runtime mode switch (<c>editor.enter</c>/<c>exit</c>) — and
    /// re-derives the active group. The request is the middle step of the derivation (context row → requested group →
    /// profile default), so a currently-matching context row keeps the seat's ACTIVE group derived from the row while
    /// the request is remembered (and reported as shadowed by <see cref="DescribeContextDerivation"/>); it applies the
    /// moment no row matches. The apply is a pointer-level switch on the seat's already-compiled profile: no recompose,
    /// no document churn, and the seat's press latches, held chord, and armed command chords survive (see
    /// <see cref="PagedInputBindings.SetActiveGroup"/>). Per-seat by design: a mode is one seat's state, never a world
    /// <c>bindingOverlays</c> mutation (those re-bind every seat).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="group">The group to request (e.g. <see cref="WorldEditorBindings.GroupId"/>), or
    /// <see langword="null"/> for the profile's default group.</param>
    /// <returns><see langword="false"/> when the seat's compiled profile declares no such group (the request is not
    /// recorded).</returns>
    public bool SetActiveGroup(int slot, string? group) {
        if (((uint)slot >= SeatCount) || ((group is not null) && !m_seats[slot].HasGroup(group: group))) {
            return false;
        }

        m_requestedGroups[slot] = group;
        DeriveActiveGroup(slot: slot);

        return true;
    }

    /// <summary>Publishes one admitted context family's current state for a seat (see
    /// <see cref="WorldContextFamilies"/>) and re-derives the seat's active group when the state changed — the
    /// post-step sync path (<see cref="WorldSimulation"/> reads the roster and the server's Control-route table each
    /// tick and pushes the two family states here). An unadmitted family is ignored.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="family">The admitted family name (e.g. <see cref="WorldContextFamilies.Engagement"/>).</param>
    /// <param name="state">The family's current state for the seat (e.g.
    /// <see cref="WorldContextFamilies.EngagementEngaged"/>).</param>
    public void SetContextState(int slot, string family, string state) {
        var familyIndex = Array.IndexOf(array: s_contextFamilies, value: family);

        if (((uint)slot >= SeatCount) || (familyIndex < 0) || string.Equals(a: m_contextStates[slot][familyIndex], b: state, comparisonType: StringComparison.Ordinal)) {
            return;
        }

        m_contextStates[slot][familyIndex] = state;
        DeriveActiveGroup(slot: slot);
    }

    /// <summary>Describes a seat's full context derivation for the <c>player.bindings</c> read-back: each admitted
    /// family's current state with its matched row (winner or shadowed), the requested group, and the finally-resolved
    /// active group with its derivation step.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The derivation echo (slot 0's for an out-of-range slot).</returns>
    public WorldContextDerivationEcho DescribeContextDerivation(int slot) {
        slot = (((uint)slot < SeatCount) ? slot : 0);

        var (winnerFamily, winnerState, _) = FirstMatch(slot: slot);
        var families = new WorldContextFamilyEcho[s_contextFamilies.Length];

        for (var familyIndex = 0; (familyIndex < s_contextFamilies.Length); familyIndex++) {
            var family = s_contextFamilies[familyIndex];
            var state = m_contextStates[slot][familyIndex];
            string? matchedGroup = null;

            foreach (var row in m_seatContexts[slot]) {
                if (string.Equals(a: row.Family, b: family, comparisonType: StringComparison.Ordinal) &&
                    string.Equals(a: row.State, b: state, comparisonType: StringComparison.Ordinal)) {
                    matchedGroup = row.Group;

                    break;
                }
            }

            families[familyIndex] = new WorldContextFamilyEcho(
                Family: family,
                Group: matchedGroup,
                State: state,
                Wins: ((matchedGroup is not null) && string.Equals(a: winnerFamily, b: family, comparisonType: StringComparison.Ordinal))
            );
        }

        var requested = m_requestedGroups[slot];
        var requestedApplies = ((requested is not null) && m_seats[slot].HasGroup(group: requested));

        return new WorldContextDerivationEcho(
            ActiveGroup: m_seats[slot].ViewFor(slot: slot).Group,
            Families: families,
            RequestedGroup: requested,
            RequestedShadowed: ((winnerFamily is not null) && (requested is not null)),
            Step: ((winnerFamily is not null)
                ? $"context {winnerFamily}={winnerState}"
                : (requestedApplies ? "requested" : "default"))
        );
    }

    /// <inheritdoc/>
    public void ResetAll() {
        for (var slot = 0; (slot < SeatCount); slot++) {
            m_seats[slot].Reset(slot: slot);
        }
    }

    /// <summary>The immutable view of the page the seat's held chord currently selects — the binding bar's read
    /// seam (a single volatile reference read; see <see cref="PagedInputBindings.ViewFor"/>).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The active page's precomputed view (slot 0's for an out-of-range slot).</returns>
    public BindingPageView PageView(int slot) =>
        m_seats[(((uint)slot < SeatCount) ? slot : 0)].ViewFor(slot: slot);

    /// <summary>The wheel the seat's ACTIVE page presents, or <see langword="null"/> when no wheel is held open —
    /// the radial presenter's one open/closed read (see <see cref="PagedInputBindings.WheelFor"/>).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The active wheel view, or <see langword="null"/> (always <see langword="null"/> for an out-of-range
    /// slot).</returns>
    public BindingWheelView? WheelView(int slot) =>
        (((uint)slot < SeatCount) ? m_seats[slot].WheelFor(slot: slot) : null);

    /// <summary>The seat's current live session-rebind layer, or <see langword="null"/> when it has none.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public BindingProfileDocument? SessionRebind(int slot) => (((uint)slot < SeatCount) ? m_sessionRebinds[slot] : null);

    /// <summary>The document the seat currently resolves through — the full composed stack (engine default ⊕ overlays ⊕
    /// profile ⊕ session), with the SAME unregistered-page skip <see cref="RecomposeSeat"/> applies (silent here —
    /// this read never narrates; the recompose that already ran, or the boot sweep, already did) so the
    /// <c>player.bindings</c> echo never claims a dead page the seat cannot actually resolve.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The composed document, or the composed base for an out-of-range slot.</returns>
    public BindingProfileDocument ComposedDocument(int slot) => SkipUnregisteredPages(document: (((uint)slot < SeatCount) ? ComposeSeat(slot: slot) : ComposeBase()), label: null);

    /// <summary>Reflects a changed definition (an applied bindings or <c>channels</c> mutation),
    /// re-deriving <see cref="Channels"/> and recomposing every seat. A definition whose overlay list AND channel
    /// list are both reference-equal to the held ones (the common per-step case) short-circuits, so the post-step
    /// call this feeds costs two comparisons on an unchanged tick. Both halves are tested because a channel a seat
    /// binds and the channel table that declares it are one document's two faces — refreshing one without the other
    /// is how a seat comes to be linted against a vocabulary the world no longer has.</summary>
    /// <param name="definition">The server's live world definition.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    public void SyncDefinition(WorldDefinition definition) {
        ArgumentNullException.ThrowIfNull(argument: definition);

        // The world's own control feel re-points BEFORE the overlay/channel early-return below: a
        // world.row.set playerDefaults.seatLook changes neither the overlay list nor the channel rows, so it would
        // otherwise be skipped entirely and a live feel edit would never reach a seat. Every seat still sitting at the
        // world's floor picks the new policy up on its next drag; a seat carrying its own profile's feel is
        // untouched, which is the whole point of the split.
        m_seatFeel.SetWorldLook(worldLook: definition.PlayerDefaults.SeatLook);

        var overlays = definition.BindingOverlays;
        var channels = definition.Channels;

        if (ReferenceEquals(objA: overlays, objB: m_overlays) && ReferenceEquals(objA: channels, objB: m_channelSource)) {
            return;
        }

        m_overlays = (overlays ?? []);
        m_channelSource = channels;
        Channels = WorldChannelTable.Compile(channels: channels);

        for (var slot = 0; (slot < SeatCount); slot++) {
            RecomposeSeat(slot: slot);
        }
    }

    /// <summary>Runs the affordance-vocabulary check over every seat's composed document and prints one loud line per
    /// finding — the composition root's post-build sweep covering the layers that composed BEFORE the vocabulary
    /// existed (the engine default and the world's boot overlays compile at construction, pre-container). Findings do
    /// not un-bind anything: the boot mapping already resolved, and a dead entry resolves to nothing at dispatch — the
    /// sweep exists so that silence is loud instead. Runs the SAME unregistered-page skip <see cref="RecomposeSeat"/>
    /// applies first (one narration line per page, e.g. the whole <c>editor</c>/<c>sculpt</c> groups on a boot shape
    /// that never registered them), then reports whatever vocabulary findings remain in the filtered document per
    /// entry — a genuine bindability or value-kind mistake still gets its full detail.</summary>
    public void ValidateAffordancesLoudly() {
        var errors = new List<string>();
        var sweptBase = false;

        for (var slot = 0; (slot < SeatCount); slot++) {
            // A seat with no profile/session layer composes to the same base document as every other such seat —
            // sweep that document once and attribute it to the base rather than four times to four seats.
            var isBase = ((m_profileBindings[slot] is null) && (m_sessionRebinds[slot] is null));

            if (isBase && sweptBase) {
                continue;
            }

            var label = (isBase ? "composed base" : $"seat {(slot + 1)}");
            var document = SkipUnregisteredPages(document: ComposeSeat(slot: slot), label: label);

            errors.Clear();
            WorldAffordances.Validate(document: document, channels: Channels, errors: errors);

            foreach (var error in errors) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: {error}");
            }

            sweptBase |= isBase;
        }
    }

    private void RecomposeSeat(int slot) {
        var label = $"seat {(slot + 1)}";
        // Drop any page (or bare-command row) naming a command outside THIS composition's registered vocabulary —
        // the engine-default document always compiles in the editor/sculpt groups, but a headless boot never
        // registers EditorCommandModule, so those pages are unreachable by construction, not a per-seat mistake. One
        // narration line per skipped page, keyed on the registration FACT (WorldAffordances.IsCommandRegistered),
        // never a headless boolean — a mixed page (e.g. the play group's base page, which folds editor.enter beside
        // its movement rows) keeps its registered entries and loses only the unregistered ones.
        var document = SkipUnregisteredPages(document: ComposeSeat(slot: slot), label: label);

        // Compile the seat's composed document and hot-swap it. Whatever vocabulary trouble survives the skip above
        // (a bindability or value-kind mistake — a REAL authoring error, not a registration gap) still rejects the
        // whole recompose loudly, keeping the seat on its prior mapping rather than taking the input path down.
        if (RejectedByVocabulary(document: document, channels: Channels, label: label)) {
            return;
        }

        try {
            m_seats[slot].Reload(profile: BindingProfile.Compile(document: document));
        } catch (ArgumentException exception) {
            Console.Error.WriteLine(value: $"[player.bindings] {label} recompose rejected ({exception.Message.ReplaceLineEndings(replacementText: " ")}); keeping the prior mapping.");

            return;
        }

        // The new document's context rows replace the seat's cached set, and the active group re-derives against
        // them (the reload already re-applied the last APPLIED group; the derivation may now pick a different one).
        m_seatContexts[slot] = (document.Contexts ?? []);
        DeriveActiveGroup(slot: slot);
    }

    // The derivation, applied: first matching context row's group (document order) ?? the seat's requested group ??
    // null (the profile default). The apply is the existing pointer-level group switch; a winner row's group is
    // declared by construction (BindingProfile.Compile refuses a contexts row naming an undeclared group).
    private void DeriveActiveGroup(int slot) {
        var (_, _, winnerGroup) = FirstMatch(slot: slot);

        _ = m_seats[slot].SetActiveGroup(slot: slot, group: (winnerGroup ?? m_requestedGroups[slot]));
    }

    // The first context row (document order) whose family currently holds the row's state for the seat — the
    // across-family precedence rule: authored row order, first match wins.
    private (string? Family, string? State, string? Group) FirstMatch(int slot) {
        foreach (var row in m_seatContexts[slot]) {
            var familyIndex = Array.IndexOf(array: s_contextFamilies, value: row.Family);

            if ((familyIndex >= 0) && string.Equals(a: m_contextStates[slot][familyIndex], b: row.State, comparisonType: StringComparison.Ordinal)) {
                return (Family: row.Family, State: row.State, Group: row.Group);
            }
        }

        return (Family: null, State: null, Group: null);
    }

    // The vocabulary half of the recompose gate: print every finding and keep the prior mapping when the composed
    // document references commands the registry does not carry (or sends them the wrong value kind). Skipped (returns
    // false) until the composition root installs the vocabulary.
    private static bool RejectedByVocabulary(BindingProfileDocument document, WorldChannelTable channels, string label) {
        if (!WorldAffordances.Installed) {
            return false;
        }

        var errors = new List<string>();

        WorldAffordances.Validate(document: document, channels: channels, errors: errors);

        if (errors.Count == 0) {
            return false;
        }

        foreach (var error in errors) {
            Console.Error.WriteLine(value: $"[player.bindings] {label} recompose rejected: {error}; keeping the prior mapping.");
        }

        return true;
    }

    // Drops every page (or bare-command row) naming a command outside the registered vocabulary, narrating ONE line
    // per skipped row rather than one per offending entry — the composer half of the gate, run BEFORE the
    // still-standing RejectedByVocabulary/ArgumentException gates above so a genuine mistake (bindability, value
    // kind) in what SURVIVES the skip still rejects loudly. A no-op (returns document unchanged) until the
    // composition root installs the vocabulary, matching RejectedByVocabulary's own guard. Building a new list only
    // when a row actually changes keeps the common (nothing unregistered) recompose allocation-free. A null label
    // filters SILENTLY — ComposedDocument's read-back reads the seat's own recompose (or the boot sweep) already
    // narrated the same finding; echoing it again on every read would trade one flood for another.
    private static BindingProfileDocument SkipUnregisteredPages(BindingProfileDocument document, string? label) {
        if (!WorldAffordances.Installed) {
            return document;
        }

        var rows = document.Chords;
        List<BindingChordDefinition>? rewritten = null;

        for (var rowIndex = 0; (rowIndex < rows.Count); rowIndex++) {
            var row = rows[rowIndex];
            var filtered = SkipUnregisteredEntries(row: row, label: label);

            if (ReferenceEquals(objA: filtered, objB: row)) {
                rewritten?.Add(item: row);

                continue;
            }

            rewritten ??= [.. rows.Take(count: rowIndex)];

            if (filtered is not null) {
                rewritten.Add(item: filtered);
            }
        }

        var chords = (((IReadOnlyList<BindingChordDefinition>?)rewritten) ?? rows);
        var wheels = SkipUnregisteredWheels(wheels: document.Wheels, chords: chords, label: label);

        if ((rewritten is null) && ReferenceEquals(objA: wheels, objB: document.Wheels)) {
            return document;
        }

        return (document with { Chords = chords, Wheels = wheels });
    }

    // The wheels' half of the registration skip, run against the ALREADY-FILTERED chord rows: a sector naming an
    // unregistered command drops (one narration per ring, the page-skip convention), a ring the drops leave below
    // the compiled minimum drops whole (a one-sector radial band selects nothing honestly), a wheel left ringless —
    // or whose hold page was itself dropped — drops whole. What survives always recompiles, so a registration gap
    // (a headless boot never registering the editor verbs a wheel's sectors commit) degrades the wheel instead of
    // rejecting the whole seat document. Returns the list UNCHANGED (by reference) when nothing was dropped.
    private static IReadOnlyList<BindingWheelDefinition>? SkipUnregisteredWheels(IReadOnlyList<BindingWheelDefinition>? wheels, IReadOnlyList<BindingChordDefinition> chords, string? label) {
        if (wheels is null) {
            return null;
        }

        List<BindingWheelDefinition>? rewritten = null;

        for (var wheelIndex = 0; (wheelIndex < wheels.Count); wheelIndex++) {
            var wheel = wheels[wheelIndex];
            var filtered = SkipUnregisteredSectors(wheel: wheel, chords: chords, label: label);

            if (ReferenceEquals(objA: filtered, objB: wheel)) {
                rewritten?.Add(item: wheel);

                continue;
            }

            rewritten ??= [.. wheels.Take(count: wheelIndex)];

            if (filtered is not null) {
                rewritten.Add(item: filtered);
            }
        }

        return ((rewritten is null) ? wheels : ((rewritten.Count > 0) ? rewritten : null));
    }

    // One wheel's half of the skip — see SkipUnregisteredWheels.
    private static BindingWheelDefinition? SkipUnregisteredSectors(BindingWheelDefinition wheel, IReadOnlyList<BindingChordDefinition> chords, string? label) {
        var holdPageSurvives = false;

        foreach (var row in chords) {
            if ((row.Page is { } page) && string.Equals(a: page.Id, b: wheel.HoldPage, comparisonType: StringComparison.Ordinal)) {
                holdPageSurvives = true;

                break;
            }
        }

        if (!holdPageSurvives) {
            if (label is not null) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: wheel for group \"{wheel.Group}\" skipped — its hold page \"{wheel.HoldPage}\" is not in this composition.");
            }

            return null;
        }

        List<BindingPageDefinition>? keptRings = null;
        var changed = false;

        for (var ringIndex = 0; (ringIndex < wheel.Rings.Count); ringIndex++) {
            var ring = wheel.Rings[ringIndex];
            var entries = (ring.Entries ?? []);
            List<BindingPageEntryDefinition>? keptSectors = null;
            List<string>? dropped = null;

            for (var sectorIndex = 0; (sectorIndex < entries.Count); sectorIndex++) {
                var sector = entries[sectorIndex];

                if (!string.IsNullOrEmpty(value: sector.Command) && !WorldAffordances.IsCommandRegistered(command: sector.Command)) {
                    (dropped ??= []).Add(item: sector.Command);
                    keptSectors ??= [.. entries.Take(count: sectorIndex)];

                    continue;
                }

                keptSectors?.Add(item: sector);
            }

            if (dropped is null) {
                keptRings?.Add(item: ring);

                continue;
            }

            changed = true;
            keptRings ??= [.. wheel.Rings.Take(count: ringIndex)];

            if ((keptSectors?.Count ?? 0) >= BindingWheelDefinition.MinSectorsPerRing) {
                if (label is not null) {
                    Console.Error.WriteLine(value: $"[player.bindings] {label}: wheel ring \"{ring.Id}\" (group \"{wheel.Group}\") skipped {dropped.Count} unregistered command{((dropped.Count == 1) ? "" : "s")} ({string.Join(separator: ", ", values: dropped)}) — not registered in this composition.");
                }

                keptRings.Add(item: (ring with { Entries = keptSectors! }));
            } else if (label is not null) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: wheel ring \"{ring.Id}\" (group \"{wheel.Group}\") skipped — fewer than {BindingWheelDefinition.MinSectorsPerRing} of its sectors are registered in this composition ({string.Join(separator: ", ", values: dropped)} dropped).");
            }
        }

        if (!changed) {
            return wheel;
        }

        if ((keptRings?.Count ?? 0) == 0) {
            if (label is not null) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: wheel for group \"{wheel.Group}\" skipped — none of its rings survive in this composition.");
            }

            return null;
        }

        return (wheel with { Rings = keptRings! });
    }

    // One row's half of the skip: a PAGE keeps its registered entries and loses only the unregistered ones (a mixed
    // page — the play group's base page folds editor.enter beside its movement rows — narrates once and survives with
    // its gameplay bindings intact); a bare-COMMAND row has no smaller unit to keep, so an unregistered one drops
    // whole. Returns the row UNCHANGED (by reference — the ReferenceEquals check above skips the allocation) when
    // nothing in it is unregistered, a rewritten row when some but not all of a page's entries are, or null when the
    // whole row is dropped. A channel destination (page entry or bare command) never names a command at all, so it is
    // never a candidate here — that half of the vocabulary is checked by RejectedByVocabulary, unconditionally, on
    // whatever survives.
    private static BindingChordDefinition? SkipUnregisteredEntries(BindingChordDefinition row, string? label) {
        if (row.Page is { } page) {
            var entries = page.Entries;
            List<BindingPageEntryDefinition>? kept = null;
            var droppedCount = 0;

            for (var entryIndex = 0; (entryIndex < entries.Count); entryIndex++) {
                var entry = entries[entryIndex];

                if (!string.IsNullOrEmpty(value: entry.Command) && !WorldAffordances.IsCommandRegistered(command: entry.Command)) {
                    droppedCount++;
                    kept ??= [.. entries.Take(count: entryIndex)];

                    continue;
                }

                kept?.Add(item: entry);
            }

            if (droppedCount == 0) {
                return row;
            }

            if (label is not null) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: page \"{page.Id}\" (group \"{row.Group}\") skipped {droppedCount} unregistered command{((droppedCount == 1) ? "" : "s")} — its commands are not registered in this composition.");
            }

            return (((kept?.Count ?? 0) > 0) ? (row with { Page = (page with { Entries = kept! }) }) : null);
        }

        if ((row.Command is { } command) && !string.IsNullOrEmpty(value: command.Command) && !WorldAffordances.IsCommandRegistered(command: command.Command)) {
            if (label is not null) {
                Console.Error.WriteLine(value: $"[player.bindings] {label}: chord [{string.Join(separator: '+', values: (row.Chord ?? []))}] (group \"{row.Group}\") skipped — its command is not registered in this composition.");
            }

            return null;
        }

        return row;
    }
    private BindingProfileDocument ComposeSeat(int slot) {
        return WorldBindingComposer.Compose(BaseLayers(profile: m_profileBindings[slot], session: m_sessionRebinds[slot]));
    }
    private BindingProfileDocument ComposeBase() {
        return WorldBindingComposer.Compose(BaseLayers(profile: null, session: null));
    }
    private BindingProfileDocument?[] BaseLayers(BindingProfileDocument? profile, BindingProfileDocument? session) {
        var layers = new BindingProfileDocument?[(m_overlays.Count + 3)];
        var index = 0;

        layers[index++] = m_engineDefault;

        foreach (var overlay in m_overlays) {
            layers[index++] = overlay.Document;
        }

        layers[index++] = profile;
        // Live session rebinds compose LAST — the freshest authoring wins within every group.
        layers[index] = session;

        return layers;
    }
}
