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

    /// <summary>Gets the process's cadence counts.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.cadence", kinds: [Signatures, Forced, Resampled, Blocks, Fits]);
}
