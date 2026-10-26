using System.Globalization;
using Puck.Commands;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// <c>view.reseed</c>: restarts a body's presentation-only animation as for a new occupant
/// (<see cref="WorldCreationInstances.Reseed"/>), so a scripted motion repeated from one reset pose looks the same.
/// </summary>
/// <param name="stamps">The stamp pool whose body registrations the verb restarts.</param>
public sealed class WorldReseedCommandModule(WorldCreationInstances stamps) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "view.reseed",
            description: "Restarts a body's presentation-only animation as for a new occupant: view.reseed [<body>] (the 0-based entity index, default 0) seeds its gait drivers' phases, weights and eased speed, its foot plants and steps, and its root and part followers again from the next frame's pose. A teleport (body.pose) of the same occupant keeps its gait phases so it never pops, which makes a scripted motion repeated from one reset pose show a different stride; pin view.reseed beside the body.pose (world.at) and the repeat looks the same. Echoes [view.reseed: body <n> restarted <k> registration(s)]. Session presentation only: nothing the simulation reads or a replay hashes changes.",
            handler: (_, args) => {
                var body = 0;

                if ((args.Count > 1) || ((args.Count == 1) && (!args.TryInt(index: 0, value: out body) || (body < 0)))) {
                    return CommandResult.Usage(form: "[<body>]", verb: "view.reseed");
                }

                return new CommandResult(Output: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"[view.reseed: body {body} restarted {stamps.Reseed(bodyIndex: body)} registration(s)]"
                ));
            },
            routing: CommandRouting.Immediate
        );
    }
}
