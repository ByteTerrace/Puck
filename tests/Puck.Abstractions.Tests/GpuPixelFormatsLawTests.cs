using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>Holds the byte layout an image upload reads to its definition: a level of a block-compressed format is rows
/// of whole 4x4 blocks, each edge rounded up; levels halve and never fall below one texel; a chain is its levels back
/// to back. An upload offering any other length, an empty extent or level count, or more levels than the extent has is
/// refused, and a block-compressed format declares no usage but sampling.</summary>
public sealed class GpuPixelFormatsLawTests {
    [Fact]
    public void ABlockCompressedLevelIsRowsOfWholeBlocks() {
        Assert.Equal(expected: ((17UL * 17UL) * 16UL), actual: GpuPixelFormats.LevelByteLength(format: GpuPixelFormat.Bc7Unorm, height: 68U, width: 68U));
        Assert.Equal(expected: ((9UL * 9UL) * 16UL), actual: GpuPixelFormats.LevelByteLength(format: GpuPixelFormat.Bc5Unorm, height: 34U, width: 34U));
        Assert.Equal(expected: ((5UL * 5UL) * 16UL), actual: GpuPixelFormats.LevelByteLength(format: GpuPixelFormat.Bc6hUfloat, height: 17U, width: 17U));
        Assert.Equal(expected: 8UL, actual: GpuPixelFormats.LevelByteLength(format: GpuPixelFormat.Bc4Unorm, height: 1U, width: 1U));
        Assert.Equal(expected: ((3UL * 2UL) * 4UL), actual: GpuPixelFormats.LevelByteLength(format: GpuPixelFormat.R8G8B8A8Unorm, height: 2U, width: 3U));
    }
    [Fact]
    public void AChainIsItsLevelsBackToBack() {
        Assert.Equal(expected: (2U, 1U), actual: GpuPixelFormats.LevelExtent(height: 3U, level: 5U, width: 68U));
        Assert.Equal(
            expected: ((((17UL * 17UL) + (9UL * 9UL)) + (5UL * 5UL)) * 16UL),
            actual: GpuPixelFormats.ChainByteLength(format: GpuPixelFormat.Bc7Unorm, height: 68U, levels: 3U, width: 68U)
        );
    }
    [Fact]
    public void AnUploadOfferingAnyOtherChainIsRefused() {
        var chain = GpuPixelFormats.ChainByteLength(format: GpuPixelFormat.Bc5Unorm, height: 68U, levels: 3U, width: 68U);

        Assert.Equal(expected: chain, actual: GpuPixelFormats.RequireChain(byteLength: ((long)chain), format: GpuPixelFormat.Bc5Unorm, height: 68U, levels: 3U, width: 68U));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuPixelFormats.RequireChain(byteLength: (((long)chain) - 16L), format: GpuPixelFormat.Bc5Unorm, height: 68U, levels: 3U, width: 68U));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuPixelFormats.RequireChain(byteLength: 0L, format: GpuPixelFormat.Bc5Unorm, height: 68U, levels: 0U, width: 68U));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuPixelFormats.RequireChain(byteLength: 0L, format: GpuPixelFormat.Bc5Unorm, height: 68U, levels: 8U, width: 68U));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuPixelFormats.RequireChain(byteLength: 0L, format: GpuPixelFormat.Bc5Unorm, height: 0U, levels: 1U, width: 68U));
    }
    [Fact]
    public void ABlockCompressedFormatIsOnlySampled() {
        GpuImageUsages.Validate(format: GpuPixelFormat.Bc7Unorm, height: 4U, usage: GpuImageUsage.Sampled, width: 4U);

        foreach (var usage in ((ReadOnlySpan<GpuImageUsage>)[GpuImageUsage.Storage, GpuImageUsage.ColorAttachment, (GpuImageUsage.Sampled | GpuImageUsage.Storage)])) {
            Assert.Equal(
                expected: $"The block-compressed format Bc7Unorm is only sampled; it declares {usage}. (Parameter 'usage')",
                actual: Assert.Throws<ArgumentException>(testCode: () => GpuImageUsages.Validate(format: GpuPixelFormat.Bc7Unorm, height: 4U, usage: usage, width: 4U)).Message
            );
        }
    }
}
