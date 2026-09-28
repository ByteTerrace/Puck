namespace Puck.Abstractions.Gpu;

/// <summary>
/// Uploads CPU pixels to a GPU image for sampling, on the device context the <see cref="IGpuSurfaceTransferFactory"/>
/// that created it is bound to. The upload object owns the image (and staging resources) behind the handles it
/// returns, holds them on the device of its first upload, and is released before that device goes, a device loss
/// included.
/// </summary>
public interface IGpuSurfaceUpload : IDisposable {
    /// <summary>Uploads an image's levels and returns a native image view handle over every one of them, ready for
    /// sampling by any work submitted to the same queue AFTER this call returns — the copy is either complete (a
    /// blocking backend) or queue-ordered ahead of that work (Vulkan's pipelined fenced path); the caller's pixel buffer
    /// is free to reuse either way. The returned handle is owned by this upload object — the caller never destroys it —
    /// and is only guaranteed valid until the next <see cref="Upload"/> on this instance or this object's disposal
    /// (Direct3D 12 replaces the handle on every call; Vulkan reuses the same view while the extent, format and level
    /// count are unchanged).</summary>
    /// <param name="pixels">The image's levels from level 0, tightly packed and back to back
    /// (<see cref="GpuPixelFormats.ChainByteLength"/>): rows of texels, or rows of 4x4 blocks for a block-compressed
    /// format.</param>
    /// <param name="format">The pixel format.</param>
    /// <param name="width">The width of level 0, in texels.</param>
    /// <param name="height">The height of level 0, in texels.</param>
    /// <param name="levels">The number of mip levels <paramref name="pixels"/> holds, each halving the one before it
    /// (<see cref="GpuPixelFormats.LevelExtent"/>); one for an image without mips.</param>
    /// <returns>The native image view handle, owned by this upload object.</returns>
    /// <exception cref="ArgumentException"><paramref name="pixels"/> is not exactly the chain's length, or a dimension
    /// or the level count is zero.</exception>
    /// <exception cref="NotSupportedException">The device cannot sample <paramref name="format"/>; the message names
    /// the format and the device.</exception>
    nint Upload(ReadOnlyMemory<byte> pixels, GpuPixelFormat format, uint width, uint height, uint levels = 1U);
}
