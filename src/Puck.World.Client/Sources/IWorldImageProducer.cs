using System.Numerics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Sources;
using Puck.Hosting;

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
    /// <param name="deviceContext">The live GPU device context.</param>
    /// <param name="gpu">The neutral GPU compute services.</param>
    void Publish(ulong tick, IGpuDeviceContext deviceContext, IGpuComputeServices gpu);
}
/// <summary>
/// A producer registered with the World host under an id: it opens a feed for every screen source naming it. Its id,
/// content class and transport are its document shape's (<see cref="WorldImageProducerShape"/>), which
/// <see cref="WorldImageProducers.Register"/> checks, so what a document validates and what the host opens cannot
/// disagree. Adding an emulator, a capture API or a video decoder is one registration of each; the document model and
/// its schemas do not change.
/// </summary>
public interface IWorldImageProducer : IImageSourceProducer {
    /// <summary>Opens a feed for one screen's source.</summary>
    /// <param name="source">The source naming this producer; its settings passed the producer's shape at validation.</param>
    /// <param name="screenIndex">The engine screen-surface index the feed lights.</param>
    /// <param name="feed">The opened feed, or <see langword="null"/> when it could not open.</param>
    /// <param name="fault">Why it could not open, or <see langword="null"/> when it opened.</param>
    /// <returns><see langword="true"/> when a feed opened.</returns>
    bool TryOpen(WorldScreenSource.Producer source, int screenIndex, out IWorldImageFeed? feed, out string? fault);
}
/// <summary>
/// The image producers a World host opens sources through, by id. A registration must match a shape registered in
/// <see cref="WorldImageProducerVocabulary"/> under the same id, content class and transport.
/// </summary>
public sealed class WorldImageProducers {
    private readonly ImageSourceProducerRegistry<IWorldImageProducer> m_registry = new();

    /// <summary>Gets the registered producers in registration order.</summary>
    public IReadOnlyList<IWorldImageProducer> Producers => m_registry.Producers;

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
    /// <summary>Opens a feed for a producer source through the producer registered under its id.</summary>
    /// <param name="source">The source.</param>
    /// <param name="screenIndex">The engine screen-surface index the feed lights.</param>
    /// <param name="feed">The opened feed, or <see langword="null"/>.</param>
    /// <param name="fault">Why no feed opened, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a feed opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    public bool TryOpen(WorldScreenSource.Producer source, int screenIndex, out IWorldImageFeed? feed, out string? fault) {
        ArgumentNullException.ThrowIfNull(argument: source);

        if (!m_registry.TryGet(
            id: source.Id,
            producer: out var producer
        )) {
            feed = null;
            fault = $"no image producer '{source.Id}' is registered with this host";

            return false;
        }

        return producer.TryOpen(
            fault: out fault,
            feed: out feed,
            screenIndex: screenIndex,
            source: source
        );
    }
}
