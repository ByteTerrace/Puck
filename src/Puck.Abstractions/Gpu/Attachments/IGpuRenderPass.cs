namespace Puck.Abstractions.Gpu;

/// <summary>
/// A backend's render pass: the attachment formats and load and store operations of <see cref="Description"/>, which a
/// graphics pipeline is created against and a framebuffer is made compatible with. It owns no image.
/// </summary>
public interface IGpuRenderPass : IDisposable {
    /// <summary>Gets the attachments the pass draws into.</summary>
    GpuRenderPassDescription Description { get; }
}
/// <summary>
/// A render pass bound to the images it draws into: one image per color attachment, in order, and the depth image when
/// the pass has one, all of one extent. <see cref="IGpuRecorder.BeginRenderPass"/> begins it. It owns the
/// backend's attachment views, never the images or the render pass.
/// </summary>
public interface IGpuFramebuffer : IDisposable {
    /// <summary>Gets the height, in pixels, every attachment shares.</summary>
    uint Height { get; }
    /// <summary>Gets the render pass the framebuffer was made for.</summary>
    IGpuRenderPass RenderPass { get; }
    /// <summary>Gets the width, in pixels, every attachment shares.</summary>
    uint Width { get; }
}
/// <summary>
/// Creates render passes and the framebuffers that bind them to images.
/// </summary>
public interface IGpuRenderPassFactory {
    /// <summary>Creates a render pass.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="description">The attachments; <see cref="GpuRenderPassDescription.Validate"/> states the rules.</param>
    /// <returns>A new, owning <see cref="IGpuRenderPass"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="description"/> breaks a rule.</exception>
    IGpuRenderPass Create(IGpuDeviceContext deviceContext, GpuRenderPassDescription description);
    /// <summary>Binds a render pass to images; <see cref="GpuFramebuffers.Validate"/> states the rules.</summary>
    /// <param name="deviceContext">The GPU device context.</param>
    /// <param name="renderPass">The render pass, created by this factory.</param>
    /// <param name="colors">One image per color attachment, in order, each of that attachment's format and declaring
    /// <see cref="GpuImageUsage.ColorAttachment"/>.</param>
    /// <param name="depth">The depth image when the pass has a depth attachment, declaring
    /// <see cref="GpuImageUsage.DepthAttachment"/>; otherwise <see langword="null"/>.</param>
    /// <returns>A new, owning <see cref="IGpuFramebuffer"/>, which does not own the images.</returns>
    /// <exception cref="ArgumentException">The images do not match the render pass or each other's extent.</exception>
    IGpuFramebuffer CreateFramebuffer(IGpuDeviceContext deviceContext, IGpuRenderPass renderPass, IReadOnlyList<IGpuImage> colors, IGpuImage? depth);
}
/// <summary>
/// The rules every render pass factory applies to a framebuffer's images.
/// </summary>
public static class GpuFramebuffers {
    /// <summary>Resolves the area a render pass draws: the whole framebuffer, or a rectangle inside it.</summary>
    /// <param name="framebuffer">The framebuffer the pass begins on.</param>
    /// <param name="area">The requested area, or <see langword="null"/> for the whole framebuffer.</param>
    /// <returns>The area the pass draws.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="area"/> is empty or reaches outside the
    /// framebuffer.</exception>
    public static GpuPixelRect ResolveArea(IGpuFramebuffer framebuffer, GpuPixelRect? area) {
        ArgumentNullException.ThrowIfNull(framebuffer);

        if (area is not { } rect) {
            return GpuPixelRect.Covering(
                height: framebuffer.Height,
                width: framebuffer.Width
            );
        }

        if (
            (rect.Width == 0) ||
            (rect.Height == 0) ||
            (rect.X < 0) ||
            (rect.Y < 0) ||
            ((((ulong)rect.X) + rect.Width) > framebuffer.Width) ||
            ((((ulong)rect.Y) + rect.Height) > framebuffer.Height)
        ) {
            throw new ArgumentOutOfRangeException(
                actualValue: rect,
                message: $"A render area is a non-empty rectangle inside the {framebuffer.Width}x{framebuffer.Height} framebuffer.",
                paramName: nameof(area)
            );
        }

        return rect;
    }
    /// <summary>Refuses images that do not match a render pass: a color image count other than its color attachment
    /// count, a depth image where it has no depth attachment or none where it has one, an image of another format or
    /// without the attachment usage, or images of differing extents.</summary>
    /// <param name="description">The render pass's attachments.</param>
    /// <param name="colors">The color images.</param>
    /// <param name="depth">The depth image, or <see langword="null"/>.</param>
    /// <returns>The extent every image shares.</returns>
    /// <exception cref="ArgumentException">An image does not match.</exception>
    public static (uint Width, uint Height) Validate(GpuRenderPassDescription description, IReadOnlyList<IGpuImage> colors, IGpuImage? depth) {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(colors);

        if (colors.Count != description.Colors.Count) {
            throw new ArgumentException(message: $"The render pass has {description.Colors.Count} color attachments; the framebuffer binds {colors.Count} images.");
        }

        if ((depth is null) != (description.Depth is null)) {
            throw new ArgumentException(message: ((depth is null)
                ? "The render pass has a depth attachment; the framebuffer binds no depth image."
                : "The render pass has no depth attachment; the framebuffer binds a depth image."));
        }

        (uint Width, uint Height)? extent = null;

        void Check(IGpuImage image, GpuPixelFormat format, GpuImageUsage usage) {
            if (image.Format != format) {
                throw new ArgumentException(message: $"An attachment of format {format} is bound to an image of format {image.Format}.");
            }

            if ((image.Usage & usage) == 0) {
                throw new ArgumentException(message: $"An image bound as an attachment must declare {usage}; it declares {image.Usage}.");
            }

            if (
                (extent is { } expected) &&
                (expected != (image.Width, image.Height))
            ) {
                throw new ArgumentException(message: $"Every attachment shares one extent; {image.Width}x{image.Height} differs from {expected.Width}x{expected.Height}.");
            }

            extent = (image.Width, image.Height);
        }

        for (var index = 0; (index < colors.Count); index++) {
            Check(
                format: description.Colors[index].Format,
                image: colors[index],
                usage: GpuImageUsage.ColorAttachment
            );
        }

        if (depth is not null) {
            Check(
                format: description.Depth!.Value.Format,
                image: depth,
                usage: GpuImageUsage.DepthAttachment
            );
        }

        return extent!.Value;
    }
}
