using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>
/// The data an SDF world render host needs — the render boundary between a scene-owning application node (the
/// overworld, a document-driven world) and the shared assembly in <see cref="SdfWorldRenderBuilder"/>. Everything
/// backend-specific (kernel bytecode selection) derives from
/// <see cref="HostsOnDirectX"/> in one place, the builder; a spec never names a bytecode extension.
/// </summary>
/// <param name="FrameSource">The per-frame source of the scene, cameras, and viewport regions.</param>
/// <param name="Width">The render width in pixels.</param>
/// <param name="Height">The render height in pixels.</param>
public sealed record SdfWorldRenderSpec(
    ISdfFrameSource FrameSource,
    uint Width,
    uint Height
) {
    /// <summary>The carve-bake brick pool's voxel capacity (see <see cref="SdfWorldTablesOptions.BrickPoolVoxelCapacity"/>),
    /// frozen at construction. Defaults to <see cref="SdfWorldTables.DefaultBrickPoolVoxelCapacity"/> (64 MB); a host
    /// whose scene never bakes carves sets 0 to allocate no pool.</summary>
    public int BrickPoolVoxelCapacity { get; init; } = SdfWorldTables.DefaultBrickPoolVoxelCapacity;

    /// <summary>An optional in-place decorator applied to <see cref="FrameSource"/> before the residency is built —
    /// the seam a host uses to wrap the scene's frame source (e.g. a diegetic-UI overlay that emits its own SDF
    /// geometry into the program). Returns the frame source to actually render; identity when absent.</summary>
    public Func<ISdfFrameSource, ISdfFrameSource>? DecorateFrameSource { get; init; }
    /// <summary>A floor on the dynamic-transform slot capacity — headroom above the program's own
    /// <see cref="SdfProgram.RequiredDynamicTransformCapacity"/> (the engine always raises the floor to that); a
    /// host whose moving-entity population grows over the run passes its peak here.</summary>
    public int DynamicTransformCapacity { get; init; }
    /// <summary>Whether the resolved host backend is Direct3D 12 — the one input every backend-specific choice
    /// (bytecode extension, overlay availability) derives from.</summary>
    public bool HostsOnDirectX { get; init; }
    /// <summary>A floor on the instance count the per-tile mask buffer is sized for — the capacity envelope for a
    /// frame source that hot-swaps programs whose instance counts grow past the first frame's.</summary>
    public int InstanceCapacity { get; init; }
    /// <summary>A floor on the program buffer's packed-word capacity — the capacity envelope for a frame source
    /// that hot-swaps programs larger than the first frame's.</summary>
    public int ProgramWordCapacity { get; init; }
    /// <summary>What each program-declared screen shows and the light it casts into the room, or <see langword="null"/>
    /// for a host with no screens: the source instance a screen reads, whose image the render graph hands each view's passes. A view's instance
    /// retires each lease only after the submission that sampled it has completed, and adds the wait the lease carries to
    /// that submission.</summary>
    public ISdfScreenSources? ScreenSources { get; init; }

    // NOTE: screen-surface TRANSFORM providers are read straight off FrameSource.ScreenSurfaceTransforms (see
    // ISdfFrameSource) rather than threaded through their own spec field — a caller's own type coupling would
    // otherwise grow just to spell SdfScreenSurfaceTransform in its render-assembly call site.
    /// <summary>The residency's name, which labels its uploads in a GPU capture and names its counted work. Defaults to
    /// <c>world</c>.</summary>
    public string Name { get; init; } = "world";
}
