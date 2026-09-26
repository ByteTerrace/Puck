using Puck.Abstractions.Counting;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// One backend's device-local memory over the process's life, <c>memory.&lt;backend&gt;</c>: the bytes allocated and
/// released, each at the allocation's actual size as the driver sized it (Vulkan's <c>VkMemoryRequirements.size</c>,
/// Direct3D 12's <c>GetResourceAllocationInfo</c>), and the most bytes held at once.
/// <para>
/// An allocation counts by its <see cref="GpuMemoryRole"/>, never by the memory type the driver chose, and
/// <see cref="IsCounted"/> is the one statement of that rule for both backends: images, device-local buffers, exportable
/// images, imported memory and host-visible device-local buffers (the aperture a ring region lives in) count, since
/// each consumes the adapter's memory; host-visible, staging, upload and readback buffers never do, even on a
/// unified-memory device where every memory type is device-local. Swapchain images, which the presentation engine
/// allocates, never reach here.
/// </para>
/// <para>
/// Each allocation is keyed by the device that made it and the native object whose release frees it, so a driver that
/// reuses a handle value on a recreated device never collides with an entry leaked from the old one. A device's
/// teardown ends its entries (<see cref="EndDevice"/>), and one still held there is refused by name: the leak is a
/// visible defect, never a silent count. The allocated and released counts are per-backend-deterministic; the peak
/// depends on when retired objects are released, which follows GPU completion, so it is pacing. The counts survive
/// device loss, because a recreated device counts into the same instance. Every count is written under one lock, since
/// allocation happens on build threads as well as the frame thread.
/// </para>
/// </summary>
public sealed class GpuDeviceMemoryWork : IWorkCounterSource {
    private readonly Lock m_gate = new();
    private readonly Dictionary<(nint Device, nint Allocation), long> m_live = [];

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
    /// <summary>Gets the device-local bytes held now: allocated and not yet released, including any a device's teardown
    /// refused as leaked.</summary>
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

    /// <summary>Returns whether an allocation of <paramref name="role"/> is counted: the roles that consume the
    /// adapter's memory, <see cref="GpuMemoryRole.DeviceLocal"/> and <see cref="GpuMemoryRole.HostVisibleDeviceLocal"/>,
    /// are.</summary>
    /// <param name="role">The allocation's role.</param>
    /// <returns>Whether the role counts.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is not a defined value.</exception>
    public static bool IsCounted(GpuMemoryRole role) => role switch {
        GpuMemoryRole.DeviceLocal or GpuMemoryRole.HostVisibleDeviceLocal => true,
        GpuMemoryRole.HostVisible => false,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: role,
            message: "The memory role is not a defined value.",
            paramName: nameof(role)
        ),
    };
    /// <summary>Counts one allocation when its role counts (<see cref="IsCounted"/>), keyed by its device and the native
    /// object whose release frees it.</summary>
    /// <param name="device">The native device that made the allocation: a <c>VkDevice</c> or an
    /// <c>ID3D12Device</c>; must be non-zero.</param>
    /// <param name="allocation">The native object the allocation is released through: a <c>VkDeviceMemory</c> or an
    /// <c>ID3D12Resource</c>; must be non-zero and not already counted on <paramref name="device"/>.</param>
    /// <param name="bytes">The allocation's actual size, in bytes.</param>
    /// <param name="role">What the allocation is for.</param>
    /// <returns>Whether the allocation was counted.</returns>
    /// <exception cref="ArgumentException"><paramref name="device"/> or <paramref name="allocation"/> is zero, or the
    /// allocation is already counted on the device.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative, or <paramref name="role"/> is
    /// not a defined value.</exception>
    public bool CountAllocated(nint device, nint allocation, long bytes, GpuMemoryRole role) {
        if (0 == device) {
            throw new ArgumentException(
                message: "A counted allocation needs the device that made it.",
                paramName: nameof(device)
            );
        }
        if (0 == allocation) {
            throw new ArgumentException(
                message: "A counted allocation needs its native object.",
                paramName: nameof(allocation)
            );
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value: bytes);

        if (!IsCounted(role: role)) {
            return false;
        }

        lock (m_gate) {
            if (!m_live.TryAdd(
                key: (device, allocation),
                value: bytes
            )) {
                throw new ArgumentException(
                    message: $"The allocation 0x{allocation:X} is already counted on device 0x{device:X}.",
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

        return true;
    }
    /// <summary>Counts the release of an allocation <see cref="CountAllocated"/> counted, at the size it was counted
    /// with; an object never counted on the device (zero, host memory, or a swapchain image) counts nothing.</summary>
    /// <param name="device">The native device that made the allocation.</param>
    /// <param name="allocation">The native object being released.</param>
    /// <returns>Whether the object was a counted allocation of the device.</returns>
    public bool CountReleased(nint device, nint allocation) {
        lock (m_gate) {
            if (!m_live.Remove(
                key: (device, allocation),
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
    /// <summary>Ends a device's entries at its teardown. Every allocation still counted on it was leaked by an owner
    /// that never released it: the entries are dropped, their bytes stay counted as held, and the teardown is refused
    /// with each one named.</summary>
    /// <param name="device">The native device being torn down.</param>
    /// <exception cref="InvalidOperationException">An allocation is still counted on <paramref name="device"/>; the
    /// message lists each one and its size.</exception>
    public void EndDevice(nint device) {
        List<(nint Allocation, long Bytes)>? leaked = null;

        lock (m_gate) {
            foreach (var ((owner, allocation), bytes) in m_live) {
                if (owner == device) {
                    (leaked ??= []).Add(item: (allocation, bytes));
                }
            }

            if (leaked is null) {
                return;
            }

            foreach (var (allocation, _) in leaked) {
                _ = m_live.Remove(key: (device, allocation));
            }
        }

        leaked.Sort(comparison: static (left, right) => left.Allocation.CompareTo(value: right.Allocation));

        throw new InvalidOperationException(message: $"{Name}: device 0x{device:X} was torn down holding {leaked.Count} counted allocation(s) their owners never released: {string.Join(separator: ", ", values: leaked.Select(selector: static entry => $"0x{entry.Allocation:X} ({entry.Bytes} bytes)"))}.");
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
