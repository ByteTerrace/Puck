namespace Puck.Platform;

/// <summary>
/// A live camera session on the GPU-resident zero-copy tier (M3): frames never visit host memory. The platform's
/// decode/processing device (Media Foundation's D3D11 device on Windows) converts each captured frame on-GPU and copies
/// it into one of the <em>consumer-provisioned</em> shared targets — textures the consumer created in shared GPU memory
/// (sized <see cref="Width"/> × <see cref="Height"/>, B8G8R8A8) and handed over as opaque shared handles via
/// <see cref="Start"/>. The consumer imports those same handles once on its render device and, each frame, samples the
/// slot named by <see cref="LatestSlot"/> when <see cref="FrameVersion"/> advances.
/// <para>Two-phase open: construction negotiates the device + output size (so the consumer can size the targets), then
/// <see cref="Start"/> begins streaming into them. The producer completes each copy on its own thread (a GPU flush +
/// CPU fence at the camera's cadence) <em>before</em> publishing the slot, so a published slot is always safe to sample
/// — the render pump never waits.</para>
/// <para>This built-ahead tier currently has one implementation, and its opener
/// <see cref="ICameraCaptureService.TryOpenSharedDefault"/> has no call sites.</para>
/// </summary>
public interface ICameraSharedCaptureSession : IDisposable {
    /// <summary>A monotonically increasing counter of frames published into the shared targets; a consumer compares it
    /// against the value it last sampled to skip unchanged frames (the newest-frame-wins drop policy).</summary>
    long FrameVersion { get; }
    /// <summary>Whether the feed has permanently stopped (device unplugged, end of stream, or a mid-stream error) — the
    /// consumer's signal to tear this tier down and re-open the device.</summary>
    bool IsEnded { get; }
    /// <summary>The <see cref="System.Diagnostics.Stopwatch"/> timestamp of the most recent slot publication (stamped on
    /// the grabber thread, so it shares the render pacer's clock domain) — the genlock arrival signal.</summary>
    long LastFrameTimestamp { get; }
    /// <summary>The negotiated frame height in pixels — the height the shared targets must be created with.</summary>
    int Height { get; }
    /// <summary>The index (into the <see cref="Start"/> target list) of the most recently published frame, or
    /// <c>-1</c> until the first frame arrives.</summary>
    int LatestSlot { get; }
    /// <summary>A human-readable device name, for diagnostics.</summary>
    string Name { get; }
    /// <summary>The negotiated frame width in pixels — the width the shared targets must be created with.</summary>
    int Width { get; }

    /// <summary>Begins streaming into the given shared targets (opened on the platform's decode device); frames are
    /// then published round-robin across the slots.</summary>
    /// <param name="sharedTargetHandles">The consumer-provisioned shared textures (opaque NT handles on Windows), each
    /// <see cref="Width"/> × <see cref="Height"/> B8G8R8A8; two or more slots keep the writer off the slot a consumer
    /// is sampling.</param>
    /// <exception cref="ArgumentException">No targets were provided.</exception>
    /// <exception cref="InvalidOperationException">The session already started, or a target could not be opened.</exception>
    void Start(IReadOnlyList<nint> sharedTargetHandles);
}
