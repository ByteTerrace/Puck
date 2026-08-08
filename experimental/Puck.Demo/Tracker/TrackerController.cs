using Puck.Demo.Forge;
using Puck.Input.Devices;

namespace Puck.Demo.Tracker;

/// <summary>
/// The tracker's pad state machine — mirrors <see cref="Creator.CreatorController"/>'s shape for the SDF editor
/// (constructor-injected narrate callback, edge-tracked raw pad state, a one-shot latched exit request), but flatter:
/// tracker mode has one verb page (no camera, no sub-pages to cycle — the console assist verbs cover everything the
/// pad doesn't). Like creator mode, this deliberately BYPASSES the binding-page system and tracks its own edges
/// against the raw pad state the node samples for the creating slot (slot 0). Presentation/authoring only — nothing
/// here reaches the deterministic world.
///
/// BINDINGS: d-pad up/down moves the row cursor; d-pad left/right switches pattern; the bumpers nudge the cursor
/// row's note by a semitone (right = up, left = down); the stick clicks nudge by an octave (right = up, left =
/// down); South toggles hold/off at the cursor row; East plays/stops the preview; West saves; North exits; the
/// triggers (edge-thresholded) nudge the tempo (right = slower/more frames-per-row, left = faster).
/// </summary>
internal sealed class TrackerController {
    // A trigger reads 0..1; treat it as a discrete press once it crosses this much of its travel (mirrors how a
    // digital button's edge is tracked, just against an analog axis).
    private const float TriggerPressThreshold = 0.5f;

    private readonly TrackerScene m_scene;
    private readonly Action<string> m_narrate;
    private readonly Func<bool, string> m_setPreviewPlaying;
    private GamepadButtons m_prevButtons;
    private bool m_prevLeftTrigger;
    private bool m_prevRightTrigger;
    private bool m_exitRequested;

    /// <summary>Initializes the controller over the scene it edits.</summary>
    /// <param name="scene">The authored scene the verbs mutate.</param>
    /// <param name="narrate">Writes the pattern dump (or a status line) to the player's console after any edit.</param>
    /// <param name="setPreviewPlaying">Starts (<see langword="true"/>) or stops (<see langword="false"/>) the ACTUAL
    /// preview player (not just the scene's flag) — East routes here so the pad's play/stop has the same effect as
    /// the <c>tracker.play</c>/<c>tracker.stop</c> console verbs. Returns a status line.</param>
    public TrackerController(TrackerScene scene, Action<string> narrate, Func<bool, string> setPreviewPlaying) {
        ArgumentNullException.ThrowIfNull(narrate);
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(setPreviewPlaying);

        m_narrate = narrate;
        m_scene = scene;
        m_setPreviewPlaying = setPreviewPlaying;
    }

    /// <summary>The scene this controller edits.</summary>
    public TrackerScene Scene => m_scene;

    /// <summary>Clears the edge tracking and any pending exit — call when the mode toggles so a held button never
    /// fires a stale edge into the other mode.</summary>
    public void Reset() {
        m_exitRequested = false;
        m_prevButtons = GamepadButtons.None;
        m_prevLeftTrigger = false;
        m_prevRightTrigger = false;
    }

    /// <summary>Returns whether the EXIT verb fired since the last consume (and clears it) — the node leaves the
    /// mode when this reports true.</summary>
    public bool ConsumeExitRequest() {
        var requested = m_exitRequested;

        m_exitRequested = false;

        return requested;
    }

    /// <summary>Advances one frame of tracker input (see the class remarks for the binding map). Presentation only.</summary>
    /// <param name="raw">The creating slot's raw pad state this frame.</param>
    public void Advance(in GamepadState raw) {
        var buttons = raw.Buttons;

        bool Pressed(GamepadButtons button) => ((0 != (buttons & button)) && (0 == (m_prevButtons & button)));

        var leftTrigger = (raw.LeftTrigger >= TriggerPressThreshold);
        var rightTrigger = (raw.RightTrigger >= TriggerPressThreshold);
        var leftTriggerPressed = (leftTrigger && !m_prevLeftTrigger);
        var rightTriggerPressed = (rightTrigger && !m_prevRightTrigger);
        var dirty = false;

        if (Pressed(button: GamepadButtons.DpadUp)) { m_scene.MoveRow(direction: -1); dirty = true; }
        if (Pressed(button: GamepadButtons.DpadDown)) { m_scene.MoveRow(direction: 1); dirty = true; }

        if (Pressed(button: GamepadButtons.DpadLeft)) { m_scene.MovePattern(direction: -1); dirty = true; }
        if (Pressed(button: GamepadButtons.DpadRight)) { m_scene.MovePattern(direction: 1); dirty = true; }

        if (Pressed(button: GamepadButtons.LeftShoulder)) { m_scene.NudgeNote(direction: -1); dirty = true; }
        if (Pressed(button: GamepadButtons.RightShoulder)) { m_scene.NudgeNote(direction: 1); dirty = true; }

        if (Pressed(button: GamepadButtons.LeftStickPress)) { m_scene.NudgeOctave(direction: -1); dirty = true; }
        if (Pressed(button: GamepadButtons.RightStickPress)) { m_scene.NudgeOctave(direction: 1); dirty = true; }

        if (Pressed(button: GamepadButtons.ButtonSouth)) { m_scene.ToggleHoldOff(); dirty = true; }

        if (Pressed(button: GamepadButtons.ButtonEast)) {
            m_narrate(m_setPreviewPlaying(!m_scene.Playing));
        }

        if (Pressed(button: GamepadButtons.ButtonWest)) {
            var path = AudioDocumentStore.SaveNamed(document: m_scene.Document, name: m_scene.Document.Name!);

            m_narrate($"[tracker] saved → {path}");
        }

        if (Pressed(button: GamepadButtons.ButtonNorth)) {
            m_exitRequested = true;
        }

        if (leftTriggerPressed) { m_scene.NudgeTempo(direction: -1); dirty = true; }
        if (rightTriggerPressed) { m_scene.NudgeTempo(direction: 1); dirty = true; }

        if (dirty) {
            NarrateRows();
        }

        m_prevButtons = buttons;
        m_prevLeftTrigger = leftTrigger;
        m_prevRightTrigger = rightTrigger;
    }

    /// <summary>Prints the current pattern dump to the console — called after any cursor move or row edit (from
    /// <see cref="Advance"/>), and by the node right after entering the mode so the first frame shows something.</summary>
    public void NarrateRows() {
        m_narrate(string.Join(separator: '\n', values: m_scene.RenderRows()));
    }
}
