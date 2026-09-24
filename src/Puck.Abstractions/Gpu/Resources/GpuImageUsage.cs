namespace Puck.Abstractions.Gpu;

/// <summary>
/// The ways an image may be used, declared when it is created. A backend creates the image with exactly the
/// capabilities its usages need: a Vulkan image with the matching usage flags, a Direct3D 12 texture with the matching
/// resource flags. Every image is also a copy source and destination, so a readback, an upload or a zero clear never
/// needs a usage of its own.
/// </summary>
[Flags]
public enum GpuImageUsage : uint {
    /// <summary>No usage; an image must declare at least one.</summary>
    None = 0,
    /// <summary>A shader samples the image (Vulkan <c>SAMPLED</c>; a Direct3D 12 shader resource view).</summary>
    Sampled = 0x1,
    /// <summary>A compute shader reads and writes the image as a storage image (Vulkan <c>STORAGE</c>; Direct3D 12
    /// <c>ALLOW_UNORDERED_ACCESS</c>). An image published in <see cref="GpuImageLayout.General"/> needs it.</summary>
    Storage = 0x2,
    /// <summary>A render pass draws into the image as a color attachment (Vulkan <c>COLOR_ATTACHMENT</c>; Direct3D 12
    /// <c>ALLOW_RENDER_TARGET</c>).</summary>
    ColorAttachment = 0x4,
    /// <summary>A render pass tests and writes the image as its depth attachment (Vulkan
    /// <c>DEPTH_STENCIL_ATTACHMENT</c>; Direct3D 12 <c>ALLOW_DEPTH_STENCIL</c>). Only a depth format declares it, and it
    /// combines with no other usage.</summary>
    DepthAttachment = 0x8,
}
/// <summary>
/// The rules every image factory applies to a declared <see cref="GpuImageUsage"/>, so both backends refuse the same
/// requests with the same words.
/// </summary>
public static class GpuImageUsages {
    /// <summary>The usages an image may declare.</summary>
    public const GpuImageUsage All = GpuImageUsage.Sampled | GpuImageUsage.Storage | GpuImageUsage.ColorAttachment | GpuImageUsage.DepthAttachment;

    /// <summary>Refuses an image request whose extent is empty, whose usage is empty or undefined, or whose usage does not
    /// fit its format.</summary>
    /// <param name="format">The image format.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <param name="usage">The declared usages.</param>
    /// <exception cref="ArgumentOutOfRangeException">The extent is zero, the usage is empty or undefined, or the format is
    /// undefined.</exception>
    /// <exception cref="ArgumentException">A depth format declares a usage other than
    /// <see cref="GpuImageUsage.DepthAttachment"/>, or a color format declares it.</exception>
    public static void Validate(GpuPixelFormat format, uint width, uint height, GpuImageUsage usage) {
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);

        if (!Enum.IsDefined(value: format)) {
            throw new ArgumentOutOfRangeException(
                actualValue: format,
                message: "The image format is not defined.",
                paramName: nameof(format)
            );
        }

        if (
            (usage == GpuImageUsage.None) ||
            ((usage & ~All) != 0)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: usage,
                message: "An image declares at least one defined usage.",
                paramName: nameof(usage)
            );
        }

        var depth = GpuPixelFormats.IsDepth(format: format);

        if (depth != (usage == GpuImageUsage.DepthAttachment)) {
            throw new ArgumentException(
                message: (depth
                    ? $"The depth format {format} is only a depth attachment; it declares {usage}."
                    : $"The color format {format} cannot be a depth attachment; it declares {usage}."),
                paramName: nameof(usage)
            );
        }
    }
}
