using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.Assets.Qr;
using Puck.World.Server;
using Puck.World.Machines;

namespace Puck.World;

/// <summary>
/// The diegetic screens' console surface — the wire verbs that insert content, detach displays, and inspect machines
/// behind the world's screens. Named <c>screen.insert</c> submits the provider's content operation;
/// <c>screen.eject</c> detaches the display through <see cref="WorldMutation.UpsertScreen"/>. Both use the ordered domain;
/// <c>screen.source &lt;index&gt; &lt;kind&gt; [args…]</c> stays genuinely presentation, calling
/// <see cref="WorldScreenBinder"/> directly (never a machine, never tape-covered).
/// <c>screen.state</c>/<c>screen.peek</c>/<c>screen.camera</c>/<c>world.machines</c> are read-only queries that make the
/// live state pipe-assertable. The world speaks the engine-neutral machine vocabulary.
/// </summary>
internal sealed class ScreenCommandModule(WorldScreenBinder binder, WorldServer server, IServerLink link, WorldMachineCatalog machines) : ICommandModule {
    private readonly WorldScreenBinder m_binder = binder;
    private readonly WorldEngagement m_engagement = server.Engagement;
    private readonly WorldServer m_server = server;
    private readonly IServerLink m_link = link;

    // The Control check over a screen subject, under whichever identity this dispatch's ingress door stamped.
    private bool AllowsControl(WorldPrincipal principal, int index) =>
        m_server.Grants.Allows(
            principal: principal,
            capability: WorldCapability.Control,
            subject: GrantSubject.Screen(index: index)
        );

    private IEnumerable<CommandDefinition> Commands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.insert",
            description: "Inserts content into the named machine displayed by a screen: screen.insert <index> <contentPath>. Uses the current generation and provider content.insert operation, requiring Control over the screen and machine. The machine retains its authored configuration. Empty legacy slots additionally accept [engine] [options…] through the screen-operation protocol.",
            handler: InsertHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.source",
            description: "Binds a declared screen's live PRESENTATION source, absorbing the five former per-kind verbs into one: screen.source <index> <kind> [args…] — <kind> is camera | capture | desktop | probe | qr | view, each carrying its own former arg grammar unchanged: camera [color|infrared] [seat N] (a camera is an input device seated like a pad — <seat> (1-based, default 1) names which seat's camera device to show, never hardware directly; one shared feed per (seat, sensor); concurrent color and infrared are used only when the seat's device proves both streams live; default color); probe <probeId> (a declared probe whose kind writes a texture output); capture <windowTitle...> (a case-insensitive substring match, may contain spaces); desktop [monitorIndex] (0-based, default 0 = primary); qr [payload] [ecLevel] [quietZoneModules] (payload a single token; ecLevel one of L|M|Q|H, default M; quietZoneModules default 4 — NO payload echoes the current authoring instead of changing it); view <cameraName> (the jumbotron recursion — one offscreen camera render, budgeted round-robin). Changes the presentation binding. A named machine keeps its identity and continues running when the screen changes source; a legacy slot-owned machine is ejected through the ordered domain first. Errors on an undeclared screen, an unresolved kind, or the kind's own refusal; an unassigned seat or an incompatible sensor is NOT a refusal — the bind succeeds and the fault surfaces through screen.state/screen.camera instead.",
            handler: SourceHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.eject",
            description: "Ejects a screen's live source, live: screen.eject <index>. A named machine display removes only the screen source through a WorldMutation.UpsertScreen submission and keeps its producer alive; a legacy screen-owned machine uses the same source removal, while a presentation source ejects directly through the binder.",
            handler: EjectHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.machines",
            description: "Echoes the registered screen-machine engines and cartridge forge compilers: world.machines. A query (always echoes).",
            handler: MachinesHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.links",
            description: "Echoes every live cable link: screen.links — each link's name, member screens, and live (transfers=…) or dormant (with the reason) state. A query (always echoes, even under wire.ack quiet).",
            handler: LinksHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.camera",
            description: "Echoes every known camera device: screen.camera — one section per device, its roster token (camera<N>, world.devices' own vocabulary), name, sensors, live tier (gpu | cpu | opening | unopened), and the seat it is currently assigned to (or 'unassigned'); a device with a live sensor feed additionally reports that sensor's negotiated extent, native transport subtype/rate, each device-supported control's current value/mode and range (with auto capability), the currently-resolved seat's authored value, and the authored vendor-extension rows read back raw. Controls are per DEVICE: whichever seat resolves to a device supplies its desired control state (the first controls-bearing camera row for that seat wins; an UpsertScreen mutation moves it live). A query (always echoes, even under wire.ack quiet). Errors when no camera device has ever been enumerated.",
            handler: CameraHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.state",
            description: "Echoes a screen's live machine state: screen.state <index> — assigned/empty, the hosting engine id, bound/unbound (a nonzero source handle this frame), the stepped-frame count, the engaged players, for content compiled from a cartridge document cartridge <path> hash <source hash> rom <rom hash>, and, for a screen declaring memory bindings, memory=0x<addr>:R|W=<value|none> per binding (the value each Read binding last mirrored into its cell, or each Write binding last poked into the machine — none before its first observed value). A query (always echoes, even under wire.ack quiet) — the pipe-assertable machine state.",
            handler: StateHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.peek",
            description: "Reads one memory byte from a screen's machine: screen.peek <index> <addr> — <addr> a 0x-prefixed hex machine address (or variable symbol for compiled cartridges). A read only, never a write into machine state, so a piped proof can assert a game's stored bytes. A query (always echoes). Errors when the screen carries no machine, or its machine has no memory-peek capability.",
            handler: PeekHandler
        );
    }
    // The declared screens row at the engine screen-surface index, or null when undeclared — screen.state's own
    // memory-binding segment reads the DECLARED bindings (screens[].memory) rather than anything the machine host
    // itself tracks, since a binding is document authoring, not live machine state.
    private WorldScreen? DeclaredScreen(int index) {
        var screens = m_server.Definition.Screens;

        for (var position = 0; (position < screens.Count); position++) {
            if (screens[position].Index == index) {
                return screens[position];
            }
        }

        return null;
    }
    private static CommandResult Denied(WorldPrincipal principal, string verb, int index) =>
        CommandResult.Error(output: $"[{verb}: {principal.Describe()} lacks Control over screen {index} — grant it (world.grant {principal.Describe()} control screen:{index})]");

    private CommandResult EjectHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return CommandResult.Error(output: "[screen.eject: expected one <index>]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[screen.eject: index '{args[0].ToString()}' must be an integer]");
        }

        var principal = context.ActingPrincipal();

        if (!AllowsControl(
            index: index,
            principal: principal
        )) {
            return Denied(
                index: index,
                principal: principal,
                verb: "screen.eject"
            );
        }

        if (DeclaredScreen(index: index) is { } existing &&
            (m_server.Machines.HasMachine(index: index) || existing.Source is WorldScreenSource.Machine)) {
            if (existing.Source is WorldScreenSource.Machine) {
                var updated = existing with { Source = new WorldScreenSource.None() };

                return m_link.Submit(mutation: new WorldMutation.UpsertScreen(Principal: principal, Screen: updated));
            }
        }

        var (ok, message) = m_binder.TryEject(index: index);

        return (ok
            ? new CommandResult(Output: $"[screen.eject: {message}]")
            : CommandResult.Error(output: $"[screen.eject: {message}]")
        );
    }

    private void EjectMachineFirst(int index, WorldPrincipal principal) {
        if (m_server.Machines.HasMachine(index: index) &&
            DeclaredScreen(index: index)?.Source is not WorldScreenSource.Machine) {
            if (DeclaredScreen(index: index) is { } existing) {
                var updated = existing with { Source = new WorldScreenSource.None() };
                _ = m_link.Submit(mutation: new WorldMutation.UpsertScreen(Principal: principal, Screen: updated));
            }
        }
    }

    private CommandResult InsertHandler(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return CommandResult.Error(output: "[screen.insert: expected <index> <contentPath> — plus an optional engine id and options]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[screen.insert: index '{args[0].ToString()}' must be an integer]");
        }

        var principal = context.ActingPrincipal();

        if (!AllowsControl(
            index: index,
            principal: principal
        )) {
            return Denied(
                index: index,
                principal: principal,
                verb: "screen.insert"
            );
        }

        var contentPath = args[1].ToString();
        var token = 2;
        string? engineId = null;

        if (
            (token < args.Count) &&
            machines.IsRegistered(engineId: args[token].ToString())
        ) {
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

        var existing = DeclaredScreen(index: index);

        if (existing?.Source is WorldScreenSource.Machine named) {
            if (m_server.Machines.InstanceState(named.Instance) is not { } state) {
                return CommandResult.Error($"[screen.insert: named machine '{named.Instance}' is unavailable]");
            }
            if ((engineId is not null && engineId != state.Engine) || !string.IsNullOrWhiteSpace(options)) {
                return CommandResult.Error($"[screen.insert: '{named.Instance}' keeps its authored engine and configuration; use machine.operation for provider changes]");
            }
            return WorldMachineCommandModule.InsertContent(m_link, m_server.Machines, principal,
                named.Instance, contentPath, verb: "screen.insert");
        }

        if (engineId is null) {
            if (
                (existing?.Source is WorldScreenSource.Machine m) &&
                (m_server.Machines.InstanceState(m.Instance) is { } state)
            ) {
                engineId = state.Engine;
            } else {
                var allEngines = machines.Engines.Values.ToArray();

                if (allEngines.Length == 1) {
                    engineId = allEngines[0].Id;
                } else if (allEngines.Length > 1) {
                    engineId = "gaming-brick";
                }
            }
        }

        if (engineId is null || !machines.IsRegistered(engineId: engineId)) {
            return CommandResult.Error(output: $"[screen.insert: no screen-machine engine '{engineId ?? "unspecified"}' registered]");
        }

        m_link.SubmitScreenOp(
            op: new WorldScreenOp.Insert(
                Index: index,
                ContentPath: contentPath,
                EngineId: engineId,
                Options: options
            ),
            principal: principal
        );

        return CommandResult.None;
    }

    private CommandResult LinksHandler(CommandContext context, WireArgs args) {
        if (CommandResult.RequireNoArguments(args: args, verb: "screen.links") is { } refusal) {
            return refusal;
        }

        return new CommandResult(Output: $"[screen.links: {m_binder.DescribeLinks()}]");
    }

    private CommandResult MachinesHandler(CommandContext context, WireArgs args) {
        if (CommandResult.RequireNoArguments(args: args, verb: "world.machines") is { } refusal) {
            return refusal;
        }

        var engines = machines.Engines.Values.ToArray();
        var compilers = machines.ContentProviders;
        var sb = new StringBuilder();
        _ = sb.Append("[world.machines: ");

        if (engines.Length == 0) {
            _ = sb.Append("none registered");
        } else {
            for (var i = 0; (i < engines.Length); i++) {
                if (i > 0) {
                    _ = sb.Append(", ");
                }

                var eng = engines[i];
                _ = sb.Append(eng.Id);

                if (compilers.ContainsKey(key: eng.Id)) {
                    _ = sb.Append(" (forge)");
                }
            }
        }

        _ = sb.Append(']');

        return new CommandResult(Output: sb.ToString());
    }

    private CommandResult PeekHandler(CommandContext context, WireArgs args) {
        if (args.Count != 2) {
            return CommandResult.Error(output: "[screen.peek: expected <index> <addr> — addr a 0x-prefixed hex address or symbol]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[screen.peek: index '{args[0].ToString()}' must be an integer]");
        }

        ushort address;

        if (TryParseHex(
            token: args[1],
            value: out var parsedAddr
        )) {
            address = parsedAddr;
        } else if (m_server.Machines.TryResolveSymbol(index: index, symbol: args[1].ToString(), address: out var symAddr)) {
            address = unchecked((ushort)symAddr);
        } else {
            return CommandResult.Error(output: $"[screen.peek: addr '{args[1].ToString()}' must be a 0x-prefixed hex address or recognized symbol]");
        }

        var (ok, message) = m_binder.TryPeek(
            address: address,
            index: index,
            value: out var value
        );

        if (!ok) {
            return CommandResult.Error(output: $"[screen.peek: {message}]");
        }

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[screen.peek: {index} 0x{address:X4}=0x{value:X2}]"
        ));
    }
    private CommandResult SourceCamera(int index, WorldPrincipal principal, in WireArgs args) {
        if (args.Count > 5) {
            return CommandResult.Error(output: "[screen.source: camera expects [color|infrared] [seat N]]");
        }

        // The optional sensor token: color (the default) or infrared — the sensor-camera stream a Windows Hello
        // capable device exposes as its own capture device, streaming BESIDE the color feed rather than replacing it.
        var sensor = WorldCameraSensor.Color;
        var seat = 1;
        var token = 2;

        if (
            (token < args.Count) &&
            !args.Is(index: token, value: "seat")
        ) {
            var sensorToken = args[token].ToString();

            if (string.Equals(a: sensorToken, b: "infrared", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                sensor = WorldCameraSensor.Infrared;
            } else if (!string.Equals(a: sensorToken, b: "color", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return CommandResult.Error(output: $"[screen.source: unknown camera sensor '{sensorToken}' — expected color or infrared]");
            }

            token++;
        }

        if (token < args.Count) {
            if (!args.Is(index: token, value: "seat")) {
                return CommandResult.Error(output: $"[screen.source: unexpected token '{args[token].ToString()}' — expected 'seat <N>']");
            }

            token++;

            if (
                (token >= args.Count) ||
                !args.TryInt(index: token, value: out seat)
            ) {
                return CommandResult.Error(output: "[screen.source: seat expects an integer]");
            }

            token++;
        }

        if (token != args.Count) {
            return CommandResult.Error(output: "[screen.source: camera expects [color|infrared] [seat N]]");
        }

        EjectMachineFirst(
            index: index,
            principal: principal
        );

        var (ok, message) = m_binder.TryCamera(
            index: index,
            seat: seat,
            sensor: sensor
        );

        return (ok
            ? Success(
                args: in args,
                message: $"[screen.source: {message}]"
            )
            : CommandResult.Error(output: $"[screen.source: {message}]")
        );
    }
    private CommandResult SourceCapture(int index, WorldPrincipal principal, in WireArgs args) {
        if (args.Count < 3) {
            return CommandResult.Error(output: "[screen.source: capture expects <windowTitle...>]");
        }

        // The window title is every token after <index> <kind> joined with spaces — a title may contain spaces.
        var titleBuilder = new StringBuilder();

        for (var token = 2; (token < args.Count); token++) {
            if (token > 2) {
                _ = titleBuilder.Append(value: ' ');
            }

            _ = titleBuilder.Append(value: args[token].ToString());
        }

        EjectMachineFirst(
            index: index,
            principal: principal
        );

        var (ok, message) = m_binder.TryCapture(
            index: index,
            windowTitle: titleBuilder.ToString()
        );

        return (ok
            ? Success(
                args: in args,
                message: $"[screen.source: {message}]"
            )
            : CommandResult.Error(output: $"[screen.source: {message}]")
        );
    }
    private CommandResult SourceDesktop(int index, WorldPrincipal principal, in WireArgs args) {
        if (args.Count is < 2 or > 3) {
            return CommandResult.Error(output: "[screen.source: desktop expects [monitorIndex]]");
        }

        var monitorIndex = 0;

        if (
            (args.Count == 3) &&
            !args.TryInt(
            index: 2,
            value: out monitorIndex
        )
        ) {
            return CommandResult.Error(output: $"[screen.source: monitorIndex '{args[2].ToString()}' must be an integer]");
        }

        EjectMachineFirst(
            index: index,
            principal: principal
        );

        var (ok, message) = m_binder.TryDesktop(
            index: index,
            monitorIndex: monitorIndex
        );

        return (ok
            ? Success(
                args: in args,
                message: $"[screen.source: {message}]"
            )
            : CommandResult.Error(output: $"[screen.source: {message}]")
        );
    }
    // screen.source <index> <kind> [args…] — the single dispatcher absorbing the five former per-kind verbs
    // (camera/capture/desktop/qr/view). The Control check and index parse run ONCE here, up front, shared by every
    // kind (each former verb ran the identical pair); every kind-specific method below picks up parsing at token 2
    // (index and kind occupy 0 and 1) — the exact same grammar and refusal cases each former verb had, just shifted
    // one position by the inserted <kind> token.
    private CommandResult SourceHandler(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return CommandResult.Error(output: "[screen.source: expected <index> <kind> [args…] — kind is camera | capture | desktop | qr | view]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[screen.source: index '{args[0].ToString()}' must be an integer]");
        }

        var principal = context.ActingPrincipal();

        if (!AllowsControl(
            index: index,
            principal: principal
        )) {
            return Denied(
                index: index,
                principal: principal,
                verb: "screen.source"
            );
        }

        if (args.Is(
            index: 1,
            value: "camera"
        )) {
            return SourceCamera(
                args: in args,
                index: index,
                principal: principal
            );
        }
        if (args.Is(
            index: 1,
            value: "capture"
        )) {
            return SourceCapture(
                args: in args,
                index: index,
                principal: principal
            );
        }
        if (args.Is(
            index: 1,
            value: "probe"
        )) {
            return SourceProbe(
                args: in args,
                index: index,
                principal: principal
            );
        }
        if (args.Is(
            index: 1,
            value: "desktop"
        )) {
            return SourceDesktop(
                args: in args,
                index: index,
                principal: principal
            );
        }
        if (args.Is(
            index: 1,
            value: "qr"
        )) {
            return SourceQr(
                args: in args,
                index: index,
                principal: principal
            );
        }
        if (args.Is(
            index: 1,
            value: "view"
        )) {
            return SourceView(
                args: in args,
                index: index,
                principal: principal
            );
        }

        return CommandResult.Error(output: $"[screen.source: '{args[1].ToString()}' must be camera, capture, desktop, qr, or view]");
    }
    private CommandResult SourceQr(int index, WorldPrincipal principal, in WireArgs args) {
        if (args.Count is < 2 or > 5) {
            return CommandResult.Error(output: "[screen.source: qr expects [payload] [ecLevel] [quietZoneModules]]");
        }

        // No payload: echo the current authoring without changing it (the screen.options / screen.select read-back
        // pattern — the decision surface this verb creates is readable through the same verb that set it).
        if (args.Count == 2) {
            return (m_binder.TryReadQr(
                authoring: out var authoring,
                index: index
            )
                ? new CommandResult(Output: $"[screen.source: {index} qr v{authoring.Version} {QrErrorCorrection.Letter(level: authoring.Level)} mask{authoring.Mask} quietZone={authoring.QuietZoneModules} {authoring.Width}x{authoring.Height} '{authoring.Payload}']")
                : CommandResult.Error(output: $"[screen.source: screen {index} has no QR source]")
            );
        }

        var quietZoneModules = default(int?);

        if (args.Count == 5) {
            if (!args.TryInt(
                index: 4,
                value: out var quietZone
            )) {
                return CommandResult.Error(output: $"[screen.source: quietZoneModules '{args[4].ToString()}' must be an integer]");
            }

            quietZoneModules = quietZone;
        }

        EjectMachineFirst(
            index: index,
            principal: principal
        );

        var (ok, message) = m_binder.TryQr(
            index: index,
            payload: args[2].ToString(),
            ecLevel: ((args.Count >= 4)
            ? args[3].ToString()
            : null),
            quietZoneModules: quietZoneModules
        );

        return (ok
            ? Success(
                args: in args,
                message: $"[screen.source: {message}]"
            )
            : CommandResult.Error(output: $"[screen.source: {message}]")
        );
    }
    private CommandResult SourceProbe(int index, WorldPrincipal principal, in WireArgs args) {
        if (args.Count != 3) {
            return CommandResult.Error(output: "[screen.source: probe expects <probeId>]");
        }

        EjectMachineFirst(
            index: index,
            principal: principal
        );

        var (ok, message) = m_binder.TryProbe(
            index: index,
            id: args[2].ToString()
        );

        return (ok
            ? Success(
                args: in args,
                message: $"[screen.source: {message}]"
            )
            : CommandResult.Error(output: $"[screen.source: {message}]")
        );
    }
    private CommandResult SourceView(int index, WorldPrincipal principal, in WireArgs args) {
        if (args.Count != 3) {
            return CommandResult.Error(output: "[screen.source: view expects <cameraName>]");
        }

        EjectMachineFirst(
            index: index,
            principal: principal
        );

        var (ok, message) = m_binder.TryView(
            index: index,
            cameraName: args[2].ToString()
        );

        return (ok
            ? Success(
                args: in args,
                message: $"[screen.source: {message}]"
            )
            : CommandResult.Error(output: $"[screen.source: {message}]")
        );
    }
    private CommandResult CameraHandler(CommandContext context, WireArgs args) {
        if (CommandResult.RequireNoArguments(args: args, verb: "screen.camera") is { } refusal) {
            return refusal;
        }

        return ((m_binder.DescribeCamera() is { } description)
            ? new CommandResult(Output: $"[screen.camera: {description}]")
            : CommandResult.Error(output: "[screen.camera: no camera feed (bind a camera screen first)]")
        );
    }
    private CommandResult StateHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return CommandResult.Error(output: "[screen.state: expected one <index>]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[screen.state: index '{args[0].ToString()}' must be an integer]");
        }

        if (m_binder.State(index: index) is not { } state) {
            return CommandResult.Error(output: $"[screen.state: no screen {index} declared]");
        }

        var engaged = m_engagement.PlayersOn(screenIndex: index);
        var engagedText = ((engaged.Count > 0)
            ? string.Join(
                separator: "+",
                values: engaged.Select(selector: static entry => (entry.Capture
                ? $"p{entry.Display}"
                : $"p{entry.Display}(mirror)"))
            )
            : "none"
        );
        var builder = new StringBuilder();

        _ = builder.Append(
            provider: CultureInfo.InvariantCulture,
            handler: $"[screen.state: {index} "
        );

        if (state.Assigned) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"assigned {(state.Engine ?? "?")} {((state.Handle != 0)
                ? "bound"
                : "unbound")} frames={state.FramesStepped} pending={state.PendingSteps}/{state.MaximumPendingSteps} backpressure={state.BackpressureEvents} engaged={engagedText}"
            );
        } else {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $"empty {((state.Handle != 0)
                ? "bound"
                : "unbound")} engaged={engagedText}"
            );
        }

        if (state.Cartridge is { } cartridge) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" cartridge {cartridge.Path} hash {cartridge.SourceHash} rom {cartridge.RomHash}"
            );
        }

        // The magazine selector and cable link, when present — one query answers the whole arc.
        if (m_binder.TryMagazine(
            index: index,
            magazine: out var magazine,
            selected: out var selected
        )) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" entry={selected}/{magazine.Entries.Count}"
            );
        }

        if (m_binder.LinkOf(index: index) is { } link) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" link={link}"
            );
        }

        if (state.Fault is { } fault) {
            _ = builder.Append(
                provider: CultureInfo.InvariantCulture,
                handler: $" fault={fault}"
            );
        }

        if (DeclaredScreen(index: index)?.Memory is { Count: > 0 } bindings) {
            _ = builder.Append(value: " memory=");

            for (var bindingIndex = 0; (bindingIndex < bindings.Count); bindingIndex++) {
                var binding = bindings[bindingIndex];

                if (bindingIndex > 0) {
                    _ = builder.Append(value: ',');
                }

                var tag = ((binding.Direction == WorldScreenMemoryDirection.Write) ? 'W' : 'R');
                var text = (m_server.TryMachineMemoryObserved(screen: index, address: binding.Address, direction: binding.Direction, value: out var value)
                    ? value.ToString(provider: CultureInfo.InvariantCulture)
                    : "none"
                );

                _ = builder.Append(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"0x{binding.Address:X4}:{tag}={text}"
                );
            }
        }

        return new CommandResult(Output: builder.Append(value: ']').ToString());
    }
    // A side-effecting verb's success echo, gated on the ack mode: a quiet flood drops it (CommandResult.None).
    private static CommandResult Success(in WireArgs args, string message) {
        return (args.Echo
            ? new CommandResult(Output: message)
            : CommandResult.None
        );
    }
    // Parse a 0x-prefixed (or bare) hex address into a 16-bit value.
    private static bool TryParseHex(ReadOnlySpan<char> token, out ushort value) {
        var span = (token.StartsWith(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "0x"
        )
            ? token[2..]
            : token
        );

        return ushort.TryParse(
            s: span,
            style: NumberStyles.HexNumber,
            provider: CultureInfo.InvariantCulture,
            result: out value
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        foreach (var command in Commands()) {
            yield return ((command.Name is "screen.state" or "screen.peek" or "screen.links" or "world.machines")
                ? command
                : command with { Routing = CommandRouting.Simulation }
            );
        }
    }
}
