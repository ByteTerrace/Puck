using System.Numerics;

namespace Puck.SdfVm;

/// <summary>One per-object instance's declaration: the instruction slice it owns (<see cref="First"/>..<see cref="End"/>,
/// exclusive) plus its world-space bounding sphere — the world renderer's tile-cull beam prepass tests the sphere per
/// tile and writes a per-tile instance mask (<see cref="SdfProgram.InstanceMaskWordCount"/> uints, ceiling
/// <see cref="SdfProgramBuilder.MaxInstances"/> bits) so <c>map()</c> in the world path evaluates an instance's slice
/// only for tiles the sphere may cover. Declared via
/// <see cref="SdfProgramBuilder.BeginInstance"/>/<see cref="SdfProgramBuilder.BeginInstanceDynamic"/>/
/// <see cref="SdfProgramBuilder.EndInstance"/>; packed into the program word stream by <see cref="SdfProgram"/>.</summary>
/// <param name="First">The instance's first instruction index (inclusive).</param>
/// <param name="End">The instance's instruction end index (exclusive).</param>
/// <param name="IsDynamic">Whether the bound center tracks a dynamic-transform slot (<see cref="Slot"/>) rather than
/// being a fixed world-space point.</param>
/// <param name="Center">A STATIC instance's world-space bound center; a DYNAMIC instance's pre-dynamic offset (added
/// to the slot's per-frame position on the GPU — no quaternion rotate).</param>
/// <param name="Radius">The bound radius (post-dynamic local geometry folded in for a dynamic instance).</param>
/// <param name="Slot">The dynamic-transform slot index; meaningless when <see cref="IsDynamic"/> is <see langword="false"/>.</param>
public readonly record struct SdfInstanceRange(
    int First,
    int End,
    bool IsDynamic,
    Vector3 Center,
    float Radius,
    int Slot
);
