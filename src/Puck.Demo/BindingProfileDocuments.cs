using Puck.Commands;
using Puck.Input;

namespace Puck.Demo;

/// <summary>
/// The demo's built-in default binding profile — the document seeded to storage on first run and the fallback
/// when the stored one fails to load. The demo surface is PURELY the overworld plus its debug screens: the
/// no-modifier page is the movement/contextual baseline (South = jump, North = interact/boot, West = the
/// contextual run/activate; the sticks live in the always-on fallback table), and the two order-sensitive chord
/// pages are the DEBUG surface — SDF engine view modes (RT→LT) followed by the GamingBrick controls/debugs (LT→RT),
/// dispatched by the overworld's console mode.
/// </summary>
internal static class BindingProfileDocuments {
    /// <summary>The modifier id / chord vocabulary of the default profile.</summary>
    public const string LeftModifier = "left";
    /// <inheritdoc cref="LeftModifier"/>
    public const string RightModifier = "right";

    /// <summary>The twelve physical button sources a page binds, in the WoW addon's slot order (the binding-bar
    /// layout indexes match this array).</summary>
    public static readonly string[] PageButtons = [
        InputSources.Gamepad.DpadUp,
        InputSources.Gamepad.DpadRight,
        InputSources.Gamepad.DpadDown,
        InputSources.Gamepad.DpadLeft,
        InputSources.Gamepad.LeftShoulder,
        InputSources.Gamepad.LeftStickPress,
        InputSources.Gamepad.ButtonNorth,
        InputSources.Gamepad.ButtonWest,
        InputSources.Gamepad.ButtonSouth,
        InputSources.Gamepad.ButtonEast,
        InputSources.Gamepad.RightShoulder,
        InputSources.Gamepad.RightStickPress,
    ];

    /// <summary>Builds the default profile document.</summary>
    /// <returns>A fresh default document (callers may mutate it with <c>with</c>-expressions before compiling).</returns>
    public static BindingProfileDocument BuildDefault() {
        return new BindingProfileDocument(
            Modifiers: [
                new BindingModifierDefinition(Icon: "modifier.leftTrigger", Id: LeftModifier, Label: "LT", Source: InputSources.Gamepad.LeftTrigger),
                new BindingModifierDefinition(Icon: "modifier.rightTrigger", Id: RightModifier, Label: "RT", Source: InputSources.Gamepad.RightTrigger),
            ],
            Pages: [
                new BindingPageDefinition(
                    Chord: [],
                    Entries: [
                        new BindingPageEntryDefinition(Command: "overworld.jump", Icon: "action.jump", Label: "Jump", Source: InputSources.Gamepad.ButtonSouth),
                        // The overworld deliberately interacts on NORTH (East is the GB joypad B, so booting a
                        // stand never leaks into a running game) — the profile follows; remap in the JSON if desired.
                        new BindingPageEntryDefinition(Command: "overworld.interact", Icon: "action.interact", Label: "Interact", Source: InputSources.Gamepad.ButtonNorth),
                        // West is CONTEXTUAL in console mode: held = the Mario hold-to-run; pressed at an unbooted
                        // stand = activate (boot) it. West is not a GB joypad line, so holding it never leaks
                        // into a running game either.
                        new BindingPageEntryDefinition(Command: DemoActionCommandModule.ContextCommand, Icon: "action.run", Label: "Run / Activate", Source: InputSources.Gamepad.ButtonWest),
                        // Left bumper DISENGAGES: a player seated at / driving a console leaves it and runs the room
                        // again. A bumper is not a GB joypad line, so it can neither be swallowed by nor leak into the
                        // machine the player is driving — the dedicated way out that never collides with brick input.
                        new BindingPageEntryDefinition(Command: "overworld.leave", Icon: "action.leave", Label: "Leave", Source: InputSources.Gamepad.LeftShoulder),
                    ],
                    Id: "movement",
                    Label: "Movement"
                ),
                // The two chord pages are the DEBUG surface. Keep ENGINE second-to-last and GamingBrick controls LAST
                // in the profile order; chord order still determines which page is active at runtime.
                new BindingPageDefinition(
                    Chord: [RightModifier, LeftModifier],
                    Entries: [
                        new BindingPageEntryDefinition(Command: DebugViewModes.Command(mode: 1), Icon: "action.1", Label: "Depth", Source: InputSources.Gamepad.DpadUp),
                        new BindingPageEntryDefinition(Command: DebugViewModes.Command(mode: 3), Icon: "action.2", Label: "Ray dir", Source: InputSources.Gamepad.DpadRight),
                        new BindingPageEntryDefinition(Command: DebugViewModes.Command(mode: 5), Icon: "action.3", Label: "Iterations", Source: InputSources.Gamepad.DpadDown),
                        new BindingPageEntryDefinition(Command: DebugViewModes.Command(mode: 4), Icon: "action.4", Label: "Material ID", Source: InputSources.Gamepad.DpadLeft),
                        new BindingPageEntryDefinition(Command: DebugViewModes.Command(mode: 0), Icon: "action.5", Label: "Off", Source: InputSources.Gamepad.ButtonNorth),
                        new BindingPageEntryDefinition(Command: DebugViewModes.Command(mode: 2), Icon: "action.6", Label: "Normals", Source: InputSources.Gamepad.ButtonWest),
                    ],
                    Id: "debug-engine",
                    Label: "Debug: Engine"
                ),
                new BindingPageDefinition(
                    Chord: [LeftModifier, RightModifier],
                    Entries: [
                        new BindingPageEntryDefinition(Command: DemoActionCommandModule.BrickSpeedToggleCommand, Icon: "action.1", Label: "Speed pin", Source: InputSources.Gamepad.ButtonWest),
                        new BindingPageEntryDefinition(Command: DemoActionCommandModule.BrickModeDmgCommand, Icon: "action.2", Label: "Mode DMG", Source: InputSources.Gamepad.DpadLeft),
                        new BindingPageEntryDefinition(Command: DemoActionCommandModule.BrickModeCgbCommand, Icon: "action.3", Label: "Mode CGB", Source: InputSources.Gamepad.DpadDown),
                        new BindingPageEntryDefinition(Command: DemoActionCommandModule.BrickModeAgbCommand, Icon: "action.4", Label: "Mode AGB", Source: InputSources.Gamepad.DpadUp),
                        new BindingPageEntryDefinition(Command: DemoActionCommandModule.BrickSaveClearCommand, Icon: "action.5", Label: "Clear save", Source: InputSources.Gamepad.ButtonNorth),
                        new BindingPageEntryDefinition(Command: DemoActionCommandModule.BrickStateHashCommand, Icon: "action.6", Label: "State hash", Source: InputSources.Gamepad.ButtonSouth),
                        new BindingPageEntryDefinition(Command: DemoActionCommandModule.BrickFleetStatusCommand, Icon: "action.7", Label: "Fleet status", Source: InputSources.Gamepad.ButtonEast),
                        new BindingPageEntryDefinition(Command: DemoActionCommandModule.BrickCaptureCommand, Icon: "action.8", Label: "Capture", Source: InputSources.Gamepad.RightShoulder),
                    ],
                    Id: "debug-bricks",
                    Label: "Debug: Bricks"
                ),
            ],
            Version: BindingProfileDocument.CurrentVersion
        );
    }

}
