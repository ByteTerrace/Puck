using Puck.Compositing;
using Puck.Hosting;
using Puck.SdfVm.Rendering;
using Puck.Shaders;
using Puck.Vulkan;
using Puck.Vulkan.Interfaces;

namespace Puck.SdfVm.Nodes;

public sealed class SdfEngineNode : IRenderNode {
    private static readonly IReadOnlyDictionary<int, IRenderNode> EmptyChildren = new Dictionary<int, IRenderNode>();
    private readonly Surface?[] m_childSurfaces = new Surface?[SdfViewRenderer.ViewCount];
    private readonly IReadOnlyDictionary<int, IRenderNode> m_children;
    private readonly NodeDescriptor m_descriptor;
    private readonly ISdfFrameSource m_frameSource;
    private readonly IHostContext m_hostContext;
    private readonly SdfViewRenderer m_renderer;
    private bool m_disposed;

    internal SdfEngineNode(
        ISdfFrameSource frameSource,
        SdfViewRenderer renderer,
        IReadOnlyDictionary<int, IRenderNode>? children,
        IHostContext? hostContext,
        string? name
    ) {
        m_children = (children ?? EmptyChildren);
        m_descriptor = new NodeDescriptor(
            Name: (name ?? "sdf-engine"),
            SurfaceId: SurfaceId.New()
        );
        m_frameSource = frameSource;
        m_hostContext = (hostContext ?? HostContext.Empty);
        m_renderer = renderer;
    }

    public NodeDescriptor Descriptor => m_descriptor;

    public static SdfEngineNode Create(
        SdfViewRendererOptions options,
        ISdfFrameSource frameSource,
        IShaderModuleLoader shaderModuleLoader,
        IVulkanShaderModuleFactory shaderModuleFactory,
        IVulkanGraphicsPipelineFactory graphicsPipelineFactory,
        IVulkanVertexBufferFactory vertexBufferFactory,
        IVulkanStorageBufferFactory storageBufferFactory,
        IVulkanOffscreenImageApi offscreenImageApi,
        IVulkanRenderPassApi renderPassApi,
        IVulkanFramebufferSetApi framebufferSetApi,
        IVulkanCommandResourcesFactory commandResourcesFactory,
        IVulkanCommandBufferRecordingApi commandBufferRecordingApi,
        IVulkanFrameReadbackApi frameReadbackApi,
        IVulkanDescriptorApi descriptorApi,
        IReadOnlyDictionary<int, IRenderNode>? children = null,
        IHostContext? hostContext = null,
        string? name = null,
        bool produceCpuPixels = false
    ) {
        ArgumentNullException.ThrowIfNull(frameSource);

        var descriptorAllocator = new VulkanDescriptorAllocator(descriptorApi: descriptorApi);
        var externalMemoryApi = new VulkanNativeExternalMemoryApi();
        var queueSubmitter = new VulkanQueueSubmitter();
        var renderer = new SdfViewRenderer(
            commandBufferRecordingApi: commandBufferRecordingApi,
            commandResourcesFactory: commandResourcesFactory,
            descriptorAllocator: descriptorAllocator,
            externalMemoryApi: externalMemoryApi,
            framebufferSetApi: framebufferSetApi,
            frameReadbackApi: frameReadbackApi,
            graphicsPipelineFactory: graphicsPipelineFactory,
            offscreenImageApi: offscreenImageApi,
            options: options,
            produceCpuPixels: produceCpuPixels,
            queueSubmitter: queueSubmitter,
            renderPassApi: renderPassApi,
            shaderModuleFactory: shaderModuleFactory,
            shaderModuleLoader: shaderModuleLoader,
            storageBufferFactory: storageBufferFactory,
            vertexBufferFactory: vertexBufferFactory
        );

        return new SdfEngineNode(
            children: children,
            frameSource: frameSource,
            hostContext: hostContext,
            name: name,
            renderer: renderer
        );
    }
    public Surface ProduceFrame(in FrameContext context) {
        if (
            m_disposed ||
            (0 == context.TargetWidth) ||
            (0 == context.TargetHeight)
        ) {
            return default;
        }

        if (!context.Host.TryResolveCapability<IVulkanDeviceContext>(capability: out var device)) {
            return default;
        }

        var frame = m_frameSource.CaptureFrame(
            deltaSeconds: (float)(double)context.DeltaSeconds,
            height: context.TargetHeight,
            width: context.TargetWidth
        );

        Array.Clear(array: m_childSurfaces);

        foreach (var (slot, child) in m_children) {
            var childContext = (context with {
                Host = new ChainedHostContext(
                primary: m_hostContext,
                fallback: context.Host
            )
            });
            var childSurface = child.ProduceFrame(context: childContext);

            if (!childSurface.IsEmpty) {
                m_childSurfaces[slot] = childSurface;
            }
        }

        return m_renderer.Render(
            childSurfaces: m_childSurfaces,
            deviceContext: device,
            frame: frame,
            height: context.TargetHeight,
            width: context.TargetWidth
        );
    }
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;

        foreach (var child in m_children.Values) {
            child.Dispose();
        }

        m_renderer.Dispose();
    }
}
