using System.Numerics;
using Puck.Abstractions.Presentation;
using Puck.Overlays;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The cursor facts the <c>world.view.pointer</c> read-back echoes — the last composed frame's decision,
/// stashed beside the publish so the verb answers exactly what the overlay drew.</summary>
/// <param name="Slot">The 0-based seat slot the pointer rides (the keyboard's seat).</param>
/// <param name="Position">The cursor position in CLIENT pixels, as the platform reported it (meaningful once a
/// position has been reported).</param>
/// <param name="Frame">The same position mapped into FRAME pixels — the space the overlay draws and the viewports
/// live in; see <c>Decide</c>'s client→frame mapping (meaningful when a view resolved).</param>
/// <param name="Viewport">The seat's viewport rect, normalized (zero until the seat resolves a view).</param>
/// <param name="Visible">Whether the cursor drew this frame.</param>
/// <param name="Reason">The visibility rule's verdict token: <c>visible</c>, <c>no-position</c>, <c>no-view</c>,
/// <c>outside-viewport</c>, or <c>orbit-drag</c>.</param>
/// <param name="Hover">The hovered thing's label, or empty with nothing under the cursor.</param>
/// <param name="Local">The cursor position in viewport terms — normalized 0..1 within the seat's viewport rect
/// (meaningful when a view resolved).</param>
/// <param name="SystemReleaseCount">The seat's <see cref="WorldPointer.SystemReleaseCount"/> as of this composed
/// frame — non-destructive, so stashing it costs nothing extra and keeps every field on <c>world.view.pointer</c>'s
/// echo answering "as of the same frame" rather than mixing a stashed read with a live one.</param>
/// <param name="Buttons">The seat's held pointer buttons as a fixed word (<c>-</c> for none, else up to
/// <c>LRM</c> in that order) — the store's live non-destructive button state, echoed so an injected press is
/// pipe-assertable before anything acts on it.</param>
internal readonly record struct WorldCursorStatus(
    int Slot,
    Vector2 Position,
    Vector2 Frame,
    NormalizedRect Viewport,
    bool Visible,
    string Reason,
    string Hover,
    Vector2 Local,
    int SystemReleaseCount,
    string Buttons
);
/// <summary>
/// The World-side feed behind the unified overlay's drawn-cursor source: once per produced frame (the node's
/// <c>FeedTick</c>, after the frame's dress resolved each seat's viewport + camera) it reads the pointer store's
/// NON-DESTRUCTIVE state (position, held buttons — never the drained motion/wheel accumulators, which belong to
/// <see cref="WorldSeatViewInput"/>), applies THE visibility rule (one place, below), hover-tests the authored HUD
/// panels' published rects, and publishes one <see cref="OverlayCursorFrame"/>. Everything here is presentation/
/// session state: nothing rides a <see cref="Puck.Commands.CommandSnapshot"/>, touches the binding vocabulary, or
/// reaches the simulation.
/// </summary>
internal sealed class WorldCursorFeed {
    // The eight held-button words, indexed by the L|R|M bit mask — interned so the per-frame status never
    // allocates a string for a state with only eight values.
    private static readonly string[] ButtonWords = ["-", "L", "R", "LR", "M", "LM", "RM", "LRM"];

    private readonly WorldClient m_client;
    private readonly WorldOverlayFacts m_facts;
    private readonly IHudSource m_hud;
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;
    private readonly CursorStore m_store;
    // Per-seat control feel: the cursor's steering readout must agree with what THAT seat's drag actually does.
    private readonly WorldSeatViewInput m_viewInput;
    private readonly WorldSeatViewports m_viewports;

    // The hover-label cache: labels re-format only when the hovered panel changes (a per-frame string would ride
    // the frame path; the same human-cadence discipline every overlay feed here follows).
    private string? m_hoverPanelId;

    private readonly OverlayCursorSeat[] m_seats = new OverlayCursorSeat[PlayerRoster.MaxSlots];
    private string m_hoverLabel = string.Empty;
    private WorldCursorStatus m_status = new(
        Slot: 0,
        Position: Vector2.Zero,
        Frame: Vector2.Zero,
        Viewport: default,
        Visible: false,
        Reason: "no-position",
        Hover: string.Empty,
        Local: Vector2.Zero,
        SystemReleaseCount: 0,
        Buttons: ButtonWords[0]
    );

    /// <summary>Initializes a new instance of the <see cref="WorldCursorFeed"/> class.</summary>
    /// <param name="pointer">The live pointer store (read non-destructively: position and held buttons only).</param>
    /// <param name="roster">The roster the pointer's seat resolves against (the keyboard's seat).</param>
    /// <param name="client">The client view.</param>
    /// <param name="viewInput">The shared pointer-to-view adapter and steering resolver.</param>
    /// <param name="viewports">The per-seat viewport + camera publication the frame source fills each dress.</param>
    /// <param name="hud">The authored HUD structure source (panel rects, the overlay-side hover targets).</param>
    /// <param name="store">The cursor store the overlay reads.</param>
    /// <param name="facts">The overlay-visibility fact evaluator the cursor policy's <c>visible</c> reads.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldCursorFeed(WorldPointer pointer, PlayerRoster roster, WorldClient client, WorldSeatViewInput viewInput, WorldSeatViewports viewports, IHudSource hud, CursorStore store, WorldOverlayFacts facts) {
        ArgumentNullException.ThrowIfNull(facts);
        m_facts = facts;
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: viewInput);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: hud);
        ArgumentNullException.ThrowIfNull(argument: store);

        m_client = client;
        m_hud = hud;
        m_pointer = pointer;
        m_roster = roster;
        m_viewInput = viewInput;
        m_store = store;
        m_viewports = viewports;
    }

    /// <summary>The last composed frame's cursor facts (the <c>world.view.pointer</c> read-back).</summary>
    public WorldCursorStatus Status => m_status;

    private static bool Contains(in OverlayHudRect rect, float x, float y) {
        return (
            (x >= rect.X) &&
            (x <= (rect.X + rect.Width)) &&
            (y >= rect.Y) &&
            (y <= (rect.Y + rect.Height))
        );
    }
    // THE VISIBILITY RULE — the one place it lives. Returns null for a visible cursor, or the hidden reason token.
    // The cursor draws whenever:
    //   (1) the platform has reported a position for the seat (there is nothing honest to draw before that);
    //   (2) the seat resolved a view this frame and the cursor sits inside its viewport rect (the drawn cursor is
    //       confined to the seat's own split — a pointer parked over another seat's view points at nothing); and
    //   (3) the seat-look drag is not currently steering the camera: while the authored arming condition holds
    //       (Arming.Always, or the armed button held), pointer motion IS the camera and a drawn cursor would sit
    //       frozen mid-screen pretending to point at things — the moment the button lifts, the cursor returns.
    // Console and editor activity need no special case: neither holds the arming button, so the cursor is already
    // visible there under (3). Arming.None can never steer, so the cursor is simply always free.
    private string? Decide(int slot, Vector2 position, in WorldSeatView view, out Vector2 framePosition, out float localX, out float localY) {
        framePosition = position;
        localX = 0f;
        localY = 0f;

        if (!m_pointer.HasPosition(slot: slot)) {
            return "no-position";
        }

        if (!view.Present) {
            return "no-view";
        }

        // CLIENT→FRAME — the one place the two pixel spaces meet. Pointer positions arrive in CLIENT pixels
        // (WM_MOUSEMOVE's lParam), but everything downstream — the seat viewport rects, the drawn overlay, the
        // pick-ray unproject — lives in the FIXED frame extent the engine was constructed with (view.Width/Height).
        // The two diverge the moment the OS window is resized: the launcher passes the live client area to
        // presenter.BeginFrame every frame (the swapchain chases it) and both presenters STRETCH the produced frame
        // over the whole back buffer (the fullscreen-triangle blit in DirectXSurfaceCompositor / the Vulkan
        // SurfaceCompositor twin), so the inverse of that presentation scale — per-axis frame/client — is the one
        // honest mapping, and it puts the drawn cursor under the physical pointer at any window size. Before the
        // first RenderViews publishes a client extent (or if it ever publishes zero), the spaces are taken as
        // coincident — the boot configuration, where the swapchain extent equals the constructed frame extent.
        var clientWidth = m_viewports.ClientWidth;
        var clientHeight = m_viewports.ClientHeight;

        if (
            (clientWidth > 0) &&
            (clientHeight > 0)
        ) {
            framePosition = new Vector2(
                x: (position.X * (view.Width / ((float)clientWidth))),
                y: (position.Y * (view.Height / ((float)clientHeight)))
            );
        }

        var regionWidthPx = (view.Region.Width * view.Width);
        var regionHeightPx = (view.Region.Height * view.Height);

        if (
            (regionWidthPx < 1f) ||
            (regionHeightPx < 1f)
        ) {
            return "no-view";
        }

        localX = ((framePosition.X - (view.Region.X * view.Width)) / regionWidthPx);
        localY = ((framePosition.Y - (view.Region.Y * view.Height)) / regionHeightPx);

        if (
            (localX < 0f) ||
            (localX > 1f) ||
            (localY < 0f) ||
            (localY > 1f)
        ) {
            return "outside-viewport";
        }

        var steering = m_viewInput.IsSteering(slot: slot);

        return (steering
            ? "orbit-drag"
            : null
        );
    }
    // The overlay-side hover test: the world-scope HUD panels' screen-space rects, then the seat's OWN player-scope
    // panel (its rect is local to the seat viewport). Panels are tested in reverse document order so the topmost-drawn
    // of two overlapping panels wins, matching what the eye sees.
    private string? HoveredPanelId(Vector2 framePosition, in WorldSeatView view) {
        if (!m_hud.TrySnapshot(frame: out var frame)) {
            return null;
        }

        var normalizedX = (framePosition.X / view.Width);
        var normalizedY = (framePosition.Y / view.Height);
        var seatPanels = frame.SeatPanels.Span;

        // Seat panels draw last (topmost), so they test first.
        for (var index = (seatPanels.Length - 1); (index >= 0); index--) {
            ref readonly var seatPanel = ref seatPanels[index];
            var viewport = seatPanel.Viewport;

            if (
                (viewport.Width <= 0f) ||
                (viewport.Height <= 0f)
            ) {
                continue;
            }

            var panelLocalX = ((normalizedX - viewport.X) / viewport.Width);
            var panelLocalY = ((normalizedY - viewport.Y) / viewport.Height);

            if (Contains(
                rect: seatPanel.Panel.Rect,
                x: panelLocalX,
                y: panelLocalY
            )) {
                return seatPanel.Panel.Id;
            }
        }

        var panels = frame.Panels.Span;

        for (var index = (panels.Length - 1); (index >= 0); index--) {
            ref readonly var panel = ref panels[index];

            if (Contains(
                rect: panel.Rect,
                x: normalizedX,
                y: normalizedY
            )) {
                return panel.Id;
            }
        }

        return null;
    }
    // The hover resolution: the authored HUD panels' published rects (they draw over the world). Returns the
    // hovered panel's label, or empty — there is no world-row pick program any more (the editor tool that consumed
    // it was deleted with the editor.* verb surface).
    private string ResolveHover(Vector2 framePosition, in WorldSeatView view) {
        if (HoveredPanelId(
            framePosition: framePosition,
            view: in view
        ) is { } panelId) {
            if (!string.Equals(
                a: panelId,
                b: m_hoverPanelId,
                comparisonType: StringComparison.Ordinal
            )) {
                m_hoverPanelId = panelId;
                m_hoverLabel = $"panel '{panelId}'";
            }

            return m_hoverLabel;
        }

        if (m_hoverPanelId is not null) {
            m_hoverPanelId = null;
            m_hoverLabel = string.Empty;
        }

        return m_hoverLabel;
    }
    // The world-authored role token mapped onto the overlay's concrete color role (Puck.World.Schema cannot reference
    // Puck.Overlays, so the document speaks its own closed token set and this is the one mapping).
    private static OverlayColorRole RoleOf(WorldHudCursorRole role) => role switch {
        WorldHudCursorRole.TextDim => OverlayColorRole.TextDim,
        WorldHudCursorRole.Accent => OverlayColorRole.Accent,
        WorldHudCursorRole.Phosphor => OverlayColorRole.Phosphor,
        _ => OverlayColorRole.TextPrimary,
    };

    /// <summary>Recomposes and publishes this frame's cursor frame (the overlay's <c>FeedTick</c>).</summary>
    public void Tick() {
        // The process has one pointer, so at most one cursor entry publishes per frame.
        var slot = WorldPointerSlot.Resolve(roster: m_roster);
        var count = 0;
        var position = m_pointer.Position(slot: slot);
        var view = m_viewports.Seat(slot: slot);
        var reason = Decide(
            framePosition: out var framePosition,
            localX: out var localX,
            localY: out var localY,
            position: position,
            slot: slot,
            view: in view
        );
        var hover = string.Empty;

        // LIVE-CONSUMED: the world-authored cursor policy (hud.defaults.cursor), read fresh each frame so a
        // world.row.set hud.defaults edit applies on the very next composed frame. The engine draws no cursor of
        // its own: an unauthored policy is a hidden cursor, decided like any other reason.
        var cursorPolicy = m_client.Definition.Hud.Defaults?.Cursor;

        if (
            (reason is null) &&
            (cursorPolicy is null)
        ) {
            reason = "no-policy";
        }

        // The authored cursor policy's own visibility condition hides the cursor like any other decided reason.
        if (
            (reason is null) &&
            !m_facts.Evaluate(
            predicate: cursorPolicy!.Visible,
            slot: slot
        )
        ) {
            reason = "visible-false";
        }

        if (reason is null) {
            var policy = cursorPolicy!;

            hover = ResolveHover(
                framePosition: framePosition,
                view: in view
            );
            m_seats[count++] = new OverlayCursorSeat(
                Viewport: view.Region,
                X: framePosition.X,
                Y: framePosition.Y,
                Hover: (hover.Length > 0),
                HoverLabel: hover,
                SizePx: Math.Clamp(
                    value: policy.SizePx,
                    min: 1f,
                    max: CursorWriter.MaxSizePx
                ),
                Role: RoleOf(role: policy.Role)
            );
        } else if (m_hoverPanelId is not null) {
            // A hidden cursor hovers nothing: clear the cache so re-showing over the same spot re-resolves fresh.
            m_hoverPanelId = null;
            m_hoverLabel = string.Empty;
        }

        var buttonMask = (m_pointer.IsButtonDown(
            button: 0,
            slot: slot
        )
            ? 1
            : 0)
            | (m_pointer.IsButtonDown(
            button: 1,
            slot: slot
        )
            ? 2
            : 0)
            | (m_pointer.IsButtonDown(
            button: 2,
            slot: slot
        )
            ? 4
            : 0
        );

        m_status = new WorldCursorStatus(
            Slot: slot,
            Position: position,
            Frame: framePosition,
            Viewport: view.Region,
            Visible: (reason is null),
            Reason: (reason ?? "visible"),
            Hover: hover,
            Local: new Vector2(
                x: localX,
                y: localY
            ),
            SystemReleaseCount: m_pointer.SystemReleaseCount(slot: slot),
            Buttons: ButtonWords[buttonMask]
        );
        m_store.Publish(frame: new OverlayCursorFrame(Seats: m_seats.AsMemory(
            length: count,
            start: 0
        )));
    }
}
