using Puck.Commands;
using Puck.World.Server;

namespace Puck.World.Testing;

/// <summary>Resolves every invocation to one row, as the desktop's boot console authority does.</summary>
/// <param name="instance">The row every invocation resolves to.</param>
internal class FixedAuthority(WorldInstance instance) : IWorldConsoleAuthority {
    public bool TryResolve(CommandContext context, out WorldInstance resolved, out string refusal) {
        resolved = instance;
        refusal = string.Empty;

        return true;
    }
}
/// <summary>Answers every row with one wait gate.</summary>
/// <param name="gate">The gate every row waits on.</param>
internal sealed class FixedGate(WorldConsoleWaitGate gate) : IWorldWaitGateResolver {
    public WorldConsoleWaitGate GateFor(WorldInstance instance) => gate;
}
