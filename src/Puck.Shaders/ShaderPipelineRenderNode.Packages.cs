using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// A graph's package passes, recorded inside the node's own submission. The planner orders a package pass in the compute
// shape it reaches resources by, so the node allocates the versions it writes like any pass's, records its planned
// barriers, and hands its recorder the command buffer with the versions bound to its ports resolved for the frame slot,
// the pass's frame block and the frame's lease list. The package's factory builds its pipelines with the candidate's
// shader passes on the thread pool; its recorder is created from those objects when the graph installs, records its own
// work, binds its own descriptors from the graph's pool, and is disposed with the graph's passes, so a replacement, a
// device loss and disposal each release it with the objects it recorded against. The node ends and submits the command
// buffer.
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

    // What a package pass's factory builds and creates its recorder for, captured on the frame thread's request and read
    // on the pool.
    private static RenderGraphPackageRecorderContext PackageContextOf(ShaderPipelinePlannedPass planned, BuildRequest request, IReadOnlyDictionary<string, ShaderPipelineResource> specs, (uint Width, uint Height) extent) => new(
        Device: request.Device,
        Height: extent.Height,
        HostsOnDirectX: request.DirectX,
        InFlightFrames: ((int)request.InFlight),
        Inputs: [.. planned.Inputs.Select(selector: input => specs[input.Name])],
        Instance: request.Instance,
        Outputs: [.. planned.Outputs.Select(selector: output => specs[output.Name])],
        Package: planned.Package!.Package,
        Parameters: planned.Parameters,
        Pass: planned.Name,
        Services: request.Gpu,
        Width: extent.Width
    );
    // Creates a built package pass's recorder once the graph's pool exists. The recorder owns the built objects from
    // here, so a factory that throws releases them itself; the ones it never received are released here.
    private void InstallPackage(ShaderPipelinePlannedPass planned, RuntimePass runtime, PassObjects objects, nint descriptorPool) {
        if (planned.Package is null) {
            return;
        }

        var built = objects.PackageBuilt;

        objects.PackageBuilt = null;
        runtime.PackageInputs = new RenderGraphPackageResource[runtime.Inputs.Length];
        runtime.PackageOutputs = new RenderGraphPackageResource[runtime.Outputs.Length];
        runtime.PackageInputLayouts = LayoutsOf(
            accesses: runtime.Accesses,
            ports: runtime.Inputs
        );
        runtime.PackageOutputLayouts = LayoutsOf(
            accesses: runtime.Accesses,
            ports: runtime.Outputs
        );
        runtime.Package = objects.PackageFactory!.Create(
            built: built,
            context: objects.PackageContext!,
            descriptorPool: ((runtime.PackageSetBindings == 0)
                ? 0
                : descriptorPool)
        );
    }
    private void RecordPackage(RuntimePass pass, int slot, in FrameContext context, List<nint> commands) {
        var handle = pass.Pools![slot].CommandBufferHandle;
        var recorder = m_gpu.Recorder;
        var inputs = pass.PackageInputs!;
        var outputs = pass.PackageOutputs!;
        Span<byte> frameBlock = stackalloc byte[((int)pass.ParametersLayout.SizeBytes)];

        for (var index = 0; (index < inputs.Length); index++) {
            var input = pass.Inputs[index];
            var resource = m_resourceLookup[input.Name];

            inputs[index] = Resolve(
                index: HistoryIndex(
                    previous: input.PreviousFrame,
                    resource: resource,
                    slot: slot
                ),
                layout: pass.PackageInputLayouts![index],
                name: input.Name,
                resource: resource
            );
        }
        for (var index = 0; (index < outputs.Length); index++) {
            var output = pass.Outputs[index];

            outputs[index] = Resolve(
                index: slot,
                layout: pass.PackageOutputLayouts![index],
                name: output.Name,
                resource: m_resourceLookup[output.Name]
            );
        }

        WriteFrameBlock(
            bytes: frameBlock,
            context: in context,
            height: pass.Height,
            pass: pass,
            width: pass.Width
        );
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
            FrameBlock: frameBlock,
            Height: pass.Height,
            Inputs: inputs,
            Leases: m_frameLeases,
            Outputs: outputs,
            Recorder: recorder,
            Slot: slot,
            Width: pass.Width
        ));
        recorder.EndCommandBuffer(commandBufferHandle: handle);
        commands.Add(item: handle);
    }
    // The layout each port's image is in when the recorder records: the one its planned access's barrier left it in.
    private static GpuImageLayout[] LayoutsOf(ShaderPipelineAccess[] accesses, ResourceReference[] ports) {
        var layouts = new GpuImageLayout[ports.Length];

        for (var index = 0; (index < ports.Length); index++) {
            var port = ports[index];

            foreach (var access in accesses) {
                if (
                    string.Equals(
                        a: access.Version,
                        b: port.Name,
                        comparisonType: StringComparison.Ordinal
                    ) &&
                    (access.PreviousFrame == port.PreviousFrame)
                ) {
                    layouts[index] = access.Use.Layout;
                }
            }
        }

        return layouts;
    }
    private RenderGraphPackageResource Resolve(RuntimeResource resource, string name, int index, GpuImageLayout layout) => ((resource.Spec.Kind == ShaderPipelineResourceKind.Buffer)
        ? new RenderGraphPackageResource(
            Buffer: ResolveBuffer(
                index: index,
                name: name,
                resource: resource
            ),
            Image: default,
            Kind: ShaderPipelineResourceKind.Buffer,
            Owned: null,
            Version: name
        )
        : new RenderGraphPackageResource(
            Buffer: null,
            Image: ResolveImage(
                index: index,
                name: name,
                resource: resource
            ) with {
                Layout = layout,
            },
            Kind: resource.Spec.Kind,
            Owned: (resource.Spec.IsExternal
                ? null
                : resource.Images?[index]),
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
