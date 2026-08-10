using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// The concrete GPU-services closure every view-composition construction site needs — the whole dependency closure
/// of <see cref="SdfEngineNode"/>, <see cref="Views.SdfCameraView"/>, <see cref="Views.NestedWorldView"/>, and
/// Puck.World's <c>WorldScreenBinder</c>'s stashed view factory turned out to be exactly these three members.
/// Resolved once, eagerly, at the composition root — Puck.Overlays' <c>OverlayServices.Build</c> precedent: resolve
/// inside the factory, hand out concrete members, the provider itself never escapes — then forwarded unchanged to
/// every construction site instead of a retained <see cref="IServiceProvider"/> each site would otherwise stash
/// and re-resolve from on its own late-construction path. Concrete, read-only, and declared in this consuming
/// layer: the constructor rule's shape for a presentation producer context.
/// </summary>
/// <param name="Gpu">The neutral GPU compute services bundle (compute pipelines, storage, descriptors, the queue).</param>
/// <param name="TimingFactory">The GPU timestamp-pool factory, or <see langword="null"/> when the backend
/// registered no timing seam.</param>
/// <param name="TimingRecorder">The GPU timestamp recorder, or <see langword="null"/> (see <see cref="TimingFactory"/>).</param>
public sealed record SdfViewGpuServices(IGpuComputeServices Gpu, IGpuTimingPoolFactory? TimingFactory, IGpuTimingRecorder? TimingRecorder);
