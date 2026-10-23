using Puck.Hosting;
using Puck.Shaders;
using Puck.World.Client;

namespace Puck.World;

// The render graph's side of the screens' sources: the producers and uploads its source instances open through, and the
// feed and fault of each instance it runs.
public sealed partial class WorldScreenBinder {
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
    /// gate hands out its latest published slot, or its capture fill while the gate fills or before the probe's first
    /// frame. Only the boot world runs a
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
    // The source instance a producer context opens, rebuilt from its package and settings.
    private static RenderGraphInstance SourceInstanceOf(RenderGraphExternalProducerContext context) => RenderGraphInstance.Source(
        name: context.Instance,
        producer: context.Package[RenderGraphInstance.SourcePackagePrefix.Length..],
        settings: context.Settings
    );
    // The feed of an instance the runtime runs, by the producer that opened it.
    private static IWorldImageFeed? RunningFeedOf(RenderGraphRuntime runtime, string instance) {
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
    // The fault of an instance the runtime runs, by the producer that opened it.
    private static string? RunningSourceFault(RenderGraphRuntime runtime, string instance) {
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
