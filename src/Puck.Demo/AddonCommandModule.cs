using System.CommandLine;

using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>
/// The <c>addon</c> console verbs — list / enable / disable the run document's WASM addons in-session (the unification
/// contract: reachable over the on-screen panel AND stdin, never a <c>--flag</c>):
/// <list type="bullet">
/// <item><c>addon</c> / <c>addon list</c> — one line per loaded addon (petname, hash, slot, last-tick fuel, state).</item>
/// <item><c>addon enable &lt;name&gt;</c> — re-instantiate the addon's store, clearing any sticky fault; it resumes.</item>
/// <item><c>addon disable &lt;name&gt;</c> — administratively disable the addon; its ghost goes idle.</item>
/// <item><c>addon reload &lt;name&gt;</c> — re-read the module from disk, recompile, and swap in a fresh store: the
/// in-session edit loop (same roster body, new brain). The petname changes when the content does.</item>
/// </list>
/// Reaches the loaded addon set through <see cref="IAddonControlHost"/> (implemented by <see cref="OverworldFrameSource"/>),
/// resolved lazily off the overworld render node exactly like <see cref="OverworldControlCommandModule"/> reaches
/// <see cref="IOverworldControlHost"/> — null (and every verb reports unavailable) for a non-overworld root or before
/// the node's first frame builds the frame source.
/// </summary>
internal sealed class AddonCommandModule : ICommandModule {
    // The overworld root, captured at construction. The addon runtime is built by the graph builder and reachable off
    // the node from its first frame (unlike the lazily-built frame source), through the same ICreatorModeHost seam the
    // overworld control verbs use. Null for any non-overworld root.
    private readonly Overworld.ICreatorModeHost? m_creatorHost;

    public AddonCommandModule(IRenderNode rootNode) {
        m_creatorHost = (rootNode as Overworld.ICreatorModeHost);
    }

    // The addon runtime seam, resolved through the node's ICreatorModeHost — null for a non-overworld root, or an
    // overworld run that declares no addons.
    private IAddonControlHost? Host => m_creatorHost?.AddonControl;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Lists / enables / disables / hot-reloads the run's WASM addons: addon [list] | addon enable <name> | addon disable <name> | addon reload <name>.",
            handler: (_, args) => new CommandResult(Dispatch(args: args)),
            name: "addon"
        );
    }

    // Parses the one trailing token list into a list / enable / disable action (usage strings beat parser errors on a
    // game console), then delegates to the primitive-typed host seam.
    private string Dispatch(string[] args) {
        if (Host is not { } host) {
            return "[addon: unavailable — the overworld is not the active root]";
        }

        if ((args.Length == 0) || string.Equals(a: args[0], b: "list", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return host.ListAddons();
        }

        if (string.Equals(a: args[0], b: "reload", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            if (args.Length < 2) {
                return "[addon reload: usage — addon reload <name>]";
            }

            return host.ReloadAddon(name: args[1]);
        }

        var enable = string.Equals(a: args[0], b: "enable", comparisonType: StringComparison.OrdinalIgnoreCase);

        if (!enable && !string.Equals(a: args[0], b: "disable", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return "[addon: usage — addon [list] | addon enable <name> | addon disable <name> | addon reload <name>]";
        }

        if (args.Length < 2) {
            return $"[addon {args[0]}: usage — addon {args[0]} <name>]";
        }

        return host.SetAddonEnabled(enabled: enable, name: args[1]);
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
