using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>One shape an instance holds, as the mesh bake refines it: the box the shape occupies and the finest feature it
/// carries. An instance that holds several shapes (a pooled blend group, or a static creation emitted as one scoped
/// instance) declares one part a shape (<see cref="SdfInstanceRange.Parts"/>), so its group bakes as a union does: each
/// part a member of the union's final lattice, refined over its own box at its own spacing, never one detail spread over
/// the whole instance's bound.</summary>
/// <param name="Slot">The dynamic-transform slot a dynamic instance's shape rides, whose frame <paramref name="Low"/> and
/// <paramref name="High"/> are in; <see cref="StaticSlot"/> for a static instance's part, whose box is in world axes.</param>
/// <param name="Low">The low corner of the shape's box, in its slot's frame and units, or in world units for a static
/// part.</param>
/// <param name="High">The high corner of the shape's box.</param>
/// <param name="Detail">The finest feature, in the box's units, the shape carries that its box does not imply; zero for
/// none (<see cref="SdfInstanceRange.Detail"/>).</param>
public readonly record struct SdfInstancePart(int Slot, Vector3 Low, Vector3 High, float Detail) {
    /// <summary>The <see cref="Slot"/> of a static instance's part: it rides no slot, and its box is in world axes.</summary>
    public const int StaticSlot = -1;

    /// <summary>Returns whether the part's corners are finite and ordered, its slot <see cref="StaticSlot"/> or a slot
    /// index and its detail finite and non-negative: a part the builder keeps, a static instance's when its slot is
    /// <see cref="StaticSlot"/> and a dynamic instance's otherwise.</summary>
    public bool IsValid =>
        (Slot >= StaticSlot) &&
        float.IsFinite(f: Low.X) && float.IsFinite(f: Low.Y) && float.IsFinite(f: Low.Z) &&
        float.IsFinite(f: High.X) && float.IsFinite(f: High.Y) && float.IsFinite(f: High.Z) &&
        (Low.X <= High.X) && (Low.Y <= High.Y) && (Low.Z <= High.Z) &&
        float.IsFinite(f: Detail) && (Detail >= 0f);
}
