using System.Runtime.Versioning;
using Puck.DirectX.Apis;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Interop;

/// <summary>
/// A Direct3D 12 <see cref="IGpuImage"/>: a default-heap texture created by <see cref="DirectXTextures"/> with the
/// resource flags, initial state and optimized clear value its declared usages need. <see cref="ImageHandle"/> is the raw
/// resource (barriers, copies and readbacks name it); <see cref="ImageViewHandle"/> is a <see cref="DirectXImageView"/>
/// handle in <see cref="DirectXImageViews"/>, which <see cref="DirectXGpuBindings"/> turns into a UAV or SRV, and a framebuffer into a render-target or depth-stencil view.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuImage : IGpuImage {
    private readonly nint m_imageViewHandle;
    private readonly GpuDeviceMemoryWork? m_memory;

    private bool m_disposed;
    private nint m_resource;

    /// <summary>Initializes a new instance, allocating the texture.</summary>
    /// <param name="request">The device context, format, and extent the image is allocated with.</param>
    /// <param name="format">The neutral format, which <paramref name="request"/> carries translated.</param>
    /// <param name="usage">The declared usages; <see cref="GpuImageUsages.Validate"/> refuses a request that breaks a
    /// rule before anything is created.</param>
    /// <param name="clearDepth">The depth a depth attachment is cleared to (its attachment's
    /// <see cref="GpuDepthAttachment.ClearDepth"/>, which the factory validated), which it is created with as its optimized
    /// clear value; unused for any other usage.</param>
    /// <param name="family">The family its memory is attributed to (<see cref="GpuObjectName.MemoryFamily"/>).</param>
    public DirectXGpuImage(DirectXGpuImageRequest request, GpuPixelFormat format, GpuImageUsage usage, float clearDepth, string family) {
        var (deviceContext, dxgiFormat, width, height) = request;

        ArgumentNullException.ThrowIfNull(deviceContext);
        GpuImageUsages.Validate(
            format: format,
            height: height,
            usage: usage,
            width: width
        );
        // The device accepts a larger texture's creation and refuses only the recording that copies into it.
        GpuImageUsages.RequireWithinLimit(
            device: "Direct3D 12",
            height: height,
            limit: PInvoke.D3D12_REQ_TEXTURE2D_U_OR_V_DIMENSION,
            width: width
        );

        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var (resourceFormat, viewFormat) = DirectXTextures.FormatsOf(format: dxgiFormat, usage: usage);

        // A shader samples the view's format: a sampled depth image's typeless resource is read as R32_FLOAT.
        if ((usage & GpuImageUsage.Sampled) != 0) {
            RequireSampled(
                device: device,
                dxgiFormat: viewFormat,
                format: format
            );
        }

        Format = format;
        Height = height;
        Usage = usage;
        Width = width;

        var (flags, clearValue) = DirectXTextures.OfUsage(
            clearDepth: clearDepth,
            format: dxgiFormat,
            usage: usage
        );

        m_resource = ((nint)DirectXTextures.Create(
            arena: (deviceContext as DirectXDeviceContext)?.HeapArena,
            clearValue: clearValue,
            device: device,
            family: family,
            flags: flags,
            format: resourceFormat,
            height: height,
            initialState: D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON,
            memory: deviceContext.Memory,
            width: width
        ));
        m_memory = deviceContext.Memory;
        m_imageViewHandle = DirectXImageViews.Register(view: new DirectXImageView {
            Format = viewFormat,
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
    public nint ImageViewHandle => m_imageViewHandle;
    /// <inheritdoc/>
    public GpuImageUsage Usage { get; }
    /// <inheritdoc/>
    public uint Width { get; }


    // Refuses a format the device cannot sample from a two-dimensional texture, naming the format and what the device
    // reports for it.
    private static void RequireSampled(ID3D12Device* device, GpuPixelFormat format, DXGI_FORMAT dxgiFormat) {
        const D3D12_FORMAT_SUPPORT1 Required = D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_TEXTURE2D | D3D12_FORMAT_SUPPORT1.D3D12_FORMAT_SUPPORT1_SHADER_SAMPLE;

        var support = new D3D12_FEATURE_DATA_FORMAT_SUPPORT {
            Format = dxgiFormat,
        };

        if (
            !DirectXFeatureReads.TryCheck(
                data: ref support,
                feature: D3D12_FEATURE.D3D12_FEATURE_FORMAT_SUPPORT,
                support: new DirectXDeviceFeatureSupport(device: device)
            ) ||
            ((support.Support1 & Required) != Required)
        ) {
            throw new NotSupportedException(message: $"The Direct3D 12 device cannot sample {format} textures: it reports {support.Support1} for {dxgiFormat}.");
        }
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        DirectXImageViews.Release(handle: m_imageViewHandle);

        if (0 != m_resource) {
            DirectXDeviceMemory.CountReleased(
                memory: m_memory,
                resource: m_resource
            );
            _ = ((IUnknown*)m_resource)->Release();
            m_resource = 0;
        }
    }
}
