using Puck.Commands;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The overlay theme's read-back and the player's contrast preference: <c>world.theme</c> names where the live theme
/// comes from (the document's own, or the engine's neutral default when it authors none) and the contrast mode, and
/// <c>world.theme contrast high|normal|toggle</c> sets the preference <see cref="WorldThemeResolve.HighContrast"/>
/// holds. Presentation only: the preference belongs to this process's player, never to the document, and nothing
/// here reaches the simulation, so a press needs no grant.
/// </summary>
/// <param name="server">The authority whose live definition the read-back names.</param>
/// <param name="theme">The presentation's theme resolver.</param>
internal sealed class WorldThemeCommandModule(WorldServer server, WorldThemeResolve theme) : ICommandModule {
    private const string Usage = "[world.theme: expected contrast high|normal|toggle]";

    private CommandResult Describe() => new(Output: $"[world.theme: source={((server.Definition.ThemeRaw is null)
        ? "engine-default"
        : "document")} contrast={(theme.HighContrast
        ? "high"
        : "normal")}]");

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: "world.theme",
            description: "Reads the overlay theme's source and the contrast preference, or sets the preference: world.theme [contrast high|normal|toggle]. The source is document (the world authors or imports a theme) or engine-default (the engine's neutral theme, which a world that authors none draws). High contrast raises every scrim to at least WorldThemeCapacity.HighContrastScrimAlpha opaque, so overlay text stays legible over any scene. Presentation only and this process's own: the document is untouched and nothing reaches the simulation.",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return Describe();
                }

                if (
                    (args.Count != 2) ||
                    !args.Is(
                        index: 0,
                        value: "contrast"
                    )
                ) {
                    return CommandResult.Error(output: Usage);
                }

                bool? high = (args.Is(
                    index: 1,
                    value: "high"
                )
                    ? true
                    : (args.Is(
                        index: 1,
                        value: "normal"
                    )
                        ? false
                        : (args.Is(
                            index: 1,
                            value: "toggle"
                        )
                            ? !theme.HighContrast
                            : null)));

                if (high is not { } resolved) {
                    return CommandResult.Error(output: Usage);
                }

                theme.HighContrast = resolved;

                return Describe();
            },
            routing: CommandRouting.Immediate
        );
    }
}
