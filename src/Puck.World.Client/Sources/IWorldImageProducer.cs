using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.World.Client;

/// <summary>
/// One open image source on a screen: the feed a <see cref="IWorldImageProducer"/> opened for a
/// <see cref="WorldScreenSource.Producer"/> source. The screen binder publishes it once per produced frame, samples it
/// through <see cref="WorldCaptureGate"/> (so an external feed never reaches a capture), and disposes it when the
/// screen stops showing it. Every member runs on the presentation thread.
/// </summary>
public interface IWorldImageFeed : IDisposable {
    /// <summary>Gets the feed's descriptor: its producer, transport, format, cadence, content class and capture fill.</summary>
    ImageSourceDescriptor Descriptor { get; }
    /// <summary>Gets why the feed shows nothing, or <see langword="null"/> while it shows its image.</summary>
    string? Fault { get; }
    /// <summary>Gets the image's average emitted color, normalized to 0–1, which lights the room around the screen.</summary>
    Vector3 Light { get; }

    /// <summary>Acquires the image for one submitted frame. A feed whose image another thread keeps writing returns a
    /// lease the sampling node retires once that submission completes.</summary>
    /// <returns>The lease, or one holding a zero handle while the feed has no image.</returns>
    GpuImageLease AcquireFrame();
    /// <summary>Returns the current image-view handle for a read that submits no GPU work.</summary>
    /// <returns>The handle, or zero while the feed has no image.</returns>
    nint Handle();
    /// <summary>Drops every device-owned resource after a device loss; the next <see cref="Publish"/> recreates them.</summary>
    void NotifyDeviceLost();
    /// <summary>Publishes the feed's current image for this produced frame, uploading only what its cadence owes.</summary>
    /// <param name="tick">The world's completed-step ordinal.</param>
    /// <param name="deviceContext">The live GPU device context, whose services upload the image.</param>
    void Publish(ulong tick, IGpuDeviceContext deviceContext);
}
/// <summary>
/// A producer registered with the World host under an id: it opens a feed for every screen source naming it. Its id,
/// content class and transport are its document shape's (<see cref="WorldImageProducerShape"/>), which
/// <see cref="WorldImageProducers.Register"/> checks, so what a document validates and what the host opens cannot
/// disagree. Adding an emulator, a capture API or a video decoder is one registration of each; the document model and
/// its schemas do not change.
/// </summary>
public interface IWorldImageProducer : IImageSourceProducer {
    /// <summary>Opens a feed for one source: the image every screen and instance showing that source reads.</summary>
    /// <param name="source">The source naming this producer; its settings passed the producer's shape at validation.</param>
    /// <param name="feed">The opened feed, or <see langword="null"/> when it could not open.</param>
    /// <param name="fault">Why it could not open, or <see langword="null"/> when it opened.</param>
    /// <returns><see langword="true"/> when a feed opened.</returns>
    bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault);
}
/// <summary>What a source instance's external-producer factory opened: the instance it opened for, and its feed or why it
/// has none. The host adapts it to the render-graph producer that publishes and hands out the feed's image.</summary>
/// <param name="Context">The source instance, its settings and the device it renders on.</param>
/// <param name="Feed">The opened feed, which agrees with its producer's registration, or <see langword="null"/> when none
/// opened.</param>
/// <param name="Fault">Why no feed opened, naming the producer, or <see langword="null"/> when one did.</param>
public readonly record struct WorldImageSourceOpening(RenderGraphExternalProducerContext Context, IWorldImageFeed? Feed, string? Fault);
/// <summary>
/// The image producers a World host opens sources through, by id. A registration must match a shape registered in
/// <see cref="WorldImageProducerVocabulary"/> under the same id, content class and transport, and every feed a producer
/// opens must declare them too. A source instance of a producer (<see cref="RenderGraphInstance.SourcePackage"/>) opens
/// through the external-producer factory <see cref="RegisterPackages"/> registers under its package id.
/// </summary>
public sealed class WorldImageProducers {
    private readonly ImageSourceProducerRegistry<IWorldImageProducer> m_registry = new();

    /// <summary>Gets the registered producers in registration order.</summary>
    public IReadOnlyList<IWorldImageProducer> Producers => m_registry.Producers;

    /// <summary>Registers one external-producer factory per producer registered so far with a host's render-graph
    /// packages, under the producer's source package (<c>source.&lt;id&gt;</c>). Each factory opens the feed of the source instance
    /// it is created for, from the instance's settings, through <see cref="TryOpen"/>, so a feed that disagrees with its
    /// registration is refused by name there, and hands what it opened to <paramref name="adapt"/>.</summary>
    /// <param name="packages">The packages the host's render-graph runtime installs instances from.</param>
    /// <param name="adapt">Adapts one opening to the render-graph producer the runtime owns; it owns the feed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="packages"/> or <paramref name="adapt"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A producer's source package already has an external producer.</exception>
    public void RegisterPackages(RenderGraphPackageRecorders packages, Func<WorldImageSourceOpening, IRenderGraphExternalProducer> adapt) {
        ArgumentNullException.ThrowIfNull(argument: packages);
        ArgumentNullException.ThrowIfNull(argument: adapt);

        foreach (var producer in m_registry.Producers) {
            var id = producer.Id;

            packages.RegisterProducer(
                factory: context => {
                    _ = TryOpen(
                        fault: out var fault,
                        feed: out var feed,
                        source: new WorldScreenSource.Producer(
                            Id: id,
                            Settings: context.Settings
                        )
                    );

                    return adapt(arg: new WorldImageSourceOpening(
                        Context: context,
                        Fault: fault,
                        Feed: feed
                    ));
                },
                package: RenderGraphInstance.SourcePackage(producer: id)
            );
        }
    }
    /// <summary>Registers a producer.</summary>
    /// <param name="producer">The producer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="producer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">No shape is registered under the producer's id, the shape declares another
    /// content class or transport, or a producer is already registered under the id.</exception>
    public void Register(IWorldImageProducer producer) {
        ArgumentNullException.ThrowIfNull(argument: producer);

        if (!WorldImageProducerVocabulary.TryGet(
            id: producer.Id,
            shape: out var shape
        )) {
            throw new ArgumentException(
                message: $"No document shape is registered for image producer '{producer.Id}'; register its WorldImageProducerShape first.",
                paramName: nameof(producer)
            );
        }

        if (
            (shape.Content != producer.Content) ||
            (shape.Transport != producer.Transport)
        ) {
            throw new ArgumentException(
                message: $"Image producer '{producer.Id}' declares {producer.Content} over {producer.Transport}; its document shape declares {shape.Content} over {shape.Transport}.",
                paramName: nameof(producer)
            );
        }

        m_registry.Register(producer: producer);
    }
    /// <summary>Opens a feed for a producer source through the producer registered under its id. A feed whose descriptor
    /// names another producer, content class or transport than the registration is disposed and refused by name.</summary>
    /// <param name="source">The source.</param>
    /// <param name="feed">The opened feed, or <see langword="null"/>.</param>
    /// <param name="fault">Why no feed opened, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a feed opened and agrees with its registration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public bool TryOpen(WorldScreenSource.Producer source, out IWorldImageFeed? feed, out string? fault) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (!m_registry.TryGet(
            id: source.Id,
            producer: out var producer
        )) {
            feed = null;
            fault = $"no image producer '{source.Id}' is registered with this host";

            return false;
        }

        if (!producer.TryOpen(
            fault: out fault,
            feed: out feed,
            source: source
        )) {
            return false;
        }

        // The feed's descriptor is what every consumer reads, so it must say what the registration says: the producer
        // it came from, and the content class and transport its document shape declares. A feed depends on the source's
        // settings, so this is checked where it opens.
        var descriptor = feed!.Descriptor;

        if (
            !string.Equals(
                a: descriptor.Producer,
                b: producer.Id,
                comparisonType: StringComparison.Ordinal
            ) ||
            (descriptor.Content != producer.Content) ||
            (descriptor.Transport != producer.Transport)
        ) {
            feed.Dispose();
            feed = null;
            fault = $"image producer '{producer.Id}' opened a feed declaring producer '{descriptor.Producer}' with {descriptor.Content} over {descriptor.Transport}, but it is registered as {producer.Content} over {producer.Transport}";

            return false;
        }

        return true;
    }
}
