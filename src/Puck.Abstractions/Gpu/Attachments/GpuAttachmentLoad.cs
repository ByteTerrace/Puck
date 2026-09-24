namespace Puck.Abstractions.Gpu;

/// <summary>
/// What a render pass does with an attachment's contents when it begins.
/// </summary>
public enum GpuAttachmentLoad : byte {
    /// <summary>Clears the attachment: a color attachment to opaque black (0, 0, 0, 1), a depth attachment to 1, the far
    /// plane. The contents before the pass are discarded, so the attachment may begin in any layout.</summary>
    Clear = 0,
    /// <summary>Keeps the attachment's contents. The attachment must already be in its attachment layout
    /// (<see cref="GpuImageLayout.RenderTarget"/> or <see cref="GpuImageLayout.DepthAttachment"/>).</summary>
    Load = 1,
    /// <summary>Discards the contents without clearing them: the pass writes every pixel it reads. The attachment may
    /// begin in any layout.</summary>
    Discard = 2,
}
