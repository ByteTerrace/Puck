using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanRenderPassApi"/>, marshaling to the
/// <c>vkCreateRenderPass</c> and <c>vkDestroyRenderPass</c> entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeRenderPassApi : IVulkanRenderPassApi {
    // Color attachments are referenced in COLOR_ATTACHMENT_OPTIMAL during the subpass — structural to being a
    // color attachment, not a policy choice. Per-attachment initial/final layouts come from the caller's
    // VkAttachmentDescription.
    private const uint ColorAttachmentOptimalLayout = 2;
    private const uint DepthStencilAttachmentOptimalLayout = 3;
    private const uint GraphicsPipelineBindPoint = 0;
    private const uint StructureTypeRenderPassCreateInfo = 38;

    private readonly IAllocator m_allocator;

    /// <summary>Initializes a new instance of the <see cref="VulkanNativeRenderPassApi"/> class.</summary>
    /// <param name="allocator">The unmanaged allocator used to marshal native Vulkan structures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    public VulkanNativeRenderPassApi(IAllocator allocator) {
        ArgumentNullException.ThrowIfNull(argument: allocator);

        m_allocator = allocator;
    }

    /// <inheritdoc/>
    public VkResult CreateRenderPass(VulkanRenderPassCreateRequest request, out nint renderPassHandle) {
        ArgumentNullException.ThrowIfNull(
            argument: request.Device,
            paramName: nameof(request)
        );

        var colorAttachments = (request.ColorAttachments ?? []);

        if (
            (0 == colorAttachments.Count) &&
            (request.DepthAttachment is null)
        ) {
            throw new ArgumentException(
                message: "A render pass requires at least one attachment.",
                paramName: nameof(request)
            );
        }

        var dependencies = (request.Dependencies ?? []);
        var createRenderPass = request.Device.CreateRenderPass;

        var colorCount = colorAttachments.Count;
        var attachments = new List<VkAttachmentDescription>(collection: colorAttachments);
        var references = new VkAttachmentReference[colorCount];
        // The depth attachment follows the colors, referenced in its attachment layout for the whole subpass.
        var depthReference = new VkAttachmentReference {
            Attachment = ((uint)colorCount),
            Layout = DepthStencilAttachmentOptimalLayout,
        };

        if (request.DepthAttachment is { } depth) {
            attachments.Add(item: depth);
        }

        for (var index = 0; (index < colorCount); index++) {
            references[index] = new VkAttachmentReference {
                Attachment = ((uint)index),
                Layout = ColorAttachmentOptimalLayout,
            };
        }

        nint attachmentsPointer = 0;
        nint referencesPointer = 0;
        nint dependencyPointer = 0;

        try {
            attachmentsPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: m_allocator,
                values: attachments
            );
            referencesPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: m_allocator,
                values: references
            );
            dependencyPointer = VulkanMarshalHelpers.AllocateArray(
                allocator: m_allocator,
                values: dependencies
            );

            // The one subpass is a stack local: vkCreateRenderPass reads it synchronously.
            var subpass = new VkSubpassDescription {
                ColorAttachmentCount = ((uint)colorCount),
                PColorAttachments = referencesPointer,
                PDepthStencilAttachment = ((request.DepthAttachment is null)
                    ? 0
                    : ((nint)(&depthReference))),
                PipelineBindPoint = GraphicsPipelineBindPoint,
            };
            var createInfo = new VkRenderPassCreateInfo {
                AttachmentCount = ((uint)attachments.Count),
                DependencyCount = ((uint)dependencies.Count),
                PAttachments = attachmentsPointer,
                PDependencies = dependencyPointer,
                PSubpasses = ((nint)(&subpass)),
                SType = StructureTypeRenderPassCreateInfo,
                SubpassCount = 1,
            };

            return createRenderPass(
                request.Device.Handle,
                in createInfo,
                0,
                out renderPassHandle
            );
        } finally {
            m_allocator.Free(ptr: attachmentsPointer);
            m_allocator.Free(ptr: referencesPointer);
            m_allocator.Free(ptr: dependencyPointer);
        }
    }
    /// <inheritdoc/>
    public void DestroyRenderPass(VulkanDeviceCommands device, nint renderPassHandle) =>
        device?.Destroy(
            destroy: device.DestroyRenderPass,
            handle: renderPassHandle
        );
}
