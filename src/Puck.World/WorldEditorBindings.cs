using Puck.Commands;
using Puck.Input;

namespace Puck.World;

/// <summary>
/// The editor MODE layer's binding document — code-authored pages speaking the <c>editor.*</c> command vocabulary,
/// composed as the final layer of an editing seat's stack (engine default ⊕ world overlays ⊕ profile ⊕ session ⊕
/// MODE) by <see cref="WorldSeatBindings.SetModeLayer"/>. Ordered LT/RT trigger chords select the pages
/// (<see cref="BindingModifierDefinition"/> hysteresis keeps a resting trigger from flapping them); the binding bar
/// renders whatever page the chord selects, so entering the mode lights the editor pages with zero bar-side work.
/// </summary>
/// <remarks>
/// The no-modifier page reuses <see cref="WorldDefaultBindings.BasePageId"/> so the composer MERGES it into the
/// default base page (the compiled profile admits exactly one empty-chord page): the gamepad sources it names are
/// REPLACED with editor verbs, while the keyboard's <c>player.*</c> movement entries persist and are masked by the
/// seat's Idle intent source for the mode's duration. Sticks are re-bound on EVERY editor page, so flight continues
/// while a trigger chord is held (and a live drag re-routes those same latched samples onto the pending row).
/// </remarks>
internal static class WorldEditorBindings {
    /// <summary>The left-trigger modifier id (chord vocabulary: <c>lt</c>).</summary>
    public const string LeftTriggerModifierId = "lt";
    /// <summary>The right-trigger modifier id (chord vocabulary: <c>rt</c>).</summary>
    public const string RightTriggerModifierId = "rt";

    /// <summary>The camera page id (chord: LT held).</summary>
    public const string CameraPageId = "editor-camera";
    /// <summary>The selection page id (chord: RT held): pick, cycle, deselect, delete, grab.</summary>
    public const string SelectPageId = "editor-select";
    /// <summary>The placement page id (chord: LT then RT held): the grab/drag verb set, spawn ghosts, snap.</summary>
    public const string PlacePageId = "editor-place";

    /// <summary>The display label the merged no-modifier page carries while the mode layer is active — the binding
    /// bar's (and <c>editor.status</c>'s) visible evidence the editor pages are live.</summary>
    public const string BasePageLabel = "Editor";

    // Trigger hysteresis: latch at a deliberate squeeze, release only on a clear letoff, so a trigger resting near
    // its threshold never flaps the active page mid-gesture.
    private const float TriggerPress = 0.55f;
    private const float TriggerRelease = 0.35f;

    // Built once — the layer document is immutable data shared by every entering seat.
    private static readonly BindingProfileDocument s_document = Build();

    /// <summary>The shared editor mode-layer document.</summary>
    public static BindingProfileDocument Document => s_document;

    private static BindingProfileDocument Build() {
        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [
                new BindingModifierDefinition(Id: LeftTriggerModifierId, Source: InputSources.Gamepad.LeftTrigger, PressThreshold: TriggerPress, ReleaseThreshold: TriggerRelease, Label: "Camera page"),
                new BindingModifierDefinition(Id: RightTriggerModifierId, Source: InputSources.Gamepad.RightTrigger, PressThreshold: TriggerPress, ReleaseThreshold: TriggerRelease, Label: "Select page"),
            ],
            Pages: [
                // The no-modifier editor page, merged INTO the default base page (same id + empty chord): free-fly
                // sticks, shoulder verticals, camera toggle, exit, status, and speed steps.
                new BindingPageDefinition(
                    Id: WorldDefaultBindings.BasePageId,
                    Chord: [],
                    Entries: [
                        .. StickEntries(),
                        .. HoldRelease(source: InputSources.Gamepad.RightShoulder, command: EditorCommandModule.AscendCommand, label: "Rise", icon: "action.jump"),
                        .. HoldRelease(source: InputSources.Gamepad.LeftShoulder, command: EditorCommandModule.DescendCommand, label: "Sink", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorCommandModule.CameraToggleCommand, label: "Camera", icon: "edit.op"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorCommandModule.ExitCommand, label: "Exit", icon: "edit.exit"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorCommandModule.StatusCommand, label: "Status", icon: "action.target"),
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorCommandModule.FasterCommand, label: "Faster", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorCommandModule.SlowerCommand, label: "Slower", icon: "edit.prev"),
                        // The same controls that entered the mode leave it (Back mirrors the default page's enter).
                        Press(source: InputSources.Gamepad.Back, command: EditorCommandModule.ExitCommand, label: "Exit", icon: "edit.exit"),
                        Press(source: InputSources.Keyboard.Tab, command: EditorCommandModule.ExitCommand, label: "Exit", icon: "edit.exit"),
                    ],
                    Label: BasePageLabel
                ),
                // The LT camera page: explicit fly/orbit selection plus the shared speed steps; North is the
                // focus-selection (pick under the crosshair, so orbit has a pivot the moment you aim at something).
                // Sticks stay bound so flight continues under the held chord.
                new BindingPageDefinition(
                    Id: CameraPageId,
                    Chord: [LeftTriggerModifierId],
                    Entries: [
                        .. StickEntries(),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorCommandModule.FlyCommand, label: "Fly", icon: "edit.play"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorCommandModule.OrbitCommand, label: "Orbit", icon: "action.target"),
                        Press(source: InputSources.Gamepad.ButtonNorth, command: EditorSelectionCommandModule.PickCommand, label: "Focus", icon: "action.target"),
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorCommandModule.FasterCommand, label: "Faster", icon: "edit.next"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorCommandModule.SlowerCommand, label: "Slower", icon: "edit.prev"),
                    ],
                    Label: "Camera"
                ),
                // The RT select page: the crosshair pick, the proximity cycle, deselect/delete, and the grab toggle
                // (grab here so pick→grab flows without releasing RT for the LT+RT place chord).
                new BindingPageDefinition(
                    Id: SelectPageId,
                    Chord: [RightTriggerModifierId],
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
                ),
                // The LT+RT place page: the drag verb set (grab/commit toggle, cancel, snap) and the two spawn ghosts.
                // While a drag is live the sticks translate the pending row instead of flying (the session's routing).
                new BindingPageDefinition(
                    Id: PlacePageId,
                    Chord: [LeftTriggerModifierId, RightTriggerModifierId],
                    Entries: [
                        .. StickEntries(),
                        Press(source: InputSources.Gamepad.ButtonSouth, command: EditorSelectionCommandModule.GrabCommand, label: "Grab", icon: "edit.place"),
                        Press(source: InputSources.Gamepad.ButtonEast, command: EditorSelectionCommandModule.CancelCommand, label: "Cancel", icon: "edit.deselect"),
                        Press(source: InputSources.Gamepad.ButtonWest, command: EditorSelectionCommandModule.SnapCommand, label: "Snap", icon: "edit.style"),
                        Press(source: InputSources.Gamepad.DpadUp, command: EditorSelectionCommandModule.SpawnBoulderCommand, label: "Boulder", icon: "edit.duplicate"),
                        Press(source: InputSources.Gamepad.DpadDown, command: EditorSelectionCommandModule.SpawnSlabCommand, label: "Slab", icon: "edit.duplicate"),
                    ],
                    Label: "Place"
                ),
            ]
        );
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
