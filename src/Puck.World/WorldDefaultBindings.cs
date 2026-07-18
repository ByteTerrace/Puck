using Puck.Commands;
using Puck.Input;

namespace Puck.World;

/// <summary>
/// The engine-default binding document — the data-file successor of the former hard-coded <c>inputBindingTable</c> in
/// <c>Program.cs</c>, speaking the <c>player.*</c> command vocabulary. It is the FIRST layer of every seat's composed
/// mapping (§2.4: engine default ⊕ world overlays ⊕ profile bindings ⊕ live rebinds), code-authored (never serialized —
/// a null profile <c>Bindings</c> section inherits it), and reproduces the former table's <see cref="CommandBinding"/>
/// lists value-for-value so the same physical signal yields the same command stream.
/// </summary>
/// <remarks>
/// One no-modifier base page, no modifiers. The classic doom keyboard layout (W/S forward/back, A/D turn, Q/E strafe;
/// arrows mirror WASD) binds each movement source TWICE — a press edge (default phase) and a release edge
/// (<see cref="CommandPhase.Completed"/>), <c>AnyModifiers</c> so an incidental Shift/Ctrl never breaks gameplay — so
/// one verb handler reads the phase to hold-or-free its axis. Space is the primary action channel (both edges), Enter
/// confirms, F1..F4 claim a slot carried as the binding's constant <see cref="CommandValue.Axis(float)"/>, the sticks
/// route move/look, South/East are the primary/secondary gestures (both edges), and Start cycles.
/// </remarks>
internal static class WorldDefaultBindings {
    /// <summary>The base page id — the empty-chord (no-modifier) page every composed layer merges into.</summary>
    public const string BasePageId = "base";

    /// <summary>Builds the engine-default binding document.</summary>
    /// <returns>A fresh default document (callers compose it with overlays/profile/session layers before compiling).</returns>
    public static BindingProfileDocument BuildDocument() {
        return new BindingProfileDocument(
            Version: BindingProfileDocument.CurrentVersion,
            Modifiers: [],
            Pages: [
                new BindingPageDefinition(
                    Id: BasePageId,
                    Chord: [],
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
                        // Editor entry — Gamepad Back (the view/menu button) and Keyboard Tab, both free here and both
                        // deliberate (a rarely-pressed control, so gameplay never trips into the mode by accident). The
                        // editor's own pages arrive as a per-seat MODE layer on entry (WorldEditorBindings), never as a
                        // world overlay.
                        new BindingPageEntryDefinition(Source: InputSources.Gamepad.Back, Command: EditorCommandModule.EnterCommand, ActivateOn: CommandPhase.Started, Label: "Editor", AnyModifiers: true),
                        new BindingPageEntryDefinition(Source: InputSources.Keyboard.Tab, Command: EditorCommandModule.EnterCommand, ActivateOn: CommandPhase.Started, Label: "Editor", AnyModifiers: true),
                    ],
                    Label: "Base"
                ),
            ]
        );
    }

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
