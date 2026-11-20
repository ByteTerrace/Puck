using Puck.Commands;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Acquires authority for a registry-admitted operator handler. A normal command context cannot be
    /// supplied here, and the addressed world's live grant is checked even if the capability was retained.</summary>
    /// <param name="context">The operator invocation minted by the command registry.</param>
    /// <returns>The operator capability for this addressed world.</returns>
    public WorldOperatorAuthority AcquireOperatorAuthority(OperatorContext context) {
        ArgumentNullException.ThrowIfNull(context);
        using (EnterAtTickBoundary()) {
            ThrowIfTickPending();
            if (!Grants.IsOperator(context.Invocation.Principal)) {
                throw new InvalidOperationException("The invocation does not operate this world.");
            }
            return new WorldOperatorAuthority(this, context);
        }
    }
}
