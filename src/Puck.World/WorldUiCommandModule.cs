using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The overlay-UI verb surface: <c>world.screenshot</c> (arm a one-shot PNG capture of the NEXT composed frame,
/// through the outermost render decorator, so the readback shows exactly what the player sees — overlay included)
/// and <c>world.console</c> (show/hide the on-screen console mirror panel). A SEPARATE module from
/// <see cref="WorldCommandModule"/> to keep every class under its analyzer ceilings.
/// </summary>
internal sealed class WorldUiCommandModule(WorldRenderProbe renderProbe, WorldConsoleMirror consoleMirror) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.screenshot",
            description: "Arms a one-shot PNG capture of the next composed frame (world + overlay, via the outermost render decorator): world.screenshot <path.png>. The file lands when that frame produces; the parent directory is created here.",
            handler: (context, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: "[world.screenshot: a target path is required — world.screenshot <path.png>]") { IsError = true };
                }

                if (renderProbe.Render is not { } render) {
                    return new CommandResult(Output: "[world.screenshot: the renderer is not built yet — retry after the first frame]") { IsError = true };
                }

                var path = Path.GetFullPath(path: args[0]);

                try {
                    if (Path.GetDirectoryName(path: path) is { Length: > 0 } directory) {
                        _ = Directory.CreateDirectory(path: directory);
                    }
                } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException) {
                    return new CommandResult(Output: $"[world.screenshot: could not create the target directory ({exception.Message})]") { IsError = true };
                }

                render.RequestCapture(path: path);

                return new CommandResult(Output: $"[world.screenshot: {path}]");
            }
        );
        yield return CommandDefinition.WithTrailingArgs(
            name: "world.console",
            description: "Shows/hides the on-screen console mirror panel: world.console [on|off] — no argument echoes the current state. The pipe keeps working either way; the panel is its visible twin.",
            handler: (_, args) => {
                if (args.Length == 0) {
                    return new CommandResult(Output: $"[world.console: {(consoleMirror.Visible ? "on" : "off")}]");
                }

                bool? on = args[0].ToUpperInvariant() switch {
                    "ON" => true,
                    "OFF" => false,
                    _ => null,
                };

                if (on is not { } resolved) {
                    return new CommandResult(Output: $"[world.console: unknown state '{args[0]}' — on|off]") { IsError = true };
                }

                consoleMirror.SetVisible(visible: resolved);

                return new CommandResult(Output: $"[world.console: {(resolved ? "on" : "off")}]");
            }
        );
    }
}
