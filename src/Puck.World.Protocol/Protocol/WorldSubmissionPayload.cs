namespace Puck.World.Protocol;

/// <summary>
/// The closed payload union a <see cref="SubmissionEnvelope"/> carries — every non-intent submission kind
/// (command/grant/revoke/session/rebuild/mutation/undo/composition/lever/query/addon-lifecycle/screen-op), one case
/// per kind, so the server's ONE ordered domain never has to split by message kind to know what it is holding. Per-tick
/// <see cref="IntentSubmission"/> is NOT a case here — intents keep their own separate buffer (arrival order is
/// fold-independent), never this envelope.
/// </summary>
public abstract record WorldSubmissionPayload {
    private WorldSubmissionPayload() {
    }

    /// <summary>A validated authority command for one entity (<see cref="Protocol.WorldCommand"/>).</summary>
    /// <param name="Value">The command.</param>
    public sealed record Command(WorldCommand Value) : WorldSubmissionPayload;
    /// <summary>A capability grant (the <c>world.grant</c> half).</summary>
    /// <param name="Value">The grant row.</param>
    public sealed record Grant(WorldGrant Value) : WorldSubmissionPayload;
    /// <summary>A capability revoke (the <c>world.revoke</c> half).</summary>
    /// <param name="Value">The grant (capability + subject) to revoke.</param>
    public sealed record Revoke(WorldGrant Value) : WorldSubmissionPayload;
    /// <summary>A session/identity request (join/leave/profile/population/peer-source/player-section).</summary>
    /// <param name="Value">The request.</param>
    public sealed record Session(SessionRequest Value) : WorldSubmissionPayload;
    /// <summary>A whole-document rebuild-and-swap (<c>world.reset</c>/<c>world.load</c>/<c>world.reload</c>) — one of
    /// the three document sources named by <see cref="Protocol.WorldRebuildRequest.Kind"/>.</summary>
    /// <param name="Value">The rebuild request.</param>
    public sealed record Rebuild(WorldRebuildRequest Value) : WorldSubmissionPayload;
    /// <summary>A live world-document edit (one <see cref="Protocol.WorldMutation"/> kind).</summary>
    /// <param name="Value">The mutation.</param>
    public sealed record Mutation(WorldMutation Value) : WorldSubmissionPayload;
    /// <summary>A journal undo request (<c>world.undo</c>).</summary>
    /// <param name="Count">How many trailing mutations to undo (at least 1).</param>
    public sealed record Undo(int Count) : WorldSubmissionPayload;
    /// <summary>A live window-composition override (<c>view.override layout</c>/<c>view.override camera</c>).</summary>
    /// <param name="Value">The composition override.</param>
    public sealed record Composition(WorldComposition Value) : WorldSubmissionPayload;
    /// <summary>A live session-lever write (<c>world.volume</c>, <c>world.shadows</c>, …).</summary>
    /// <param name="Value">The lever write.</param>
    public sealed record Lever(WorldSessionLever Value) : WorldSubmissionPayload;
    /// <summary>A read-back query (<c>player.where</c>, <c>player.channels</c>, …).</summary>
    /// <param name="Value">The query.</param>
    public sealed record Query(WorldQuery Value) : WorldSubmissionPayload;
    /// <summary>A live addon-runtime lifecycle change (<c>world.addon.mount</c>/<c>world.addon.unmount</c>) — see
    /// <see cref="Protocol.WorldAddonLifecycle"/>.</summary>
    /// <param name="Value">The lifecycle action.</param>
    public sealed record AddonLifecycle(WorldAddonLifecycle Value) : WorldSubmissionPayload;
    /// <summary>A live screen-machine lifecycle change (<c>screen.insert</c>/<c>.eject</c>/<c>.select</c>/
    /// <c>.options</c>/<c>.link</c>/<c>.unlink</c>) — see <see cref="Protocol.WorldScreenOp"/>.</summary>
    /// <param name="Value">The screen op.</param>
    public sealed record ScreenOp(WorldScreenOp Value) : WorldSubmissionPayload;
    /// <summary>A subject-bearing target-register write.</summary>
    /// <param name="Value">The proposed designation.</param>
    public sealed record Designation(WorldDesignation Value) : WorldSubmissionPayload;
}
