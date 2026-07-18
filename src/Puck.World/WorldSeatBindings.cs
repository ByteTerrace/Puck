using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The World-side per-seat <see cref="IInputBindings"/> the <see cref="InputRouter"/> resolves through — the successor
/// of the single shared table. It holds one <see cref="PagedInputBindings"/> per local seat, each compiled from that
/// seat's composed document (engine default ⊕ world overlays ⊕ the seat's profile bindings ⊕ its live session rebinds
/// ⊕ its runtime MODE layer). Composition and compilation happen only on a CHANGE (a profile selection, a rebind, an
/// overlay mutation, a mode enter/exit) — never per frame; the per-signal resolve path stays the existing paged lookups.
/// </summary>
/// <remarks>Single-threaded, like every input-fold type here: recomposition runs on the launcher's window-pump thread
/// (a verb handler, a roster mutation, or the post-step overlay sync), and <see cref="Resolve(int, in InputSignal)"/>
/// runs on the same thread inside the router's snapshot fold. No lock guards this state. Constructed early in
/// composition (before the container is built) with just the engine default and boot overlays — both pure/available
/// there; the per-seat profile and session layers start null (every seat inherits the engine default at boot), and
/// the roster/verbs push them in as they change.</remarks>
internal sealed class WorldSeatBindings : IInputBindings {
    private readonly BindingProfileDocument m_engineDefault;
    private readonly PagedInputBindings[] m_seats;
    private readonly BindingProfileDocument?[] m_profileBindings;
    private readonly BindingProfileDocument?[] m_sessionRebinds;
    private readonly BindingProfileDocument?[] m_modeLayers;
    private IReadOnlyList<WorldBindingOverlay> m_overlays;

    /// <summary>The number of local seats this router resolves for.</summary>
    public const int SeatCount = WorldPopulation.LocalSeatCount;

    /// <summary>Initializes a new instance over the engine-default document and the world's boot binding overlays. Every
    /// seat starts compiled from the composed base (default ⊕ overlays); profile and session layers are null.</summary>
    /// <param name="engineDefault">The engine-default binding document (layer 0).</param>
    /// <param name="overlays">The world document's boot binding overlays (layer 1..).</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldSeatBindings(BindingProfileDocument engineDefault, IReadOnlyList<WorldBindingOverlay> overlays) {
        ArgumentNullException.ThrowIfNull(argument: engineDefault);
        ArgumentNullException.ThrowIfNull(argument: overlays);

        m_engineDefault = engineDefault;
        m_overlays = overlays;
        m_profileBindings = new BindingProfileDocument?[SeatCount];
        m_sessionRebinds = new BindingProfileDocument?[SeatCount];
        m_modeLayers = new BindingProfileDocument?[SeatCount];

        var seedBase = BindingProfile.Compile(document: ComposeSeat(slot: 0));

        m_seats = new PagedInputBindings[SeatCount];

        for (var slot = 0; (slot < SeatCount); slot++) {
            m_seats[slot] = new PagedInputBindings(profile: seedBase);
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

    /// <summary>The slot-blind console-dispatch table built from the composed BASE layers (engine default ⊕ world
    /// overlays) — the same composed documents the seats resolve through, flattened for the dormant
    /// <see cref="BindingCommandSource"/> (no second authoring grammar). Recomputed to reflect the live overlays.</summary>
    /// <returns>The base-page <c>source → commands</c> table.</returns>
    public IReadOnlyDictionary<string, IReadOnlyList<CommandBinding>> ConsoleBaseTable() {
        return WorldBindingComposer.BasePageTable(document: ComposeBase());
    }

    /// <summary>Sets a seat's selected-profile binding layer (null = the engine default) and recomposes that seat. Called
    /// by the roster on a profile selection / join / live identity switch.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="bindings">The profile's binding section, or <see langword="null"/>.</param>
    public void SetProfileBindings(int slot, BindingProfileDocument? bindings) {
        if ((uint)slot >= SeatCount) {
            return;
        }

        m_profileBindings[slot] = bindings;
        RecomposeSeat(slot: slot);
    }

    /// <summary>Sets a seat's live session-rebind layer and recomposes that seat — the <c>player.bind</c> path. The layer
    /// is unsaved until <c>profile.save</c> folds it into the seat's profile; passing null clears it.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="rebinds">The session rebind document, or <see langword="null"/> to clear it.</param>
    public void SetSessionRebind(int slot, BindingProfileDocument? rebinds) {
        if ((uint)slot >= SeatCount) {
            return;
        }

        m_sessionRebinds[slot] = rebinds;
        RecomposeSeat(slot: slot);
    }

    /// <summary>Sets a seat's runtime MODE layer (the FINAL compose layer — an editor page set entered/exited at
    /// runtime) and recomposes that seat; passing <see langword="null"/> clears it. Per-seat by design: a mode is one
    /// seat's state, never a world <c>bindingOverlays</c> mutation (those re-bind every seat).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="layer">The mode-layer document, or <see langword="null"/> to leave the mode.</param>
    public void SetModeLayer(int slot, BindingProfileDocument? layer) {
        if ((uint)slot >= SeatCount) {
            return;
        }

        m_modeLayers[slot] = layer;
        RecomposeSeat(slot: slot);
    }

    /// <summary>The immutable view of the page the seat's held chord currently selects — the binding bar's read
    /// seam (a single volatile reference read; see <see cref="PagedInputBindings.ViewFor"/>).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The active page's precomputed view (slot 0's for an out-of-range slot).</returns>
    public BindingPageView PageView(int slot) =>
        m_seats[(((uint)slot < SeatCount) ? slot : 0)].ViewFor(slot: slot);

    /// <summary>The seat's current live session-rebind layer, or <see langword="null"/> when it has none.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    public BindingProfileDocument? SessionRebind(int slot) => (((uint)slot < SeatCount) ? m_sessionRebinds[slot] : null);

    /// <summary>The document the seat currently resolves through — the full composed stack (engine default ⊕ overlays ⊕
    /// profile ⊕ session ⊕ mode). The <c>player.bindings</c> echo reads its active base page.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>The composed document, or the composed base for an out-of-range slot.</returns>
    public BindingProfileDocument ComposedDocument(int slot) => (((uint)slot < SeatCount) ? ComposeSeat(slot: slot) : ComposeBase());

    /// <summary>Reflects a changed world-overlay set (an applied <c>world.bindings.*</c> mutation) if it actually
    /// changed, recomposing every seat. A reference-equal set (the common per-step case) short-circuits, so the post-step
    /// call this feeds costs one comparison on an unchanged tick.</summary>
    /// <param name="overlays">The server's live binding overlays.</param>
    public void SyncOverlays(IReadOnlyList<WorldBindingOverlay> overlays) {
        if (ReferenceEquals(objA: overlays, objB: m_overlays)) {
            return;
        }

        m_overlays = (overlays ?? []);

        for (var slot = 0; (slot < SeatCount); slot++) {
            RecomposeSeat(slot: slot);
        }
    }

    private void RecomposeSeat(int slot) {
        // Compile the seat's composed document and hot-swap it. A composed document that fails to compile (a bad live
        // rebind slipping past the caller's checks) is dropped loudly, keeping the seat on its prior mapping rather than
        // taking the input path down.
        try {
            m_seats[slot].Reload(profile: BindingProfile.Compile(document: ComposeSeat(slot: slot)));
        } catch (ArgumentException exception) {
            Console.Error.WriteLine(value: $"[player.bindings] seat {slot + 1} recompose rejected ({exception.Message.ReplaceLineEndings(replacementText: " ")}); keeping the prior mapping.");
        }
    }

    private BindingProfileDocument ComposeSeat(int slot) {
        return WorldBindingComposer.Compose(BaseLayers(profile: m_profileBindings[slot], session: m_sessionRebinds[slot], mode: m_modeLayers[slot]));
    }

    private BindingProfileDocument ComposeBase() {
        return WorldBindingComposer.Compose(BaseLayers(profile: null, session: null, mode: null));
    }

    private BindingProfileDocument?[] BaseLayers(BindingProfileDocument? profile, BindingProfileDocument? session, BindingProfileDocument? mode) {
        var layers = new BindingProfileDocument?[m_overlays.Count + 4];
        var index = 0;

        layers[index++] = m_engineDefault;

        foreach (var overlay in m_overlays) {
            layers[index++] = overlay.Document;
        }

        layers[index++] = profile;
        layers[index++] = session;
        // The mode layer composes LAST: while a seat is in a mode, its pages outrank even live session rebinds.
        layers[index] = mode;

        return layers;
    }
}
