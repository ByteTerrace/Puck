namespace Puck.Abstractions.Gpu;

/// <summary>One color attachment of a render pass.</summary>
/// <param name="Format">The attachment's color format.</param>
/// <param name="Load">What the pass does with the contents when it begins.</param>
/// <param name="Store">What the pass does with the contents when it ends.</param>
/// <param name="FinalLayout">The layout the attachment ends in: <see cref="GpuImageLayout.RenderTarget"/>, where the
/// caller records any later transition itself, or <see cref="GpuImageLayout.ShaderReadOnly"/>, where the pass leaves it
/// ready for a later fragment shader to sample.</param>
public readonly record struct GpuColorAttachment(GpuPixelFormat Format, GpuAttachmentLoad Load, GpuAttachmentStore Store, GpuImageLayout FinalLayout);
/// <summary>The depth attachment of a render pass. It begins and ends in <see cref="GpuImageLayout.DepthAttachment"/>,
/// unless its load discards or clears, when it may begin in any layout.</summary>
/// <param name="Format">The attachment's depth format.</param>
/// <param name="Load">What the pass does with the contents when it begins.</param>
/// <param name="Store">What the pass does with the contents when it ends.</param>
public readonly record struct GpuDepthAttachment(GpuPixelFormat Format, GpuAttachmentLoad Load, GpuAttachmentStore Store);
/// <summary>
/// The attachments one render pass draws into, as formats and load and store operations: what a graphics pipeline is
/// created against (<see cref="IGpuPipelineFactory"/>). The images come later, in an <see cref="IGpuFramebuffer"/>.
/// </summary>
/// <param name="Colors">The color attachments, in shader output order (<c>SV_Target0</c> first); at most
/// <see cref="MaxColorAttachments"/>.</param>
/// <param name="Depth">The depth attachment, or <see langword="null"/> for none.</param>
public sealed record GpuRenderPassDescription(IReadOnlyList<GpuColorAttachment> Colors, GpuDepthAttachment? Depth = null) {
    /// <summary>The most color attachments a render pass may have on every backend.</summary>
    public const int MaxColorAttachments = 8;

    /// <summary>Refuses a render pass with no attachment, too many color attachments, a color attachment in a depth format
    /// or a depth attachment in a color format, an undefined operation, or a color final layout other than
    /// <see cref="GpuImageLayout.RenderTarget"/> or <see cref="GpuImageLayout.ShaderReadOnly"/>.</summary>
    /// <exception cref="ArgumentException">The description breaks one of those rules.</exception>
    public void Validate() {
        ArgumentNullException.ThrowIfNull(Colors);

        if (
            (Colors.Count == 0) &&
            (Depth is null)
        ) {
            throw new ArgumentException(message: "A render pass needs at least one attachment.");
        }

        if (Colors.Count > MaxColorAttachments) {
            throw new ArgumentException(message: $"A render pass has at most {MaxColorAttachments} color attachments; this one has {Colors.Count}.");
        }

        foreach (var color in Colors) {
            if (
                !Enum.IsDefined(value: color.Format) ||
                GpuPixelFormats.IsDepth(format: color.Format)
            ) {
                throw new ArgumentException(message: $"A color attachment needs a color format; {color.Format} is not one.");
            }

            if (
                !Enum.IsDefined(value: color.Load) ||
                !Enum.IsDefined(value: color.Store)
            ) {
                throw new ArgumentException(message: "A color attachment's load and store operations must be defined.");
            }

            if (color.FinalLayout is not (GpuImageLayout.RenderTarget or GpuImageLayout.ShaderReadOnly)) {
                throw new ArgumentException(message: $"A color attachment ends in RenderTarget or ShaderReadOnly, not {color.FinalLayout}.");
            }
        }

        if (Depth is { } depth) {
            if (!GpuPixelFormats.IsDepth(format: depth.Format)) {
                throw new ArgumentException(message: $"A depth attachment needs a depth format; {depth.Format} is not one.");
            }

            if (
                !Enum.IsDefined(value: depth.Load) ||
                !Enum.IsDefined(value: depth.Store)
            ) {
                throw new ArgumentException(message: "A depth attachment's load and store operations must be defined.");
            }
        }
    }
}
