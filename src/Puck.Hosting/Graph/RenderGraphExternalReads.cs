using Puck.Abstractions.Collections;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Hosting;

/// <summary>The image, resting layout, view handle and publication unlocked from a
/// <see cref="RenderGraphExternalInput"/> at a <see cref="DeliveryLatch"/>.</summary>
/// <param name="Image">The image, or an empty surface when the instance read hands out its image view through the lease
/// alone.</param>
/// <param name="Layout">The layout the image rests in between its producer's submissions; a sampling submission hands
/// it back in this layout.</param>
/// <param name="ImageViewHandle">The image view to sample, or zero when none is bound.</param>
/// <param name="Publication">The bound image's actual publication, under the same lease that protects its pixels.</param>
public readonly record struct RenderGraphResolvedRead(Surface Image, GpuImageLayout Layout, nint ImageViewHandle, ReferenceRevision Publication);

/// <summary>One image an external producer or package pass reads when it records: the latest completed output of an
/// instance its own instance reads, paired with its <see cref="DeliveryLabel"/> so its handle and surface can be
/// obtained only through <see cref="TryRead"/>.</summary>
public readonly struct RenderGraphExternalInput {
    private readonly DeliveryContent<RenderGraphResolvedRead> m_content;

    internal RenderGraphExternalInput(string producer, DeliveryContent<RenderGraphResolvedRead> content, GpuImageLease lease) {
        Producer = producer;
        m_content = content;
        Lease = lease;
    }

    internal GpuImageLease Lease { get; }

    /// <summary>Gets the name of the instance read.</summary>
    public string Producer { get; }
    /// <summary>Gets the read's provenance and external-content classification.</summary>
    public DeliveryLabel Label => m_content.Label;
    /// <summary>Gets whether a classified image is bound for this read.</summary>
    public bool IsClassified => m_content.IsClassified;

    /// <summary>Reads the bound image, view handle and publication when <see cref="Label"/> allows
    /// <paramref name="latch"/>.</summary>
    /// <param name="latch">The frame's latch.</param>
    /// <param name="read">The resolved read when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a classified image is bound and every span in <see cref="Label"/> is
    /// current at <paramref name="latch"/>.</returns>
    public bool TryRead(in DeliveryLatch latch, out RenderGraphResolvedRead read) =>
        m_content.TryRead(latch: in latch, value: out read);
}

/// <summary>The images one external producer or package pass reads in one produced frame, in the order its instance
/// declares the reads. The render-graph runtime binds each read's latest completed output and hands the list to
/// <see cref="IRenderGraphExternalProducer.Produce"/> or <c>RenderGraphPackageRecording</c>; the reader takes or samples
/// the leases of the images its submission samples (<see cref="TryTake"/>, <see cref="TrySample"/>) at the frame's
/// <see cref="DeliveryLatch"/>, and the runtime retires every untaken lease once the frame returns. The runtime reuses
/// one list per instance, so a steady frame allocates nothing.</summary>
public sealed class RenderGraphExternalReads {
    private readonly Dictionary<string, int> m_indexByProducer;
    private readonly RenderGraphExternalInput[] m_inputs;
    private readonly bool[] m_taken;

    /// <summary>Initializes a new instance of the <see cref="RenderGraphExternalReads"/> class.</summary>
    /// <param name="producers">The instances read, in the order the consumer declares them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="producers"/> is <see langword="null"/>.</exception>
    public RenderGraphExternalReads(IReadOnlyList<string> producers) {
        ArgumentNullException.ThrowIfNull(argument: producers);

        m_indexByProducer = new Dictionary<string, int>(
            capacity: producers.Count,
            comparer: StringComparer.Ordinal
        );
        m_inputs = new RenderGraphExternalInput[producers.Count];
        m_taken = new bool[producers.Count];

        for (var index = 0; (index < producers.Count); index++) {
            _ = m_indexByProducer.TryAdd(
                key: producers[index],
                value: index
            );
            m_inputs[index] = new RenderGraphExternalInput(
                content: default,
                lease: default,
                producer: producers[index]
            );
        }
    }

    /// <summary>Gets the number of reads.</summary>
    public int Count => m_inputs.Length;
    /// <summary>Gets the union of every bound read's <see cref="DeliveryLabel"/> this frame, or a public host label
    /// when no read is bound.</summary>
    public DeliveryLabel Label {
        get {
            var combined = DeliveryLabel.ForSource(source: DeliveryPublicSource.Host);

            for (var index = 0; (index < m_inputs.Length); index++) {
                if (m_inputs[index].IsClassified) {
                    combined = combined.Union(other: m_inputs[index].Label);
                }
            }

            return combined;
        }
    }
    /// <summary>Gets whether any read bound this frame holds external pixels the capture gate did not fill.</summary>
    public bool External => Label.External;

    /// <summary>Gets one read.</summary>
    /// <param name="index">The read's position, from zero.</param>
    /// <returns>The read's carrying input this frame.</returns>
    public RenderGraphExternalInput this[int index] => m_inputs[index];

    /// <summary>Binds one read's image for the frame, replacing whatever it held.</summary>
    /// <param name="index">The read's position.</param>
    /// <param name="image">The image, or an empty surface when the instance read hands out a view handle alone.</param>
    /// <param name="layout">The layout the image rests in.</param>
    /// <param name="lease">The acquisition that keeps it alive.</param>
    /// <param name="label">The image's provenance and external-content classification; an unclassified label is
    /// refused.</param>
    /// <exception cref="ArgumentException"><paramref name="label"/> is unclassified.</exception>
    public void Bind(int index, Surface image, GpuImageLayout layout, GpuImageLease lease, DeliveryLabel label) {
        if (!label.IsClassified) {
            lease.Retire();

            throw new ArgumentException(
                message: $"External read '{m_inputs[index].Producer}' has an unclassified delivery label.",
                paramName: nameof(label)
            );
        }

        m_inputs[index] = new RenderGraphExternalInput(
            content: new DeliveryContent<RenderGraphResolvedRead>(
                label: label,
                value: new RenderGraphResolvedRead(
                    Image: image,
                    ImageViewHandle: lease.ImageViewHandle,
                    Layout: layout,
                    Publication: lease.Publication
                )
            ),
            lease: lease,
            producer: m_inputs[index].Producer
        );
        m_taken[index] = false;
    }
    /// <summary>Returns the position of the read of an instance.</summary>
    /// <param name="producer">The instance's name.</param>
    /// <returns>The position, or -1 when no read names it.</returns>
    public int IndexOf(string producer) => (m_indexByProducer.TryGetValue(
        key: producer,
        value: out var index
    )
        ? index
        : -1
    );
    /// <summary>Returns whether one read's lease was taken this frame, so a producer that samples one image in several
    /// places takes it once.</summary>
    /// <param name="index">The read's position.</param>
    /// <returns><see langword="true"/> once <see cref="TryTake"/> or <see cref="TrySample"/> has taken it this
    /// frame.</returns>
    public bool IsTaken(int index) => m_taken[index];
    /// <summary>Retires every lease no producer took and clears every read's image, which the runtime does once
    /// the frame's recording returns.</summary>
    public void RetireUntaken() {
        for (var index = 0; (index < m_inputs.Length); index++) {
            var lease = m_inputs[index].Lease;

            if (!m_taken[index]) {
                lease.Retire();
            }

            m_inputs[index] = new RenderGraphExternalInput(
                content: default,
                lease: default,
                producer: m_inputs[index].Producer
            );
            m_taken[index] = false;
        }
    }
    /// <summary>Reads one bound input at <paramref name="latch"/> and takes its lease at most once for the frame.</summary>
    /// <param name="index">The read's position.</param>
    /// <param name="latch">The frame's latch.</param>
    /// <param name="read">The resolved image, view handle and publication when this returns <see langword="true"/>.</param>
    /// <param name="lease">The lease to retire once the sampling submission finishes.</param>
    /// <returns><see langword="true"/> when a classified image with a non-zero view handle is bound and allowed at
    /// <paramref name="latch"/>.</returns>
    /// <exception cref="InvalidOperationException">The lease was taken already this frame.</exception>
    public bool TryTake(int index, in DeliveryLatch latch, out RenderGraphResolvedRead read, out GpuImageLease lease) {
        if (m_taken[index]) {
            throw new InvalidOperationException(message: $"The read of '{m_inputs[index].Producer}' was taken already this frame.");
        }
        if (
            !m_inputs[index].TryRead(latch: in latch, read: out read) ||
            (read.ImageViewHandle == 0)
        ) {
            read = default;
            lease = default;

            return false;
        }

        m_taken[index] = true;
        lease = m_inputs[index].Lease;

        return true;
    }
    /// <summary>Samples one read at <paramref name="latch"/>, holding its lease in <paramref name="leases"/> once for
    /// the frame even when several passes of the recording sample the same read.</summary>
    /// <param name="index">The read's position.</param>
    /// <param name="latch">The frame's latch.</param>
    /// <param name="leases">The frame's lease list, which retires the lease after the submission.</param>
    /// <param name="imageViewHandle">The sampled image-view handle when this returns <see langword="true"/>; otherwise
    /// zero.</param>
    /// <returns><see langword="true"/> when a classified image with a non-zero view handle is bound and allowed at
    /// <paramref name="latch"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="leases"/> is <see langword="null"/>.</exception>
    public bool TrySample(int index, in DeliveryLatch latch, LeaseRetireList leases, out nint imageViewHandle) {
        ArgumentNullException.ThrowIfNull(argument: leases);

        if (
            !m_inputs[index].TryRead(latch: in latch, read: out var read) ||
            (read.ImageViewHandle == 0)
        ) {
            imageViewHandle = 0;

            return false;
        }
        if (!m_taken[index]) {
            m_taken[index] = true;

            var lease = m_inputs[index].Lease;

            leases.Hold(lease: in lease);
        }

        imageViewHandle = read.ImageViewHandle;

        return true;
    }
}
