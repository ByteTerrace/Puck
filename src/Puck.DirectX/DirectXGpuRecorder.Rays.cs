using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

public sealed unsafe partial class DirectXGpuRecorder {
    /// <inheritdoc/>
    public void BuildAccelerationStructure(nint commandBufferHandle, in GpuAccelerationStructureInputs inputs, IGpuAccelerationStructure destination, IGpuBuffer scratch) {
        ArgumentNullException.ThrowIfNull(argument: scratch);
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        GlobalBarrier(barrier: DirectXBarriers.AccelerationStructureInputs, state: state);

        var geometry = default(D3D12_RAYTRACING_GEOMETRY_DESC);
        var description = new D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC {
            DestAccelerationStructureData = DirectXGpuAccelerationStructures.AddressOf(structure: destination),
            Inputs = DirectXGpuAccelerationStructures.Describe(geometry: &geometry, inputs: in inputs),
            ScratchAccelerationStructureData = ((ID3D12Resource*)scratch.BufferHandle)->GetGPUVirtualAddress(),
        };

        ((ID3D12GraphicsCommandList4*)state.EnhancedCommandList)->BuildRaytracingAccelerationStructure(&description, 0, null);
        StructureWritten(state: state);
    }
    /// <inheritdoc/>
    public void WriteCompactedSize(nint commandBufferHandle, IGpuAccelerationStructure source, IGpuBuffer destination, ulong destinationOffsetBytes) {
        ArgumentNullException.ThrowIfNull(argument: destination);
        var state = DecodeState(commandBufferHandle: commandBufferHandle);

        GlobalBarrier(barrier: DirectXBarriers.PostbuildInfoWrite, state: state);

        var description = new D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC {
            DestBuffer = (((ID3D12Resource*)destination.BufferHandle)->GetGPUVirtualAddress() + destinationOffsetBytes),
            InfoType = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_TYPE.D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_COMPACTED_SIZE,
        };
        var address = DirectXGpuAccelerationStructures.AddressOf(structure: source);

        ((ID3D12GraphicsCommandList4*)state.EnhancedCommandList)->EmitRaytracingAccelerationStructurePostbuildInfo(&description, 1, &address);
        GlobalBarrier(barrier: DirectXBarriers.PostbuildInfoRead, state: state);
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
        GlobalBarrier(barrier: DirectXBarriers.AccelerationStructureWritten, state: state);
    // Records one global barrier after the texture transitions queued before it: the one door a global barrier takes,
    // MemoryBarrier's included.
    private static void GlobalBarrier(DirectXCommandBufferState state, D3D12_GLOBAL_BARRIER barrier) {
        FlushTextureBarriers(state);
        DirectXBarriers.Record(
            barrier: barrier,
            commandList: ((ID3D12GraphicsCommandList7*)state.EnhancedCommandList)
        );
    }
}
