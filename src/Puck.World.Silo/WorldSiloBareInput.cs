using Puck.Commands;

namespace Puck.World.Silo;

/// <summary>The bare <see cref="IInputBindings"/>/<see cref="ICommandPrincipalResolver"/> pair
/// <see cref="WorldSiloSimulation"/>'s registration needs to satisfy <c>Puck.Launcher</c>'s router/simulation
/// pairing rule — the silo embodies no local seats and drives no physical input, so every slot binds nothing and
/// claims no principal.</summary>
internal sealed class WorldSiloBareInputBindings : IInputBindings {
    public IReadOnlyList<CommandBinding>? Resolve(int slot, string source) => null;
}
internal sealed class WorldSiloBarePrincipalResolver : ICommandPrincipalResolver {
    public CommandPrincipal PrincipalOf(int slot) => CommandPrincipal.Console;
}
