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

    /// <summary>The editor resting page id (empty chord: free-fly sticks, verticals, exit, status, speed).</summary>
    public const string RestingPageId = "editor";
    /// <summary>The camera page id (chord: LT held).</summary>
    public const string CameraPageId = "editor-camera";
    /// <summary>The selection page id (chord: RT held): pick, cycle, deselect, delete, grab.</summary>
    public const string SelectPageId = "editor-select";
    /// <summary>The placement page id (chord: LT then RT held): the grab/drag verb set, spawn ghosts, snap.</summary>
    public const string PlacePageId = "editor-place";

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
            // The LT+RT place page: the drag verb set (grab/commit toggle, cancel, snap) and the two spawn ghosts.
            // While a drag is live the sticks translate the pending row instead of flying (the session's routing).
            new BindingChordDefinition(
                Group: GroupId,
                Chord: [WorldDefaultBindings.LeftTriggerModifierId, WorldDefaultBindings.RightTriggerModifierId],
                Page: new BindingPageDefinition(
                    Id: PlacePageId,
                    Entries: [
                        .. StickEntries(),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSelectionCommandModule.GrabCommand, label: "Grab", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSelectionCommandModule.CancelCommand, label: "Cancel", icon: "edit.deselect"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSelectionCommandModule.SnapCommand, label: "Snap", icon: "edit.style"),
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorSelectionCommandModule.SpawnBoulderCommand, label: "Boulder", icon: "edit.duplicate"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorSelectionCommandModule.SpawnSlabCommand, label: "Slab", icon: "edit.duplicate"),
                    ],
                    Label: "Place"
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
