using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

// Barrier recording. The plan gives every access its prior state and barrier (ShaderPipelinePlannedPass.Accesses), and
// the node records exactly those. The only states the plan cannot place are the ones the host's events leave an instance
// in: new or reset storage, a zero clear, a presentation, and history carried over from a replaced graph. Each such
// instance holds an override until its next access, which starts from the override and always records a barrier, so
// every later planned barrier, which waits only on planned stages, still orders the unplanned accesses through it.
public sealed partial class ShaderPipelineRenderNode {
    private const GpuStage ShaderStages = GpuStage.ComputeShader | GpuStage.FragmentShader;

    // The state a reset leaves every owned instance in: contents discarded, and any earlier access, including a
    // downstream reader of a published image, possibly still in flight.
    private static ShaderPipelineAccessState Discarded => ShaderPipelineAccessState.Host(layout: GpuImageLayout.Undefined);

    // The instance a reference reaches: this frame slot's, or the previous slot's for a previous-frame read. A host-owned
    // storage has one instance every slot shares.
    private static int InstanceIndex(RuntimeResource resource, int slot, bool previous) =>
        (resource.Spec.IsExternal
            ? 0
            : HistoryIndex(
                previous: previous,
                resource: resource,
                slot: slot
            ));
    // Clears the instances the plan says a pass reads before any pass writes them: every instance of a zero-initialized
    // storage no pass writes, and for history a pass rewrites, the instance the first frame reads as the previous one.
    // An instance already cleared or written holds contents, including history carried from a replaced graph, and is
    // not cleared.
    private void InitializeResources(nint command, IGpuRecorder recorder, int slot) {
        if (!m_initializationPending) {
            return;
        }
        foreach (var resource in m_resources) {
            var clear = resource.Storage.Clear;

            if (clear == ShaderPipelineClear.None) {
                continue;
            }
            for (var instance = 0; (instance < resource.Count); instance++) {
                if (
                    resource.Initialized[instance] ||
                    ((clear == ShaderPipelineClear.PreviousInstance) && (instance != HistoryIndex(
                        previous: true,
                        resource: resource,
                        slot: slot
                    )))
                ) {
                    continue;
                }
                RecordBarrier(
                    barrier: ShaderPipelineBarrier.Between(
                        kind: resource.Spec.Kind,
                        prior: resource.Prior(instance: instance),
                        use: ShaderPipelineAccessState.Cleared
                    ),
                    command: command,
                    instance: instance,
                    recorder: recorder,
                    resource: resource
                );
                if (resource.Spec.Kind == ShaderPipelineResourceKind.Buffer) {
                    var buffer = resource.Buffers![instance];

                    recorder.ClearStorageBuffer(
                        command,
                        buffer.BufferHandle,
                        buffer.SizeBytes
                    );
                } else {
                    recorder.ClearStorageImage(
                        command,
                        ResolveImage(
                            resource,
                            resource.Spec.Name,
                            instance
                        ).ImageHandle,
                        ParseFormat(format: resource.Spec.Format)
                    );
                }
                resource.SetOverride(
                    instance: instance,
                    state: ShaderPipelineAccessState.Cleared
                );
                resource.Initialized[instance] = true;
            }
        }
        m_initializationPending = false;
    }
    // Records the planned barrier of every access a pass makes, in the plan's order.
    private void RecordAccesses(RuntimePass pass, int slot, nint command, IGpuRecorder recorder) {
        var accesses = pass.Accesses;

        for (var index = 0; (index < accesses.Length); index++) {
            var access = accesses[index];
            var resource = m_resources[access.Storage];
            var instance = InstanceIndex(
                previous: access.PreviousFrame,
                resource: resource,
                slot: slot
            );
            var barrier = access.Barrier;

            if (access.PriorKind == ShaderPipelinePriorKind.Host) {
                // A host-owned instance starts every frame in the host's hands, whatever the last frame left.
                resource.HasOverride[instance] = false;
                barrier = ShaderPipelineBarrier.Between(
                    kind: resource.Spec.Kind,
                    prior: HostPrior(resource: resource),
                    use: access.Use
                );
            } else if (resource.HasOverride[instance]) {
                barrier = ShaderPipelineBarrier.Always(
                    kind: resource.Spec.Kind,
                    prior: resource.Override[instance],
                    use: access.Use
                );
                resource.HasOverride[instance] = false;
            }
            RecordBarrier(
                barrier: barrier,
                command: command,
                instance: instance,
                recorder: recorder,
                resource: resource
            );

            // A written instance holds contents from here on, so history carried into a replacing graph is never
            // cleared as if it were new.
            if (access.Use.Writes) {
                resource.Initialized[instance] = true;
            }
        }
    }
    private void RecordBarrier(ShaderPipelineBarrier barrier, RuntimeResource resource, int instance, nint command, IGpuRecorder recorder) {
        switch (barrier.Kind) {
            case ShaderPipelineBarrierKind.Image:
                recorder.TransitionImageLayout(
                    command,
                    ResolveImage(
                        resource,
                        resource.Spec.Name,
                        instance
                    ).ImageHandle,
                    barrier.OldLayout,
                    barrier.NewLayout,
                    barrier.SourceAccess,
                    barrier.DestinationAccess,
                    barrier.SourceStage,
                    barrier.DestinationStage
                );
                break;
            case ShaderPipelineBarrierKind.Memory:
                recorder.MemoryBarrier(
                    command,
                    barrier.SourceAccess,
                    barrier.DestinationAccess,
                    barrier.SourceStage,
                    barrier.DestinationStage
                );
                break;
            case ShaderPipelineBarrierKind.Buffer:
                recorder.TransitionBuffer(
                    command,
                    ResolveBuffer(
                        resource,
                        resource.Spec.Name,
                        instance
                    ).BufferHandle,
                    barrier.SourceAccess,
                    barrier.DestinationAccess,
                    barrier.SourceStage,
                    barrier.DestinationStage
                );
                break;
        }
    }
    // The state a host-owned storage arrives in: a bound image in its declared layout, ready to sample; a buffer after
    // anything its host did between frames.
    private ShaderPipelineAccessState HostPrior(RuntimeResource resource) =>
        ((resource.Spec.Kind == ShaderPipelineResourceKind.Image)
            ? ShaderPipelineAccessState.Handover(layout: m_externalImages[resource.Spec.Name].Layout)
            : ShaderPipelineAccessState.Host(layout: GpuImageLayout.Undefined));
    // The state this frame's instance is in once the frame's passes have run: an override, or the plan's frame end.
    private ShaderPipelineAccessState FrameEnd(RuntimeResource resource, int instance) =>
        (resource.HasOverride[instance]
            ? resource.Override[instance]
            : ((resource.Storage.FrameEndKind == ShaderPipelinePriorKind.Host)
                ? HostPrior(resource: resource)
                : resource.Storage.FrameEnd));
    // The barrier before a presentation reads this frame's instance in the given state, and the override it leaves.
    private ShaderPipelineBarrier Present(RuntimeResource resource, int instance, ShaderPipelineAccessState use) {
        var prior = FrameEnd(
            instance: instance,
            resource: resource
        );
        var barrier = (resource.HasOverride[instance]
            ? ShaderPipelineBarrier.Always(
                kind: resource.Spec.Kind,
                prior: prior,
                use: use
            )
            : ShaderPipelineBarrier.Between(
                kind: resource.Spec.Kind,
                prior: prior,
                use: use
            ));

        resource.SetOverride(
            instance: instance,
            state: ((barrier.Kind == ShaderPipelineBarrierKind.None)
                ? prior.Then(use: use)
                : use)
        );
        return barrier;
    }
    // Records publication of the selected output and hands every host-owned image back in its host's layout.
    private void RecordPresentation(RuntimeResource selected, int slot, nint command, IGpuRecorder recorder) {
        if (!NeedsPreview(spec: selected.Spec)) {
            RecordBarrier(
                barrier: Present(
                    instance: slot,
                    resource: selected,
                    use: new ShaderPipelineAccessState(
                        Access: GpuAccess.ShaderRead,
                        Layout: m_outputLayout,
                        Stage: ShaderStages
                    )
                ),
                command: command,
                instance: slot,
                recorder: recorder,
                resource: selected
            );
        }
        foreach (var resource in m_resources) {
            if (
                !resource.Spec.IsExternal ||
                (resource.Spec.Kind != ShaderPipelineResourceKind.Image)
            ) {
                continue;
            }
            var host = ShaderPipelineAccessState.Host(layout: m_externalImages[resource.Spec.Name].Layout);

            RecordBarrier(
                barrier: Present(
                    instance: 0,
                    resource: resource,
                    use: host
                ),
                command: command,
                instance: 0,
                recorder: recorder,
                resource: resource
            );
        }
    }
    // The barrier the float preview records before sampling this frame's instance of the selected output.
    private ShaderPipelineBarrier PreviewSource(RuntimeResource selected, int slot) =>
        Present(
            instance: InstanceIndex(
                previous: false,
                resource: selected,
                slot: slot
            ),
            resource: selected,
            use: new ShaderPipelineAccessState(
                Access: GpuAccess.ShaderRead,
                Layout: GpuImageLayout.ShaderReadOnly,
                Stage: GpuStage.FragmentShader
            )
        );
    // A reset discards every owned instance's contents and re-arms zero initialization.
    private void DiscardInstances() {
        foreach (var resource in m_resources) {
            if (resource.Spec.IsExternal) {
                continue;
            }
            for (var instance = 0; (instance < resource.Count); instance++) {
                resource.SetOverride(
                    instance: instance,
                    state: Discarded
                );
                resource.Initialized[instance] = false;
            }
        }
        m_initializationPending = true;
    }
    // The state each instance of carried history is in under the graph it came from: its override, or, with none, the
    // old plan's frame end for the instance the last frame wrote and the old plan's steady start for the rest.
    private void CarryStates(RuntimeResource old, RuntimeResource current, ShaderPipelinePlan oldPlan) {
        var written = ((int)(((m_frame + ((ulong)old.Count)) - 1UL) % ((ulong)old.Count)));
        var start = old.Storage.FrameEnd;

        foreach (var pass in oldPlan.Passes) {
            foreach (var access in pass.Accesses) {
                if (
                    (access.Storage == old.Storage.Index) &&
                    !access.PreviousFrame &&
                    (access.PriorKind == ShaderPipelinePriorKind.CrossFrame)
                ) {
                    start = access.Prior;
                }
            }
        }
        for (var instance = 0; (instance < current.Count); instance++) {
            current.SetOverride(
                instance: instance,
                state: (old.HasOverride[instance]
                    ? old.Override[instance]
                    : ((instance == written)
                        ? old.Storage.FrameEnd
                        : start))
            );
        }
    }
}
