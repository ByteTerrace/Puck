using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Hosting;

namespace Puck.Shaders;

// Uploaded sources. An external instance whose package registers an upload (RenderGraphPackageRecorders.RegisterSource)
// renders through a node like a graph instance, running a graph the runtime makes from the upload's descriptor: one
// external buffer, the region, bound to the node as a host buffer port, and one package pass, the conversion
// ImageSourceConversion.PassOf names, writing the image every consumer reads. So a source converts once a frame at most
// however many instances read it, and its consumers bind its output as they bind any graph instance's. The runtime
// declares the source's cadence and extent to the scheduler from the descriptor, beside whatever the host declares.
public sealed partial class RenderGraphRuntime {
    // Each instance's upload, or null for an instance that renders a graph the host gave or through an external producer.
    private SourceGraph?[] m_sources = [];

    // What the scheduler reads for the frame being scheduled: the host's source states, then each upload's own.
    private readonly List<RenderGraphSourceState> m_sourceStates = [];

    /// <summary>The name of the one pass of an uploaded source's graph, which its node's counted work reports.</summary>
    public const string SourceConversionPass = "convert";

    /// <summary>Returns the graph an instance renders, for inspection: the host's, or an uploaded source's conversion
    /// graph.</summary>
    /// <param name="instance">The instance's index in <see cref="Instances"/>.</param>
    /// <returns>The graph, or <see langword="null"/> for an external producer's instance, a graph instance whose graph is
    /// not installed, and an uploaded source no conversion reads.</returns>
    public RenderGraphRuntimeGraph? Graph(int instance) => m_graphs[instance];
    /// <summary>Returns the upload an uploaded source instance renders from, for inspection.</summary>
    /// <param name="instance">The instance's index in <see cref="Instances"/>.</param>
    /// <returns>The upload, or <see langword="null"/> for an instance that is no uploaded source.</returns>
    public IRenderGraphSourceUpload? Source(int instance) => m_sources[instance]?.Upload;

    // Makes the one-pass graph an upload's descriptor names, or returns why none can be made.
    private static RenderGraphRuntimeGraph? GraphOf(ImageSourceDescriptor descriptor, out ImageSourceUploadHeader header, out string? fault) {
        header = default;

        string pass;

        try {
            pass = ImageSourceConversion.PassOf(
                color: descriptor.Color,
                format: descriptor.Format
            );
            header = ImageSourceUploadLayout.HeaderOf(
                color: descriptor.Color,
                format: descriptor.Format,
                height: descriptor.Height,
                width: descriptor.Width
            );
        } catch (ArgumentOutOfRangeException exception) {
            fault = $"image producer '{descriptor.Producer}' declares {descriptor.Format} at {descriptor.Width}x{descriptor.Height}, which no conversion reads: {exception.Message}";

            return null;
        }

        var definition = new RenderGraphDefinition(
            Name: pass,
            Outputs: [RenderGraphPackageCatalog.SourceImage],
            Packages: [
                new RenderGraphPackagePass(
                    Inputs: [new ResourceReference(Name: RenderGraphPackageCatalog.SourceRegion)],
                    Name: SourceConversionPass,
                    Outputs: [new ResourceReference(Name: RenderGraphPackageCatalog.SourceImage)],
                    Package: pass
                ),
            ],
            Resources: [
                new ShaderPipelineResource(
                    Initialization: ShaderPipelineInitialization.Host,
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: RenderGraphPackageCatalog.SourceRegion,
                    SizeBytes: ((ulong)ImageSourceUploadLayout.ByteCount(header: in header))
                ),
                new ShaderPipelineResource(
                    Dimensions: ShaderPipelineDimensions.Absolute(
                        height: descriptor.Height,
                        width: descriptor.Width
                    ),
                    Format: RenderGraphPackageCatalog.SourceFormatOf(package: pass).ToString(),
                    Name: RenderGraphPackageCatalog.SourceImage
                ),
            ],
            Schema: RenderGraphSchemas.Graph
        );

        if (!new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).TryCompile(
            definition: definition,
            diagnostics: out var diagnostics,
            plan: out var plan
        )) {
            fault = $"image producer '{descriptor.Producer}''s conversion graph was refused: {string.Join(separator: "; ", values: diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}";

            return null;
        }

        fault = null;

        return new RenderGraphRuntimeGraph(
            Inputs: [],
            Pipeline: new CompiledShaderPipeline(
                plan: plan.Pipeline,
                shaders: new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal)
            )
        );
    }

    /// <summary>Creates a converter of CPU pixels a producer holds outside the set, on the runtime's device, packages and
    /// pipeline cache: a node of its own running the one-pass graph the descriptor names, the conversion an uploaded
    /// source instance renders through. The caller owns the converter and disposes it before the device goes.</summary>
    /// <param name="name">The converter's name, which names its node and its GPU objects.</param>
    /// <param name="descriptor">What the pixels are: their format, color encoding and extent.</param>
    /// <returns>The converter, whose <see cref="RenderGraphSourceConverter.Fault"/> names a descriptor no conversion
    /// reads.</returns>
    /// <exception cref="ObjectDisposedException">The runtime is disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="descriptor"/> is
    /// <see langword="null"/>.</exception>
    public RenderGraphSourceConverter CreateConverter(string name, ImageSourceDescriptor descriptor) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        ArgumentNullException.ThrowIfNull(argument: name);
        ArgumentNullException.ThrowIfNull(argument: descriptor);

        var graph = GraphOf(
            descriptor: descriptor,
            fault: out var fault,
            header: out var header
        );

        return new RenderGraphSourceConverter(
            fault: fault,
            graph: graph,
            header: header,
            node: CreateNode(
                deviceContext: m_device,
                hostsOnDirectX: m_hostsOnDirectX,
                inFlightFrames: m_inFlightFrames,
                name: name,
                packages: m_packages,
                pipelines: m_pipelines
            )
        );
    }

    // Opens an uploaded source instance's upload and makes its graph and its node, which renders nothing when the upload
    // refused or no conversion reads what it declares. The node takes the graph once every graph of the set binds
    // (Install).
    private static (SourceGraph Source, ShaderPipelineRenderNode Node) CreateSource(RenderGraphInstance instance, RenderGraphPackageRecorders packages, GpuPassPipelineCache pipelines, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint inFlightFrames) {
        var upload = packages.CreateSource(context: new RenderGraphExternalProducerContext(
            Device: deviceContext,
            HostsOnDirectX: hostsOnDirectX,
            Instance: instance.Name,
            Package: instance.ExternalPackage!,
            Settings: instance.Settings
        ));
        ShaderPipelineRenderNode? node = null;

        try {
            var header = default(ImageSourceUploadHeader);
            var fault = upload.Fault;
            var graph = (((fault is null) && (upload.Descriptor is { } descriptor))
                ? GraphOf(
                    descriptor: descriptor,
                    fault: out fault,
                    header: out header
                )
                : null);

            node = CreateNode(
                deviceContext: deviceContext,
                hostsOnDirectX: hostsOnDirectX,
                inFlightFrames: inFlightFrames,
                name: instance.Name,
                packages: packages,
                pipelines: pipelines
            );

            return (new SourceGraph(
                fault: (fault ?? ((upload.Descriptor is null) ? $"image producer of '{instance.ExternalPackage}' opened no image" : null)),
                graph: graph,
                header: header,
                name: instance.Name,
                upload: upload
            ), node);
        } catch {
            node?.Dispose();
            upload.Dispose();

            throw;
        }
    }
    // A capture armed on an uploaded source whose cadence did not render it this frame, as a static source's never does
    // again, is served by one more conversion of its current image, which becomes the source's latest output.
    private void ConvertForCapture(RenderGraphSchedule schedule, long frame, long tick, in FrameContext context) {
        var index = m_captureInstance;

        if (
            (m_capture.PendingPath is null) ||
            (m_sources[index] is not { } source) ||
            schedule.Renders.Contains(value: index)
        ) {
            return;
        }

        var node = m_nodes[index]!;

        if (
            !node.IsReady ||
            !source.TryWrite(
                node: node,
                tick: tick
            )
        ) {
            return;
        }

        m_capture.Forward(target: node);

        var submitted = node.FrameCounter;
        var surface = node.ProduceFrame(context: in context);

        if (node.FrameCounter == submitted) {
            return;
        }

        m_previous[index] = m_current[index];
        m_current[index] = new Output(
            Buffer: null,
            Frame: frame,
            Image: surface,
            Layout: node.PublishedLayout
        );
    }
    private static void DisposeSources(SourceGraph?[] sources) {
        foreach (var source in sources) {
            source?.Dispose();
        }
    }
    // The frame the scheduler reads: the host's frame, with each upload's and each source producer's cadence and extent
    // after the host's source states, unless the host declares that source itself.
    private RenderGraphFrame WithSourceStates(in RenderGraphFrame frame) {
        var uploads = false;

        foreach (var source in m_sources) {
            uploads |= (source?.State is not null);
        }

        if (
            !uploads &&
            !HasSourceProducers()
        ) {
            return frame;
        }

        m_sourceStates.Clear();

        if (frame.Sources is { } declared) {
            for (var index = 0; (index < declared.Count); index++) {
                m_sourceStates.Add(item: declared[index]);
            }
        }

        for (var index = 0; (index < m_sources.Length); index++) {
            if (m_sources[index]?.State is not { } state) {
                continue;
            }

            var named = false;

            for (var position = 0; (position < m_sourceStates.Count); position++) {
                named |= string.Equals(
                    a: m_sourceStates[position].Instance,
                    b: state.Instance,
                    comparisonType: StringComparison.Ordinal
                );
            }

            if (!named) {
                m_sourceStates.Add(item: state);
            }
        }

        AddProducerSourceStates();

        return (frame with {
            Sources = m_sourceStates,
        });
    }
    // Rebuilds every uploaded source whose upload now declares another descriptor than its graph was made for, before the
    // frame schedules: the running set reconfigured onto itself, which keeps every other instance and makes the drifted
    // sources' graphs, regions and nodes again from what their uploads declare now. Nothing allocates while no source
    // drifted.
    private void RebuildDriftedSources() {
        var drifted = false;

        foreach (var source in m_sources) {
            drifted |= (source?.Drifted ?? false);
        }

        if (!drifted) {
            return;
        }

        if (!TryReconfigure(
            graphs: new RenderGraphRuntimeGraph?[m_set.Instances.Count],
            refusal: out var refusal,
            root: Root,
            set: m_set
        )) {
            throw new InvalidOperationException(message: $"The running set refused its own reconfiguration while rebuilding a drifted source: {refusal.Message}");
        }
    }

    // One uploaded source: its upload, the graph its descriptor names, and the region the upload writes, which the node
    // owns once it is bound and releases on device loss, when the next render binds a new one.
    private sealed class SourceGraph(IRenderGraphSourceUpload upload, RenderGraphRuntimeGraph? graph, ImageSourceUploadHeader header, string name, string? fault) : IDisposable {
        // The descriptor the graph and region were made for.
        private readonly ImageSourceDescriptor? m_built = upload.Descriptor;
        private readonly RenderGraphSourceRegion m_region = new(header: header);

        // Whether the upload declares another descriptor now than the graph was made for, so the source is rebuilt.
        public bool Drifted => !Equals(
            objA: Upload.Descriptor,
            objB: m_built
        );

        public string? Fault { get; } = fault;
        public RenderGraphRuntimeGraph? Graph { get; } = graph;

        // Gives a new source's node its graph, at the extent the descriptor fixed.
        public void Install(ShaderPipelineRenderNode node) {
            if (Graph is null) {
                return;
            }

            node.Swap(pipeline: Graph.Pipeline);
            node.Resize(
                height: header.Height,
                width: header.Width
            );
        }

        // The scheduler's view of the source, or null when it has no graph and never renders.
        public RenderGraphSourceState? State { get; } = (((graph is not null) && (upload.Descriptor is { } descriptor))
            ? new RenderGraphSourceState(
                Cadence: descriptor.Cadence,
                Height: ((int)descriptor.Height),
                Instance: name,
                Width: ((int)descriptor.Width)
            )
            : null);
        public IRenderGraphSourceUpload Upload { get; } = upload;

        public void Dispose() => Upload.Dispose();
        // The node lost its device objects, the region among them.
        public void OnDeviceLost() => m_region.OnDeviceLost();
        // Writes the upload's image for a render into the region and returns whether the region holds an image to
        // convert: never while the node cannot bind one yet.
        public bool TryWrite(long tick, ShaderPipelineRenderNode node) => (
            (Graph is not null) &&
            (m_region.Bind(node: node) is { } region) &&
            Upload.TryWrite(
                region: region,
                tick: tick
            )
        );
    }
}
