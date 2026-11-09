using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One shape a dynamic instance holds, as the mesh bake refines it: the box the shape occupies in the frame of
/// the slot it rides and the finest feature it carries. A dynamic instance that holds several shapes (a pooled blend
/// group) declares one part a shape (<see cref="SdfInstanceRange.Parts"/>), so its group bakes as a static union does:
/// each part a member of the union's final lattice, refined over its own box at its own spacing, never one detail spread
/// over the whole instance's bound.</summary>
/// <param name="Slot">The dynamic-transform slot the shape rides, whose frame <paramref name="Low"/> and
/// <paramref name="High"/> are in.</param>
/// <param name="Low">The low corner of the shape's box in its slot's frame, in that slot's own units.</param>
/// <param name="High">The high corner of the shape's box in its slot's frame.</param>
/// <param name="Detail">The finest feature, in the slot's units, the shape carries that its box does not imply; zero for
/// none (<see cref="SdfInstanceRange.Detail"/>).</param>
public readonly record struct SdfInstancePart(int Slot, Vector3 Low, Vector3 High, float Detail) {
    /// <summary>Returns whether the part's corners are finite and ordered, its slot non-negative and its detail finite and
    /// non-negative: a part the builder keeps.</summary>
    public bool IsValid =>
        (Slot >= 0) &&
        float.IsFinite(f: Low.X) && float.IsFinite(f: Low.Y) && float.IsFinite(f: Low.Z) &&
        float.IsFinite(f: High.X) && float.IsFinite(f: High.Y) && float.IsFinite(f: High.Z) &&
        (Low.X <= High.X) && (Low.Y <= High.Y) && (Low.Z <= High.Z) &&
        float.IsFinite(f: Detail) && (Detail >= 0f);
}
