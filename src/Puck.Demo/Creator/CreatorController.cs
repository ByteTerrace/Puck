using System.Numerics;
using Puck.Commands;
using Puck.Input.Devices;
using Puck.SdfVm;

namespace Puck.Demo.Creator;

/// <summary>The editor's verb pages — CHORD-SELECTED (see <see cref="CreatorController"/>'s remarks), not cycled.
/// The wire value doubles as the binding-bar's page index (<c>OverworldFrameSource.CreatorBarPage</c> casts this
/// straight to <see langword="int"/>).</summary>
public enum CreatorPage {
    /// <summary>Bare (no modifier held): place/undo/redo/cycle-primitive — the classic authoring page.</summary>
    Sculpt = 0,
    /// <summary>[LT] held: selection — cycle/duplicate/delete/link/ungroup.</summary>
    Select = 1,
    /// <summary>[RT] held: composition style — material, blend op, mirror/twist/onion, the bake style knob.</summary>
    Style = 2,
    /// <summary>[LT,RT] held (LT pressed first): animation frames — step/record/delete/play/rest.</summary>
    Animate = 3,
    /// <summary>[RT,LT] held (RT pressed first): the RIG — chains, goals, poles, gait.</summary>
    Rig = 4,
}

/// <summary>
/// The creator mode's pad state machine — extracted from the overworld render node so the authoring input surface can
/// grow without swelling the node. Creator input deliberately BYPASSES the binding-page system: it repurposes the same
/// physical buttons the gameplay pages bind, so it tracks its own edges (and its OWN order-sensitive chord tracker —
/// see <see cref="AdvancePage"/>) against the raw pad state the node samples for the creating slot (slot 0). Leaving
/// the mode is the NODE's affair (it eases the director back and narrates), so the EXIT press only latches a request
/// the node consumes next frame (<see cref="ConsumeExitRequest"/>).
///
/// THE CHORD-FIRST CONTROL SCHEME (the arc's UX law): the triggers are PURE CHORD MODIFIERS, exactly like
/// <c>BindingProfileDocuments</c>' order-sensitive held sequences — bare, [LT], [RT], [LT,RT], [RT,LT] select one of
/// FIVE pages (<see cref="CreatorPage"/>), by PRESS ORDER while both stay held (LT-then-RT ≠ RT-then-LT). <c>Back</c>
/// remains a legacy linear cycle through the five pages as a fallback for a pad with unreliable analog triggers.
///
/// GLOBAL bindings (every page): left stick moves the TARGET planar (X/Z), right stick spins it (yaw/pitch), d-pad
/// up/down raises/lowers it, d-pad left/right rolls it — EXCEPT the STYLE page, which repurposes the whole d-pad
/// (documented on <see cref="AdvanceStylePage"/>) and the RIG page, which repurposes it for the cursor chain's pole
/// nudge (see <see cref="AdvanceRigPage"/>). Left-stick click toggles shape↔group scope; right-stick click toggles
/// the workpiece CAMERA mode (sticks/triggers hand to the orbit camera until toggled off — the chord tracker simply
/// stops advancing while camera mode is up, since nothing reads the triggers for anything else then). North EXITS
/// creator mode on every page. <c>Start</c> stays the node's commit hotkey (read outside this class).
/// </summary>
public sealed class CreatorController {
    private const int ActivePageCount = 5;

    // The orbit envelope: pitch stays off the poles, distance stays outside the workpiece and inside the room.
    private const float MinPitch = 0.05f;
    private const float MaxDistance = 14f;
    private const float MaxPitch = 1.35f;
    private const float MinDistance = 2f;
    // The order-sensitive chord tracker's press/release hysteresis on the trigger axes (mirrors
    // BindingModifierDefinition's defaults, so the creator's own chord feel matches the paged binding system).
    private const float ModifierPressThreshold = 0.5f;
    private const float ModifierReleaseThreshold = 0.4f;

    private readonly CreatorScene m_scene;
    private readonly Action<string> m_narrate;
    private CreatorPage m_page;
    private GamepadButtons m_prevButtons;
    private bool m_exitRequested;
    // The order-sensitive LT/RT chord tracker: which modifiers are latched held, in PRESS ORDER, so [LT,RT] and
    // [RT,LT] resolve to different pages while both stay held. Creator input bypasses the paged binding system
    // entirely (it repurposes the same physical buttons the gameplay pages bind), so it drives the shared
    // Puck.Commands.HeldOrderTracker primitive directly against raw pad state instead of through PagedInputBindings.
    private readonly HeldOrderTracker m_modifierTracker = new(modifierCount: 2, pressThreshold: ModifierPressThreshold, releaseThreshold: ModifierReleaseThreshold);
    // The workpiece camera (always driving the room view while creator is up): an orbit about a pannable target.
    // Camera MODE (right-stick click) hands the sticks/triggers to it; outside the mode it holds its last framing.
    private bool m_cameraMode;
    private float m_orbitYaw;
    private float m_orbitPitch = 0.5f;
    private float m_orbitDistance = 6.5f;
    private Vector3 m_orbitTarget;
    private bool m_orbitTargetInitialized;

    /// <summary>Initializes the controller over the scene it edits.</summary>
    /// <param name="scene">The authored scene the verbs mutate.</param>
    /// <param name="narrate">Writes a one-line status to the player's console (page flips, verb outcomes).</param>
    public CreatorController(CreatorScene scene, Action<string> narrate) {
        ArgumentNullException.ThrowIfNull(narrate);
        ArgumentNullException.ThrowIfNull(scene);

        m_narrate = narrate;
        m_scene = scene;
    }

    /// <summary>The scene this controller edits.</summary>
    public CreatorScene Scene => m_scene;

    /// <summary>The active verb page (drives the binding bar's layout).</summary>
    public CreatorPage Page => m_page;

    /// <summary>The workpiece camera's frame for the screen director: the orbit target + yaw/pitch/distance, and
    /// whether the SPRITE intent's locked head-on framing applies. Null while the scene is down.</summary>
    public (Vector3 Target, float Yaw, float Pitch, float Distance, bool Sprite)? CameraFrame {
        get {
            if (!m_scene.Active) {
                return null;
            }

            if (!m_orbitTargetInitialized) {
                m_orbitTarget = m_scene.Workbench.SpawnPosition;
                m_orbitTargetInitialized = true;
            }

            return (m_orbitTarget, m_orbitYaw, m_orbitPitch, m_orbitDistance, (m_scene.Intent == CreatorIntent.Sprite));
        }
    }

    /// <summary>Clears the edge tracking, any pending exit, camera mode, the chord tracker, and returns to the
    /// SCULPT page — call when the mode toggles so a held button/trigger never fires a stale edge into the other
    /// mode.</summary>
    public void Reset() {
        m_cameraMode = false;
        m_exitRequested = false;
        m_page = CreatorPage.Sculpt;
        m_prevButtons = GamepadButtons.None;
        m_modifierTracker.Reset();
    }

    /// <summary>Returns whether the EXIT verb fired since the last consume (and clears it) — the node leaves the
    /// mode and restores the view when this reports true.</summary>
    public bool ConsumeExitRequest() {
        var requested = m_exitRequested;

        m_exitRequested = false;

        return requested;
    }

    /// <summary>Advances one frame of creator input (see the class remarks for the chord map). Presentation only —
    /// nothing here reaches the deterministic world.</summary>
    /// <param name="raw">The creating slot's raw pad state this frame.</param>
    /// <param name="deltaSeconds">The frame delta.</param>
    public void Advance(in GamepadState raw, float deltaSeconds) {
        var buttons = raw.Buttons;

        bool Pressed(GamepadButtons button) => ((0 != (buttons & button)) && (0 == (m_prevButtons & button)));
        bool Held(GamepadButtons button) => (0 != (buttons & button));

        // CAMERA MODE (right-stick click): the sticks/triggers drive the workpiece camera instead of the target —
        // left stick orbits (yaw/pitch), triggers zoom, right stick pans the orbit target across the workbench.
        // Everything else idles until the mode toggles off, so a camera move can never mutate the scene, and the
        // chord tracker is left exactly as it was (a camera-mode trigger tap must not silently reassign the page).
        if (Pressed(button: GamepadButtons.RightStickPress)) {
            m_cameraMode = !m_cameraMode;
            m_narrate($"[creator] camera {(m_cameraMode ? "ON — left stick orbits, triggers zoom, right stick pans; click again to edit" : "off — editing resumes")}");
        }

        if (m_cameraMode) {
            AdvanceCamera(raw: in raw, deltaSeconds: deltaSeconds);
            m_prevButtons = buttons;
            m_scene.EndInputFrame();

            return;
        }

        AdvancePage(raw: in raw);

        // GLOBAL — the target's transform. Every page shares left-stick planar move and right-stick yaw/pitch. The
        // d-pad's meaning is the one page-dependent surface: STYLE repurposes both axes for its twist/onion sweeps
        // and RIG for the cursor chain's pole nudge (handled inside their own Advance* methods below); every OTHER
        // page reads the shared default here — up/down raises/lowers, left/right rolls.
        m_scene.Move(
            deltaSeconds: deltaSeconds,
            planar: new Vector2(x: raw.LeftStick.X, y: -raw.LeftStick.Y),
            vertical: ((m_page is CreatorPage.Style or CreatorPage.Rig) ? 0f : ((Held(button: GamepadButtons.DpadUp) ? 1f : 0f) - (Held(button: GamepadButtons.DpadDown) ? 1f : 0f)))
        );
        m_scene.Rotate(
            deltaSeconds: deltaSeconds,
            roll: ((m_page is CreatorPage.Style or CreatorPage.Rig) ? 0f : ((Held(button: GamepadButtons.DpadRight) ? 1f : 0f) - (Held(button: GamepadButtons.DpadLeft) ? 1f : 0f))),
            stick: raw.RightStick
        );

        if (Pressed(button: GamepadButtons.LeftStickPress)) {
            m_narrate($"[creator] scope: {(m_scene.ToggleGroupScope() ? "GROUP (transforms move the whole group about its centroid)" : "shape")}");
        }

        // Legacy fallback: Back still linearly cycles the five pages (a pad with unreliable analog triggers can
        // still reach every page). It does not touch the chord tracker, so releasing the modifiers afterward
        // re-asserts whatever page they actually spell.
        if (Pressed(button: GamepadButtons.Back)) {
            m_page = (CreatorPage)(((int)m_page + 1) % ActivePageCount);
            m_narrate($"[creator] page: {PageName(page: m_page)}");
        }

        switch (m_page) {
            case CreatorPage.Select:
                AdvanceSelectPage(pressed: Pressed);
                break;
            case CreatorPage.Style:
                AdvanceStylePage(pressed: Pressed, held: Held, deltaSeconds: deltaSeconds);
                break;
            case CreatorPage.Animate:
                AdvanceAnimatePage(pressed: Pressed);
                break;
            case CreatorPage.Rig:
                AdvanceRigPage(pressed: Pressed, held: Held, deltaSeconds: deltaSeconds);
                break;
            default:
                AdvanceSculptPage(pressed: Pressed);
                break;
        }

        if (Pressed(button: GamepadButtons.ButtonNorth)) {
            m_exitRequested = true;
        }

        // Playback runs regardless of the active page (author on SCULPT while the loop plays on the workbench).
        m_scene.TickPlayback(deltaSeconds: deltaSeconds);
        // Closes any drag whose continuous verb did not fire THIS frame — must run after every verb above.
        m_scene.EndInputFrame();

        m_prevButtons = buttons;
    }

    // The order-sensitive LT/RT chord tracker: feeds both triggers into the shared HeldOrderTracker (hysteresis
    // mirroring BindingModifierDefinition's defaults) and reads back the held set in PRESS ORDER, so [LT,RT] (left
    // pressed while right is already held) and [RT,LT] resolve to distinct pages — the exact semantics
    // BindingChordTracker resolves a binding page from, applied here because creator input never touches
    // PagedInputBindings.
    private void AdvancePage(in GamepadState raw) {
        const int leftModifier = 0;
        const int rightModifier = 1;

        _ = m_modifierTracker.Set(index: leftModifier, value: raw.LeftTrigger);
        _ = m_modifierTracker.Set(index: rightModifier, value: raw.RightTrigger);

        var heldOrder = m_modifierTracker.HeldOrder;
        var next = heldOrder.Length switch {
            0 => CreatorPage.Sculpt,
            1 => ((heldOrder[0] == leftModifier) ? CreatorPage.Select : CreatorPage.Style),
            _ => ((heldOrder[0] == leftModifier) ? CreatorPage.Animate : CreatorPage.Rig),
        };

        if (next != m_page) {
            m_page = next;
            m_narrate($"[creator] page: {PageName(page: m_page)}");
        }
    }

    // The camera-mode stick handling: orbit + zoom + pan, all clamped/eased at authoring-comfortable rates.
    private void AdvanceCamera(in GamepadState raw, float deltaSeconds) {
        const float orbitSpeed = 2.4f;  // radians/second at full deflection
        const float panSpeed = 4.0f;    // world units/second at full deflection

        m_orbitYaw += ((raw.LeftStick.X * orbitSpeed) * deltaSeconds);
        m_orbitPitch = Math.Clamp(value: (m_orbitPitch + ((raw.LeftStick.Y * orbitSpeed) * deltaSeconds)), max: MaxPitch, min: MinPitch);
        m_orbitDistance = Math.Clamp(value: (m_orbitDistance * MathF.Exp(x: (((raw.LeftTrigger - raw.RightTrigger) * 1.2f) * deltaSeconds))), max: MaxDistance, min: MinDistance);

        if (raw.RightStick != Vector2.Zero) {
            // Pan in camera-relative planar axes (right = orbit-right, up on the stick = away from the camera), so
            // the target moves the way the view suggests regardless of the orbit angle.
            var forward = new Vector2(x: -MathF.Sin(x: m_orbitYaw), y: -MathF.Cos(x: m_orbitYaw));
            var right = new Vector2(x: -forward.Y, y: forward.X);
            var planar = (((right * raw.RightStick.X) + (forward * raw.RightStick.Y)) * (panSpeed * deltaSeconds));

            m_orbitTarget = m_scene.Workbench.Clamp(position: (m_orbitTarget + new Vector3(x: planar.X, y: 0f, z: planar.Y)));
        }
    }

    // SCULPT (bare): bumpers cycle the primitive, South places, East undoes, West redoes, d-pad up/down raises,
    // d-pad left/right rolls (the GLOBAL default, applied centrally in Advance — this page makes no d-pad exception).
    private void AdvanceSculptPage(Func<GamepadButtons, bool> pressed) {
        if (pressed(arg: GamepadButtons.RightShoulder)) { m_scene.CyclePrimitive(direction: 1); }
        if (pressed(arg: GamepadButtons.LeftShoulder)) { m_scene.CyclePrimitive(direction: -1); }
        if (pressed(arg: GamepadButtons.ButtonSouth)) { m_scene.Place(); }

        if (pressed(arg: GamepadButtons.ButtonEast)) {
            m_narrate((m_scene.Undo() ? $"[creator] undo — {m_scene.PlacedCount} shape(s)" : "[creator] nothing to undo"));
        }

        if (pressed(arg: GamepadButtons.ButtonWest)) {
            m_narrate((m_scene.Redo() ? $"[creator] redo — {m_scene.PlacedCount} shape(s)" : "[creator] nothing to redo"));
        }
    }

    // SELECT ([LT]): bumpers cycle the selection (PAST shapes into chain goals — the TARGET model's rig extension),
    // South duplicates, East deletes, West links two selections into a group; d-pad stays the global default.
    private void AdvanceSelectPage(Func<GamepadButtons, bool> pressed) {
        if (pressed(arg: GamepadButtons.RightShoulder)) {
            m_scene.CycleSelection(direction: 1);
            NarrateSelection();
        }

        if (pressed(arg: GamepadButtons.LeftShoulder)) {
            m_scene.CycleSelection(direction: -1);
            NarrateSelection();
        }

        if (pressed(arg: GamepadButtons.ButtonSouth) && m_scene.DuplicateTarget()) {
            m_narrate($"[creator] duplicated → {DescribeTarget()}");
        }

        if (pressed(arg: GamepadButtons.ButtonEast) && m_scene.DeleteSelected()) {
            m_narrate($"[creator] deleted — {m_scene.PlacedCount} shape(s) remain");
        }

        if (pressed(arg: GamepadButtons.ButtonWest)) {
            m_narrate(((m_scene.LinkWithPrevious() is { } group)
                ? $"[creator] linked into group {group} — blends now act within it (STYLE page sets the op)"
                : "[creator] link needs two shapes: select one, select another, then link"));
        }
    }

    // STYLE ([RT]): bumpers cycle material, South/East cycle the blend op, West toggles mirror, d-pad UP toggles
    // the bake style (a discrete press — North stays the global exit, so this is the one free face-equivalent slot
    // past the two continuous sweeps below), d-pad LEFT/RIGHT sweeps twist (bidirectional, held), d-pad DOWN sweeps
    // onion UP from 0 (held; onion has no natural "downward" gesture on a 4-way pad — creator.onion on the console
    // sets any exact value, including back to 0). Every one of the page's 9 non-global slots (2 bumpers + South/
    // East/West + all 4 d-pad directions) does exactly one job — nothing doubles up, nothing collides with the
    // GLOBAL right-stick-click camera toggle (checked unconditionally before the page switch in Advance, so a
    // page-local reuse of it would silently dead-code the page's own binding — learned the hard way while designing
    // this page, kept here as the reasoning for NOT putting bake style there).
    private void AdvanceStylePage(Func<GamepadButtons, bool> pressed, Func<GamepadButtons, bool> held, float deltaSeconds) {
        var twistDelta = ((held(arg: GamepadButtons.DpadRight) ? 1f : 0f) - (held(arg: GamepadButtons.DpadLeft) ? 1f : 0f));
        var onionDelta = (held(arg: GamepadButtons.DpadDown) ? 1f : 0f);

        m_scene.AdjustTwist(delta: twistDelta, deltaSeconds: deltaSeconds);
        m_scene.AdjustOnion(delta: onionDelta, deltaSeconds: deltaSeconds);

        if (pressed(arg: GamepadButtons.RightShoulder)) {
            m_narrate($"[creator] material: palette slot {m_scene.CycleMaterial(direction: 1)}");
        }

        if (pressed(arg: GamepadButtons.LeftShoulder)) {
            m_narrate($"[creator] material: palette slot {m_scene.CycleMaterial(direction: -1)}");
        }

        if (pressed(arg: GamepadButtons.ButtonSouth)) {
            NarrateBlend(blend: m_scene.CycleBlend(direction: 1));
        }

        if (pressed(arg: GamepadButtons.ButtonEast)) {
            NarrateBlend(blend: m_scene.CycleBlend(direction: -1));
        }

        if (pressed(arg: GamepadButtons.ButtonWest)) {
            m_narrate($"[creator] mirror: {(m_scene.ToggleMirror() ? "on" : "off")}");
        }

        if (pressed(arg: GamepadButtons.DpadUp)) {
            m_narrate($"[creator] bake style: {m_scene.ToggleBakeStyle()}");
        }
    }

    // ANIMATE ([LT,RT], LT pressed first): bumpers step frames, South records, East deletes, West plays/stops,
    // North (shared exit slot) ALSO returns to rest first (see the narration — exit still fires this same press,
    // which is the correct behavior: leaving the timeline mid-playback should stop it too). d-pad stays the global
    // default (posing still needs to move the target).
    private void AdvanceAnimatePage(Func<GamepadButtons, bool> pressed) {
        if (pressed(arg: GamepadButtons.RightShoulder)) {
            NarrateFrame(cursor: m_scene.StepFrame(direction: 1));
        }

        if (pressed(arg: GamepadButtons.LeftShoulder)) {
            NarrateFrame(cursor: m_scene.StepFrame(direction: -1));
        }

        if (pressed(arg: GamepadButtons.ButtonSouth)) {
            m_narrate($"[creator] recorded frame {m_scene.RecordFrame()} of {m_scene.FrameCount}");
        }

        if (pressed(arg: GamepadButtons.ButtonEast) && m_scene.DeleteCurrentFrame()) {
            m_narrate($"[creator] frame deleted — {m_scene.FrameCount} remain");
        }

        if (pressed(arg: GamepadButtons.ButtonWest)) {
            m_narrate((m_scene.TogglePlayback()
                ? "[creator] playing — West stops, North returns to rest and exits"
                : ((m_scene.FrameCount == 0) ? "[creator] nothing to play — pose the target, then South records a frame" : "[creator] stopped")));
        }
    }

    // RIG ([RT,LT], RT pressed first): bumpers cycle the CURSOR chain (which chain the pole-nudge/kind/delete
    // verbs act on), South defines a new limb chain from the current selection (root + next 2 shapes), East deletes
    // the cursor chain, West toggles its kind, North (shared exit slot) also stops any running gait sweep would —
    // gait itself is console-only (creator.gait) since sweeping needs a name PREFIX, awkward to spell on a pad. The
    // d-pad, RIG-ONLY, nudges the CURSOR chain's pole (planar, like Move) rather than raising/rolling the target —
    // the pole is what needs hands-on tuning here; the target itself (including a GOAL target) still moves via the
    // GLOBAL sticks.
    private void AdvanceRigPage(Func<GamepadButtons, bool> pressed, Func<GamepadButtons, bool> held, float deltaSeconds) {
        var poleMove = new Vector2(
            x: ((held(arg: GamepadButtons.DpadRight) ? 1f : 0f) - (held(arg: GamepadButtons.DpadLeft) ? 1f : 0f)),
            y: ((held(arg: GamepadButtons.DpadUp) ? 1f : 0f) - (held(arg: GamepadButtons.DpadDown) ? 1f : 0f))
        );

        m_scene.NudgePole(planar: poleMove, deltaSeconds: deltaSeconds);

        if (pressed(arg: GamepadButtons.RightShoulder)) {
            NarrateChain(chain: m_scene.CycleChainCursor(direction: 1));
        }

        if (pressed(arg: GamepadButtons.LeftShoulder)) {
            NarrateChain(chain: m_scene.CycleChainCursor(direction: -1));
        }

        if (pressed(arg: GamepadButtons.ButtonSouth)) {
            m_narrate(((m_scene.DefineChainFromSelection() is { } defined)
                ? $"[creator] chain defined: {DescribeChain(chain: defined)} — SELECT page's bumpers now cycle into its goal"
                : "[creator] select a shape with 2 more placed after it (document order), then South defines a limb"));
        }

        if (pressed(arg: GamepadButtons.ButtonEast)) {
            m_narrate((m_scene.DeleteCurrentChain() ? "[creator] chain deleted" : "[creator] no chain cursored — bumpers cycle to one"));
        }

        if (pressed(arg: GamepadButtons.ButtonWest)) {
            m_narrate(((m_scene.ToggleCurrentChainKind() is { } kind) ? $"[creator] chain kind: {kind}" : "[creator] no chain cursored — bumpers cycle to one"));
        }
    }
    private void NarrateFrame(int cursor) {
        m_narrate(((cursor == 0)
            ? "[creator] frame: rest (the live pose)"
            : $"[creator] frame: {cursor} of {m_scene.FrameCount} — pose the target, South re-records"));
    }
    private void NarrateSelection() {
        m_narrate((m_scene.TargetIsGoal
            ? $"[creator] goal selected: {DescribeChain(chain: m_scene.TargetGoalChain!)} — move it to pose the chain live"
            : ((m_scene.SelectedShape is { } shape)
                ? $"[creator] selected {DescribeShape(shape: shape)}"
                : "[creator] selection cleared — the ghost is the target")));
    }
    private void NarrateChain(CreatorChainState? chain) {
        m_narrate(((chain is { } found)
            ? $"[creator] chain cursor: {DescribeChain(chain: found)}"
            : "[creator] chain cursor: none"));
    }
    private void NarrateBlend(SdfBlendOp blend) {
        var grouped = (m_scene.SelectedShape is { GroupId: not 0 });

        m_narrate($"[creator] blend: {blend}{(grouped ? "" : (m_scene.TargetIsGhost ? " (inherited by the next place)" : " (auto-grouped)"))}");
    }
    private string DescribeTarget() =>
        ((m_scene.SelectedShape is { } shape) ? DescribeShape(shape: shape) : "the ghost");
    private static string DescribeShape(CreatorShapeState shape) =>
        $"#{shape.Id} {(shape.Name ?? shape.Type.ToString())}{((shape.GroupId != 0) ? $" (group {shape.GroupId})" : "")}";
    private static string DescribeChain(CreatorChainState chain) =>
        $"#{chain.Id} {(chain.Name ?? chain.Kind)}";
    private static string PageName(CreatorPage page) {
        return page switch {
            CreatorPage.Select => "SELECT [LT] — bumpers cycle shapes then goals, South duplicates, East deletes, West links",
            CreatorPage.Style => "STYLE [RT] — bumpers cycle material, South/East cycle blend, West toggles mirror, d-pad up flips bake style, left/right sweeps twist, down sweeps onion",
            CreatorPage.Animate => "ANIMATE [LT,RT] — bumpers step frames (0 = rest), South records, East deletes, West plays/stops",
            CreatorPage.Rig => "RIG [RT,LT] — bumpers cycle chains, South defines one from the selection, East deletes, West toggles kind, d-pad nudges the pole",
            _ => "SCULPT (bare) — bumpers cycle the primitive, South places, East undoes, West redoes",
        };
    }
}
