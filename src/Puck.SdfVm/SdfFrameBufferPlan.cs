using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

/// <summary>The device-local buffers that one SDF-engine dispatch writes and a later dispatch in the same command list reads.</summary>
public enum SdfFrameBuffer {
    /// <summary>The device-local viewport table the upload pass copies from the ring slot's host table.</summary>
    Viewports,
    /// <summary>The device-local dynamic-transform table the upload pass copies from the ring slot's host table.</summary>
    DynamicTransforms,
    /// <summary>The device-local frame instance grid the upload pass copies from the ring slot's host table.</summary>
    InstanceGrid,
    /// <summary>The carve-bake brick pool the brick upload and bake passes write.</summary>
    BrickPool,
    /// <summary>The per-tile instance masks the mask pass writes.</summary>
    InstanceMasks,
    /// <summary>The cull buffer: the beam's four tile planes and refitted part bounds.</summary>
    Tiles,
    /// <summary>The indirect dispatch arguments the cull-args pass writes for the hit and views passes.</summary>
    ViewsArgs,
    /// <summary>The surviving-tile bbox group origin the cull-args pass writes.</summary>
    CullBounds,
    /// <summary>The per-pixel visibility records (<c>sdf-visibility.hlsli</c>) the hit passes write and shading reads.</summary>
    PrimaryHits,
}
/// <summary>The SDF engine's dispatches in recording order. Brick work and the per-table uploads record only when they have work.</summary>
public enum SdfFramePass {
    /// <summary>A queued host-baked brick copied into the pool.</summary>
    BrickUpload,
    /// <summary>Carve-bake slices written into the pool.</summary>
    BrickBake,
    /// <summary>The viewport table copied to its device-local twin.</summary>
    UploadViewports,
    /// <summary>The dynamic transforms copied to their device-local twin.</summary>
    UploadDynamicTransforms,
    /// <summary>The frame instance grid copied to its device-local twin.</summary>
    UploadInstanceGrid,
    /// <summary>The sky pre-pass.</summary>
    Sky,
    /// <summary>The instance-cull pass that builds each tile's instance mask.</summary>
    Mask,
    /// <summary>The beam prepass that writes the tile planes and part bounds.</summary>
    Beam,
    /// <summary>The reduction to the indirect dispatch bounds.</summary>
    CullArgs,
    /// <summary>Primary traversal.</summary>
    Primary,
    /// <summary>Surface evaluation.</summary>
    Surface,
    /// <summary>Ambient occlusion.</summary>
    Ambient,
    /// <summary>Shading into the per-view sources.</summary>
    Views,
    /// <summary>The split-screen composite.</summary>
    Composite,
}
/// <summary>How a dispatch reaches a buffer. A dispatch that only reads a buffer binds it read-only, so a read is always
/// <see cref="Read"/>: Direct3D 12 would hold a buffer bound through a read-write (UAV) binding in
/// <c>UNORDERED_ACCESS</c> even when the kernel only reads it.</summary>
public enum SdfBufferAccess {
    /// <summary>Reads through a read-only binding.</summary>
    Read,
    /// <summary>Writes through a read-write binding without reading an earlier dispatch's values.</summary>
    Write,
    /// <summary>Reads and writes through a read-write binding.</summary>
    ReadWrite,
    /// <summary>Reads as indirect dispatch arguments.</summary>
    IndirectRead,
}
/// <summary>One buffer a pass touches and how.</summary>
/// <param name="Buffer">The buffer.</param>
/// <param name="Access">How the pass reaches it.</param>
public readonly record struct SdfBufferUse(SdfFrameBuffer Buffer, SdfBufferAccess Access);
/// <summary>A hazard between two dispatches on one buffer, recorded as one buffer transition.</summary>
/// <param name="Buffer">The buffer.</param>
/// <param name="Producer">The pass that last touched the buffer.</param>
/// <param name="Consumer">The pass about to touch it.</param>
/// <param name="Before">How the producer reached it.</param>
/// <param name="After">How the consumer reaches it.</param>
public readonly record struct SdfBufferEdge(SdfFrameBuffer Buffer, SdfFramePass Producer, SdfFramePass Consumer, SdfBufferAccess Before, SdfBufferAccess After) {
    /// <summary>The access scope the transition makes available.</summary>
    public GpuComputeAccess SourceAccess => SdfFrameBufferPlan.Declared(access: Before);
    /// <summary>The access scope the transition makes visible.</summary>
    public GpuComputeAccess DestinationAccess => SdfFrameBufferPlan.Declared(access: After);
    /// <summary>The stages the transition waits on.</summary>
    public GpuComputeStage SourceStage => SdfFrameBufferPlan.Stage(access: Before);
    /// <summary>The stages that wait on the transition.</summary>
    public GpuComputeStage DestinationStage => SdfFrameBufferPlan.Stage(access: After);
}
/// <summary>
/// Declares every buffer each SDF-engine dispatch touches and derives the buffer transitions a command list needs from
/// that declaration. A transition is owed between two uses of one buffer when either writes, or when they reach it
/// differently: Direct3D 12 needs a state change between read kinds, and Vulkan needs the matching buffer memory
/// barrier. The first use of a buffer in a command list owes nothing here: the buffer starts the list in
/// <c>COMMON</c> on Direct3D 12 and is promoted by that use, and the engine's top-of-frame barrier orders it after the
/// previous frame.
/// </summary>
public static class SdfFrameBufferPlan {
    /// <summary>The most buffers one pass touches.</summary>
    public const int MaxUsesPerPass = 9;

    private static readonly SdfBufferUse[] BrickWrites = [new(Access: SdfBufferAccess.Write, Buffer: SdfFrameBuffer.BrickPool)];
    private static readonly SdfBufferUse[] ViewportUpload = [new(Access: SdfBufferAccess.Write, Buffer: SdfFrameBuffer.Viewports)];
    private static readonly SdfBufferUse[] DynamicTransformUpload = [new(Access: SdfBufferAccess.Write, Buffer: SdfFrameBuffer.DynamicTransforms)];
    private static readonly SdfBufferUse[] InstanceGridUpload = [new(Access: SdfBufferAccess.Write, Buffer: SdfFrameBuffer.InstanceGrid)];
    private static readonly SdfBufferUse[] SkyUses = [
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.Viewports),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.DynamicTransforms),
    ];
    private static readonly SdfBufferUse[] MaskUses = [
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.Viewports),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.DynamicTransforms),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.InstanceGrid),
        new(Access: SdfBufferAccess.Write, Buffer: SdfFrameBuffer.InstanceMasks),
    ];
    private static readonly SdfBufferUse[] BeamUses = [
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.Viewports),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.DynamicTransforms),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.InstanceMasks),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.BrickPool),
        new(Access: SdfBufferAccess.Write, Buffer: SdfFrameBuffer.Tiles),
    ];
    private static readonly SdfBufferUse[] CullArgsUses = [
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.Tiles),
        new(Access: SdfBufferAccess.Write, Buffer: SdfFrameBuffer.ViewsArgs),
        new(Access: SdfBufferAccess.Write, Buffer: SdfFrameBuffer.CullBounds),
    ];
    private static readonly SdfBufferUse[] PrimaryUses = HitPassUses(primaryHits: SdfBufferAccess.Write);
    private static readonly SdfBufferUse[] ResolveUses = HitPassUses(primaryHits: SdfBufferAccess.ReadWrite);
    private static readonly SdfBufferUse[] ViewsUses = HitPassUses(primaryHits: SdfBufferAccess.Read);

    /// <summary>The buffers <paramref name="pass"/> touches and how, in the order its transitions are recorded.</summary>
    /// <param name="pass">The dispatch.</param>
    /// <returns>The pass's buffer uses; host-written tables and images are not listed, so the composite, which binds
    /// only images, has none.</returns>
    public static ReadOnlySpan<SdfBufferUse> Uses(SdfFramePass pass) => pass switch {
        SdfFramePass.BrickUpload or SdfFramePass.BrickBake => BrickWrites,
        SdfFramePass.UploadViewports => ViewportUpload,
        SdfFramePass.UploadDynamicTransforms => DynamicTransformUpload,
        SdfFramePass.UploadInstanceGrid => InstanceGridUpload,
        SdfFramePass.Sky => SkyUses,
        SdfFramePass.Mask => MaskUses,
        SdfFramePass.Beam => BeamUses,
        SdfFramePass.CullArgs => CullArgsUses,
        SdfFramePass.Primary => PrimaryUses,
        SdfFramePass.Surface or SdfFramePass.Ambient => ResolveUses,
        SdfFramePass.Views => ViewsUses,
        SdfFramePass.Composite => [],
        _ => throw new ArgumentOutOfRangeException(
            actualValue: pass,
            message: "Unknown SDF frame pass.",
            paramName: nameof(pass)
        ),
    };
    /// <summary>Whether an access writes the buffer.</summary>
    /// <param name="access">The access.</param>
    /// <returns><see langword="true"/> for <see cref="SdfBufferAccess.Write"/> and <see cref="SdfBufferAccess.ReadWrite"/>.</returns>
    public static bool Writes(SdfBufferAccess access) => (access is SdfBufferAccess.Write or SdfBufferAccess.ReadWrite);
    /// <summary>Whether a use after another use of the same buffer owes a transition.</summary>
    /// <param name="before">The earlier use.</param>
    /// <param name="after">The later use.</param>
    /// <returns><see langword="true"/> when either writes or the two reach the buffer differently.</returns>
    public static bool NeedsTransition(SdfBufferAccess before, SdfBufferAccess after) =>
        (Writes(access: before) || Writes(access: after) || (before != after));
    /// <summary>The neutral access an <see cref="SdfBufferAccess"/> declares to the recorder.</summary>
    /// <param name="access">The access.</param>
    /// <returns>The declared access.</returns>
    public static GpuComputeAccess Declared(SdfBufferAccess access) => access switch {
        SdfBufferAccess.Read => GpuComputeAccess.ShaderRead,
        SdfBufferAccess.Write => GpuComputeAccess.ShaderWrite,
        SdfBufferAccess.IndirectRead => GpuComputeAccess.IndirectCommandRead,
        _ => (GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite),
    };
    /// <summary>The stage an <see cref="SdfBufferAccess"/> happens in.</summary>
    /// <param name="access">The access.</param>
    /// <returns>The indirect-argument stage for <see cref="SdfBufferAccess.IndirectRead"/>, otherwise the compute shader stage.</returns>
    public static GpuComputeStage Stage(SdfBufferAccess access) => ((access == SdfBufferAccess.IndirectRead)
        ? GpuComputeStage.DrawIndirect
        : GpuComputeStage.ComputeShader
    );
    /// <summary>The transitions one command list recording <paramref name="passes"/> in order owes.</summary>
    /// <param name="passes">The recorded dispatches, in order.</param>
    /// <returns>Every owed transition, in recording order.</returns>
    public static SdfBufferEdge[] Edges(ReadOnlySpan<SdfFramePass> passes) {
        var hazards = new SdfFrameBufferHazards();
        var edges = new List<SdfBufferEdge>();
        Span<SdfBufferEdge> owed = stackalloc SdfBufferEdge[MaxUsesPerPass];

        foreach (var pass in passes) {
            var count = hazards.Enter(
                edges: owed,
                pass: pass
            );

            for (var index = 0; (index < count); index++) {
                edges.Add(item: owed[index]);
            }
        }

        return [.. edges];
    }

    private static SdfBufferUse[] HitPassUses(SdfBufferAccess primaryHits) => [
        new(Access: SdfBufferAccess.IndirectRead, Buffer: SdfFrameBuffer.ViewsArgs),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.CullBounds),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.Viewports),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.DynamicTransforms),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.InstanceGrid),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.InstanceMasks),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.BrickPool),
        new(Access: SdfBufferAccess.Read, Buffer: SdfFrameBuffer.Tiles),
        new(Access: primaryHits, Buffer: SdfFrameBuffer.PrimaryHits),
    ];
}
