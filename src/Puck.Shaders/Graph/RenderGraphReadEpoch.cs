using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>A finite operation's shared snapshot of independent image reads. Each producer is copied once by its
/// first recording consumer; all consumers use that submitted copy until the owner ends the epoch. The runtime still
/// observes live acquisitions so a later source change can start a new epoch. Every member runs on the frame thread.</summary>
public sealed class RenderGraphReadEpoch : IDisposable {
    private readonly Dictionary<string, FrozenRead> m_images = new(comparer: StringComparer.Ordinal);

    /// <summary>Gets whether the operation still consumes this epoch's immutable pixels.</summary>
    public bool IsActive { get; private set; } = true;
    /// <summary>Gets whether every submitted copy still has its original device owner. Loss invalidates the whole
    /// epoch; its owner must start another epoch before any consumer can render again.</summary>
    public bool IsValid { get; private set; } = true;

    /// <summary>Gets whether a live acquisition differs from one already copied for this epoch.</summary>
    public bool Changed { get; private set; }

    /// <summary>Ends the epoch. Existing node ownership and submitted-reader leases retire its copies safely.</summary>
    public void Dispose() => IsActive = false;

    internal bool TryGet(string producer, out FrozenRead? image) => m_images.TryGetValue(key: producer, value: out image);
    internal void Observe(in RenderGraphExternalInput input) {
        if (m_images.TryGetValue(key: input.Producer, value: out var held)) {
            Changed |= ((input.Publication != held.Publication) || (input.Tainted != held.Tainted));
        }
    }
    internal void Submitted(FrozenRead image) => m_images.Add(key: image.Producer, value: image);
    internal void Forget(FrozenRead image) {
        if (m_images.TryGetValue(key: image.Producer, value: out var current) && ReferenceEquals(objA: current, objB: image)) {
            m_images.Remove(key: image.Producer);
            // An owner disappearing cannot silently replace part of an in-flight immutable source.
            IsValid = false;
        }
    }

    internal sealed class FrozenRead(RenderGraphReadEpoch epoch, string producer, IGpuImage image,
        GpuImagePublication publication, bool tainted) : IDisposable {
        public RenderGraphReadEpoch Epoch { get; } = epoch;
        public string Producer { get; } = producer;
        public GpuImagePublication Publication { get; } = publication;
        public bool Tainted { get; } = tainted;
        public Surface Image { get; } = Surface.SameDeviceImage(image.ImageHandle, image.ImageViewHandle,
            image.Width, image.Height, image.Format);

        public void Dispose() { Epoch.Forget(image: this); image.Dispose(); }
    }
}
