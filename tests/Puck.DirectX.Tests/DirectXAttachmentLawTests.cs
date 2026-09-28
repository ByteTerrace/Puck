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

    // A counting draw's layout: its pass group binds the work counters, a read-write buffer its fragments add to.
    private static GpuGraphicsPipelineDescription CountingDraw() => new(
        Layout: new GpuPipelineLayoutDescription(
            groups: [new GpuGroupLayoutDescription(
                bindings: [
                    new GpuGroupBinding(binding: 0, kind: GpuBindingKind.ConstantBuffer),
                    new GpuGroupBinding(binding: 1, kind: GpuBindingKind.ReadWriteBuffer),
                ],
                ordinal: 3
            )],
            pushesIndex: false,
            stages: GpuShaderStage.Vertex | GpuShaderStage.Fragment
        ),
        Name: "counting",
        VertexInput: new GpuVertexInputLayout(
            Attributes: [],
            StrideBytes: 0
        )
    );

    // A render pass allowing shader writes opens with ALLOW_UAV_WRITES, which Direct3D 12 requires of a UAV write inside
    // a render pass, as a counting fragment stage makes; one that does not opens with no flag.
    [Fact]
    public void ARenderPassAllowingShaderWritesOpensAllowingUavWrites() {
        var colorOnly = ColorAndDepth with { Depth = null };

        Assert.True(condition: CountingDraw().Layout.ShaderWrites);
        Assert.Equal(
            actual: new DirectXGpuRenderPass(description: colorOnly with { ShaderWrites = true }).Flags,
            expected: Windows.Win32.Graphics.Direct3D12.D3D12_RENDER_PASS_FLAGS.D3D12_RENDER_PASS_FLAG_ALLOW_UAV_WRITES
        );
        Assert.Equal(
            actual: new DirectXGpuRenderPass(description: colorOnly).Flags,
            expected: Windows.Win32.Graphics.Direct3D12.D3D12_RENDER_PASS_FLAGS.D3D12_RENDER_PASS_FLAG_NONE
        );
    }
    // A pipeline whose layout lets its shaders write is refused against a render pass that does not allow it, before the
    // device is touched, and validates against one that does.
    [Fact]
    public void AWritingPipelineIsRefusedAgainstARenderPassThatDoesNotAllowShaderWrites() {
        var colorOnly = ColorAndDepth with { Depth = null };
        var draw = CountingDraw();

        _ = Assert.Throws<ArgumentException>(testCode: () => new DirectXGpuPipelineFactory(deviceContext: null!).Create(
            description: draw,
            fragmentShaderModule: null!,
            name: default,
            renderPass: new DirectXGpuRenderPass(description: colorOnly),
            vertexShaderModule: null!
        ));
        draw.ValidateAgainst(renderPass: new DirectXGpuRenderPass(description: colorOnly with { ShaderWrites = true }));
    }

    private sealed record StubImage(GpuPixelFormat Format, uint Width, uint Height, GpuImageUsage Usage) : IGpuImage {
        public nint ImageHandle => 0x100;
        public nint ImageViewHandle => 0x200;

        public void Dispose() { }
    }
}
