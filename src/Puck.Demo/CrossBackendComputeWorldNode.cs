using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.DirectX;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// A cross-backend present node: it runs the generic compute SDF world on a bespoke Direct3D 12 device (LUID-matched
/// to the Vulkan host adapter, DXIL kernels) that writes into an <em>exportable</em> shared storage image, then
/// hands the Vulkan host only the shared NT handle — which the host imports and blits zero-copy, no host-memory
/// round trip. It is the compute counterpart of the SDF showcase's Direct3D 12 producer: the inner node is the
/// identical neutral <see cref="WorldProducerNode"/>; only the device it runs on and its exportable output differ.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class CrossBackendComputeWorldNode : IRenderNode {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;

    private readonly NodeDescriptor m_descriptor = new(
        Name: "cross-backend-compute-world",
        SurfaceId: SurfaceId.New()
    );
    private readonly DirectXComputeWorldDevice m_device;
    private readonly WorldProducerNode m_inner;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="CrossBackendComputeWorldNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (resolves the live Vulkan device for the adapter LUID).</param>
    /// <param name="withChild">Whether the bottom-right slot is a hosted <see cref="ChildSurfaceNode"/> instead of an SDF camera (the injected frame source then supplies the four-viewport split).</param>
    /// <param name="frameSource">The data-driven scene/camera source to render (a <c>JsonSdfFrameSource</c> over the document's scene + viewports).</param>
    /// <param name="capturePath">An optional PNG path; the inner producer reads its first rendered frame back from the bespoke Direct3D 12 device and writes it there.</param>
    /// <param name="width">The render width in pixels (defaults to 960).</param>
    /// <param name="height">The render height in pixels (defaults to 600).</param>
    /// <exception cref="ArgumentNullException"><paramref name="serviceProvider"/> or <paramref name="frameSource"/> is <see langword="null"/>.</exception>
    public CrossBackendComputeWorldNode(IServiceProvider serviceProvider, ISdfFrameSource frameSource, bool withChild = false, string? capturePath = null, uint width = 960, uint height = 600) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(frameSource);

        m_device = new DirectXComputeWorldDevice(hostProvider: serviceProvider);
        m_inner = new WorldProducerNode(
            beamBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-beam.comp.dxil")),
            cullArgsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-cull-args.comp.dxil")),
            capturePath: capturePath,
            children: withChild ? ChildSurfaceNode.CreateWorldChildren(serviceProvider: m_device.Services, directX: true) : null,
            compositeBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-composite.comp.dxil")),
            createStorageImage: deviceContext => new DirectXGpuSurfaceExportFactory().CreateExportableStorageImage(
                deviceContext: deviceContext,
                format: Format,
                height: height,
                width: width
            ),
            frameSource: frameSource,
            height: height,
            serviceProvider: m_device.Services,
            viewsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-views.comp.dxil")),
            width: width
        );
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        // Redirect the inner node to the bespoke Direct3D 12 device (so it resolves that device, not the Vulkan
        // host) while carrying the host's deterministic timing and target extent through unchanged.
        return m_inner.ProduceFrame(context: context with { Host = m_device.Host });
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_inner.Dispose();
        m_device.Dispose();
    }
}
