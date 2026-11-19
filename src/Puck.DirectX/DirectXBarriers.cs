using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

/// <summary>
/// Builds and records every enhanced barrier the backend issues: texture transitions, buffer and global dependencies, the
/// swap chain's hand-overs and the acceleration-structure orderings. Every texture transition is a <see cref="Texture"/>
/// barrier over every subresource; layouts persist across command lists, so the backend never relies on the decay and
/// promotion rules of the older resource states.
/// </summary>
/// <remarks>
/// <para>Each side of a barrier is a layout, an access set and a sync set, and the runtime refuses the whole command
/// list (<c>Close</c> fails with E_INVALIDARG) when they disagree. Two tables decide them, both transcribed from
/// Microsoft's Enhanced Barriers specification
/// (<see href="https://microsoft.github.io/DirectX-Specs/d3d/D3D12EnhancedBarriers.html"/>):</para>
/// <list type="bullet">
/// <item><description><see cref="LayoutAccess"/> holds "Layout Access Compatibility": the accesses a texture side may
/// state in each layout. A side states every access its layout permits, never the caller's masks, which serve Vulkan's
/// finer image layouts. One deviation follows the runtime instead of the table: the table lists
/// <c>DEPTH_STENCIL_READ</c> beside <c>DEPTH_STENCIL_WRITE</c> for the depth-write layout, but the Direct3D 12 debug
/// layer refuses the read bit there ("AccessAfter bits D3D12_BARRIER_ACCESS_DEPTH_STENCIL_READ are incompatible with
/// LayoutAfter D3D12_BARRIER_LAYOUT_DEPTH_STENCIL_WRITE"), and the specification's own legacy mapping gives
/// <c>D3D12_RESOURCE_STATE_DEPTH_WRITE</c> the write access alone, which also covers the depth test's reads.</description></item>
/// <item><description><see cref="SyncsServing"/> holds "Access Bits Barrier Sync Compatibility": for each access bit,
/// the sync bits of which at least one must accompany it. The runtime also checks the converse, refusing a sync bit no
/// access of the side permits ("SyncAfter bits D3D12_BARRIER_SYNC_COPY are incompatible with AccessAfter bits
/// D3D12_BARRIER_ACCESS_UNORDERED_ACCESS"), so <see cref="Paired"/> keeps only the permitted sync bits and widens to
/// <c>SYNC_ALL</c>, which every access accepts, when what remains does not serve every access.</description></item>
/// </list>
/// <para>Every side this class builds passes through <see cref="Paired"/> or is a constant checked against the same
/// tables, and <c>DirectXBarrierCompatibilityLawTests</c> holds every side the builders can produce to an independent
/// transcription of the specification's tables. Where the specification's web page truncates, the table rows were read
/// from its source (<c>d3d/D3D12EnhancedBarriers.md</c> in microsoft/DirectX-Specs); the SDK header
/// (<c>d3d12.h</c>, 10.0.26100.0) defines the enumerations and carries no compatibility table.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXBarriers {
    /// <summary>Returns the enhanced layout a neutral image layout stands for. <see cref="GpuImageLayout.Undefined"/> is
    /// the layout whose contents a barrier from it discards.</summary>
    /// <param name="layout">The neutral layout.</param>
    /// <returns>The layout.</returns>
    public static D3D12_BARRIER_LAYOUT Layout(GpuImageLayout layout) => Row(layout: layout).Layout;
    /// <summary>Returns every access a texture side states in a layout: the specification's layout access compatibility
    /// (see the class remarks for the depth-write row).</summary>
    /// <param name="layout">The neutral layout.</param>
    /// <returns>The accesses; none for an undefined layout.</returns>
    public static D3D12_BARRIER_ACCESS LayoutAccess(GpuImageLayout layout) => Row(layout: layout).Access;
    /// <summary>Returns the sync bits of which at least one must accompany an access bit in a barrier: the specification's
    /// access bits barrier sync compatibility. The common access and no access accept any sync, which an unknown bit
    /// narrows to <c>SYNC_ALL</c>.</summary>
    /// <param name="access">One access bit.</param>
    /// <returns>The sync bits that serve it.</returns>
    public static D3D12_BARRIER_SYNC SyncsServing(D3D12_BARRIER_ACCESS access) => access switch {
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_VERTEX_BUFFER => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VERTEX_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_CONSTANT_BUFFER => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | Shaders | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_INDEX_BUFFER => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_INDEX_INPUT | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RENDER_TARGET),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | Shaders | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_CLEAR_UNORDERED_ACCESS_VIEW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EMIT_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RAYTRACING),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_WRITE or D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_READ => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DEPTH_STENCIL),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | Shaders | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RAYTRACING),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_STREAM_OUTPUT => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VERTEX_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_DRAW | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING),
        // d3d12.h gives the predication access the indirect-argument bit, so the one bit takes both rows' syncs.
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_INDIRECT_ARGUMENT => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EXECUTE_INDIRECT | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_PREDICATION),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST or D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RESOLVE_DEST or D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RESOLVE_SOURCE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RESOLVE),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_READ => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RAYTRACING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EMIT_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_RAYTRACING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_BUILD_RAYTRACING_ACCELERATION_STRUCTURE | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY_RAYTRACING_ACCELERATION_STRUCTURE),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADING_RATE_SOURCE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_PIXEL_SHADING | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL_SHADING),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_VIDEO_DECODE_READ or D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_VIDEO_DECODE_WRITE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VIDEO_DECODE),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_VIDEO_PROCESS_READ or D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_VIDEO_PROCESS_WRITE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VIDEO_PROCESS),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_VIDEO_ENCODE_READ or D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_VIDEO_ENCODE_WRITE => (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL | D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VIDEO_ENCODE),
        D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON or D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS => AnySync,
        _ => D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL,
    };
    /// <summary>Returns one barrier side's sync made compatible with its accesses, both ways: only the sync bits some
    /// access permits stay, and a sync that then serves not every access (or nothing) widens to <c>SYNC_ALL</c>, which
    /// every access accepts. No access keeps any sync, none included.</summary>
    /// <param name="sync">The sync the caller's stages name.</param>
    /// <param name="access">The side's accesses.</param>
    /// <returns>The side.</returns>
    public static (D3D12_BARRIER_SYNC Sync, D3D12_BARRIER_ACCESS Access) Paired(D3D12_BARRIER_SYNC sync, D3D12_BARRIER_ACCESS access) {
        if (access == D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS) {
            return (sync, access);
        }

        var permitted = AnySync;
        var served = true;

        if (access != D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON) {
            permitted = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE;

            for (var bits = ((uint)access); (bits != 0U); bits &= (bits - 1U)) {
                permitted |= SyncsServing(access: ((D3D12_BARRIER_ACCESS)(bits & (0U - bits))));
            }
        }

        sync &= permitted;

        for (var bits = ((uint)access); (bits != 0U) && served; bits &= (bits - 1U)) {
            served = ((sync & SyncsServing(access: ((D3D12_BARRIER_ACCESS)(bits & (0U - bits))))) != 0);
        }

        return (((sync == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE) || !served) ? D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL : sync, access);
    }
    /// <summary>Builds the barrier that moves every subresource of a texture between two layouts. Its accesses are the
    /// layouts' (<see cref="LayoutAccess"/>) and its synchronization is the caller's stages, made compatible with them
    /// (<see cref="Paired"/>) and widened to every stage wherever the stages are empty (a first use has prior work this
    /// list cannot see). A transition out of an undefined layout makes no access on its before side.</summary>
    /// <param name="resource">The texture.</param>
    /// <param name="oldLayout">The layout the texture is in.</param>
    /// <param name="newLayout">The layout the texture moves to.</param>
    /// <param name="sourceStages">The stages whose accesses to the old layout the barrier waits for.</param>
    /// <param name="destinationStages">The stages that wait for the barrier.</param>
    /// <returns>The barrier, for <see cref="Record(ID3D12GraphicsCommandList7*, ReadOnlySpan{D3D12_TEXTURE_BARRIER})"/>.</returns>
    public static D3D12_TEXTURE_BARRIER Texture(ID3D12Resource* resource, GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuStage sourceStages, GpuStage destinationStages) {
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
    /// <summary>Whether a transition between two neutral layouts changes the enhanced layout: one that does not records
    /// nothing, since a barrier that keeps the layout is a memory barrier.</summary>
    /// <param name="oldLayout">The layout the texture is in.</param>
    /// <param name="newLayout">The layout the texture moves to.</param>
    /// <returns><see langword="true"/> when a texture barrier is needed.</returns>
    public static bool ChangesLayout(GpuImageLayout oldLayout, GpuImageLayout newLayout) =>
        (Layout(layout: oldLayout) != Layout(layout: Entered(layout: newLayout)));
    /// <summary>Builds the global barrier that stands for a layout transition of a texture whose layout cannot change: a
    /// simultaneous-access texture is in the common layout for life, so a transition of one orders its accesses alone.</summary>
    /// <param name="oldLayout">The layout the caller leaves.</param>
    /// <param name="newLayout">The layout the caller enters.</param>
    /// <param name="sourceStages">The stages whose accesses the barrier waits for.</param>
    /// <param name="destinationStages">The stages that wait for the barrier.</param>
    /// <returns>The barrier.</returns>
    public static D3D12_GLOBAL_BARRIER Fixed(GpuImageLayout oldLayout, GpuImageLayout newLayout, GpuStage sourceStages, GpuStage destinationStages) {
        newLayout = Entered(layout: newLayout);

        return Sides(
            after: Side(layout: newLayout, stages: destinationStages),
            before: Side(layout: oldLayout, stages: sourceStages)
        );
    }
    /// <summary>Builds the global barrier that orders the accesses two neutral masks name across every resource.</summary>
    /// <param name="sourceAccess">The accesses the barrier makes visible.</param>
    /// <param name="destinationAccess">The accesses that wait for it.</param>
    /// <param name="sourceStages">The stages whose accesses the barrier waits for.</param>
    /// <param name="destinationStages">The stages that wait for the barrier.</param>
    /// <returns>The barrier.</returns>
    public static D3D12_GLOBAL_BARRIER Global(GpuAccess sourceAccess, GpuAccess destinationAccess, GpuStage sourceStages, GpuStage destinationStages) =>
        Sides(
            after: Side(access: destinationAccess, buffer: false, stages: destinationStages),
            before: Side(access: sourceAccess, buffer: false, stages: sourceStages)
        );
    /// <summary>Builds the barrier that orders a default-heap buffer's accesses. A buffer has no layout. A source that
    /// names no device work (a host write, a first use) still waits for every earlier access with every access, since an
    /// earlier list in the same execution may have touched the buffer, and a buffer that cannot be unordered-accessed
    /// states no unordered write before it.</summary>
    /// <param name="resource">The buffer.</param>
    /// <param name="unorderedAccess">Whether the buffer was created to allow unordered access.</param>
    /// <param name="sourceAccess">The accesses the barrier makes visible.</param>
    /// <param name="destinationAccess">The accesses that wait for it.</param>
    /// <param name="sourceStages">The stages whose accesses the barrier waits for.</param>
    /// <param name="destinationStages">The stages that wait for the barrier.</param>
    /// <returns>The barrier.</returns>
    public static D3D12_BUFFER_BARRIER Buffer(ID3D12Resource* resource, bool unorderedAccess, GpuAccess sourceAccess, GpuAccess destinationAccess, GpuStage sourceStages, GpuStage destinationStages) {
        if (!unorderedAccess) {
            sourceAccess &= ~(GpuAccess.ShaderWrite | GpuAccess.TransferWrite);
        }

        var (syncBefore, accessBefore) = Side(access: sourceAccess, buffer: true, stages: sourceStages);
        var (syncAfter, accessAfter) = Side(access: destinationAccess, buffer: true, stages: destinationStages);

        if (syncBefore == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE) {
            (syncBefore, accessBefore) = (D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL, D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COMMON);
        }

        return new D3D12_BUFFER_BARRIER {
            AccessAfter = accessAfter,
            AccessBefore = accessBefore,
            Size = ulong.MaxValue,
            SyncAfter = syncAfter,
            SyncBefore = syncBefore,
            pResource = resource,
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
    /// <summary>The dependency before an acceleration-structure build: every earlier copy, kernel or build write is
    /// visible to the build's reads of its inputs and its scratch. Scratch is unordered access, which no
    /// acceleration-structure sync carries, so both sides take <c>SYNC_ALL</c>.</summary>
    public static D3D12_GLOBAL_BARRIER AccelerationStructureInputs => new() {
        AccessAfter = (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS |
            D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_READ | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE),
        AccessBefore = (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS |
            D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE),
        SyncAfter = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL,
        SyncBefore = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL,
    };
    /// <summary>The dependency after a build or copy: its structure write, and its scratch write, are visible to every
    /// later build, copy, post-build read and kernel; both sides take <c>SYNC_ALL</c> for the scratch's unordered
    /// access.</summary>
    public static D3D12_GLOBAL_BARRIER AccelerationStructureWritten => new() {
        AccessAfter = (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_READ | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS),
        AccessBefore = (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RAYTRACING_ACCELERATION_STRUCTURE_WRITE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS),
        SyncAfter = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL,
        SyncBefore = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL,
    };
    /// <summary>The dependency before a post-build size write: a copy that read the destination buffer finishes first.</summary>
    public static D3D12_GLOBAL_BARRIER PostbuildInfoWrite => new() {
        AccessAfter = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS,
        AccessBefore = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE,
        SyncAfter = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EMIT_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO,
        SyncBefore = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY,
    };
    /// <summary>The dependency after a post-build size write: the write is visible to the copy that reads it.</summary>
    public static D3D12_GLOBAL_BARRIER PostbuildInfoRead => new() {
        AccessAfter = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE,
        AccessBefore = D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS,
        SyncAfter = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COPY,
        SyncBefore = D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_EMIT_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO,
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
    public static void Record(ID3D12GraphicsCommandList7* commandList, ReadOnlySpan<D3D12_TEXTURE_BARRIER> barriers) {
        if (barriers.IsEmpty) {
            return;
        }

        fixed (D3D12_TEXTURE_BARRIER* pointer = barriers) {
            var group = new D3D12_BARRIER_GROUP {
                NumBarriers = ((uint)barriers.Length),
                Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_TEXTURE,
            };

            group.Anonymous.pTextureBarriers = pointer;
            Record(commandList: commandList, group: group);
        }
    }
    /// <summary>Records one buffer barrier on a list's enhanced-barrier interface.</summary>
    /// <param name="commandList">The enhanced-barrier interface.</param>
    /// <param name="barrier">The barrier, from <see cref="Buffer"/>.</param>
    public static void Record(ID3D12GraphicsCommandList7* commandList, D3D12_BUFFER_BARRIER barrier) {
        var group = new D3D12_BARRIER_GROUP {
            NumBarriers = 1,
            Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_BUFFER,
        };

        group.Anonymous.pBufferBarriers = &barrier;
        Record(commandList: commandList, group: group);
    }
    /// <summary>Records one global barrier on a list's enhanced-barrier interface.</summary>
    /// <param name="commandList">The enhanced-barrier interface.</param>
    /// <param name="barrier">The barrier, from <see cref="Global"/>, <see cref="Fixed"/> or one of the
    /// acceleration-structure constants.</param>
    public static void Record(ID3D12GraphicsCommandList7* commandList, D3D12_GLOBAL_BARRIER barrier) {
        var group = new D3D12_BARRIER_GROUP {
            NumBarriers = 1,
            Type = D3D12_BARRIER_TYPE.D3D12_BARRIER_TYPE_GLOBAL,
        };

        group.Anonymous.pGlobalBarriers = &barrier;
        Record(commandList: commandList, group: group);
    }

    // The one native Barrier call; the group's barriers stay pinned by the caller for its duration.
    private static void Record(ID3D12GraphicsCommandList7* commandList, D3D12_BARRIER_GROUP group) =>
        commandList->Barrier(
            NumBarrierGroups: 1,
            pBarrierGroups: &group
        );
    private static D3D12_GLOBAL_BARRIER Sides((D3D12_BARRIER_SYNC Sync, D3D12_BARRIER_ACCESS Access) before, (D3D12_BARRIER_SYNC Sync, D3D12_BARRIER_ACCESS Access) after) =>
        new() {
            AccessAfter = after.Access,
            AccessBefore = before.Access,
            SyncAfter = after.Sync,
            SyncBefore = before.Sync,
        };
    // Every sync bit, for an access that accepts any; never ANDed into a sync except with a real one.
    private const D3D12_BARRIER_SYNC AnySync = ~D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE;
    // The three programmable shader stages a stage mask names.
    private const D3D12_BARRIER_SYNC Shaders = (
        D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_COMPUTE_SHADING |
        D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_PIXEL_SHADING |
        D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_VERTEX_SHADING
    );

    // The specification's layout access compatibility, one row per neutral layout (the class remarks give the
    // depth-write row's deviation).
    private static (D3D12_BARRIER_LAYOUT Layout, D3D12_BARRIER_ACCESS Access) Row(GpuImageLayout layout) => layout switch {
        GpuImageLayout.General => (D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_UNORDERED_ACCESS, D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_UNORDERED_ACCESS),
        GpuImageLayout.ShaderReadOnly => (D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_SHADER_RESOURCE, D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE),
        GpuImageLayout.External => (D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COMMON, (D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_SHADER_RESOURCE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE | D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST)),
        GpuImageLayout.RenderTarget => (D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_RENDER_TARGET, D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_RENDER_TARGET),
        GpuImageLayout.DepthAttachment => (D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_DEPTH_STENCIL_WRITE, D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_DEPTH_STENCIL_WRITE),
        GpuImageLayout.TransferSource => (D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COPY_SOURCE, D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_SOURCE),
        GpuImageLayout.TransferDestination => (D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_COPY_DEST, D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_COPY_DEST),
        _ => (D3D12_BARRIER_LAYOUT.D3D12_BARRIER_LAYOUT_UNDEFINED, D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS),
    };
    // An undefined layout can only be left, never entered: a caller that moves an image to it hands it over in the common
    // layout.
    private static GpuImageLayout Entered(GpuImageLayout layout) => ((layout == GpuImageLayout.Undefined) ? GpuImageLayout.External : layout);
    // One side of a texture barrier: the layout's accesses with the sync the stages name, paired. An undefined layout
    // makes no access; stages that name no pipeline stage widen to every stage.
    private static (D3D12_BARRIER_SYNC Sync, D3D12_BARRIER_ACCESS Access) Side(GpuImageLayout layout, GpuStage stages) {
        var sync = Sync(
            access: (GpuAccess.TransferRead | GpuAccess.TransferWrite),
            buffer: false,
            stages: stages
        );
        var (paired, access) = Paired(
            access: LayoutAccess(layout: layout),
            sync: sync
        );

        return ((paired == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE) ? D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_ALL : paired, access);
    }
    // One side of a buffer or global barrier: the accesses the masks name with the sync their stages name, paired. A side
    // whose stages name no device work makes no access.
    private static (D3D12_BARRIER_SYNC Sync, D3D12_BARRIER_ACCESS Access) Side(GpuAccess access, bool buffer, GpuStage stages) {
        var sync = Sync(
            access: access,
            buffer: buffer,
            stages: stages
        );

        return (sync == D3D12_BARRIER_SYNC.D3D12_BARRIER_SYNC_NONE)
            ? (sync, D3D12_BARRIER_ACCESS.D3D12_BARRIER_ACCESS_NO_ACCESS)
            : Paired(
                access: Access(access: access, buffer: buffer),
                sync: sync
            );
    }
    // The sync a stage mask names for its accesses; none when it names no device stage or no access.
    private static D3D12_BARRIER_SYNC Sync(GpuStage stages, GpuAccess access, bool buffer) {
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
    // The accesses a neutral mask names. A buffer is never a shader resource and an unordered access at once, and never
    // an attachment; a mask that names no device access (a host read) is the common access.
    private static D3D12_BARRIER_ACCESS Access(GpuAccess access, bool buffer) {
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
}
