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
    /// <summary>Gets a static instance's world-axis box, where its emitter knows one tighter than its bound sphere's cube:
    /// the part of a static union whose surface the bake refines for this instance (<see cref="Detail"/>), so a long,
    /// thin member refines its own surface rather than its neighbours' across a cube its length wide.
    /// <see langword="null"/> where the sphere alone bounds the instance.</summary>
    public (Vector3 Low, Vector3 High)? Box { get; init; }
    /// <summary>Gets the shapes a dynamic instance holds, each the box it occupies in its own slot's frame and the detail it
    /// declares (<see cref="SdfInstancePart"/>): the members its group's union lattice refines one by one, as a static union
    /// refines each instance's box. Empty where the instance's bound alone describes it.</summary>
    public IReadOnlyList<SdfInstancePart> Parts { get; init; } = [];
    /// <summary>Gets the height field a static instance draws (<see cref="SdfProgramBuilder.HeightField"/>), whose
    /// heights displace its baked reference as it is drawn (<see cref="SdfProgram.HeightFields"/>); <see langword="null"/>
    /// for an instance drawn as it was baked.</summary>
    public SdfHeightField? HeightField { get; init; }
}
