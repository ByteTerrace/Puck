using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using static Puck.Demo.CommandArgs;

namespace Puck.Demo;

/// <summary>
/// The scripted-driving + observability console verbs — the agent-facing control plane for the overworld demo.
/// Every verb wraps EXISTING overworld machinery through <see cref="IOverworldControlHost"/> (the primitive-typed
/// host seam, the CA1506 escape) so a piped stdin script drives the whole run and reads the echoed results:
/// <list type="bullet">
/// <item><c>reveal [world|editor]</c> — force a fourth-wall reveal now (default <c>world</c>; <c>editor</c> = the
/// in-session authoring unlock, an independent ladder rung that coexists with the world reveal).</item>
/// <item><c>boot &lt;i&gt;</c> — boot console i.</item>
/// <item><c>win &lt;i&gt;</c> — force console i's game to its win (write its authored victory bytes into the win region);
/// win a whole meta group and the room's REAL XOR opens the editor — the one verb that drives "complete X games → the
/// editor reveal" end to end without gameplay input.</item>
/// <item><c>cart &lt;i&gt; &lt;type&gt;</c> — set console i's selected cart type (0..10), live-swapping if booted.</item>
/// <item><c>player.add</c> / <c>join</c> — add one scripted player.</item>
/// <item><c>link &lt;i&gt; &lt;j&gt;</c> — mark consoles i and j a linked serial-cable pair.</item>
/// <item><c>state</c> — echo a one-line world-state summary (hash, mode, booted mask, players, frame/tick) for assertions.</item>
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
            handler: (_, args) => new CommandResult((Host is not { } host)
                ? Unavailable(verb: "reveal")
                : (TryParseRevealKind(args: args, kind: out var kind)
                    ? host.RequestRevealNow(kind: kind)
                    : "[reveal: usage — reveal [world|editor]]")),
            name: "reveal"
        );
        yield return WithArgs(
            description: "Boots a console by index: boot <i> (inserts its selected cart and powers it on).",
            handler: (_, args) => new CommandResult((Host is not { } host)
                ? Unavailable(verb: "boot")
                : (TryParseIndex(args: args, at: 0, value: out var index)
                    ? host.BootConsole(index: index)
                    : "[boot: usage — boot <i>]")),
            name: "boot"
        );
        yield return WithArgs(
            description: "Forces a console's game to its WIN: win <i> — writes its authored victory bytes into the win region so the room's meta XOR counts it. Win a whole meta group to open the editor (the workshop). Lets a script drive 'complete X games -> the editor' end to end without gameplay input.",
            handler: (_, args) => new CommandResult((Host is not { } host)
                ? Unavailable(verb: "win")
                : (TryParseIndex(args: args, at: 0, value: out var winIndex)
                    ? host.WinConsole(index: winIndex)
                    : "[win: usage — win <i>]")),
            name: "win"
        );
        yield return WithArgs(
            description: "Sets a cabinet's selected cart type (0..10), live-swapping if booted: cart <i> <type>.",
            handler: (_, args) => new CommandResult((Host is not { } host)
                ? Unavailable(verb: "cart")
                : ((TryParseIndex(args: args, at: 0, value: out var console) && TryParseIndex(args: args, at: 1, value: out var type))
                    ? host.SetCartType(index: console, type: type)
                    : "[cart: usage — cart <i> <type> (type 0..10)]")),
            name: "cart"
        );
        yield return Plain(
            description: "Adds one scripted player to the room (padless; drives no input).",
            handler: _ => new CommandResult(Host?.AddScriptedPlayer() ?? Unavailable(verb: "player.add")),
            name: "player.add",
            aliases: ["join"]
        );
        yield return WithArgs(
            description: "Marks two consoles a linked serial-cable pair: link <i> <j>.",
            handler: (_, args) => new CommandResult((Host is not { } host)
                ? Unavailable(verb: "link")
                : ((TryParseIndex(args: args, at: 0, value: out var first) && TryParseIndex(args: args, at: 1, value: out var second))
                    ? host.LinkConsoles(first: first, second: second)
                    : "[link: usage — link <i> <j>]")),
            name: "link"
        );
        yield return Plain(
            description: "Echoes a one-line world-state summary (hash, mode, booted mask, players, frame/tick) for scripted assertions.",
            handler: _ => new CommandResult(Host?.DescribeState() ?? Unavailable(verb: "state")),
            name: "state"
        );
        yield return WithArgs(
            description: "Captures the next frame to a path: capture <png> (the directory is created; the shot lands next frame).",
            handler: (_, args) => new CommandResult((Host is not { } host)
                ? Unavailable(verb: "capture")
                : ((args.Length > 0)
                    ? host.RequestCaptureTo(path: args[0])
                    : "[capture: usage — capture <png>]")),
            name: "capture"
        );
        yield return WithArgs(
            description: "Defers the rest of the piped script by n produced frames: step <n> (the observability keystone for scripted determinism).",
            handler: (_, args) => new CommandResult((Host is null)
                ? Unavailable(verb: "step")
                : (TryParseCount(args: args, value: out var count)
                    ? ArmStep(count: count)
                    : "[step: usage — step <n> (n >= 1)]")),
            name: "step"
        );
        yield return Plain(
            description: "Defers the rest of the piped script until the screen-layout / reveal transitions have quiesced (no active easing).",
            handler: _ => new CommandResult((Host is null) ? Unavailable(verb: "settle") : ArmSettle()),
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

    // Parses the OPTIONAL reveal-rung token: no token (or an empty list) defaults to World (back-compat — a bare
    // `reveal` still breaks the fourth wall into the world); "world"/"editor" (case-insensitive) select the rung; any
    // other token is a usage error.
    private static bool TryParseRevealKind(string[] args, out RevealKind kind) {
        kind = RevealKind.World;

        if (args.Length == 0) {
            return true; // bare `reveal` → World (back-compat).
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
