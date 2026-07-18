using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.SdfVm;

/// <summary>
/// The data an SDF world render host needs — the render boundary between a scene-owning application node (the
/// overworld, a document-driven world) and the shared assembly in <see cref="SdfWorldRenderBuilder"/>. Everything
/// backend-specific (kernel bytecode selection, child-node backend flags, overlay availability) derives from
/// <see cref="HostsOnDirectX"/> in ONE place, the builder; a spec never names a bytecode extension.
/// </summary>
/// <param name="FrameSource">The per-frame source of the scene, cameras, and viewport regions.</param>
/// <param name="Width">The render width in pixels.</param>
/// <param name="Height">The render height in pixels.</param>
public sealed record SdfWorldRenderSpec(
    ISdfFrameSource FrameSource,
    uint Width,
    uint Height
) {
    /// <summary>An optional PNG path; the first rendered frame is read back and written there.</summary>
    public string? CapturePath { get; init; }
    /// <summary>Child render nodes keyed by viewport slot (each supplies its slot's surface instead of an SDF
    /// camera).</summary>
    public IReadOnlyDictionary<int, IRenderNode>? Children { get; init; }
    /// <summary>An optional factory for the output image (export mode when it returns an exportable image).</summary>
    public Func<IGpuDeviceContext, IGpuStorageImage>? CreateOutputImage { get; init; }
    /// <summary>An optional render-node decorator wrapped around the producer (e.g. the unified overlay). Applied on
    /// EVERY host backend — a decorator resolves neutral services and selects its bytecode from the resolved host
    /// (the <see cref="SdfWorldRenderBuilder.BytecodeExtension"/> convention), exactly like the kernels.</summary>
    public Func<SdfEngineNode, IRenderNode>? Decorate { get; init; }
    /// <summary>An optional in-place decorator applied to <see cref="FrameSource"/> before the engine node is built —
    /// the seam a host uses to wrap the scene's frame source (e.g. a diegetic-UI overlay that emits its own SDF
    /// geometry into the program). Returns the frame source to actually render; identity when absent.</summary>
    public Func<ISdfFrameSource, ISdfFrameSource>? DecorateFrameSource { get; init; }
    /// <summary>A FLOOR on the dynamic-transform slot capacity — HEADROOM above the program's own
    /// <see cref="SdfProgram.RequiredDynamicTransformCapacity"/> (the engine always raises the floor to that); a
    /// host whose moving-entity population grows over the run passes its peak here.</summary>
    public int DynamicTransformCapacity { get; init; }
    /// <summary>Whether the resolved host backend is Direct3D 12 — the ONE input every backend-specific choice
    /// (bytecode extension, child-node flags, overlay availability) derives from.</summary>
    public bool HostsOnDirectX { get; init; }
    /// <summary>A FLOOR on the instance count the per-tile mask buffer is sized for — the capacity envelope for a
    /// frame source that hot-swaps programs whose instance counts grow past the first frame's.</summary>
    public int InstanceCapacity { get; init; }
    /// <summary>A FLOOR on the program buffer's packed-word capacity — the capacity envelope for a frame source
    /// that hot-swaps programs larger than the first frame's.</summary>
    public int ProgramWordCapacity { get; init; }
    /// <summary>A FLOOR on the compositor's viewport capacity — the capacity envelope for a frame source whose
    /// per-frame view count GROWS past the first frame's (a split-screen host whose players join later). The engine
    /// composites each frame's actual <see cref="SdfFrame.Views"/> count, up to this envelope; without a floor the
    /// capacity freezes at the first frame's count (the pre-existing behavior).</summary>
    public int ViewportCapacity { get; init; }
    /// <summary>The carve-bake brick pool's voxel capacity (see <see cref="SdfWorldEngineOptions.BrickPoolVoxelCapacity"/>),
    /// FROZEN at construction. Defaults to <see cref="SdfWorldEngine.DefaultBrickPoolVoxelCapacity"/> (64 MB); a host
    /// whose scene never bakes carves sets 0 to allocate no pool.</summary>
    public int BrickPoolVoxelCapacity { get; init; } = SdfWorldEngine.DefaultBrickPoolVoxelCapacity;
    /// <summary>Screen-source providers keyed by the program-declared screen index (the diegetic-screen seam).</summary>
    public IReadOnlyDictionary<int, Func<nint>>? ScreenSources { get; init; }
    /// <summary>Screen-LIGHT color providers, parallel to <see cref="ScreenSources"/>: the colored glow each screen
    /// emits into the room (its framebuffer average), keyed by screen index.</summary>
    public IReadOnlyDictionary<int, Func<Vector3>>? ScreenLights { get; init; }
    // NOTE: screen-surface TRANSFORM providers are read straight off FrameSource.ScreenSurfaceTransforms (see
    // ISdfFrameSource) rather than threaded through their own spec field — a caller's own type coupling would
    // otherwise grow just to spell SdfScreenSurfaceTransform in its render-assembly call site.

    /// <summary>The resolved <c>host.timing</c> toggle (per-pass GPU-ms timestamps), or <see langword="null"/> for the
    /// disarmed default. The engine always receives the timing seam and arms live from
    /// <see cref="GpuTimingControl.Shared"/> (arm it live via the demo's gpu.timing switch / Puck.World's world.timing
    /// verb, or the run-doc <c>host.timing</c> field) — this value only SEEDS that shared control at construction, the
    /// lowest precedence tier beneath a programmatic arm and the run-doc composition seed.</summary>
    public bool? Timing { get; init; }
    /// <summary>The resolved <c>PUCK_RAY_QUERY</c> toggle, or <see langword="null"/> to let <see cref="SdfEngineNode"/>
    /// fall back to the environment/default. Parallel to <see cref="Timing"/>; see
    /// <see cref="SdfEngineNode"/>'s constructor doc for why no current render path consults it yet.</summary>
    public bool? RayQuery { get; init; }
}
