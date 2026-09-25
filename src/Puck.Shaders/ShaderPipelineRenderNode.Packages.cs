using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// A graph's package passes, recorded inside the node's own submission. The planner plans a package pass's barriers and
// layouts from its ports' accesses as it plans a shader pass's, so the node allocates the versions it writes like any
// pass's, records its planned barriers, and hands its recorder the command buffer with the versions bound to its ports
// resolved for the frame slot in their planned layouts, the pass's frame block and the frame's lease list. The recorder
// records no barrier. The package's factory builds its pipelines with the candidate's
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
        runtime.PackageAliasRefusal = AliasRefusalOf(
            pass: runtime,
            planned: planned
        );
        runtime.Package = objects.PackageFactory!.Create(
            built: built,
            context: objects.PackageContext!,
            descriptorPool: ((runtime.PackageSetBindings == 0)
                ? 0
                : descriptorPool)
        );
    }
    // Why a pass that draws nothing cannot leave its outputs standing for its inputs, or null when it can: output i
    // stands for input i, so each output must be an owned RGBA8 image no other pass of the graph touches, bound beside
    // an input image of its format, so publishing the input in its place changes nothing any pass reads. The input must
    // be this frame's: a previous frame's instance rests in the layout its own role left it in, which this frame's plan
    // does not state, so presenting it would start from a layout it is not in.
    private string? AliasRefusalOf(RuntimePass pass, ShaderPipelinePlannedPass planned) {
        for (var index = 0; (index < pass.Outputs.Length); index++) {
            var output = m_resourceLookup[pass.Outputs[index].Name];
            var why = ((index >= pass.Inputs.Length)
                ? "has no input at its position"
                : (pass.Inputs[index].PreviousFrame
                ? $"would stand for the previous frame of '{pass.Inputs[index].Name}', which rests in the layout that frame's role left it in"
                : (((output.Spec.Kind != ShaderPipelineResourceKind.Image) ||
                   (m_resourceLookup[pass.Inputs[index].Name].Spec.Kind != ShaderPipelineResourceKind.Image) ||
                   !string.Equals(
                       a: output.Spec.Format,
                       b: m_resourceLookup[pass.Inputs[index].Name].Spec.Format,
                       comparisonType: StringComparison.OrdinalIgnoreCase
                   ) ||
                   (ParseFormat(format: output.Spec.Format) is not (GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm)))
                    ? "is not an RGBA8 image bound beside an input image of its format"
                    : ((output.History || m_pipeline!.Plan.Passes.Any(predicate: other => (
                        (other.Index != planned.Index) &&
                        other.Accesses.Any(predicate: access => (access.Storage == output.Storage.Index))
                    )))
                        ? "is read by another pass or as history"
                        : null))));

            if (why is not null) {
                return $"Package pass '{pass.Name}' drew nothing, but its output '{pass.Outputs[index].Name}' {why}, so it cannot stand for its input.";
            }
        }

        return null;
    }
    // The position of the first input an output of the pass would stand for that is a host's image bound in another
    // layout than the node publishes in, or -1 when there is none. The node publishes every image in its output layout,
    // which is the layout its consumer's descriptor is written with, and hands a host's image back in the host's own
    // layout, so an output cannot stand for such an input: the recording must draw.
    private int HostInputInAnotherLayout(RuntimePass pass) {
        var count = Math.Min(
            val1: pass.Inputs.Length,
            val2: pass.Outputs.Length
        );

        for (var index = 0; (index < count); index++) {
            if (
                m_resourceLookup[pass.Inputs[index].Name].Spec.IsExternal &&
                m_externalImages.TryGetValue(
                    key: pass.Inputs[index].Name,
                    value: out var image
                ) &&
                (image.Layout != m_outputLayout)
            ) {
                return index;
            }
        }

        return -1;
    }
    // Records what a package's recording did with its outputs: each output of a recording that drew nothing stands for
    // the input at its position until the pass records again, and a recording that drew clears that.
    private void ApplyOutcome(RuntimePass pass, int slot, RenderGraphPackageOutcome outcome) {
        if (outcome == RenderGraphPackageOutcome.DrewNothing) {
            if (pass.PackageAliasRefusal is { } refusal) {
                throw new InvalidOperationException(message: refusal);
            }
            if (HostInputInAnotherLayout(pass: pass) is var host and >= 0) {
                var name = pass.Inputs[host].Name;

                throw new InvalidOperationException(message: $"Package pass '{pass.Name}' drew nothing, but its input '{name}' is a host's image in {m_externalImages[name].Layout} layout and the instance publishes in {m_outputLayout}, so its output cannot stand for it.");
            }
        }


        for (var index = 0; (index < pass.Outputs.Length); index++) {
            var output = m_resourceLookup[pass.Outputs[index].Name];

            if (outcome != RenderGraphPackageOutcome.DrewNothing) {
                output.Alias = default;

                continue;
            }

            var input = pass.Inputs[index];
            var target = m_resourceLookup[input.Name];

            output.Alias = new PackageAlias(
                Instance: InstanceIndex(
                    previous: input.PreviousFrame,
                    resource: target,
                    slot: slot
                ),
                Name: input.Name,
                Target: target
            );
        }
    }
    // The image an output publishes in its place: its own, or the input it stands for this frame.
    private (RuntimeResource Resource, string Name, int Instance) PublicationOf(RuntimeResource selected, int slot) => ((selected.Alias.Target is { } target)
        ? (target, selected.Alias.Name!, selected.Alias.Instance)
        : (selected, selected.Spec.Name, slot));

    // An output a package that drew nothing leaves standing for one of its inputs: the input's storage, name and the
    // instance the pass read.
    private readonly record struct PackageAlias(RuntimeResource? Target, string? Name, int Instance);

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
        var outcome = pass.Package!.Record(recording: new RenderGraphPackageRecording(
            CommandBuffer: handle,
            Context: context,
            FrameBlock: frameBlock,
            Height: pass.Height,
            Inputs: inputs,
            Leases: m_frameLeases,
            MayStandIn: ((pass.PackageAliasRefusal is null) && (HostInputInAnotherLayout(pass: pass) < 0)),
            Outputs: outputs,
            Recorder: recorder,
            Slot: slot,
            Width: pass.Width
        ));

        ApplyOutcome(
            outcome: outcome,
            pass: pass,
            slot: slot
        );
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
