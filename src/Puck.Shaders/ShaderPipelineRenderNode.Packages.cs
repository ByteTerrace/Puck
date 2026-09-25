using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// A graph's package passes, recorded inside the node's own submission. The planner orders a package pass in the compute
// shape it reaches resources by, so the node allocates the versions it writes like any pass's, records its planned
// barriers, and hands its recorder the command buffer with the versions bound to its ports resolved for the frame slot.
// The package records its own work and binds its own descriptors; the node ends and submits the command buffer. A
// recorder belongs to one installed graph: it is created when that graph installs and disposed with the graph's passes,
// so a replacement, a device loss and disposal each release it with the objects it recorded against.
public sealed partial class ShaderPipelineRenderNode {
    // The host's recorders, or an empty set for a node given none, which refuses every package pass.
    private readonly RenderGraphPackageRecorders m_packages;

    // The buffer the published default output holds in the frame slot the node most recently submitted: what a graph
    // instance's consumers bind when its output is a buffer. Null before the first submission, after a device loss, or
    // when the output is not a buffer the node allocates.
    internal IGpuBuffer? LatestOutputBuffer() {
        if (
            (m_frame == 0) ||
            (m_pipeline is null) ||
            !m_ready ||
            !m_resourceLookup.TryGetValue(
                key: m_pipeline.Plan.DefaultOutput,
                value: out var resource
            ) ||
            (resource.Buffers is not { } buffers)
        ) {
            return null;
        }

        return buffers[((int)((m_frame - 1) % m_inFlight))];
    }

    private void InstallPackage(ShaderPipelinePlannedPass planned, RuntimePass runtime) {
        if (planned.Package is not { } step) {
            return;
        }

        runtime.PackageInputs = new RenderGraphPackageResource[runtime.Inputs.Length];
        runtime.PackageOutputs = new RenderGraphPackageResource[runtime.Outputs.Length];
        runtime.Package = m_packages.Create(context: new RenderGraphPackageRecorderContext(
            Device: m_device,
            HostsOnDirectX: m_directX,
            InFlightFrames: ((int)m_inFlight),
            Instance: m_descriptor.Name,
            Package: step.Package,
            Pass: planned.Name
        ));
    }
    private void RecordPackage(RuntimePass pass, int slot, List<nint> commands) {
        var handle = pass.Pools![slot].CommandBufferHandle;
        var recorder = m_gpu.Recorder;
        var inputs = pass.PackageInputs!;
        var outputs = pass.PackageOutputs!;

        for (var index = 0; (index < inputs.Length); index++) {
            var input = pass.Inputs[index];
            var resource = m_resourceLookup[input.Name];

            inputs[index] = Resolve(
                index: HistoryIndex(
                    previous: input.PreviousFrame,
                    resource: resource,
                    slot: slot
                ),
                name: input.Name,
                resource: resource
            );
        }
        for (var index = 0; (index < outputs.Length); index++) {
            var output = pass.Outputs[index];

            outputs[index] = Resolve(
                index: slot,
                name: output.Name,
                resource: m_resourceLookup[output.Name]
            );
        }

        recorder.BeginCommandBuffer(commandBufferHandle: handle);
        InitializeResources(
            command: handle,
            recorder: recorder,
            slot: slot
        );
        RecordAccesses(
            command: handle,
            pass: pass,
            recorder: recorder,
            slot: slot
        );
        pass.Package!.Record(recording: new RenderGraphPackageRecording(
            CommandBuffer: handle,
            Height: pass.Height,
            Inputs: inputs,
            Outputs: outputs,
            Recorder: recorder,
            Slot: slot,
            Width: pass.Width
        ));
        recorder.EndCommandBuffer(commandBufferHandle: handle);
        commands.Add(item: handle);
    }
    private RenderGraphPackageResource Resolve(RuntimeResource resource, string name, int index) => ((resource.Spec.Kind == ShaderPipelineResourceKind.Buffer)
        ? new RenderGraphPackageResource(
            Buffer: ResolveBuffer(
                index: index,
                name: name,
                resource: resource
            ),
            Image: default,
            Kind: ShaderPipelineResourceKind.Buffer,
            Version: name
        )
        : new RenderGraphPackageResource(
            Buffer: null,
            Image: ResolveImage(
                index: index,
                name: name,
                resource: resource
            ),
            Kind: resource.Spec.Kind,
            Version: name
        ));
    // A graph whose package pass nothing records is refused when it is swapped in, naming the pass and its package.
    private void ValidatePackages(ShaderPipelinePlan plan) {
        if (m_packages.TryFindUnserved(
            pass: out var unserved,
            plan: plan
        )) {
            throw new InvalidDataException(message: RenderGraphPackageRecorders.Unserved(
                instance: m_descriptor.Name,
                package: unserved.Package!.Package,
                pass: unserved.Name
            ));
        }
    }
}
