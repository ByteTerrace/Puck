using Puck.Abstractions.Collections;

namespace Puck.Shaders;

/// <summary>
/// What one instance's reads of other instances' images will bind in the frame being scheduled, as the runtime knows it
/// when it asks the instance's packages whether its latest render stands
/// (<see cref="IRenderGraphPackageFactory.IsUnchanged"/>): an image stands for the frame only when its producer is a graph
/// instance the runtime has already declared unchanged this frame and the consumer reads the producer's current frame,
/// so the image its consumer would bind is the one the producer published before. A package compares that publication
/// with the one its instance's latest render sampled to decide whether an image it samples moved. Valid only during the
/// call it is handed to.
/// </summary>
public readonly struct RenderGraphReadStands {
    private readonly RenderGraphRuntime? m_runtime;
    private readonly int m_consumer;

    internal RenderGraphReadStands(RenderGraphRuntime runtime, int consumer) {
        m_runtime = runtime;
        m_consumer = consumer;
    }

    /// <summary>Gets read stands that know of no producer: every image counts as moved.</summary>
    public static RenderGraphReadStands None => default;

    /// <summary>Returns whether a producer's image stands for the frame, and the publication it holds.</summary>
    /// <param name="producer">The name of the instance read.</param>
    /// <param name="publication">The publication of the image the read would bind, when this returns
    /// <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the producer is a graph instance already declared unchanged this frame, the
    /// consumer reads its current frame, and it holds a published image; otherwise <see langword="false"/>, an external
    /// producer, one that renders this frame or one read at its previous frame included.</returns>
    public bool TryStanding(string producer, out ReferenceRevision publication) {
        publication = default;

        return ((m_runtime is { } runtime) && runtime.TryStandingRead(consumer: m_consumer, producer: producer, publication: out publication));
    }
}
