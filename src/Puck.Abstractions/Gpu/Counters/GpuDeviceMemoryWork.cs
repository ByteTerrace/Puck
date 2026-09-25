using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// One backend's device-local memory over the process's life, <c>memory.&lt;backend&gt;</c>: the bytes allocated and
/// released, each at the allocation's actual size as the driver sized it (Vulkan's <c>VkMemoryRequirements.size</c>,
/// Direct3D 12's <c>GetResourceAllocationInfo</c>), and the most bytes held at once. The backends count at their
/// allocation sites — buffers, images, and exported and imported memory — and never count swapchain images, which the
/// presentation engine allocates. Memory the host can map but that is not device-local is not counted. The allocated
/// and released counts are per-backend-deterministic; the peak depends on when retired objects are released, which
/// follows GPU completion, so it is pacing. The counts survive device loss, because a recreated device counts into the
/// same instance; a device lost with memory still counted keeps it counted as held. Every count is written under one
/// lock, since allocation happens on build threads as well as the frame thread.
/// </summary>
public sealed class GpuDeviceMemoryWork : IWorkCounterSource {
    private readonly Lock m_gate = new();
    private readonly Dictionary<nint, long> m_live = [];
    private readonly WorkCounterSet m_counts;

    private long m_held;
    private long m_peak;

    /// <summary>Gets the kind counting device-local bytes allocated, at each allocation's actual size.</summary>
    public static WorkKind Allocated { get; } = new(name: "gpu.memory.device-local.allocated", unit: "bytes", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting device-local bytes released, at each allocation's actual size.</summary>
    public static WorkKind Released { get; } = new(name: "gpu.memory.device-local.released", unit: "bytes", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind reporting the most device-local bytes held at once.</summary>
    public static WorkKind Peak { get; } = new(name: "gpu.memory.device-local.peak", unit: "bytes", workClass: WorkClass.Pacing);

    /// <summary>Initializes a new instance of the <see cref="GpuDeviceMemoryWork"/> class.</summary>
    /// <param name="backend">The backend's name, as a report labels it (<c>vulkan</c>, <c>directx</c>).</param>
    /// <exception cref="ArgumentException"><paramref name="backend"/> is empty, or the composed name
    /// <c>memory.&lt;backend&gt;</c> is not a dotted work name.</exception>
    public GpuDeviceMemoryWork(string backend) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: backend);

        Backend = backend;
        m_counts = new WorkCounterSet(
            kinds: Counts.Kinds,
            name: $"memory.{backend}"
        );
    }

    /// <summary>Gets the backend's name.</summary>
    public string Backend { get; }
    /// <summary>Gets the device-local bytes held now: allocated and not yet released.</summary>
    public long Held {
        get {
            lock (m_gate) {
                return m_held;
            }
        }
    }
    /// <summary>Gets the name a counters report heads this source's section with, <c>memory.&lt;backend&gt;</c>.</summary>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => Counts.Kinds;

    /// <summary>Counts one device-local allocation, keyed by the native object whose release frees it.</summary>
    /// <param name="allocation">The native object the allocation is released through: a <c>VkDeviceMemory</c> or an
    /// <c>ID3D12Resource</c>; must be non-zero and not already counted.</param>
    /// <param name="bytes">The allocation's actual size, in bytes.</param>
    /// <exception cref="ArgumentException"><paramref name="allocation"/> is zero or already counted.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public void CountAllocated(nint allocation, long bytes) {
        if (0 == allocation) {
            throw new ArgumentException(
                message: "A counted allocation needs its native object.",
                paramName: nameof(allocation)
            );
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value: bytes);

        lock (m_gate) {
            if (!m_live.TryAdd(
                key: allocation,
                value: bytes
            )) {
                throw new ArgumentException(
                    message: $"The allocation 0x{allocation:X} is already counted.",
                    paramName: nameof(allocation)
                );
            }

            m_held += bytes;
            m_counts.Add(
                amount: bytes,
                kind: Allocated
            );

            if (m_held > m_peak) {
                m_counts.Add(
                    amount: (m_held - m_peak),
                    kind: Peak
                );
                m_peak = m_held;
            }
        }
    }
    /// <summary>Counts the release of an allocation <see cref="CountAllocated"/> counted, at the size it was counted
    /// with; an object never counted (zero, host memory, or a swapchain image) counts nothing.</summary>
    /// <param name="allocation">The native object being released.</param>
    /// <returns>Whether the object was a counted allocation.</returns>
    public bool CountReleased(nint allocation) {
        lock (m_gate) {
            if (!m_live.Remove(
                key: allocation,
                value: out var bytes
            )) {
                return false;
            }

            m_held -= bytes;
            m_counts.Add(
                amount: bytes,
                kind: Released
            );

            return true;
        }
    }
    /// <summary>Reads one of this source's kinds.</summary>
    /// <param name="kind">One of <see cref="Allocated"/>, <see cref="Released"/> or <see cref="Peak"/>.</param>
    /// <returns>The count.</returns>
    /// <exception cref="ArgumentException"><paramref name="kind"/> is not one of this source's kinds.</exception>
    public long Read(WorkKind kind) =>
        m_counts.Read(kind: kind);
    /// <inheritdoc/>
    public bool TryRead(WorkKind kind, out long value) =>
        m_counts.TryRead(
            kind: kind,
            value: out value
        );

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Counts {
        internal static readonly WorkKind[] Kinds = [Allocated, Released, Peak];
    }
}