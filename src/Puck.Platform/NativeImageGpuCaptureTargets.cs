namespace Puck.Platform;

/// <summary>
/// A consumer-provisioned set of shared GPU capture targets for the zero-copy transport of a native-image feed. The
/// consumer creates the textures in shared GPU memory on its render device — each <see cref="Width"/> × <see cref="Height"/>,
/// B8G8R8A8 — and hands their D3D12 <c>CreateSharedHandle</c> NT handles to
/// <see cref="INativeImageCaptureFeed.AttachGpuTargets"/>. The feed opens each handle once on its capture device and,
/// each published tick, copies the captured frame into the next round-robin slot; the consumer imports the same handles
/// on its render device and samples the slot named by <see cref="INativeImageCaptureFeed.LatestGpuSlot"/> whenever
/// <see cref="INativeImageCaptureFeed.GpuRevision"/> advances. Two or more slots keep the writer off the slot a consumer
/// is sampling.
/// </summary>
/// <param name="SharedTargetHandles">The shared NT handles (D3D12 <c>CreateSharedHandle</c>) of the target textures, each
/// created at <see cref="Width"/> × <see cref="Height"/> B8G8R8A8; two or more are required.</param>
/// <param name="Width">The shared-target width in pixels; the consumer sizes it to the live source extent
/// (<see cref="INativeImageCaptureFeed.SourceWidth"/>).</param>
/// <param name="Height">The shared-target height in pixels; the consumer sizes it to the live source extent
/// (<see cref="INativeImageCaptureFeed.SourceHeight"/>).</param>
/// <param name="CpuReadbackDivisor">The cadence divisor for the coexisting CPU readback: every <c>N</c>-th capture tick
/// also runs the CPU path (for the glow and the probe). A value of zero or less disables CPU frames while GPU mode is
/// active.</param>
public sealed record NativeImageGpuCaptureTargets(
    IReadOnlyList<nint> SharedTargetHandles,
    int Width,
    int Height,
    int CpuReadbackDivisor = 8
);
