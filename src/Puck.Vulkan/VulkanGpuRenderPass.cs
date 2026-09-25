using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// A Vulkan <see cref="IGpuRenderPass"/>: a <c>VkRenderPass</c> whose attachments are the description's colors, then its
/// depth. An attachment that loads begins in its attachment layout; one that clears or discards begins undefined. A
/// color attachment ends in its declared final layout and the depth attachment in its attachment layout.
/// </summary>
public sealed class VulkanGpuRenderPass : IGpuRenderPass {
    private const uint AttachmentLoadOpClear = 1;
    private const uint AttachmentLoadOpDontCare = 2;
    private const uint AttachmentLoadOpLoad = 0;
    private const uint AttachmentStoreOpDontCare = 1;
    private const uint AttachmentStoreOpStore = 0;
    private const uint SampleCount1Bit = 1;
    private const uint SubpassExternal = uint.MaxValue;

    private VulkanGpuRenderPass(GpuRenderPassDescription description, VulkanRenderPass renderPass) {
        Description = description;
        RenderPass = renderPass;
        var clearValues = description.Colors.Select(selector: static _ => VkClearValue.OfColor(
            alpha: 1f,
            blue: 0f,
            green: 0f,
            red: 0f
        )).ToList();

        if (description.Depth is { } depth) {
            clearValues.Add(item: VkClearValue.OfDepth(depth: depth.ClearDepth));
        }

        ClearValues = clearValues;
    }

    /// <summary>Gets one clear value per attachment, in attachment order: opaque black for a color, the declared
    /// <see cref="GpuDepthAttachment.ClearDepth"/> for the depth.</summary>
    public IReadOnlyList<VkClearValue> ClearValues { get; }
    /// <inheritdoc/>
    public GpuRenderPassDescription Description { get; }
    /// <summary>Gets the owned native render pass.</summary>
    public VulkanRenderPass RenderPass { get; }

    private static uint LoadOp(GpuAttachmentLoad load) => load switch {
        GpuAttachmentLoad.Clear => AttachmentLoadOpClear,
        GpuAttachmentLoad.Load => AttachmentLoadOpLoad,
        _ => AttachmentLoadOpDontCare,
    };
    private static uint StoreOp(GpuAttachmentStore store) => ((store == GpuAttachmentStore.Store)
        ? AttachmentStoreOpStore
        : AttachmentStoreOpDontCare
    );
    private static VkAttachmentDescription Attachment(GpuPixelFormat format, GpuAttachmentLoad load, GpuAttachmentStore store, uint attachmentLayout, uint finalLayout) => new() {
        FinalLayout = finalLayout,
        Format = VulkanGpuFormats.ToVkFormat(gpuPixelFormat: format),
        InitialLayout = ((load == GpuAttachmentLoad.Load)
            ? attachmentLayout
            : VulkanImageLayout.Undefined),
        LoadOp = LoadOp(load: load),
        Samples = SampleCount1Bit,
        StencilLoadOp = AttachmentLoadOpDontCare,
        StencilStoreOp = AttachmentStoreOpDontCare,
        StoreOp = StoreOp(store: store),
    };

    /// <summary>Builds the native request for a render pass description. The entry dependency orders the pass's
    /// attachment accesses after earlier attachment work, which a loaded attachment's contents need; a color attachment
    /// that ends shader-readable adds an exit dependency that makes its writes visible to later fragment-shader
    /// sampling.</summary>
    /// <param name="device">The device's command table.</param>
    /// <param name="description">The attachments.</param>
    /// <returns>The request.</returns>
    public static VulkanRenderPassCreateRequest RequestOf(VulkanDeviceCommands device, GpuRenderPassDescription description) {
        ArgumentNullException.ThrowIfNull(description);
        description.Validate();

        const uint AttachmentStages = VulkanPipelineStageFlags.ColorAttachmentOutput | VulkanPipelineStageFlags.EarlyFragmentTests | VulkanPipelineStageFlags.LateFragmentTests;
        const uint AttachmentAccesses = VulkanAccessFlags.ColorAttachmentRead | VulkanAccessFlags.ColorAttachmentWrite | VulkanAccessFlags.DepthStencilAttachmentRead | VulkanAccessFlags.DepthStencilAttachmentWrite;
        var dependencies = new List<VkSubpassDependency> {
            new() {
                DstAccessMask = AttachmentAccesses,
                DstStageMask = AttachmentStages,
                DstSubpass = 0,
                SrcAccessMask = VulkanAccessFlags.ColorAttachmentWrite | VulkanAccessFlags.DepthStencilAttachmentWrite,
                SrcStageMask = AttachmentStages,
                SrcSubpass = SubpassExternal,
            },
        };

        if (description.Colors.Any(predicate: static color => (color.FinalLayout == GpuImageLayout.ShaderReadOnly))) {
            dependencies.Add(item: new() {
                DstAccessMask = VulkanAccessFlags.ShaderRead,
                DstStageMask = VulkanPipelineStageFlags.FragmentShader,
                DstSubpass = SubpassExternal,
                SrcAccessMask = VulkanAccessFlags.ColorAttachmentWrite,
                SrcStageMask = VulkanPipelineStageFlags.ColorAttachmentOutput,
                SrcSubpass = 0,
            });
        }

        return new VulkanRenderPassCreateRequest(
            ColorAttachments: description.Colors.Select(selector: static color => Attachment(
                attachmentLayout: VulkanImageLayout.ColorAttachmentOptimal,
                finalLayout: ((color.FinalLayout == GpuImageLayout.ShaderReadOnly)
                    ? VulkanImageLayout.ShaderReadOnlyOptimal
                    : VulkanImageLayout.ColorAttachmentOptimal),
                format: color.Format,
                load: color.Load,
                store: color.Store
            )).ToArray(),
            DepthAttachment: ((description.Depth is { } depth)
                ? Attachment(
                    attachmentLayout: VulkanImageLayout.DepthStencilAttachmentOptimal,
                    finalLayout: VulkanImageLayout.DepthStencilAttachmentOptimal,
                    format: depth.Format,
                    load: depth.Load,
                    store: depth.Store
                )
                : null),
            Dependencies: dependencies,
            Device: device
        );
    }
    /// <summary>Creates a render pass.</summary>
    /// <param name="renderPassApi">The API that creates and destroys the native render pass.</param>
    /// <param name="device">The device's command table.</param>
    /// <param name="description">The attachments.</param>
    /// <returns>The render pass, owned by the caller.</returns>
    public static VulkanGpuRenderPass Create(IVulkanRenderPassApi renderPassApi, VulkanDeviceCommands device, GpuRenderPassDescription description) {
        ArgumentNullException.ThrowIfNull(renderPassApi);

        renderPassApi.CreateRenderPass(
            renderPassHandle: out var handle,
            request: RequestOf(
                description: description,
                device: device
            )
        ).ThrowIfFailed(operation: "vkCreateRenderPass");

        return new VulkanGpuRenderPass(
            description: description,
            renderPass: new VulkanRenderPass(
                device: device,
                renderPassApi: renderPassApi,
                renderPassHandle: handle
            )
        );
    }
    /// <inheritdoc/>
    public void Dispose() => RenderPass.Dispose();
}
/// <summary>
/// A Vulkan <see cref="IGpuFramebuffer"/>: a <c>VkFramebuffer</c> binding a <see cref="VulkanGpuRenderPass"/> to its
/// images' views. It owns the framebuffer only.
/// </summary>
public sealed class VulkanGpuFramebuffer : IGpuFramebuffer {
    private readonly IVulkanFramebufferSetApi m_framebufferSetApi;

    private bool m_disposed;

    private VulkanGpuFramebuffer(IVulkanFramebufferSetApi framebufferSetApi, VulkanGpuRenderPass renderPass, nint handle, uint width, uint height) {
        m_framebufferSetApi = framebufferSetApi;
        Handle = handle;
        Height = height;
        Pass = renderPass;
        Width = width;
    }

    /// <summary>Gets the native <c>VkFramebuffer</c> handle.</summary>
    public nint Handle { get; }
    /// <inheritdoc/>
    public uint Height { get; }
    /// <summary>Gets the render pass the framebuffer was made for.</summary>
    public VulkanGpuRenderPass Pass { get; }
    /// <inheritdoc/>
    public IGpuRenderPass RenderPass => Pass;
    /// <inheritdoc/>
    public uint Width { get; }

    /// <summary>Creates a framebuffer over images that match the render pass (<see cref="GpuFramebuffers.Validate"/>).</summary>
    /// <param name="framebufferSetApi">The API that creates and destroys the framebuffer.</param>
    /// <param name="renderPass">The render pass.</param>
    /// <param name="colors">One image per color attachment, in order.</param>
    /// <param name="depth">The depth image, or <see langword="null"/>.</param>
    /// <returns>The framebuffer, owned by the caller.</returns>
    public static VulkanGpuFramebuffer Create(IVulkanFramebufferSetApi framebufferSetApi, VulkanGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) {
        ArgumentNullException.ThrowIfNull(framebufferSetApi);
        ArgumentNullException.ThrowIfNull(renderPass);

        var (width, height) = GpuFramebuffers.Validate(
            colors: colors,
            depth: depth,
            description: renderPass.Description
        );
        var views = colors.Select(selector: static image => image.ImageViewHandle).ToList();

        if (depth is not null) {
            views.Add(item: depth.ImageViewHandle);
        }

        framebufferSetApi.CreateFramebuffer(
            framebufferHandle: out var handle,
            request: new VulkanFramebufferCreateRequest(
                Device: renderPass.RenderPass.Device,
                Height: height,
                ImageViewHandles: views,
                RenderPassHandle: renderPass.RenderPass.Handle,
                Width: width
            )
        ).ThrowIfFailed(operation: "vkCreateFramebuffer");

        return new VulkanGpuFramebuffer(
            framebufferSetApi: framebufferSetApi,
            handle: handle,
            height: height,
            renderPass: renderPass,
            width: width
        );
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_framebufferSetApi.DestroyFramebuffer(
            device: Pass.RenderPass.Device,
            framebufferHandle: Handle
        );
    }
}
/// <summary>
/// Implements <see cref="IGpuRenderPassFactory"/> for Vulkan through <see cref="VulkanGpuRenderPass"/> and
/// <see cref="VulkanGpuFramebuffer"/>.
/// </summary>
public sealed class VulkanGpuRenderPassFactory(IVulkanDeviceContext deviceContext, IVulkanRenderPassApi renderPassApi, IVulkanFramebufferSetApi framebufferSetApi, GpuObjectNaming naming) : IGpuRenderPassFactory {
    /// <inheritdoc/>
    public IGpuRenderPass Create(GpuRenderPassDescription description, in GpuObjectName name) {
        var renderPass = VulkanGpuRenderPass.Create(
            description: description,
            device: deviceContext.LogicalDevice.Commands,
            renderPassApi: renderPassApi
        );

        naming.Name(
            handle: renderPass.RenderPass.Handle,
            kind: GpuObjectKind.RenderPass,
            name: in name
        );

        return renderPass;
    }
    /// <inheritdoc/>
    public IGpuFramebuffer CreateFramebuffer(IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) =>
        VulkanGpuFramebuffer.Create(
            colors: colors,
            depth: depth,
            framebufferSetApi: framebufferSetApi,
            renderPass: ((VulkanGpuRenderPass)renderPass)
        );
}
