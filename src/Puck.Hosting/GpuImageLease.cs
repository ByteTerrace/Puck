namespace Puck.Hosting;

/// <summary>One image a render node samples for one submitted frame. Most images carry only an image-view handle; an
/// image another producer keeps writing also carries a release callback and token, and the node that sampled it holds
/// the lease in a <see cref="LeaseRetireList"/> until the GPU has finished the submission that read it.</summary>
/// <param name="ImageViewHandle">The backend-native image-view handle, or zero for no image.</param>
/// <param name="Release">The producer's release callback, or <see langword="null"/> for a lease that needs no
/// retirement.</param>
/// <param name="ReleaseToken">The opaque token passed to <paramref name="Release"/>: the producer's own key for the
/// acquisition this lease retires.</param>
public readonly record struct GpuImageLease(nint ImageViewHandle, Action<int>? Release = null, int ReleaseToken = 0) {
    /// <summary>Gets whether this lease must be retired once the submission that sampled it has finished.</summary>
    public bool RequiresRetirement => (Release is not null);

    /// <summary>Releases this lease's acquisition. A handle-only lease is a no-op.</summary>
    public void Retire() => Release?.Invoke(obj: ReleaseToken);

    /// <summary>Wraps an image-view handle that needs no retirement.</summary>
    /// <param name="imageViewHandle">The backend-native image-view handle.</param>
    public static implicit operator GpuImageLease(nint imageViewHandle) => new(ImageViewHandle: imageViewHandle);
}
