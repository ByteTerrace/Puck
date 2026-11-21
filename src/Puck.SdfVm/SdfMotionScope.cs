namespace Puck.SdfVm;

/// <summary>
/// What one raster pass of a view, and the passes that shade what it drew, can show of a residency's changes: the cull
/// the pass draws under (<see cref="SdfMeshCull"/>) and a revision that moves whenever a draw that cull keeps changes.
/// A pass signs with its scope's revision in place of the residency's own, so a body moving where the pass draws
/// nothing leaves the pass's image standing. Presentation only.
/// <para>
/// The invariant: while a scope's revision holds, no draw its cull keeps has changed since the revision was read. The
/// tables keep it at the one door every staged change passes (<see cref="SdfWorldTables.ObserveMotion(SdfMotionScope, in SdfMeshCull, int)"/>
/// and the staging beside it): a changed draw record is judged by the box it held before the change and the box it
/// holds after, a changed transform slot by the draws that read it, and a change no box bounds (a light, a medium or a
/// paint riding a slot, a whole table seeded again) moves every scope. A cull other than the one the scope held moves
/// the revision too, since what the earlier changes showed under the new cull was never judged; a revision is never
/// reused, so a camera that returns to an earlier pose does not return to an earlier revision.
/// </para>
/// <para>
/// A scope the tables have not been asked about for <see cref="SdfWorldTables.MotionUnobservedStagings"/> stagings is let
/// go, so a parked view costs nothing; asking again starts it at a new revision.
/// </para>
/// </summary>
/// <param name="glass">Whether the scope's pass draws the glass draws alone (<see cref="SdfMeshDraw.Glass"/>), so only a
/// glass draw's change can show in it.</param>
public sealed class SdfMotionScope(bool glass = false) {
    // Every scope's revisions come from one sequence, so no scope repeats a value another, or its own earlier life, held.
    private static long Revisions;

    private SdfMeshCull m_cull;

    /// <summary>Gets the scope's revision: a new value whenever a change its cull keeps is staged, its cull changes, or
    /// the tables take it up again after letting it go.</summary>
    public long Revision { get; private set; } = Next();
    /// <summary>Gets the revision as of the tables' latest upload: the poses the current transform and draw tables hold
    /// for the draws the scope keeps.</summary>
    public long Poses { get; private set; } = Next();
    /// <summary>Gets <see cref="Poses"/> as of the upload before the latest: the poses the previous tables hold for the
    /// draws the scope keeps, equal to <see cref="Poses"/> once those draws have stood for an upload.</summary>
    public long PreviousPoses { get; private set; } = Next();
    /// <summary>Gets the index, in the frame's views, of the view the scope's pass belongs to.</summary>
    public int View { get; private set; }

    // The tables' staging the scope was last met in and last asked about in, and whether the tables hold it.
    internal long MetStaging { get; private set; } = -1;
    internal long Observed { get; set; }
    internal bool Attached { get; set; }

    private static long Next() => Interlocked.Increment(location: ref Revisions);

    // Whether the scope's pass may show a draw of a box: a draw of the kind it draws, which its cull keeps.
    internal bool Shows(in SdfMeshBox? box, bool isGlass) => ((!glass || isGlass) && m_cull.Keeps(box: in box));
    // A change the scope's cull keeps was staged.
    internal void Meet(long staging) {
        MetStaging = staging;
        Revision = Next();
    }
    // Takes the cull the scope's pass draws under now; a new one moves the revision.
    internal bool Follow(in SdfMeshCull cull, int view) {
        if ((cull == m_cull) && (view == View)) {
            return false;
        }

        m_cull = cull;
        View = view;
        Revision = Next();

        return true;
    }
    // The tables uploaded: the previous tables now hold what the current ones held.
    internal void Uploaded() {
        PreviousPoses = Poses;
        Poses = Revision;
    }
    // The tables take the scope up: nothing it stood for is known to stand.
    internal void Restart(long staging) {
        MetStaging = staging;
        Revision = Next();
        Poses = Revision;
        PreviousPoses = Next();
    }
}
