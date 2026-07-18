using System.CommandLine;
using System.Globalization;
using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Hosting;

namespace Puck.Demo;

/// <summary>The host seam <see cref="RtsCommandModule"/> drives — mirrors <see cref="GardenCommandModule"/>'s
/// <c>IGardenHost</c> pattern exactly: the module reaches the live RTS unit pool through the root node, never by
/// holding its own reference to <see cref="OverworldFrameSource"/>.</summary>
public interface IRtsHost {
    /// <summary>Spawns a unit at an explicit room-local XZ, or (when <paramref name="x"/>/<paramref name="z"/> are
    /// null) at <see cref="Rts.RtsScenario.DefaultSpawnPosition"/>'s next deterministic grid slot.</summary>
    /// <param name="x">The room-local X to spawn at, or <see langword="null"/> for the default grid.</param>
    /// <param name="z">The room-local Z to spawn at, or <see langword="null"/> for the default grid.</param>
    /// <returns>The slot index, or -1 when full or the spawn point is blocked, or when the root is not ready.</returns>
    int SpawnRtsUnit(double? x, double? z);

    /// <summary>Selects every active unit inside the given box.</summary>
    int SelectRtsUnitsInBox(double minX, double minZ, double maxX, double maxZ);

    /// <summary>Orders every selected unit to move to the given room-local XZ.</summary>
    int MoveSelectedRtsUnits(double x, double z);

    /// <summary>Despawns every RTS unit.</summary>
    void ClearRtsUnits();

    /// <summary>The RTS unit pool, when the root is ready (empty otherwise).</summary>
    IReadOnlyList<OverworldWorld.RtsUnit> RtsUnits { get; }
}

/// <summary>
/// Console verbs for the RTS scenario: spawn units, box-select, order a move,
/// list what's on the field, clear it. Every verb is scoped to <see cref="OverworldWorld"/>'s RTS unit pool (real
/// sim state); the drawn tokens themselves are never touched here — that is
/// <see cref="Rts.RtsUnitInstanceEmitter"/>'s job, driven purely by what this module spawns/orders.
/// </summary>
internal sealed class RtsCommandModule(IRenderNode rootNode) : ICommandModule {
    // The render node is at its analyzer coupling ceiling and cannot implement IRtsHost itself — every authoring
    // surface reaches its composition point through ICreatorModeHost.CreatorFrameSource instead, adapted here
    // (mirrors GardenCommandModule.FrameSourceHost).
    private sealed class FrameSourceHost(Overworld.ICreatorModeHost creatorHost) : IRtsHost {
        private int m_spawnOrdinal;

        /// <inheritdoc/>
        public int SpawnRtsUnit(double? x, double? z) {
            if (creatorHost.CreatorFrameSource is not { } frameSource) {
                return -1;
            }

            if ((x is { } explicitX) && (z is { } explicitZ)) {
                return frameSource.SpawnRtsUnit(x: explicitX, z: explicitZ);
            }

            var (defaultX, defaultZ) = Rts.RtsScenario.DefaultSpawnPosition(index: m_spawnOrdinal++);

            return frameSource.SpawnRtsUnit(x: (double)(float)defaultX, z: (double)(float)defaultZ);
        }

        /// <inheritdoc/>
        public int SelectRtsUnitsInBox(double minX, double minZ, double maxX, double maxZ) =>
            (creatorHost.CreatorFrameSource?.SelectRtsUnitsInBox(minX: minX, minZ: minZ, maxX: maxX, maxZ: maxZ) ?? 0);
        /// <inheritdoc/>
        public int MoveSelectedRtsUnits(double x, double z) =>
            (creatorHost.CreatorFrameSource?.MoveSelectedRtsUnits(x: x, z: z) ?? 0);
        /// <inheritdoc/>
        public void ClearRtsUnits() =>
            creatorHost.CreatorFrameSource?.ClearRtsUnits();
        /// <inheritdoc/>
        public IReadOnlyList<OverworldWorld.RtsUnit> RtsUnits =>
            (creatorHost.CreatorFrameSource?.RtsUnits ?? []);
    }

    private readonly IRtsHost? m_host = (((rootNode as Overworld.ICreatorModeHost) is { } creatorHost) ? new FrameSourceHost(creatorHost: creatorHost) : null);

    private const string HostUnavailable = "[rts: unavailable — the overworld is not the active root]";

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return WithArgs(
            description: "Spawns a unit: rts.spawn [x z] (a deterministic grid position is used when omitted). Rejected when full or the spawn point is blocked.",
            handler: WithHostArgs(handler: Spawn),
            name: "rts.spawn"
        );
        yield return WithArgs(
            description: "Selects every unit inside a box: rts.select <minX> <minZ> <maxX> <maxZ>.",
            handler: WithHostArgs(handler: Select),
            name: "rts.select"
        );
        yield return WithArgs(
            description: "Orders every selected unit to move: rts.move <x> <z>.",
            handler: WithHostArgs(handler: Move),
            name: "rts.move"
        );
        yield return Plain(
            description: $"Lists every RTS unit: slot, active/selected, position, order (up to {OverworldWorld.MaxRtsUnits} at once).",
            handler: WithHost(handler: List),
            name: "rts.list"
        );
        yield return Plain(
            description: "Despawns every RTS unit.",
            handler: WithHost(handler: Clear),
            name: "rts.clear"
        );
    }

    private Func<CommandContext, CommandResult> WithHost(Func<IRtsHost, string> handler) =>
        CommandAvailability.WithTarget(getTarget: () => m_host, handler: handler, unavailableMessage: HostUnavailable);
    private Func<CommandContext, string[], CommandResult> WithHostArgs(Func<IRtsHost, string[], string> handler) =>
        CommandAvailability.WithTargetArgs(getTarget: () => m_host, handler: handler, unavailableMessage: HostUnavailable);
    private static string Spawn(IRtsHost host, string[] args) {
        double? x = null;
        double? z = null;

        if ((args.Length >= 2) && double.TryParse(s: args[0], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var parsedX) && double.TryParse(s: args[1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var parsedZ)) {
            x = parsedX;
            z = parsedZ;
        }

        var slot = host.SpawnRtsUnit(x: x, z: z);

        if (slot < 0) {
            return "[rts.spawn: rejected — the field is full or the spawn point is blocked]";
        }

        var unit = host.RtsUnits[slot];

        return $"[rts.spawn: #{slot} at ({(double)(float)unit.X:0.00}, {(double)(float)unit.Z:0.00})]";
    }
    private static string Select(IRtsHost host, string[] args) {
        if ((args.Length < 4)
            || !double.TryParse(s: args[0], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var minX)
            || !double.TryParse(s: args[1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var minZ)
            || !double.TryParse(s: args[2], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var maxX)
            || !double.TryParse(s: args[3], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var maxZ)) {
            return "[rts.select: usage — rts.select <minX> <minZ> <maxX> <maxZ>]";
        }

        var count = host.SelectRtsUnitsInBox(minX: minX, minZ: minZ, maxX: maxX, maxZ: maxZ);

        return $"[rts.select: {count} selected]";
    }
    private static string Move(IRtsHost host, string[] args) {
        if ((args.Length < 2)
            || !double.TryParse(s: args[0], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var x)
            || !double.TryParse(s: args[1], style: NumberStyles.Float, provider: CultureInfo.InvariantCulture, result: out var z)) {
            return "[rts.move: usage — rts.move <x> <z>]";
        }

        var count = host.MoveSelectedRtsUnits(x: x, z: z);

        return ((count == 0) ? "[rts.move: no units selected — rts.select a box first]" : $"[rts.move: {count} unit(s) ordered to ({x:0.00}, {z:0.00})]");
    }
    private static string List(IRtsHost host) {
        var units = host.RtsUnits;
        var lines = new List<string>();

        for (var slot = 0; (slot < units.Count); slot++) {
            var unit = units[slot];

            if (!unit.Active) {
                continue;
            }

            var order = (unit.HasTarget ? $" ->({(double)(float)unit.TargetX:0.00},{(double)(float)unit.TargetZ:0.00})" : "");

            lines.Add(item: $"#{slot}{(unit.Selected ? "*" : "")} ({(double)(float)unit.X:0.00},{(double)(float)unit.Y:0.00},{(double)(float)unit.Z:0.00}){order}");
        }

        return ((lines.Count == 0)
            ? "[rts.list: no units — rts.spawn [x z] fields one]"
            : $"[rts.list: {string.Join(separator: ", ", values: lines)}]");
    }
    private static string Clear(IRtsHost host) {
        host.ClearRtsUnits();

        return "[rts.clear: field cleared]";
    }

    // A no-argument console verb (mirrors GardenCommandModule.Plain).
    private static CommandDefinition Plain(string description, Func<CommandContext, CommandResult> handler, string name) =>
        CommandDefinition.Verb(description: description, handler: handler, name: name, valueKind: CommandValueKind.Digital);

    // An argument-taking console verb: one trailing token list, parsed by the handler (mirrors
    // GardenCommandModule.WithArgs).
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
