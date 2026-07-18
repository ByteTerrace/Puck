namespace Puck.SdfVm;

/// <summary>
/// The STATIC, host-derivable slot layout of the SDF brick pool (carve-bake plan §3). The pool is one persistent
/// device-local <c>float</c> buffer partitioned into <see cref="MaxBricks"/> equal slots of <see cref="VoxelsPerBrick"/>
/// voxels each; slot <c>i</c> owns the contiguous word run at <see cref="SlotWordOffset(int)"/>. Because the layout is a
/// pure function of the slot index, the carve-bake planner computes each brick instruction's <c>brickWordOffset</c> lane
/// itself with no engine round-trip at emit time — and the engine's <see cref="SdfWorldEngine.DefaultBrickPoolVoxelCapacity"/>
/// sizes the pool to hold every slot at full <see cref="BrickDim"/><sup>3</sup> resolution.
/// </summary>
public static class SdfBrickPoolLayout {
    /// <summary>The number of independent brick slots the pool holds — the LRU eviction set (carve-bake plan §4). A
    /// SampledRegion instance addresses exactly one slot's word run.</summary>
    public const int MaxBricks = 8;
    /// <summary>The per-axis voxel resolution a full brick slot reserves (a cubic <see cref="BrickDim"/><sup>3</sup>
    /// lattice). A baked brick may be smaller per axis (the planner sizes it to its bin), but never larger — the slot's
    /// reserved footprint is always the full cube.</summary>
    public const int BrickDim = 128;
    /// <summary>The voxel (word) count a full brick slot reserves: <see cref="BrickDim"/><sup>3</sup>.</summary>
    public const int VoxelsPerBrick = ((BrickDim * BrickDim) * BrickDim); // 2,097,152
    /// <summary>The pool's total voxel (word) capacity when every slot is provisioned at full resolution:
    /// <see cref="MaxBricks"/> × <see cref="VoxelsPerBrick"/> = 16,777,216 voxels = 64 MB as f32 — the value
    /// <see cref="SdfWorldEngine.DefaultBrickPoolVoxelCapacity"/> resolves to.</summary>
    public const int TotalVoxels = (MaxBricks * VoxelsPerBrick); // 16,777,216 = 64 MB f32

    /// <summary>The base word index of brick slot <paramref name="slot"/> in the pool buffer — the value a SampledRegion
    /// instruction carries as its <c>brickWordOffset</c> lane. Each slot is a contiguous <see cref="VoxelsPerBrick"/>-word
    /// run, so slot <c>i</c> starts at <c>i × VoxelsPerBrick</c>.</summary>
    /// <param name="slot">The slot index, in <c>[0, <see cref="MaxBricks"/>)</c>.</param>
    /// <returns>The slot's base word offset.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is outside <c>[0, <see cref="MaxBricks"/>)</c>.</exception>
    public static int SlotWordOffset(int slot) {
        if ((slot < 0) || (slot >= MaxBricks)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(slot), message: $"A brick slot must be in [0, {MaxBricks}).");
        }

        return (slot * VoxelsPerBrick);
    }
}
