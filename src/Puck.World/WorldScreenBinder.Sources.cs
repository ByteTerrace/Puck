using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Machines;
using Puck.Abstractions.Presentation;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Platform;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

// The screens as the engine node binds them, and the source instances their rows read. A row's producer, machine or probe
// source is a render-graph source instance: the runtime opens it through the registered producers (and the machine and
// probe producers below, of the reserved ids), publishes it at its cadence, and hands the world producer its latest
// image, which the node binds to every screen whose row reads it. Every other image a screen shows the binder renders
// or holds itself.
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

        return slot.DeclaredSource switch {
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
    /// <summary>Creates the producer of a machine source instance (<see cref="WorldImageProducerSettings.MachineId"/>):
    /// it publishes the named machine output's latest framebuffer once per completed tick and hands out its image.</summary>
    /// <param name="context">The source instance and its settings.</param>
    /// <returns>The producer, which owns nothing: the machine belongs to <see cref="Server.WorldMachineHost"/>.</returns>
    public IRenderGraphExternalProducer MachineSource(RenderGraphExternalProducerContext context) => new MachineSourceProducer(
        binder: this,
        instance: context.Instance,
        source: (SourceOf(context: context) as WorldScreenSource.Machine)
    );
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
    /// <remarks>A screen reads its row's source instance while it shows its row; a live presentation source bound over
    /// the row is rendered by the binder.</remarks>
    public string? ReadOf(int screen) => (m_liveBinds.Contains(item: screen)
        ? null
        : Mappings.InstanceOf(screen: screen)
    );
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
        if (slot.DeclaredSource is WorldScreenSource.Probe probe) {
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

    // A machine output as a source: published once per completed tick at the output's extent. Its image is written on
    // this device by the machine's own upload, so its lease needs no retirement.
    private sealed class MachineSourceProducer(WorldScreenBinder binder, WorldScreenSource.Machine? source, string instance) : IRenderGraphSourceProducer, IGpuWorkSource {
        private ImageSourceDescriptor? m_descriptor;
        private IMachineVideoOutput? m_described;

        public ImageSourceDescriptor? Descriptor {
            get {
                var output = Output();

                if (!ReferenceEquals(
                    objA: output,
                    objB: m_described
                )) {
                    m_described = output;
                    m_descriptor = ((output is null)
                        ? null
                        : new ImageSourceDescriptor(
                            Cadence: ImageSourceCadence.Tick,
                            Color: ImageColorEncoding.Srgb,
                            Content: ImageContentClass.Deterministic,
                            Format: ImagePixelFormat.R8G8B8A8Unorm,
                            Height: ((uint)output.Height),
                            Producer: WorldImageProducerSettings.MachineId,
                            Transport: ImageSourceTransport.Uploaded,
                            Width: ((uint)output.Width)
                        ));
                }

                return m_descriptor;
            }
        }
        public SurfaceFormat Format => SurfaceFormat.R8G8B8A8Unorm;
        public string? NotReadyReason => ((Output() is { NativeImageViewHandle: not 0 })
            ? null
            : $"machine source '{instance}' has no published frame");
        public string? PendingCapturePath => null;
        public IGpuWorkSource Work => this;

        private IMachineVideoOutput? Output() => ((source is null)
            ? null
            : binder.m_machines.VideoOutput(
                instance: source.Instance,
                output: source.Output
            ));

        public void Dispose() { }
        // The binder retires every output it published when the device is lost.
        public void OnDeviceLost() { }
        public bool Produce(in FrameContext context, uint width, uint height, RenderGraphExternalReads? reads = null) {
            if (
                (source is null) ||
                (Output() is not { } output) ||
                !context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)
            ) {
                return false;
            }

            binder.m_presentedMachineOutputs.Publish(
                deviceContext: device,
                instance: source.Instance,
                machine: output,
                output: source.Output
            );

            return (output.NativeImageViewHandle != 0);
        }
        public void RequestCapture(FrameCaptureRequest request) {
            ArgumentNullException.ThrowIfNull(argument: request);

            _ = request.TryFail(error: new NotSupportedException(message: $"Source '{instance}' is captured through the instance that shows it."));
        }
        public bool TryAcquireOutput(out RenderGraphExternalOutput output) {
            var handle = (Output()?.NativeImageViewHandle ?? 0);

            output = ((handle == 0)
                ? default
                : new RenderGraphExternalOutput(
                    Image: default,
                    Layout: GpuImageLayout.ShaderReadOnly,
                    Lease: handle
                ));

            return (handle != 0);
        }

        bool IGpuWorkSource.TryReadCompleted(GpuWorkSample sample) => false;
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
        public SurfaceFormat Format => SurfaceFormat.R8G8B8A8Unorm;
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
