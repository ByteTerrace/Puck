using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

public sealed unsafe partial class DirectXGpuRecorder {
    /// <inheritdoc/>
    public void BuildAccelerationStructure(nint commandBufferHandle, in GpuAccelerationStructureInputs inputs, IGpuAccelerationStructure destination, IGpuBuffer scratch) {
        ArgumentNullException.ThrowIfNull(argument: scratch);
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        // Every earlier copy, kernel or build write is visible to the build's reads of its inputs and scratch.
        GlobalBarrier(state: state,
            accessAfter: (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS |
                D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_READ | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE),
            accessBefore: (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS |
                D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE),
            syncAfter: D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE,
            syncBefore: (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING |
                D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY_RAYTRACING_ACCELERATION_STRUCTURE));

        var geometry = default(D3D12_RAYTRACING_GEOMETRY_DESC);
        var description = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC {
            DestAccelerationStructureData = DirectXGpuAccelerationStructures.AddressOf(structure: destination),
            Inputs = DirectXGpuAccelerationStructures.Describe(addresses: true, geometry: &geometry, inputs: in inputs),
            ScratchAccelerationStructureData = ((ID3D12Resource*)scratch.BufferHandle)->GetGPUVirtualAddress(),
        };

        ((ID3D12GraphicsCommandList4*)state.EnhancedCommandList)->BuildRaytracingAccelerationStructure(&description, 0, null);
        StructureWritten(state: state);
    }
    /// <inheritdoc/>
    public void WriteCompactedSize(nint commandBufferHandle, IGpuAccelerationStructure source, IGpuBuffer destination, ulong destinationOffsetBytes) {
        ArgumentNullException.ThrowIfNull(argument: destination);
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        // A copy that read the buffer before finishes before the write; the write is visible to the copy after it.
        GlobalBarrier(state: state,
            accessAfter: D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS, accessBefore: D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE,
            syncAfter: D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EMIT_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO, syncBefore: D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY);

        var description = new D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC {
            DestBuffer = (((ID3D12Resource*)destination.BufferHandle)->GetGPUVirtualAddress() + destinationOffsetBytes),
            InfoType = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_TYPE.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_COMPACTED_SIZE,
        };
        var address = DirectXGpuAccelerationStructures.AddressOf(structure: source);

        ((ID3D12GraphicsCommandList4*)state.EnhancedCommandList)->EmitRaytracingAccelerationStructurePostbuildInfo(&description, 1, &address);
        GlobalBarrier(state: state,
            accessAfter: D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE, accessBefore: D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS,
            syncAfter: D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY, syncBefore: D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EMIT_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO);
    }
    /// <inheritdoc/>
    public void CompactAccelerationStructure(nint commandBufferHandle, IGpuAccelerationStructure source, IGpuAccelerationStructure destination) {
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        FlushTextureBarriers(state);
        ((ID3D12GraphicsCommandList4*)state.EnhancedCommandList)->CopyRaytracingAccelerationStructure(
            DirectXGpuAccelerationStructures.AddressOf(structure: destination),
            DirectXGpuAccelerationStructures.AddressOf(structure: source),
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_COPY_MODE.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_COPY_MODE_COMPACT
        );
        StructureWritten(state: state);
    }

    // Makes a build's or copy's structure write, and its scratch write, visible to every later build, copy, post-build
    // read and kernel.
    private static void StructureWritten(DirectXCommandBufferState state) =>
        GlobalBarrier(state: state,
            accessAfter: (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_READ | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS),
            accessBefore: (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS),
            syncAfter: (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY_RAYTRACING_ACCELERATION_STRUCTURE |
                D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EMIT_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING),
            syncBefore: (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY_RAYTRACING_ACCELERATION_STRUCTURE));
    // Records one global barrier after the texture transitions queued before it: the one door a global barrier takes,
    // MemoryBarrier's included.
    private static void GlobalBarrier(DirectXCommandBufferState state, D3D12_BARRIER_SYNC syncBefore, D3D12_BARRIER_SYNC syncAfter, D3D12_BARRIER_ACCESS accessBefore, D3D12_BARRIER_ACCESS accessAfter) {
        FlushTextureBarriers(state);
        var barrier = new D3D12_GLOBAL_BARRIER {
            AccessAfter = accessAfter,
            AccessBefore = accessBefore,
            SyncAfter = syncAfter,
            SyncBefore = syncBefore,
        };
        var group = new D3D12_BARRIER_GROUP {
            NumBarriers = 1,
            Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_GLOBAL,
        };

        group.Anonymous.pGlobalBarriers = &barrier;
        ((ID3D12GraphicsCommandList7*)state.EnhancedCommandList)->Barrier(1, &group);
    }
}
