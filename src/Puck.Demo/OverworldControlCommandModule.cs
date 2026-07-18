using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using static Puck.Commands.CommandArgs;

namespace Puck.Demo;

/// <summary>
/// The scripted-driving + observability console verbs — the agent-facing control plane for the overworld demo.
/// Every verb wraps EXISTING overworld machinery through <see cref="IOverworldControlHost"/> (the primitive-typed
/// host seam, the CA1506 escape) so a piped stdin script drives the whole run and reads the echoed results:
/// <list type="bullet">
/// <item><c>reveal [world|editor]</c> — force a fourth-wall reveal now (default <c>world</c>; <c>editor</c> = the
/// in-session authoring unlock, an independent ladder rung that coexists with the world reveal).</item>
/// <item><c>boot &lt;i&gt;</c> — boot console i.</item>
/// <item><c>eject &lt;i&gt;</c> — eject console i (remove its cart and power it off; the reverse of boot).</item>
/// <item><c>win &lt;i&gt;</c> — force console i's game to its win (write its authored victory bytes into the win region);
/// win a whole meta group and the room's REAL XOR opens the editor — the one verb that drives "complete X games → the
/// editor reveal" end to end without gameplay input.</item>
/// <item><c>cart &lt;i&gt; &lt;type&gt;</c> — set console i's selected cart type (0..12), live-swapping if booted.</item>
/// <item><c>player.add</c> / <c>join</c> — add one scripted player.</item>
/// <item><c>player.move &lt;slot&gt; &lt;x&gt; &lt;z&gt;</c> — teleport a player to a room-local XZ (a tick-boundary sim
/// op, refused rather than clamped when the destination is blocked).</item>
/// <item><c>link &lt;i&gt; &lt;j&gt;</c> — mark consoles i and j a linked serial-cable pair.</item>
/// <item><c>state</c> — echo a one-line world-state summary (hash, mode, booted mask, players, frame/tick) for assertions.</item>
/// <item><c>render-scale [tier]</c> — set the world render-scale quality tier live
/// (native/three-quarter/half/quarter/eighth), scaling the settled revealed room; no argument echoes the current tier +
/// valid set (the durable form is the run-doc <c>revealedRenderScale</c> field).</item>
/// <item><c>room.bench [n|abort]</c> — the fixed-camera perf-bench channel: pins the camera over the live revealed
/// room (no program swap) for n produced frames (default ~300) and echoes one median/min/p95-per-pass summary line.</item>
/// <item><c>capture &lt;png&gt;</c> — capture the next frame to a path.</item>
/// <item><c>step &lt;n&gt;</c> / <c>settle</c> — the observability KEYSTONE: DEFER the rest of the piped script by n
/// produced frames (<c>step</c>) or until the screen-layout/reveal transitions quiesce (<c>settle</c>), so a
/// <c>boot 0</c> / <c>step 30</c> / <c>capture x.png</c> script lands the shot on the settled frame it asked for.</item>
/// </list>
/// The drain model this rides: <see cref="TextCommandSource.Collect"/> submits EVERY queued line each frame (before
/// the frame's <c>ProduceFrame</c>). <c>step</c>/<c>settle</c> arm a HOLD gate (<see cref="TextCommandSource.HoldGate"/>)
/// the drain honors — while held the drain dequeues nothing, so the lines AFTER the gate wait on the frame boundary
/// and resume in FIFO order once the gate releases. When the root is not the overworld, every verb returns
/// "[verb: unavailable — the overworld is not the active root]" and the gate never holds.
/// </summary>
internal sealed class OverworldControlCommandModule : ICommandModule {
    // A script never hangs the run: settle (and any single step) release no later than this many produced frames after
    // they arm, even if the layout never reports settled — a loud fallback beats a wedged pipe.
    private const int MaxGateFrames = 600;

    // The overworld root, captured at construction (the frame source it composes — the actual control host — does not
    // exist until the node's first ProduceFrame, so resolve THROUGH it lazily, exactly like WorldCommandModule reaches
    // the world scene). Null for any non-overworld root — every verb then reports unavailable and the gate never holds.
    private readonly Overworld.ICreatorModeHost? m_creatorHost;
    // The HOLD gate's live state: the produced-frame index the current step/settle releases AT (-1 = not holding), the
    // safety-cap frame a settle releases AT no matter what (int.MaxValue = no cap, for a plain step), and whether the
    // current hold is a settle (waits on LayoutSettled) rather than a plain frame count.
    private int m_releaseAtFrame = -1;
    private int m_capFrame = int.MaxValue;
    private bool m_settleHold;
    // How many consecutive frames the startup hold (waiting for the overworld's frame source to be built) has held —
    // bounded by MaxGateFrames so a root that never comes alive can't wedge the pipe forever.
    private int m_startupHeldFrames;

    public OverworldControlCommandModule(IRenderNode rootNode) {
        m_creatorHost = (rootNode as Overworld.ICreatorModeHost);
    }

    // The live control host (the frame source), resolved lazily through the node's one blessed seam — null until the
    // node's first ProduceFrame builds the frame source, and for a non-overworld root.
    private IOverworldControlHost? Host => (m_creatorHost?.CreatorFrameSource as IOverworldControlHost);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Forces a fourth-wall reveal now: reveal [world|editor] (default world — the same path a machine's win fires; editor = the authoring unlock).",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "reveal")
                : (TryParseRevealKind(args: args, kind: out var kind)
                    ? host.RequestRevealNow(kind: kind)
                    : "[reveal: usage — reveal [world|editor]]"))),
            name: "reveal"
        );
        yield return WithArgs(
            description: "Boots a console by index: boot <i> (inserts its selected cart and powers it on).",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "boot")
                : (TryParseIndex(args: args, at: 0, value: out var index)
                    ? host.BootConsole(index: index)
                    : "[boot: usage — boot <i>]"))),
            name: "boot"
        );
        yield return WithArgs(
            description: "Ejects a console by index: eject <i> (removes its cart and powers it off — the reverse of boot).",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "eject")
                : (TryParseIndex(args: args, at: 0, value: out var ejectIndex)
                    ? host.EjectConsole(index: ejectIndex)
                    : "[eject: usage — eject <i>]"))),
            name: "eject"
        );
        yield return WithArgs(
            description: "Forces a console's game to its WIN: win <i> — writes its authored victory bytes into the win region so the room's meta XOR counts it. Win a whole meta group to open the editor (the workshop). Lets a script drive 'complete X games -> the editor' end to end without gameplay input.",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "win")
                : (TryParseIndex(args: args, at: 0, value: out var winIndex)
                    ? host.WinConsole(index: winIndex)
                    : "[win: usage — win <i>]"))),
            name: "win"
        );
        yield return WithArgs(
            description: "Sets a cabinet's selected cart type (0..12), live-swapping if booted: cart <i> <type>.",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "cart")
                : ((TryParseIndex(args: args, at: 0, value: out var console) && TryParseIndex(args: args, at: 1, value: out var type))
                    ? host.SetCartType(index: console, type: type)
                    : "[cart: usage — cart <i> <type> (type 0..12)]"))),
            name: "cart"
        );
        yield return Plain(
            description: "Adds one scripted player to the room (padless; drives no input).",
            handler: _ => new CommandResult((Host?.AddScriptedPlayer() ?? Unavailable(verb: "player.add"))),
            name: "player.add",
            aliases: ["join"]
        );
        yield return WithArgs(
            description: "Teleports a player to a room-local XZ (Y holds at floor height): player.move <slot> <x> <z>. Refused (the body is left untouched) when the destination is blocked by the walk grid or a console/shelf keep-out.",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "player.move")
                : ((TryParseIndex(args: args, at: 0, value: out var moveSlot) && TryParseFloats(args: args, count: 2, start: 1, values: out var xz))
                    ? host.MovePlayer(slot: moveSlot, x: xz[0], z: xz[1])
                    : "[player.move: usage — player.move <slot> <x> <z>]"))),
            name: "player.move"
        );
        yield return WithArgs(
            description: "Marks two consoles a linked serial-cable pair: link <i> <j> (re-issue on the same already-linked pair to UNLINK — the console mirror of the in-game Link toggle).",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "link")
                : ((TryParseIndex(args: args, at: 0, value: out var first) && TryParseIndex(args: args, at: 1, value: out var second))
                    ? host.LinkConsoles(first: first, second: second)
                    : "[link: usage — link <i> <j>]"))),
            name: "link"
        );
        yield return WithArgs(
            description: "Streams a console's completed serial transfers to stdout: serial.watch <i> (each byte finally shifted through the link — either role — echoes a '[serial.watch i] 0x..' line as it lands). Re-issue on the same cabinet to STOP (the mirror of the link toggle). The watch survives a cart swap / re-boot and pauses while the cabinet is ejected.",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "serial.watch")
                : (TryParseIndex(args: args, at: 0, value: out var watchIndex)
                    ? host.WatchSerial(index: watchIndex)
                    : "[serial.watch: usage — serial.watch <i>]"))),
            name: "serial.watch"
        );
        yield return WithArgs(
            description: "Drives a scripted joypad tape onto a console: press <i> <keys...> (e.g. 'press 0 up a*4' or 'press 1 a - a - a'). Keys a/b/start/select/up/down/left/right joined by '+'; *N holds N frames; xN repeats a step; none/- releases. A linked cabinet drives its pair in lockstep; an owned or unbooted cabinet is refused; the cabinet seats back at the shared timeline head when the tape ends.",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "press")
                : ((TryParseIndex(args: args, at: 0, value: out var pressIndex) && (args.Length > 1))
                    ? host.PressConsole(index: pressIndex, script: string.Join(separator: ' ', values: args[1..]))
                    : "[press: usage — press <i> <keys[*frames][xrepeats]> ... (e.g. press 0 up a*4)]"))),
            name: "press"
        );
        yield return Plain(
            description: "Echoes a one-line world-state summary (hash, mode, booted mask, players, frame/tick) for scripted assertions.",
            handler: _ => new CommandResult((Host?.DescribeState() ?? Unavailable(verb: "state"))),
            name: "state"
        );
        yield return WithArgs(
            description: "Shows or hides the diegetic console terminal — the in-room CRT that mirrors this console: terminal on|off (default on; a dev assist — display only this tier).",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "terminal")
                : (TryParseOnOff(args: args, value: out var visible)
                    ? host.SetTerminalVisible(visible: visible)
                    : "[terminal: usage — terminal on|off]"))),
            name: "terminal"
        );
        yield return WithArgs(
            description: "Shows or hides the diegetic UI action bar — the camera-rig-mounted mirror of the overlay binding bar: ui.diegetic on|off (default on; the overlay bar stays either way — they coexist this tier).",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "ui.diegetic")
                : (TryParseOnOff(args: args, value: out var visible)
                    ? host.SetDiegeticUiVisible(visible: visible)
                    : "[ui.diegetic: usage — ui.diegetic on|off]"))),
            name: "ui.diegetic"
        );
        yield return WithArgs(
            description: "Sets the world render-scale quality tier live: render-scale [native|three-quarter|half|quarter|eighth] — scales the settled revealed room (the most expensive view), trading softness for frame cost (~ scale^2). No argument echoes the current tier and the valid set. The durable form is the run-doc revealedRenderScale field.",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "render-scale")
                : host.SetRenderScaleTier(name: ((args.Length > 0) ? args[0] : null)))),
            name: "render-scale"
        );
        yield return WithArgs(
            description: "Runs the REVEALED-ROOM fixed-camera perf bench: room.bench [n|abort]. Pins the camera to a fixed deterministic pose framing the lit room (a booted cabinet's CRT in view, so the sun soft-shadow path is exercised exactly as ordinary play) over the ACTUAL live content -- no program swap, no simulation touch -- holds it for n produced frames (default ~300; 'abort' cancels a run in flight and releases the pin). On completion echoes ONE summary line to stdout: median/min/p95 per render pass + frame + the live render-scale tier + the beam pass singled out again as the DVFS-clock canary (only within-session paired runs at matched beam clocks compare cleanly). Zero cost while idle.",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "room.bench")
                : host.RoomBench(args: args))),
            name: "room.bench"
        );
        yield return WithArgs(
            description: "Captures the next frame to a path: capture <png> (the directory is created; the shot lands next frame).",
            handler: (_, args) => new CommandResult(((Host is not { } host)
                ? Unavailable(verb: "capture")
                : ((args.Length > 0)
                    ? host.RequestCaptureTo(path: args[0])
                    : "[capture: usage — capture <png>]"))),
            name: "capture"
        );
        yield return WithArgs(
            description: "Defers the rest of the piped script by n produced frames: step <n> (the observability keystone for scripted determinism).",
            handler: (_, args) => new CommandResult(((Host is null)
                ? Unavailable(verb: "step")
                : (TryParseCount(args: args, value: out var count)
                    ? ArmStep(count: count)
                    : "[step: usage — step <n> (n >= 1)]"))),
            name: "step"
        );
        yield return Plain(
            description: "Defers the rest of the piped script until the screen-layout / reveal transitions have quiesced (no active easing).",
            handler: _ => new CommandResult(((Host is null) ? Unavailable(verb: "settle") : ArmSettle())),
            name: "settle"
        );
    }

    /// <summary>The step/settle HOLD predicate the stdin text source consults each frame's Collect (before that frame's
    /// ProduceFrame) — wired onto <see cref="TextCommandSource.HoldGate"/> by <see cref="OverworldControlGateInstaller"/>
    /// after the DI graph is built (the module cannot take the text source directly: TextCommandSource →
    /// CommandRegistry → every ICommandModule is a cycle). Returns true to keep the remaining queued script lines
    /// waiting. Two independent holds compose:
    /// <list type="number">
    /// <item>STARTUP: the piped script's lines are all enqueued at once, and the FIRST Collect runs before the node's
    /// first ProduceFrame builds the frame source (the control host) — so with no gate the whole script would drain
    /// against a null host and every verb would report "unavailable". While this IS the overworld root but its host
    /// isn't built yet, hold — the script waits for the overworld to come alive — bounded by a startup cap so a
    /// stuck/never-ready root can't wedge the pipe.</item>
    /// <item>STEP/SETTLE: once a step/settle verb arms, hold until the produced-frame target (step) or the settled
    /// layout (settle) is reached, bounded by m_capFrame so a never-quiescing layout can't wedge the pipe either.</item>
    /// </list></summary>
    public bool ShouldHold() {
        if (m_creatorHost is null) {
            return false; // not the overworld root — never hold (the verbs all report unavailable and drain through).
        }

        if (Host is not { } host) {
            // The overworld is still booting (frame source not built). Hold for the script to catch it, up to the cap.
            m_startupHeldFrames++;

            return (m_startupHeldFrames <= MaxGateFrames);
        }

        m_startupHeldFrames = 0;

        if (m_releaseAtFrame < 0) {
            return false; // host is up and no step/settle is armed — drain freely.
        }

        var frame = host.ProducedFramesCount;
        // Step: the frame target alone. Settle: the frame target AND the layout settled, OR the safety cap tripped.
        var released = (m_settleHold
            ? (((frame >= m_releaseAtFrame) && host.LayoutSettled) || (frame >= m_capFrame))
            : (frame >= m_releaseAtFrame));

        if (released) {
            Release();

            return false;
        }

        return true;
    }

    private void Release() {
        m_releaseAtFrame = -1;
        m_capFrame = int.MaxValue;
        m_settleHold = false;
    }

    // Arms a step gate: hold the remaining queued lines until COUNT more frames have been produced. Read the current
    // produced-frame count at arm time; ShouldHold releases once the count advances by count.
    private string ArmStep(int count) {
        var clamped = Math.Clamp(value: count, max: MaxGateFrames, min: 1);

        m_settleHold = false;
        m_releaseAtFrame = (Host!.ProducedFramesCount + clamped);
        m_capFrame = int.MaxValue;

        return $"[step: holding {clamped} frame(s)]";
    }

    // Arms a settle gate: hold until the layout reports settled, or the safety cap (MaxGateFrames frames out) trips.
    // Always yields at least one produced frame first (the +1), so a capture after settle is a fresh frame.
    private string ArmSettle() {
        var frame = Host!.ProducedFramesCount;

        m_settleHold = true;
        m_releaseAtFrame = (frame + 1);
        m_capFrame = (frame + MaxGateFrames);

        return "[settle: holding until the layout quiesces]";
    }
    private static string Unavailable(string verb) =>
        $"[{verb}: unavailable — the overworld is not the active root]";

    // Parses a required console index (a non-negative int) at position `at`.
    private static bool TryParseIndex(string[] args, int at, out int value) {
        value = 0;

        return ((args.Length > at) && TryParseInt(text: args[at], value: out value) && (value >= 0));
    }

    // Parses the OPTIONAL reveal-rung token: no token (or an empty list) defaults to World (a bare
    // `reveal` breaks the fourth wall into the world); "world"/"editor" (case-insensitive) select the rung; any
    // other token is a usage error.
    private static bool TryParseRevealKind(string[] args, out RevealKind kind) {
        kind = RevealKind.World;

        if (args.Length == 0) {
            return true; // bare `reveal` → World.
        }

        if (string.Equals(a: args[0], b: "world", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            kind = RevealKind.World;

            return true;
        }

        if (string.Equals(a: args[0], b: "editor", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            kind = RevealKind.Editor;

            return true;
        }

        return false;
    }

    // Parses a required on/off token (case-insensitive) — the terminal show/hide toggle.
    private static bool TryParseOnOff(string[] args, out bool value) {
        value = false;

        if (args.Length == 0) {
            return false;
        }

        if (string.Equals(a: args[0], b: "on", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            value = true;

            return true;
        }

        return string.Equals(a: args[0], b: "off", comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    // Parses a required positive step count (>= 1).
    private static bool TryParseCount(string[] args, out int value) {
        value = 0;

        return ((args.Length > 0) && TryParseInt(text: args[0], value: out value) && (value >= 1));
    }

    // A no-argument console verb.
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name, IReadOnlyList<string>? aliases = null) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital, aliases: aliases);

    // An argument-taking console verb: one trailing token list, parsed by the handler (uniform + forgiving — usage
    // strings beat parser errors on a game console). Mirrors CreatorCommandModule.WithArgs.
    private static CommandDefinition WithArgs(string description, Func<CommandContext, string[], CommandResult> handler, string name) {
        var rest = new Argument<string[]>(name: "args") {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
        };

        return new CommandDefinition(
            Description: description,
            Handler: context => handler(arg1: context, arg2: (context.Parse?.GetValue(argument: rest) ?? [])),
            Name: name,
            TextCommand: new Command(description: description, name: name) {
                rest,
            },
            ValueKind: CommandValueKind.Digital
        );
    }
}
