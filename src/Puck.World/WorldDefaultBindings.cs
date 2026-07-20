using Puck.Commands;
using Puck.Input;

namespace Puck.World;

/// <summary>
/// The engine-default binding document, authored as a data file rather than code. It is the FIRST layer of every
/// seat's composed mapping (engine default ⊕ world overlays ⊕ profile bindings ⊕ live rebinds), code-authored
/// (never serialized — a null profile <c>Bindings</c> section inherits
/// it), and carries BOTH page groups: the <see cref="PlayGroup"/> rows below (the <c>player.*</c> vocabulary) and the
/// <see cref="WorldEditorBindings"/> rows (group <c>editor</c>, always compiled in — entering the editor is a per-seat
/// <see cref="WorldSeatBindings.SetActiveGroup"/> pointer flip, never a recompose).
/// </summary>
/// <remarks>
/// The play group is FIVE pages, one per ordered trigger chord — the model every group in this document follows:
/// <c>[]</c> base, <c>[lt]</c>, <c>[rt]</c>, <c>[lt, rt]</c>, <c>[rt, lt]</c>. Holding the triggers IS the page
/// turn: the binding bar re-renders the page the held chord selects, so the chord vocabulary is discoverable by
/// squeezing rather than memorized. Pages 1..4 are deliberately SPARSE — they carry only the stick routers (a held
/// analog re-dispatches against the ACTIVE page each tick, so a page without them would stall movement while its
/// chord is held) and wait to be authored through the binding document. Editor entry is Gamepad Back / Keyboard
/// Tab on the base page; no trigger combination enters the editor.
/// The classic doom keyboard layout (W/S forward/back, A/D turn, Q/E strafe; arrows mirror WASD) binds each movement
/// source TWICE — a press edge (default phase) and a release edge (<see cref="CommandPhase.Completed"/>),
/// <c>AnyModifiers</c> so an incidental Shift/Ctrl never breaks gameplay — so one verb handler reads the phase to
/// hold-or-free its axis. Space is the primary action channel (both edges), Enter confirms, F1..F4 claim a slot
/// carried as the binding's constant <see cref="CommandValue.Axis(float)"/>, the sticks route move/look, South/East
/// are the primary/secondary gestures (both edges), and Start cycles.
/// </remarks>
internal static class WorldDefaultBindings {
    /// <summary>The play group — the default page group every seat resolves in outside a mode.</summary>
    public const string PlayGroup = "play";

    /// <summary>The play group's resting page id (chord: nothing held) — page 0.</summary>
    public const string BasePageId = "base";
    /// <summary>The play group's page 1 id (chord: LT held).</summary>
    public const string LeftPageId = "play-lt";
    /// <summary>The play group's page 2 id (chord: RT held).</summary>
    public const string RightPageId = "play-rt";
    /// <summary>The play group's page 3 id (chord: LT then RT held).</summary>
    public const string LeftRightPageId = "play-lt-rt";
    /// <summary>The play group's page 4 id (chord: RT then LT held — the reverse squeeze).</summary>
    public const string RightLeftPageId = "play-rt-lt";

    /// <summary>The left-trigger modifier id (chord vocabulary: <c>lt</c>). Declared here, on the engine default,
    /// because modifiers are document-global: every play and editor page chord references the same two
    /// declarations.</summary>
    public const string LeftTriggerModifierId = "lt";
    /// <summary>The right-trigger modifier id (chord vocabulary: <c>rt</c>).</summary>
    public const string RightTriggerModifierId = "rt";

    // Trigger hysteresis: latch at a deliberate squeeze, release only on a clear letoff, so a trigger resting near
    // its threshold never flaps the active page mid-gesture.
    private const float TriggerPress = 0.55f;
    private const float TriggerRelease = 0.35f;

    /// <summary>Builds the engine-default binding document.</summary>
    /// <returns>A fresh default document (callers compose it with overlays/profile/session layers before compiling).</returns>
    public static BindingProfileDocument BuildDocument() {
        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [
                new BindingModifierDefinition(Id: LeftTriggerModifierId, Source: InputSources.Gamepad.LeftTrigger, PressThreshold: TriggerPress, ReleaseThreshold: TriggerRelease, Label: "LT"),
                new BindingModifierDefinition(Id: RightTriggerModifierId, Source: InputSources.Gamepad.RightTrigger, PressThreshold: TriggerPress, ReleaseThreshold: TriggerRelease, Label: "RT"),
            ],
            Chords: [
                // The play resting page — first row, so "play" is the profile's DEFAULT group.
                new BindingChordDefinition(
                    Group: PlayGroup,
                    Chord: [],
                    Page: new BindingPageDefinition(
                        Id: BasePageId,
                        Entries: [
                            // Movement — each source press-and-release, AnyModifiers, onto the six hold/release verbs.
                            .. HoldRelease(source: InputSources.Keyboard.Letter(letter: 'w'), command: "player.forward"),
                            .. HoldRelease(source: InputSources.Keyboard.ArrowUp, command: "player.forward"),
                            .. HoldRelease(source: InputSources.Keyboard.Letter(letter: 's'), command: "player.back"),
                            .. HoldRelease(source: InputSources.Keyboard.ArrowDown, command: "player.back"),
                            .. HoldRelease(source: InputSources.Keyboard.Letter(letter: 'a'), command: "player.turn-left"),
                            .. HoldRelease(source: InputSources.Keyboard.ArrowLeft, command: "player.turn-left"),
                            .. HoldRelease(source: InputSources.Keyboard.Letter(letter: 'd'), command: "player.turn-right"),
                            .. HoldRelease(source: InputSources.Keyboard.ArrowRight, command: "player.turn-right"),
                            .. HoldRelease(source: InputSources.Keyboard.Letter(letter: 'q'), command: "player.strafe-left"),
                            .. HoldRelease(source: InputSources.Keyboard.Letter(letter: 'e'), command: "player.strafe-right"),
                            // Space = the primary action channel (both edges, AnyModifiers) — variable height under a jump kit.
                            .. HoldRelease(source: InputSources.Keyboard.Space, command: PlayerCommandModule.PrimaryCommand),
                            // Roster verbs on the keyboard: Enter confirms; F1..F4 claim a slot as the binding's Axis1D value.
                            new BindingPageEntryDefinition(Source: InputSources.Keyboard.Enter, Command: PlayerCommandModule.ConfirmCommand, ActivateOn: CommandPhase.Started, AnyModifiers: true),
                            Claim(function: 1),
                            Claim(function: 2),
                            Claim(function: 3),
                            Claim(function: 4),
                            // The gamepad sticks route move/look (default active phase — the router re-dispatches the carried
                            // analog sample each tick).
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.LeftStick, Command: PlayerCommandModule.MoveCommand),
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.RightStick, Command: PlayerCommandModule.LookCommand),
                            // South = the context-routed primary gesture (both edges), East = the secondary gesture (both edges).
                            .. HoldRelease(source: InputSources.Gamepad.ButtonSouth, command: PlayerCommandModule.SouthCommand),
                            .. HoldRelease(source: InputSources.Gamepad.ButtonEast, command: PlayerCommandModule.SecondaryCommand),
                            // Start = device cycle (press edge).
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.Start, Command: PlayerCommandModule.CycleCommand, ActivateOn: CommandPhase.Started, AnyModifiers: true),
                            // Editor entry — Gamepad Back (the view/menu button) and Keyboard Tab, both free here and
                            // both deliberate. The triggers turn pages; they never enter a mode.
                            new BindingPageEntryDefinition(Source: InputSources.Gamepad.Back, Command: EditorCommandModule.EnterCommand, ActivateOn: CommandPhase.Started, Label: "Editor", AnyModifiers: true),
                            new BindingPageEntryDefinition(Source: InputSources.Keyboard.Tab, Command: EditorCommandModule.EnterCommand, ActivateOn: CommandPhase.Started, Label: "Editor", AnyModifiers: true),
                        ],
                        Label: "Base"
                    )
                ),
                // Pages 1..4 — the four ordered trigger chords, held to turn the bar. Sparse by construction: the
                // stick routers only, so movement survives a held chord; content is authored into the document.
                SparsePage(chord: [LeftTriggerModifierId], id: LeftPageId, label: "LT"),
                SparsePage(chord: [RightTriggerModifierId], id: RightPageId, label: "RT"),
                SparsePage(chord: [LeftTriggerModifierId, RightTriggerModifierId], id: LeftRightPageId, label: "LT+RT"),
                SparsePage(chord: [RightTriggerModifierId, LeftTriggerModifierId], id: RightLeftPageId, label: "RT+LT"),
                // The editor group (always compiled in; editor.enter flips the seat's active group onto it).
                .. WorldEditorBindings.Rows(),
            ]
        );
    }

    // One of the play group's four held-chord pages: labelled for the bar, carrying only the stick routers (a held
    // analog re-dispatches against the ACTIVE page each tick, so omitting them would freeze movement mid-chord).
    private static BindingChordDefinition SparsePage(string[] chord, string id, string label) => new(
        Group: PlayGroup,
        Chord: chord,
        Page: new BindingPageDefinition(
            Id: id,
            Entries: [
                new BindingPageEntryDefinition(Source: InputSources.Gamepad.LeftStick, Command: PlayerCommandModule.MoveCommand),
                new BindingPageEntryDefinition(Source: InputSources.Gamepad.RightStick, Command: PlayerCommandModule.LookCommand),
            ],
            Label: label
        )
    );

    // A source bound to a command on BOTH edges (the HoldRelease pattern): a press-edge entry (default phase, fires on
    // Started/Active) and a release-edge entry (ActivateOn Completed), both AnyModifiers.
    private static BindingPageEntryDefinition[] HoldRelease(string source, string command) => [
        new BindingPageEntryDefinition(Source: source, Command: command, AnyModifiers: true),
        new BindingPageEntryDefinition(Source: source, Command: command, ActivateOn: CommandPhase.Completed, AnyModifiers: true),
    ];

    // A function-key claim entry: press edge, AnyModifiers, carrying the 1-based slot as the constant Axis1D value the
    // player.claim handler reads.
    private static BindingPageEntryDefinition Claim(int function) => new(
        Source: InputSources.Keyboard.Function(number: function),
        Command: PlayerCommandModule.ClaimCommand,
        ActivateOn: CommandPhase.Started,
        AnyModifiers: true,
        Value: CommandValue.Axis(value: function)
    );
}
