using System.Numerics;

namespace Puck.SdfVm;

/// <summary>The lifecycle state of one brick pool slot (carve-bake plan §3). A slot is <see cref="Empty"/> until a
/// bake is requested, <see cref="Baking"/> while the engine dispatches its sliced background bake across produced
/// frames, and <see cref="Ready"/> once every slice has been recorded — at which point the planner may swap the bin's
/// analytic carves for the one SampledRegion instance that samples this slot.</summary>
public enum BrickBakeState {
    /// <summary>No bake has been requested for this slot (its pool words are undefined).</summary>
    Empty = 0,
    /// <summary>A bake is in flight — the engine is dispatching its voxel slices background-budget, one slice per
    /// produced frame, until the whole brick is written.</summary>
    Baking = 1,
    /// <summary>Every slice has been recorded; the slot's pool contents are complete (and ordered before any later
    /// frame's render read by the engine's cross-frame barrier), so a program may reference it.</summary>
    Ready = 2,
}

/// <summary>A brick slot's current state plus the monotonic per-slot bake serial (carve-bake plan §3): a re-request
/// cancels the in-flight bake and bumps the serial, so a consumer that cached <see cref="BrickBakeState.Ready"/> at an
/// older serial knows its brick was superseded.</summary>
/// <param name="State">The slot's lifecycle state.</param>
/// <param name="Serial">The monotonic bake serial — incremented on every <see cref="SdfWorldEngine.RequestBrickBake"/>.</param>
public readonly record struct BrickBakeStatus(BrickBakeState State, ulong Serial);

/// <summary>A request to bake the settled-carve UNION distance field of one bin into a brick pool slot (carve-bake
/// plan §1/§3). The bake kernel writes <c>min_i(|v − cᵢ| − rᵢ) / λ</c> at each voxel centre — the closed-form
/// sphere-union, pre-scaled by <see cref="InverseLambda"/> so the render kernels' trilinear interpolant stays
/// 1-Lipschitz and march-safe. The brick box runs <c>[BoxMin, BoxMin + dims·CellSize]</c> in world space; voxel
/// <c>(i,j,k)</c> is centred at <c>BoxMin + (i+0.5, j+0.5, k+0.5)·CellSize</c> (KEEP IN SYNC with
/// <c>sdfSampledRegion</c>'s half-voxel sample convention).</summary>
/// <param name="BoxMin">The brick box's minimum world-space corner.</param>
/// <param name="CellSize">The cubic voxel edge length in world units (box extent = dims × cell size).</param>
/// <param name="DimX">The voxel count along X, in <c>[1, <see cref="SdfBrickPoolLayout.BrickDim"/>]</c>.</param>
/// <param name="DimY">The voxel count along Y, in <c>[1, <see cref="SdfBrickPoolLayout.BrickDim"/>]</c>.</param>
/// <param name="DimZ">The voxel count along Z, in <c>[1, <see cref="SdfBrickPoolLayout.BrickDim"/>]</c>.</param>
/// <param name="InverseLambda">The stored-value scale <c>1/λ</c> (λ = √3 for the carve-union field — carve-bake plan §1);
/// folded into every written distance so no runtime multiply and no <c>stepScale</c> change is needed. Finite and &gt; 0.</param>
/// <param name="Carves">The settled sphere carves, one <see cref="Vector4"/> each: <c>xyz</c> = centre, <c>w</c> = radius.
/// The bake reads exactly this many; an empty list bakes a pure far field (no carve bites).</param>
public readonly record struct BrickBakeRequest(
    Vector3 BoxMin,
    float CellSize,
    int DimX,
    int DimY,
    int DimZ,
    float InverseLambda,
    ReadOnlyMemory<Vector4> Carves
);
