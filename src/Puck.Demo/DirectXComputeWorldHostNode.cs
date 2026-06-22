using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.DirectX.Interfaces;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// Hosts the compute SDF world on a <em>Direct3D 12-hosted</em> window: it runs the neutral
/// <see cref="WorldProducerNode"/> on the Direct3D 12 host device (DXIL kernels, the D3D12 compute services) and
/// returns its storage-image surface for the Direct3D 12 presenter to blit — the same-device compute counterpart of
/// the graphics showcase hosting on Direct3D 12. Unlike <see cref="CrossBackendComputeWorldNode"/> it creates no
/// bespoke LUID-matched device and no exportable image: the host device IS Direct3D 12, so the world renders and
/// presents on one device with no cross-API import. The compute services live in a private provider; the host device
/// is resolved from the frame's host context (which a Direct3D 12 host publishes as <see cref="IGpuDeviceContext"/>).
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class DirectXComputeWorldHostNode : IRenderNode {
    private readonly NodeDescriptor m_descriptor = new(
        Name: "directx-compute-world-host",
        SurfaceId: SurfaceId.New()
    );
    private readonly IHostContext m_host;
    private readonly WorldProducerNode m_inner;
    private readonly ServiceProvider m_services;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="DirectXComputeWorldHostNode"/> class.</summary>
    /// <param name="hostProvider">The application provider, from which the Direct3D 12 host device is resolved.</param>
    /// <param name="withChild">Whether the bottom-right slot is a hosted <see cref="ChildSurfaceNode"/> instead of an SDF camera (the injected frame source then supplies the four-viewport split).</param>
    /// <param name="frameSource">The data-driven scene/camera source to render (a <c>JsonSdfFrameSource</c> over the document's scene + viewports).</param>
    /// <param name="capturePath">An optional PNG path; the first rendered frame is read back from the D3D12 device and written there.</param>
    /// <param name="width">The render width in pixels (defaults to 960).</param>
    /// <param name="height">The render height in pixels (defaults to 600).</param>
    /// <exception cref="ArgumentNullException"><paramref name="hostProvider"/> or <paramref name="frameSource"/> is <see langword="null"/>.</exception>
    public DirectXComputeWorldHostNode(IServiceProvider hostProvider, bool withChild, ISdfFrameSource frameSource, string? capturePath = null, uint width = 960, uint height = 600) {
        ArgumentNullException.ThrowIfNull(hostProvider);
        ArgumentNullException.ThrowIfNull(frameSource);

        // The frame's host context publishes IGpuDeviceContext ambiguously (both presenters contribute one), so
        // resolve the Direct3D 12 host device explicitly and republish it as the inner node's device — the same
        // device the Direct3D 12 presenter blits from, so the storage image renders and presents on one device.
        var directXDevice = (IGpuDeviceContext)hostProvider.GetRequiredService<IDirectXDeviceContext>();

        m_host = new HostContext(capabilities: new Dictionary<Type, object> {
            [typeof(IGpuDeviceContext)] = directXDevice,
        });

        // The neutral Direct3D 12 compute services the world producer drives — including the GPU-timing counters this
        // hosted path can surface (PUCK_TIMING), which the shared bundle already registers.
        var services = new ServiceCollection().AddDirectXComputeWorld();

        m_services = services.BuildServiceProvider();
        m_inner = new WorldProducerNode(
            beamBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-beam.comp.dxil")),
            cullArgsBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-cull-args.comp.dxil")),
            capturePath: capturePath,
            children: (withChild ? ChildSurfaceNode.CreateWorldChildren(serviceProvider: m_services, directX: true) : null),
            compositeBytecode: File.ReadAllBytes(path: Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-world-composite.comp.dxil")),
            frameSource: frameSource,
            height: height,
            serviceProvider: m_services,
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

        // Redirect the inner node to the Direct3D 12 host device (carrying the host's deterministic timing and
        // target extent through unchanged), so it resolves that device rather than the ambiguous frame host.
        return m_inner.ProduceFrame(context: context with { Host = m_host });
    }

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // The host D3D12 device is recreated in place by the presenter (its capability identity is preserved), so the
        // inner producer just releases + rebuilds its own resources against the new device next frame.
        m_inner.OnDeviceLost();
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_inner.Dispose();
        m_services.Dispose();
    }
}
