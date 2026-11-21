namespace Puck.SdfVm;

/// <summary>What the pass a motion scope belongs to reads of a residency's draws and slots
/// (<see cref="SdfMotionScope"/>).</summary>
public enum SdfMotionReader : byte {
    /// <summary>A view's mesh pass and the passes that shade what it drew: every draw its cull keeps, and every slot
    /// shading reads besides a draw's (a light's, a medium's, a paint's).</summary>
    Shading = 0,
    /// <summary>A view's glass passes: as <see cref="Shading"/>, of the glass draws alone
    /// (<see cref="SdfMeshDraw.Glass"/>).</summary>
    Glass = 1,
    /// <summary>A shadow cascade: every draw its caster cull keeps and nothing else, since a cascade holds depths
    /// alone.</summary>
    Caster = 2,
}
/// <summary>Why a motion scope's revision last moved, for <c>world.residency</c>.</summary>
internal enum SdfMotionCause : byte {
    /// <summary>The tables took the scope up.</summary>
    Taken = 0,
    /// <summary>The scope's pass draws under another cull.</summary>
    Cull,
    /// <summary>A staged draw record the cull keeps.</summary>
    Draw,
    /// <summary>A skinned draw one of whose slots moved.</summary>
    Skin,
    /// <summary>A draw whose frame slot's row changed.</summary>
    Slot,
    /// <summary>A slot a light, a medium or a paint rides.</summary>
    Reader,
    /// <summary>A row of the view's pose overlay.</summary>
    Overlay,
    /// <summary>A previous table seeded whole.</summary>
    Seed,
    /// <summary>A screen whose emitted light reaches the cull published a new reduction.</summary>
    Emission,
}
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
/// paint riding a slot, a whole table seeded again) moves every scope that reads it. A cull other than the one the scope
/// held moves the revision too, since what the earlier changes showed under the new cull was never judged; a revision
/// is never reused, so a camera that returns to an earlier pose does not return to an earlier revision.
/// </para>
/// <para>
/// A scope the tables have not been asked about for <see cref="SdfWorldTables.MotionUnobservedStagings"/> stagings is let
/// go, so a parked view costs nothing; asking again starts it at a new revision.
/// </para>
/// </summary>
/// <param name="reader">What the scope's pass reads.</param>
public sealed class SdfMotionScope(SdfMotionReader reader = SdfMotionReader.Shading) {
    // Every scope's revisions come from one sequence, so no scope repeats a value another, or its own earlier life, held.
    private static long Revisions;

    private SdfMeshCull m_cull;

    /// <summary>Gets what the scope's pass reads.</summary>
    public SdfMotionReader Reader => reader;
    /// <summary>Gets the scope's revision: a new value whenever a change its cull keeps is staged, its cull changes, or
    /// the tables take it up again after letting it go.</summary>
    public long Revision { get; private set; } = Next();
    /// <summary>Gets the scope's lighting revision: a new value whenever a light no draw or slot carries changes where its
    /// cull keeps (a screen's emission, whose reach meets it), which the passes that shade sign with beside
    /// <see cref="Revision"/>. A pose never moves it, so the temporal history's poses stand through it.</summary>
    public long Lighting { get; private set; } = Next();
    /// <summary>Gets the revision as of the tables' latest upload: the poses the current transform and draw tables hold
    /// for the draws the scope keeps.</summary>
    public long Poses { get; private set; } = Next();
    /// <summary>Gets <see cref="Poses"/> as of the upload before the latest: the poses the previous tables hold for the
    /// draws the scope keeps, equal to <see cref="Poses"/> once those draws have stood for an upload.</summary>
    public long PreviousPoses { get; private set; } = Next();
    /// <summary>Gets the index, in the frame's views, of the view the scope's pass belongs to.</summary>
    public int View { get; private set; }

    // The cull the scope judges by, for world.residency.
    internal SdfMeshCull Cull => m_cull;
    // The tables' staging the scope was last met in and last asked about in, and whether the tables hold it.
    internal long MetStaging { get; private set; } = -1;
    internal long Observed { get; set; }
    internal bool Attached { get; set; }
    // Why the revision last moved, the row or slot that moved it, and how often changes have moved it.
    internal SdfMotionCause Cause { get; private set; }
    internal int CauseDetail { get; private set; }
    internal long Meets { get; private set; }

    private static long Next() => Interlocked.Increment(location: ref Revisions);

    // Whether the scope's pass may show a draw of a box: a draw of the kind it draws, which its cull keeps.
    internal bool Shows(in SdfMeshBox? box, bool isGlass) => (((reader != SdfMotionReader.Glass) || isGlass) && m_cull.Keeps(box: in box));
    // A change the scope's pass can show was staged.
    internal void Meet(long staging, SdfMotionCause cause, int detail) {
        MetStaging = staging;
        Revision = Next();
        (Cause, CauseDetail) = (cause, detail);
        Meets++;
    }
    // A light no draw carries changed where the cull keeps.
    internal void Light(SdfMotionCause cause, int detail) {
        Lighting = Next();
        (Cause, CauseDetail) = (cause, detail);
        Meets++;
    }
    // Takes the cull the scope's pass draws under now; a new one moves the revision.
    internal bool Follow(in SdfMeshCull cull, int view) {
        if ((cull == m_cull) && (view == View)) {
            return false;
        }

        m_cull = cull;
        View = view;
        Revision = Next();
        (Cause, CauseDetail) = (SdfMotionCause.Cull, 0);

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
        (Cause, CauseDetail) = (SdfMotionCause.Taken, 0);
    }
}
