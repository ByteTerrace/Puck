using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 <see cref="IGpuImage"/>: a default-heap texture created by <see cref="DirectXTextures"/> with the
/// resource flags, initial state and optimized clear value its declared usages need. <see cref="ImageHandle"/> is the raw
/// resource (barriers, copies and readbacks name it); <see cref="ImageViewHandle"/> is a <see cref="DirectXImageView"/>
/// token the descriptor allocator turns into a UAV or SRV, and a framebuffer into a render-target or depth-stencil view.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuImage : IGpuImage {
    private readonly GCHandle m_imageViewToken;

    private bool m_disposed;
    private nint m_resource;

    /// <summary>Initializes a new instance, allocating the texture.</summary>
    /// <param name="request">The device context, format, and extent the image is allocated with.</param>
    /// <param name="format">The neutral format, which <paramref name="request"/> carries translated.</param>
    /// <param name="usage">The declared usages; <see cref="GpuImageUsages.Validate"/> refuses a request that breaks a
    /// rule before anything is created.</param>
    public DirectXGpuImage(DirectXGpuImageRequest request, GpuPixelFormat format, GpuImageUsage usage) {
        var (deviceContext, dxgiFormat, width, height) = request;

        ArgumentNullException.ThrowIfNull(deviceContext);
        GpuImageUsages.Validate(
            format: format,
            height: height,
            usage: usage,
            width: width
        );

        Format = format;
        Height = height;
        Usage = usage;
        Width = width;

        var (flags, clearValue) = DirectXTextures.OfUsage(
            format: dxgiFormat,
            usage: usage
        );
        var initialState = DirectXTextures.InitialStateOf(usage: usage);

        m_resource = ((nint)DirectXTextures.CreateCommitted(
            clearValue: clearValue,
            device: ((ID3D12Device*)deviceContext.Device.Handle),
            flags: flags,
            format: dxgiFormat,
            height: height,
            initialState: initialState,
            width: width
        ));
        DirectXResourceStates.Register(
            resource: m_resource,
            state: initialState
        );
        m_imageViewToken = GCHandle.Alloc(value: new DirectXImageView {
            Format = dxgiFormat,
            ResourceHandle = m_resource,
        });
    }

    /// <inheritdoc/>
    public GpuPixelFormat Format { get; }
    /// <inheritdoc/>
    public uint Height { get; }
    /// <inheritdoc/>
    public nint ImageHandle => m_resource;
    /// <inheritdoc/>
    public nint ImageViewHandle => GCHandle.ToIntPtr(value: m_imageViewToken);
    /// <inheritdoc/>
    public GpuImageUsage Usage { get; }
    /// <inheritdoc/>
    public uint Width { get; }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        DirectXResourceStates.Forget(resource: m_resource);

        if (m_imageViewToken.IsAllocated) {
            m_imageViewToken.Free();
        }

        if (0 != m_resource) {
            _ = ((IUnknown*)m_resource)->Release();
            m_resource = 0;
        }
    }
}
