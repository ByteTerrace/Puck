namespace Puck.SdfVm;

/// <summary>One screen source selected for one submitted frame. Most sources carry only an image-view handle; an
/// asynchronously updated external source may also carry a release callback and token that the renderer invokes only
/// after the submitted GPU frame has retired.</summary>
/// <param name="ImageViewHandle">The backend-native image-view handle, or zero for an unbound screen.</param>
/// <param name="Release">The optional callback that releases this frame's source acquisition.</param>
/// <param name="ReleaseToken">The opaque token passed to <paramref name="Release"/>.</param>
public readonly record struct SdfScreenSourceFrame(nint ImageViewHandle, Action<int>? Release = null, int ReleaseToken = 0) {
    /// <summary>Gets whether this frame carries an acquisition that must retire with the GPU submission.</summary>
    public bool RequiresRetirement => (Release is not null);

    /// <summary>Releases this frame's acquisition. A handle-only frame is a no-op.</summary>
    public void Retire() => Release?.Invoke(obj: ReleaseToken);

    /// <summary>Wraps an unleased image-view handle.</summary>
    /// <param name="imageViewHandle">The backend-native image-view handle.</param>
    public static implicit operator SdfScreenSourceFrame(nint imageViewHandle) => new(ImageViewHandle: imageViewHandle);
}
