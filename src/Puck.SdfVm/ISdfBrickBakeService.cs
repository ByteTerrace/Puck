using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>The engine-side seam a frame source's brick producer drives (<see cref="ISdfFrameSource.AdvanceBricks"/>):
/// request a sliced background bake into a pool slot, queue a host-baked brick, poll a slot's state, and ask whether
/// the engine owns a pool. A <see cref="SdfWorldTables"/> is this service, so a producer reaches the engine without
/// naming the concrete engine type — a poll-each-produced-frame contract with no callbacks and no cross-thread
/// seams.</summary>
public interface ISdfBrickBakeService {
    /// <summary>Gets a value indicating whether this engine provisions a brick pool at all (its <c>BrickPoolVoxelCapacity</c> was non-zero).
    /// A pool-less engine cannot bake, so a producer never proposes one.</summary>
    bool BrickBakeAvailable { get; }

    /// <summary>Polls a slot's current bake state and monotonic serial (see <see cref="SdfWorldTables.GetBrickState"/>).</summary>
    /// <param name="slot">The pool slot, in <c>[0, <see cref="SdfBrickPoolLayout.MaxBricks"/>)</c>.</param>
    /// <returns>The slot's state and serial.</returns>
    BrickBakeStatus GetBrickState(int slot);
    /// <summary>Requests a sliced background bake of a settled-carve bin's union field into a slot (see
    /// <see cref="SdfWorldTables.RequestBrickBake"/>). Does NOT wait; re-requesting a slot restarts it and bumps the
    /// serial.</summary>
    /// <param name="slot">The pool slot, in <c>[0, <see cref="SdfBrickPoolLayout.MaxBricks"/>)</c>.</param>
    /// <param name="request">The bake request (box, cell size, dims, 1/λ, and the sphere carves).</param>
    void RequestBrickBake(int slot, BrickBakeRequest request);
    /// <summary>Queues host-baked voxel distances for a brick slot — the path for a field produced on the CPU rather
    /// than by the sphere-union baker. The engine marks the slot <see cref="BrickBakeState.Ready"/> after a later
    /// produced frame records the upload; values are the pool's stored scale (distance/λ).</summary>
    /// <param name="slot">The brick slot.</param>
    /// <param name="dimX">Voxels along X.</param>
    /// <param name="dimY">Voxels along Y.</param>
    /// <param name="dimZ">Voxels along Z.</param>
    /// <param name="voxels">The voxel values, X fastest, then Y, then Z; exactly dimX·dimY·dimZ entries.</param>
    void UploadBrick(int slot, int dimX, int dimY, int dimZ, ReadOnlySpan<float> voxels);
}
