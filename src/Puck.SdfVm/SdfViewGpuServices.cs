using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>
/// The concrete GPU-services closure every view-composition construction site needs — the whole dependency closure
/// of <see cref="SdfEngineNode"/>, <see cref="Views.SdfCameraView"/>, <see cref="Views.WorldSessionView"/>, and
/// Puck.World's <c>WorldScreenBinder</c>'s stashed view factory: the compute services and the pipeline sets they
/// share. Resolved once, eagerly, at the composition root — Puck.Overlays' <c>OverlayServices.Build</c> precedent: resolve
/// inside the factory, hand out concrete members, the provider itself never escapes — then forwarded unchanged to
/// every construction site instead of a retained <see cref="IServiceProvider"/> each site would otherwise stash
/// and re-resolve from on its own late-construction path. Concrete, read-only, and declared in this consuming
/// layer: the constructor rule's shape for a presentation producer context.
/// </summary>
/// <param name="Gpu">The neutral GPU compute services bundle (compute pipelines, storage, descriptors, the queue).</param>
/// <param name="Pipelines">The pipeline sets every node and view built from this closure shares: one per device and
/// kernel set, however many render with it.</param>
public sealed record SdfViewGpuServices(IGpuComputeServices Gpu, SdfWorldPipelineCache Pipelines);
