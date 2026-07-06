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
/// <param name="Active">Whether the instance participates in the tile-cull scan. A PARKED instance (<see langword="false"/>)
/// still occupies its reserved instance/dynamic-transform slot (so a pool's live-emission always fits the once-sized
/// buffers), but the beam prepass skips it with a single cheap branch (the packed bound carries a negative-radius
/// sentinel — see <see cref="SdfProgram"/>) instead of running the full sphere-vs-cone test, so a parked slot costs
/// almost nothing per tile. Its mask bit is always 0, so Stage 1 never marches it — bit-identical to a bound that no
/// ray can reach, at a fraction of the cull cost.</param>
public readonly record struct SdfInstanceRange(
    int First,
    int End,
    bool IsDynamic,
    Vector3 Center,
    float Radius,
    int Slot,
    bool Active = true
);
