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
    /// <summary>Counts the measures the demand points' movement made due, a part of <see cref="DemandGroups"/>.</summary>
    public static readonly WorkKind DemandDue = new(name: "sdf.residency.demand.due", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the groups whose slot moved by less than their tier band leaves, so the bakes measured nothing for
    /// them.</summary>
    public static readonly WorkKind DemandStood = new(name: "sdf.residency.demand.stood", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the measures a moved slot made: its group's box moved by as much as its tier band leaves, a part of
    /// <see cref="DemandGroups"/>.</summary>
    public static readonly WorkKind DemandMoved = new(name: "sdf.residency.demand.moved", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the measures a moved copy made, a part of <see cref="DemandGroups"/>.</summary>
    public static readonly WorkKind DemandCopied = new(name: "sdf.residency.demand.copied", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the demand points' changes that no movement bound covers (a view came or went, its focal or frustum
    /// changed, the detail bias moved), each of which measures every group again.</summary>
    public static readonly WorkKind DemandResets = new(name: "sdf.residency.demand.resets", unit: "resets", workClass: WorkClass.Pacing);
    /// <summary>Counts the groups the tier decisions read.</summary>
    public static readonly WorkKind TierGroups = new(name: "sdf.residency.tiers.groups", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the tier decisions run.</summary>
    public static readonly WorkKind TierDecisions = new(name: "sdf.residency.tiers.decisions", unit: "decisions", workClass: WorkClass.Pacing);
    /// <summary>Counts the replacements the tier decisions started (a finer or coarser tier, or a floor's first detail).</summary>
    public static readonly WorkKind TierStarts = new(name: "sdf.residency.tiers.started", unit: "groups", workClass: WorkClass.Pacing);
    /// <summary>Counts the rows a staging judged because the slot they ride moved.</summary>
    public static readonly WorkKind MotionRows = new(name: "sdf.residency.motion.rows", unit: "rows", workClass: WorkClass.Pacing);
    /// <summary>Counts the slot readers (lights, media, paints) a staging judged because their slot moved.</summary>
    public static readonly WorkKind MotionReaders = new(name: "sdf.residency.motion.readers", unit: "readers", workClass: WorkClass.Pacing);
    /// <summary>Counts the placed meshes the unselected-bytes report walked.</summary>
    public static readonly WorkKind MarkMeshes = new(name: "sdf.residency.marks.meshes", unit: "meshes", workClass: WorkClass.Pacing);

    /// <summary>Gets the process's residency counts.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.residency", kinds: [Prepared, DemandGroups, DemandBoxes, DemandDue, DemandStood, DemandMoved, DemandCopied, DemandResets, TierGroups, TierDecisions, TierStarts, MotionRows, MotionReaders, MarkMeshes]);
}
