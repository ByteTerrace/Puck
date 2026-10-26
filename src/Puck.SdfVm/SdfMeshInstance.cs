using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>A copy of one local baked geometry asset. Its pose and host identity never enter the field program.</summary>
/// <param name="AssetSlot">The local template's dynamic slot in the program.</param>
/// <param name="Identity">The host's picking identity for this copy.</param>
/// <param name="Transform">The copy's root pose.</param>
/// <param name="Scale">The uniform size of the copy.</param>
public sealed record SdfMeshInstance(int AssetSlot, object Identity, DynamicTransform Transform, float Scale) {
    /// <summary>Gets additional local surfaces of the asset, such as its separate glass bake.</summary>
    public IReadOnlyList<int> OtherAssetSlots { get; init; } = [];
    /// <summary>Gets optional authored triangle geometry, shared by all copies.</summary>
    public SdfMesh? Mesh { get; init; }
    /// <summary>Gets the authored mesh's shared program material.</summary>
    public int Material { get; init; }
    /// <summary>Returns whether a bake group is one of this asset's local surfaces.</summary>
    public bool UsesSlot(int slot) => AssetSlot == slot || OtherAssetSlots.Contains(slot);
    /// <summary>Gets the portal plane the copy straddles, before its frame slot is assigned.</summary>
    public SdfSlotClip? Clip { get; init; }
    /// <summary>Gets the frame's root slot, assigned after the program's articulated slots.</summary>
    public int FrameSlot { get; internal set; }
    /// <summary>Gets the template's first program instance, filled by the bake's dressing for material lookup.</summary>
    public int FirstInstance { get; internal set; } = -1;
    /// <summary>Gets the copy's root matrix.</summary>
    public Matrix4x4 Matrix => Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Transform.Orientation) * Matrix4x4.CreateTranslation(Transform.Position);
    /// <summary>Compares the host pose independently of frame-assigned lookup metadata.</summary>
    public bool SamePose(SdfMeshInstance other) => AssetSlot == other.AssetSlot && Equals(Identity, other.Identity) && Transform == other.Transform && Scale == other.Scale && Clip == other.Clip && ReferenceEquals(Mesh, other.Mesh) && Material == other.Material && OtherAssetSlots.SequenceEqual(other.OtherAssetSlots);
}
