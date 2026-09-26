using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Platform;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

// The screens as the engine node binds them, and the source instances their rows read. A row's producer, machine or probe
// source is a render-graph source instance: the runtime opens it through the registered producers (and the machine
// upload and probe producer below, of the reserved ids), renders it at its cadence, and hands the world producer its
// latest image, which the node binds to every screen whose row reads it. Every other image a screen shows the binder
// renders or holds itself.
internal sealed partial class WorldScreenBinder : ISdfScreenSources {
    /// <summary>Gets or sets the render-graph runtime the world renders through, whose source instances this binder reads
    /// a screen's feed, fault and light from; <see langword="null"/> in a presentation with no render graph, which runs no
    /// source instance.</summary>
    public RenderGraphRuntime? Runtime { get; set; }
    /// <inheritdoc/>
    public IReadOnlyList<int> Screens => m_screenIndices;

    /// <summary>Adapts what a source instance's factory opened for a producer that is not uploaded to the render-graph
    /// producer the runtime owns, which resolves the feed's image through this binder's capture gate and fills.</summary>
    /// <param name="opening">What the factory opened.</param>
    /// <returns>The producer, which owns the feed.</returns>
    public IRenderGraphExternalProducer Adapt(WorldImageSourceOpening opening) => new WorldImageFeedProducer(
        fill: m_fillImage,
        gate: m_captureGate,
        opening: opening
    );
    /// <inheritdoc/>
    /// <remarks>A screen reading a source instance lights the room with its source's light, resolved through the capture
    /// gate; any other with what it shows locally.</remarks>
    public Vector3 Light(int screen) {
        if (!m_slots.TryGetValue(
            key: screen,
            value: out var slot
        )) {
            return Vector3.Zero;
        }
        if (ReadOf(screen: screen) is not { } instance) {
            return slot.Light();
        }

        return ShownOf(screen: screen) switch {
            WorldScreenSource.Machine machine => (m_machines.VideoOutput(
                instance: machine.Instance,
                output: machine.Output
            )?.EmittedLight ?? Vector3.Zero),
            WorldScreenSource.Probe probe => (FillsExternal
                ? WorldImageLight.OfFill(rgba: ImageSourceDescriptor.DefaultCaptureFill)
                : (m_probeFeeds.TryGetValue(
                    key: probe.Id,
                    value: out var feed
                ) ? feed.Light : Vector3.Zero)),
            _ => ((FeedOf(instance: instance) is { } source)
                ? ResolveLight(feed: source)
                : Vector3.Zero),
        };
    }
    /// <summary>Creates the upload of a machine source instance (<see cref="WorldImageProducerSettings.MachineId"/>): once per
    /// completed tick it writes the named machine output's latest complete frame into the instance's region, which the
    /// runtime converts once however many screens show it.</summary>
    /// <param name="context">The source instance and its settings.</param>
    /// <returns>The upload, which owns nothing: the machine belongs to <see cref="Server.WorldMachineHost"/>.</returns>
    public IRenderGraphSourceUpload MachineSource(RenderGraphExternalProducerContext context) {
        var source = (SourceOf(context: context) as WorldScreenSource.Machine);

        return new MachineVideoSourceUpload(
            name: context.Instance,
            output: () => ((source is null)
                ? null
                : m_machines.VideoOutput(
                    instance: source.Instance,
                    output: source.Output
                )),
            producer: WorldImageProducerSettings.MachineId
        );
    }
    /// <summary>Creates the producer of a probe source instance (<see cref="WorldImageProducerSettings.ProbeId"/>): it hands
    /// out the probe output's latest published slot, or its capture fill while the gate fills.</summary>
    /// <param name="context">The source instance and its settings.</param>
    /// <returns>The producer, which owns nothing: the probe's feed belongs to this binder.</returns>
    public IRenderGraphExternalProducer ProbeSource(RenderGraphExternalProducerContext context) => new ProbeSourceProducer(
        binder: this,
        feed: ((SourceOf(context: context) is WorldScreenSource.Probe probe)
            ? GetOrAddProbeFeed(id: probe.Id)
            : null),
        instance: context.Instance
    );
    /// <inheritdoc/>
    /// <remarks>A screen reads the source instance of the source it shows: its row's, or the one a live presentation
    /// verb bound over the row.</remarks>
    public string? ReadOf(int screen) => Mappings.InstanceOf(screen: screen);
    /// <inheritdoc/>
    public GpuImageLease Rendered(int screen) => (m_slots.TryGetValue(
        key: screen,
        value: out var slot
    )
        ? slot.AcquireFrame()
        : 0
    );

    // The screen source a source instance's settings name.
    private static WorldScreenSource? SourceOf(RenderGraphExternalProducerContext context) => WorldSourceInstances.SourceOf(instance: RenderGraphInstance.Source(
        name: context.Instance,
        producer: context.Package[RenderGraphInstance.SourcePackagePrefix.Length..],
        settings: context.Settings
    ));
    // The feed a running source instance opened: an imported producer's through its adapter, an uploaded producer's
    // through its upload; null for a machine or probe source, one the set does not run, or no runtime.
    private IWorldImageFeed? FeedOf(string instance) {
        if (Runtime is not { } runtime) {
            return null;
        }

        var index = runtime.Instances.IndexOf(name: instance);

        if (index < 0) {
            return null;
        }

        return (runtime.Producer(instance: index) switch {
            WorldImageFeedProducer adapter => adapter.Feed,
            null => (runtime.Source(instance: index) as WorldImageSourceUpload)?.Opening.Feed,
            _ => null,
        });
    }
    // Why a screen reading a source instance shows nothing: its producer's fault (a feed that did not open or has no
    // signal, an upload refused), a probe not yet live, or null while it shows its image. A machine's fault is
    // Machines.State's concern.
    private string? SourceFault(ScreenSlot slot, string instance) {
        if (ShownOf(screen: slot.Index) is WorldScreenSource.Probe probe) {
            return ((m_probeFeeds.TryGetValue(
                key: probe.Id,
                value: out var feed
            ) && !feed.Live)
                ? feed.Fault
                : null);
        }
        if (Runtime is not { } runtime) {
            return null;
        }

        var index = runtime.Instances.IndexOf(name: instance);

        if (index < 0) {
            return null;
        }

        return (runtime.Producer(instance: index) switch {
            WorldImageFeedProducer adapter => adapter.Fault,
            null => runtime.Source(instance: index)?.Fault,
            _ => null,
        });
    }

    // A probe output as a source: the kernel publishes its slots on its own thread and the binder services the ring each
    // frame, so the source is due once per completed tick only to report whether it is live. The image is external
    // content, handed out through the capture gate.
    private sealed class ProbeSourceProducer(WorldScreenBinder binder, ProbeFeed? feed, string instance) : IRenderGraphSourceProducer, IGpuWorkSource {
        private ImageSourceDescriptor? m_descriptor;
        // The ring the descriptor describes, a new one whenever the probe's output is provisioned again.
        private LatestSlotPublication? m_described;

        public ImageSourceDescriptor? Descriptor {
            get {
                var output = feed?.Output;

                if (!ReferenceEquals(
                    objA: output?.Slots,
                    objB: m_described
                )) {
                    m_described = output?.Slots;
                    m_descriptor = ((output is not { } ring)
                        ? null
                        : new ImageSourceDescriptor(
                            Cadence: ImageSourceCadence.Tick,
                            Color: ImageColorEncoding.Srgb,
                            Content: ImageContentClass.External,
                            Format: ImagePixelFormat.R8G8B8A8Unorm,
                            Height: ((uint)ring.Height),
                            Producer: WorldImageProducerSettings.ProbeId,
                            Transport: ImageSourceTransport.Imported,
                            Width: ((uint)ring.Width)
                        ));
                }

                return m_descriptor;
            }
        }
        public GpuPixelFormat Format => GpuPixelFormat.R8G8B8A8Unorm;
        public string? NotReadyReason => ((feed is { Live: true })
            ? null
            : (feed?.Fault ?? $"probe source '{instance}' names no probe"));
        public string? PendingCapturePath => null;
        public IGpuWorkSource Work => this;

        public void Dispose() { }
        // The binder retires every probe ring when the device is lost.
        public void OnDeviceLost() { }
        public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) => (feed is { Live: true });
        public void RequestCapture(FrameCaptureRequest request) {
            ArgumentNullException.ThrowIfNull(argument: request);

            _ = request.TryFail(error: new NotSupportedException(message: $"Source '{instance}' is captured through the instance that shows it."));
        }
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            if (feed is null) {
                output = default;

                return false;
            }

            var lease = (binder.FillsExternal
                ? binder.FillImage(rgba: ImageSourceDescriptor.DefaultCaptureFill)
                : feed.AcquireFrame());

            if (lease.ImageViewHandle == 0) {
                lease.Retire();
                output = default;

                return false;
            }

            output = new RenderGraphExternalOutput(
                Image: default,
                Layout: GpuImageLayout.ShaderReadOnly,
                Lease: lease
            );

            return true;
        }

        bool IGpuWorkSource.TryReadCompleted(GpuWorkSample sample) => false;
    }
}
