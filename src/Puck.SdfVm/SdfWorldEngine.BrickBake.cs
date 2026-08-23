using System.Numerics;
using Puck.SignedDistance;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    private static void ValidateBrickDimension(int value, string name) {
        if (
            (value < 1) ||
            (value > SdfBrickPoolLayout.BrickDim)
        ) {
            throw new ArgumentOutOfRangeException(
                message: $"A brick dimension must be in [1, {SdfBrickPoolLayout.BrickDim}] (a slot reserves one {SdfBrickPoolLayout.BrickDim}³ cube).",
                paramName: name
            );
        }
    }

    /// <summary>Polls brick pool slot <paramref name="slot"/>'s current bake state and serial —
    /// the frame source reads this each produced frame to know when a bake has finished and it may swap the bin's
    /// analytic carves for the one SampledRegion instance sampling this slot. <see cref="BrickBakeState.Ready"/> means
    /// every slice has been recorded; the engine's cross-frame barrier orders those writes before any later frame's
    /// render read, so a program that references the slot only after seeing Ready never samples an incomplete brick.</summary>
    /// <param name="slot">The brick pool slot, in <c>[0, <see cref="SdfBrickPoolLayout.MaxBricks"/>)</c>.</param>
    /// <returns>The slot's state and monotonic bake serial.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is out of range.</exception>
    public BrickBakeStatus GetBrickState(int slot) {
        if (
            (slot < 0) ||
            (slot >= SdfBrickPoolLayout.MaxBricks)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(slot),
                message: $"A brick slot must be in [0, {SdfBrickPoolLayout.MaxBricks})."
            );
        }

        return new BrickBakeStatus(
            State: m_brickStates[slot],
            Serial: m_brickSerials[slot]
        );
    }
    /// <summary>Requests a sliced background bake of a settled-carve bin's union distance field into brick pool slot
    /// <paramref name="slot"/>. The carve list is copied into the slot's request buffer and the
    /// bake begins slicing across subsequent produced frames (≤ 256K voxels each); it does not wait. Re-requesting a
    /// slot cancels its in-flight bake and restarts it, bumping the slot's monotonic bake serial. The slot's word range
    /// is the fixed <see cref="SdfBrickPoolLayout.SlotWordOffset(int)"/> region, so a SampledRegion instruction the
    /// caller emits with that same offset samples exactly this brick once it reaches <see cref="BrickBakeState.Ready"/>
    /// (poll <see cref="GetBrickState"/>).</summary>
    /// <param name="slot">The brick pool slot, in <c>[0, <see cref="SdfBrickPoolLayout.MaxBricks"/>)</c>.</param>
    /// <param name="request">The bake request (box, cell size, dims, 1/λ, and the sphere carves).</param>
    /// <exception cref="InvalidOperationException">The engine has no brick pool (<c>BrickPoolVoxelCapacity</c> was 0), or is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is out of range, a dimension is outside
    /// <c>[1, <see cref="SdfBrickPoolLayout.BrickDim"/>]</c>, the cell size or 1/λ is not finite and positive, or the
    /// carve list exceeds the per-bake capacity.</exception>
    public void RequestBrickBake(int slot, BrickBakeRequest request) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (!m_brickPoolEnabled) {
            throw new InvalidOperationException(message: "This engine has no brick pool (it was constructed with BrickPoolVoxelCapacity 0); RequestBrickBake is unavailable.");
        }

        if (
            (slot < 0) ||
            (slot >= SdfBrickPoolLayout.MaxBricks)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(slot),
                message: $"A brick slot must be in [0, {SdfBrickPoolLayout.MaxBricks})."
            );
        }

        ValidateBrickDimension(
            value: request.DimX,
            name: nameof(request.DimX)
        );
        ValidateBrickDimension(
            value: request.DimY,
            name: nameof(request.DimY)
        );
        ValidateBrickDimension(
            value: request.DimZ,
            name: nameof(request.DimZ)
        );

        if (
            !float.IsFinite(f: request.CellSize) ||
            (request.CellSize <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(request),
                message: "A brick bake cell size must be finite and greater than zero."
            );
        }

        if (
            !float.IsFinite(f: request.InverseLambda) ||
            (request.InverseLambda <= 0f)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(request),
                message: "A brick bake inverse-lambda must be finite and greater than zero."
            );
        }

        var carves = request.Carves.Span;

        if (carves.Length > MaxBrickCarvesPerBake) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(request),
                message: $"A brick bake carries {carves.Length} carves; the per-bake capacity is {MaxBrickCarvesPerBake}."
            );
        }

        var totalVoxels = ((request.DimX * request.DimY) * request.DimZ);
        var destWordOffset = SdfBrickPoolLayout.SlotWordOffset(slot: slot);

        if ((destWordOffset + totalVoxels) > m_brickPoolVoxelCapacity) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(request),
                message: $"Brick slot {slot}'s {request.DimX}x{request.DimY}x{request.DimZ} = {totalVoxels} voxels at word {destWordOffset} exceed the pool capacity {m_brickPoolVoxelCapacity}."
            );
        }

        // The request buffer is read across this slot's slices as they dispatch over the next frames; a re-request that
        // rewrites it must first drain every in-flight ring frame, exactly like the shared program/glyph re-uploads.
        WaitForFrameRing();

        // Pack the header + carve list. Header: (boxMin, cellSize), asfloat(dims + carveCount), (asfloat(destWordOffset),
        // 1/λ, 0, 0). KEEP IN SYNC with sdf-brick-bake.comp's request layout.
        m_brickRequestScratch[0] = new Vector4(
            x: request.BoxMin.X,
            y: request.BoxMin.Y,
            z: request.BoxMin.Z,
            w: request.CellSize
        );
        m_brickRequestScratch[1] = new Vector4(
            x: BitConverter.UInt32BitsToSingle(value: ((uint)request.DimX)),
            y: BitConverter.UInt32BitsToSingle(value: ((uint)request.DimY)),
            z: BitConverter.UInt32BitsToSingle(value: ((uint)request.DimZ)),
            w: BitConverter.UInt32BitsToSingle(value: ((uint)carves.Length))
        );
        m_brickRequestScratch[2] = new Vector4(
            x: BitConverter.UInt32BitsToSingle(value: ((uint)destWordOffset)),
            y: request.InverseLambda,
            z: 0f,
            w: 0f
        );
        carves.CopyTo(destination: m_brickRequestScratch.AsSpan(start: BrickBakeRequestHeaderFloat4Count));

        m_brickRequestBuffers[slot].Write<Vector4>(data: m_brickRequestScratch.AsSpan(
            start: 0,
            length: (BrickBakeRequestHeaderFloat4Count + carves.Length)
        ));

        m_brickStates[slot] = BrickBakeState.Baking;
        m_brickTotalVoxels[slot] = totalVoxels;
        m_brickVoxelCursor[slot] = 0;
        m_brickSerials[slot]++;
    }

    /// <summary>Queues a host-baked brick upload. A later produced frame records the copy and moves the slot to
    /// <see cref="BrickBakeState.Ready"/>; re-queuing the same slot before that frame keeps only the newest values.</summary>
    /// <param name="slot">The brick slot.</param>
    /// <param name="dimX">Voxels along X.</param>
    /// <param name="dimY">Voxels along Y.</param>
    /// <param name="dimZ">Voxels along Z.</param>
    /// <param name="voxels">Stored-scale voxel values, X fastest, then Y, then Z.</param>
    /// <exception cref="InvalidOperationException">The engine has no brick upload path.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A slot, dimension, pool range, or voxel count is invalid.</exception>
    public void UploadBrick(int slot, int dimX, int dimY, int dimZ, ReadOnlySpan<float> voxels) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        if (
            !m_brickPoolEnabled ||
            (m_brickUploadPipeline is null)
        ) {
            throw new InvalidOperationException(message: "This engine has no brick pool or no upload kernel; UploadBrick is unavailable.");
        }

        if (
            (slot < 0) ||
            (slot >= SdfBrickPoolLayout.MaxBricks)
        ) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(slot),
                message: $"A brick slot must be in [0, {SdfBrickPoolLayout.MaxBricks})."
            );
        }

        ValidateBrickDimension(
            value: dimX,
            name: nameof(dimX)
        );
        ValidateBrickDimension(
            value: dimY,
            name: nameof(dimY)
        );
        ValidateBrickDimension(
            value: dimZ,
            name: nameof(dimZ)
        );

        var totalVoxels = ((dimX * dimY) * dimZ);

        if (voxels.Length != totalVoxels) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(voxels),
                message: $"A {dimX}x{dimY}x{dimZ} brick takes {totalVoxels} voxels; {voxels.Length} were supplied."
            );
        }

        for (var index = 0; (index < voxels.Length); index++) {
            if (!float.IsFinite(f: voxels[index])) {
                throw new ArgumentOutOfRangeException(
                    paramName: nameof(voxels),
                    message: $"Brick voxel {index} is not finite."
                );
            }
        }

        if ((SdfBrickPoolLayout.SlotWordOffset(slot: slot) + totalVoxels) > m_brickPoolVoxelCapacity) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(slot),
                message: $"Brick slot {slot}'s {totalVoxels} voxels exceed the pool capacity {m_brickPoolVoxelCapacity}."
            );
        }

        // The copy is recorded by a later frame, so the voxels are taken by value; a slot queued twice before a frame
        // drains keeps only the newest.
        var pending = m_brickUploads.ToArray();

        m_brickUploads.Clear();

        foreach (var entry in pending) {
            if (entry.Slot != slot) {
                m_brickUploads.Enqueue(item: entry);
            }
        }

        m_brickUploads.Enqueue(item: (slot, totalVoxels, voxels.ToArray()));
        m_brickStates[slot] = BrickBakeState.Baking;
        m_brickTotalVoxels[slot] = totalVoxels;
        m_brickVoxelCursor[slot] = totalVoxels;
    }

    /// <summary>Whether this engine provisions a brick pool (its <c>BrickPoolVoxelCapacity</c> was non-zero) — the
    /// <see cref="ISdfBrickBakeService"/> predicate the carve-bake planner checks before ever proposing a bake, so a
    /// pool-less engine keeps every carve analytic instead of throwing at <see cref="RequestBrickBake"/>.</summary>
    public bool BrickBakeAvailable => m_brickPoolEnabled;
}
