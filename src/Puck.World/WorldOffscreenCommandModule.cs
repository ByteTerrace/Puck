using Puck.Commands;
using Puck.Launcher;

namespace Puck.World;

/// <summary>
/// The offscreen presentation's own console surface, <c>world.resize</c>: an offscreen host has no window whose size
/// could change, so a script resizes its display here. The next produced frame asks the render root for the new extent,
/// and the display follows it as a windowed display follows its window (<see cref="WorldDisplayExtent"/>): the root
/// schedules every instance's footprint against it and every camera and session view fits its declared extent to it. The
/// root presents its last image until its graph installs the new extent, so no frame shows a projection composed for
/// another, and a capture armed meanwhile lands on the first frame at the new extent. Registered only by
/// <c>AddWorldOffscreenPresentation</c>.
/// </summary>
internal sealed class WorldOffscreenCommandModule(OffscreenRenderOptions options) : ICommandModule {
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

        options.Resize(
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
            description: "Resizes the offscreen display live: world.resize <width> <height>, each 1..16384 pixels. The display shows its last image until the render root has installed the new extent, and a world.screenshot armed meanwhile captures the first frame at it, so a script checks a resize without a window. No argument echoes the current extent. Offscreen only: a windowed host's display follows its window's client area.",
            handler: Handler
        );
    }
}
