namespace Puck.World.Protocol;

/// <summary>
/// The closed screen-runtime union a <see cref="WorldSubmissionPayload.ScreenOp"/> submission carries —
/// <c>screen.select</c>/<c>.link</c>/<c>.unlink</c> travel as this leaf through the one ordered domain, landing in
/// <c>Server.WorldServer.Machines</c>. Applied synchronously at submit, exactly like
/// <see cref="WorldCommand"/>/<see cref="WorldGrant"/> — never buffered to the tick boundary — because
/// <c>body.engage</c>'s auto-insert precheck submits a <see cref="Select"/> immediately ahead of the
/// <see cref="WorldCommand.ComposeControl"/> that follows it in the same batch, and the second submission must observe the
/// first's effect. Deliberately narrower than <see cref="WorldDefinition.Screens"/>'
/// <c>UpsertScreen</c>/<c>RemoveScreen</c> mutations, which are document-only (what a screen declares). A screen boots
/// no machine of its own: content mounts only into a declared machine, through its provider operations.
/// </summary>
public abstract record WorldScreenOp {
    private WorldScreenOp() {
    }

    /// <summary>Advances a screen's source magazine to <paramref name="Entry"/> — the runtime <c>screen.select</c>
    /// path. The selector always moves; the binder applies the selected entry's source on the presentation side.</summary>
    /// <param name="Index">The engine screen-surface index.</param>
    /// <param name="Entry">The 0-based magazine entry to select.</param>
    public sealed record Select(int Index, int Entry) : WorldScreenOp;
    /// <summary>Establishes (or records dormant) a runtime cable link over two or more declared screens' machines —
    /// the <c>screen.link</c> path.</summary>
    /// <param name="Name">The link's stable name.</param>
    /// <param name="Members">The engine screen indices in cable order.</param>
    public sealed record Link(string Name, IReadOnlyList<int> Members) : WorldScreenOp;
    /// <summary>Severs a runtime cable link by name — the <c>screen.unlink</c> path.</summary>
    /// <param name="Name">The link name.</param>
    public sealed record Unlink(string Name) : WorldScreenOp;
}
