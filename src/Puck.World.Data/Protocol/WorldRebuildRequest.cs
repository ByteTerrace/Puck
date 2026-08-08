namespace Puck.World.Protocol;

/// <summary>The three document sources a live rebuild-and-swap can install from — one small closed set behind ONE
/// mechanism (compose → validate → capacity → solids → swap → journal reset), rather than three independently-shaped
/// verbs. See <see cref="WorldRebuildRequest"/> for the payload each kind carries.</summary>
public enum WorldRebuildKind {
    /// <summary>Rebuild from the server's own in-memory BASE (<c>world.reset</c>) — the last <c>world.save</c>, or the
    /// boot document if never saved. Carries no document of its own; the server resolves it from its own base.</summary>
    Reset,

    /// <summary>Rebuild from a DIFFERENT document read from disk (<c>world.load &lt;path&gt; [force]</c>).</summary>
    Load,

    /// <summary>Re-read the CURRENT document origin from disk and rebuild from it (<c>world.reload</c>) — the artist
    /// external-edit loop.</summary>
    Reload,
}

/// <summary>One <c>world.reset</c>/<c>world.load</c>/<c>world.reload</c> request — the payload
/// <see cref="WorldSubmissionPayload.Rebuild"/> carries through the ordered submission domain. The console-side
/// Immediate handler resolves everything I/O-shaped (reading and validating a file) BEFORE this ever reaches the
/// domain, exactly like the prior <c>world.load</c> did — this record only carries the RESULT of that resolution
/// (or, for <see cref="WorldRebuildKind.Reset"/>, nothing at all: the base is server state, never client-supplied).</summary>
/// <param name="Kind">Which of the three document sources this request rebuilds from.</param>
/// <param name="Definition">The document to install — required for <see cref="WorldRebuildKind.Load"/>/
/// <see cref="WorldRebuildKind.Reload"/>, always <see langword="null"/> for <see cref="WorldRebuildKind.Reset"/>
/// (the server supplies its own base).</param>
/// <param name="PathHint">The origin path this request names, for the completion echo and (on
/// <see cref="WorldRebuildKind.Load"/>/<see cref="WorldRebuildKind.Reload"/> success) the new save/reload target —
/// required for those two kinds, always <see langword="null"/> for <see cref="WorldRebuildKind.Reset"/>.</param>
/// <param name="Force">For <see cref="WorldRebuildKind.Load"/> only: overrides the dirty-journal guard (a live edit
/// since the last save/reset would otherwise be silently discarded). Ignored by <see cref="WorldRebuildKind.Reset"/>
/// (reset IS the discard) and by <see cref="WorldRebuildKind.Reload"/> (the artist external-edit loop is expected to
/// discard the in-session journal on every reload).</param>
/// <param name="ContentHash">The canonical <c>sha256-64/{hex}</c> pin of the EXACT bytes the console read off disk —
/// required for <see cref="WorldRebuildKind.Load"/>/<see cref="WorldRebuildKind.Reload"/> (computed once, at the same
/// read that produced <see cref="Definition"/>), always <see langword="null"/> for <see cref="WorldRebuildKind.Reset"/>
/// (there is no file; the server computes the base's own canonical-bytes hash at apply time — see
/// <c>WorldServer.ApplyRebuild</c>). This is the replay tape's CAS pin: a re-drive refuses BY NAME when a re-read of
/// the same path (or a re-hash of the re-driven run's own base) disagrees with what was recorded, rather than
/// silently reproducing a base or file that has moved since the recording was made.</param>
public sealed record WorldRebuildRequest(WorldRebuildKind Kind, WorldDefinition? Definition, string? PathHint, bool Force, string? ContentHash = null);
