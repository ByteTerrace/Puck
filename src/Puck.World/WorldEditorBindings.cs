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
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorCommandModule.CameraToggleCommand, label: "Camera", icon: "edit.op"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorCommandModule.ExitCommand, label: "Exit", icon: "edit.exit"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorCommandModule.StatusCommand, label: "Status", icon: "action.target"),
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorCommandModule.FasterCommand, label: "Faster", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorCommandModule.SlowerCommand, label: "Slower", icon: "edit.prev"),
                        // The same controls that entered the mode leave it (Back/Tab mirror the play page's enter twins).
                        Press(source: InputSources.Gamepad.Back, command: EditorCommandModule.ExitCommand, label: "Exit", icon: "edit.exit"),
                        Press(source: InputSources.Keyboard.Tab, command: EditorCommandModule.ExitCommand, label: "Exit", icon: "edit.exit"),
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
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorCommandModule.FlyCommand, label: "Fly", icon: "edit.play"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorCommandModule.OrbitCommand, label: "Orbit", icon: "action.target"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSelectionCommandModule.PickCommand, label: "Focus", icon: "action.target"),
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorCommandModule.FasterCommand, label: "Faster", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorCommandModule.SlowerCommand, label: "Slower", icon: "edit.prev"),
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
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSelectionCommandModule.DeselectCommand, label: "Clear", icon: "edit.deselect"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSelectionCommandModule.DeleteCommand, label: "Delete", icon: "edit.delete"),
                        Press(source: InputSources.Gamepad.DpadRight, command: EditorSelectionCommandModule.NextCommand, label: "Next", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadLeft, command: EditorSelectionCommandModule.PrevCommand, label: "Prev", icon: "edit.prev"),
                    ],
                    Label: "Select"
                )
            ),
            // The LT+RT place page: the drag verb set (grab/commit toggle, cancel, snap), the two scene spawn ghosts,
            // and place-by-name — D-pad Left/Right cycle the armed world creation, North ghosts a placement of it.
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
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorSelectionCommandModule.SpawnBoulderCommand, label: "Boulder", icon: "edit.duplicate"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorSelectionCommandModule.SpawnSlabCommand, label: "Slab", icon: "edit.duplicate"),
                        Press(source: InputSources.Gamepad.DpadRight, command: EditorCreationCommandModule.NextCommand, label: "Creation+", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadLeft, command: EditorCreationCommandModule.PrevCommand, label: "Creation-", icon: "edit.prev"),
                    ],
                    Label: "Place"
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
                        Press(source: InputSources.Gamepad.DpadRight, command: EditorSculptShapeCommandModule.NextCommand, label: "Next", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadLeft, command: EditorSculptShapeCommandModule.PrevCommand, label: "Prev", icon: "edit.prev"),
                        Press(source: InputSources.Gamepad.Back, command: EditorSculptCommandModule.ExitCommand, label: "Done", icon: "edit.exit"),
                        Press(source: InputSources.Keyboard.Tab, command: EditorSculptCommandModule.ExitCommand, label: "Done", icon: "edit.exit"),
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
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorSculptCommandModule.ZoomInCommand, label: "Zoom+", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorSculptCommandModule.ZoomOutCommand, label: "Zoom-", icon: "edit.prev"),
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
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSculptStyleCommandModule.MaterialNextCommand, label: "Color+", icon: "edit.material"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSculptStyleCommandModule.MaterialPrevCommand, label: "Color-", icon: "edit.material"),
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorSculptStyleCommandModule.SmoothUpCommand, label: "Smooth+", icon: "edit.op"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorSculptStyleCommandModule.SmoothDownCommand, label: "Smooth-", icon: "edit.op"),
                        Press(source: InputSources.Gamepad.DpadRight, command: EditorSculptShapeCommandModule.GrowCommand, label: "Grow", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadLeft, command: EditorSculptShapeCommandModule.ShrinkCommand, label: "Shrink", icon: "edit.prev"),
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
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSculptRigCommandModule.FrameNextCommand, label: "Frame+", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSculptRigCommandModule.FramePrevCommand, label: "Frame-", icon: "edit.prev"),
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
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSculptRigCommandModule.ChainDefineCommand, label: "Chain", icon: "edit.link"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSculptRigCommandModule.ChainKindCommand, label: "Kind", icon: "edit.style"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSculptRigCommandModule.ChainRemoveCommand, label: "Del chain", icon: "edit.delete"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSculptRigCommandModule.ChainNextCommand, label: "Chain+", icon: "edit.next"),
                    ],
                    Label: "Rig"
                )
            ),
        ];
    }

    // The two stick routers every editor page carries: a held analog re-dispatches each tick against the ACTIVE page,
    // so a page missing these entries would stall fresh flight input while its chord is held.
    private static BindingPageEntryDefinition[] StickEntries() => [
        new BindingPageEntryDefinition(Source: InputSources.Gamepad.LeftStick, Command: EditorCommandModule.MoveCommand, Label: "Fly"),
        new BindingPageEntryDefinition(Source: InputSources.Gamepad.RightStick, Command: EditorCommandModule.LookCommand, Label: "Look"),
    ];

    // A press-edge entry, AnyModifiers so an incidental keyboard modifier never eats an editor act.
    private static BindingPageEntryDefinition Press(string source, string command, string label, string icon) => new(
        Source: source,
        Command: command,
        ActivateOn: CommandPhase.Started,
        Label: label,
        Icon: icon,
        AnyModifiers: true
    );

    // A source bound on BOTH edges (the WorldDefaultBindings HoldRelease pattern) so a held vertical reads held until
    // its release edge.
    private static BindingPageEntryDefinition[] HoldRelease(string source, string command, string label, string icon) => [
        new BindingPageEntryDefinition(Source: source, Command: command, Label: label, Icon: icon, AnyModifiers: true),
        new BindingPageEntryDefinition(Source: source, Command: command, ActivateOn: CommandPhase.Completed, AnyModifiers: true),
    ];
}
