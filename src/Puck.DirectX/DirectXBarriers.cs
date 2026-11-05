using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Builds the backend's resource barriers. Every transition the backend records goes through
/// <see cref="Transition"/>, so every one covers the whole resource and carries no split-barrier flag.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXBarriers {
    internal static D3D12_BARRIER_SYNC Sync(GpuStage stages, GpuAccess access, bool buffer = false) {
        if ((stages & ~(GpuStage.Host | GpuStage.TopOfPipe)) == 0 || access == GpuAccess.None) {
            return D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE;
        }
        var sync = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE;
        if ((stages & GpuStage.ComputeShader) != 0) { sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING; }
        if ((stages & GpuStage.VertexShader) != 0) { sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VERTEX_SHADING; }
        if ((stages & GpuStage.FragmentShader) != 0) { sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_PIXEL_SHADING; }
        if ((stages & GpuStage.DrawIndirect) != 0) { sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EXECUTE_INDIRECT; }
        if (!buffer) {
            if ((stages & GpuStage.ColorAttachmentOutput) != 0) { sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RENDER_TARGET; }
            if ((stages & GpuStage.FragmentTests) != 0) { sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DEPTH_STENCIL; }
        }
        if ((stages & GpuStage.Transfer) != 0) {
            if ((access & (GpuAccess.CopyWrite | GpuAccess.TransferRead)) != 0 || (!buffer && (access & GpuAccess.TransferWrite) != 0)) { sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY; }
            if ((access & GpuAccess.TransferWrite) != 0) { sync |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_CLEAR_UNORDERED_ACCESS_VIEW; }
        }
        return sync;
    }
    internal static D3D12_BARRIER_ACCESS Access(GpuAccess access, GpuStage stages, bool buffer) {
        if (Sync(stages, access, buffer) == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE) { return D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS; }
        var result = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON;
        if ((access & GpuAccess.ShaderRead) != 0 && (!buffer || (access & GpuAccess.ShaderWrite) == 0)) { result |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE; }
        if ((access & (GpuAccess.ShaderWrite | GpuAccess.TransferWrite)) != 0) { result |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS; }
        if ((access & GpuAccess.IndirectCommandRead) != 0) { result |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_INDIRECT_ARGUMENT; }
        if ((access & GpuAccess.TransferRead) != 0) { result |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE; }
        if ((access & GpuAccess.CopyWrite) != 0 || (!buffer && (access & GpuAccess.TransferWrite) != 0)) { result |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST; }
        if (!buffer) {
            if ((access & (GpuAccess.ColorAttachmentRead | GpuAccess.ColorAttachmentWrite)) != 0) { result |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET; }
            if ((access & GpuAccess.DepthAttachmentRead) != 0) { result |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_READ; }
            if ((access & GpuAccess.DepthAttachmentWrite) != 0) { result |= D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_WRITE; }
        }
        return result;
    }
    /// <summary>Builds a transition of every subresource of <paramref name="resource"/> from
    /// <paramref name="before"/> to <paramref name="after"/>, recorded whole (<c>D3D12_RESOURCE_BARRIER_FLAG_NONE</c>).</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="before">The state the resource is in.</param>
    /// <param name="after">The state the resource moves to.</param>
    /// <returns>The barrier, for <c>ResourceBarrier</c>.</returns>
    public static D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* resource, D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after) {
        var barrier = new D3D12_RESOURCE_BARRIER {
            Flags = D3D12_RESOURCE_BARRIER_FLAGS.D3D12_RESOURCE_BARRIER_FLAG_NONE,
            Type = D3D12_RESOURCE_BARRIER_TYPE.D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        };

        barrier.Anonymous.Transition = new D3D12_RESOURCE_TRANSITION_BARRIER {
            StateAfter = after,
            StateBefore = before,
            Subresource = DirectXConstants.AllSubresources,
            pResource = resource,
        };

        return barrier;
    }
}
