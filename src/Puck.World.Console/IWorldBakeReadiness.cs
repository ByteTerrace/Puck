namespace Puck.World;

/// <summary>
/// Whether the presentation's mesh-path bakes are settled: every residency it renders (the world's own, each session
/// view's, each routed scene's and each infinity layer's) has baked every group of the program it last drew, so no
/// lattice is owed and no contour still runs. It is the fact the console waits on (<c>world.wait bakes</c>), so a
/// script that reads the baked frame waits on the bake rather than on a tick count. A host that composes no renderer
/// registers none. Members are read on the host pump.
/// </summary>
public interface IWorldBakeReadiness {
    /// <summary>Gets how many frames the world's presentation has captured, the mark a wait arms from.</summary>
    long Frames { get; }

    /// <summary>Returns whether the bakes are settled for the content as it stood at <paramref name="mark"/>: the world's
    /// presentation has captured a frame since, holds the program that frame's content called for (no rebuild in flight)
    /// and has baked it, so a document change delivered before the mark (a <c>world.reload</c>, a row edit) is in the
    /// bake rather than still queued. Allocates nothing, so a hold polls it every drain.</summary>
    /// <param name="mark">The value of <see cref="Frames"/> when the wait armed.</param>
    /// <returns><see langword="true"/> when the bakes are settled for that content.</returns>
    bool IsSettledSince(long mark);

    /// <summary>Returns why the bakes are not settled for the content as it stood at <paramref name="mark"/>, or
    /// <see langword="null"/> once they are. It builds a string, so a caller reads it only to report.</summary>
    /// <param name="mark">The value of <see cref="Frames"/> when the wait armed.</param>
    /// <returns>The reason, naming the residencies still baking, or <see langword="null"/>.</returns>
    string? PendingReasonSince(long mark);
}
