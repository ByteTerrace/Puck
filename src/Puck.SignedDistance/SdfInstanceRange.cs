using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One per-object instance's declaration: the instruction slice it owns (<see cref="First"/>..<see cref="End"/>,
/// exclusive) plus its world-space bounding sphere — the mesh bake masks instances per lattice
/// (<see cref="SdfProgram.InstanceMaskWordCount"/> uints, ceiling <see cref="SdfProgramBuilder.MaxInstances"/> bits) so
/// <c>mapCore</c> evaluates an instance's slice only where its group bakes, and the walk skips an instance whose sphere
/// cannot win. Declared via
/// <see cref="SdfProgramBuilder.BeginInstance"/>/<see cref="SdfProgramBuilder.BeginInstanceDynamic"/>/
/// <see cref="SdfProgramBuilder.EndInstance"/>; packed into the program word stream by <see cref="SdfProgram"/>.</summary>
/// <param name="First">The instance's first instruction index (inclusive).</param>
/// <param name="End">The instance's instruction end index (exclusive).</param>
/// <param name="IsDynamic">Whether the bound center tracks a dynamic-transform slot (<see cref="Slot"/>) rather than
/// being a fixed world-space point.</param>
/// <param name="Center">A STATIC instance's finite world-space bound center; a DYNAMIC instance's finite pre-dynamic
/// offset, world-axis by default or rotated with its slot when <see cref="BoundInSlotFrame"/>.</param>
/// <param name="Radius">The finite, non-negative bound radius (post-dynamic local geometry folded in for a dynamic
/// instance).</param>
/// <param name="Slot">The dynamic-transform slot index; meaningless when <see cref="IsDynamic"/> is <see langword="false"/>.</param>
/// <param name="Active">Whether the instance contributes to the field. A PARKED instance (<see langword="false"/>)
/// remains an addressable program entry, but the walk skips it (the packed bound carries a negative-radius sentinel —
/// see <see cref="SdfProgram"/>) and the bake gives it no group.</param>
/// <param name="BakesWithPrevious">Whether a static instance bakes into one mesh with the static instance just before
/// it: the baked-mesh path's group of one rigid copy is the run of instances it heads, so shapes that share a face
/// bake as their union rather than as meshes fighting over the face. Meaningless for a dynamic instance, whose group is
/// its slot's unless <see cref="BakesSeparately"/>.</param>
/// <param name="Detail">The finest feature, in world units, the instance carries that its bound does not imply, such as an
/// embossed glyph's relief; zero for none. The bake contours the instance's region of its group (a static union, or a
/// dynamic slot's group in that slot's frame) at no coarser a spacing.</param>
public readonly record struct SdfInstanceRange(
    int First,
    int End,
    bool IsDynamic,
    Vector3 Center,
    float Radius,
    int Slot,
    bool Active = true,
    bool BakesWithPrevious = false,
    float Detail = 0f
) {
    /// <summary>Gets whether this instance has its own bake while sharing its motion owner's transform.</summary>
    public bool BakesSeparately { get; init; }
    /// <summary>Gets whether a dynamic bound's offset is expressed in its slot's rotating local frame.</summary>
    public bool BoundInSlotFrame { get; init; }
}
