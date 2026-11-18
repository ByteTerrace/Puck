using Puck.Commands;

namespace Puck.World.Server;

public sealed partial class WorldServer {
    /// <summary>Acquires only the shape needed to spell a write. Name resolution precedes policy admission;
    /// no declaration is returned before admission, and absent and withheld names have the same answer.
    /// The returned descriptor carries neither cells nor a live binder.</summary>
    /// <param name="context">The ingress-stamped command invocation.</param>
    /// <param name="name">The row the submission names.</param>
    /// <param name="declaration">The detached declaration, when the write footprint reads whole.</param>
    /// <param name="reason">The policy refusal, independent of row existence.</param>
    /// <returns>Whether the caller may acquire this write's declaration.</returns>
    public bool TryAcquireWriteDeclaration(CommandContext context, string name, out StateRowDeclaration? declaration, out string reason) {
        lock (AuthorityGate) {
            declaration = null;
            var view = WriteView(context.Principal);
            if (view.IsAuthority) {
                var row = WorldDefinitionRows.FindStateRow(Definition.State, name);
                if (row is not null) { declaration = StateRowDeclaration.From(row); }
                reason = string.Empty;
                return true;
            }
            var access = ReadForWrite(view);
            try {
                declaration = access.Declaration(access.ResolveRow(name));
            } catch (StateUnavailableException) {
                reason = view.Refusal("mutation", name);
                return false;
            }
            reason = string.Empty;
            return true;
        }
    }
    internal StateReadAccess ReadForWrite(WorldRecipientView view) {
        var arena = Arena;
        return view.IsAuthority ? arena.ReadAccess : arena.RestrictReads(ordinal => view.Admits(arena.Catalog.Descriptors[ordinal].Name));
    }
}
