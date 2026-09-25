using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>The state one use leaves a storage instance in: its layout (images only) and the accesses and stages a later
/// barrier must wait on. After a write it is that write; after reads that followed a barrier it is the union of those
/// reads, because a later write must wait for every one of them.</summary>
/// <param name="Layout">The image layout; <see cref="GpuImageLayout.Undefined"/> for a buffer, or for an instance whose
/// contents are discarded.</param>
/// <param name="Access">The accesses made since the last barrier.</param>
/// <param name="Stage">The stages those accesses ran in.</param>
public readonly record struct ShaderPipelineAccessState(GpuImageLayout Layout, GpuComputeAccess Access, GpuComputeStage Stage) {
    private const GpuComputeAccess WriteAccesses = GpuComputeAccess.ShaderWrite | GpuComputeAccess.TransferWrite | GpuComputeAccess.ColorAttachmentWrite | GpuComputeAccess.DepthAttachmentWrite;

    /// <summary>Gets the state of an instance nothing has touched, whose contents are undefined.</summary>
    public static ShaderPipelineAccessState Fresh { get; } = new(
        Access: GpuComputeAccess.None,
        Layout: GpuImageLayout.Undefined,
        Stage: GpuComputeStage.TopOfPipe
    );
    /// <summary>Gets the state a zero clear leaves an instance in.</summary>
    public static ShaderPipelineAccessState Cleared { get; } = new(
        Access: GpuComputeAccess.TransferWrite,
        Layout: GpuImageLayout.General,
        Stage: GpuComputeStage.Transfer
    );

    /// <summary>Gets whether the state includes a write, so any later use must wait on it.</summary>
    public bool Writes => ((Access & WriteAccesses) != 0);

    /// <summary>Returns the state a host-owned image arrives in each frame: sampleable in the host's layout, with the
    /// host having ordered its own writes before the handover.</summary>
    /// <param name="layout">The layout the host binds the image in.</param>
    /// <returns>The handover state.</returns>
    public static ShaderPipelineAccessState Handover(GpuImageLayout layout) => new(
        Access: GpuComputeAccess.ShaderRead,
        Layout: layout,
        Stage: GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader
    );
    /// <summary>Returns the state the host may use an instance in between frames: its own layout, with any access in any
    /// stage. It is where a host-owned image is handed back at the end of a frame, and where a host-owned buffer starts
    /// each frame.</summary>
    /// <param name="layout">The host's layout; <see cref="GpuImageLayout.Undefined"/> for a buffer.</param>
    /// <returns>The host state.</returns>
    public static ShaderPipelineAccessState Host(GpuImageLayout layout) => new(
        Access: GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite | GpuComputeAccess.TransferWrite | GpuComputeAccess.ColorAttachmentWrite | GpuComputeAccess.DepthAttachmentWrite,
        Layout: layout,
        Stage: GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader | GpuComputeStage.Transfer | GpuComputeStage.ColorAttachmentOutput | GpuComputeStage.FragmentTests
    );
    /// <summary>Returns whether <paramref name="use"/> reads in a way this state's reads do not include, such as an
    /// indirect-argument read after shader reads. The two are different read states: Direct3D 12 transitions between
    /// them, so the planner records a barrier. A fresh instance, having no reads, moves into any read state
    /// freely.</summary>
    /// <param name="use">The state the next use needs.</param>
    /// <returns><see langword="true"/> when the use adds an access this state lacks.</returns>
    public bool ChangesReadState(ShaderPipelineAccessState use) =>
        ((Access != GpuComputeAccess.None) && ((use.Access & ~Access) != 0));
    /// <summary>Returns the state after <paramref name="use"/> follows this one: the use alone when a barrier separates
    /// them, or the union of reads when a read follows reads of the same kind in the same layout with no
    /// barrier.</summary>
    /// <param name="use">The state the next use leaves.</param>
    /// <returns>The resulting state.</returns>
    public ShaderPipelineAccessState Then(ShaderPipelineAccessState use) =>
        ((Writes || use.Writes || (Layout != use.Layout) || ChangesReadState(use: use))
            ? use
            : new ShaderPipelineAccessState(
                Access: Access | use.Access,
                Layout: Layout,
                Stage: Stage | use.Stage
            ));
}
/// <summary>What one barrier records.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineBarrierKind>))]
public enum ShaderPipelineBarrierKind : byte {
    /// <summary>Nothing is recorded: a read follows reads in the same layout.</summary>
    None = 0,
    /// <summary>An image layout transition.</summary>
    Image = 1,
    /// <summary>A memory barrier over an image whose layout does not change.</summary>
    Memory = 2,
    /// <summary>A buffer barrier.</summary>
    Buffer = 3,
}
/// <summary>The one barrier between a storage instance's prior state and its next use, exactly as a recorder takes
/// it.</summary>
/// <param name="Kind">What is recorded.</param>
/// <param name="OldLayout">The image layout the instance is in.</param>
/// <param name="NewLayout">The image layout the use needs.</param>
/// <param name="SourceAccess">The prior accesses waited on.</param>
/// <param name="DestinationAccess">The accesses made visible.</param>
/// <param name="SourceStage">The prior stages waited on.</param>
/// <param name="DestinationStage">The stages that wait.</param>
public readonly record struct ShaderPipelineBarrier(
    ShaderPipelineBarrierKind Kind,
    GpuImageLayout OldLayout,
    GpuImageLayout NewLayout,
    GpuComputeAccess SourceAccess,
    GpuComputeAccess DestinationAccess,
    GpuComputeStage SourceStage,
    GpuComputeStage DestinationStage
) {
    // A sampled read makes the prior write visible to every shader stage, so later readers of the same contents in the
    // same layout need no barrier of their own.
    private static GpuComputeStage DestinationStages(ShaderPipelineAccessState use) =>
        (((use.Layout == GpuImageLayout.ShaderReadOnly) && (use.Access == GpuComputeAccess.ShaderRead))
            ? GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader
            : use.Stage);
    private static ShaderPipelineBarrier Record(ShaderPipelineBarrierKind kind, ShaderPipelineAccessState prior, ShaderPipelineAccessState use) =>
        new(
            DestinationAccess: use.Access,
            DestinationStage: DestinationStages(use: use),
            Kind: kind,
            NewLayout: use.Layout,
            OldLayout: prior.Layout,
            SourceAccess: prior.Access,
            SourceStage: ((prior.Stage == GpuComputeStage.None)
                ? GpuComputeStage.TopOfPipe
                : prior.Stage)
        );

    /// <summary>Returns the barrier a use needs after a prior state: a transition when the image layout changes, a
    /// memory or buffer barrier when either side writes or the use changes the read state
    /// (<see cref="ShaderPipelineAccessState.ChangesReadState"/>), and nothing when a read follows reads of the same
    /// kind in the same layout.</summary>
    /// <param name="prior">The state the instance is in.</param>
    /// <param name="use">The state the use needs.</param>
    /// <param name="kind">The resource kind; a buffer has no layout.</param>
    /// <returns>The barrier.</returns>
    public static ShaderPipelineBarrier Between(ShaderPipelineAccessState prior, ShaderPipelineAccessState use, ShaderPipelineResourceKind kind) {
        var hazard = (prior.Writes || use.Writes || prior.ChangesReadState(use: use));

        if (kind == ShaderPipelineResourceKind.Buffer) {
            return Record(
                kind: (hazard
                    ? ShaderPipelineBarrierKind.Buffer
                    : ShaderPipelineBarrierKind.None),
                prior: prior,
                use: use
            );
        }

        return Record(
            kind: ((prior.Layout != use.Layout)
                ? ShaderPipelineBarrierKind.Image
                : (hazard
                    ? ShaderPipelineBarrierKind.Memory
                    : ShaderPipelineBarrierKind.None)),
            prior: prior,
            use: use
        );
    }
    /// <summary>Returns <see cref="Between"/>, recording a memory or buffer barrier where it would record nothing. A
    /// use that starts from a state the plan did not produce records one, so every later planned barrier, which waits
    /// only on planned stages, still orders the unplanned accesses through it.</summary>
    /// <param name="prior">The unplanned state the instance is in.</param>
    /// <param name="use">The state the use needs.</param>
    /// <param name="kind">The resource kind.</param>
    /// <returns>The barrier, never <see cref="ShaderPipelineBarrierKind.None"/>.</returns>
    public static ShaderPipelineBarrier Always(ShaderPipelineAccessState prior, ShaderPipelineAccessState use, ShaderPipelineResourceKind kind) {
        var barrier = Between(
            kind: kind,
            prior: prior,
            use: use
        );

        return ((barrier.Kind != ShaderPipelineBarrierKind.None)
            ? barrier
            : (barrier with {
                Kind = ((kind == ShaderPipelineResourceKind.Buffer)
                    ? ShaderPipelineBarrierKind.Buffer
                    : ShaderPipelineBarrierKind.Memory),
            }));
    }
}
/// <summary>Where an access's prior state comes from.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelinePriorKind>))]
public enum ShaderPipelinePriorKind : byte {
    /// <summary>An earlier pass of the same frame.</summary>
    Pass = 1,
    /// <summary>The instance's uses in earlier frames: the first use of an instance in a frame.</summary>
    CrossFrame = 2,
    /// <summary>The host, which owns the instance between frames; its layout is known only once the host binds it.</summary>
    Host = 3,
}
/// <summary>One access a pass makes to a storage instance, with the state it starts from and the barrier between
/// them. A pass's accesses are listed in recording order: an indirect dispatch's arguments, then its inputs, then its
/// outputs.</summary>
/// <param name="Storage">The index of the storage in <see cref="ShaderPipelinePlan.Storages"/>.</param>
/// <param name="Version">The version the pass names.</param>
/// <param name="PreviousFrame">Whether the access reaches the instance the previous frame wrote.</param>
/// <param name="PriorKind">Where <paramref name="Prior"/> comes from.</param>
/// <param name="PriorPass">The pass index of the prior use within this frame, or -1 for a cross-frame or host prior.</param>
/// <param name="Prior">The state the instance is in before the access, in the steady state.</param>
/// <param name="Use">The state the access needs, which is also the state it leaves: a render pass leaves each attachment
/// in its attachment layout.</param>
/// <param name="Barrier">The barrier recorded before the access in the steady state.</param>
public sealed record ShaderPipelineAccess(
    int Storage,
    string Version,
    bool PreviousFrame,
    ShaderPipelinePriorKind PriorKind,
    int PriorPass,
    ShaderPipelineAccessState Prior,
    ShaderPipelineAccessState Use,
    ShaderPipelineBarrier Barrier
);
/// <summary>Which instances of a storage the node clears when a graph installs or resets.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineClear>))]
public enum ShaderPipelineClear : byte {
    /// <summary>None: the storage is not zero-initialized, or a pass writes every instance before anything reads it.</summary>
    None = 0,
    /// <summary>The instance the first frame reads as the previous frame's, for history a pass rewrites every frame.</summary>
    PreviousInstance = 1,
    /// <summary>Every instance, for a zero-initialized storage no pass writes.</summary>
    EveryInstance = 2,
}
/// <summary>How a version's contents begin when its writer runs.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineContents>))]
public enum ShaderPipelineContents : byte {
    /// <summary>The writer starts from discarded contents.</summary>
    Discarded = 1,
    /// <summary>The writer continues its predecessor's contents.</summary>
    Preserved = 2,
    /// <summary>No pass writes the version; the node zero-initializes it.</summary>
    Initialized = 3,
    /// <summary>No pass writes the version; the host supplies it.</summary>
    External = 4,
}
