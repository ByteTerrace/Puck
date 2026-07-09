using System.Numerics;
using Puck.Cameras;
using Puck.Compositing;
using Puck.SdfVm;

namespace Puck.Demo.Overworld;

/// <summary>Which family of layout keyframes the <see cref="ScreenLayoutDirector"/>'s staged transitions walk. PURE
/// PRESENTATION — a mode switch retargets the eased rects, never the simulation.</summary>
public enum ScreenLayoutDirectorMode {
    /// <summary>The classic overworld: the room shrinks through the staged layouts as consoles boot (fullscreen →
    /// side-by-side → big-top/two-bottom → quad quarter → top half over four quarter panes). Effectively LEGACY: the
    /// demo's default and <c>--rom</c> path both open in <see cref="Immersed"/>, so this mode has no current
    /// in-session entry point.</summary>
    Standard,
    /// <summary>The fourth-wall boot: the room is ZERO-AREA and the visible panes alone tile the whole screen
    /// (fullscreen → side-by-side halves → big-top + two-bottom → 2×2 quad) — the player is INSIDE the machines.</summary>
    Immersed,
    /// <summary>The reveal: the room is fullscreen and every pane is zero-area — the games play on diegetically, on
    /// the stands' in-world screens.</summary>
    Revealed,
}

/// <summary>Which RUNG of the reveal ladder a fourth-wall break drives (the north star's rung 2 vs. rung 3). The two
/// reveals are INDEPENDENT one-shot latches and may COEXIST in one session: the intro's exit/solo/meta-XOR win drives
/// the <see cref="World"/> reveal (the room/town becomes visible), while a later meta-completion drives the
/// <see cref="Editor"/> reveal (the in-session authoring UNLOCK — the workshop opens). A trigger site names its rung so
/// the two latches never conflate.</summary>
public enum RevealKind {
    /// <summary>Rung 2 — the WORLD reveal. The fourth wall breaks and eases the camera OUT of the intro machines and
    /// INTO the world the data file defines; the room becomes visible and players stand at their machines. This is the
    /// reveal every existing trigger (a brick's exit condition, a solo 128-bit win, the room-level meta XOR, and the
    /// bare <c>reveal</c> verb) drives today.</summary>
    World,

    /// <summary>Rung 3 — the EDITOR reveal. A diegetic moment (later: a meta-victory across the arcade games) discloses
    /// that this world is EDITABLE. For this stage the outcome is MINIMAL — the state-machine latch + the seam only, an
    /// in-session unlock narrated to stderr; the diegetic workbench form is a later stage. Authoring stays always-on
    /// regardless of this latch (creator / Start / console verbs never gate on it).</summary>
    Editor,
}

/// <summary>
/// Turns the simulation's presentation-facing state into the per-frame view list — PURE PRESENTATION, derived from the
/// simulation, never fed back into it (its animated state is not part of <see cref="OverworldWorld.StateHash"/>, so it
/// never perturbs determinism or replay). It always emits exactly <see cref="ViewCount"/> views (so the world
/// compositor's first-frame viewport count never changes, and hosted children keep stepping — a zero-area pane still
/// produces at 1px): view 0 is the ROOM — a shared chase framing of the active players (an overview when the room is
/// empty) — and views 1..4 are the console PANES, one per console index, zero-area until that console boots. The
/// layout keyframes come from the current <see cref="ScreenLayoutDirectorMode"/>; in every mode each rect eases from its
/// previous place (a newly-lit pane grows out of its target's center; a departing rect collapses in place), so every
/// boot — and the fourth-wall reveal itself — is a smooth screen transition.
/// </summary>
public sealed class ScreenLayoutDirector {
    /// <summary>The fixed per-frame view count: the room view plus one pane per console the room can seat — exactly
    /// the world compositor's viewport capacity.</summary>
    public const int ViewCount = SdfWorldEngine.MaxViewports;

    private const float TransitionSeconds = 0.6f; // how long a layout transition (boot or mode switch) takes
    private const float SmoothRate = 6f;          // low-pass rate for the centroid + spread metrics
    private const float FieldOfViewRadians = (50f * (MathF.PI / 180f));
    // Per-pane camera: each pane slot frames its OWN cabinet's diegetic screen, its "closeness" eased from 0 (the wide
    // room framing) to 1 (right up on the screen, so the brick fills the pane — the immersed "inside the ROM" look).
    // A break-out pane sits partway in (a medium shot of the cabinet + the player). FocusRate is how fast closeness
    // eases; PaneEyeUp/Distance place the FULLY-CLOSE camera in front of the screen (+Z, head-on, filling it).
    private const float FocusRate = 3f;
    // A fully-close pane sits head-on in FRONT of the screen (+Z) at exactly the distance that makes the screen fill
    // the viewport HEIGHT for the shared vertical FoV — so a diegetic screen reads like a real GB/GBA panel scaled up
    // (pillar-boxed on wide panes). The margin leaves a sliver of the square bezel visible.
    // CONTAIN framing: the fully-close pane fits the WHOLE screen inside the viewport (the limiting dimension fills
    // edge-to-edge, the other letterboxes), so NO gameplay is cropped. The immersed room is unlit, so the letterbox
    // margins render black — a real handheld/emulator-fullscreen look. Margin 1 = the limiting axis exactly fills.
    private const float ScreenFillMargin = 1.0f;
    // The diegetic screen's content aspect (width / height) — the native Game Boy panel. KEEP IN SYNC with
    // OverworldFrameSource.ScreenAspect (the slab is authored at this aspect).
    private const float ScreenContentAspect = (160f / 144f);
    // The REVEALED room framing: a fixed, centered isometric-ish overview of the whole group (not a tight chase), so
    // every player can roam freely. The eye sits up and in front (+Y/+Z), looking slightly toward the machine wall.
    private static readonly Vector3 IsoEyeOffset = new(0f, 10f, 14f);
    private static readonly Vector3 IsoTargetOffset = new(0f, 0.5f, -1f);
    // How much of the frame HEIGHT the room (the PRIMARY slice) keeps once players engage machines; the engaged games
    // share the remaining bottom strip as SECONDARY slices.
    private const float RoomPrimaryHeight = 0.66f;
    // A thin divider between adjacent panes (the "middle bezel") — inset only on INTERIOR edges, so panes stay flush
    // to the window edges (no outer bezel) but a gap opens between neighbours.
    private const float InterPaneGap = 0.012f;

    private static readonly Vector3 TargetLift = new(0f, 0.4f, 0f);
    // Reused per-frame view buffer — refilled (never reallocated) each Compose() call so a high-rate VRR present loop
    // allocates nothing here; valid only until the next Compose() (the renderer consumes it synchronously per frame).
    private readonly SdfViewSnapshot[] m_views = new SdfViewSnapshot[ViewCount];
    private readonly NormalizedRect[] m_current = new NormalizedRect[ViewCount];
    private readonly NormalizedRect[] m_from = new NormalizedRect[ViewCount];
    private readonly NormalizedRect[] m_target = new NormalizedRect[ViewCount];
    // The effective pane list (console indices, boot order, filtered by the mode) plus the copy the retarget check
    // compares against — both reused, no per-frame allocation.
    private readonly List<int> m_panes = new(capacity: (ViewCount - 1));
    private readonly int[] m_lastPanes = new int[ViewCount - 1];
    private ScreenLayoutDirectorMode m_mode;
    private ScreenLayoutDirectorMode m_lastMode;
    // CREATOR VIEW: an override the host raises while the player edits their avatar. It eases the ROOM to fullscreen and
    // hides every game pane REGARDLESS of the underlying mode (so creator works even from an immersed game filling the
    // screen) — the "step out to the workbench" view. The underlying mode is untouched, so lowering it restores the game.
    private bool m_creatorView;
    private bool m_lastCreatorView;
    // The eased workpiece camera (creator view) — initialized from the live chase framing on entry so the mode
    // change reads as a camera move, never a cut.
    private bool m_creatorCameraInitialized;
    private Vector3 m_creatorEye;
    private Vector3 m_creatorTarget;
    private bool m_initialized;
    private int m_lastPaneCount = -1;
    private float m_transition = 1f;
    private Vector3 m_smoothCentroid;
    private float m_smoothSpread;
    // Per-pane camera state: each pane's eased closeness (0=room framing, 1=up on its screen) and the last screen point
    // it framed (kept so an ease-OUT after a pane hides still has somewhere to blend back from).
    private readonly float[] m_paneCloseness = new float[ViewCount - 1];
    private readonly Vector3[] m_paneScreen = new Vector3[ViewCount - 1];
    private readonly float[] m_paneScreenHalfHeight = new float[ViewCount - 1];
    // The reveal zoom: the room camera eases FROM the triggering machine's native-screen framing TO the iso overview
    // over one transition. Presentation-only; started by BeginReveal, consumed in RoomEyeTarget.
    private bool m_revealActive;
    private float m_revealBlend;
    private int m_revealPane;
    // The full-frame aspect (width / height) of the most recent Compose — the reveal-from framing uses it to cover.
    private float m_frameAspect = (16f / 10f);

    /// <summary>Initializes a new instance of the <see cref="ScreenLayoutDirector"/> class.</summary>
    /// <param name="initialMode">The mode the first composed layout snaps to (an IMMERSED session must open inside the
    /// machines, never animate there from a misleading fullscreen room frame).</param>
    public ScreenLayoutDirector(ScreenLayoutDirectorMode initialMode = ScreenLayoutDirectorMode.Standard) {
        m_mode = initialMode;
        m_lastMode = initialMode;
    }

    /// <summary>Raises/lowers the CREATOR VIEW: while set, the room eases to fullscreen and all game panes hide, so a
    /// player can edit their avatar even when a game was filling the screen. Presentation-only; the underlying
    /// <see cref="ScreenLayoutDirectorMode"/> is preserved and restored when cleared.</summary>
    public bool CreatorView {
        get => m_creatorView;
        set => m_creatorView = value;
    }

    /// <summary>Gets or sets an optional per-console visibility read for the IMMERSED tiling: a booted console's pane
    /// participates only while this answers <see langword="true"/> (the overworld passes its host-side ownership map, so
    /// a pad eviction's release eases that pane out while the machine stays booted). Null admits every booted pane.
    /// Presentation-only — the read never feeds back into the simulation.</summary>
    public Func<int, bool>? ImmersedPaneVisible { get; set; }

    /// <summary>Gets or sets the REVEALED-mode break-out read: after the wall breaks, a console's pane reappears only
    /// while this answers <see langword="true"/> (the overworld passes "is this cabinet being driven"). The room stays the
    /// main view and the driven panes tile beneath/around it (room → 1:1 → 1:2 → quad). Null = no break-out (the classic
    /// reveal: room fullscreen, games on the diegetic screens). Presentation-only.</summary>
    public Func<int, bool>? BreakoutPaneVisible { get; set; }

    /// <summary>Gets or sets the per-pane camera driver: for a console index (0-based, pane slot = 1+index) it returns
    /// that cabinet's diegetic-screen center (render-relative) and how CLOSE the pane camera should sit — 0 the wide
    /// room framing, 1 right up on the screen (the brick fills the pane). Null (or a null result) leaves the pane on
    /// the room camera. The closeness eases both ways; presentation-only, never fed back into the simulation.</summary>
    public Func<int, (Vector3 ScreenPoint, float Closeness, float ScreenHalfHeight)?>? PaneCameraSource { get; set; }

    /// <summary>Gets or sets the WORKPIECE camera driver for creator mode: while <see cref="CreatorView"/> is up and
    /// this yields a frame, the room view leaves the player-chase framing for an authoring camera — an ORBIT about
    /// the target (yaw/pitch/distance) for object intent, or a locked HEAD-ON framing (what-you-see-is-what-bakes)
    /// for sprite intent. Eased both ways; null (or a null result) keeps the chase framing. Presentation-only.</summary>
    public Func<(Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)?>? CreatorCameraSource { get; set; }

    /// <summary>Gets or sets whether the workpiece camera SNAPS to the source pose instead of easing. The perf bench
    /// asserts this while a run is in flight: an eased pose converges on the wall-clock delta and never exactly
    /// arrives, so fast configurations get sampled MID-EASE and two runs never measure the same framing — a snapped
    /// pose is settled the instant the configuration starts, the same discipline as <see cref="ScenarioCameraPose"/>.
    /// Null or false keeps the ease (entering a mode reads as a move, not a cut).</summary>
    public Func<bool>? CreatorCameraSnapSource { get; set; }

    /// <summary>Gets or sets the REVEALED-room framing source — the loaded world's bounds, so the fourth-wall reveal
    /// frames the WHOLE place (a sculpted town is far larger than the default room). Null (or a null result) keeps the
    /// legacy fixed overview centred on the players — so the DEFAULT room's reveal is byte-unchanged; only a loaded
    /// world (grown bounds) overrides it. Presentation-only.</summary>
    public Func<RoomFraming?>? RoomFramingSource { get; set; }

    /// <summary>A loaded world's reveal framing: the room CENTER (at eye-height) and its planar half-extents, so the
    /// revealed overview can pull the iso camera back far enough to contain the whole lot.</summary>
    /// <param name="Center">The room center at a small eye-lift above the floor.</param>
    /// <param name="HalfWidth">Half the lot's X span.</param>
    /// <param name="HalfDepth">Half the lot's Z span.</param>
    public readonly record struct RoomFraming(Vector3 Center, float HalfWidth, float HalfDepth);

    /// <summary>Gets or sets the SCENARIO camera pose — the deterministic-capture override the <c>--scenario</c>
    /// harness supplies per shot. UNLIKE <see cref="CreatorCameraSource"/> (which the director EASES toward on the
    /// wall-clock delta, so two runs never match bit-for-bit), this pose is applied to the room view VERBATIM — no
    /// smoothing, no chase — so the framing is fully settled the instant it is set and the same shot renders
    /// byte-identically every run. Null leaves the room on its normal (chase / creator / world-sculpt) camera.
    /// Presentation-only; never fed back into the simulation.</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? ScenarioCameraPose { get; set; }

    /// <summary>Switches the layout mode. The next <see cref="Compose"/> eases every rect from its live place into the
    /// new mode's keyframes through the same transition machinery a boot uses — the REVEAL is the panes collapsing in
    /// place while the room grows out of the screen center.</summary>
    /// <param name="mode">The mode to walk the layout toward.</param>
    public void SetMode(ScreenLayoutDirectorMode mode) {
        m_mode = mode;
    }

    /// <summary>Starts the reveal zoom: the room camera eases FROM the triggering machine's native-screen framing (the
    /// player was "inside" that game) OUT to the centered iso overview over one transition. Call together with
    /// <see cref="SetMode"/>(<see cref="ScreenLayoutDirectorMode.Revealed"/>). Presentation-only.</summary>
    /// <param name="triggerPaneIndex">The console index whose fourth-wall condition fired (the pane to zoom out of).</param>
    public void BeginReveal(int triggerPaneIndex) {
        m_revealActive = true;
        m_revealBlend = 0f;
        m_revealPane = Math.Clamp(value: triggerPaneIndex, max: (ViewCount - 2), min: 0);
    }

    /// <summary>Whether the layout is fully settled — the last-composed frame's mode transition has reached its end
    /// (<c>m_transition</c> at 1) and no reveal zoom is still easing. The scripted-console <c>settle</c> verb reads
    /// this to hold the command stream until the screen layout / reveal transitions have quiesced, so a capture lands
    /// on a still frame. The per-pane closeness eases are an exponential approach (they never reach a value exactly),
    /// so they are deliberately NOT part of the quiesced test — the mode/reveal transitions are the observable
    /// "the panes are done moving" signal. Presentation-only, like the rest of the director.</summary>
    public bool LayoutSettled => ((m_transition >= 1f) && !m_revealActive);

    /// <summary>Composes the per-frame views: the room at view 0 (framing the active players, in slot order) and the
    /// console panes at views 1..4, laid out by the boot order under the current mode. Always returns
    /// <see cref="ViewCount"/> views; <paramref name="deltaSeconds"/> drives the animation.</summary>
    /// <param name="activePositions">The active players' render-relative positions (slot order).</param>
    /// <param name="bootOrder">The booted console indices in boot order (the pane-assignment order).</param>
    /// <param name="imageWidth">The full frame width in pixels.</param>
    /// <param name="imageHeight">The full frame height in pixels.</param>
    /// <param name="deltaSeconds">The frame's wall-clock delta (animation only).</param>
    /// <returns>The reused, per-slot view buffer.</returns>
    public IReadOnlyList<SdfViewSnapshot> Compose(IReadOnlyList<Vector3> activePositions, IReadOnlyList<int> bootOrder, uint imageWidth, uint imageHeight, float deltaSeconds) {
        ArgumentNullException.ThrowIfNull(activePositions);
        ArgumentNullException.ThrowIfNull(bootOrder);

        AdvanceLayout(bootOrder: bootOrder, deltaSeconds: deltaSeconds);

        m_frameAspect = ((imageHeight == 0) ? m_frameAspect : ((float)imageWidth / imageHeight));

        // The machines are decoupled from the slots now, so EVERY slot is a real SDF view: slot 0 the wide room, slots
        // 1..4 per-cabinet cameras (framing each cabinet's diegetic screen at their own eased closeness).
        var (roomEye, roomTarget) = RoomEyeTarget(activePositions: activePositions, deltaSeconds: deltaSeconds);

        for (var slot = 0; (slot < ViewCount); slot++) {
            var region = m_current[slot];
            var viewportWidth = Math.Max(1u, (uint)(region.Width * imageWidth));
            var viewportHeight = Math.Max(1u, (uint)(region.Height * imageHeight));

            var viewportAspect = ((float)viewportWidth / viewportHeight);
            var (eye, target) = ((slot == 0)
                ? (roomEye, roomTarget)
                : PaneEyeTarget(paneIndex: (slot - 1), roomEye: roomEye, roomTarget: roomTarget, deltaSeconds: deltaSeconds, viewportAspect: viewportAspect));

            m_views[slot] = new SdfViewSnapshot(
                Camera: CameraSnapshot.LookAt(position: eye, target: target, fieldOfViewRadians: FieldOfViewRadians, viewportWidth: viewportWidth, viewportHeight: viewportHeight),
                Region: region
            );
        }

        return m_views;
    }

    // Walk the current rects toward the mode's layout targets: on a pane-list or mode change, snapshot the live rects
    // as the new FROM set (a pane lighting up starts collapsed at its target's center; a departing rect collapses in
    // place) and restart the ease.
    private void AdvanceLayout(IReadOnlyList<int> bootOrder, float deltaSeconds) {
        BuildPaneList(bootOrder: bootOrder);

        if (LayoutChanged()) {
            var firstLayout = (m_lastPaneCount < 0);

            BuildTargets();

            for (var slot = 0; (slot < ViewCount); slot++) {
                var wasVisible = ((m_current[slot].Width > 0f) && (m_current[slot].Height > 0f));

                m_from[slot] = (wasVisible
                    ? m_current[slot]
                    : CenterOf(rect: m_target[slot]));

                // A rect LEAVING the layout (the reveal's panes, an evicted player's pane) collapses toward its OWN
                // center rather than sliding to the frame origin's zero rect — the reveal reads as the panes shrinking
                // into the room.
                if (wasVisible && (m_target[slot].Width <= 0f) && (m_target[slot].Height <= 0f)) {
                    m_target[slot] = CenterOf(rect: m_current[slot]);
                }
            }

            m_lastMode = m_mode;
            m_lastCreatorView = m_creatorView;
            m_lastPaneCount = m_panes.Count;

            for (var pane = 0; (pane < m_panes.Count); pane++) {
                m_lastPanes[pane] = m_panes[pane];
            }

            // A session that OPENS mid-layout (a scripted boot mask applied before the first frame, or an immersed
            // start) snaps instead of animating from a misleading fullscreen frame.
            m_transition = (firstLayout ? 1f : 0f);
        }

        m_transition = Math.Clamp(value: (m_transition + (deltaSeconds / TransitionSeconds)), min: 0f, max: 1f);

        var ease = SmoothStep(t: m_transition);

        for (var slot = 0; (slot < ViewCount); slot++) {
            m_current[slot] = Lerp(a: m_from[slot], b: m_target[slot], t: ease);
        }
    }

    // The mode's effective pane list, in boot order: STANDARD shows every booted console; IMMERSED additionally
    // filters through the visibility read (ownership, host-side); REVEALED shows none (the games live on the
    // diegetic stand screens instead).
    private void BuildPaneList(IReadOnlyList<int> bootOrder) {
        m_panes.Clear();

        // Creator view hides every game pane — the workbench is room-only.
        if (m_creatorView) {
            return;
        }

        foreach (var consoleIndex in bootOrder) {
            if ((consoleIndex < 0) || ((1 + consoleIndex) >= ViewCount)) {
                continue;
            }

            // REVEALED shows a pane only for a BROKEN-OUT (driven) console — the room stays the main view, the driven
            // panes tile around it. IMMERSED filters through its ownership read. STANDARD shows every booted console.
            var include = m_mode switch {
                ScreenLayoutDirectorMode.Revealed => (BreakoutPaneVisible?.Invoke(consoleIndex) ?? false),
                ScreenLayoutDirectorMode.Immersed => (ImmersedPaneVisible?.Invoke(consoleIndex) ?? true),
                _ => true,
            };

            if (include) {
                m_panes.Add(item: consoleIndex);
            }
        }
    }

    // Whether the effective layout inputs moved since the last retarget — the mode, or the pane list's length OR
    // membership (an ownership release changes membership without changing the boot count).
    private bool LayoutChanged() {
        if ((m_mode != m_lastMode) || (m_creatorView != m_lastCreatorView) || (m_panes.Count != m_lastPaneCount)) {
            return true;
        }

        for (var pane = 0; (pane < m_panes.Count); pane++) {
            if (m_panes[pane] != m_lastPanes[pane]) {
                return true;
            }
        }

        return false;
    }

    // The layout keyframes for the current mode. Pane k (the effective pane order) gets the k-th pane rect of the
    // stage; the room takes the rest (STANDARD), nothing (IMMERSED), or everything (REVEALED).
    private void BuildTargets() {
        for (var slot = 0; (slot < ViewCount); slot++) {
            m_target[slot] = Hidden;
        }

        var count = m_panes.Count;

        // Creator view: the room fills the whole frame (panes were cleared in BuildPaneList, so they stay Hidden).
        if (m_creatorView) {
            m_target[0] = new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 1f);

            return;
        }

        m_target[0] = m_mode switch {
            ScreenLayoutDirectorMode.Immersed => Hidden,
            ScreenLayoutDirectorMode.Revealed => RevealedRoomRect(count: count),
            _ => StandardRoomRect(count: count),
        };

        for (var pane = 0; (pane < count); pane++) {
            m_target[1 + m_panes[pane]] = m_mode switch {
                // Immersed panes cover the whole frame; a thin interior gap divides neighbours without an outer bezel.
                ScreenLayoutDirectorMode.Immersed => WithInteriorGaps(rect: ImmersedPaneRect(count: count, pane: pane)),
                ScreenLayoutDirectorMode.Revealed => WithInteriorGaps(rect: RevealedPaneRect(count: count, pane: pane)),
                _ => PaneRect(count: count, pane: pane),
            };
        }
    }

    // Insets a rect on the edges that are INTERIOR to the frame (not touching a window edge), opening the inter-pane
    // divider while keeping panes flush to the outside. A degenerate result collapses to nothing (harmless).
    private static NormalizedRect WithInteriorGaps(NormalizedRect rect) {
        const float half = (InterPaneGap * 0.5f);

        var x0 = (rect.X > 0.001f) ? (rect.X + half) : rect.X;
        var y0 = (rect.Y > 0.001f) ? (rect.Y + half) : rect.Y;
        var x1 = ((rect.X + rect.Width) < 0.999f) ? ((rect.X + rect.Width) - half) : (rect.X + rect.Width);
        var y1 = ((rect.Y + rect.Height) < 0.999f) ? ((rect.Y + rect.Height) - half) : (rect.Y + rect.Height);

        return new NormalizedRect(X: x0, Y: y0, Width: MathF.Max(0f, (x1 - x0)), Height: MathF.Max(0f, (y1 - y0)));
    }

    // The REVEALED break-out layout: the room stays the MAIN view and the driven panes tile with it — room fullscreen
    // (none driving) → room top / one pane bottom (1:1) → room top / two panes bottom (1:2) → the 2×2 quad (three
    // driving), exactly the staged split the design calls for.
    // The reveal split: the room is the big PRIMARY slice across the top; each engaged player's game is a SECONDARY
    // slice sharing the bottom strip in engaged order. Nobody engaged (count 0) = the room fullscreen.
    private static NormalizedRect RevealedRoomRect(int count) {
        return (count == 0)
            ? FullScreen
            : new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: RoomPrimaryHeight);
    }
    private static NormalizedRect RevealedPaneRect(int count, int pane) {
        var width = (1f / count);

        return new NormalizedRect(X: (width * pane), Y: RoomPrimaryHeight, Width: width, Height: (1f - RoomPrimaryHeight));
    }

    // The STANDARD room rect, staged by the booted-pane count. Stages 0..3 are pixel-identical to the pre-immersed
    // director; the 4-boot stage keeps the room on top at half height over four quarter panes.
    private static NormalizedRect StandardRoomRect(int count) {
        return count switch {
            0 => FullScreen,                                                  // the room alone
            1 => new NormalizedRect(X: 0f, Y: 0f, Width: 0.5f, Height: 1f),   // room | pane, side by side
            2 => new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 0.6f),   // big room top, two small panes below
            3 => new NormalizedRect(X: 0f, Y: 0f, Width: 0.5f, Height: 0.5f), // the 2x2 quad
            _ => new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 0.5f),   // room top half, four quarter panes below
        };
    }
    private static NormalizedRect PaneRect(int count, int pane) {
        return count switch {
            1 => new NormalizedRect(X: 0.5f, Y: 0f, Width: 0.5f, Height: 1f),
            2 => ((pane == 0)
                ? new NormalizedRect(X: 0f, Y: 0.6f, Width: 0.5f, Height: 0.4f)
                : new NormalizedRect(X: 0.5f, Y: 0.6f, Width: 0.5f, Height: 0.4f)),
            3 => pane switch {
                0 => new NormalizedRect(X: 0.5f, Y: 0f, Width: 0.5f, Height: 0.5f),
                1 => new NormalizedRect(X: 0f, Y: 0.5f, Width: 0.5f, Height: 0.5f),
                _ => new NormalizedRect(X: 0.5f, Y: 0.5f, Width: 0.5f, Height: 0.5f),
            },
            // The 4-boot stage: four quarter panes across the bottom, in pane order.
            _ => new NormalizedRect(X: (0.25f * pane), Y: 0.5f, Width: 0.25f, Height: 0.5f),
        };
    }
    // The IMMERSED tiling: the visible panes alone cover the whole frame — fullscreen, side-by-side halves,
    // big-top + two-bottom, then the 2×2 quad (the same shapes the standard stages walk, minus the room).
    private static NormalizedRect ImmersedPaneRect(int count, int pane) {
        return count switch {
            1 => FullScreen,
            2 => new NormalizedRect(X: (0.5f * pane), Y: 0f, Width: 0.5f, Height: 1f),
            3 => pane switch {
                0 => new NormalizedRect(X: 0f, Y: 0f, Width: 1f, Height: 0.5f),
                1 => new NormalizedRect(X: 0f, Y: 0.5f, Width: 0.5f, Height: 0.5f),
                _ => new NormalizedRect(X: 0.5f, Y: 0.5f, Width: 0.5f, Height: 0.5f),
            },
            _ => new NormalizedRect(X: (0.5f * (pane % 2)), Y: (0.5f * (pane / 2)), Width: 0.5f, Height: 0.5f),
        };
    }

    // The WIDE room framing (eye + target) — a shared chase that fits every active player (eye pulled up + back as the
    // spread grows), an overview when the room is empty. Slot 0 always uses it; the pane cameras blend FROM it toward
    // their cabinet screens.
    private (Vector3 Eye, Vector3 Target) RoomEyeTarget(IReadOnlyList<Vector3> activePositions, float deltaSeconds) {
        // The scenario harness's deterministic shot pose (when set) wins outright and applies VERBATIM — no chase, no
        // ease — so a shot renders identically every run regardless of the wall-clock frame delta.
        if (ScenarioCameraPose is { } pose) {
            return ScenarioEyeTarget(pose: pose);
        }

        if (activePositions.Count == 0) {
            return (new Vector3(0f, 12f, 16f), Vector3.Zero);
        }

        var centroid = Vector3.Zero;

        for (var index = 0; (index < activePositions.Count); index++) {
            centroid += activePositions[index];
        }

        centroid /= activePositions.Count;

        var spread = 0f;

        for (var index = 0; (index < activePositions.Count); index++) {
            var delta = (activePositions[index] - centroid);

            spread = MathF.Max(spread, MathF.Sqrt((delta.X * delta.X) + (delta.Z * delta.Z)));
        }

        if (!m_initialized) {
            m_smoothCentroid = centroid;
            m_smoothSpread = spread;
            m_initialized = true;
        } else {
            var smoothing = MathF.Min(1f, (SmoothRate * deltaSeconds));

            m_smoothCentroid += ((centroid - m_smoothCentroid) * smoothing);
            m_smoothSpread += ((spread - m_smoothSpread) * smoothing);
        }

        // REVEALED: a fixed, centered isometric-ish overview so every player can roam — the room is the primary slice.
        // On the reveal itself the camera eases OUT of the triggering machine's native-screen framing into this.
        if (m_mode == ScreenLayoutDirectorMode.Revealed) {
            var (isoEye, isoTarget) = RevealOverview();

            if (!m_revealActive) {
                return (isoEye, isoTarget);
            }

            m_revealBlend = Math.Clamp(value: (m_revealBlend + (deltaSeconds / TransitionSeconds)), min: 0f, max: 1f);

            var (fromEye, fromTarget) = RevealFromCamera(fallbackEye: isoEye, fallbackTarget: isoTarget);
            var ease = SmoothStep(t: m_revealBlend);

            if (m_revealBlend >= 1f) {
                m_revealActive = false;
            }

            return (Vector3.Lerp(value1: fromEye, value2: isoEye, amount: ease), Vector3.Lerp(value1: fromTarget, value2: isoTarget, amount: ease));
        }

        // STANDARD / IMMERSED: the chase framing that fits every active player (eye pulled up + back as spread grows).
        var target = (m_smoothCentroid + TargetLift);
        var eye = (m_smoothCentroid + new Vector3(0f, (6.5f + m_smoothSpread), (11f + (m_smoothSpread * 1.5f))));

        // The CREATOR workpiece camera: while the creator view is up, the room view leaves the chase framing for the
        // authoring camera — eased from wherever the room camera was, so entering the mode reads as a move, not a cut.
        if (m_creatorView && (CreatorCameraSource?.Invoke() is { } workpiece)) {
            var desiredTarget = workpiece.Target;
            // Object intent orbits the target; sprite intent locks HEAD-ON (+Z looking toward -Z, zero pitch) so the
            // authored silhouette is exactly what the bake will rasterize.
            var desiredEye = (workpiece.Sprite
                ? (desiredTarget + new Vector3(0f, 0f, workpiece.Distance))
                : (desiredTarget + (new Vector3(
                    (MathF.Sin(x: workpiece.Yaw) * MathF.Cos(x: workpiece.Pitch)),
                    MathF.Sin(x: workpiece.Pitch),
                    (MathF.Cos(x: workpiece.Yaw) * MathF.Cos(x: workpiece.Pitch))
                ) * workpiece.Distance)));

            if (!m_creatorCameraInitialized) {
                // Enter by easing FROM the live chase framing.
                m_creatorEye = eye;
                m_creatorTarget = target;
                m_creatorCameraInitialized = true;
            }

            if (CreatorCameraSnapSource?.Invoke() ?? false) {
                // The bench's measurement pose: applied VERBATIM (no smoothing), so every configuration samples an
                // identical, fully settled framing and two runs produce comparable tables.
                m_creatorEye = desiredEye;
                m_creatorTarget = desiredTarget;

                return (m_creatorEye, m_creatorTarget);
            }

            var creatorSmoothing = MathF.Min(1f, (SmoothRate * deltaSeconds));

            m_creatorEye += ((desiredEye - m_creatorEye) * creatorSmoothing);
            m_creatorTarget += ((desiredTarget - m_creatorTarget) * creatorSmoothing);

            return (m_creatorEye, m_creatorTarget);
        }

        m_creatorCameraInitialized = false;

        return (eye, target);
    }

    // The scenario shot's eye/target, derived VERBATIM from the pose (same orbit vs. head-on math the creator
    // workpiece camera uses, minus the easing): object intent orbits the target by yaw/pitch/distance; sprite intent
    // locks head-on (+Z, zero pitch) so the authored silhouette is exactly what a bake would rasterize.
    private static (Vector3 Eye, Vector3 Target) ScenarioEyeTarget((Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite) pose) {
        var eye = (pose.Sprite
            ? (pose.Target + new Vector3(0f, 0f, pose.Distance))
            : (pose.Target + (new Vector3(
                (MathF.Sin(x: pose.Yaw) * MathF.Cos(x: pose.Pitch)),
                MathF.Sin(x: pose.Pitch),
                (MathF.Cos(x: pose.Yaw) * MathF.Cos(x: pose.Pitch))
            ) * pose.Distance)));

        return (eye, pose.Target);
    }

    // The revealed overview's settled eye/target. A loaded world (the town) frames its whole LOT — centred on the lot,
    // the iso offset pulled back proportional to how much bigger the lot is than the default room (half-extent 8u) — so
    // nothing spills off-frame. With no world loaded the framing is the legacy fixed overview around the player
    // centroid (byte-unchanged for the default room's reveal).
    private (Vector3 Eye, Vector3 Target) RevealOverview() {
        if (RoomFramingSource?.Invoke() is { } framing) {
            var scale = MathF.Max(1f, (MathF.Max(framing.HalfWidth, framing.HalfDepth) / 8f));

            return ((framing.Center + (IsoEyeOffset * scale)), (framing.Center + IsoTargetOffset));
        }

        return ((m_smoothCentroid + IsoEyeOffset), (m_smoothCentroid + IsoTargetOffset));
    }

    // The reveal's FROM framing: the triggering machine's native-screen camera (the shot the player was "inside").
    private (Vector3 Eye, Vector3 Target) RevealFromCamera(Vector3 fallbackEye, Vector3 fallbackTarget) {
        if (PaneCameraSource?.Invoke(m_revealPane) is { } request) {
            return NativeScreenCamera(screen: request.ScreenPoint, screenHalfHeight: request.ScreenHalfHeight, viewportAspect: m_frameAspect);
        }

        return (fallbackEye, fallbackTarget);
    }

    // The head-on camera that CONTAINS the whole screen in the viewport (full gameplay, no crop; the letterbox margins
    // fall on the unlit room = black bars): sit in FRONT of the screen (+Z, the face normal) at the distance where the
    // LIMITING dimension of the screen just fits the FoV. Head-on, so there is no perspective keystone.
    private static (Vector3 Eye, Vector3 Target) NativeScreenCamera(Vector3 screen, float screenHalfHeight, float viewportAspect) {
        // Fill the vertical half-FoV; push the camera BACK when the screen is wider (per its content aspect) than the
        // viewport so the horizontal axis just fits instead — max() picks the further camera = contain (whole screen in).
        var fit = MathF.Max(x: 1f, y: (ScreenContentAspect / MathF.Max(viewportAspect, 1e-3f)));
        var distance = (((screenHalfHeight * ScreenFillMargin) * fit) / MathF.Tan(x: (FieldOfViewRadians * 0.5f)));

        return ((screen + new Vector3(0f, 0f, distance)), screen);
    }

    /// <summary>The room-light multiplier for this frame (the frame source scales its ambient + sun by it): the room is
    /// UNLIT (0) while immersed so the letterbox margins around a filled screen render BLACK, and eases up to fully lit
    /// (1) as the reveal pulls the camera out into the room. Standard mode is always lit. Presentation-only.</summary>
    public float RoomLightFactor => (m_creatorView ? 1f : m_mode switch {
        ScreenLayoutDirectorMode.Immersed => 0f,
        ScreenLayoutDirectorMode.Revealed => (m_revealActive ? SmoothStep(t: m_revealBlend) : 1f),
        _ => 1f,
    });

    // A pane's camera: eased from the wide room framing toward "right up on" its cabinet's diegetic screen. Closeness 0
    // = the room camera (so a not-yet-broken-out pane matches the room); 1 = head-on and close, the screen filling the
    // pane natively. The overworld drives the target closeness (immersed / engaged = 1, hidden = 0); it eases here.
    private (Vector3 Eye, Vector3 Target) PaneEyeTarget(int paneIndex, Vector3 roomEye, Vector3 roomTarget, float deltaSeconds, float viewportAspect) {
        var request = PaneCameraSource?.Invoke(paneIndex);
        var goal = ((request is { } r) ? Math.Clamp(value: r.Closeness, min: 0f, max: 1f) : 0f);

        if (request is { } req) {
            // Remember screen + half-height, so an ease-out after the pane hides still blends from the right framing.
            m_paneScreen[paneIndex] = req.ScreenPoint;
            m_paneScreenHalfHeight[paneIndex] = req.ScreenHalfHeight;
        }

        m_paneCloseness[paneIndex] += ((goal - m_paneCloseness[paneIndex]) * MathF.Min(1f, (FocusRate * deltaSeconds)));

        if (m_paneCloseness[paneIndex] <= 0.001f) {
            return (roomEye, roomTarget);
        }

        var blend = SmoothStep(m_paneCloseness[paneIndex]);
        var (closeEye, closeTarget) = NativeScreenCamera(screen: m_paneScreen[paneIndex], screenHalfHeight: m_paneScreenHalfHeight[paneIndex], viewportAspect: viewportAspect);

        return (Vector3.Lerp(value1: roomEye, value2: closeEye, amount: blend), Vector3.Lerp(value1: roomTarget, value2: closeTarget, amount: blend));
    }

    private static NormalizedRect FullScreen => new(X: 0f, Y: 0f, Width: 1f, Height: 1f);
    private static NormalizedRect Hidden => new(X: 0f, Y: 0f, Width: 0f, Height: 0f);

    private static NormalizedRect CenterOf(NormalizedRect rect) =>
        new(X: (rect.X + (0.5f * rect.Width)), Y: (rect.Y + (0.5f * rect.Height)), Width: 0f, Height: 0f);
    private static NormalizedRect Lerp(NormalizedRect a, NormalizedRect b, float t) =>
        new(
            Height: (a.Height + ((b.Height - a.Height) * t)),
            Width: (a.Width + ((b.Width - a.Width) * t)),
            X: (a.X + ((b.X - a.X) * t)),
            Y: (a.Y + ((b.Y - a.Y) * t))
        );

    private static float SmoothStep(float t) => ((t * t) * (3f - (2f * t)));
}
