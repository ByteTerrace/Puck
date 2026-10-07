using Puck.Commands;
using Puck.Hosting;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.World.Client;
using Puck.World.Server;

namespace Puck.World;

// The screens as the world's views bind them, and the instances they read. A row's producer, machine or probe source is a
// render-graph source instance: the runtime opens it through the registered producers (and the machine upload and probe
// producer below, of the reserved ids), renders it at its cadence, and hands each sdf.world instance that reads it its latest image, which
// the view's passes bind to every screen whose row reads it. A camera view or a session is a view instance the runtime
// hands a view the same way.
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
        fillRender: m_fills.RenderOf,
        gate: m_captureGate,
        opening: opening
    );
    /// <inheritdoc/>
    public bool Emits(int screen) => (m_slots.TryGetValue(key: screen, value: out var slot) && (ShownOf(screen: screen) switch {
        WorldScreenSource.Machine or WorldScreenSource.Producer or WorldScreenSource.Probe => true,
        WorldScreenSource.Session => ((slot.Session is { } feed) && (feed.InstanceName != WorldInstanceHost.BootInstanceName)),
        _ => false,
    }));
    /// <inheritdoc/>
    /// <remarks>A screen is drawn from the mapping <see cref="Mappings"/> last published for it.</remarks>
    public SourceMapping? MappingOf(int screen) => (Mappings.TryGet(
        mapping: out var mapping,
        screen: screen
    )
        ? mapping
        : null
    );
    /// <summary>Creates the upload of a machine source instance (<see cref="WorldImageProducerSettings.MachineId"/>): once per
    /// completed tick it writes the named machine output's latest complete frame into the instance's region, which the
    /// runtime converts once however many screens show it. The machine is its world's own, read from the host of the
    /// world instance its settings name (<see cref="MachinesOf"/>), the boot world's or a world shown through a screen,
    /// so the source shows nothing while that world runs on another authority.</summary>
    /// <param name="context">The source instance and its settings.</param>
    /// <returns>The upload, which owns nothing: the machine belongs to its world's machine host.</returns>
    public IRenderGraphSourceUpload MachineSource(RenderGraphExternalProducerContext context) {
        var instance = SourceInstanceOf(context: context);
        var source = (WorldSourceInstances.SourceOf(instance: instance) as WorldScreenSource.Machine);
        var world = WorldSourceInstances.WorldOf(instance: instance);

        return new MachineVideoSourceUpload(
            name: context.Instance,
            output: () => (((source is null) || (world is null))
                ? null
                : MachinesOf(world: world)?.VideoOutput(
                    instance: source.Instance,
                    output: source.Output
                )),
            producer: WorldImageProducerSettings.MachineId
        );
    }
    /// <summary>Creates the producer of a probe source instance (<see cref="WorldImageProducerSettings.ProbeId"/>): an
    /// imported source adapted like any other (<see cref="Adapt"/>), whose feed is the probe output's ring, so the capture
    /// gate hands out its latest published slot, or its capture fill while the gate fills. Only the boot world runs a
    /// probe host: a probe of a world shown through a screen opens with a fault naming that world.</summary>
    /// <param name="context">The source instance and its settings.</param>
    /// <returns>The producer, whose feed owns nothing: the probe's ring belongs to this binder.</returns>
    public IRenderGraphExternalProducer ProbeSource(RenderGraphExternalProducerContext context) {
        var instance = SourceInstanceOf(context: context);
        var world = WorldSourceInstances.WorldOf(instance: instance);

        return Adapt(opening: (WorldSourceInstances.SourceOf(instance: instance) switch {
            WorldScreenSource.Probe probe when string.Equals(
                a: world,
                b: WorldInstanceHost.BootInstanceName,
                comparisonType: StringComparison.Ordinal
            ) => new WorldImageSourceOpening(
                Context: context,
                Fault: null,
                Feed: new ProbeSourceFeed(feed: GetOrAddProbeFeed(id: probe.Id))
            ),
            WorldScreenSource.Probe probe => new WorldImageSourceOpening(
                Context: context,
                Fault: $"probe '{probe.Id}' of world '{world}': no probe host runs for a world shown through a screen",
                Feed: null
            ),
            _ => new WorldImageSourceOpening(
                Context: context,
                Fault: $"probe source '{context.Instance}' names no probe",
                Feed: null
            ),
        }));
    }
    /// <summary>Returns the machine host of a world instance this process runs: the boot world's own, or the host of a
    /// local instance a screen shows or a seat is presented in, which steps that world's machines on its own ticks.</summary>
    /// <param name="world">The world instance's name.</param>
    /// <returns>The host, or <see langword="null"/> for a world this process does not run, whose machines run on its own
    /// authority and reach no screen here.</returns>
    public IWorldMachineHost? MachinesOf(string world) {
        if (string.Equals(
            a: world,
            b: WorldInstanceHost.BootInstanceName,
            comparisonType: StringComparison.Ordinal
        )) {
            return m_machines;
        }

        return ((m_instanceHost.TryGet(
            instance: out var instance,
            name: world
        ) && (instance is not null))
            ? instance.Server.Machines
            : null);
    }
    /// <inheritdoc/>
    /// <remarks>Every view of the world's residency, a seat's or a camera's, shows the world's screens alike
    /// (<see cref="InstanceOf"/>).</remarks>
    public string? ReadOf(int view, int screen) => InstanceOf(screen: screen);
    /// <summary>Returns the name of the instance a screen of the boot world reads: the source it shows, its row's or the
    /// one a live presentation verb bound over the row; the source instance of a producer, machine or probe source, and
    /// the view instance of a camera view or a session.</summary>
    /// <param name="screen">The screen's index.</param>
    /// <returns>The instance's name, or <see langword="null"/> when the screen shows nothing or text.</returns>
    public string? InstanceOf(int screen) => (Mappings.InstanceOf(screen: screen) ?? (m_slots.TryGetValue(
        key: screen,
        value: out var slot
    )
        ? slot.ViewInstance
        : null
    ));

    // The source instance a producer context opens, rebuilt from its package and settings.
    private static RenderGraphInstance SourceInstanceOf(RenderGraphExternalProducerContext context) => RenderGraphInstance.Source(
        name: context.Instance,
        producer: context.Package[RenderGraphInstance.SourcePackagePrefix.Length..],
        settings: context.Settings
    );
    // The feed a running source instance opened: an imported producer's (a probe's included) through its adapter, an
    // uploaded producer's through its upload; null for a machine source, one the set does not run, or no runtime.
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
    // signal, a probe not yet live, an upload refused), or null while it shows its image. A machine's fault is
    // Machines.State's concern.
    private string? SourceFault(string instance) {
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
}
