using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;
using static Puck.Commands.CommandArgs;

namespace Puck.Demo;

/// <summary>
/// The machine-neutral time-travel console verb family — ONE <c>rewind</c>/<c>rewind.status</c>/<c>runahead</c>/
/// <c>fastforward</c> surface for every machine in the session, routed by the target token:
/// <list type="bullet">
/// <item><b>Cabinet-scoped</b> (a leading console index, the <c>boot</c>/<c>win</c>/<c>press</c> convention):
/// <c>rewind 0 60</c>, <c>rewind.status 0</c>, <c>runahead 0 5</c>, <c>fastforward 0 4</c> drive an overworld HGB
/// cabinet through <see cref="IOverworldControlHost"/> (queued, applied by the node next frame, outcome echoed to
/// stdout — the <c>press</c> pattern).</item>
/// <item><b>Scene-scoped</b> (no index): <c>rewind 60</c>, <c>rewind.status</c>, <c>runahead 5</c>,
/// <c>fastforward 4</c> drive the fullscreen AGB debug scene's single machine through
/// <see cref="AgbDebug.AgbDebugService"/> (synchronous — the scene machine is render-thread-owned).</item>
/// </list>
/// Both routes drive the same build-once <c>MachineTimeTravel</c> layer. Usage-string-on-bad-input, never throws.
/// </summary>
internal sealed class TimeTravelCommandModule(AgbDebug.AgbDebugService service, IRenderNode rootNode) : ICommandModule {
    private readonly ICreatorModeHost? m_creatorHost = (rootNode as ICreatorModeHost);

    // The live overworld control host, resolved lazily through the node's blessed seam (null until the node's first
    // ProduceFrame builds the frame source, and for a non-overworld root) — the OverworldControlCommandModule shape.
    private IOverworldControlHost? Host => (m_creatorHost?.CreatorFrameSource as IOverworldControlHost);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Machine-neutral rewind through the delta-ring history. Cabinet: rewind <i> <frames|Ns|on|off> (e.g. rewind 0 60). AGB debug scene: rewind <frames|Ns> (e.g. rewind 50 or rewind 2s). Clamps to the oldest captured frame; echoes how many frames it moved back plus the landed frame/cycle.",
            handler: (_, args) => new CommandResult(Route(args: args, op: "rewind", minimumCabinetArgs: 2, scene: static (service, rest) => service.Rewind(argument: rest))),
            name: "rewind"
        );
        yield return WithArgs(
            description: "Reports a machine's rewind-ring depth, time span, memory footprint, and live runahead/fast-forward settings. Cabinet: rewind.status <i>. AGB debug scene: rewind.status.",
            handler: (_, args) => new CommandResult(Route(args: args, op: "status", minimumCabinetArgs: 1, scene: static (service, _) => service.RewindStatus())),
            name: "rewind.status"
        );
        yield return WithArgs(
            description: "Machine-neutral runahead: keeps one persistent lookahead fork n frames ahead on predicted (held) input and shows ITS framebuffer; the real machine stays the authoritative tick-locked sim and the only audio source. Cabinet: runahead <i> <n|off>. AGB debug scene: runahead <n|off>.",
            handler: (_, args) => new CommandResult(Route(args: args, op: "runahead", minimumCabinetArgs: 2, scene: static (service, rest) => service.Runahead(argument: rest))),
            name: "runahead"
        );
        yield return WithArgs(
            description: "Machine-neutral fast-forward: multiplies the per-frame cycle budget (host-level, never a timing hack inside the core) so the machine advances factor-times realtime while presentation frames are skipped. Cabinet: fastforward <i> <factor|off>. AGB debug scene: fastforward <factor|off>.",
            handler: (_, args) => new CommandResult(Route(args: args, op: "fastforward", minimumCabinetArgs: 2, scene: static (service, rest) => service.FastForward(argument: rest))),
            name: "fastforward"
        );
    }

    // The one routing rule: a leading integer token with enough arguments for the cabinet form targets an overworld
    // cabinet; anything else drives the AGB debug scene. `rewind 60` is therefore the scene (one token), `rewind 0 60`
    // cabinet 0 — the same explicit-index convention every cabinet-scoped verb uses.
    private string Route(string[] args, string op, int minimumCabinetArgs, Func<AgbDebug.AgbDebugService, string, string> scene) {
        if ((args.Length >= minimumCabinetArgs) && TryParseInt(text: args[0], value: out var index) && (index >= 0)) {
            return ((Host is { } host)
                ? host.TimeTravel(index: index, op: op, argument: ((args.Length > 1) ? string.Join(separator: ' ', values: args[1..]) : ""))
                : "[cabinet time-travel: unavailable — the overworld is not the active root]");
        }

        return scene(arg1: service, arg2: ((args.Length > 0) ? string.Join(separator: ' ', values: args) : ""));
    }

    // An argument-taking console verb: one trailing token list, parsed by the handler (uniform + forgiving — usage
    // strings beat parser errors on a game console). Mirrors OverworldControlCommandModule.WithArgs.
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
