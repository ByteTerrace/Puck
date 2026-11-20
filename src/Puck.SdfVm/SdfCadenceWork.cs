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
    /// <summary>Counts the pass blocks written from a frame (<see cref="SdfFrameBlock.Write"/>).</summary>
    public static readonly WorkKind Blocks = new(name: "sdf.cadence.blocks", unit: "blocks", workClass: WorkClass.Pacing);
    /// <summary>Counts the shadow cascade sets fitted (<see cref="SdfMeshShadowCascades.Fit"/>).</summary>
    public static readonly WorkKind Fits = new(name: "sdf.cadence.fits", unit: "fits", workClass: WorkClass.Pacing);

    /// <summary>Gets the process's cadence counts.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.cadence", kinds: [Signatures, Blocks, Fits]);
}
