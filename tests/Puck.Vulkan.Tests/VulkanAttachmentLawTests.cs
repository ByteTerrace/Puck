using System.Runtime.CompilerServices;
using Puck.Abstractions.Gpu;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, how Vulkan begins and ends each attachment a render pass declares, and that an
/// image or pipeline incompatible with its render pass is refused before anything is created.</summary>
public sealed class VulkanAttachmentLawTests {
    private static readonly GpuRenderPassDescription ColorAndDepth = new(
        Colors: [new GpuColorAttachment(
            FinalLayout: GpuImageLayout.RenderTarget,
            Format: GpuPixelFormat.R8G8B8A8Unorm,
            Load: GpuAttachmentLoad.Load,
            Store: GpuAttachmentStore.Store
        )],
        Depth: new GpuDepthAttachment(
            Format: GpuPixelFormat.D32Float,
            Load: GpuAttachmentLoad.Clear,
            Store: GpuAttachmentStore.Discard
        )
    );
    // A command table no test calls through, made without resolving any entry point.
    private static readonly VulkanDeviceCommands UncalledDevice = ((VulkanDeviceCommands)RuntimeHelpers.GetUninitializedObject(type: typeof(VulkanDeviceCommands)));

    private static VulkanGpuRenderPass RenderPass(GpuRenderPassDescription description) =>
        VulkanGpuRenderPass.Create(
            description: description,
            device: UncalledDevice,
            renderPassApi: new RecordingRenderPassApi()
        );
    private static StubImage Image(GpuPixelFormat format, uint extent, GpuImageUsage usage) => new(
        Format: format,
        Height: extent,
        Usage: usage,
        Width: extent
    );

    [Fact]
    public void ALoadedAttachmentBeginsInItsAttachmentLayoutAndAClearedOneUndefined() {
        var request = VulkanGpuRenderPass.RequestOf(
            description: ColorAndDepth,
            device: null!
        );
        var color = request.ColorAttachments.Single();
        var depth = request.DepthAttachment!.Value;

        Assert.Equal(
            actual: (color.Format, color.LoadOp, color.StoreOp, color.InitialLayout, color.FinalLayout),
            expected: (VulkanFormat.R8G8B8A8Unorm, 0U, 0U, VulkanImageLayout.ColorAttachmentOptimal, VulkanImageLayout.ColorAttachmentOptimal)
        );
        Assert.Equal(
            actual: (depth.Format, depth.LoadOp, depth.StoreOp, depth.InitialLayout, depth.FinalLayout),
            expected: (VulkanFormat.D32Sfloat, 1U, 1U, VulkanImageLayout.Undefined, VulkanImageLayout.DepthStencilAttachmentOptimal)
        );
    }
    [InlineData("depth-extent")]
    [InlineData("color-format")]
    [InlineData("depth-usage")]
    [InlineData("missing-depth")]
    [Theory]
    public void AFramebufferOverIncompatibleImagesIsRefusedBeforeItIsCreated(string incompatibility) {
        var views = new RecordingFramebufferSetApi();
        using var pass = RenderPass(description: ColorAndDepth);
        var color = Image(
            extent: 8,
            format: GpuPixelFormat.R8G8B8A8Unorm,
            usage: GpuImageUsage.ColorAttachment | GpuImageUsage.Sampled
        );
        var depth = Image(
            extent: 8,
            format: GpuPixelFormat.D32Float,
            usage: GpuImageUsage.DepthAttachment
        );

        var (colors, bound) = incompatibility switch {
            "depth-extent" => (((IGpuImage[])[color]), ((IGpuImage?)(depth with { Height = 16, Width = 16 }))),
            "color-format" => ([color with { Format = GpuPixelFormat.B8G8R8A8Unorm }], depth),
            "depth-usage" => ([color], depth with { Usage = GpuImageUsage.Sampled }),
            "missing-depth" => ([color], null),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(incompatibility)),
        };

        _ = Assert.Throws<ArgumentException>(testCode: () => VulkanGpuFramebuffer.Create(
            colors: colors,
            depth: bound,
            framebufferSetApi: views,
            renderPass: pass
        ));
        Assert.Equal(
            actual: views.Created,
            expected: 0
        );
    }
    [Fact]
    public void APipelineWhoseDepthTestDisagreesWithItsRenderPassIsRefused() {
        using var withDepth = RenderPass(description: ColorAndDepth);
        using var withoutDepth = RenderPass(description: ColorAndDepth with { Depth = null });
        var factory = new VulkanGpuPipelineFactory(allocator: null!, computePipelineApi: null!, deviceContext: null!, naming: GpuObjectNaming.Off, pipelineFactory: null!);
        var untested = new GpuGraphicsPipelineDescription(
            EnableStorageBuffer: false,
            Name: "geometry",
            PushConstantBinding: null,
            TextureSamplerCount: 0,
            VertexInput: new GpuVertexInputLayout(
                Attributes: [],
                StrideBytes: 0
            )
        );

        foreach (var (pass, description) in (((IGpuRenderPass, GpuGraphicsPipelineDescription)[])[(withDepth, untested), (withoutDepth, untested with { DepthCompare = GpuDepthCompare.Less })])) {
            _ = Assert.Throws<ArgumentException>(testCode: () => factory.Create(
                description: description,
                fragmentShaderModule: null!,
                name: default,
                renderPass: pass,
                vertexShaderModule: null!
            ));
        }
    }
    [InlineData(GpuDepthCompare.Less, 1U)]
    [InlineData(GpuDepthCompare.Equal, 2U)]
    [InlineData(GpuDepthCompare.LessOrEqual, 3U)]
    [InlineData(GpuDepthCompare.Greater, 4U)]
    [InlineData(GpuDepthCompare.GreaterOrEqual, 6U)]
    [InlineData(GpuDepthCompare.Always, 7U)]
    [Theory]
    public void EachDepthComparisonIsItsVkCompareOp(GpuDepthCompare compare, uint op) =>
        Assert.Equal(
            actual: VulkanGpuPipelineFactory.ToVkCompareOp(compare: compare),
            expected: op
        );

    private sealed record StubImage(GpuPixelFormat Format, uint Width, uint Height, GpuImageUsage Usage) : IGpuImage {
        public nint ImageHandle => 0x100;
        public nint ImageViewHandle => 0x200;

        public void Dispose() { }
    }
    private sealed class RecordingRenderPassApi : IVulkanRenderPassApi {
        public VkResult CreateRenderPass(VulkanRenderPassCreateRequest request, out nint renderPassHandle) {
            renderPassHandle = 0x300;

            return VkResult.Success;
        }
        public void DestroyRenderPass(VulkanDeviceCommands device, nint renderPassHandle) { }
    }
    private sealed class RecordingFramebufferSetApi : IVulkanFramebufferSetApi {
        public int Created { get; private set; }

        public VkResult CreateFramebuffer(VulkanFramebufferCreateRequest request, out nint framebufferHandle) {
            Created++;
            framebufferHandle = 0x400;

            return VkResult.Success;
        }
        public VkResult CreateImageView(VulkanImageViewCreateRequest request, out nint imageViewHandle) => throw new NotSupportedException();
        public void DestroyFramebuffer(VulkanDeviceCommands device, nint framebufferHandle) { }
        public void DestroyImageView(VulkanDeviceCommands device, nint imageViewHandle) { }
        public IReadOnlyList<nint> GetSwapchainImages(VulkanDeviceCommands device, nint swapchainHandle) => throw new NotSupportedException();
    }
}
