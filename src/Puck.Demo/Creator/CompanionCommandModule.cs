using System.CommandLine;
using Puck.Assets;
using Puck.Commands;
using Puck.Hosting;

namespace Puck.Demo.Creator;

/// <summary>The host seam <see cref="CompanionCommandModule"/> drives — kept separate from
/// <see cref="Overworld.ICreatorModeHost"/>'s internal interface (this module's owning file cannot edit the render
/// node), so the render node's patch block (delivered alongside this file, not applied by it) implements this
/// interface explicitly. Mirrors <see cref="CreatorCommandModule"/>'s <c>Scene</c> composition-point pattern: the
/// module reaches the live roster/store through the root node, never by holding its own reference to either.</summary>
public interface ICompanionHost {
    /// <summary>The room's live companion roster, when the overworld root is ready.</summary>
    CompanionRoster? Companions { get; }

    /// <summary>The content-addressed store companions resolve <c>companion.add</c>'s hash argument against, when
    /// the root is ready.</summary>
    ContentAddressedStore? CompanionStore { get; }

    /// <summary>The authoring workbench region — companions steer inside this exact bound (the same region
    /// <see cref="CreatorScene"/> clamps shapes to), when the root is ready.</summary>
    WorkbenchRegion? CompanionBounds { get; }

    /// <summary>A spawn position for a newly-added companion (typically the workbench's spawn point, offset per
    /// roster slot so several companions do not stack), when the root is ready.</summary>
    /// <param name="rosterIndex">The new companion's index (0-based) — lets the host fan out spawn points.</param>
    System.Numerics.Vector3 CompanionSpawnPosition(int rosterIndex);
}

/// <summary>
/// The console-assist verbs for companions — sculpted creations living in the room as presentation-only creatures,
/// the seam the three flagship avatars will inhabit. Mirrors <see cref="CreatorCommandModule"/>'s shape (a thin
/// module wrapping availability-guarded handlers) exactly. Every verb is scoped to the LIVE
/// <see cref="CompanionRoster"/> the host composes; nothing here ever touches the deterministic sim.
/// </summary>
internal sealed class CompanionCommandModule(IRenderNode rootNode) : ICommandModule {
    // The render node is at its analyzer coupling ceiling and cannot implement ICompanionHost itself — every
    // authoring surface reaches its composition point through ICreatorModeHost.CreatorFrameSource instead (the one
    // host seam the node already pays for), adapted here.
    private sealed class FrameSourceHost(Overworld.ICreatorModeHost creatorHost) : ICompanionHost {
        /// <inheritdoc/>
        public CompanionRoster? Companions => creatorHost.CreatorFrameSource?.Companions;
        /// <inheritdoc/>
        public ContentAddressedStore? CompanionStore => creatorHost.CreatorFrameSource?.WorldContentStore;
        /// <inheritdoc/>
        public WorkbenchRegion? CompanionBounds => creatorHost.CreatorFrameSource?.CompanionBounds;

        /// <inheritdoc/>
        public System.Numerics.Vector3 CompanionSpawnPosition(int rosterIndex) =>
            (creatorHost.CreatorFrameSource?.CompanionSpawnPosition(rosterIndex: rosterIndex) ?? default);
    }

    private readonly ICompanionHost? m_host = (((rootNode as Overworld.ICreatorModeHost) is { } creatorHost) ? new FrameSourceHost(creatorHost: creatorHost) : null);

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: $"Loads a companion into the room: companion.add <creationNameOrHash> [swim] (up to {CompanionState.MaxCompanions}). The name/hash resolves CAS-first, then ./creations/.",
            handler: (_, args) => Add(args: args),
            name: "companion.add"
        );
        yield return WithArgs(
            description: "Removes a companion: companion.del <index|all> (1-based index, matching companion.list).",
            handler: (_, args) => Delete(args: args),
            name: "companion.del"
        );
        yield return Plain(
            description: "Lists the room's live companions.",
            handler: _ => List(),
            name: "companion.list"
        );
        yield return WithArgs(
            description: "Sets a companion's face channel: companion.face <index> <emote|lure|auto> (auto = hail-radius tune-in; emote/lure pin it).",
            handler: (_, args) => Face(args: args),
            name: "companion.face"
        );
    }

    private CommandResult Add(string[] args) {
        if (m_host is not { Companions: { } roster, CompanionStore: { } store, CompanionBounds: { } bounds }) {
            return new CommandResult("[companion: unavailable — the overworld is not the active root]");
        }

        if (args.Length == 0) {
            return new CommandResult("[companion.add: give a creation name or CAS hash]");
        }

        if (roster.Companions.Count >= CompanionState.MaxCompanions) {
            return new CommandResult($"[companion.add: the room already has {CompanionState.MaxCompanions} companions — companion.del one first]");
        }

        if (CompanionState.ResolveDocument(nameOrHash: args[0], store: store) is not { } document) {
            return new CommandResult($"[companion.add: nothing readable at '{args[0]}']");
        }

        var swim = ((args.Length > 1) && string.Equals(a: args[1], b: "swim", comparisonType: StringComparison.OrdinalIgnoreCase));
        var rosterIndex = roster.Companions.Count;
        var companion = new CompanionState(bounds: bounds, document: document, isSwimmer: swim, spawnPosition: m_host.CompanionSpawnPosition(rosterIndex: rosterIndex));

        return new CommandResult(roster.Add(companion: companion)
            ? $"[companion.add: '{document.Name}' joined the room ({roster.Companions.Count}/{CompanionState.MaxCompanions}){(swim ? " — swimming" : "")}]"
            : $"[companion.add: the room already has {CompanionState.MaxCompanions} companions — companion.del one first]");
    }

    private CommandResult Delete(string[] args) {
        if (m_host is not { Companions: { } roster }) {
            return new CommandResult("[companion: unavailable — the overworld is not the active root]");
        }

        if (args.Length == 0) {
            return new CommandResult("[companion.del: give an index (companion.list) or 'all']");
        }

        if (string.Equals(a: args[0], b: "all", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return new CommandResult($"[companion.del: removed {roster.Clear()} companion(s)]");
        }

        if (!int.TryParse(s: args[0], result: out var oneBased)) {
            return new CommandResult("[companion.del: give a numeric index (companion.list) or 'all']");
        }

        return new CommandResult(roster.RemoveAt(index: (oneBased - 1))
            ? $"[companion.del: removed #{oneBased}]"
            : $"[companion.del: no companion at #{oneBased} — companion.list shows what's loaded]");
    }

    private CommandResult List() {
        if (m_host is not { Companions: { } roster }) {
            return new CommandResult("[companion: unavailable — the overworld is not the active root]");
        }

        if (roster.Companions.Count == 0) {
            return new CommandResult("[companion.list: none loaded — companion.add <name> joins one]");
        }

        var lines = new List<string>(capacity: roster.Companions.Count);

        for (var index = 0; (index < roster.Companions.Count); index++) {
            var companion = roster.Companions[index];

            lines.Add(item: $"#{index + 1} '{companion.Document.Name}'{(companion.IsSwimmer ? " (swim)" : "")}{(companion.ScreenFaced ? $" [face:{companion.FaceChannel}/{companion.FacePin}]" : "")}");
        }

        return new CommandResult($"[companion.list: {string.Join(separator: ", ", values: lines)}]");
    }

    private CommandResult Face(string[] args) {
        if (m_host is not { Companions: { } roster }) {
            return new CommandResult("[companion: unavailable — the overworld is not the active root]");
        }

        if ((args.Length < 2) || !int.TryParse(s: args[0], result: out var oneBased)) {
            return new CommandResult("[companion.face: usage — companion.face <index> <emote|lure|auto>]");
        }

        var index = (oneBased - 1);

        if ((index < 0) || (index >= roster.Companions.Count)) {
            return new CommandResult($"[companion.face: no companion at #{oneBased} — companion.list shows what's loaded]");
        }

        if (ParsePin(name: args[1]) is not { } pin) {
            return new CommandResult("[companion.face: one of emote, lure, auto]");
        }

        roster.Companions[index].SetFacePin(pin: pin);

        return new CommandResult($"[companion.face: #{oneBased} = {pin}]");
    }

    private static CompanionFacePin? ParsePin(string name) {
        return name.ToLowerInvariant() switch {
            "emote" or "emotes" => CompanionFacePin.Emotes,
            "lure" or "luecam" or "lurecam" => CompanionFacePin.LureCam,
            "auto" => CompanionFacePin.Auto,
            _ => null,
        };
    }

    // A no-argument console verb (mirrors CreatorCommandModule.Plain).
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    // An argument-taking console verb: one trailing token list, parsed by the handler (mirrors
    // CreatorCommandModule.WithArgs).
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
