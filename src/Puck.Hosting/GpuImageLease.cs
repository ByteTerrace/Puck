using System.Runtime.CompilerServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Hosting;

/// <summary>The identity of an image's actual publication. The owner identifies one publication stream and the
/// sequence identifies one successful write within it; acquiring the same image again keeps this identity.</summary>
/// <param name="Owner">The publication stream, or null when no image was published.</param>
/// <param name="Sequence">The stream's monotonically increasing write sequence, starting at one.</param>
public readonly record struct GpuImagePublication(object? Owner, long Sequence) {
    /// <summary>Gets whether the identity describes a publication.</summary>
    public bool IsKnown => ((Owner is not null) && (Sequence > 0));

    /// <summary>Compares the publication stream by object identity and its completed write sequence.</summary>
    public bool Equals(GpuImagePublication other) => (ReferenceEquals(objA: Owner, objB: other.Owner) && (Sequence == other.Sequence));
    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(value1: ((Owner is null) ? 0 : RuntimeHelpers.GetHashCode(o: Owner)), value2: Sequence);
}
/// <summary>One image a render node samples for one submitted frame. Most images carry only an image-view handle; an
/// image another producer keeps writing also carries a release callback and token, and the node that sampled it holds
/// the lease in a <see cref="LeaseRetireList"/> until the GPU has finished the submission that read it. An image another
/// device writes carries the wait on that device's fence too, which the node adds to the one submission that samples
/// the image, never to whichever submission the device makes next.</summary>
/// <param name="ImageViewHandle">The backend-native image-view handle, or zero for no image.</param>
/// <param name="Release">The producer's release callback, or <see langword="null"/> for a lease that needs no
/// retirement.</param>
/// <param name="ReleaseToken">The opaque token passed to <paramref name="Release"/>: the producer's own key for the
/// acquisition this lease retires.</param>
/// <param name="Wait">The wait the sampling submission carries before it may read the image: the value the writing
/// device's shared fence reaches once the write finished, or the default (no fence) for an image written on this
/// device or finished on the CPU.</param>
/// <param name="Publication">The acquired image's publication, captured together with its lease and fence. A handle
/// or release token identifies storage or an acquisition and cannot substitute for a publication.</param>
public readonly record struct GpuImageLease(nint ImageViewHandle, Action<int>? Release = null, int ReleaseToken = 0, GpuExternalWait Wait = default, GpuImagePublication Publication = default) {
    /// <summary>Gets the complete same-device image acquired with this lease, when its producer exposes one. The
    /// image, view, extent, format, publication and wait all describe the same acquired slot; an empty surface means
    /// only sampling through the view is supported. Holding this metadata alone never freezes the image's pixels.</summary>
    public Surface Image { get; init; }
    /// <summary>Gets whether the sampling submission must wait on another device's fence (<see cref="Wait"/>).</summary>
    public bool HasWait => (Wait.Fence is not null);
    /// <summary>Gets whether this lease must be retired once the submission that sampled it has finished.</summary>
    public bool RequiresRetirement => (Release is not null);

    /// <summary>Releases this lease's acquisition. A handle-only lease is a no-op.</summary>
    public void Retire() => Release?.Invoke(obj: ReleaseToken);

    /// <summary>Wraps an image-view handle that needs no retirement.</summary>
    /// <param name="imageViewHandle">The backend-native image-view handle.</param>
    public static implicit operator GpuImageLease(nint imageViewHandle) => new(ImageViewHandle: imageViewHandle);
}
