using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Messages;

/// <summary>
/// Builds <see cref="VulkanRenderPassCreateRequest"/> values for the common single-color-attachment render
/// passes used by the engine, with the right load/store operations, layouts, and subpass dependencies
/// already wired up.
/// </summary>
public static class VulkanRenderPassRequests {
    private const uint AccessColorAttachmentRead = 0x00000080;
    private const uint AccessColorAttachmentWrite = 0x00000100;
    private const uint AccessShaderRead = 0x00000020;
    private const uint AttachmentLoadOpClear = 1;
    private const uint AttachmentLoadOpLoad = 0;
    private const uint AttachmentStoreOpDontCare = 1;
    private const uint AttachmentStoreOpStore = 0;
    private const uint ImageLayoutPresentSourceKhr = 1000001002;
    private const uint ImageLayoutShaderReadOnlyOptimal = 5;
    private const uint ImageLayoutUndefined = 0;
    private const uint PipelineStageColorAttachmentOutput = 0x00000400;
    private const uint PipelineStageFragmentShader = 0x00000080;
    private const uint SampleCount1Bit = 1;
    private const uint SubpassExternal = uint.MaxValue;

    /// <summary>Builds a request for a render pass whose color attachment is cleared on load and ends in the present-source layout, ready to be presented.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="colorFormat">The color attachment format, as a <c>VkFormat</c> value.</param>
    /// <returns>A render pass create request configured for presentation.</returns>
    public static VulkanRenderPassCreateRequest Present(nint deviceHandle, uint colorFormat) {
        return SingleColorAttachment(
            colorFormat: colorFormat,
            deviceHandle: deviceHandle,
            finalLayout: ImageLayoutPresentSourceKhr
        );
    }
    /// <summary>Builds a request for an offscreen render pass whose color attachment ends in the shader-read-only layout, ready to be sampled by a later pass.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="colorFormat">The color attachment format, as a <c>VkFormat</c> value.</param>
    /// <param name="preserveExistingContents">Whether the attachment's existing contents are loaded and preserved rather than cleared.</param>
    /// <returns>A render pass create request configured for offscreen rendering that is later sampled.</returns>
    public static VulkanRenderPassCreateRequest Sampled(nint deviceHandle, uint colorFormat, bool preserveExistingContents) {
        return SingleColorAttachment(
            colorFormat: colorFormat,
            deviceHandle: deviceHandle,
            finalLayout: ImageLayoutShaderReadOnlyOptimal,
            makeResultsVisibleToFragmentShaders: true,
            preserveExistingContents: preserveExistingContents
        );
    }
    /// <summary>Builds a request for a render pass with a single color attachment, configurable final layout, load behavior, and exit synchronization.</summary>
    /// <param name="deviceHandle">The native <c>VkDevice</c> handle.</param>
    /// <param name="colorFormat">The color attachment format, as a <c>VkFormat</c> value.</param>
    /// <param name="finalLayout">The layout the attachment is transitioned to when the pass ends, as a <c>VkImageLayout</c> value.</param>
    /// <param name="preserveExistingContents">Whether the attachment's existing contents are loaded and preserved (<see langword="true"/>) rather than cleared (<see langword="false"/>).</param>
    /// <param name="makeResultsVisibleToFragmentShaders">Whether to add an exit dependency making the color writes visible to later fragment-shader sampling.</param>
    /// <returns>A render pass create request for a single color attachment.</returns>
    public static VulkanRenderPassCreateRequest SingleColorAttachment(
        nint deviceHandle,
        uint colorFormat,
        uint finalLayout,
        bool preserveExistingContents = false,
        bool makeResultsVisibleToFragmentShaders = false
    ) {
        var dependencies = new List<VkSubpassDependency>(capacity: 2) {
            // Entry: synchronize the pass's color-attachment access against prior work.
            // A loading pass both reads (loadOp) and writes the attachment.
            new() {
                DstAccessMask = (preserveExistingContents
                    ? AccessColorAttachmentRead | AccessColorAttachmentWrite
                    : AccessColorAttachmentRead),
                DstStageMask = PipelineStageColorAttachmentOutput,
                DstSubpass = 0,
                SrcStageMask = PipelineStageColorAttachmentOutput,
                SrcSubpass = SubpassExternal,
            },
        };

        if (makeResultsVisibleToFragmentShaders) {
            // Exit: make the color writes visible to later fragment-shader sampling before
            // the pass's results are consumed.
            dependencies.Add(item: new() {
                DstAccessMask = AccessShaderRead,
                DstStageMask = PipelineStageFragmentShader,
                DstSubpass = SubpassExternal,
                SrcAccessMask = AccessColorAttachmentWrite,
                SrcStageMask = PipelineStageColorAttachmentOutput,
                SrcSubpass = 0,
            });
        }

        return new VulkanRenderPassCreateRequest(
            ColorAttachments: [
                new VkAttachmentDescription {
                    FinalLayout = finalLayout,
                    Format = colorFormat,
                    InitialLayout = (preserveExistingContents
                        ? finalLayout
                        : ImageLayoutUndefined),
                    LoadOp = (preserveExistingContents
                        ? AttachmentLoadOpLoad
                        : AttachmentLoadOpClear),
                    Samples = SampleCount1Bit,
                    StencilLoadOp = AttachmentStoreOpDontCare,
                    StencilStoreOp = AttachmentStoreOpDontCare,
                    StoreOp = AttachmentStoreOpStore,
                },
            ],
            Dependencies: dependencies,
            DeviceHandle: deviceHandle
        );
    }
}
