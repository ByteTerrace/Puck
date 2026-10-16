using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    /// <summary>Gets or sets the host lever applied to this residency, including nested views.</summary>
    public SdfIndirectTier? IndirectTierOverride { get; set; }
    /// <summary>Gets the indirect-light tier: the host lever, else the frame's. It sizes the mesh path's irradiance volume
    /// (<see cref="MeshIrradiance"/>); Off reads no volume, and views light with the authored fill.</summary>
    public SdfIndirectTier IndirectTier => (IndirectTierOverride ?? (m_frame?.IndirectTier ?? SdfIndirectTier.Off));
}
