using Puck.Abstractions.Gpu;

namespace Puck.Hosting;

/// <summary>
/// The <see cref="GpuImageLease"/>s a render node holds until the GPU has finished a submission that sampled them.
/// The node holds each lease that requires retirement as it binds the image, and retires the list once a fence wait
/// proves the submission that read them has finished; a node with frames in flight keeps one list per frame-ring
/// slot and moves the frame's list into its slot when it submits.
/// <para>
/// Every member runs on the node's recording thread. Holding, moving and retiring allocate nothing once the list has
/// grown to the most leases one frame holds.
/// </para>
/// </summary>
public sealed class LeaseRetireList {
    private int m_count;
    private GpuImageLease[] m_leases;

    /// <summary>Initializes a new instance of the <see cref="LeaseRetireList"/> class.</summary>
    /// <param name="capacity">The number of leases the list holds before it grows.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    public LeaseRetireList(int capacity = 0) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: capacity);

        m_leases = new GpuImageLease[capacity];
    }

    /// <summary>Gets the number of leases held.</summary>
    public int Count => m_count;

    /// <summary>Adds the wait of every held lease that carries one (<see cref="GpuImageLease.Wait"/>) to the list the
    /// submitter's next submission carries. The node calls it immediately before the one submission that samples the
    /// held images, so each wait lands in that submission.</summary>
    /// <param name="submitter">The submitter of the sampling submission.</param>
    /// <exception cref="ArgumentNullException"><paramref name="submitter"/> is <see langword="null"/>.</exception>
    public void AddWaits(IGpuQueueSubmitter submitter) {
        ArgumentNullException.ThrowIfNull(argument: submitter);

        for (var index = 0; (index < m_count); index++) {
            if (m_leases[index].HasWait) {
                submitter.AddExternalWait(wait: m_leases[index].Wait);
            }
        }
    }
    /// <summary>Holds <paramref name="lease"/> until <see cref="RetireAll"/>. A lease that requires no retirement and
    /// carries no wait is not held.</summary>
    /// <param name="lease">The lease a submission samples.</param>
    public void Hold(in GpuImageLease lease) {
        if (
            !lease.RequiresRetirement &&
            !lease.HasWait
        ) {
            return;
        }

        if (m_count == m_leases.Length) {
            Array.Resize(
                array: ref m_leases,
                newSize: Math.Max(
                    val1: 4,
                    val2: (m_count * 2)
                )
            );
        }

        m_leases[m_count++] = lease;
    }
    /// <summary>Moves every held lease to the end of <paramref name="destination"/>, leaving this list empty.</summary>
    /// <param name="destination">The list that holds the leases from now on.</param>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    public void MoveTo(LeaseRetireList destination) {
        ArgumentNullException.ThrowIfNull(destination);

        for (var index = 0; (index < m_count); index++) {
            destination.Hold(lease: in m_leases[index]);
            m_leases[index] = default;
        }

        m_count = 0;
    }
    /// <summary>Retires every held lease, in the order it was held, and empties the list. Call only once no submission
    /// can still sample the images: after the fence wait that proves it, or after a device loss.</summary>
    public void RetireAll() {
        var count = m_count;

        m_count = 0;

        for (var index = 0; (index < count); index++) {
            var lease = m_leases[index];

            m_leases[index] = default;
            lease.Retire();
        }
    }
}
