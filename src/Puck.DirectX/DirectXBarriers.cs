using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Builds the backend's enhanced barriers. Every texture transition the backend records is a
/// <see cref="Texture"/> barrier over every subresource; layouts persist across command lists, so the backend never
/// relies on the decay and promotion rules of the older resource states.
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
    /// <summary>Returns the enhanced layout a neutral image layout stands for. <see cref="GpuImageLayout.Undefined"/> is
    /// the layout whose contents a barrier from it discards.</summary>
    /// <param name="layout">The neutral layout.</param>
    /// <returns>The layout.</returns>
    internal static D3D12_BARRIER_LAYOUT Layout(GpuImageLayout layout) => layout switch {
        GpuImageLayout.General => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_UNORDERED_ACCESS,
        GpuImageLayout.ShaderReadOnly => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_SHADER_RESOURCE,
        GpuImageLayout.External => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COMMON,
        GpuImageLayout.RenderTarget => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_RENDER_TARGET,
        GpuImageLayout.DepthAttachment => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_DEPTH_STENCIL_WRITE,
        GpuImageLayout.TransferSource => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COPY_SOURCE,
        GpuImageLayout.TransferDestination => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COPY_DEST,
        _ => D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_UNDEFINED,
    };
    /// <summary>Returns every access a texture in a layout permits: the specification's layout access compatibility,
    /// which a barrier's accesses must stay inside. A barrier states the accesses its layout permits rather than the
    /// caller's masks, which also serve Vulkan's finer image layouts.</summary>
    /// <param name="layout">The neutral layout.</param>
    /// <returns>The accesses; none for an undefined layout.</returns>
    internal static D3D12_BARRIER_ACCESS LayoutAccess(GpuImageLayout layout) => layout switch {
        GpuImageLayout.General => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS,
        GpuImageLayout.ShaderReadOnly => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE,
        GpuImageLayout.External => (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST),
        GpuImageLayout.RenderTarget => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET,
        GpuImageLayout.DepthAttachment => (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_READ | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_WRITE),
        GpuImageLayout.TransferSource => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE,
        GpuImageLayout.TransferDestination => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST,
        _ => D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS,
    };
    /// <summary>Builds the barrier that moves every subresource of a texture between two layouts. Its accesses are the
    /// layouts' (<see cref="LayoutAccess"/>) and its synchronization is the caller's stages, widened to every stage
    /// wherever the stages are empty (a first use has prior work this list cannot see) or none of them can perform an
    /// access the layout permits. A transition out of an undefined layout makes no access on its before side.</summary>
    /// <param name="resource">The texture.</param>
    /// <param name="oldLayout">The layout the texture is in.</param>
    /// <param name="newLayout">The layout the texture moves to.</param>
    /// <param name="sourceStages">The stages whose accesses to the old layout the barrier waits for.</param>
    /// <param name="destinationStages">The stages that wait for the barrier.</param>
    /// <returns>The barrier, for <see cref="Record(ID3D12GraphicsCommandList7*, ReadOnlySpan{D3D12_TEXTURE_BARRIER})"/>.</returns>
    internal static D3D12_TEXTURE_BARRIER Texture(ID3D12Resource* resource, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuStage sourceStages, GpuStage destinationStages) {
        newLayout = Entered(layout: newLayout);

        var (syncBefore, accessBefore) = Side(layout: oldLayout, stages: sourceStages);
        var (syncAfter, accessAfter) = Side(layout: newLayout, stages: destinationStages);

        return new D3D12_TEXTURE_BARRIER {
            AccessAfter = accessAfter,
            AccessBefore = accessBefore,
            LayoutAfter = Layout(layout: newLayout),
            LayoutBefore = Layout(layout: oldLayout),
            Subresources = new D3D12_BARRIER_SUBRESOURCE_RANGE {
                IndexOrFirstMipLevel = DirectXConstants.AllSubresources,
                NumMipLevels = 0U,
            },
            SyncAfter = syncAfter,
            SyncBefore = syncBefore,
            pResource = resource,
        };
    }
    /// <summary>Builds the global barrier that stands for a layout transition of a texture whose layout cannot change: a
    /// simultaneous-access texture is in the common layout for life, so a transition of one orders its accesses alone.</summary>
    /// <param name="oldLayout">The layout the caller leaves.</param>
    /// <param name="newLayout">The layout the caller enters.</param>
    /// <param name="sourceStages">The stages whose accesses the barrier waits for.</param>
    /// <param name="destinationStages">The stages that wait for the barrier.</param>
    /// <returns>The barrier.</returns>
    internal static D3D12_GLOBAL_BARRIER Fixed(GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuStage sourceStages, GpuStage destinationStages) {
        newLayout = Entered(layout: newLayout);

        var (syncBefore, accessBefore) = Side(layout: oldLayout, stages: sourceStages);
        var (syncAfter, accessAfter) = Side(layout: newLayout, stages: destinationStages);

        return new D3D12_GLOBAL_BARRIER {
            AccessAfter = accessAfter,
            AccessBefore = accessBefore,
            SyncAfter = syncAfter,
            SyncBefore = syncBefore,
        };
    }
    /// <summary>Builds the barrier that hands a swap chain's back buffer to rendering, or back to presentation. A back
    /// buffer is created in the common layout, which presentation also names, and nothing in the command list waits on
    /// or after it beyond the rendering itself.</summary>
    /// <param name="backBuffer">The back buffer.</param>
    /// <param name="toPresent"><see langword="true"/> to move it from rendering to presentation, <see langword="false"/>
    /// for the reverse.</param>
    /// <returns>The barrier.</returns>
    public static D3D12_TEXTURE_BARRIER Present(ID3D12Resource* backBuffer, bool toPresent) =>
        new() {
            AccessAfter = (toPresent ? D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS : D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET),
            AccessBefore = (toPresent ? D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET : D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS),
            LayoutAfter = (toPresent ? D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COMMON : D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_RENDER_TARGET),
            LayoutBefore = (toPresent ? D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_RENDER_TARGET : D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COMMON),
            Subresources = new D3D12_BARRIER_SUBRESOURCE_RANGE {
                IndexOrFirstMipLevel = DirectXConstants.AllSubresources,
                NumMipLevels = 0U,
            },
            SyncAfter = (toPresent ? D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE : D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RENDER_TARGET),
            SyncBefore = (toPresent ? D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RENDER_TARGET : D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE),
            pResource = backBuffer,
        };
    /// <summary>Records texture barriers, as one barrier group, on a command list.</summary>
    /// <param name="commandList">The list; its enhanced-barrier interface is borrowed for the call.</param>
    /// <param name="barriers">The barriers.</param>
    /// <exception cref="InvalidOperationException">The list has no enhanced-barrier interface.</exception>
    public static void Record(ID3D12GraphicsCommandList* commandList, ReadOnlySpan<D3D12_TEXTURE_BARRIER> barriers) {
        var result = ((IUnknown*)commandList)->QueryInterface(ppvObject: out ID3D12GraphicsCommandList7* enhanced);

        if (result.Failed) {
            throw new InvalidOperationException(message: "Direct3D 12 texture transitions require enhanced barriers (ID3D12GraphicsCommandList7).");
        }

        try {
            Record(
                barriers: barriers,
                commandList: enhanced
            );
        } finally {
            _ = ((IUnknown*)enhanced)->Release();
        }
    }
    /// <summary>Records texture barriers, as one barrier group, on a list's enhanced-barrier interface.</summary>
    /// <param name="commandList">The enhanced-barrier interface.</param>
    /// <param name="barriers">The barriers.</param>
    internal static void Record(ID3D12GraphicsCommandList7* commandList, ReadOnlySpan<D3D12_TEXTURE_BARRIER> barriers) {
        if (barriers.IsEmpty) {
            return;
        }

        fixed (D3D12_TEXTURE_BARRIER* pointer = barriers) {
            var group = new D3D12_BARRIER_GROUP {
                NumBarriers = ((uint)barriers.Length),
                Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_TEXTURE,
            };

            group.Anonymous.pTextureBarriers = pointer;
            commandList->Barrier(
                NumBarrierGroups: 1,
                pBarrierGroups: &group
            );
        }
    }

    // An undefined layout can only be left, never entered: a caller that moves an image to it hands it over in the common
    // layout.
    private static GpuImageLayout Entered(GpuImageLayout layout) => ((layout == GpuImageLayout.Undefined) ? GpuImageLayout.External : layout);
    // One side of a texture barrier: the synchronization and the accesses before or after it. An undefined layout
    // makes no access; stages that name no pipeline stage, or none that can perform what the layout permits, widen to every
    // stage, which the specification accepts beside any access.
    private static (D3D12_BARRIER_SYNC Sync, D3D12_BARRIER_ACCESS Access) Side(GpuImageLayout layout, GpuStage stages) {
        var access = LayoutAccess(layout: layout);
        var sync = Sync(
            access: (GpuAccess.TransferRead | GpuAccess.TransferWrite),
            buffer: false,
            stages: stages
        );

        if (access == D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS) {
            return ((sync == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE)
                ? D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL
                : sync, access);
        }

        // The runtime checks every synchronization bit against the accesses, so a bit no access of the layout permits
        // (copy beside unordered access, clear beside a copy layout) is dropped, not merely outvoted by a compatible one.
        sync &= Permitted(access: access);

        return ((sync == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE) || !Serves(access: access, sync: sync))
            ? (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL, access)
            : (sync, access);
    }
    private const D3D12_BARRIER_SYNC Shading = (
        D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL |
        D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING |
        D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING |
        D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW |
        D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_PIXEL_SHADING |
        D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VERTEX_SHADING
    );
    // Every synchronization bit some access of the set may be paired with; the common access permits every bit.
    private static D3D12_BARRIER_SYNC Permitted(D3D12_BARRIER_ACCESS access) {
        if (access == D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON) { return ~D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE; }

        var permitted = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL;

        if ((access & D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE) != 0) { permitted |= Shading; }
        if ((access & D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS) != 0) { permitted |= (Shading | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_CLEAR_UNORDERED_ACCESS_VIEW); }
        if ((access & D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET) != 0) { permitted |= (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RENDER_TARGET); }
        if ((access & (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_READ | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_WRITE)) != 0) { permitted |= (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DEPTH_STENCIL); }
        if ((access & (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST)) != 0) { permitted |= D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY; }

        return permitted;
    }
    // Whether the synchronization holds, for every access, one of the stages the specification lets perform it.
    private static bool Serves(D3D12_BARRIER_ACCESS access, D3D12_BARRIER_SYNC sync) {
        if (((access & D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE) != 0) && ((sync & Shading) == 0)) { return false; }
        if (((access & D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS) != 0) && ((sync & (Shading | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_CLEAR_UNORDERED_ACCESS_VIEW)) == 0)) { return false; }
        if (((access & D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET) != 0) && ((sync & (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RENDER_TARGET)) == 0)) { return false; }
        if (((access & (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_READ | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_WRITE)) != 0) && ((sync & (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DEPTH_STENCIL)) == 0)) { return false; }
        if (((access & (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST)) != 0) && ((sync & (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY)) == 0)) { return false; }

        return true;
    }
}
