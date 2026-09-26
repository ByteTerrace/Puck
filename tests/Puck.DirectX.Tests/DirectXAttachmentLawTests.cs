using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Pins, without a device, that Direct3D 12 refuses an image or pipeline incompatible with its render pass
/// before it touches the device, exactly as the Vulkan backend does.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXAttachmentLawTests {
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

    private static StubImage Image(GpuPixelFormat format, uint extent, GpuImageUsage usage) => new(
        Format: format,
        Height: extent,
        Usage: usage,
        Width: extent
    );

    [InlineData("depth-extent")]
    [InlineData("color-format")]
    [InlineData("depth-usage")]
    [InlineData("missing-depth")]
    [Theory]
    public void AFramebufferOverIncompatibleImagesIsRefusedBeforeTheDeviceIsTouched(string incompatibility) {
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

        // No device context: a framebuffer that got past validation would fail on it with another exception.
        _ = Assert.Throws<ArgumentException>(testCode: () => new DirectXGpuRenderPassFactory(deviceContext: null!).CreateFramebuffer(
            colors: colors,
            depth: bound,
            renderPass: new DirectXGpuRenderPass(description: ColorAndDepth)
        ));
    }
    [Fact]
    public void APipelineWhoseDepthTestDisagreesWithItsRenderPassIsRefused() {
        var factory = new DirectXGpuPipelineFactory(deviceContext: null!);
        var untested = new GpuGraphicsPipelineDescription(
            Layout: new GpuPipelineLayoutDescription(
                groups: [],
                pushesIndex: false,
                stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment
            ),
            Name: "geometry",
            VertexInput: new GpuVertexInputLayout(
                Attributes: [],
                StrideBytes: 0
            )
        );

        foreach (var (pass, description) in (((IGpuRenderPass, GpuGraphicsPipelineDescription)[])[(new DirectXGpuRenderPass(description: ColorAndDepth), untested), (new DirectXGpuRenderPass(description: ColorAndDepth with { Depth = null }), untested with { DepthCompare = GpuDepthCompare.Less })])) {
            _ = Assert.Throws<ArgumentException>(testCode: () => factory.Create(
                description: description,
                fragmentShaderModule: null!,
                name: default,
                renderPass: pass,
                vertexShaderModule: null!
            ));
        }
    }

    private sealed record StubImage(GpuPixelFormat Format, uint Width, uint Height, GpuImageUsage Usage) : IGpuImage {
        public nint ImageHandle => 0x100;
        public nint ImageViewHandle => 0x200;

        public void Dispose() { }
    }
}
