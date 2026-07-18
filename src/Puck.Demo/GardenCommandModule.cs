using System.CommandLine;
using Puck.Commands;
using Puck.Demo.Garden;
using Puck.Demo.Overworld;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>The host seam <see cref="GardenCommandModule"/> drives — mirrors
/// <see cref="Puck.Demo.Creator.CompanionCommandModule"/>'s <c>ICompanionHost</c> pattern exactly: the module reaches
/// the live garden state through the root node, never by holding its own reference to <see cref="OverworldFrameSource"/>.</summary>
public interface IGardenHost {
    /// <summary>Plants a garden near player slot 0 (or a fixed workbench-adjacent spot). Returns the slot index, or
    /// -1 when the garden is full, or when the overworld root is not ready.</summary>
    /// <param name="seed">The seed to plant, or <see langword="null"/> to derive one from the current tick.</param>
    int PlantGarden(uint? seed);

    /// <summary>Uproots every planted garden, when the root is ready.</summary>
    void ClearGardens();

    /// <summary>The planted-garden slots, when the root is ready (empty otherwise).</summary>
    IReadOnlyList<OverworldWorld.GardenPlant?> Gardens { get; }

    /// <summary>The sim's current tick, when the root is ready (0 otherwise) — <c>garden.list</c>'s age/stage math.</summary>
    ulong CurrentTick { get; }
}

/// <summary>
/// The console verbs for the deterministic garden: plant a seed, watch it grow over sim ticks, list what's planted,
/// clear the plot. Every verb is scoped to <see cref="OverworldWorld"/>'s planted-garden pool (real sim state —
/// unlike the presentation-only companions this module's shape was copied from); the GROWN GEOMETRY itself is never
/// touched here — that is <see cref="GardenRenderer"/>'s job, driven purely by what this module plants.
/// </summary>
internal sealed class GardenCommandModule(IRenderNode rootNode) : ICommandModule {
    // The render node is at its analyzer coupling ceiling and cannot implement IGardenHost itself — every authoring
    // surface reaches its composition point through ICreatorModeHost.CreatorFrameSource instead, adapted here
    // (mirrors CompanionCommandModule.FrameSourceHost).
    private sealed class FrameSourceHost(Overworld.ICreatorModeHost creatorHost) : IGardenHost {
        /// <inheritdoc/>
        public int PlantGarden(uint? seed) =>
            (creatorHost.CreatorFrameSource?.PlantGarden(seed: seed) ?? -1);
        /// <inheritdoc/>
        public void ClearGardens() =>
            creatorHost.CreatorFrameSource?.ClearGardens();
        /// <inheritdoc/>
        public IReadOnlyList<OverworldWorld.GardenPlant?> Gardens =>
            (creatorHost.CreatorFrameSource?.Gardens ?? []);
        /// <inheritdoc/>
        public ulong CurrentTick =>
            (creatorHost.CreatorFrameSource?.CurrentTickState().Tick ?? 0UL);
    }

    private readonly IGardenHost? m_host = (((rootNode as Overworld.ICreatorModeHost) is { } creatorHost) ? new FrameSourceHost(creatorHost: creatorHost) : null);

    private const string HostUnavailable = "[garden: unavailable — the overworld is not the active root]";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Plants a garden seed near the player (a fixed spot near the workbench when no player is present): garden.plant [seed] (a deterministic seed derives from the current tick when omitted — never wall-clock).",
            handler: WithHostArgs(handler: Plant),
            name: "garden.plant"
        );
        yield return Plain(
            description: $"Lists every planted garden: seed, age in ticks, and growth stage (up to {OverworldWorld.MaxGardens} at once).",
            handler: WithHost(handler: List),
            name: "garden.list"
        );
        yield return Plain(
            description: "Uproots every planted garden.",
            handler: WithHost(handler: Clear),
            name: "garden.clear"
        );
    }

    private Func<CommandContext, CommandResult> WithHost(Func<IGardenHost, string> handler) =>
        CommandAvailability.WithTarget(getTarget: () => m_host, handler: handler, unavailableMessage: HostUnavailable);
    private Func<CommandContext, string[], CommandResult> WithHostArgs(Func<IGardenHost, string[], string> handler) =>
        CommandAvailability.WithTargetArgs(getTarget: () => m_host, handler: handler, unavailableMessage: HostUnavailable);
    private static string Plant(IGardenHost host, string[] args) {
        uint? seed = null;

        if ((args.Length > 0) && uint.TryParse(s: args[0], result: out var parsed)) {
            seed = parsed;
        }

        var slot = host.PlantGarden(seed: seed);

        if (slot < 0) {
            return $"[garden.plant: the garden is full ({OverworldWorld.MaxGardens}/{OverworldWorld.MaxGardens}) — garden.clear first]";
        }

        var planted = host.Gardens[slot];

        return $"[garden.plant: #{(slot + 1)} seed=0x{planted!.Value.Seed:X8}]";
    }
    private static string List(IGardenHost host) {
        var gardens = host.Gardens;
        var lines = new List<string>();

        for (var slot = 0; (slot < gardens.Count); slot++) {
            if (gardens[slot] is not { } planted) {
                continue;
            }

            var age = (host.CurrentTick - planted.PlantedTick);
            var structure = GardenTreeGenerator.Generate(seed: planted.Seed, worstCase: false);
            var stage = Math.Min(val1: structure.MaxDepth, val2: (int)(age / GardenTreeGenerator.TicksPerStage));

            lines.Add(item: $"#{(slot + 1)} seed=0x{planted.Seed:X8} age={age} stage={stage}/{structure.MaxDepth}");
        }

        return ((lines.Count == 0)
            ? "[garden.list: none planted — garden.plant [seed] grows one]"
            : $"[garden.list: {string.Join(separator: ", ", values: lines)}]");
    }
    private static string Clear(IGardenHost host) {
        host.ClearGardens();

        return "[garden.clear: every garden uprooted]";
    }

    // A no-argument console verb (mirrors CompanionCommandModule.Plain).
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    // An argument-taking console verb: one trailing token list, parsed by the handler (mirrors
    // CompanionCommandModule.WithArgs).
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
