using Puck.Commands;
using Puck.Launcher;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The offscreen presentation's own console surface, <c>world.resize</c>: an offscreen host has no window whose size
/// could change, so a script resizes its display here. The next produced frame composes its cameras for the new extent:
/// the offscreen host asks its render root for it, the root schedules every instance's footprint against it, and every
/// camera and session view fits its declared extent to it. The root presents its last image until its graph installs
/// the new extent, so no frame shows a projection composed for another, and a capture armed meanwhile lands on the first
/// frame at the new extent. Registered only by <c>AddWorldOffscreenPresentation</c>.
/// </summary>
internal sealed class WorldOffscreenCommandModule(OffscreenRenderOptions options, WorldRenderProbe probe, WorldScreenBinder binder, WorldFramePresenter presenter) : ICommandModule {
    private const string Verb = "world.resize";

    private CommandResult Handler(CommandContext context, WireArgs args) {
        if (args.Count == 0) {
            var (currentWidth, currentHeight) = options.Extent;

            return new CommandResult(Output: $"[{Verb}: {currentWidth}x{currentHeight}]");
        }

        if (
            (args.Count != 2) ||
            !args.TryInt(
                index: 0,
                value: out var width
            ) ||
            !args.TryInt(
                index: 1,
                value: out var height
            ) ||
            (width < 1) ||
            (height < 1) ||
            (width > WorldHostDefaults.MaxDisplayExtent) ||
            (height > WorldHostDefaults.MaxDisplayExtent)
        ) {
            return CommandResult.Error(output: $"[{Verb}: expected a width and a height, each 1..{WorldHostDefaults.MaxDisplayExtent} pixels — world.resize <width> <height>]");
        }

        if (probe.Root is not { } root) {
            return CommandResult.Error(output: $"[{Verb}: renderer not ready]");
        }

        options.Resize(
            height: ((uint)height),
            width: ((uint)width)
        );
        root.Resize(
            height: ((uint)height),
            width: ((uint)width)
        );
        binder.ResizeDisplay(
            displayHeight: height,
            displayWidth: width
        );
        // The presenter prepares the next frame's panes and cameras before that frame reports its extent, so it learns
        // the new one here rather than one frame late.
        presenter.ResizeDisplay(
            height: ((uint)height),
            width: ((uint)width)
        );

        return new CommandResult(Output: $"[{Verb}: {width}x{height}]");
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: Verb,
            description: "Resizes the offscreen display live: world.resize <width> <height>, each 1..16384 pixels. The display shows its last image until the render root has installed the new extent, and a world.screenshot armed meanwhile captures the first frame at it, so a script checks a resize without a window. No argument echoes the current extent. Offscreen only: a windowed host's display keeps its document extent, which the swap chain scales to the window.",
            handler: Handler
        );
    }
}
