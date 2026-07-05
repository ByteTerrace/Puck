using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.SdfVm;

namespace Puck.Demo;

/// <summary>
/// The data an SDF world render host needs — the render boundary between a scene-owning application node (the
/// overworld, a document-driven world) and the shared assembly in <see cref="SdfWorldRenderBuilder"/>. Everything
/// backend-specific (kernel bytecode selection, child-node backend flags, overlay availability) derives from
/// <see cref="HostsOnDirectX"/> in ONE place, the builder; a spec never names a bytecode extension.
/// </summary>
/// <param name="FrameSource">The per-frame source of the scene, cameras, and viewport regions.</param>
/// <param name="Width">The render width in pixels.</param>
/// <param name="Height">The render height in pixels.</param>
internal sealed record SdfWorldRenderSpec(
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
    /// <summary>An optional render-node decorator wrapped around the producer (e.g. the overworld binding-bar overlay).
    /// Applied by the builder ONLY on a Vulkan host — the current decorators bind Vulkan services, and binding
    /// Vulkan bytecode on a Direct3D 12 host must be an explicit decision, never a silent one.</summary>
    public Func<SdfEngineNode, IRenderNode>? Decorate { get; init; }
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
    /// <summary>Screen-source providers keyed by the program-declared screen index (the diegetic-screen seam).</summary>
    public IReadOnlyDictionary<int, Func<nint>>? ScreenSources { get; init; }
    /// <summary>Screen-LIGHT color providers, parallel to <see cref="ScreenSources"/>: the colored glow each screen
    /// emits into the room (its framebuffer average), keyed by screen index.</summary>
    public IReadOnlyDictionary<int, Func<Vector3>>? ScreenLights { get; init; }
}
