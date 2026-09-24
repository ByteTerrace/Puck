using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>
/// Creates the backend's committed textures, as <see cref="DirectXBuffers"/> creates its buffers. Every texture the
/// backend allocates goes through <see cref="CreateCommitted"/>, so every one is a single-mip, single-sample, 2D texture
/// on a default heap in the driver's layout, and differs only in its extent, format, heap flags, initial state, resource
/// flags and optimized clear value.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXTextures {
    /// <summary>Gets the resource flags and optimized clear value declared image usages need: a render target, a depth
    /// stencil, or unordered access, each only when its usage is declared. An optimized clear value is given only to a
    /// render target (opaque black) or a depth stencil (1, the far plane), matching the clear a render pass records, so
    /// the fast-clear path is taken and the debug layer never reports a mismatched clear.</summary>
    /// <param name="format">The texture format.</param>
    /// <param name="usage">The declared usages.</param>
    /// <returns>The resource flags, and the optimized clear value or <see langword="null"/>.</returns>
    public static (D3D12_RESOURCE_FLAGS Flags, D3D12_CLEAR_VALUE? ClearValue) OfUsage(DXGI_FORMAT format, GpuImageUsage usage) {
        var flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE;
        D3D12_CLEAR_VALUE? clearValue = null;

        if ((usage & GpuImageUsage.Storage) != 0) {
            flags |= D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        }

        if ((usage & GpuImageUsage.ColorAttachment) != 0) {
            flags |= D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
            clearValue = DirectXConstants.BlackClearValue(format: format);
        }

        if ((usage & GpuImageUsage.DepthAttachment) != 0) {
            flags |= D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_DEPTH_STENCIL;

            var depthClear = new D3D12_CLEAR_VALUE {
                Format = format,
            };

            depthClear.Anonymous.DepthStencil = new D3D12_DEPTH_STENCIL_VALUE {
                Depth = 1f,
                Stencil = 0,
            };
            clearValue = depthClear;
        }

        return (flags, clearValue);
    }
    /// <summary>Gets the state a texture with declared usages is created in, and rests in until its first barrier: a
    /// storage image in <c>UNORDERED_ACCESS</c>, a render target in <c>RENDER_TARGET</c>, a depth stencil in
    /// <c>DEPTH_WRITE</c>, and a texture only ever sampled in the shader-read state.</summary>
    /// <param name="usage">The declared usages.</param>
    /// <returns>The initial resource state.</returns>
    public static D3D12_RESOURCE_STATES InitialStateOf(GpuImageUsage usage) =>
        (((usage & GpuImageUsage.Storage) != 0)
            ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS
            : (((usage & GpuImageUsage.ColorAttachment) != 0)
                ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET
                : (((usage & GpuImageUsage.DepthAttachment) != 0)
                    ? D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE
                    : DirectXResourceStates.ShaderRead)));
    /// <summary>Creates a committed 2D texture.</summary>
    /// <param name="device">The device.</param>
    /// <param name="format">The texture format.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="initialState">The state it is created in.</param>
    /// <param name="flags">Its resource flags.</param>
    /// <param name="heapFlags">Its heap flags; <c>SHARED</c> for a texture another device opens.</param>
    /// <param name="clearValue">The optimized clear value of a render target or depth stencil, or
    /// <see langword="null"/>.</param>
    /// <returns>The texture, owned by the caller.</returns>
    public static ID3D12Resource* CreateCommitted(ID3D12Device* device, DXGI_FORMAT format, uint width, uint height, D3D12_RESOURCE_STATES initialState, D3D12_RESOURCE_FLAGS flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_NONE, D3D12_HEAP_FLAGS heapFlags = D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE, D3D12_CLEAR_VALUE? clearValue = null) {
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
        };
        var description = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Flags = flags,
            Format = format,
            Height = height,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = width,
        };
        var resourceIid = ID3D12Resource.IID_Guid;
        void* texture;

        device->CreateCommittedResource(
            HeapFlags: heapFlags,
            InitialResourceState: initialState,
            pDesc: in description,
            pHeapProperties: in heapProperties,
            pOptimizedClearValue: clearValue,
            ppvResource: &texture,
            riidResource: in resourceIid
        );

        return ((ID3D12Resource*)texture);
    }
}
