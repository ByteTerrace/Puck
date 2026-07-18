using System.Globalization;
using System.Text;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The diegetic screens' console surface — the wire verbs that boot, eject, and inspect the deterministic machines
/// behind the world's screens. <c>screen.insert</c>/<c>screen.eject</c> are the runtime twin of the declared screen data
/// (the same binder boot/eject code), so an agent scripts a cabinet over the pipe with no restart;
/// <c>screen.state</c>/<c>screen.peek</c> are read-only queries that make the machine state pipe-assertable (a booted
/// machine's engine, bound handle, stepped-frame count, engaged players, and one memory byte). The world speaks the
/// engine-neutral machine vocabulary — a machine is resolved against a registered engine by id, and each engine owns its
/// own options string. Every verb is wire-native — each failure marks <see cref="CommandResult.IsError"/> so
/// <c>wire.ack quiet</c> drops only successes, and the two queries always echo their data.
/// </summary>
internal sealed class ScreenCommandModule(WorldScreenBinder binder, WorldEngagement engagement) : ICommandModule {
    private readonly WorldScreenBinder m_binder = binder;
    private readonly WorldEngagement m_engagement = engagement;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        foreach (var command in Commands()) {
            yield return ((command.Name is "screen.state" or "screen.peek")
                ? command
                : command with { Routing = CommandRouting.Simulation });
        }
    }

    private IEnumerable<CommandDefinition> Commands() {
        yield return CommandDefinition.WithWireArgs(
            name: "screen.insert",
            description: "Boots content onto a declared screen, live: screen.insert <index> <contentPath> [engine] [options…] — <index> the engine screen index, <contentPath> a content file (a cartridge ROM), the optional [engine] a registered screen-machine engine id (omit it when one is registered — the mechanical default), and the trailing tokens the engine's own options string (the gaming-brick engine reads dmg|cgb|agb plus dmgspeed). The runtime twin of the declared machine data (same boot code); an existing machine on the slot is live-swapped. Errors on an undeclared screen, an unresolved engine, an unreadable file, or rejected options.",
            handler: InsertHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "screen.camera",
            description: "Shows the live webcam on a declared screen: screen.camera <index>. Binds the ONE shared camera session (every camera screen samples one feed — two sessions on a device flicker), using the runtime feed profile and skipping unchanged camera frames. Any existing producer on the slot is cleared. Errors on an undeclared screen or when no camera device can be opened.",
            handler: CameraHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "screen.capture",
            description: "Captures a live desktop window onto a declared screen: screen.capture <index> <windowTitle...> — the title fragment (may contain spaces) is a case-insensitive substring match resolved when the command runs. Windows Graphics Capture publishes compositor frames at the runtime feed profile's fixed extent and cadence. Any existing producer on the slot is cleared. Errors on an undeclared screen, a missing target, or an unavailable capture service.",
            handler: CaptureHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "screen.desktop",
            description: "Captures a whole monitor onto a declared screen: screen.desktop <index> [monitorIndex] — [monitorIndex] the 0-based monitor (default 0 = primary). Windows Graphics Capture publishes the monitor's compositor frames at the runtime feed profile's fixed extent and cadence, reacquiring if the monitor disconnects and returns. Any existing producer on the slot is cleared. Errors on an undeclared screen, a monitor index not present, or an unavailable capture service.",
            handler: DesktopHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "screen.view",
            description: "Shows a placeable camera's view of this same world on a declared screen (the jumbotron recursion): screen.view <index> <cameraName>. Registers one offscreen camera render, budgeted round-robin; the screen's own face binds nothing inside that render (no feedback). Any existing producer on the slot is cleared. Errors on an undeclared screen or an unknown camera name.",
            handler: ViewHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "screen.eject",
            description: "Ejects a screen's live source, live: screen.eject <index> — clears any producer kind (a cartridge, the webcam, a window capture). The slot reverts to its declared test pattern or to the engine's procedural no-signal fallback. Errors on an undeclared screen or a slot with no live source.",
            handler: EjectHandler
        );
        yield return CommandDefinition.WithWireArgs(
            name: "screen.state",
            description: "Echoes a screen's live machine state: screen.state <index> — assigned/empty, the hosting engine id, bound/unbound (a nonzero source handle this frame), the stepped-frame count, and the engaged players. A query (always echoes, even under wire.ack quiet) — the pipe-assertable machine state.",
            handler: StateHandler,
            echoesData: true
        );
        yield return CommandDefinition.WithWireArgs(
            name: "screen.peek",
            description: "Reads one memory byte from a screen's machine: screen.peek <index> <addr> — <addr> a 0x-prefixed hex machine address (the gaming-brick's work RAM is [0xC000, 0xDFFF]). A read only, never a write into machine state, so a piped proof can assert a game's stored bytes. A query (always echoes). Errors when the screen carries no machine, or its machine has no memory-peek capability.",
            handler: PeekHandler,
            echoesData: true
        );
    }
    private CommandResult InsertHandler(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return Error(message: "[screen.insert: expected <index> <contentPath> — plus an optional engine id and options]");
        }

        if (!args.TryInt(index: 0, value: out var index)) {
            return Error(message: $"[screen.insert: index '{args[0].ToString()}' must be an integer]");
        }

        var contentPath = args[1].ToString();
        // Grammar: <index> <contentPath> [engine] [options…]. The first trailing token is the engine id ONLY when it
        // matches a registered engine; otherwise it belongs to the options string and the engine defaults (the sole
        // registered engine). The remaining trailing tokens join, space-separated, into the engine's options string.
        var token = 2;
        string? engineId = null;

        if ((token < args.Count) && m_binder.HasEngine(engineId: args[token].ToString())) {
            engineId = args[token].ToString();
            token++;
        }

        string? options = null;

        if (token < args.Count) {
            var optionsBuilder = new StringBuilder();

            for (; (token < args.Count); token++) {
                if (optionsBuilder.Length > 0) {
                    _ = optionsBuilder.Append(value: ' ');
                }

                _ = optionsBuilder.Append(value: args[token].ToString());
            }

            options = optionsBuilder.ToString();
        }

        var (ok, message) = m_binder.TryInsert(index: index, contentPath: contentPath, engineId: engineId, options: options);

        return (ok
            ? Success(args: in args, message: $"[screen.insert: {message}]")
            : Error(message: $"[screen.insert: {message}]"));
    }
    private CommandResult CameraHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return Error(message: "[screen.camera: expected one <index>]");
        }

        if (!args.TryInt(index: 0, value: out var index)) {
            return Error(message: $"[screen.camera: index '{args[0].ToString()}' must be an integer]");
        }

        var (ok, message) = m_binder.TryCamera(index: index);

        return (ok
            ? Success(args: in args, message: $"[screen.camera: {message}]")
            : Error(message: $"[screen.camera: {message}]"));
    }
    private CommandResult CaptureHandler(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return Error(message: "[screen.capture: expected <index> <windowTitle...>]");
        }

        if (!args.TryInt(index: 0, value: out var index)) {
            return Error(message: $"[screen.capture: index '{args[0].ToString()}' must be an integer]");
        }

        // The window title is every token after the index joined with spaces — a title may contain spaces.
        var titleBuilder = new StringBuilder();

        for (var token = 1; (token < args.Count); token++) {
            if (token > 1) {
                _ = titleBuilder.Append(value: ' ');
            }

            _ = titleBuilder.Append(value: args[token].ToString());
        }

        var (ok, message) = m_binder.TryCapture(index: index, windowTitle: titleBuilder.ToString());

        return (ok
            ? Success(args: in args, message: $"[screen.capture: {message}]")
            : Error(message: $"[screen.capture: {message}]"));
    }
    private CommandResult DesktopHandler(CommandContext context, WireArgs args) {
        if (args.Count is < 1 or > 2) {
            return Error(message: "[screen.desktop: expected <index> [monitorIndex]]");
        }

        if (!args.TryInt(index: 0, value: out var index)) {
            return Error(message: $"[screen.desktop: index '{args[0].ToString()}' must be an integer]");
        }

        var monitorIndex = 0;

        if ((args.Count == 2) && !args.TryInt(index: 1, value: out monitorIndex)) {
            return Error(message: $"[screen.desktop: monitorIndex '{args[1].ToString()}' must be an integer]");
        }

        var (ok, message) = m_binder.TryDesktop(index: index, monitorIndex: monitorIndex);

        return (ok
            ? Success(args: in args, message: $"[screen.desktop: {message}]")
            : Error(message: $"[screen.desktop: {message}]"));
    }
    private CommandResult ViewHandler(CommandContext context, WireArgs args) {
        if (args.Count != 2) {
            return Error(message: "[screen.view: expected <index> <cameraName>]");
        }

        if (!args.TryInt(index: 0, value: out var index)) {
            return Error(message: $"[screen.view: index '{args[0].ToString()}' must be an integer]");
        }

        var (ok, message) = m_binder.TryView(index: index, cameraName: args[1].ToString());

        return (ok
            ? Success(args: in args, message: $"[screen.view: {message}]")
            : Error(message: $"[screen.view: {message}]"));
    }
    private CommandResult EjectHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return Error(message: "[screen.eject: expected one <index>]");
        }

        if (!args.TryInt(index: 0, value: out var index)) {
            return Error(message: $"[screen.eject: index '{args[0].ToString()}' must be an integer]");
        }

        var (ok, message) = m_binder.TryEject(index: index);

        return (ok
            ? Success(args: in args, message: $"[screen.eject: {message}]")
            : Error(message: $"[screen.eject: {message}]"));
    }
    private CommandResult StateHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return Error(message: "[screen.state: expected one <index>]");
        }

        if (!args.TryInt(index: 0, value: out var index)) {
            return Error(message: $"[screen.state: index '{args[0].ToString()}' must be an integer]");
        }

        if (m_binder.State(index: index) is not { } state) {
            return Error(message: $"[screen.state: no screen {index} declared]");
        }

        var engaged = m_engagement.PlayersOn(screenIndex: index);
        var engagedText = ((engaged.Count > 0) ? string.Join(separator: "+", values: engaged.Select(selector: static n => $"p{n}")) : "none");
        var builder = new StringBuilder();

        _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"[screen.state: {index} ");

        if (state.Assigned) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"assigned {(state.Engine ?? "?")} {((state.Handle != 0) ? "bound" : "unbound")} frames={state.FramesStepped} pending={state.PendingSteps}/{state.MaximumPendingSteps} backpressure={state.BackpressureEvents} engaged={engagedText}");
        } else {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $"empty {((state.Handle != 0) ? "bound" : "unbound")} engaged={engagedText}");
        }

        if (state.Fault is { } fault) {
            _ = builder.Append(provider: CultureInfo.InvariantCulture, handler: $" fault={fault}");
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    private CommandResult PeekHandler(CommandContext context, WireArgs args) {
        if (args.Count != 2) {
            return Error(message: "[screen.peek: expected <index> <addr> — addr a 0x-prefixed hex address]");
        }

        if (!args.TryInt(index: 0, value: out var index)) {
            return Error(message: $"[screen.peek: index '{args[0].ToString()}' must be an integer]");
        }

        if (!TryParseHex(token: args[1], value: out var address)) {
            return Error(message: $"[screen.peek: addr '{args[1].ToString()}' must be a 0x-prefixed hex address]");
        }

        var (ok, message) = m_binder.TryPeek(index: index, address: address, value: out var value);

        if (!ok) {
            return Error(message: $"[screen.peek: {message}]");
        }

        return new CommandResult(Output: string.Create(provider: CultureInfo.InvariantCulture, handler: $"[screen.peek: {index} 0x{address:X4}=0x{value:X2}]"));
    }

    // A side-effecting verb's success echo, gated on the ack mode: a quiet flood drops it (CommandResult.None).
    private static CommandResult Success(in WireArgs args, string message) {
        return (args.Echo ? new CommandResult(Output: message) : CommandResult.None);
    }

    // A wire failure: always surfaced and marked IsError so wire.ack quiet suppresses only successes, never errors.
    private static CommandResult Error(string message) {
        return new CommandResult(Output: message) {
            IsError = true,
        };
    }

    // Parse a 0x-prefixed (or bare) hex address into a 16-bit value.
    private static bool TryParseHex(ReadOnlySpan<char> token, out ushort value) {
        var span = (token.StartsWith(value: "0x", comparisonType: StringComparison.OrdinalIgnoreCase) ? token[2..] : token);

        return ushort.TryParse(s: span, style: NumberStyles.HexNumber, provider: CultureInfo.InvariantCulture, result: out value);
    }
}
