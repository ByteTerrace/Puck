using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The overlay-UI verb surface: <c>world.screenshot</c> (arm a one-shot PNG capture of the next composed frame,
/// through the outermost render decorator, so the readback shows exactly what the player sees — overlay included)
/// and <c>world.console</c> (show/hide the on-screen console mirror panel). A separate module from
/// <see cref="WorldCommandModule"/> to keep every class under its analyzer ceilings. The drawn cursor's live
/// read-back is <c>world.view.pointer</c> (<see cref="WorldViewCommandModule"/> — the <c>world.view.orbit</c>
/// family, per-seat live presentation state).
/// </summary>
/// <remarks><c>world.screenshot</c> arms work; it does not do it. The file appears when a frame composes, which is
/// after this handler has returned and may be never — the run can end first, and a second request armed inside the
/// same frame replaces the first. The echo therefore says pending and names the path as a request, never as a file
/// that exists; the render chain prints the resolved path when the frame lands (<c>[capture] unified overlay -&gt;
/// …</c> from the overlay decorator, <c>[debug] captured frame N -&gt; …</c> from the engine node beneath it); a
/// request replaced before it was served is refused out loud, naming the path that will never be written; and
/// <see cref="WorldPostBuildWiring"/> reports anything still outstanding when the run ends. A caller reading either
/// stream can always tell "written" from "never happened" — which is the whole point, since a scripted caller that
/// cannot is a caller being lied to.
/// <para>Registered in every boot shape for command-vocabulary parity (the document validators must see the same
/// committed vocabulary in every boot shape — <c>world.console</c> is a stock play-wheel sector every shipped world
/// commits, and Play's own overlay is what every dungeon's copies it from). This one matters beyond ordinary parity:
/// the console is the control plane (process stdin drives it; the on-screen panel is only a mirror of that pipe), so
/// a headless boot that left <c>world.console</c> unregistered would refuse a world for authoring a wheel sector
/// that opens the very mirror of the pipe driving it — an absurd refusal for a boot shape that has no on-screen
/// panel to toggle in the first place. Both dependencies are optional (default <see langword="null"/> — DI supplies
/// the default rather than throwing when <see cref="WorldBootComposition.AddWorldPresentation"/> never ran) and
/// every handler refuses by name at use, never at registration or validation, when the dependency it needs is
/// absent.</para></remarks>
internal sealed class WorldUiCommandModule(WorldRenderProbe? renderProbe = null, WorldConsoleMirror? consoleMirror = null) : ICommandModule {
    /// <summary>The console-mirror toggle verb name — bound to the backtick key by
    /// <see cref="WorldDefaultBindings"/>.</summary>
    public const string ConsoleCommand = "world.console";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.screenshot",
            description: "Arms a one-shot PNG capture of the next composed frame (world + overlay, via the outermost render decorator): world.screenshot <path.png>. This REQUESTS a capture, it does not take one — the echo reads 'pending <path>' because no file exists yet, and the render chain prints the resolved path ('[capture] unified overlay -> <path>', or '[debug] captured frame N -> <path>' when the overlay drew nothing and forwarded the request down) on stderr the moment the frame lands. Fence a frame (world.wait) before reading the file. Arming a second capture while one is still pending REFUSES rather than silently replacing it — the earlier path would never be written — and a request still outstanding when the run ends is reported on stderr instead of leaving the caller believing a file exists. The parent directory is created here.",
            handler: (context, args) => {
                if (args.Count == 0) {
                    return CommandResult.Error(output: "[world.screenshot: a target path is required — world.screenshot <path.png>]");
                }

                if (renderProbe is null) {
                    return CommandResult.Error(output: "[world.screenshot: requires a windowed boot — headless has no renderer to capture]");
                }

                if (renderProbe.Render is not { } render) {
                    return CommandResult.Error(output: "[world.screenshot: the renderer is not built yet — retry after the first frame]");
                }

                // A pending request is UNFINISHED WORK, not a stale value to overwrite: arming over it would drop a
                // file the caller was already promised, with nothing on either stream to say so. Refusing names both
                // paths and leaves the first request intact, so the caller fences a frame and asks again.
                if (render.PendingCapturePath is { } outstanding) {
                    return CommandResult.Error(output: $"[world.screenshot: a capture of {outstanding} is still pending — arming over it would write no file for it; fence a frame (world.wait) and retry]");
                }

                var path = Path.GetFullPath(path: args[0].ToString());

                try {
                    if (Path.GetDirectoryName(path: path) is { Length: > 0 } directory) {
                        _ = Directory.CreateDirectory(path: directory);
                    }
                } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or NotSupportedException)) {
                    return CommandResult.Error(output: $"[world.screenshot: could not create the target directory ({exception.Message})]");
                }

                render.RequestCapture(path: path);

                // "pending", not the bare path: the words are true at the instant they are printed. The capture line
                // on stderr is what says the file exists.
                return new CommandResult(Output: $"[world.screenshot: pending {path} — lands on the next composed frame]");
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Bindable,
            name: ConsoleCommand,
            description: "Shows/hides the on-screen console mirror panel: world.console [on|off] — no argument TOGGLES the panel; on|off force a side. Bound to the backtick key by default (the console toggle), so it dispatches with no argument and flips visibility each press. Every form echoes the resulting state. The pipe keeps working either way; the panel is its visible twin.",
            handler: (_, args) => {
                if (consoleMirror is null) {
                    return CommandResult.Error(output: "[world.console: requires a windowed boot — headless registers this verb for vocabulary parity only]");
                }

                // A bound key dispatches with no argument, so the no-arg form must ACT, not query: it toggles and
                // echoes the resulting state (which is also the state read a bare query would have wanted).
                bool resolved;

                if (args.Count == 0) {
                    resolved = !consoleMirror.Visible;
                } else if (args.Is(index: 0, value: "on")) {
                    resolved = true;
                } else if (args.Is(index: 0, value: "off")) {
                    resolved = false;
                } else {
                    return CommandResult.Error(output: $"[world.console: unknown state '{args[0].ToString()}' — on|off, or no argument to toggle]");
                }

                consoleMirror.SetVisible(visible: resolved);

                return new CommandResult(Output: $"[world.console: {(resolved ? "on" : "off")}]");
            }
        );
    }
}
