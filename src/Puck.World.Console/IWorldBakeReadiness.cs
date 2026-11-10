namespace Puck.World;

/// <summary>
/// Whether the presentation's mesh-path bakes are settled: every residency it renders (the world's own, each session
/// view's, each routed scene's and each infinity layer's) has baked every group of the program it last drew, so no
/// lattice is owed and no contour still runs, and the world's own meshes stand at the detail its demand points ask
/// and its mesh region's budget allows. It is the fact the console waits on (<c>world.wait bakes</c>), so a
/// script that reads the baked frame waits on the bake rather than on a tick count. A host that composes no renderer
/// registers none. Members are read on the host pump.
/// </summary>
public interface IWorldBakeReadiness {
    /// <summary>Gets how many frames the world's presentation has captured, the mark a wait arms from.</summary>
    long Frames { get; }

    /// <summary>Returns whether the bakes are settled for the content as it stood at <paramref name="mark"/>: the world's
    /// presentation has captured since the frames its bake measures every group's demand over, holds the program the
    /// latest one's content called for (no rebuild in flight), has baked it, and has left its meshes' detail alone for
    /// as long, so a change delivered before the mark (a <c>world.reload</c>, a row edit, a camera moved to a stop) is
    /// in the bake rather than still queued or still to be measured. Allocates nothing, so a hold polls it every
    /// drain.</summary>
    /// <param name="mark">The value of <see cref="Frames"/> when the wait armed.</param>
    /// <returns><see langword="true"/> when the bakes are settled for that content.</returns>
    bool IsSettledSince(long mark);

    /// <summary>Returns why the bakes are not settled for the content as it stood at <paramref name="mark"/>, or
    /// <see langword="null"/> once they are. It builds a string, so a caller reads it only to report.</summary>
    /// <param name="mark">The value of <see cref="Frames"/> when the wait armed.</param>
    /// <returns>The reason, naming the residencies still baking, or <see langword="null"/>.</returns>
    string? PendingReasonSince(long mark);
    /// <summary>Returns whether the same baked content is also landed in each rendered residency and its required
    /// geometry upload has completed on the GPU. Does not require lighting convergence or idle dynamic uploads.</summary>
    /// <param name="mark">The frame mark at which the wait armed.</param>
    /// <returns>Whether current geometry is ready to draw.</returns>
    bool IsResidentSince(long mark);

    /// <summary>Returns the pending bake or geometry-upload reason, or null when current geometry is resident.</summary>
    /// <param name="mark">The frame mark at which the wait armed.</param>
    /// <returns>The pending reason.</returns>
    string? PendingResidencyReasonSince(long mark);
}
