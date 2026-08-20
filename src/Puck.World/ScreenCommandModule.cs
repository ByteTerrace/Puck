using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.Assets.Qr;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The diegetic screens' console surface — the wire verbs that boot, eject, and inspect the deterministic machines
/// behind the world's screens. <c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/<c>.options</c>/<c>.link</c>/
/// <c>.unlink</c> submit a <see cref="WorldScreenOp"/> through the ordered submission domain
/// (<see cref="IServerLink.SubmitScreenOp"/>) — <see cref="Server.WorldMachineHost"/> applies it synchronously and
/// authoritatively, so an agent scripts a cabinet over the pipe with no
/// restart and the op reproduces on replay; <c>screen.source &lt;index&gt; &lt;kind&gt; [args…]</c> (kind: camera |
/// capture | desktop | qr | view — absorbing the five former per-kind verbs into one dispatcher) stays genuinely
/// presentation, calling <see cref="WorldScreenBinder"/> directly (never a machine, never tape-covered).
/// <c>screen.state</c>/<c>screen.peek</c>/<c>screen.camera</c> are read-only queries that make the live state
/// pipe-assertable (a booted machine's engine, bound handle, stepped-frame count, engaged players, one memory byte,
/// and the shared camera device's control surface). The world speaks the
/// engine-neutral machine vocabulary — a machine is resolved against a registered engine by id, and each engine owns its
/// own options string. Every verb is wire-native — each failure marks <see cref="CommandResult.IsError"/> so
/// <c>wire.ack quiet</c> drops only successes, and the two queries always echo their data.
/// </summary>
internal sealed class ScreenCommandModule(WorldScreenBinder binder, WorldServer server, IServerLink link) : ICommandModule {
    private readonly WorldScreenBinder m_binder = binder;
    private readonly WorldEngagement m_engagement = server.Engagement;
    private readonly WorldServer m_server = server;
    private readonly IServerLink m_link = link;

    // Advance a magazine selector by delta, wrapping or clamping per the magazine policy.
    private static int Advance(int selected, int delta, int count, bool wrap) {
        if (count <= 0) {
            return 0;
        }

        var next = (selected + delta);

        return (wrap
            ? (((next % count) + count) % count)
            : Math.Clamp(
                max: (count - 1),
                min: 0,
                value: next
            )
        );
    }
    // The Control check over a screen subject, under whichever identity this dispatch's ingress door stamped —
    // a CLIENT-SIDE precheck for a fast, friendly denial; Server.WorldMachineHost's own apply re-checks the
    // identical pair AUTHORITATIVELY for screen-op verbs (see TryApplyScreenOp), so this is defense in depth, not
    // the only gate, exactly like player.engage's own documented precheck/re-check split. Console and every seat
    // hold Control over every screen by the permissive local defaults, so this is transparent until someone narrows
    // the trust (world.grant/world.revoke).
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
            description: "Boots content onto a declared screen, live: screen.insert <index> <contentPath> [engine] [options…] — <index> the engine screen index, <contentPath> a content file (a cartridge ROM), the optional [engine] a registered screen-machine engine id (omit it when one is registered — the mechanical default), and the trailing tokens the engine's own options string (the gaming-brick engine reads dmg|cgb|agb plus dmgspeed). Submits a WorldScreenOp.Insert through the ordered submission domain — Server.WorldMachineHost applies it synchronously and authoritatively, CAS-pinning the exact bytes read (the replay tape's negative control refuses a re-drive whose re-read disagrees); an existing machine on the slot is live-swapped. The server's own loud accept/reject line prints when it applies. Errors on an undeclared screen, an unresolved engine, an unreadable file, or rejected options.",
            handler: InsertHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.source",
            description: "Binds a declared screen's live PRESENTATION source, absorbing the five former per-kind verbs into one: screen.source <index> <kind> [args…] — <kind> is camera | capture | desktop | qr | view, each carrying its own former arg grammar unchanged: camera [color|infrared] (one shared session PER SENSOR — the color and infrared sensors are separate physical devices and stream simultaneously; every camera screen naming a sensor samples that sensor's one feed; default color); capture <windowTitle...> (a case-insensitive substring match, may contain spaces); desktop [monitorIndex] (0-based, default 0 = primary); qr [payload] [ecLevel] [quietZoneModules] (payload a single token; ecLevel one of L|M|Q|H, default M; quietZoneModules default 4 — NO payload echoes the current authoring instead of changing it); view <cameraName> (the jumbotron recursion — one offscreen camera render, budgeted round-robin). Genuinely presentation for every kind (never a machine, never tape-covered) — a booted machine on the slot is ejected FIRST, through the ordered domain, exactly as each former verb did. Errors on an undeclared screen, an unresolved kind, or the kind's own refusal (a missing capture target, an unavailable capture service, an absent sensor device, an unknown camera name, an unrecognized EC-level letter, a negative quiet zone, a payload too large for the encoder).",
            handler: SourceHandler,
            ackOnly: true
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.eject",
            description: "Ejects a screen's live source, live: screen.eject <index>. A booted machine ejects through a WorldScreenOp.Eject submission (ordered domain, tape-covered, the server prints the accept/reject line); the webcam or a window capture ejects directly through the binder (genuinely presentation, unchanged). The slot reverts to its declared test pattern or to the engine's procedural no-signal fallback. Errors on an undeclared screen or a slot with no live source.",
            handler: EjectHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.select",
            description: "Advances a screen's source magazine, live: screen.select <index> [next|prev|<entry>]. No third token echoes the current selection. The selector move ALWAYS submits a WorldScreenOp.Select (ordered domain, tape-covered) — Server.WorldMachineHost boots the entry authoritatively when it is a Machine row; for a non-machine entry (camera/capture/view) the selector still moves authoritatively and this verb ALSO applies the entry locally through the binder (genuinely presentation), so this verb echoes both outcomes. Errors on an undeclared screen, a screen with no magazine, or an out-of-range entry.",
            handler: SelectHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.options",
            description: "Reconfigures a screen's live machine across the engine's options vocabulary, live: screen.options <index> [options…]. No options echoes the machine's current string. With options, submits a WorldScreenOp.SetOptions through the ordered submission domain to retarget the running machine (the dmg|cgb|agb device swap — no reboot, no lost progress); the server's own loud accept/reject line prints when it applies. Errors on an undeclared screen, a slot with no machine, a machine without the reconfigure capability, or rejected options.",
            handler: OptionsHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.link",
            description: "Cable-links two or more declared screens' machines into one deterministically stepped group: screen.link <name> <index> <index> [index…] — the runtime twin of the machine sources' authored cable ports (world.save folds the live set back onto them). Submits a WorldScreenOp.Link through the ordered submission domain; Server.WorldMachineHost applies it authoritatively and the server's own loud line prints the live/dormant outcome. A group whose members cannot currently be linked (a member with no machine, mixed engines, an engine with no linking capability) is recorded DORMANT with a reason. Errors on an undeclared screen, a duplicate member, or a member already in another link.",
            handler: LinkHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.unlink",
            description: "Severs a runtime cable link by name: screen.unlink <name>. Submits a WorldScreenOp.Unlink through the ordered submission domain. Its members resume individual stepping. Errors when no link of that name is live.",
            handler: UnlinkHandler
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
            description: "Echoes every shared camera feed's live control surface, one section per sensor (color | infrared — separate physical devices, streaming simultaneously): screen.camera — the device name, live tier (gpu | cpu | pending), negotiated extent, each device-supported control's current value/mode, device range (with auto capability), and authored document value, plus the authored vendor-extension rows read back raw and a faulted sensor's fault. The pipe-assertable read-back for a camera source row's `controls`/`sensor` members (authored defaults apply at open; an UpsertScreen mutation moves the device live). A query (always echoes, even under wire.ack quiet). Errors when no camera feed was ever attempted.",
            handler: CameraHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.state",
            description: "Echoes a screen's live machine state: screen.state <index> — assigned/empty, the hosting engine id, bound/unbound (a nonzero source handle this frame), the stepped-frame count, and the engaged players. A query (always echoes, even under wire.ack quiet) — the pipe-assertable machine state.",
            handler: StateHandler
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "screen.peek",
            description: "Reads one memory byte from a screen's machine: screen.peek <index> <addr> — <addr> a 0x-prefixed hex machine address (the gaming-brick's work RAM is [0xC000, 0xDFFF]). A read only, never a write into machine state, so a piped proof can assert a game's stored bytes. A query (always echoes). Errors when the screen carries no machine, or its machine has no memory-peek capability.",
            handler: PeekHandler
        );
    }
    private static CommandResult Denied(WorldPrincipal principal, string verb, int index) =>
        // The grant subject is ONE colon-joined token: `screen:{index}`, not `screen {index}` (which the parser refuses
        // for both the split subject and the arity it pushes past).
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

        if (m_server.Machines.HasMachine(index: index)) {
            m_link.SubmitScreenOp(
                op: new WorldScreenOp.Eject(Index: index),
                principal: principal
            );

            return CommandResult.None;
        }

        var (ok, message) = m_binder.TryEject(index: index);

        return (ok
            ? new CommandResult(Output: $"[screen.eject: {message}]")
            : CommandResult.Error(output: $"[screen.eject: {message}]")
        );
    }
    // A machine present on the target index is ejected FIRST, through the ordered domain, so a camera/capture/view
    // bind (all genuinely presentation) never has to reach past this project's own architecture firewall to dispose
    // one — Server.WorldMachineHost owns that lifetime now. A no-op (no submission) when no machine is present.
    private void EjectMachineFirst(int index, WorldPrincipal principal) {
        if (m_server.Machines.HasMachine(index: index)) {
            m_link.SubmitScreenOp(
                op: new WorldScreenOp.Eject(Index: index),
                principal: principal
            );
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
        // Grammar: <index> <contentPath> [engine] [options…]. The first trailing token is the engine id ONLY when it
        // matches a registered engine; otherwise it belongs to the options string and the engine defaults (the sole
        // registered engine). The remaining trailing tokens join, space-separated, into the engine's options string.
        var token = 2;
        string? engineId = null;

        if (
            (token < args.Count) &&
            m_binder.HasEngine(engineId: args[token].ToString())
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

        m_link.SubmitScreenOp(
            op: new WorldScreenOp.Insert(
                ContentPath: contentPath,
                EngineId: engineId,
                Index: index,
                Options: options
            ),
            principal: principal
        );

        return CommandResult.None;
    }
    private CommandResult LinkHandler(CommandContext context, WireArgs args) {
        if (args.Count < 3) {
            return CommandResult.Error(output: "[screen.link: expected <name> <index> <index> [index…]]");
        }

        var name = args[0].ToString();
        var members = new List<int>(capacity: (args.Count - 1));
        var principal = context.ActingPrincipal();

        for (var token = 1; (token < args.Count); token++) {
            if (!args.TryInt(
                index: token,
                value: out var member
            )) {
                return CommandResult.Error(output: $"[screen.link: '{args[token].ToString()}' must be an integer]");
            }

            if (!AllowsControl(
                index: member,
                principal: principal
            )) {
                return Denied(
                    index: member,
                    principal: principal,
                    verb: "screen.link"
                );
            }

            members.Add(item: member);
        }

        m_link.SubmitScreenOp(
            op: new WorldScreenOp.Link(
                Members: members,
                Name: name
            ),
            principal: principal
        );

        return CommandResult.None;
    }
    private CommandResult LinksHandler(CommandContext context, WireArgs args) {
        if (args.Count != 0) {
            return CommandResult.Error(output: "[screen.links: expected no arguments]");
        }

        return new CommandResult(Output: $"[screen.links: {m_binder.DescribeLinks()}]");
    }
    private CommandResult OptionsHandler(CommandContext context, WireArgs args) {
        if (args.Count < 1) {
            return CommandResult.Error(output: "[screen.options: expected <index> [options…]]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[screen.options: index '{args[0].ToString()}' must be an integer]");
        }

        var principal = context.ActingPrincipal();

        if (!AllowsControl(
            index: index,
            principal: principal
        )) {
            return Denied(
                index: index,
                principal: principal,
                verb: "screen.options"
            );
        }

        // No options: echo the machine's current string.
        if (args.Count == 1) {
            return (m_binder.TryReadOptions(
                index: index,
                out var current
            )
                ? new CommandResult(Output: $"[screen.options: {index} '{current}']")
                : CommandResult.Error(output: $"[screen.options: screen {index} has no reconfigurable machine]")
            );
        }

        var optionsBuilder = new StringBuilder();

        for (var token = 1; (token < args.Count); token++) {
            if (optionsBuilder.Length > 0) {
                _ = optionsBuilder.Append(value: ' ');
            }

            _ = optionsBuilder.Append(value: args[token].ToString());
        }

        m_link.SubmitScreenOp(
            op: new WorldScreenOp.SetOptions(
                Index: index,
                Options: optionsBuilder.ToString()
            ),
            principal: principal
        );

        return CommandResult.None;
    }
    private CommandResult PeekHandler(CommandContext context, WireArgs args) {
        if (args.Count != 2) {
            return CommandResult.Error(output: "[screen.peek: expected <index> <addr> — addr a 0x-prefixed hex address]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[screen.peek: index '{args[0].ToString()}' must be an integer]");
        }

        if (!TryParseHex(
            token: args[1],
            value: out var address
        )) {
            return CommandResult.Error(output: $"[screen.peek: addr '{args[1].ToString()}' must be a 0x-prefixed hex address]");
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
    private CommandResult SelectHandler(CommandContext context, WireArgs args) {
        if (args.Count is < 1 or > 2) {
            return CommandResult.Error(output: "[screen.select: expected <index> [next|prev|<entry>]]");
        }

        if (!args.TryInt(
            index: 0,
            value: out var index
        )) {
            return CommandResult.Error(output: $"[screen.select: index '{args[0].ToString()}' must be an integer]");
        }

        var principal = context.ActingPrincipal();

        if (!AllowsControl(
            index: index,
            principal: principal
        )) {
            return Denied(
                index: index,
                principal: principal,
                verb: "screen.select"
            );
        }

        if (!m_binder.TryMagazine(
            index: index,
            magazine: out var magazine,
            selected: out var selected
        )) {
            return CommandResult.Error(output: $"[screen.select: screen {index} has no magazine]");
        }

        // No third token: echo the current selection without moving.
        if (args.Count == 1) {
            return new CommandResult(Output: $"[screen.select: {index} entry {selected}/{magazine.Entries.Count} (unchanged)]");
        }

        var token = args[1].ToString();
        int target;

        if (string.Equals(
            a: token,
            b: "next",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            target = Advance(
                selected: selected,
                delta: 1,
                count: magazine.Entries.Count,
                wrap: magazine.Wrap
            );
        } else if (string.Equals(
            a: token,
            b: "prev",
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            target = Advance(
                selected: selected,
                delta: -1,
                count: magazine.Entries.Count,
                wrap: magazine.Wrap
            );
        } else if (!int.TryParse(
            s: token,
            style: NumberStyles.Integer,
            provider: CultureInfo.InvariantCulture,
            result: out target
        )) {
            return CommandResult.Error(output: $"[screen.select: '{token}' must be next, prev, or an entry index]");
        }

        if (
            (target < 0) ||
            (target >= magazine.Entries.Count)
        ) {
            return CommandResult.Error(output: $"[screen.select: entry {target} is outside 0..{(magazine.Entries.Count - 1)}]");
        }

        // The selector move is ALWAYS a WorldScreenOp.Select submission — Server.WorldMachineHost applies it
        // authoritatively (booting a Machine entry, or simply moving the pointer for a non-machine one) and its own
        // loud accept/reject line prints. A non-machine entry ALSO gets applied locally, right here, since that
        // half is genuinely presentation (see this module's own remarks).
        m_link.SubmitScreenOp(
            op: new WorldScreenOp.Select(
                Entry: target,
                Index: index
            ),
            principal: principal
        );

        if (magazine.Entries[target] is WorldScreenSource.Machine) {
            return CommandResult.None;
        }

        var (ok, message) = m_binder.ApplyNonMachineSource(
            index: index,
            source: magazine.Entries[target]
        );

        return new CommandResult(Output: $"[screen.select: {index} entry {target}/{magazine.Entries.Count} {(ok
            ? message
            : $"selected (presentation apply failed: {message})")}]") {
            IsError = !ok,
        };
    }
    private CommandResult SourceCamera(int index, WorldPrincipal principal, in WireArgs args) {
        if (args.Count > 3) {
            return CommandResult.Error(output: "[screen.source: camera expects at most one [color|infrared] sensor token]");
        }

        // The optional sensor token: color (the default) or infrared — the sensor-camera stream a Windows Hello
        // capable device exposes as its own capture device, streaming BESIDE the color feed rather than replacing it.
        var sensor = WorldCameraSensor.Color;

        if (args.Count == 3) {
            var token = args[2].ToString();

            if (string.Equals(a: token, b: "infrared", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                sensor = WorldCameraSensor.Infrared;
            } else if (!string.Equals(a: token, b: "color", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return CommandResult.Error(output: $"[screen.source: unknown camera sensor '{token}' — expected color or infrared]");
            }
        }

        EjectMachineFirst(
            index: index,
            principal: principal
        );

        var (ok, message) = m_binder.TryCamera(
            index: index,
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
        if (args.Count != 0) {
            return CommandResult.Error(output: "[screen.camera: expected no arguments]");
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
    private CommandResult UnlinkHandler(CommandContext context, WireArgs args) {
        if (args.Count != 1) {
            return CommandResult.Error(output: "[screen.unlink: expected one <name>]");
        }

        var name = args[0].ToString();
        var principal = context.ActingPrincipal();

        // Control over every member is required to sever (the grant table's Screen(index)-for-every-member rule) — the
        // same gate screen.link applies when the link is formed. A missing link falls through to the server's own
        // honest "no link" refusal.
        if (m_binder.TryReadLinkMembers(
            members: out var members,
            name: name
        )) {
            foreach (var member in members) {
                if (!AllowsControl(
                    index: member,
                    principal: principal
                )) {
                    return Denied(
                        index: member,
                        principal: principal,
                        verb: "screen.unlink"
                    );
                }
            }
        }

        m_link.SubmitScreenOp(
            op: new WorldScreenOp.Unlink(Name: name),
            principal: principal
        );

        return CommandResult.None;
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        foreach (var command in Commands()) {
            yield return ((command.Name is "screen.state" or "screen.peek" or "screen.links")
                ? command
                : command with { Routing = CommandRouting.Simulation }
            );
        }
    }
}
