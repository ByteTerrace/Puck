namespace Puck.Overlays;

/// <summary>One acquired frame-source lease: a same-device sampleable image view a <c>Frame</c> HUD element samples
/// for the produced frame — the shape of <c>Puck.SdfVm.SdfScreenSourceFrame</c>, declared here so Puck.Overlays gains
/// no reference to Puck.SdfVm.</summary>
/// <param name="ImageViewHandle">The native image-view handle to sample.</param>
/// <param name="Release">The host's release callback for this lease, or <see langword="null"/> for a lease that
/// needs no retirement.</param>
/// <param name="ReleaseToken">The opaque token passed to <paramref name="Release"/> — the host's own bookkeeping key
/// for which acquisition this retires.</param>
public readonly record struct OverlayFrameLease(nint ImageViewHandle, Action<int>? Release, int ReleaseToken) {
    /// <summary>Gets whether this lease must be retired (<see cref="Retire"/>) once the pass that sampled it has
    /// retired on the GPU.</summary>
    public bool RequiresRetirement => (Release is not null);

    /// <summary>Releases this lease's acquisition. A handle-only lease is a no-op.</summary>
    public void Retire() => Release?.Invoke(obj: ReleaseToken);
}
/// <summary>The host seam <see cref="OverlayFrameSlots"/> acquires HUD <c>Frame</c>-element content through — one
/// live source per opaque key the host assigned (a declared frame source's registry key).</summary>
public interface IOverlayFrameSources {
    /// <summary>Attempts to acquire this produced frame's content for <paramref name="key"/>.</summary>
    /// <param name="key">The opaque source id the host handed the HUD element.</param>
    /// <param name="lease">The acquired lease, when live.</param>
    /// <returns><see langword="true"/> when content is live this frame; <see langword="false"/> when there is
    /// nothing to show (the element then draws nothing, never a placeholder).</returns>
    bool TryAcquire(int key, out OverlayFrameLease lease);
}
