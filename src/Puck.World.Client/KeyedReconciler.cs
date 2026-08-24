namespace Puck.World.Client;

/// <summary>Diff-by-stable-key reconciliation for one live entry against its currently authored row: locate the row,
/// release the entry when it is gone, release and recreate when the row's content changed enough to require it, or
/// update the entry in place otherwise. Called at a delivery boundary, never per frame — the delegate arguments may
/// close over per-call state freely.</summary>
internal static class KeyedReconciler {
    /// <summary>Reconciles one live entry against its current row.</summary>
    /// <typeparam name="TLive">The live registration type.</typeparam>
    /// <typeparam name="TRow">The authored row/content type the live entry tracks.</typeparam>
    /// <param name="live">The current live entry.</param>
    /// <param name="tryFindRow">Resolves the entry's current authored row, or <see langword="null"/> when it no
    /// longer exists (or is no longer valid for the caller's pool).</param>
    /// <param name="isRecreateRequired">Whether the row's content changed enough to require releasing the live
    /// entry and constructing a fresh one, rather than updating the existing one in place.</param>
    /// <param name="recreate">Constructs a fresh live entry from the row.</param>
    /// <param name="update">Updates the live entry in place from an unchanged row.</param>
    /// <returns>The entry to keep in the slot (<paramref name="live"/>, or a fresh instance on recreate), or
    /// <see langword="null"/> to release the slot.</returns>
    public static TLive? Reconcile<TLive, TRow>(TLive live, Func<TLive, TRow?> tryFindRow, Func<TLive, TRow, bool> isRecreateRequired, Func<TRow, TLive> recreate, Action<TLive, TRow> update)
        where TRow : struct {
        if (tryFindRow(live) is not { } row) {
            return default;
        }

        if (isRecreateRequired(live, row)) {
            return recreate(row);
        }

        update(live, row);

        return live;
    }
}
