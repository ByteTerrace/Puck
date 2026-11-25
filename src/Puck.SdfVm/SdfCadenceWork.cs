using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// The work the cadence does to decide whether a pass stands, process-wide: the pass signatures read
/// (<see cref="SdfWorldTables.PassSignature"/>, <see cref="SdfWorldTables.ViewSignature"/>), the pass blocks written
/// (<see cref="SdfFrameBlock.Write"/>) and the shadow cascade sets fitted (<see cref="SdfMeshShadowCascades.Fit"/>). Over
/// the same frames, <c>blocks</c> and <c>fits</c> against <c>signatures</c> is how much of each signature's input was
/// built again rather than read. Counts of work, never compared with a wall clock.
/// </summary>
public static class SdfCadenceWork {
    /// <summary>Counts the pass and view signatures read.</summary>
    public static readonly WorkKind Signatures = new(name: "sdf.cadence.signatures", unit: "signatures", workClass: WorkClass.Pacing);
    /// <summary>Counts the passes the cadence forced to render with no signature: the gate off, a pending pick, an
    /// active convergence, or another instance's image read with no publication to identify it by.</summary>
    public static readonly WorkKind Forced = new(name: "sdf.cadence.forced", unit: "passes", workClass: WorkClass.Pacing);
    /// <summary>Counts the frames an instance's temporal history could not stand for, which moves the sample revision
    /// every pass of the instance signs with.</summary>
    public static readonly WorkKind Resampled = new(name: "sdf.cadence.resampled", unit: "frames", workClass: WorkClass.Pacing);
    /// <summary>Counts the pass blocks written from a frame (<see cref="SdfFrameBlock.Write"/>).</summary>
    public static readonly WorkKind Blocks = new(name: "sdf.cadence.blocks", unit: "blocks", workClass: WorkClass.Pacing);
    /// <summary>Counts the shadow cascade sets fitted (<see cref="SdfMeshShadowCascades.Fit"/>).</summary>
    public static readonly WorkKind Fits = new(name: "sdf.cadence.fits", unit: "fits", workClass: WorkClass.Pacing);
    /// <summary>Counts the per-view light selections run (<see cref="SignedDistance.SdfLightSelection.Select"/>).</summary>
    public static readonly WorkKind LightSelections = new(name: "sdf.cadence.light-selections", unit: "selections", workClass: WorkClass.Pacing);

    /// <summary>Counts the motion scopes the tables held, summed over the stagings of their draw records
    /// (<see cref="SdfMotionScope"/>): the most <see cref="ScopesMet"/> can reach over the same stagings.</summary>
    public static readonly WorkKind Scopes = new(name: "sdf.cadence.scopes", unit: "scopes", workClass: WorkClass.Pacing);
    /// <summary>Counts the scopes a staging moved the revision of, each at most once a staging: a change one of its draws
    /// could show, or one no box bounds. A scope not counted stood through its staging.</summary>
    public static readonly WorkKind ScopesMet = new(name: "sdf.cadence.scopes.met", unit: "scopes", workClass: WorkClass.Pacing);
    /// <summary>Counts the scopes a publication of the screens' emission lit (<see cref="SdfMotionScope.Lighting"/>): one
    /// for each changed screen whose light reaches a scope that shades. A view that shows no screen's light counts none.</summary>
    public static readonly WorkKind ScopesLit = new(name: "sdf.cadence.scopes.lit", unit: "scopes", workClass: WorkClass.Pacing);
    /// <summary>Counts the changes no box bounds that moved every open scope: a view's pose overlay row (that view's
    /// scopes) or a previous table seeded whole.</summary>
    public static readonly WorkKind ScopeUnbounded = new(name: "sdf.cadence.scopes.unbounded", unit: "changes", workClass: WorkClass.Pacing);
    /// <summary>Counts the revisions a scope moved because its pass's cull changed: its camera, its cascade or its
    /// extent.</summary>
    public static readonly WorkKind ScopeCulls = new(name: "sdf.cadence.scopes.culls", unit: "scopes", workClass: WorkClass.Pacing);
    /// <summary>Counts the changed draws and slot readers judged against at least one open scope.</summary>
    public static readonly WorkKind ScopeChanges = new(name: "sdf.cadence.scopes.changes", unit: "changes", workClass: WorkClass.Pacing);
    /// <summary>Counts the scopes those changes were judged against, each up to two box tests.</summary>
    public static readonly WorkKind ScopeTests = new(name: "sdf.cadence.scopes.tests", unit: "tests", workClass: WorkClass.Pacing);

    /// <summary>A screen slot a resident screen claimed (SdfWorldTables.SettleScreenSlots).</summary>
    public static readonly WorkKind ScreenSlotClaims = new(name: "sdf.screens.claims", unit: "slots", workClass: WorkClass.Pacing);
    /// <summary>A screen slot a screen that left residency gave back.</summary>
    public static readonly WorkKind ScreenSlotReleases = new(name: "sdf.screens.releases", unit: "slots", workClass: WorkClass.Pacing);
    /// <summary>The screens holding a slot, summed over every settle: divided by the settles, the screens resident at once.</summary>
    public static readonly WorkKind ScreensResident = new(name: "sdf.screens.resident", unit: "screens", workClass: WorkClass.Pacing);
    /// <summary>The screens wanted resident that held no slot because more were wanted than the slots hold, summed over
    /// every settle.</summary>
    public static readonly WorkKind ScreensDegraded = new(name: "sdf.screens.degraded", unit: "screens", workClass: WorkClass.Pacing);
    /// <summary>Gets the process's cadence and screen-slot counts.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.cadence", kinds: [Signatures, Forced, Resampled, Blocks, Fits, LightSelections, Scopes, ScopesMet, ScopesLit, ScopeUnbounded, ScopeCulls, ScopeChanges, ScopeTests, ScreenSlotClaims, ScreenSlotReleases, ScreensResident, ScreensDegraded]);
}
