using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>Holds the byte layout an image upload reads to its definition: an image is its rows of texels, tightly
/// packed. An upload offering any other length or an empty extent is refused.</summary>
public sealed class GpuPixelFormatsLawTests {
    [Fact]
    public void AnImageIsItsRowsOfTexels() {
        Assert.Equal(expected: ((3UL * 2UL) * 4UL), actual: GpuPixelFormats.ImageByteLength(format: GpuPixelFormat.R8G8B8A8Unorm, height: 2U, width: 3U));
        Assert.Equal(expected: (3UL * 2UL), actual: GpuPixelFormats.ImageByteLength(format: GpuPixelFormat.R8Unorm, height: 2U, width: 3U));
        Assert.Equal(expected: ((3UL * 2UL) * 2UL), actual: GpuPixelFormats.ImageByteLength(format: GpuPixelFormat.R8G8Unorm, height: 2U, width: 3U));
        Assert.Equal(expected: ((3UL * 2UL) * 16UL), actual: GpuPixelFormats.ImageByteLength(format: GpuPixelFormat.R32G32B32A32Float, height: 2U, width: 3U));
    }
    [Fact]
    public void EveryFormatStatesItsTexelBytes() {
        foreach (var format in Enum.GetValues<GpuPixelFormat>()) {
            Assert.True(condition: (GpuPixelFormats.TexelBytes(format: format) > 0U), userMessage: $"{format}");
        }

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuPixelFormats.TexelBytes(format: default));
    }
    [Fact]
    public void AnUploadOfferingAnyOtherLengthIsRefused() {
        var image = GpuPixelFormats.ImageByteLength(format: GpuPixelFormat.R8G8Unorm, height: 68U, width: 68U);

        Assert.Equal(expected: image, actual: GpuPixelFormats.RequireImage(byteLength: ((long)image), format: GpuPixelFormat.R8G8Unorm, height: 68U, width: 68U));
        Assert.Equal(
            expected: $"The upload of a R8G8Unorm image at 68x68 requires exactly {image} tightly packed bytes; it offers {image - 2UL}. (Parameter 'byteLength')",
            actual: Assert.Throws<ArgumentException>(testCode: () => GpuPixelFormats.RequireImage(byteLength: (((long)image) - 2L), format: GpuPixelFormat.R8G8Unorm, height: 68U, width: 68U)).Message
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuPixelFormats.RequireImage(byteLength: 0L, format: GpuPixelFormat.R8G8Unorm, height: 0U, width: 68U));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuPixelFormats.RequireImage(byteLength: 0L, format: GpuPixelFormat.R8G8Unorm, height: 68U, width: 0U));
    }
    // A depth attachment image is created from its attachment alone, so the depth it clears to has one statement: Create
    // refuses the usage, and CreateDepth takes a depth format and a clear depth in [0, 1]. A depth image may also be
    // sampled (a shadow cascade), and declares no other usage.
    [Fact]
    public void ADepthAttachmentImageIsCreatedFromItsAttachment() {
        static GpuDepthAttachment Depth(GpuPixelFormat format, float clear) => new(
            ClearDepth: clear,
            Format: format,
            Load: GpuAttachmentLoad.Clear,
            Store: GpuAttachmentStore.Discard
        );

        Assert.Equal(expected: GpuImageUsage.DepthAttachment, actual: GpuImageUsages.ValidateDepth(attachment: Depth(clear: 0f, format: GpuPixelFormat.D32Float), height: 4U, sampled: false, width: 4U));
        Assert.Equal(expected: (GpuImageUsage.DepthAttachment | GpuImageUsage.Sampled), actual: GpuImageUsages.ValidateDepth(attachment: Depth(clear: 1f, format: GpuPixelFormat.D32Float), height: 4U, sampled: true, width: 4U));
        Assert.StartsWith(
            actualString: Assert.Throws<ArgumentException>(testCode: () => GpuImageUsages.ValidateCreate(format: GpuPixelFormat.D32Float, height: 4U, usage: GpuImageUsage.DepthAttachment, width: 4U)).Message,
            expectedStartString: "A depth attachment image is created from its attachment (IGpuImageFactory.CreateDepth)"
        );
        Assert.Equal(
            expected: "The depth format D32Float is a depth attachment, sampled or not; it declares Sampled, Storage, DepthAttachment. (Parameter 'usage')",
            actual: Assert.Throws<ArgumentException>(testCode: () => GpuImageUsages.Validate(format: GpuPixelFormat.D32Float, height: 4U, usage: (GpuImageUsage.DepthAttachment | GpuImageUsage.Sampled | GpuImageUsage.Storage), width: 4U)).Message
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuImageUsages.ValidateDepth(attachment: Depth(clear: 1.5f, format: GpuPixelFormat.D32Float), height: 4U, sampled: false, width: 4U));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuImageUsages.ValidateDepth(attachment: Depth(clear: float.NaN, format: GpuPixelFormat.D32Float), height: 4U, sampled: false, width: 4U));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuImageUsages.ValidateDepth(attachment: Depth(clear: 0f, format: GpuPixelFormat.R8G8B8A8Unorm), height: 4U, sampled: true, width: 4U));
    }
}
