namespace Puck.Abstractions.Gpu;

/// <summary>
/// What a render pass does with an attachment's contents when it ends.
/// </summary>
public enum GpuAttachmentStore : byte {
    /// <summary>Keeps what the pass wrote, for a later pass, a sampler, a copy or publication.</summary>
    Store = 0,
    /// <summary>Lets the contents go: nothing reads them after the pass.</summary>
    Discard = 1,
}
