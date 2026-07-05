using Puck.Commands;

namespace Puck.Demo;

/// <summary>
/// The demo's action vocabulary — PURELY the overworld's contextual verb plus the GamingBrick control/debug page.
/// Actual SDF engine debug views live under the <c>debug.view.*</c> command family. The overworld's console mode dispatches these for real;
/// outside it every handler just logs, proving a press resolved on the right page through the deterministic
/// snapshot path.
/// </summary>
internal sealed class DemoActionCommandModule : ICommandModule {
    /// <summary>The contextual run/activate command (West on the no-modifier page): held = the Mario hold-to-run;
    /// pressed near an unbooted overworld stand = activate (boot) it. The overworld's console mode interprets the
    /// context; elsewhere the handler just logs.</summary>
    public const string ContextCommand = "overworld.context";
    /// <summary>Debug (Bricks page): toggle the FAIRNESS speed pin on the nearest booted console.</summary>
    public const string BrickSpeedToggleCommand = "brick.speedToggle";
    /// <summary>Debug (Bricks page): live-change the nearest booted console to the DMG capability.</summary>
    public const string BrickModeDmgCommand = "brick.modeDmg";
    /// <summary>Debug (Bricks page): live-change the nearest booted console to the CGB capability.</summary>
    public const string BrickModeCgbCommand = "brick.modeCgb";
    /// <summary>Debug (Bricks page): live-change the nearest booted console to the AGB capability.</summary>
    public const string BrickModeAgbCommand = "brick.modeAgb";
    /// <summary>Debug (Bricks page): delete the nearest booted console's persisted battery save and reboot it with
    /// fresh external RAM (the cartridge boots as if its battery were replaced).</summary>
    public const string BrickSaveClearCommand = "brick.saveClear";
    /// <summary>Debug (Bricks page): log the deterministic world state hash at the current tick.</summary>
    public const string BrickStateHashCommand = "brick.stateHash";
    /// <summary>Debug (Bricks page): log the brick fleet's status (boot/model/speed/choir/cursor per console).</summary>
    public const string BrickFleetStatusCommand = "brick.fleetStatus";
    /// <summary>Debug (Bricks page): capture the next produced frame to a PNG under the artifacts directory.</summary>
    public const string BrickCaptureCommand = "brick.capture";

    /// <summary>The debug/context verbs above, for interning: outside the overworld's console mode (which dispatches
    /// them directly) each handler just logs, proving the press resolved on the right page.</summary>
    private static readonly string[] LoggedVerbs = [
        ContextCommand,
        BrickSpeedToggleCommand,
        BrickModeDmgCommand,
        BrickModeCgbCommand,
        BrickModeAgbCommand,
        BrickSaveClearCommand,
        BrickStateHashCommand,
        BrickFleetStatusCommand,
        BrickCaptureCommand,
    ];

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        foreach (var verb in LoggedVerbs) {
            var name = verb;

            yield return CommandDefinition.Verb(
                description: $"Overworld context/debug verb {name} (the overworld's console mode dispatches it; elsewhere: logs).",
                handler: _ => {
                    Console.Out.WriteLine(value: $"[demo] {name}");

                    return CommandResult.None;
                },
                name: name,
                valueKind: CommandValueKind.Digital
            );
        }
    }
}
