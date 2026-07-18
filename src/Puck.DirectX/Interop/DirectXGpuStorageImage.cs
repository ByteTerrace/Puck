using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 compute storage image implementing <see cref="IGpuStorageImage"/>. It owns a DEFAULT-heap texture
/// created with <c>ALLOW_UNORDERED_ACCESS</c> (so a compute shader writes it as a UAV and a compositor samples it as
/// an SRV), created in the <c>UNORDERED_ACCESS</c> state. <see cref="ImageHandle"/> is the raw resource (the readback
/// copies from it; the compute recorder transitions it); <see cref="ImageViewHandle"/> is a
/// <see cref="DirectXImageView"/> token the descriptor allocator turns into the UAV (compute) or SRV (sampling).
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuStorageImage : IGpuStorageImage {
    private readonly GCHandle m_imageViewToken;
    private bool m_disposed;
    private nint m_resource;

    /// <summary>Initializes a new instance, allocating the UAV-capable default-heap texture.</summary>
    public DirectXGpuStorageImage(IDirectXDeviceContext deviceContext, DXGI_FORMAT format, uint width, uint height) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        Height = height;
        Width = width;

        var device = (ID3D12Device*)deviceContext.Device.Handle;
        var heapProperties = new D3D12_HEAP_PROPERTIES {
            Type = D3D12_HEAP_TYPE.D3D12_HEAP_TYPE_DEFAULT,
        };
        var textureDesc = new D3D12_RESOURCE_DESC {
            DepthOrArraySize = 1,
            Dimension = D3D12_RESOURCE_DIMENSION.D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Flags = D3D12_RESOURCE_FLAGS.D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            Format = format,
            Height = height,
            Layout = D3D12_TEXTURE_LAYOUT.D3D12_TEXTURE_LAYOUT_UNKNOWN,
            MipLevels = 1,
            SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, },
            Width = width,
        };

        void* resource;
        var resourceIid = ID3D12Resource.IID_Guid;

        device->CreateCommittedResource(
            in heapProperties,
            D3D12_HEAP_FLAGS.D3D12_HEAP_FLAG_NONE,
            in textureDesc,
            D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS,
            (D3D12_CLEAR_VALUE?)null,
            in resourceIid,
            &resource
        );
        m_resource = (nint)resource;

        m_imageViewToken = GCHandle.Alloc(value: new DirectXImageView {
            Format = format,
            ResourceHandle = m_resource,
        });
    }

    /// <inheritdoc/>
    public nint ImageHandle => m_resource;
    /// <inheritdoc/>
    public nint ImageViewHandle => GCHandle.ToIntPtr(value: m_imageViewToken);
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public uint Width { get; }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        if (m_imageViewToken.IsAllocated) {
            m_imageViewToken.Free();
        }

        if (0 != m_resource) {
            _ = ((IUnknown*)m_resource)->Release();
            m_resource = 0;
        }
    }
}
