using System.Globalization;
using Puck.Abstractions.Windowing;
using Puck.Commands;
using Puck.Launcher;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// <c>world.window</c>: the host window's live placement, through the launcher's one door to it
/// (<see cref="HostWindowPlacement"/>). It sizes the client area (to a size, or to a device profile's frame bar resolved
/// as <c>--frame-bar</c> resolves it, by <see cref="WorldDeviceProfiles.FrameBarOf"/>), moves or centres the window,
/// switches the frame-filling mode where the platform window has one, and reads where the window stands. A size change
/// is the windowing system's own resize, so the swap chain, render targets and HUD follow it as they follow a drag of
/// the window's edge, and the World keeps running through it. Every shape registers the verb; a host with no window
/// refuses it by name.
/// </summary>
/// <param name="window">The door to the host window, or <see langword="null"/> where the launcher composed none.</param>
/// <param name="server">The authority whose document's <c>render.devices</c> rows author the frame bars.</param>
/// <param name="deviceProfile">The device profile selection, whose adapter <c>frame-bar auto</c> matches.</param>
internal sealed class WorldWindowCommandModule(HostWindowPlacement? window, WorldServer server, WorldDeviceProfileState deviceProfile) : ICommandModule {
    private const string Verb = "world.window";
    private static readonly CommandForms Forms = CommandForms.Of(Verb, "size <width> <height>", "frame-bar [auto|<profile>]", "center", "move <x> <y>", "fullscreen [on|off]");

    private static string Describe(NativeWindowPlacement placement) => string.Create(provider: CultureInfo.InvariantCulture, handler:
        $"size={placement.Width}x{placement.Height} at={Area(area: placement.Client, sized: false)} monitor={placement.Monitor ?? "unknown"} monitor-area={Area(area: placement.MonitorArea, sized: true)} work-area={Area(area: placement.WorkArea, sized: true)} fullscreen={(placement.Fullscreen ? "on" : "off")}");
    // An area as x,y (its corner alone) or x,y,WxH.
    private static string Area(NativeDesktopArea? area, bool sized) => ((area is { } known)
        ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{known.X},{known.Y}{(sized ? $",{known.Width}x{known.Height}" : string.Empty)}")
        : "unknown");

    private CommandResult Run(CommandContext context, WireArgs args) {
        if (window is null) {
            return CommandResult.Error(output: $"[{Verb}: this host composes no window]");
        }
        if (!window.TryEnter(refusal: out var absence, window: out var placement)) {
            return CommandResult.Error(output: $"[{Verb}: {absence}]");
        }
        if (args.Count == 0) {
            return new CommandResult(Output: $"[{Verb}: {Describe(placement: placement.Placement)}]");
        }

        var operation = args[0].ToString();
        string? note = null;
        string? refusal;
        bool applied;

        switch (operation) {
            case "size" when (args.Count == 3) && args.TryULong(index: 1, value: out var width) && args.TryULong(index: 2, value: out var height) && (width <= uint.MaxValue) && (height <= uint.MaxValue):
                applied = placement.TrySetClientSize(height: ((uint)height), refusal: out refusal, width: ((uint)width));
                note = Granted(placement: placement, width: ((uint)width), height: ((uint)height));
                break;
            case "frame-bar" when args.Count <= 2:
                var request = ((args.Count == 2) ? args[1].ToString() : "auto");
                var adapter = deviceProfile.Current.Adapter;

                if (WorldDeviceProfiles.FrameBarOf(adapterName: adapter, refusal: out refusal, render: server.ReadView(context).Definition.Render, request: request) is not { FrameBar: { } bar } profile) {
                    return CommandResult.Error(output: $"[{Verb} frame-bar {request}: {refusal}]");
                }

                applied = placement.TrySetClientSize(height: ((uint)bar.Height), refusal: out refusal, width: ((uint)bar.Width));
                var grant = Granted(placement: placement, width: ((uint)bar.Width), height: ((uint)bar.Height));

                note = string.Create(provider: CultureInfo.InvariantCulture, handler: $"the frame bar of the device profile '{profile.Name}'{((adapter is null) ? string.Empty : $" (adapter '{adapter}')")}, {bar.Hertz} Hz{((grant is null) ? string.Empty : $"; {grant}")}");
                break;
            case "center" when args.Count == 1:
                applied = placement.TryCenter(refusal: out refusal);
                break;
            case "move" when (args.Count == 3) && args.TryInt(index: 1, value: out var x) && args.TryInt(index: 2, value: out var y):
                applied = placement.TryMoveClient(refusal: out refusal, x: x, y: y);
                break;
            case "fullscreen" when (args.Count == 1) || ((args.Count == 2) && (args.Is(index: 1, value: "on") || args.Is(index: 1, value: "off"))):
                applied = placement.TrySetFullscreen(fullscreen: ((args.Count == 1) ? !placement.Placement.Fullscreen : args.Is(index: 1, value: "on")), refusal: out refusal);
                break;
            default:
                return Forms.Refuse(args: args);
        }

        return (applied
            ? new CommandResult(Output: $"[{Verb} {operation}: {Describe(placement: placement.Placement)}{((note is null) ? string.Empty : $" ({note})")}]")
            : CommandResult.Error(output: $"[{Verb} {operation}: {refusal}]"));
    }
    // Names a size the windowing system granted in place of the one asked for; null when they agree.
    private static string? Granted(INativeWindowPlacement placement, uint width, uint height) {
        var current = placement.Placement;

        return (((current.Width == width) && (current.Height == height))
            ? null
            : string.Create(provider: CultureInfo.InvariantCulture, handler: $"asked {width}x{height}; the window reports {current.Width}x{current.Height}, and a windowing system that answers later reports its grant on the next read"));
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Local,
            bindability: CommandBindability.Unbindable,
            name: Verb,
            description: $"Reads or changes the host window's placement while the World runs (Immediate, local): {Forms.Spelling}. With no argument it reads the client area's size, where its top-left corner stands on the desktop (at), the monitor holding the window with its whole area and work area (the monitor less its task bars), and whether the window fills its monitor. size sets the client area to a size in pixels (1 to {NativeWindowPlacement.MaxClientExtent} a side) and leaves the window where it stands; frame-bar sets it to a device profile's frame bar (render.devices' frameBar), the profile the running adapter selects for auto (the default) or the one named, as --frame-bar does at boot. center centres the window, frame included, in its monitor's work area, keeping a window taller than that area's title bar at its top. move puts the client area's top-left corner at a desktop point, the coordinates the read reports. fullscreen fills the monitor without a frame, or returns to the window it was (with no word it switches). A window filling its monitor, minimized or maximized returns to a window before it is sized or moved. A size change is the windowing system's own resize, the one a drag of the window's edge makes: the swap chain, render targets and HUD follow it on the next frame and the World runs on through it, its session, seats and camera untouched. Each change answers with the placement it left. A Win32 window does all of it; an X11 window has no fullscreen mode and centres on its root screen, and reports the size its window manager grants from the next read; a Wayland compositor places its toplevels itself, so a Wayland window refuses move and center and has no fullscreen mode. A headless or offscreen host has no window and refuses by name.",
            forms: Forms,
            handler: Run
        );
    }
}
