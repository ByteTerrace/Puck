using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>A half-open range of ordinary local program instances shared by static mesh copies.</summary>
/// <param name="FirstInstance">The first program instance ordinal.</param>
/// <param name="InstanceCount">The number of program instances in the template.</param>
public readonly record struct SdfMeshTemplateRange(int FirstInstance, int InstanceCount) {
    /// <summary>Returns whether an instance belongs to this template.</summary>
    /// <param name="instance">The program instance ordinal.</param>
    /// <returns>Whether the ordinal is inside this half-open range.</returns>
    public bool Contains(int instance) => ((instance >= FirstInstance) && (((long)instance - FirstInstance) < InstanceCount));
}

/// <summary>A copy of one local baked geometry asset. Its pose and host identity never enter the field program.</summary>
/// <param name="AssetSlot">The local template's dynamic slot in the program.</param>
/// <param name="Identity">The host's picking identity for this copy.</param>
/// <param name="Transform">The copy's root pose.</param>
/// <param name="Scale">The uniform size of the copy.</param>
/// <param name="IsDynamic">Whether the surface can move or deform; fixed copies may enter static irradiance geometry.</param>
public sealed record SdfMeshInstance(int AssetSlot, object Identity, DynamicTransform Transform, float Scale, bool IsDynamic) {
    /// <summary>Gets the host's motion-history identity, stable through continuous motion and changed on a pose cut.
    /// Falls back to <see cref="Identity"/> when absent.</summary>
    public object? MotionIdentity { get; init; }
    /// <summary>Gets the ordinary static instance range that supplies local geometry, or null to use
    /// <see cref="AssetSlot"/> and <see cref="OtherAssetSlots"/>. The asset slot is ignored for a range template.</summary>
    public SdfMeshTemplateRange? TemplateRange { get; init; }
    /// <summary>Gets whether material layers use the copy root rather than the world frame. Geometry and static
    /// lighting membership are independent of this material-coordinate choice.</summary>
    public bool UsesRootMaterialFrame { get; init; } = true;
    /// <summary>Gets additional local surfaces of the asset, such as its separate glass bake.</summary>
    public IReadOnlyList<int> OtherAssetSlots { get; init; } = [];
    /// <summary>Gets optional authored triangle geometry, shared by all copies.</summary>
    public SdfMesh? Mesh { get; init; }
    /// <summary>Gets the authored mesh's shared program material.</summary>
    public int Material { get; init; }

    /// <summary>Returns whether a bake group is one of this asset's local surfaces.</summary>
    public bool UsesSlot(int slot) => ((AssetSlot == slot) || OtherAssetSlots.Contains(value: slot));

    /// <summary>Gets the portal plane the copy straddles, before its frame slot is assigned.</summary>
    public SdfSlotClip? Clip { get; init; }
    /// <summary>Gets the frame's root slot, assigned after the program's articulated slots.</summary>
    public int FrameSlot { get; internal set; }
    /// <summary>Gets the copy's root matrix.</summary>
    public Matrix4x4 Matrix => ((Matrix4x4.CreateScale(scale: Scale) * Matrix4x4.CreateFromQuaternion(quaternion: Transform.Orientation)) * Matrix4x4.CreateTranslation(position: Transform.Position));

    /// <summary>Compares the host pose independently of frame-assigned lookup metadata.</summary>
    public bool SamePose(SdfMeshInstance other) => SamePose(other.AssetSlot, other.Identity, other.MotionIdentity, other.Transform, other.Scale, other.IsDynamic, other.Clip, other.Mesh, other.Material, other.OtherAssetSlots, other.UsesRootMaterialFrame, other.TemplateRange);
    /// <summary>Compares a candidate pose before allocating its immutable frame record.</summary>
    public bool SamePose(int assetSlot, object identity, object? motionIdentity, DynamicTransform transform, float scale, bool isDynamic, SdfSlotClip? clip, SdfMesh? mesh, int material, IReadOnlyList<int> otherAssetSlots, bool usesRootMaterialFrame = true, SdfMeshTemplateRange? templateRange = null) {
        if ((AssetSlot != assetSlot) || !Equals(objA: Identity, objB: identity) || !Equals(objA: MotionIdentity, objB: motionIdentity) || (Transform != transform) || (Scale != scale) || (IsDynamic != isDynamic) || (Clip != clip) || !ReferenceEquals(objA: Mesh, objB: mesh) || (Material != material) || (OtherAssetSlots.Count != otherAssetSlots.Count) || (UsesRootMaterialFrame != usesRootMaterialFrame) || (TemplateRange != templateRange)) { return false; }
        for (var index = 0; (index < OtherAssetSlots.Count); index++) {
            if (OtherAssetSlots[index] != otherAssetSlots[index]) { return false; }
        }
        return true;
    }
}
