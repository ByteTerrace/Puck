using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;
using static Puck.DirectX.DirectXConstants;

namespace Puck.DirectX;

/// <summary>
/// A Direct3D 12 <see cref="IGpuRenderPass"/>. Direct3D 12 has no render-pass object, so this is the description with its
/// formats translated: what a pipeline state object is created for (<see cref="ColorFormats"/>,
/// <see cref="DepthFormat"/>) and what <see cref="DirectXGpuCommandRecorder.BeginRenderPass"/> begins and ends.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuRenderPass : IGpuRenderPass {
    /// <summary>Initializes a render pass over a validated description.</summary>
    /// <param name="description">The attachments.</param>
    public DirectXGpuRenderPass(GpuRenderPassDescription description) {
        ArgumentNullException.ThrowIfNull(description);
        description.Validate();

        Description = description;
        ColorFormats = description.Colors.Select(selector: static color => DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: color.Format)).ToArray();
        DepthFormat = ((description.Depth is { } depth)
            ? DirectXGpuFormats.ToDxgiFormat(gpuPixelFormat: depth.Format)
            : DXGI_FORMAT.DXGI_FORMAT_UNKNOWN
        );
    }

    /// <summary>Gets each color attachment's DXGI format, in order.</summary>
    public IReadOnlyList<DXGI_FORMAT> ColorFormats { get; }
    /// <summary>Gets the depth attachment's DXGI format, or <c>DXGI_FORMAT_UNKNOWN</c> for none.</summary>
    public DXGI_FORMAT DepthFormat { get; }
    /// <inheritdoc/>
    public GpuRenderPassDescription Description { get; }

    /// <summary>Releases nothing: the render pass owns no native object.</summary>
    public void Dispose() { }
}
/// <summary>
/// A Direct3D 12 <see cref="IGpuFramebuffer"/>: a CPU-only render-target-view heap with one view per color image, and a
/// depth-stencil-view heap with the depth image's view, beside the images' resources. It owns the heaps only.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuFramebuffer : IGpuFramebuffer {
    private nint m_dsvHeap;
    private bool m_disposed;
    private nint m_rtvHeap;

    /// <summary>Creates the views of images that match a render pass (<see cref="GpuFramebuffers.Validate"/>). Images
    /// that do not match are refused before the device is touched.</summary>
    /// <param name="deviceContext">The device context whose device creates the views.</param>
    /// <param name="renderPass">The render pass.</param>
    /// <param name="colors">One image per color attachment, in order.</param>
    /// <param name="depth">The depth image, or <see langword="null"/>.</param>
    public DirectXGpuFramebuffer(IDirectXDeviceContext deviceContext, DirectXGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) {
        ArgumentNullException.ThrowIfNull(renderPass);

        (Width, Height) = GpuFramebuffers.Validate(
            colors: colors,
            depth: depth,
            description: renderPass.Description
        );
        ArgumentNullException.ThrowIfNull(deviceContext);
        Pass = renderPass;
        ColorResources = colors.Select(selector: static image => image.ImageHandle).ToArray();
        DepthResource = (depth?.ImageHandle ?? 0);

        var device = ((ID3D12Device*)deviceContext.Device.Handle);
        var colorViews = new nuint[colors.Count];

        try {
            if (colors.Count != 0) {
                var rtvHeap = DirectXDescriptorHeaps.Create(
                    count: ((uint)colors.Count),
                    device: device,
                    shaderVisible: false,
                    type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV
                );

                m_rtvHeap = ((nint)rtvHeap);

                var start = GetCpuHeapStart(heap: rtvHeap);
                var increment = device->GetDescriptorHandleIncrementSize(DescriptorHeapType: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

                for (var index = 0; (index < colors.Count); index++) {
                    var view = new D3D12_CPU_DESCRIPTOR_HANDLE { ptr = (start.ptr + (((nuint)index) * increment)) };

                    device->CreateRenderTargetView(
                        DestDescriptor: view,
                        pDesc: null,
                        pResource: ((ID3D12Resource*)colors[index].ImageHandle)
                    );
                    colorViews[index] = view.ptr;
                }
            }

            if (depth is not null) {
                var dsvHeap = DirectXDescriptorHeaps.Create(
                    count: 1,
                    device: device,
                    shaderVisible: false,
                    type: D3D12_DESCRIPTOR_HEAP_TYPE.D3D12_DESCRIPTOR_HEAP_TYPE_DSV
                );

                m_dsvHeap = ((nint)dsvHeap);

                var view = GetCpuHeapStart(heap: dsvHeap);

                device->CreateDepthStencilView(
                    DestDescriptor: view,
                    pDesc: null,
                    pResource: ((ID3D12Resource*)depth.ImageHandle)
                );
                DepthView = view.ptr;
            }
        } catch {
            Dispose();

            throw;
        }

        ColorViews = colorViews;
    }

    /// <summary>Gets each color image's resource, in attachment order.</summary>
    public IReadOnlyList<nint> ColorResources { get; }
    /// <summary>Gets each color image's render-target view, in attachment order.</summary>
    public IReadOnlyList<nuint> ColorViews { get; } = [];
    /// <summary>Gets the depth image's resource, or zero.</summary>
    public nint DepthResource { get; }
    /// <summary>Gets the depth image's depth-stencil view, or zero.</summary>
    public nuint DepthView { get; }
    /// <inheritdoc/>
    public uint Height { get; }
    /// <summary>Gets the render pass the framebuffer was made for.</summary>
    public DirectXGpuRenderPass Pass { get; }
    /// <inheritdoc/>
    public IGpuRenderPass RenderPass => Pass;
    /// <inheritdoc/>
    public uint Width { get; }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        Release(pointer: ref m_rtvHeap);
        Release(pointer: ref m_dsvHeap);
    }
}
/// <summary>
/// Implements <see cref="IGpuRenderPassFactory"/> for Direct3D 12 through <see cref="DirectXGpuRenderPass"/> and
/// <see cref="DirectXGpuFramebuffer"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuRenderPassFactory : IGpuRenderPassFactory {
    /// <inheritdoc/>
    public IGpuRenderPass Create(IGpuDeviceContext deviceContext, GpuRenderPassDescription description) =>
        new DirectXGpuRenderPass(description: description);
    /// <inheritdoc/>
    public IGpuFramebuffer CreateFramebuffer(IGpuDeviceContext deviceContext, IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) =>
        new DirectXGpuFramebuffer(
            colors: colors,
            depth: depth,
            deviceContext: ((IDirectXDeviceContext)deviceContext),
            renderPass: ((DirectXGpuRenderPass)renderPass)
        );
}
