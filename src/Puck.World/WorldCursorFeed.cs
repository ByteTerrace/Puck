using System.Numerics;
using Puck.Abstractions.Cameras;
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
/// <see cref="WorldCameraOrbitDrag"/>), applies THE visibility rule (one place, below), hover-tests what the cursor
/// rests on, and publishes one <see cref="OverlayCursorFrame"/>. Hover is two tests in draw order: the authored HUD
/// panels' published rects first (they draw over the world), then the EXISTING editor pick program aimed down the
/// cursor ray instead of the screen-center look ray — reused, never forked. Everything here is presentation/session
/// state: nothing rides a <see cref="Puck.Commands.CommandSnapshot"/>, touches the binding vocabulary, or reaches
/// the simulation.
/// </summary>
internal sealed class WorldCursorFeed {
    private readonly WorldClient m_client;
    private readonly IHudSource m_hud;
    private readonly WorldEditorPicker m_picker;
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;
    // Per-seat control feel: the cursor's steering readout must agree with what THAT seat's drag actually does.
    private readonly WorldSeatFeel m_seatFeel;
    private readonly OverlayCursorSeat[] m_seats = new OverlayCursorSeat[PlayerRoster.MaxSlots];
    private readonly CursorStore m_store;
    private readonly WorldSeatViewports m_viewports;
    // The hover-label cache: labels re-format only when the hovered thing changes (a per-frame string would ride
    // the frame path; the same human-cadence discipline every overlay feed here follows).
    private EditorPickTarget? m_hoverTarget;
    private string? m_hoverPanelId;
    private string m_hoverLabel = string.Empty;
    private WorldCursorStatus m_status = new(Slot: 0, Position: Vector2.Zero, Frame: Vector2.Zero, Viewport: default, Visible: false, Reason: "no-position", Hover: string.Empty, Local: Vector2.Zero, SystemReleaseCount: 0, Buttons: s_buttonWords[0]);
    // The eight held-button words, indexed by the L|R|M bit mask — interned so the per-frame status never
    // allocates a string for a state with only eight values.
    private static readonly string[] s_buttonWords = ["-", "L", "R", "LR", "M", "LM", "RM", "LRM"];

    /// <summary>Initializes a new instance of the <see cref="WorldCursorFeed"/> class.</summary>
    /// <param name="pointer">The live pointer store (read non-destructively: position and held buttons only).</param>
    /// <param name="roster">The roster the pointer's seat resolves against (the keyboard's seat).</param>
    /// <param name="client">The client view.</param>
    /// <param name="seatFeel">Every local seat's live control feel — the per-seat arming policy this feed's steering
    /// readout must agree with.</param>
    /// <param name="viewports">The per-seat viewport + camera publication the frame source fills each dress.</param>
    /// <param name="picker">The editor's look-ray picking program, aimed down the cursor ray here.</param>
    /// <param name="hud">The authored HUD structure source (panel rects, the overlay-side hover targets).</param>
    /// <param name="store">The cursor store the overlay reads.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldCursorFeed(WorldPointer pointer, PlayerRoster roster, WorldClient client, WorldSeatFeel seatFeel, WorldSeatViewports viewports, WorldEditorPicker picker, IHudSource hud, CursorStore store) {
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: seatFeel);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: picker);
        ArgumentNullException.ThrowIfNull(argument: hud);
        ArgumentNullException.ThrowIfNull(argument: store);

        m_client = client;
        m_hud = hud;
        m_picker = picker;
        m_pointer = pointer;
        m_roster = roster;
        m_seatFeel = seatFeel;
        m_store = store;
        m_viewports = viewports;
    }

    /// <summary>The last composed frame's cursor facts (the <c>world.view.pointer</c> read-back).</summary>
    public WorldCursorStatus Status => m_status;

    /// <summary>The world row under the cursor as of the last composed frame, or <see langword="null"/> (a hidden
    /// cursor, a hovered panel, or empty space) — the typed twin of <see cref="WorldCursorStatus.Hover"/>'s label,
    /// so the mouse policy's press acts on exactly the row the hover echo named.</summary>
    public EditorPickTarget? HoverTarget => m_hoverTarget;

    /// <summary>The HUD panel id under the cursor as of the last composed frame, or <see langword="null"/> — panels
    /// draw over the world, so a press on one is UI, never a world act.</summary>
    public string? HoverPanelId => m_hoverPanelId;

    /// <summary>Recomposes and publishes this frame's cursor frame (the overlay's <c>FeedTick</c>).</summary>
    public void Tick() {
        // The process has one pointer, so at most one cursor entry publishes per frame.
        var slot = WorldPointerSlot.Resolve(roster: m_roster);
        var count = 0;
        var position = m_pointer.Position(slot: slot);
        var view = m_viewports.Seat(slot: slot);
        var reason = Decide(slot: slot, position: position, view: in view, framePosition: out var framePosition, localX: out var localX, localY: out var localY);
        var hover = string.Empty;

        if (reason is null) {
            // LIVE-CONSUMED: the world-authored cursor policy (hud.defaults.cursor), read fresh each frame so a
            // world.row.set hud.defaults edit applies on the very next composed frame — the same live-read
            // null-coalesce convention.
            var policy = (m_client.Definition.Hud.Defaults?.Cursor ?? WorldHudCursor.Default);

            hover = ResolveHover(framePosition: framePosition, view: in view, localX: localX, localY: localY, hoverRadius: policy.HoverRadius);
            m_seats[count++] = new OverlayCursorSeat(
                Viewport: view.Region,
                X: framePosition.X,
                Y: framePosition.Y,
                Hover: (hover.Length > 0),
                HoverLabel: hover,
                SizePx: Math.Clamp(value: policy.SizePx, min: 1f, max: CursorWriter.MaxSizePx),
                Role: RoleOf(role: policy.Role)
            );
        } else if ((m_hoverTarget is not null) || (m_hoverPanelId is not null)) {
            // A hidden cursor hovers nothing: clear the cache so re-showing over the same spot re-resolves fresh.
            m_hoverTarget = null;
            m_hoverPanelId = null;
            m_hoverLabel = string.Empty;
        }

        var buttonMask = ((m_pointer.IsButtonDown(slot: slot, button: 0) ? 1 : 0)
            | (m_pointer.IsButtonDown(slot: slot, button: 1) ? 2 : 0)
            | (m_pointer.IsButtonDown(slot: slot, button: 2) ? 4 : 0));

        m_status = new WorldCursorStatus(
            Slot: slot,
            Position: position,
            Frame: framePosition,
            Viewport: view.Region,
            Visible: (reason is null),
            Reason: (reason ?? "visible"),
            Hover: hover,
            Local: new Vector2(x: localX, y: localY),
            SystemReleaseCount: m_pointer.SystemReleaseCount(slot: slot),
            Buttons: s_buttonWords[buttonMask]
        );
        m_store.Publish(frame: new OverlayCursorFrame(Seats: m_seats.AsMemory(start: 0, length: count)));
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

        if ((clientWidth > 0) && (clientHeight > 0)) {
            framePosition = new Vector2(
                x: (position.X * (view.Width / (float)clientWidth)),
                y: (position.Y * (view.Height / (float)clientHeight))
            );
        }

        var regionWidthPx = (view.Region.Width * view.Width);
        var regionHeightPx = (view.Region.Height * view.Height);

        if ((regionWidthPx < 1f) || (regionHeightPx < 1f)) {
            return "no-view";
        }

        localX = ((framePosition.X - (view.Region.X * view.Width)) / regionWidthPx);
        localY = ((framePosition.Y - (view.Region.Y * view.Height)) / regionHeightPx);

        if ((localX < 0f) || (localX > 1f) || (localY < 0f) || (localY > 1f)) {
            return "outside-viewport";
        }

        var seatLook = m_seatFeel.Look(slot: slot);
        var steering = (seatLook.Arming switch {
            WorldSeatLookArming.None => false,
            WorldSeatLookArming.Always => true,
            _ => ((WorldCameraOrbitDrag.ArmingButtonIndex(arming: seatLook.Arming) is { } button) && m_pointer.IsButtonDown(slot: slot, button: button)),
        });

        return (steering ? "orbit-drag" : null);
    }

    // The world-authored role token mapped onto the overlay's concrete color role (Puck.World.Data cannot reference
    // Puck.Overlays, so the document speaks its own closed token set and this is the one mapping).
    private static OverlayColorRole RoleOf(WorldHudCursorRole role) => role switch {
        WorldHudCursorRole.TextDim => OverlayColorRole.TextDim,
        WorldHudCursorRole.Accent => OverlayColorRole.Accent,
        WorldHudCursorRole.Phosphor => OverlayColorRole.Phosphor,
        _ => OverlayColorRole.TextPrimary,
    };

    // The hover resolution, in draw order: the authored HUD panels' published rects first (they draw over the
    // world), then the editor pick program down the cursor ray at the world-authored hover reach. Returns the
    // hovered thing's label, or empty.
    private string ResolveHover(Vector2 framePosition, in WorldSeatView view, float localX, float localY, float hoverRadius) {
        if (HoveredPanelId(framePosition: framePosition, view: in view) is { } panelId) {
            if (!string.Equals(a: panelId, b: m_hoverPanelId, comparisonType: StringComparison.Ordinal)) {
                m_hoverPanelId = panelId;
                m_hoverTarget = null;
                m_hoverLabel = $"panel '{panelId}'";
            }

            return m_hoverLabel;
        }

        // The cursor ray through the SAME camera snapshot this seat's view rendered with — then the EXISTING pick
        // program, unchanged.
        var camera = view.Camera;
        var direction = RayDirection(camera: in camera, localX: localX, localY: localY);

        if (!m_picker.TryPick(eye: camera.Position, direction: direction, target: out var target, maxDistance: hoverRadius)) {
            if ((m_hoverTarget is not null) || (m_hoverPanelId is not null)) {
                m_hoverTarget = null;
                m_hoverPanelId = null;
                m_hoverLabel = string.Empty;
            }

            return string.Empty;
        }

        if (m_hoverTarget != target) {
            m_hoverTarget = target;
            m_hoverPanelId = null;
            m_hoverLabel = new EditorSelection(Section: target.Section, Id: target.Id, Index: target.Index).Describe();
        }

        return m_hoverLabel;
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

            if ((viewport.Width <= 0f) || (viewport.Height <= 0f)) {
                continue;
            }

            var panelLocalX = ((normalizedX - viewport.X) / viewport.Width);
            var panelLocalY = ((normalizedY - viewport.Y) / viewport.Height);

            if (Contains(rect: seatPanel.Panel.Rect, x: panelLocalX, y: panelLocalY)) {
                return seatPanel.Panel.Id;
            }
        }

        var panels = frame.Panels.Span;

        for (var index = (panels.Length - 1); (index >= 0); index--) {
            ref readonly var panel = ref panels[index];

            if (Contains(rect: panel.Rect, x: normalizedX, y: normalizedY)) {
                return panel.Id;
            }
        }

        return null;
    }
    private static bool Contains(in OverlayHudRect rect, float x, float y) {
        return ((x >= rect.X) && (x <= (rect.X + rect.Width)) && (y >= rect.Y) && (y <= (rect.Y + rect.Height)));
    }

    /// <summary>The world-space ray direction through a viewport-local cursor point — the exact inverse of the frame
    /// source's viewport projection (not normalized). The ONE derivation both the hover pass and the editor's
    /// mouse-drag follow cast through, so the two can never disagree on where the cursor points.</summary>
    /// <param name="camera">The seat view's render camera snapshot.</param>
    /// <param name="localX">The cursor's normalized X within the seat viewport (0..1).</param>
    /// <param name="localY">The cursor's normalized Y within the seat viewport (0..1).</param>
    internal static Vector3 RayDirection(in CameraSnapshot camera, float localX, float localY) {
        var ndcX = ((localX * 2f) - 1f);
        var ndcY = (1f - (localY * 2f));

        return ((camera.Forward
            + (camera.Right * ((ndcX * camera.TanHalfFieldOfView) * camera.AspectRatio)))
            + (camera.Up * (ndcY * camera.TanHalfFieldOfView)));
    }
}
