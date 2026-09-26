using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Hosting;

/// <summary>One image an external producer reads when it produces: the latest completed output of an instance its own
/// instance reads, bound by the render-graph runtime.</summary>
/// <param name="Producer">The name of the instance read.</param>
/// <param name="Image">The image, or an empty surface when the instance read has completed no output.</param>
/// <param name="Layout">The layout the image is in between its producer's submissions; a sampling submission hands it
/// back in this layout.</param>
/// <param name="Lease">The acquisition that keeps the image alive; handle-only for an instance that needs none.</param>
public readonly record struct RenderGraphExternalInput(string Producer, Surface Image, GpuImageLayout Layout, GpuImageLease Lease);
/// <summary>The images one external producer reads in one produced frame, in the order its instance declares the reads.
/// The render-graph runtime binds each read's latest completed output and hands the list to
/// <see cref="IRenderGraphExternalProducer.Produce"/>; the producer takes the leases of the images its submission
/// samples (<see cref="Take"/>) and retires each after that submission's fence, and the runtime retires every lease it
/// leaves once <c>Produce</c> returns. The runtime reuses one list per instance, so a steady frame allocates nothing.
/// Every member runs on the thread that produces frames.</summary>
public sealed class RenderGraphExternalReads {
    private readonly RenderGraphExternalInput[] m_inputs;
    private readonly bool[] m_taken;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphExternalReads"/> class.</summary>
    /// <param name="producers">The instances read, in the order the consumer declares them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="producers"/> is <see langword="null"/>.</exception>
    public RenderGraphExternalReads(IReadOnlyList<string> producers) {
        ArgumentNullException.ThrowIfNull(argument: producers);

        m_inputs = new RenderGraphExternalInput[producers.Count];
        m_taken = new bool[producers.Count];

        for (var index = 0; (index < producers.Count); index++) {
            m_inputs[index] = new RenderGraphExternalInput(
                Image: default,
                Layout: default,
                Lease: default,
                Producer: producers[index]
            );
        }
    }

    /// <summary>Gets the number of reads.</summary>
    public int Count => m_inputs.Length;

    /// <summary>Gets one read.</summary>
    /// <param name="index">The read's position, from zero.</param>
    /// <returns>The read's image this frame.</returns>
    public RenderGraphExternalInput this[int index] => m_inputs[index];

    /// <summary>Binds one read's image for the frame, replacing whatever it held.</summary>
    /// <param name="index">The read's position.</param>
    /// <param name="image">The image, or an empty surface when the instance read has none.</param>
    /// <param name="layout">The layout the image rests in.</param>
    /// <param name="lease">The acquisition that keeps it alive.</param>
    public void Bind(int index, Surface image, GpuImageLayout layout, GpuImageLease lease) {
        m_inputs[index] = (m_inputs[index] with {
            Image = image,
            Layout = layout,
            Lease = lease,
        });
        m_taken[index] = false;
    }
    /// <summary>Returns the position of the read of an instance.</summary>
    /// <param name="producer">The instance's name.</param>
    /// <returns>The position, or -1 when no read names it.</returns>
    public int IndexOf(string producer) {
        for (var index = 0; (index < m_inputs.Length); index++) {
            if (string.Equals(
                a: m_inputs[index].Producer,
                b: producer,
                comparisonType: StringComparison.Ordinal
            )) {
                return index;
            }
        }

        return -1;
    }
    /// <summary>Retires every lease no producer took and clears every read's image, which the runtime does once
    /// <see cref="IRenderGraphExternalProducer.Produce"/> returns.</summary>
    public void RetireUntaken() {
        for (var index = 0; (index < m_inputs.Length); index++) {
            var lease = m_inputs[index].Lease;

            if (!m_taken[index]) {
                lease.Retire();
            }

            m_inputs[index] = (m_inputs[index] with {
                Image = default,
                Layout = default,
                Lease = default,
            });
            m_taken[index] = false;
        }
    }
    /// <summary>Takes one read's lease: the producer's submission samples its image, and the producer retires the lease
    /// once that submission has finished, on device loss or at disposal. A lease is taken at most once a frame.</summary>
    /// <param name="index">The read's position.</param>
    /// <returns>The lease.</returns>
    /// <exception cref="InvalidOperationException">The lease was taken already this frame.</exception>
    public GpuImageLease Take(int index) {
        if (m_taken[index]) {
            throw new InvalidOperationException(message: $"The read of '{m_inputs[index].Producer}' was taken already this frame.");
        }

        m_taken[index] = true;

        return m_inputs[index].Lease;
    }
}
