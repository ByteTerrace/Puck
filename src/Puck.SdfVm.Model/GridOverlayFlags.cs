namespace Puck.SdfVm;

/// <summary>Selects which grids a view draws and where. The kernels read it from the pass block's <c>gridFlags</c>
/// (<c>shade/sdf-grid.hlsli</c>), each member generated as <c>SDF_GRID_*</c> by <see cref="SdfIsaHlsl"/>.</summary>
[Flags]
public enum GridOverlayFlags : uint {
    /// <summary>No grid.</summary>
    None = 0u,
    /// <summary>The world lattice on the working plane: horizontal surfaces within the on-plane band of
    /// <c>GridOverlayState.PlaneY</c>.</summary>
    World = 1u,
    /// <summary>The captured reference's own lattice, on its faces and the surfaces around it inside the patch
    /// radius.</summary>
    Object = 2u,
    /// <summary>The world lattice on every surface the view hits, projected along the surface normal: X and Z lines on
    /// floors at any height, Y lines on walls, and a normal-weighted blend on slopes. Replaces the working-plane test when
    /// set with <see cref="World"/>.</summary>
    Surface = 4u,
}
