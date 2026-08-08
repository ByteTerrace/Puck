using Puck.Commands;
using Puck.Input;

namespace Puck.World;

/// <summary>
/// The <see cref="GroupId"/> page group — code-authored chord rows speaking the <c>editor.*</c> command vocabulary,
/// folded into the engine-default document by <see cref="WorldDefaultBindings.BuildDocument"/> so every seat's
/// compiled profile always carries them. Entering the editor is <see cref="WorldSeatBindings.SetActiveGroup"/> with
/// this group — a pointer flip on the already-compiled profile, no recompose — and the binding bar renders whatever
/// page the group's chords select, so entering the mode lights the editor pages with zero bar-side work. Ordered
/// LT/RT trigger chords (the <see cref="WorldDefaultBindings.LeftTriggerModifierId"/>/<c>rt</c> modifiers, with
/// their hysteresis) select the pages; sticks are re-bound on EVERY editor page, so flight continues while a
/// trigger chord is held (and a live drag re-routes those same latched samples onto the pending row).
/// </summary>
internal static class WorldEditorBindings {
    /// <summary>The editor page group — a seat's active group while it edits.</summary>
    public const string GroupId = "editor";

    /// <summary>The sculpt page group — a seat's active group while its workbench is open (a mode WITHIN editor
    /// mode: <c>editor.sculpt.new</c>/<c>edit</c> flip onto it, <c>editor.sculpt.exit</c> flips back — modes are
    /// page groups; the editor group's five ordered trigger chords are all spoken for, and the
    /// sculpt feature set is a page FAMILY of its own).</summary>
    public const string SculptGroupId = "sculpt";

    /// <summary>The editor resting page id (empty chord: free-fly sticks, verticals, exit, status, speed).</summary>
    public const string RestingPageId = "editor";
    /// <summary>The camera page id (chord: LT held).</summary>
    public const string CameraPageId = "editor-camera";
    /// <summary>The selection page id (chord: RT held): pick, cycle, deselect, delete, grab.</summary>
    public const string SelectPageId = "editor-select";
    /// <summary>The placement page id (chord: LT then RT held): the grab/drag verb set, spawn ghosts, snap.</summary>
    public const string PlacePageId = "editor-place";
    /// <summary>The editor's fifth page id (chord: RT then LT held — the reverse squeeze). Sparse: the page exists
    /// so the group's five ordered chords all resolve; its content is authored through the binding document.</summary>
    public const string ReversePageId = "editor-reverse";

    /// <summary>The sculpt resting page id (empty chord: build acts — add/primitive/undo/redo, target cycling,
    /// duplicate/delete, the shape verticals).</summary>
    public const string SculptRestingPageId = "sculpt";
    /// <summary>The sculpt bench page id (chord: LT held): commit, easel, deselect, zoom.</summary>
    public const string SculptBenchPageId = "sculpt-bench";
    /// <summary>The sculpt style page id (chord: RT held): blend, mirror, material, smooth, scale steps.</summary>
    public const string SculptStylePageId = "sculpt-style";
    /// <summary>The sculpt frames page id (chord: LT then RT held): record/play/step/delete the timeline.</summary>
    public const string SculptFramesPageId = "sculpt-frames";
    /// <summary>The sculpt rig page id (chord: RT then LT held — the reverse squeeze): chain define/kind/cycle/delete.</summary>
    public const string SculptRigPageId = "sculpt-rig";

    /// <summary>The display label the editor resting page carries — the binding bar's (and <c>editor.status</c>'s)
    /// visible evidence the editor group is live.</summary>
    public const string RestingPageLabel = "Editor";

    /// <summary>The editor group's wheel hold page id (chord: Tab held) — its selection presents the editor wheel,
    /// whose Exit sector is the keyboard's way out of the mode (Tab belongs wholly to the wheel).</summary>
    public const string WheelHoldPageId = "editor-wheel";
    /// <summary>The editor wheel's sole ring page id.</summary>
    public const string WheelRingId = "editor-wheel-acts";

    /// <summary>The sculpt group's wheel hold page id (chord: Tab held) — its Done sector closes the workbench.</summary>
    public const string SculptWheelHoldPageId = "sculpt-wheel";
    /// <summary>The sculpt wheel's sole ring page id.</summary>
    public const string SculptWheelRingId = "sculpt-wheel-acts";

    /// <summary>Builds the editor group's chord rows (the <see cref="WorldDefaultBindings.BuildDocument"/> fold).</summary>
    /// <returns>The rows, resting page first.</returns>
    public static BindingChordDefinition[] Rows() {
        return [
            // The editor resting page: free-fly sticks, shoulder verticals, camera toggle, exit, status, speed steps.
            new BindingChordDefinition(
                Group: GroupId,
                Chord: [],
                Page: new BindingPageDefinition(
                    Id: RestingPageId,
                    Entries: [
                        .. StickEntries(),
                        .. HoldRelease(source: InputSources.Gamepad.RightShoulder, command: EditorCommandModule.AscendCommand, label: "Rise", icon: "action.jump"),
                        .. HoldRelease(source: InputSources.Gamepad.LeftShoulder, command: EditorCommandModule.DescendCommand, label: "Sink", icon: "edit.place"),
                        // A constant zero (not a plain Press) — editor.camera now declares Axis1D (every OTHER row
                        // targeting it carries a real +1/-1 constant), so this row must dispatch the SAME kind too,
                        // or BindingVocabularyCheck refuses every future recompose over the mismatch. Zero reads as
                        // "no explicit direction" in the handler, which falls through to the toggle.
                        PressValue(source: InputSources.Gamepad.ButtonSouth, command: EditorCommandModule.CameraToggleCommand, value: CommandValue.Axis(value: 0f), label: "Camera", icon: "edit.op"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorCommandModule.ExitCommand, label: "Exit", icon: "edit.exit"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorCommandModule.StatusCommand, label: "Status", icon: "action.target"),
                        PressValue(source: InputSources.Gamepad.DpadUp, command: EditorCommandModule.SpeedCommand, value: CommandValue.Axis(value: 1f), label: "Faster", icon: "edit.next"),
                        PressValue(source: InputSources.Gamepad.DpadDown, command: EditorCommandModule.SpeedCommand, value: CommandValue.Axis(value: -1f), label: "Slower", icon: "edit.prev"),
                        // The same control that entered the mode leaves it (Back mirrors the play page's enter row);
                        // the keyboard leaves through the wheel's Exit sector — Tab belongs wholly to the wheel.
                        Press(source: InputSources.Gamepad.Back, command: EditorCommandModule.ExitCommand, label: "Exit", icon: "edit.exit"),
                    ],
                    Label: RestingPageLabel
                )
            ),
            // The LT camera page: explicit fly/orbit selection plus the shared speed steps; North is the
            // focus-selection (pick under the crosshair, so orbit has a pivot the moment you aim at something).
            // Sticks stay bound so flight continues under the held chord.
            new BindingChordDefinition(
                Group: GroupId,
                Chord: [WorldDefaultBindings.LeftTriggerModifierId],
                Page: new BindingPageDefinition(
                    Id: CameraPageId,
                    Entries: [
                        .. StickEntries(),
                        PressValue(source: InputSources.Gamepad.ButtonSouth, command: EditorCommandModule.CameraToggleCommand, value: CommandValue.Axis(value: 1f), label: "Fly", icon: "edit.play"),
                        PressValue(source: InputSources.Gamepad.ButtonWest, command: EditorCommandModule.CameraToggleCommand, value: CommandValue.Axis(value: -1f), label: "Orbit", icon: "action.target"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSelectionCommandModule.PickCommand, label: "Focus", icon: "action.target"),
                        PressValue(source: InputSources.Gamepad.DpadUp, command: EditorCommandModule.SpeedCommand, value: CommandValue.Axis(value: 1f), label: "Faster", icon: "edit.next"),
                        PressValue(source: InputSources.Gamepad.DpadDown, command: EditorCommandModule.SpeedCommand, value: CommandValue.Axis(value: -1f), label: "Slower", icon: "edit.prev"),
                    ],
                    Label: "Camera"
                )
            ),
            // The RT select page: the crosshair pick, the proximity cycle, deselect/delete, and the grab toggle
            // (grab here so pick→grab flows without releasing RT for the LT+RT place chord).
            new BindingChordDefinition(
                Group: GroupId,
                Chord: [WorldDefaultBindings.RightTriggerModifierId],
                Page: new BindingPageDefinition(
                    Id: SelectPageId,
                    Entries: [
                        .. StickEntries(),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSelectionCommandModule.PickCommand, label: "Pick", icon: "action.target"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSelectionCommandModule.GrabCommand, label: "Grab", icon: "edit.place"),
                        // A constant zero (not a plain Press) — editor.select now declares Axis1D (the DpadRight/Left
                        // rows below carry real +1/-1 constants), so this row must dispatch the SAME kind too, or
                        // BindingVocabularyCheck refuses every future recompose over the mismatch. Zero reads as
                        // "deselect" in the handler.
                        PressValue(source: InputSources.Gamepad.ButtonWest, command: EditorSelectionCommandModule.SelectCommand, value: CommandValue.Axis(value: 0f), label: "Clear", icon: "edit.deselect"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSelectionCommandModule.DeleteCommand, label: "Delete", icon: "edit.delete"),
                        PressValue(source: InputSources.Gamepad.DpadRight, command: EditorSelectionCommandModule.SelectCommand, value: CommandValue.Axis(value: 1f), label: "Next", icon: "edit.next"),
                        PressValue(source: InputSources.Gamepad.DpadLeft, command: EditorSelectionCommandModule.SelectCommand, value: CommandValue.Axis(value: -1f), label: "Prev", icon: "edit.prev"),
                    ],
                    Label: "Select"
                )
            ),
            // The LT+RT place page: the drag verb set (grab/commit toggle, cancel, snap), and place-by-name — D-pad
            // Left/Right cycle the armed world creation, North ghosts a placement of it.
            // While a drag is live the sticks translate the pending row instead of flying (the session's
            // routing).
            new BindingChordDefinition(
                Group: GroupId,
                Chord: [WorldDefaultBindings.LeftTriggerModifierId, WorldDefaultBindings.RightTriggerModifierId],
                Page: new BindingPageDefinition(
                    Id: PlacePageId,
                    Entries: [
                        .. StickEntries(),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSelectionCommandModule.GrabCommand, label: "Grab", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorCreationCommandModule.SpawnCommand, label: "Stamp", icon: "edit.duplicate"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSelectionCommandModule.CancelCommand, label: "Cancel", icon: "edit.deselect"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSelectionCommandModule.SnapCommand, label: "Snap", icon: "edit.style"),
                        Press(source: InputSources.Gamepad.DpadRight, command: EditorCreationCommandModule.NextCommand, label: "Creation+", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadLeft, command: EditorCreationCommandModule.PrevCommand, label: "Creation-", icon: "edit.prev"),
                    ],
                    Label: "Place"
                )
            ),
            // The RT+LT reverse page: sparse — the fifth of the group's five ordered trigger chords. Sticks only,
            // so flight continues while the reverse squeeze is held.
            new BindingChordDefinition(
                Group: GroupId,
                Chord: [WorldDefaultBindings.RightTriggerModifierId, WorldDefaultBindings.LeftTriggerModifierId],
                Page: new BindingPageDefinition(
                    Id: ReversePageId,
                    Entries: [.. StickEntries()],
                    Label: "RT+LT"
                )
            ),
            // The editor wheel hold page — Tab held; sticks keep flying, the arrows/D-pad cycle the ring, and Tab's
            // release commits (see WorldDefaultBindings.WheelHoldEntries).
            new BindingChordDefinition(
                Group: GroupId,
                Chord: [WorldDefaultBindings.TabModifierId],
                Page: new BindingPageDefinition(
                    Id: WheelHoldPageId,
                    Entries: [
                        .. WheelStickEntries(),
                        .. WorldDefaultBindings.WheelHoldEntries(openerSource: InputSources.Keyboard.Tab),
                    ],
                    Label: "Wheel"
                )
            ),
            // ---- the sculpt group: the workbench mode's page family. Sticks stay bound on EVERY page (the
            // session routes move onto the sculpt target and look onto the orbit while a bench is open), and the
            // shoulder verticals ride along so raise/lower never stalls under a held chord.
            // The sculpt resting page: build acts.
            new BindingChordDefinition(
                Group: SculptGroupId,
                Chord: [],
                Page: new BindingPageDefinition(
                    Id: SculptRestingPageId,
                    Entries: [
                        .. StickEntries(),
                        .. HoldRelease(source: InputSources.Gamepad.RightShoulder, command: EditorCommandModule.AscendCommand, label: "Raise", icon: "action.jump"),
                        .. HoldRelease(source: InputSources.Gamepad.LeftShoulder, command: EditorCommandModule.DescendCommand, label: "Lower", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSculptShapeCommandModule.AddCommand, label: "Add", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSculptShapeCommandModule.PrimitiveCommand, label: "Shape", icon: "edit.duplicate"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSculptCommandModule.UndoCommand, label: "Undo", icon: "edit.undo"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSculptCommandModule.RedoCommand, label: "Redo", icon: "edit.redo"),
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorSculptShapeCommandModule.DuplicateCommand, label: "Twin", icon: "edit.duplicate"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorSculptShapeCommandModule.RemoveCommand, label: "Delete", icon: "edit.delete"),
                        PressValue(source: InputSources.Gamepad.DpadRight, command: EditorSculptShapeCommandModule.SelectCommand, value: CommandValue.Axis(value: 1f), label: "Next", icon: "edit.next"),
                        PressValue(source: InputSources.Gamepad.DpadLeft, command: EditorSculptShapeCommandModule.SelectCommand, value: CommandValue.Axis(value: -1f), label: "Prev", icon: "edit.prev"),
                        Press(source: InputSources.Gamepad.Back, command: EditorSculptCommandModule.ExitCommand, label: "Done", icon: "edit.exit"),
                    ],
                    Label: "Sculpt"
                )
            ),
            // The LT bench page: the deliberate acts (commit, easel) plus deselect and the zoom steps.
            new BindingChordDefinition(
                Group: SculptGroupId,
                Chord: [WorldDefaultBindings.LeftTriggerModifierId],
                Page: new BindingPageDefinition(
                    Id: SculptBenchPageId,
                    Entries: [
                        .. StickEntries(),
                        .. HoldRelease(source: InputSources.Gamepad.RightShoulder, command: EditorCommandModule.AscendCommand, label: "Raise", icon: "action.jump"),
                        .. HoldRelease(source: InputSources.Gamepad.LeftShoulder, command: EditorCommandModule.DescendCommand, label: "Lower", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSculptCommandModule.CommitCommand, label: "Commit", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSculptCommandModule.EaselCommand, label: "Easel", icon: "edit.link"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSculptShapeCommandModule.DeselectCommand, label: "Clear", icon: "edit.deselect"),
                        PressValue(source: InputSources.Gamepad.DpadUp, command: EditorSculptCommandModule.ZoomCommand, value: CommandValue.Axis(value: 1f), label: "Zoom+", icon: "edit.next"),
                        PressValue(source: InputSources.Gamepad.DpadDown, command: EditorSculptCommandModule.ZoomCommand, value: CommandValue.Axis(value: -1f), label: "Zoom-", icon: "edit.prev"),
                    ],
                    Label: "Bench"
                )
            ),
            // The RT style page: blend/mirror/material plus the smooth and scale steps.
            new BindingChordDefinition(
                Group: SculptGroupId,
                Chord: [WorldDefaultBindings.RightTriggerModifierId],
                Page: new BindingPageDefinition(
                    Id: SculptStylePageId,
                    Entries: [
                        .. StickEntries(),
                        .. HoldRelease(source: InputSources.Gamepad.RightShoulder, command: EditorCommandModule.AscendCommand, label: "Raise", icon: "action.jump"),
                        .. HoldRelease(source: InputSources.Gamepad.LeftShoulder, command: EditorCommandModule.DescendCommand, label: "Lower", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSculptStyleCommandModule.BlendCommand, label: "Blend", icon: "edit.op"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSculptStyleCommandModule.MirrorCommand, label: "Mirror", icon: "edit.style"),
                        PressValue(source: InputSources.Gamepad.ButtonEast, command: EditorSculptStyleCommandModule.MaterialCommand, value: CommandValue.Axis(value: 1f), label: "Color+", icon: "edit.material"),
                        PressValue(source: InputSources.Gamepad.ButtonWest, command: EditorSculptStyleCommandModule.MaterialCommand, value: CommandValue.Axis(value: -1f), label: "Color-", icon: "edit.material"),
                        PressValue(source: InputSources.Gamepad.DpadUp, command: EditorSculptStyleCommandModule.SmoothCommand, value: CommandValue.Axis(value: 1f), label: "Smooth+", icon: "edit.op"),
                        PressValue(source: InputSources.Gamepad.DpadDown, command: EditorSculptStyleCommandModule.SmoothCommand, value: CommandValue.Axis(value: -1f), label: "Smooth-", icon: "edit.op"),
                        PressValue(source: InputSources.Gamepad.DpadRight, command: EditorSculptShapeCommandModule.ScaleCommand, value: CommandValue.Axis(value: 1f), label: "Grow", icon: "edit.next"),
                        PressValue(source: InputSources.Gamepad.DpadLeft, command: EditorSculptShapeCommandModule.ScaleCommand, value: CommandValue.Axis(value: -1f), label: "Shrink", icon: "edit.prev"),
                    ],
                    Label: "Style"
                )
            ),
            // The LT+RT frames page: the timeline (record/play/step/delete).
            new BindingChordDefinition(
                Group: SculptGroupId,
                Chord: [WorldDefaultBindings.LeftTriggerModifierId, WorldDefaultBindings.RightTriggerModifierId],
                Page: new BindingPageDefinition(
                    Id: SculptFramesPageId,
                    Entries: [
                        .. StickEntries(),
                        .. HoldRelease(source: InputSources.Gamepad.RightShoulder, command: EditorCommandModule.AscendCommand, label: "Raise", icon: "action.jump"),
                        .. HoldRelease(source: InputSources.Gamepad.LeftShoulder, command: EditorCommandModule.DescendCommand, label: "Lower", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSculptRigCommandModule.FrameRecordCommand, label: "Record", icon: "edit.record"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSculptRigCommandModule.PlayCommand, label: "Play", icon: "edit.play"),
                        PressValue(source: InputSources.Gamepad.ButtonEast, command: EditorSculptRigCommandModule.FrameCommand, value: CommandValue.Axis(value: 1f), label: "Frame+", icon: "edit.next"),
                        PressValue(source: InputSources.Gamepad.ButtonWest, command: EditorSculptRigCommandModule.FrameCommand, value: CommandValue.Axis(value: -1f), label: "Frame-", icon: "edit.prev"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorSculptRigCommandModule.FrameRemoveCommand, label: "Del frame", icon: "edit.delete"),
                    ],
                    Label: "Frames"
                )
            ),
            // The RT+LT rig page (the reverse squeeze): chains — define from selection, cycle, kind, delete. Goal
            // posing rides the resting page's target cycle (selection extends into chain goals) + the move stick.
            new BindingChordDefinition(
                Group: SculptGroupId,
                Chord: [WorldDefaultBindings.RightTriggerModifierId, WorldDefaultBindings.LeftTriggerModifierId],
                Page: new BindingPageDefinition(
                    Id: SculptRigPageId,
                    Entries: [
                        .. StickEntries(),
                        .. HoldRelease(source: InputSources.Gamepad.RightShoulder, command: EditorCommandModule.AscendCommand, label: "Raise", icon: "action.jump"),
                        .. HoldRelease(source: InputSources.Gamepad.LeftShoulder, command: EditorCommandModule.DescendCommand, label: "Lower", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSculptRigCommandModule.ChainCommand, label: "Chain", icon: "edit.link"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSculptRigCommandModule.ChainKindCommand, label: "Kind", icon: "edit.style"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSculptRigCommandModule.ChainRemoveCommand, label: "Del chain", icon: "edit.delete"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSculptRigCommandModule.ChainNextCommand, label: "Chain+", icon: "edit.next"),
                    ],
                    Label: "Rig"
                )
            ),
            // The sculpt wheel hold page — the editor hold page's sculpt-group twin.
            new BindingChordDefinition(
                Group: SculptGroupId,
                Chord: [WorldDefaultBindings.TabModifierId],
                Page: new BindingPageDefinition(
                    Id: SculptWheelHoldPageId,
                    Entries: [
                        .. WheelStickEntries(),
                        .. WorldDefaultBindings.WheelHoldEntries(openerSource: InputSources.Keyboard.Tab),
                    ],
                    Label: "Wheel"
                )
            ),
        ];
    }

    /// <summary>Builds the editor and sculpt groups' wheels (folded into the engine-default document's
    /// <c>wheels</c> beside <see cref="WorldDefaultBindings"/>' play wheel). One ring each: the mode's deliberate
    /// acts, its Exit/Done sector carrying the same act the retired Tab press row used to fire directly.</summary>
    /// <returns>The two wheels, editor first.</returns>
    public static BindingWheelDefinition[] Wheels() => [
        new BindingWheelDefinition(
            Id: "editor-primary",
            Group: GroupId,
            HoldPages: [WheelHoldPageId],
            Rings: [
                new BindingPageDefinition(
                    Id: WheelRingId,
                    Entries: [
                        WorldDefaultBindings.Sector(command: EditorCommandModule.ExitCommand, label: "Exit", icon: "edit.exit"),
                        WorldDefaultBindings.Sector(command: EditorCommandModule.StatusCommand, label: "Status", icon: "action.target"),
                        WorldDefaultBindings.Sector(command: EditorCommandModule.CameraToggleCommand, label: "Camera", icon: "edit.op", value: CommandValue.Axis(value: 0f)),
                        WorldDefaultBindings.Sector(command: EditorCreationCommandModule.NextCommand, label: "Creation+", icon: "edit.next"),
                        WorldDefaultBindings.Sector(command: EditorCreationCommandModule.PrevCommand, label: "Creation-", icon: "edit.prev"),
                        WorldDefaultBindings.Sector(command: EditorCreationCommandModule.SpawnCommand, label: "Stamp", icon: "edit.duplicate"),
                    ],
                    Label: "Editor"
                ),
            ]
        ),
        new BindingWheelDefinition(
            Id: "sculpt-primary",
            Group: SculptGroupId,
            HoldPages: [SculptWheelHoldPageId],
            Rings: [
                new BindingPageDefinition(
                    Id: SculptWheelRingId,
                    Entries: [
                        WorldDefaultBindings.Sector(command: EditorSculptCommandModule.ExitCommand, label: "Done", icon: "edit.exit"),
                        WorldDefaultBindings.Sector(command: EditorSculptCommandModule.CommitCommand, label: "Commit", icon: "edit.place"),
                        WorldDefaultBindings.Sector(command: EditorSculptCommandModule.UndoCommand, label: "Undo", icon: "edit.undo"),
                        WorldDefaultBindings.Sector(command: EditorSculptCommandModule.RedoCommand, label: "Redo", icon: "edit.redo"),
                        WorldDefaultBindings.Sector(command: EditorSculptCommandModule.EaselCommand, label: "Easel", icon: "edit.link"),
                        WorldDefaultBindings.Sector(command: EditorCommandModule.StatusCommand, label: "Status", icon: "action.target"),
                    ],
                    Label: "Sculpt"
                ),
            ]
        ),
    ];

    // The two stick routers every editor page carries: a held analog re-dispatches each tick against the ACTIVE page,
    // so a page missing these entries would stall fresh flight input while its chord is held.
    private static BindingPageEntryDefinition[] StickEntries() => [
        new BindingPageEntryDefinition(Source: InputSources.Gamepad.LeftStick, Command: EditorCommandModule.MoveCommand, Label: "Fly"),
        new BindingPageEntryDefinition(Source: InputSources.Gamepad.RightStick, Command: EditorCommandModule.LookCommand, Label: "Look"),
    ];

    // A radial is modal only with respect to the controls the author gives it: left stick keeps its ordinary move
    // binding while right stick is deliberately omitted here and authored by WheelHoldEntries as radial selection.
    private static BindingPageEntryDefinition[] WheelStickEntries() => [
        new BindingPageEntryDefinition(Source: InputSources.Gamepad.LeftStick, Command: EditorCommandModule.MoveCommand, Label: "Fly"),
    ];

    // A press-edge entry.
    private static BindingPageEntryDefinition Press(string source, string command, string label, string icon) => new(
        Source: source,
        Command: command,
        ActivateOn: CommandPhase.Started,
        Label: label,
        Icon: icon
    );

    // A press-edge entry carrying a CONSTANT value in place of the source's own — the step/direction-twin fold: a
    // WithWireArgs verb sees an EMPTY WireArgs on a bound dispatch and reads this constant off context.Value
    // instead (see EditorCommandModule.TryDirection's doctrine comment). This is how a .next/.prev/.up/.down/
    // .grow/.shrink twin retired in this wave keeps its chord without a sibling command: the surviving verb stays
    // Bindable, and the binding row that used to name the twin now names the verb with the twin's direction baked
    // into Value.
    private static BindingPageEntryDefinition PressValue(string source, string command, CommandValue value, string label, string icon) => new(
        Source: source,
        Command: command,
        Value: value,
        ActivateOn: CommandPhase.Started,
        Label: label,
        Icon: icon
    );

    // A source bound on BOTH edges (the WorldDefaultBindings HoldRelease pattern) so a held vertical reads held until
    // its release edge.
    private static BindingPageEntryDefinition[] HoldRelease(string source, string command, string label, string icon) => [
        new BindingPageEntryDefinition(Source: source, Command: command, Label: label, Icon: icon),
        new BindingPageEntryDefinition(Source: source, Command: command, ActivateOn: CommandPhase.Completed),
    ];
}
