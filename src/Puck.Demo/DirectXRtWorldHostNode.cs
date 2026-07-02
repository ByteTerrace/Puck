using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX;
using Puck.DirectX.Interfaces;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// Hosts the neutral <see cref="RtWorldProducerNode"/> on a <em>Direct3D 12-hosted</em> window: it publishes the
/// host's Direct3D 12 device and a private provider of the neutral D3D12 compute services (including the DXR 1.1
/// acceleration-structure factory) to the inner ray-query node, then returns its storage-image surface for the
/// Direct3D 12 presenter to blit. The DXR peer of <see cref="DirectXComputeWorldHostNode"/>: the ray-query render
/// logic itself is backend-neutral, so this wrapper adds only the device/service hosting, exactly as the compute
/// world's host node does. Requires DXR 1.1 (Windows 10 1809+).
/// </summary>
[SupportedOSPlatform("windows10.0.17763")]
internal sealed class DirectXRtWorldHostNode : IRenderNode {
    private readonly NodeDescriptor m_descriptor = new(
        Name: "directx-rt-world-host",
        SurfaceId: SurfaceId.New()
    );
    private readonly IHostContext m_host;
    private readonly RtWorldProducerNode m_inner;
    private readonly ServiceProvider m_services;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="DirectXRtWorldHostNode"/> class.</summary>
    /// <param name="hostProvider">The application provider, from which the Direct3D 12 host device is resolved.</param>
    /// <param name="frameSource">The per-frame source of the scene and camera.</param>
    /// <param name="bytecode">The compiled ray-query kernel (DXIL).</param>
    /// <param name="width">The render width in pixels.</param>
    /// <param name="height">The render height in pixels.</param>
    /// <param name="capturePath">An optional PNG path; the first rendered frame is read back from the D3D12 device and written there.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public DirectXRtWorldHostNode(IServiceProvider hostProvider, ISdfFrameSource frameSource, ReadOnlyMemory<byte> bytecode, uint width, uint height, string? capturePath = null) {
        ArgumentNullException.ThrowIfNull(hostProvider);
        ArgumentNullException.ThrowIfNull(frameSource);

        // Resolve the Direct3D 12 host device explicitly (the frame host publishes IGpuDeviceContext ambiguously) and
        // republish it as the inner node's device — the same device the Direct3D 12 presenter blits from.
        var directXDevice = (IGpuDeviceContext)hostProvider.GetRequiredService<IDirectXDeviceContext>();

        m_host = new HostContext(capabilities: new Dictionary<Type, object> {
            [typeof(IGpuDeviceContext)] = directXDevice,
        });

        // The neutral Direct3D 12 compute services + the DXR acceleration-structure factory the ray-query node drives.
        var services = new ServiceCollection().AddDirectXComputeWorld();

        services.AddSingleton<IGpuAccelerationStructureFactory>(implementationInstance: new DirectXGpuAccelerationStructureFactory());

        m_services = services.BuildServiceProvider();
        m_inner = new RtWorldProducerNode(
            bytecode: bytecode,
            capturePath: capturePath,
            frameSource: frameSource,
            height: height,
            serviceProvider: m_services,
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

        return m_inner.ProduceFrame(context: context with { Host = m_host });
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
