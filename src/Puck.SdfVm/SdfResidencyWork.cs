using Puck.Abstractions.Counting;

namespace Puck.SdfVm;

/// <summary>
/// The work a residency's frame thread does to keep its meshes' detail, its motion scopes and its figures current,
/// process-wide: the groups and boxes the detail demand measures (<see cref="SdfMeshPathBake"/>), the groups its tier
/// decisions read, the rows and slot readers a staging judges against the motion scopes
/// (<see cref="SdfWorldTables"/>), and the walks behind the residency's reports. Each should follow what changed, not
/// what exists: divided by <see cref="Prepared"/>, a still scene reads near zero. Counts of work, never compared with a
/// wall clock.
/// </summary>
public static class SdfResidencyWork {
    /// <summary>Counts the frames residencies' bakes prepared, one per residency per frame: what the others are read
    /// against.</summary>
    public static readonly WorkKind Prepared = new(name: "sdf.residency.prepared", unit: "frames", workClass: WorkClass.Pacing);
    /// <summary>Counts the groups whose demand the bakes measured again (<see cref="SdfMeshDemand.PixelAt(System.Numerics.Vector3, System.Numerics.Vector3)"/>).</summary>
    public static readonly WorkKind DemandGroups = new(name: "sdf.residency.demand.groups", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the boxes those measures read: one a group, or each copy of a shared geometry.</summary>
    public static readonly WorkKind DemandBoxes = new(name: "sdf.residency.demand.boxes", unit: "boxes", workClass: WorkClass.Pacing);
    /// <summary>Counts the demand points those measures read their boxes against, one a point and box, its eye and
    /// frustum together.</summary>
    public static readonly WorkKind DemandViews = new(name: "sdf.residency.demand.views", unit: "views", workClass: WorkClass.Pacing);
    /// <summary>Counts the measures the demand points' movement made due, a part of <see cref="DemandGroups"/>: a clock's
    /// (<see cref="DemandDueTravel"/>, <see cref="DemandDueScale"/>) or a frustum's (<see cref="DemandFramed"/>).</summary>
    public static readonly WorkKind DemandDue = new(name: "sdf.residency.demand.due", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the measures the points' travel made due (<see cref="SdfMeshDemand.Travel"/>).</summary>
    public static readonly WorkKind DemandDueTravel = new(name: "sdf.residency.demand.due.travel", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the measures the points' change of scale made due (<see cref="SdfMeshDemand.Scale"/>).</summary>
    public static readonly WorkKind DemandDueScale = new(name: "sdf.residency.demand.due.scale", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the groups the frustum watch read: each a frame whose tier a frustum bounded to its view could move
    /// (<see cref="SdfMeshDemand.Moves"/>).</summary>
    public static readonly WorkKind DemandWatched = new(name: "sdf.residency.demand.watched", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the measures a watched frustum's move made, a part of <see cref="DemandDue"/>.</summary>
    public static readonly WorkKind DemandFramed = new(name: "sdf.residency.demand.framed", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the groups whose moved frustums all still answered as they did, their watch renewed with no
    /// measure (<see cref="SdfMeshDemand.Renew"/>).</summary>
    public static readonly WorkKind DemandRenewed = new(name: "sdf.residency.demand.renewed", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the moved slots the bakes looked up groups under, each a frame it moved.</summary>
    public static readonly WorkKind DemandSlots = new(name: "sdf.residency.demand.slots", unit: "slots", workClass: WorkClass.Pacing);
    /// <summary>Counts the groups whose slot moved by less than their tier band leaves, so the bakes measured nothing for
    /// them.</summary>
    public static readonly WorkKind DemandStood = new(name: "sdf.residency.demand.stood", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the measures a moved slot made: its group's box moved by as much as its tier band leaves, a part of
    /// <see cref="DemandGroups"/>.</summary>
    public static readonly WorkKind DemandMoved = new(name: "sdf.residency.demand.moved", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the measures a moved copy made, a part of <see cref="DemandGroups"/>.</summary>
    public static readonly WorkKind DemandCopied = new(name: "sdf.residency.demand.copied", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the demand points' changes no clock bounds (a point came, went, jumped or changed kind), each of
    /// which has every group read whether the point's distance could reach under its band
    /// (<see cref="DemandReached"/>).</summary>
    public static readonly WorkKind DemandChanges = new(name: "sdf.residency.demand.changes", unit: "changes", workClass: WorkClass.Pacing);
    /// <summary>Counts the groups the points' changes were read against, at one distance a change point and box.</summary>
    public static readonly WorkKind DemandReached = new(name: "sdf.residency.demand.reached", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the measures the points' changes made, a part of <see cref="DemandGroups"/>.</summary>
    public static readonly WorkKind DemandChanged = new(name: "sdf.residency.demand.changed", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the changes of the detail bias, each of which recomputes every group's bands from the pixel it
    /// holds, measuring none.</summary>
    public static readonly WorkKind DemandRebiased = new(name: "sdf.residency.demand.rebiased", unit: "changes", workClass: WorkClass.Pacing);
    /// <summary>Counts the resets that measure every group again over <see cref="SdfMeshTier.MeasureFrames"/> frames: the
    /// first frame, and a frame whose points changed more than their list keeps
    /// (<see cref="SdfMeshDemand.MostChanges"/>).</summary>
    public static readonly WorkKind DemandResets = new(name: "sdf.residency.demand.resets", unit: "resets", workClass: WorkClass.Pacing);
    /// <summary>Counts the groups the tier decisions read.</summary>
    public static readonly WorkKind TierGroups = new(name: "sdf.residency.tiers.groups", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the tier decisions run.</summary>
    public static readonly WorkKind TierDecisions = new(name: "sdf.residency.tiers.decisions", unit: "decisions", workClass: WorkClass.Pacing);
    /// <summary>Counts the replacements the tier decisions started (a finer or coarser tier, or a floor's first detail).</summary>
    public static readonly WorkKind TierStarts = new(name: "sdf.residency.tiers.started", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the rows a staging judged because the slot they ride moved.</summary>
    public static readonly WorkKind MotionRows = new(name: "sdf.residency.motion.rows", unit: "rows", workClass: WorkClass.Pacing);
    /// <summary>Counts the slot readers (lights, media, paints) a staging judged because their slot moved, each slot's
    /// readers of one kind judged once, as the box that holds them all.</summary>
    public static readonly WorkKind MotionReaders = new(name: "sdf.residency.motion.readers", unit: "readers", workClass: WorkClass.Pacing);
    /// <summary>Counts the placed meshes the unselected-bytes report walked.</summary>
    public static readonly WorkKind MarkMeshes = new(name: "sdf.residency.marks.meshes", unit: "meshes", workClass: WorkClass.Pacing);

    /// <summary>Gets the process's residency counts.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.residency", kinds: [Prepared, DemandGroups, DemandBoxes, DemandViews, DemandDue, DemandDueTravel, DemandDueScale, DemandWatched, DemandFramed, DemandRenewed, DemandSlots, DemandStood, DemandMoved, DemandCopied, DemandChanges, DemandReached, DemandChanged, DemandRebiased, DemandResets, TierGroups, TierDecisions, TierStarts, MotionRows, MotionReaders, MarkMeshes]);
}
