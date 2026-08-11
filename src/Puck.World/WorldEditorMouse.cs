using System.Buffers;
using System.Globalization;
using System.Numerics;
using Puck.Commands;
using Puck.World.Client;
using Puck.World.Protocol;

namespace Puck.World;

/// <summary>
/// The editor's mouse manipulation policy: with the editor active for the pointer's seat, a left-click selects the
/// row the drawn cursor is hovering (dispatching the existing <c>editor.select</c> verb through the console door, so
/// the act narrates and refuses exactly like a typed line), a click on empty space clears the selection the same
/// way, and a left-press on a draggable row (screens, placements, fixed/bed speakers) grabs it into the existing
/// <see cref="WorldEditorDrag"/> pending-row channel — while held, the pending pre-snap intent follows the cursor's
/// world point on the horizontal plane through the row's grabbed position, and a real in-viewport release commits
/// through the channel's release path: one whole-row mutation under the seat's own acting principal
/// (<see cref="PlayerRoster.PrincipalOf"/> — the same identity discipline <see cref="WorldEditorSession.Enter"/>
/// applies, never a laundered Console), coalesced exactly as a stick drag is. Everything here is
/// presentation/session policy: it composes the cursor feed's published decision, the pointer store's
/// non-destructive state, and the editor's existing acts — no new observer, consumer, verb, or mutation kind.
/// </summary>
/// <remarks>
/// <para>Ticked once per produced frame from the overlay's <c>FeedTick</c>, immediately after
/// <see cref="WorldCursorFeed.Tick"/>, so every decision reads the same frame's cursor verdict and hover target the
/// player saw — and runs on the launcher's window-pump thread with every other editor mutator (the
/// <see cref="WorldSeatViewports"/>/<see cref="WorldEditorSession"/> single-thread contract), so no lock guards any
/// of this state.</para>
/// <para>Button edges are derived here by comparing the store's held state across frames — per-slot memory, never a
/// drain and never a store mutation, so the seat-look arming read and every other button consumer stay whole. The
/// derivation is honest about its resolution: a press and release that both land between two produced frames
/// collapse in the store and are never observed — a sub-frame click does nothing.</para>
/// <para>A release commits only when it is real and the cursor still stands in the seat's viewport. A synthetic
/// release (the store's force-release on focus loss — observed as the seat's
/// <see cref="WorldPointer.SystemReleaseCount"/> advancing since the press) cancels: an alt-tab mid-drag must never
/// commit an edit. A release with the cursor outside the viewport (or otherwise hidden) cancels too: nothing valid
/// is under the cursor there, and clamping to the last valid point would edit the world at a spot the user did not
/// choose. A release that never moved the snapped position cancels silently — that is a click, already answered by
/// its selection. The channel's own cancel acts (<c>editor.cancel</c>, its chord, editor exit) retire the drag
/// externally; the next tick here simply observes the channel idle and stands down.</para>
/// <para>The follow is absolute, in position space: each frame re-maps the cursor through the live client→frame
/// mapping (<see cref="WorldCursorFeed"/>'s published <see cref="WorldCursorStatus.Local"/>) and re-casts
/// <see cref="WorldCursorFeed.RayDirection"/> against the grab plane, anchored only in the world space the channel
/// already holds (the grabbed row's position) — never in cached frame pixels, so a window resize mid-drag lands the
/// row where the cursor points.</para>
/// </remarks>
internal sealed class WorldEditorMouse {
    // The pointer store's left-button index (0=left, 1=right, 2=middle — WindowInputEvent.PointerButton's own
    // convention, mirrored by WorldSeatViewInput.ArmingButtonIndex).
    private const int LeftButton = 0;

    private readonly TextCommandSource m_console;
    private readonly WorldEditorDrag m_drag;
    private readonly WorldCursorFeed m_feed;
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;
    private readonly WorldEditorSession m_session;
    private readonly WorldSeatViewports m_viewports;
    // Per-slot policy memory (the slot the pointer rides can move with the keyboard): the previous frame's held
    // state (the edge derivation), whether the live drag in the channel is THIS policy's (a stick/verb drag must
    // never be committed by a mouse release), the system-release count sampled at press (the synthetic-release
    // discriminator), and the grab's world-space anchor — the row's grabbed position plus the press-time cursor
    // plane point, so the follow is `origin + (point - pressPoint)`: document/world space only (never frame
    // pixels), and EXACTLY the origin while the cursor has not moved, so a motionless click never manufactures a
    // one-ulp "move" to commit.
    private readonly bool[] m_wasDown = new bool[PlayerRoster.MaxSlots];
    private readonly bool[] m_dragging = new bool[PlayerRoster.MaxSlots];
    private readonly int[] m_pressReleases = new int[PlayerRoster.MaxSlots];
    private readonly Vector3[] m_grabOrigin = new Vector3[PlayerRoster.MaxSlots];
    private readonly Vector3[] m_pressPoint = new Vector3[PlayerRoster.MaxSlots];
    private readonly bool[] m_pressPointKnown = new bool[PlayerRoster.MaxSlots];

    /// <summary>Initializes a new instance of the <see cref="WorldEditorMouse"/> class.</summary>
    /// <param name="pointer">The live pointer store (read non-destructively: held buttons and the system-release
    /// count only).</param>
    /// <param name="roster">The roster the pointer's seat and the release's acting principal resolve against.</param>
    /// <param name="session">The editor session gating every act (all of this is inert for a non-editing seat).</param>
    /// <param name="drag">The pending-row preview channel every mouse drag rides.</param>
    /// <param name="feed">The cursor feed whose published per-frame decision (visibility, hover, local position)
    /// this policy composes.</param>
    /// <param name="viewports">The per-seat viewport + camera publication the cursor ray casts through.</param>
    /// <param name="console">The console door click-selection dispatches the existing <c>editor.select</c> verb
    /// through — the same tick-aligned path the on-screen panel and stdin use.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldEditorMouse(WorldPointer pointer, PlayerRoster roster, WorldEditorSession session, WorldEditorDrag drag, WorldCursorFeed feed, WorldSeatViewports viewports, TextCommandSource console) {
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: session);
        ArgumentNullException.ThrowIfNull(argument: drag);
        ArgumentNullException.ThrowIfNull(argument: feed);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: console);

        m_console = console;
        m_drag = drag;
        m_feed = feed;
        m_pointer = pointer;
        m_roster = roster;
        m_session = session;
        m_viewports = viewports;
    }

    /// <summary>Advances the policy one produced frame: derives this frame's left-button edge for the pointer's
    /// seat, acts on it, and follows a live mouse drag with the cursor.</summary>
    public void Tick() {
        var slot = WorldPointerSlot.Resolve(roster: m_roster);

        // A mouse drag whose seat the pointer no longer rides (the keyboard moved mid-hold) has lost its hand:
        // cancel it — the drag never existed — and forget that slot's edge memory so the new occupant starts clean.
        for (var other = 0; (other < PlayerRoster.MaxSlots); other++) {
            if ((other != slot) && m_dragging[other]) {
                m_dragging[other] = false;
                m_wasDown[other] = false;
                CancelNarrated(slot: other, reason: "the pointer left the seat mid-drag");
            }
        }

        var status = m_feed.Status;
        var down = m_pointer.IsButtonDown(slot: slot, button: LeftButton);
        var was = m_wasDown[slot];

        m_wasDown[slot] = down;

        if (down && !was) {
            OnPress(slot: slot, status: in status);
        }

        if (down && m_dragging[slot]) {
            Follow(slot: slot, status: in status);
        }

        if (!down && was) {
            OnRelease(slot: slot, status: in status);
        }
    }

    // The press act: select what the cursor hovers (through the existing verb), and grab a draggable row into the
    // pending channel. Inert outside editor mode, with the cursor hidden, and on HUD panels (UI, not world).
    private void OnPress(int slot, in WorldCursorStatus status) {
        if (!m_session.IsEditing(slot: slot) || !status.Visible || (m_feed.HoverPanelId is not null)) {
            return;
        }

        if (m_feed.HoverTarget is not { } target) {
            // A click on nothing clears the selection — the editor's existing clear act, through the same door.
            m_console.Enqueue(line: $"editor.select none {PlayerRoster.DisplayNumber(slot: slot)}");

            return;
        }

        var selection = new EditorSelection(Section: target.Section, Id: target.Id, Index: target.Index);

        if (SelectLine(slot: slot, section: target.Section, key: ((target.Section == WorldSection.Screens) ? target.Index.ToString(provider: CultureInfo.InvariantCulture) : target.Id)) is not { } line) {
            // A key no console line can carry verbatim (an embedded quote — the document boundary admits any
            // non-blank id, so such rows are REACHABLE) refuses the WHOLE press: selection could not dispatch, and
            // grabbing a row the editor never considered selected would split the gesture's two halves.
            Console.Error.WriteLine(value: $"[editor.mouse] seat {PlayerRoster.DisplayNumber(slot: slot)} press on {selection.Describe()} refused: its key cannot ride a console line (embedded quote) — editor.select/world.row.set address it directly");

            return;
        }

        m_console.Enqueue(line: line);

        // Only the channel's draggable kinds grab on press; everything else (spawns, cameras) is click-select only,
        // as is a press while another drag already holds the channel (the click was still a select).
        if ((target.Section is not (WorldSection.Screens or WorldSection.Placements or WorldSection.Speakers)) || m_drag.IsDragging(slot: slot)) {
            return;
        }

        if (!m_drag.TryGrab(slot: slot, selection: in selection, error: out var reason)) {
            // An undraggable row of a draggable section (an anchored speaker): the click selected it; say why it
            // did not grab, act-scale, on the refusal stream.
            Console.Error.WriteLine(value: $"[editor.mouse] seat {PlayerRoster.DisplayNumber(slot: slot)} press on {selection.Describe()} did not grab: {reason}");

            return;
        }

        m_dragging[slot] = true;
        m_pressReleases[slot] = m_pointer.SystemReleaseCount(slot: slot);
        m_grabOrigin[slot] = target.Focus;
        // The cursor grabbed the row somewhere on its proxy, not at its exact origin: the press-time plane point is
        // the follow's baseline, so the row moves by the cursor's own plane travel instead of jumping under it.
        // World space only — the frame-space mapping is re-derived every frame.
        m_pressPointKnown[slot] = TryPlanePoint(slot: slot, status: in status, planeY: target.Focus.Y, point: out var point);
        m_pressPoint[slot] = point;
        Console.Error.WriteLine(value: $"[editor.mouse] seat {PlayerRoster.DisplayNumber(slot: slot)} dragging {selection.Describe()} — release drops it, editor.cancel aborts");
    }

    // The per-frame follow: re-map the cursor through the LIVE client→frame mapping and re-cast the ray against the
    // grab plane. With the cursor hidden or outside the viewport the pending row holds its last pose (a release out
    // there cancels rather than commits).
    private void Follow(int slot, in WorldCursorStatus status) {
        if (!m_drag.IsDragging(slot: slot)) {
            // The channel retired the drag externally (editor.cancel, its chord, a grab-toggle commit, editor
            // exit): this policy's hand is empty now, and the coming release edge must not act on it.
            m_dragging[slot] = false;

            return;
        }

        if (!status.Visible) {
            return;
        }

        if (!TryPlanePoint(slot: slot, status: in status, planeY: m_grabOrigin[slot].Y, point: out var point)) {
            return;
        }

        if (!m_pressPointKnown[slot]) {
            // The press itself resolved no plane point (a degenerate ray at grab time): the first frame that does
            // resolve one becomes the baseline, so the follow still measures cursor travel, never a jump.
            m_pressPoint[slot] = point;
            m_pressPointKnown[slot] = true;
        }

        m_drag.MoveTo(slot: slot, intent: (m_grabOrigin[slot] + (point - m_pressPoint[slot])));
    }

    // The release act: cancel a synthetic or out-of-viewport release, drop an unmoved click silently, and commit
    // everything else as the channel's one whole-row mutation under the seat's own principal.
    private void OnRelease(int slot, in WorldCursorStatus status) {
        if (!m_dragging[slot]) {
            return;
        }

        m_dragging[slot] = false;

        if (!m_drag.IsDragging(slot: slot)) {
            return;
        }

        if (m_pointer.SystemReleaseCount(slot: slot) != m_pressReleases[slot]) {
            CancelNarrated(slot: slot, reason: "focus was lost mid-drag (synthetic release)");

            return;
        }

        if (!status.Visible) {
            CancelNarrated(slot: slot, reason: $"released outside the seat viewport ({status.Reason})");

            return;
        }

        if ((m_drag.PendingPosition(slot: slot) is { } pending) && (pending == m_grabOrigin[slot])) {
            // An unmoved release is a click, and its selection already landed: nothing to mutate, nothing to say.
            _ = m_drag.Cancel(slot: slot);

            return;
        }

        if (m_drag.Release(slot: slot, principal: m_roster.PrincipalOf(slot: slot)) is { } echo) {
            Console.Error.WriteLine(value: $"[editor.mouse] seat {PlayerRoster.DisplayNumber(slot: slot)} {echo}");
        }
    }

    // Cancel the channel's drag with the honest reason narrated once, act-scale, on the refusal stream.
    private void CancelNarrated(int slot, string reason) {
        if (m_drag.Cancel(slot: slot) is { } echo) {
            Console.Error.WriteLine(value: $"[editor.mouse] seat {PlayerRoster.DisplayNumber(slot: slot)} drag cancelled — {reason}; {echo}");
        }
    }

    // The cursor's world point on the horizontal plane at planeY: the shared ray derivation cast from this frame's
    // camera, accepted only forward of the eye and within the editor's pick reach (a near-horizon ray otherwise
    // slings the row toward infinity).
    private bool TryPlanePoint(int slot, in WorldCursorStatus status, float planeY, out Vector3 point) {
        point = default;

        var view = m_viewports.Seat(slot: slot);

        if (!view.Present) {
            return false;
        }

        var camera = view.Camera;
        var direction = WorldCursorFeed.RayDirection(camera: in camera, localX: status.Local.X, localY: status.Local.Y);

        if (MathF.Abs(x: direction.Y) < 1e-6f) {
            return false;
        }

        var t = ((planeY - camera.Position.Y) / direction.Y);

        if ((t <= 0f) || ((t * direction.Length()) > WorldEditorPicker.MaxPickReach)) {
            return false;
        }

        point = (camera.Position + (direction * t));

        return true;
    }

    // The row-select line a press dispatches: `editor.select <section> <key> <seat>`. A key the console line cannot
    // carry verbatim is quoted (whitespace) or refused here (an embedded quote — null) rather than dispatched
    // mangled.
    private static string? SelectLine(int slot, WorldSection section, string key) {
        if (key.Contains(value: '"')) {
            return null;
        }

        var token = (key.AsSpan().ContainsAny(values: s_whitespace) ? $"\"{key}\"" : key);

        return $"editor.select {section.ToString().ToLowerInvariant()} {token} {PlayerRoster.DisplayNumber(slot: slot)}";
    }
    private static readonly SearchValues<char> s_whitespace = SearchValues.Create(values: " \t");
}
