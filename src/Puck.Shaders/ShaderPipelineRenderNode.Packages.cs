using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// A graph's package passes, recorded inside the node's own submission. The planner plans a package pass's barriers and
// layouts from its ports' accesses as it plans a shader pass's, so the node allocates the versions it writes like any
// pass's, records its planned barriers, and hands its recorder the command buffer with the versions bound to its ports
// resolved for the frame slot in their planned layouts, the pass's pass block and the frame's lease list. The recorder
// records no barrier. The package's factory builds its pipelines with the candidate's
// shader passes on the thread pool; its recorder is created from those objects when the graph installs, records its own
// work, binds its own descriptors from the graph's pool, and is disposed with the graph's passes, so a replacement, a
// device loss and disposal each release it with the objects it recorded against. The node ends and submits the command
// buffer.
public sealed partial class ShaderPipelineRenderNode {
    // The host's recorders, or an empty set for a node given none, which refuses every package pass.
    private readonly RenderGraphPackageRecorders m_packages;

    // The images of the instances this instance reads that its graph binds to no version, which the runtime binds before
    // each frame it produces and every package recording of that frame is handed; null when there are none.
    internal RenderGraphExternalReads? Reads { get; set; }

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

        return buffers[HistoryIndex(previous: false, resource: resource, slot: ((int)((m_frame - 1) % m_inFlight)))];
    }

    // What a package pass's factory builds and creates its recorder for, captured on the frame thread's request and read
    // on the pool.
    private static RenderGraphPackageRecorderContext PackageContextOf(ShaderPipelinePlannedPass planned, BuildRequest request, IReadOnlyDictionary<string, ShaderPipelineResource> specs, (uint Width, uint Height) extent) => new(
        Device: request.Device,
        Dispatch: planned.Package!.Dispatch,
        Height: extent.Height,
        HostsOnDirectX: request.DirectX,
        InFlightFrames: ((int)request.InFlight),
        Inputs: [.. planned.Inputs.Select(selector: input => specs[input.Name])],
        Instance: request.Instance,
        Outputs: [.. planned.Outputs.Select(selector: output => specs[output.Name])],
        Package: planned.Package!.Package,
        Parameters: planned.Parameters,
        Part: planned.Package.Part,
        Pass: planned.Name,
        Pipelines: request.Pipelines,
        Services: request.Gpu,
        Width: extent.Width
    );
    // Creates a built package pass's recorder once the graph's pool exists. The recorder owns the built objects from
    // here, so a factory that throws releases them itself; the ones it never received are released here.
    // Each output's owned image instances a package can draw into, one per instance and empty for an external or buffer
    // output. History the install carries binds the replaced graph's instances, which move into this graph only once
    // nothing else can fail, as a document pass's framebuffers do.
    private static IReadOnlyList<IGpuImage>[] PackageOutputImages(RuntimePass pass, IReadOnlyDictionary<string, RuntimeResource> map, IReadOnlyDictionary<int, CarriedHistory> carried) =>
        [.. pass.Outputs.Select(selector: output => {
            var resource = map[output.Name];

            if (
                resource.Spec.IsExternal ||
                (resource.Spec.Kind == ShaderPipelineResourceKind.Buffer)
            ) {
                return ((IReadOnlyList<IGpuImage>)[]);
            }

            return ((carried.TryGetValue(
                key: resource.Storage.Index,
                value: out var carry
            )
                ? carry.Old.Images
                : resource.Images) ?? []);
        })];
    private void InstallPackage(ShaderPipelinePlannedPass planned, RuntimePass runtime, PassObjects objects, nint descriptorPool, IReadOnlyList<IReadOnlyList<IGpuImage>> outputImages, IGpuComputePipeline? copyPipeline) {
        if (planned.Package is null) {
            return;
        }

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
            plan: m_pipeline!.Plan,
            planned: planned
        );
        runtime.PackageArguments = ((planned.Package.Dispatch is { Kind: ShaderPipelineDispatchKind.Indirect, Arguments: { } arguments })
            ? arguments
            : null);

        CreatePackageRegions(
            copyPipeline: copyPipeline,
            declared: (objects.Regions ?? []),
            runtime: runtime
        );

        var built = objects.PackageBuilt;

        // The recorder owns the build from here, whether or not it is created.
        objects.PackageBuilt = null;
        runtime.Package = objects.PackageFactory!.Create(
            built: built,
            context: objects.PackageContext!,
            groups: new RenderGraphPackageGroups(
                DescriptorPool: descriptorPool,
                FrameBlocks: [.. Enumerable.Range(
                    count: ((int)m_inFlight),
                    start: 0
                ).Select(selector: slot => m_frameRegion!.Buffer(slot: slot))],
                OutputImages: outputImages,
                PassBlocks: [.. Enumerable.Range(
                    count: ((int)m_inFlight),
                    start: 0
                ).Select(selector: slot => runtime.PassRegion!.Buffer(slot: slot))],
                Regions: (runtime.Regions ?? [])
            )
        );
    }

    // Why a pass that draws nothing cannot leave its outputs standing for its inputs, or null when it can: output i
    // stands for input i, so each output must be an owned image a surface carries, bound beside an input image of its format, that
    // no pass of the graph touches except later package passes reading this frame's instance, since a package pass
    // resolves each input through what it stands for (RecordPackage): publishing the input in its place changes nothing
    // any pass reads, and a chain of passes that draw nothing resolves to the first input it stands for. Every read of
    // an image is shader-readable and made visible to every shader stage, so a later reader finds the input as the pass
    // left it. The input must be this frame's: a previous frame's instance rests in the layout its own role left it in,
    // which this frame's plan does not state, so presenting it would start from a layout it is not in. And no later pass
    // may write the input's storage, as a version forwarding it does, or the published input, and every later read of
    // the output, would hold that pass's contents. The instance-chain check uses the same rule before any graph installs.
    internal static string? AliasRefusalOf(ShaderPipelinePlan plan, ShaderPipelinePlannedPass planned) {
        var pass = planned.Package!;

        for (var index = 0; (index < pass.Outputs.Count); index++) {
            var output = plan.Storages[plan.FindResource(name: pass.Outputs[index].Name)!.Storage];
            var why = ((index >= pass.Inputs.Count)
                ? "has no input at its position"
                : (pass.Inputs[index].PreviousFrame
                ? $"would stand for the previous frame of '{pass.Inputs[index].Name}', which rests in the layout that frame's role left it in"
                : (((output.Declaration.Kind != ShaderPipelineResourceKind.Image) ||
                   (plan.Storages[plan.FindResource(name: pass.Inputs[index].Name)!.Storage].Declaration.Kind != ShaderPipelineResourceKind.Image) ||
                   !string.Equals(
                       a: output.Declaration.Format,
                       b: plan.Storages[plan.FindResource(name: pass.Inputs[index].Name)!.Storage].Declaration.Format,
                       comparisonType: StringComparison.OrdinalIgnoreCase
                   ) ||
                   !Surface.IsImageFormat(format: ParseFormat(format: output.Declaration.Format)))
                    ? "is not an image a surface carries bound beside an input image of its format"
                    : ((output.History || plan.Passes.Any(predicate: other => (
                        (other.Index != planned.Index) &&
                        other.Accesses.Any(predicate: access => (
                            (access.Storage == output.Index) &&
                            !ResolvesThroughStandIn(
                                access: access,
                                reader: other,
                                standIn: planned
                            )
                        ))
                    )))
                        ? "is read by another pass or as history"
                        : ((LaterWriterOf(plan: plan, planned: planned, storage: plan.FindResource(name: pass.Inputs[index].Name)!.Storage) is { } writer)
                            ? $"would stand for '{pass.Inputs[index].Name}', which pass '{writer}' overwrites later in the frame"
                            : null)))));

            if (why is not null) {
                return $"Package pass '{planned.Name}' drew nothing, but its output '{pass.Outputs[index].Name}' {why}, so it cannot stand for its input.";
            }
        }

        return null;
    }

    // Whether an access to a stand-in's output reads whatever the output stands for: a later package pass reading this
    // frame's instance, which it resolves through the stand-in when it records.
    private static bool ResolvesThroughStandIn(ShaderPipelineAccess access, ShaderPipelinePlannedPass reader, ShaderPipelinePlannedPass standIn) => (
        (reader.Kind == ShaderPipelinePassKind.Package) &&
        (reader.Index > standIn.Index) &&
        !access.PreviousFrame &&
        !access.Use.Writes
    );
    // Names the pass that writes this frame's instance of a storage at a later position in execution order than the
    // planned pass, or returns null.
    private static string? LaterWriterOf(ShaderPipelinePlan plan, ShaderPipelinePlannedPass planned, int storage) => plan.Passes.FirstOrDefault(predicate: other => (
        (other.Index > planned.Index) &&
        other.Accesses.Any(predicate: access => (
            !access.PreviousFrame &&
            (access.Storage == storage) &&
            access.Use.Writes
        ))
    ))?.Name;
    // The position of the first input an output of the pass would stand for that is, or itself stands for, a host's
    // image bound in another layout than the node publishes in, or -1 when there is none. The node publishes every image
    // in its output layout, which is the layout its consumer's descriptor is written with, and hands a host's image back
    // in the host's own layout, so an output cannot stand for such an input: the recording must draw.
    private int HostInputInAnotherLayout(RuntimePass pass, int slot) {
        var count = Math.Min(
            val1: pass.Inputs.Length,
            val2: pass.Outputs.Length
        );

        for (var index = 0; (index < count); index++) {
            var (resource, name, _) = StandingOf(
                input: pass.Inputs[index],
                slot: slot
            );

            if (
                resource.Spec.IsExternal &&
                m_externalImages.TryGetValue(
                    key: name,
                    value: out var image
                ) &&
                (image.Layout != m_outputLayout)
            ) {
                return index;
            }
        }

        return -1;
    }
    // The position of the first input an output of the pass would stand for that is bound to one of the node's own images
    // (its own previous output, through a host's read of itself or a loop of instances), or -1 when there is none. The
    // node renders into its own images again a few frames later, so publishing one in its output's place would publish
    // pixels it is about to overwrite: the recording must draw.
    private int OwnImageInput(RuntimePass pass, int slot) {
        var count = Math.Min(
            val1: pass.Inputs.Length,
            val2: pass.Outputs.Length
        );

        for (var index = 0; (index < count); index++) {
            var (resource, name, _) = StandingOf(
                input: pass.Inputs[index],
                slot: slot
            );

            if (
                resource.Spec.IsExternal &&
                m_externalImages.TryGetValue(
                    key: name,
                    value: out var image
                ) &&
                OwnsImage(imageHandle: image.ImageHandle)
            ) {
                return index;
            }
        }

        return -1;
    }
    // Whether an image is one the node created: an image of its installed graph, or one held from a replaced graph.
    private bool OwnsImage(nint imageHandle) {
        foreach (var resource in m_resources) {
            if (resource?.Images is not { } images) {
                continue;
            }

            foreach (var image in images) {
                if (image?.ImageHandle == imageHandle) {
                    return true;
                }
            }
        }
        foreach (var held in m_held) {
            if (!held.Leased && (held.Handle == imageHandle)) {
                return true;
            }
        }

        return false;
    }
    // What a package pass's input reads this frame: the version bound to it, or, when that version is the output of an
    // earlier pass that drew nothing, the input that output stands for, which is never itself a stand-in, since each
    // stand-in resolved through the one before it.
    private (RuntimeResource Resource, string Name, int Instance) StandingOf(ResourceReference input, int slot) {
        var resource = m_resourceLookup[input.Name];

        return ((!input.PreviousFrame && (resource.Alias.Target is { } target))
            ? (target, resource.Alias.Name!, resource.Alias.Instance)
            : (resource, input.Name, InstanceIndex(
                previous: input.PreviousFrame,
                resource: resource,
                slot: slot
            )));
    }
    // Records what a package's recording did with its outputs: each output of a recording that drew nothing stands for
    // the input at its position until the pass records again, and a recording that drew clears that.
    private void ApplyOutcome(RuntimePass pass, int slot, RenderGraphPackageOutcome outcome) {
        if (outcome == RenderGraphPackageOutcome.DrewNothing) {
            if (pass.PackageAliasRefusal is { } refusal) {
                throw new InvalidOperationException(message: refusal);
            }
            if (OwnImageInput(pass: pass, slot: slot) is var own and >= 0) {
                var name = StandingOf(input: pass.Inputs[own], slot: slot).Name;

                throw new InvalidOperationException(message: $"Package pass '{pass.Name}' drew nothing, but its input '{name}' is bound to an image the instance itself owns, which a later frame of it overwrites, so its output cannot stand for it.");
            }
            if (HostInputInAnotherLayout(pass: pass, slot: slot) is var host and >= 0) {
                var name = StandingOf(input: pass.Inputs[host], slot: slot).Name;

                throw new InvalidOperationException(message: $"Package pass '{pass.Name}' drew nothing, but its input '{name}' is a host's image in {m_externalImages[name].Layout} layout and the instance publishes in {m_outputLayout}, so its output cannot stand for it.");
            }
        }

        for (var index = 0; (index < pass.Outputs.Length); index++) {
            var output = m_resourceLookup[pass.Outputs[index].Name];

            if (outcome != RenderGraphPackageOutcome.DrewNothing) {
                output.Alias = default;

                continue;
            }

            var (target, name, instance) = StandingOf(
                input: pass.Inputs[index],
                slot: slot
            );

            output.Alias = new PackageAlias(
                Instance: instance,
                Name: name,
                Target: target
            );
        }
    }
    // The image an output publishes in its place: its own, or the input it stands for this frame.
    private (RuntimeResource Resource, string Name, int Instance) PublicationOf(RuntimeResource selected, int slot) => ((selected.Alias.Target is { } target)
        ? (target, selected.Alias.Name!, selected.Alias.Instance)
        : (selected, selected.Spec.Name, HistoryIndex(
            previous: false,
            resource: selected,
            slot: slot
        )));

    // An output a package that drew nothing leaves standing for one of its inputs: the input's storage, name and the
    // instance the pass read.
    private readonly record struct PackageAlias(RuntimeResource? Target, string? Name, int Instance);

    private void RecordPackage(RuntimePass pass, int slot, in FrameContext context, nint command) {
        var handle = command;
        var recorder = m_gpu.Recorder;
        var inputs = pass.PackageInputs!;
        var outputs = pass.PackageOutputs!;
        var region = pass.PassRegion!;
        Span<byte> passBlock = stackalloc byte[region.ByteCount];

        // An input standing for another is handed over as the one it stands for, which the planned read of that one
        // left in the shader-readable layout every image read leaves.
        for (var index = 0; (index < inputs.Length); index++) {
            var (resource, name, instance) = StandingOf(
                input: pass.Inputs[index],
                slot: slot
            );

            inputs[index] = Resolve(
                index: instance,
                layout: pass.PackageInputLayouts![index],
                name: name,
                resource: resource
            );
        }
        for (var index = 0; (index < outputs.Length); index++) {
            var output = pass.Outputs[index];

            outputs[index] = Resolve(
                index: HistoryIndex(resource: m_resourceLookup[output.Name], slot: slot, previous: false),
                layout: pass.PackageOutputLayouts![index],
                name: output.Name,
                resource: m_resourceLookup[output.Name]
            );
        }

        FillPassBlock(
            block: passBlock,
            pass: pass
        );
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
            Arguments: ((pass.PackageArguments is { } arguments)
                ? ResolveBuffer(
                    index: slot,
                    name: arguments,
                    resource: m_resourceLookup[arguments]
                )
                : null),
            CommandBuffer: handle,
            Context: context,
            FrameWidth: m_width,
            FrameHeight: m_height,
            RenderWidth: m_renderWidth,
            RenderHeight: m_renderHeight,
            Height: pass.Height,
            Inputs: inputs,
            Leases: m_frameLeases,
            MayStandIn: ((pass.PackageAliasRefusal is null) && (HostInputInAnotherLayout(pass: pass, slot: slot) < 0) && (OwnImageInput(pass: pass, slot: slot) < 0)),
            Outputs: outputs,
            PassBlock: passBlock,
            Reads: Reads,
            Recorder: recorder,
            Slot: slot,
            UnreadFrames: UnreadFrames,
            Width: pass.Width,
            WorkCounters: (pass.CountsKernelWork
                ? m_passes[0].KernelCounters!.RowOf(
                    row: pass.Index,
                    slot: slot
                )
                : null)
        ));

        RecordPackageReadback(command: handle, pass: pass, recorder: recorder, slot: slot);

        // The recorder wrote its declared values beside the extent and config; the slot takes whichever words changed.
        _ = region.Write(
            bytes: passBlock,
            offset: 0
        );
        region.Flush(slot: slot);

        ApplyOutcome(
            outcome: outcome,
            pass: pass,
            slot: slot
        );
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
                : resource.Images?[InstanceAt(
                    index: index,
                    resource: resource
                )]),
            Version: name
        ));
    // A graph whose package pass nothing records is refused when it is swapped in, naming the pass and its package.
    private void ValidatePackages(ShaderPipelinePlan plan) {
        if (m_packages.TryFindUnserved(
            pass: out var unserved,
            plan: plan
        )) {
            throw new InvalidDataException(message: RenderGraphPackageRecorders.Unserved(
                instance: m_name,
                package: unserved.Package!.Package,
                pass: unserved.Name
            ));
        }
    }
}
