using System.CommandLine;
using Puck.Assets;
using Puck.Commands;
using Puck.Hosting;
using static Puck.Commands.CommandArgs;

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

    // "[companion: unavailable...]" is the shared host-gate message — companions have no on/off mode of their own
    // (unlike creator/tracker), so CommandAvailability is given no isActive/inactiveMessage; only the host gate
    // (m_host non-null) applies. Each handler pattern-matches the exact host members it needs out of m_host.
    private const string HostUnavailable = "[companion: unavailable — the overworld is not the active root]";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: $"Loads a companion into the room: companion.add <creationNameOrHash> [swim] (up to {CompanionState.MaxCompanions}). The name/hash resolves CAS-first, then ./creations/.",
            handler: WithHostArgs(handler: Add),
            name: "companion.add"
        );
        yield return WithArgs(
            description: "Removes a companion: companion.del <index|all> (1-based index, matching companion.list).",
            handler: WithHostArgs(handler: Delete),
            name: "companion.del"
        );
        yield return Plain(
            description: "Lists the room's live companions.",
            handler: WithHost(handler: List),
            name: "companion.list"
        );
        yield return WithArgs(
            description: "Sets a companion's face feed: companion.face <index> <feedName|auto> (auto = hail-radius tune-in; a feed name pins it — e.g. emotes, lure, or any registered feed).",
            handler: WithHostArgs(handler: Face),
            name: "companion.face"
        );
    }

    private Func<CommandContext, CommandResult> WithHost(Func<ICompanionHost, string> handler) =>
        CommandAvailability.WithTarget(getTarget: () => m_host, handler: handler, unavailableMessage: HostUnavailable);
    private Func<CommandContext, string[], CommandResult> WithHostArgs(Func<ICompanionHost, string[], string> handler) =>
        CommandAvailability.WithTargetArgs(getTarget: () => m_host, handler: handler, unavailableMessage: HostUnavailable);
    private static string Add(ICompanionHost host, string[] args) {
        if (host is not { Companions: { } roster, CompanionStore: { } store, CompanionBounds: { } bounds }) {
            return HostUnavailable;
        }

        if (args.Length == 0) {
            return "[companion.add: give a creation name or CAS hash]";
        }

        if (roster.Companions.Count >= CompanionState.MaxCompanions) {
            return $"[companion.add: the room already has {CompanionState.MaxCompanions} companions — companion.del one first]";
        }

        try {
            if (CompanionState.ResolveDocument(nameOrHash: args[0], store: store) is not { } document) {
                return $"[companion.add: nothing readable at '{args[0]}']";
            }

            // The explicit "swim" token FORCES swimming (the assist override); its absence DEFERS to the creation's
            // behavior manifest (null) — a fish declared swim in its manifest swims without the token.
            var swimToken = ((args.Length > 1) && string.Equals(a: args[1], b: "swim", comparisonType: StringComparison.OrdinalIgnoreCase));
            var rosterIndex = roster.Companions.Count;
            var companion = new CompanionState(bounds: bounds, document: document, isSwimmer: (swimToken ? true : (bool?)null), spawnPosition: host.CompanionSpawnPosition(rosterIndex: rosterIndex));

            return (roster.Add(companion: companion)
                ? $"[companion.add: '{document.Name}' joined the room ({roster.Companions.Count}/{CompanionState.MaxCompanions}){(companion.IsSwimmer ? " — swimming" : "")}]"
                : $"[companion.add: the room already has {CompanionState.MaxCompanions} companions — companion.del one first]");
        } catch (Exception exception) when (CommandArgs.IsMalformedInput(exception: exception)) {
            return $"[companion.add: '{args[0]}' is unreadable — {exception.Message}]";
        }
    }
    private static string Delete(ICompanionHost host, string[] args) {
        if (host is not { Companions: { } roster }) {
            return HostUnavailable;
        }

        if (args.Length == 0) {
            return "[companion.del: give an index (companion.list) or 'all']";
        }

        if (string.Equals(a: args[0], b: "all", comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return $"[companion.del: removed {roster.Clear()} companion(s)]";
        }

        if (!TryParseInt(text: args[0], value: out var oneBased)) {
            return "[companion.del: give a numeric index (companion.list) or 'all']";
        }

        return (roster.RemoveAt(index: (oneBased - 1))
            ? $"[companion.del: removed #{oneBased}]"
            : $"[companion.del: no companion at #{oneBased} — companion.list shows what's loaded]");
    }
    private static string List(ICompanionHost host) {
        if (host is not { Companions: { } roster }) {
            return HostUnavailable;
        }

        if (roster.Companions.Count == 0) {
            return "[companion.list: none loaded — companion.add <name> joins one]";
        }

        var lines = new List<string>(capacity: roster.Companions.Count);

        for (var index = 0; (index < roster.Companions.Count); index++) {
            var companion = roster.Companions[index];

            lines.Add(item: $"#{(index + 1)} '{companion.Document.Name}'{(companion.IsSwimmer ? " (swim)" : "")}{(companion.HasFace ? $" [face:{companion.CurrentFaceFeed}{((companion.PinnedFaceFeed is { } pinned) ? $" pinned:{pinned}" : " auto")}]" : "")}");
        }

        return $"[companion.list: {string.Join(separator: ", ", values: lines)}]";
    }
    private static string Face(ICompanionHost host, string[] args) {
        if (host is not { Companions: { } roster }) {
            return HostUnavailable;
        }

        if ((args.Length < 2) || !TryParseInt(text: args[0], value: out var oneBased)) {
            return "[companion.face: usage — companion.face <index> <feedName|auto>]";
        }

        var index = (oneBased - 1);

        if ((index < 0) || (index >= roster.Companions.Count)) {
            return $"[companion.face: no companion at #{oneBased} — companion.list shows what's loaded]";
        }

        // "auto" resumes the hail-radius tune-in; any other token PINS the face to that feed name (the host's
        // named-feed registry decides what it resolves to — an unknown feed just shows the flat fallback).
        var auto = string.Equals(a: args[1], b: "auto", comparisonType: StringComparison.OrdinalIgnoreCase);
        var feedName = (auto ? null : args[1]);

        roster.Companions[index].SetFaceFeed(feedName: feedName);

        return $"[companion.face: #{oneBased} = {(feedName ?? "auto")}]";
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
