using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    /// <summary>Gets the indirect-light tier the residency's frame asks for. It sizes the mesh path's irradiance volume
    /// (<see cref="MeshIrradiance"/>), which measures at every tier; Off applies none of its light.</summary>
    public SdfIndirectTier IndirectTier => (m_frame?.IndirectTier ?? SdfIndirectTier.Off);
}
