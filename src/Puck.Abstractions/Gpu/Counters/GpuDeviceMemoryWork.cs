using Puck.Abstractions.Counting;
using Puck.Abstractions.Memory;

namespace Puck.Abstractions.Gpu;

/// <summary>
/// One backend's device memory over the process's life, <c>memory.&lt;backend&gt;</c>: the bytes allocated and released,
/// each at the allocation's actual size as the driver sized it (Vulkan's <c>VkMemoryAllocateInfo.allocationSize</c>,
/// Direct3D 12's <c>GetResourceAllocationInfo</c> or heap size), and the most bytes held at once, in two disjoint rows:
/// device-local bytes, with the part of them the host maps through the adapter's aperture
/// (<see cref="GpuMemoryRole.HostVisibleDeviceLocal"/>), and host-visible bytes (staging, upload and readback memory).
/// <para>
/// An allocation counts twice over, in rows that never add to each other. Its <see cref="GpuMemoryRole"/> chooses its
/// role row, and <see cref="IsDeviceLocal"/> is the one statement of that rule for both backends: images, device-local
/// buffers, exportable images, imported memory and host-visible device-local buffers (the aperture a ring region lives
/// in) are device-local; host-visible, staging, upload and readback buffers are host-visible, even on a unified-memory
/// device. The segment its backend placed it in (a DXGI segment group, a Vulkan heap) chooses its segment row and the
/// pool it is charged to, through the <see cref="GpuMemorySegments"/> registered for its device
/// (<see cref="BeginDevice"/>); the role never chooses a pool. <see cref="CountAllocated"/> is the one door, and it takes
/// only a <see cref="GpuMemoryGrant"/>, which <see cref="TryAdmit"/> alone makes: a backend asks before its native
/// allocation, at the size the device will be asked for, and the host's account answers whether the segment's pool and
/// the segment itself have room (<see cref="IGpuMemoryOwners.Admit"/>), so no byte is counted that was not admitted
/// first. A native allocation the device refuses hands its grant back (<see cref="GpuMemoryGrant.Refuse"/>), which
/// lowers the segment's limit to what it holds and marks the device's reading stale (<see cref="DeviceRefusals"/>). Each
/// counted byte is in exactly one role row and exactly one segment row, so each set of rows adds up to what the backend's
/// counted allocations hold, and an allocation whose role and pool disagree on a discrete adapter (a device-local role
/// placed in system memory, or the reverse) is counted (<see cref="PlacementDisagreements"/>). Each segment row reports
/// its held bytes to the observer the host hands the constructor, called under this ledger's lock. Swapchain images,
/// which the presentation engine allocates, and driver memory never reach here: a segment's API usage less its row is
/// that memory and any allocation made without counting.
/// </para>
/// <para>
/// Each counted byte is also named by at most one owner family (<see cref="GpuObjectName.MemoryFamily"/>): a dedicated
/// allocation, which one resource holds, by the family <see cref="CountAllocated"/> takes with it; a shared block by the
/// families of the resources placed in it (<see cref="CountPlaced"/>), each placement at its own size. A name adds
/// nothing: it is a part of its allocation's segment row, refused where it would name a byte twice (a placement in a
/// dedicated allocation, or placements past the block's size) or a byte never counted, so per segment the families'
/// held bytes never pass the row, and the rest of the row (room inside shared blocks, an emptied block kept for reuse)
/// is its residual (<see cref="GpuSegmentLedger.Residual"/>). Each family row reports its held bytes to the family's
/// observer (<see cref="IGpuMemoryOwners.OfFamily"/>), and keeps its allocated and released bytes, so a family that
/// churns is read apart from one that holds.
/// </para>
/// <para>
/// Each allocation is keyed by the device that made it and the native object whose release frees it, so a driver that
/// reuses a handle value on a recreated device never collides with an entry leaked from the old one. A device's
/// teardown ends its entries (<see cref="EndDevice"/>), and one still held there is refused by name: the leak is a
/// visible defect, never a silent count. Every count is pacing: which buffers exist when follows GPU completion (retired
/// objects go after their fences) and wall-clock idle windows (background GPU work releases a slot's buffers once it has
/// not needed them for <c>SdfGpuWork.IdleSeconds</c>), so two runs of one workload can allocate and release different
/// bytes. The counts survive
/// device loss, because a recreated device counts into the same instance. Every count is written under one lock, since
/// allocation happens on build threads as well as the frame thread.
/// </para>
/// </summary>
public sealed class GpuDeviceMemoryWork : IWorkCounterSource {
    private readonly Lock m_gate = new();
    private readonly Dictionary<(nint Device, nint Allocation), Allocation> m_live = [];
    private readonly Dictionary<nint, GpuMemorySegments> m_devices = [];
    // A row per segment index and pool, kept across devices so a recreated device's segments count into the rows (and
    // the observers) its predecessor's did.
    private readonly Dictionary<(int Index, PhysicalMemory Pool), SegmentRow> m_rows = [];
    // A family's row per segment, kept across devices like the segment rows.
    private readonly Dictionary<(string Family, int Index, PhysicalMemory Pool), FamilyRow> m_families = [];
    // Every placement a family names inside a shared allocation, by the token CountPlaced handed out.
    private readonly Dictionary<long, Placement> m_placements = [];

    private readonly IGpuMemoryOwners? m_owners;

    private long m_tokens;
    private long m_deviceRefusals;

    private readonly WorkCounterSet m_counts;

    private long m_apertureHeld;
    private long m_aperturePeak;
    private long m_held;
    private long m_hostVisibleHeld;
    private long m_hostVisiblePeak;
    private long m_peak;

    /// <summary>Gets the kind counting device-local bytes allocated, at each allocation's actual size.</summary>
    public static WorkKind Allocated { get; } = new(name: "gpu.memory.device-local.allocated", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting device-local bytes released, at each allocation's actual size.</summary>
    public static WorkKind Released { get; } = new(name: "gpu.memory.device-local.released", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind reporting the most device-local bytes held at once.</summary>
    public static WorkKind Peak { get; } = new(name: "gpu.memory.device-local.peak", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the part of <see cref="Allocated"/> the host maps through the adapter's aperture
    /// (<see cref="GpuMemoryRole.HostVisibleDeviceLocal"/>), whose heap is small on a discrete adapter.</summary>
    public static WorkKind ApertureAllocated { get; } = new(name: "gpu.memory.host-visible-device-local.allocated", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the part of <see cref="Released"/> that was aperture memory.</summary>
    public static WorkKind ApertureReleased { get; } = new(name: "gpu.memory.host-visible-device-local.released", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind reporting the most aperture bytes held at once.</summary>
    public static WorkKind AperturePeak { get; } = new(name: "gpu.memory.host-visible-device-local.peak", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting host-visible bytes allocated (<see cref="GpuMemoryRole.HostVisible"/>: staging,
    /// upload and readback memory), disjoint from <see cref="Allocated"/>.</summary>
    public static WorkKind HostVisibleAllocated { get; } = new(name: "gpu.memory.host-visible.allocated", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting host-visible bytes released.</summary>
    public static WorkKind HostVisibleReleased { get; } = new(name: "gpu.memory.host-visible.released", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind reporting the most host-visible bytes held at once.</summary>
    public static WorkKind HostVisiblePeak { get; } = new(name: "gpu.memory.host-visible.peak", unit: "bytes", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the native allocations the device made, of every role: each <c>vkAllocateMemory</c>,
    /// or each Direct3D 12 heap or committed resource, whatever it holds.</summary>
    public static WorkKind Allocations { get; } = new(name: "gpu.memory.allocations", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the allocations whose role and placement disagree on a discrete adapter: a
    /// device-local role placed in a segment of host memory, or a host-visible role placed in the adapter's own.</summary>
    public static WorkKind PlacementDisagreements { get; } = new(name: "gpu.memory.placement-disagreements", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the allocations the account refused before the device was asked
    /// (<see cref="TryAdmit"/>).</summary>
    public static WorkKind AdmissionRefusals { get; } = new(name: "gpu.memory.admission-refusals", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the allocations admitted past a limit because their class cannot give way
    /// (<see cref="GpuMemoryVerdict.OverLimit"/>).</summary>
    public static WorkKind OverLimitGrants { get; } = new(name: "gpu.memory.over-limit-grants", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gets the kind counting the admitted allocations the device itself refused
    /// (<see cref="GpuMemoryGrant.Refuse"/>).</summary>
    public static WorkKind DeviceRefusalCount { get; } = new(name: "gpu.memory.device-refusals", unit: "count", workClass: WorkClass.Pacing);

    /// <summary>The family of a backend's descriptor heaps, which no caller names.</summary>
    public const string DescriptorHeapFamily = "backend/descriptor-heaps";
    /// <summary>The family of memory a device opens from another device or process (an imported surface).</summary>
    public const string ImportedFamily = "backend/imported";
    /// <summary>The family of images a device creates for another API or process to open (an exported surface).</summary>
    public const string ExportedFamily = "backend/exported";

    /// <summary>Initializes a new instance of the <see cref="GpuDeviceMemoryWork"/> class.</summary>
    /// <param name="backend">The backend's name, as a report labels it (<c>vulkan</c>, <c>directx</c>).</param>
    /// <param name="owners">The observers each segment row and each family row report their held bytes to, asked for
    /// under this ledger's lock; <see langword="null"/> reports to none.</param>
    /// <exception cref="ArgumentException"><paramref name="backend"/> is empty, or the composed name
    /// <c>memory.&lt;backend&gt;</c> is not a dotted work name.</exception>
    public GpuDeviceMemoryWork(string backend, IGpuMemoryOwners? owners = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: backend);

        m_owners = owners;
        Backend = backend;
        m_counts = new WorkCounterSet(
            kinds: Counts.Kinds,
            name: $"memory.{backend}"
        );
    }

    /// <summary>Gets the backend's name.</summary>
    public string Backend { get; }
    /// <summary>Forwards account pressure to the native pools. Registration does not retain a pool once it
    /// unregisters at device teardown. A ledger without an account has no pressure source.</summary>
    public event Action? TrimRequested {
        add { if (m_owners is not null) { m_owners.TrimRequested += value; } }
        remove { if (m_owners is not null) { m_owners.TrimRequested -= value; } }
    }
    /// <summary>Gets the device-local bytes held now: allocated and not yet released, including any a device's teardown
    /// refused as leaked.</summary>
    public long Held {
        get {
            lock (m_gate) {
                return m_held;
            }
        }
    }
    /// <summary>Gets the host-visible bytes held now, disjoint from <see cref="Held"/>, including any a device's teardown
    /// refused as leaked.</summary>
    public long HostVisibleHeld {
        get {
            lock (m_gate) {
                return m_hostVisibleHeld;
            }
        }
    }
    /// <summary>Gets the name a counters report heads this source's section with, <c>memory.&lt;backend&gt;</c>.</summary>
    public string Name => m_counts.Name;
    /// <inheritdoc/>
    public ReadOnlySpan<WorkKind> WorkKinds => Counts.Kinds;

    /// <summary>Returns whether an allocation of <paramref name="role"/> counts as device-local: the roles that consume
    /// the adapter's memory, <see cref="GpuMemoryRole.DeviceLocal"/> and <see cref="GpuMemoryRole.HostVisibleDeviceLocal"/>,
    /// do; <see cref="GpuMemoryRole.HostVisible"/> counts as host-visible.</summary>
    /// <param name="role">The allocation's role.</param>
    /// <returns>Whether the role is device-local.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="role"/> is not a defined value.</exception>
    public static bool IsDeviceLocal(GpuMemoryRole role) => role switch {
        GpuMemoryRole.DeviceLocal or GpuMemoryRole.HostVisibleDeviceLocal => true,
        GpuMemoryRole.HostVisible => false,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: role,
            message: "The memory role is not a defined value.",
            paramName: nameof(role)
        ),
    };
    /// <summary>Registers the segments a device budgets, as its backend read them when it created the device, before its
    /// first counted allocation; <see cref="EndDevice"/> ends the registration. Each segment's row, and its observer, is
    /// created the first time any device registers it.</summary>
    /// <param name="device">The native device: a <c>VkDevice</c> or an <c>ID3D12Device</c>; must be non-zero.</param>
    /// <param name="segments">The device's segments.</param>
    /// <exception cref="ArgumentException"><paramref name="device"/> is zero, or is registered already.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="segments"/> is <see langword="null"/>.</exception>
    public void BeginDevice(nint device, GpuMemorySegments segments) {
        ArgumentNullException.ThrowIfNull(argument: segments);
        if (0 == device) {
            throw new ArgumentException(message: "A registered device needs its native handle.", paramName: nameof(device));
        }

        lock (m_gate) {
            if (!m_devices.TryAdd(key: device, value: segments)) {
                throw new ArgumentException(message: $"{Name}: device 0x{device:X} is registered already.", paramName: nameof(device));
            }

            foreach (var segment in segments.Segments) {
                _ = RowOf(segment: segment);
            }
        }
    }
    /// <summary>Returns the segments registered for a device (<see cref="BeginDevice"/>).</summary>
    /// <param name="device">The native device.</param>
    /// <returns>The device's segments.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="device"/> is not registered.</exception>
    public GpuMemorySegments SegmentsOf(nint device) {
        lock (m_gate) {
            return SegmentsOfLocked(device: device);
        }
    }
    /// <summary>Reads every segment row: its held, peak, allocated and released bytes, the part of its held bytes the
    /// families name, and its placement disagreements, under the ledger's lock.</summary>
    /// <returns>The rows, by segment index then pool.</returns>
    public IReadOnlyList<GpuSegmentLedger> ReadSegments() {
        lock (m_gate) {
            return [.. m_rows.Values
                .OrderBy(keySelector: static row => row.Segment.Index)
                .ThenBy(keySelector: static row => row.Segment.Pool)
                .Select(selector: static row => new GpuSegmentLedger(
                    Allocated: row.Allocated,
                    Disagreements: row.Disagreements,
                    Held: row.Held,
                    Named: row.Named,
                    Peak: row.Peak,
                    Released: row.Released,
                    Segment: row.Segment
                ))];
        }
    }
    /// <summary>Reads every family row: the bytes the family's resources hold in a segment now, the most they held at
    /// once, and the bytes they were allocated and released, under the ledger's lock.</summary>
    /// <returns>The rows, by family then segment index and pool.</returns>
    public IReadOnlyList<GpuFamilyLedger> ReadFamilies() {
        lock (m_gate) {
            return [.. m_families.Values
                .OrderBy(keySelector: static row => row.Family, comparer: StringComparer.Ordinal)
                .ThenBy(keySelector: static row => row.Segment.Index)
                .ThenBy(keySelector: static row => row.Segment.Pool)
                .Select(selector: static row => new GpuFamilyLedger(
                    Allocated: row.Allocated,
                    Family: row.Family,
                    Held: row.Held,
                    Peak: row.Peak,
                    Released: row.Released,
                    Segment: row.Segment
                ))];
        }
    }

    /// <summary>Gets how many admitted allocations the device refused over the process's life. A backend reads it when it
    /// refreshes its memory reading: a count that moved marks the reading stale. Safe to read from any thread.</summary>
    public long DeviceRefusals => Volatile.Read(location: ref m_deviceRefusals);

    /// <summary>Asks whether a device allocation may be made, before the native call that makes it, at the size the device
    /// will be asked for: a shared block's whole size or a resource's own allocation, never the size of the resource that
    /// prompted a block. The host's account answers for the pool and the segment the allocation would be placed in
    /// (<see cref="IGpuMemoryOwners.Admit"/>); a ledger with no account grants everything. A grant is settled exactly
    /// once: by <see cref="CountAllocated"/> when the native allocation succeeded, by <see cref="GpuMemoryGrant.Refuse"/>
    /// when the device refused it, or by <see cref="GpuMemoryGrant.Return"/> when it was never attempted.</summary>
    /// <param name="device">The native device that will make the allocation: a <c>VkDevice</c> or an
    /// <c>ID3D12Device</c>; must be non-zero and registered (<see cref="BeginDevice"/>).</param>
    /// <param name="bytes">The allocation's size, in bytes, as the device will be asked for it.</param>
    /// <param name="role">What the allocation is for.</param>
    /// <param name="segment">The index of the segment the device will place it in: the heap of the Vulkan memory type it
    /// allocates from (<see cref="GpuMemorySegments.OfMemoryType"/>), or the segment group of its Direct3D 12 heap's
    /// memory pool on the device's adapter.</param>
    /// <param name="family">The family that names the whole allocation, for one a single resource holds (a committed
    /// resource, a resource's own memory, a descriptor heap); <see langword="null"/> for a block shared by placements,
    /// which each name their own part (<see cref="CountPlaced"/>).</param>
    /// <param name="payer">The family of the resource that prompted the allocation, whose reservation the grant draws on
    /// first; <see langword="null"/> draws on unreserved room alone.</param>
    /// <param name="payerBytes">The bytes of the resource that prompted the allocation: the most of
    /// <paramref name="payer"/>'s reservation this allocation consumes.</param>
    /// <param name="grant">The grant, when the allocation may be made.</param>
    /// <returns><see langword="true"/> when the allocation may be made; <see langword="false"/> when the account refused
    /// it, and nothing was allocated or counted.</returns>
    /// <exception cref="ArgumentException"><paramref name="device"/> is zero, or <paramref name="family"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> or <paramref name="payerBytes"/> is
    /// negative, <paramref name="role"/> is not a defined value, or the device has no segment
    /// <paramref name="segment"/>.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="device"/> is not registered.</exception>
    public bool TryAdmit(nint device, long bytes, GpuMemoryRole role, int segment, string? family, string? payer, long payerBytes, [System.Diagnostics.CodeAnalysis.NotNullWhen(returnValue: true)] out GpuMemoryGrant? grant) {
        if (0 == device) {
            throw new ArgumentException(
                message: "An admitted allocation needs the device that will make it.",
                paramName: nameof(device)
            );
        }

        ArgumentOutOfRangeException.ThrowIfNegative(value: bytes);
        ArgumentOutOfRangeException.ThrowIfNegative(value: payerBytes);
        if (family is { Length: 0 }) {
            throw new ArgumentException(message: "A family is absent or non-empty.", paramName: nameof(family));
        }

        _ = IsDeviceLocal(role: role);

        GpuMemorySegment placed;

        lock (m_gate) {
            placed = SegmentsOfLocked(device: device)[segment];
            _ = RowOf(segment: placed);
        }

        var admission = (m_owners?.Admit(bytes: bytes, givesWay: GpuMemoryAdmissionScope.GivesWay, payer: payer, payerBytes: Math.Min(val1: payerBytes, val2: bytes), segment: placed) ?? new GpuMemoryAdmission(Bytes: bytes, Net: bytes, Payer: null, Verdict: GpuMemoryVerdict.Granted));

        if (admission.Verdict == GpuMemoryVerdict.Refused) {
            m_counts.Add(amount: 1L, kind: AdmissionRefusals);
            grant = null;

            return false;
        }
        if (admission.Verdict == GpuMemoryVerdict.OverLimit) { m_counts.Add(amount: 1L, kind: OverLimitGrants); }
        grant = new GpuMemoryGrant(admission: admission, bytes: bytes, device: device, family: family, ledger: this, role: role, segment: placed);

        return true;
    }
    /// <summary>Asks as <see cref="TryAdmit"/> does and throws when the account refuses, for a creation door whose
    /// caller handles a refusal as an exception.</summary>
    /// <param name="device">The native device that will make the allocation.</param>
    /// <param name="bytes">The allocation's size, in bytes, as the device will be asked for it.</param>
    /// <param name="role">What the allocation is for.</param>
    /// <param name="segment">The index of the segment the device will place it in.</param>
    /// <param name="family">The family that names the whole allocation, or <see langword="null"/> for a shared block.</param>
    /// <param name="payer">The family of the resource that prompted the allocation, or <see langword="null"/>.</param>
    /// <param name="payerBytes">The bytes of the resource that prompted the allocation.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="GpuMemoryRefusedException">The account refused the allocation; nothing was allocated.</exception>
    public GpuMemoryGrant Admit(nint device, long bytes, GpuMemoryRole role, int segment, string? family, string? payer, long payerBytes) => (TryAdmit(bytes: bytes, device: device, family: family, grant: out var grant, payer: payer, payerBytes: payerBytes, role: role, segment: segment)
        ? grant
        : throw new GpuMemoryRefusedException(bytes: bytes, byDevice: false, family: (family ?? payer), segment: SegmentsOf(device: device)[segment].Name));
    /// <summary>Counts one admitted allocation into the role row its role names (<see cref="IsDeviceLocal"/>) and the
    /// segment row of the segment it was placed in, charging the segment's pool, keyed by its device and the native object
    /// whose release frees it, and settles its grant.</summary>
    /// <param name="grant">The grant <see cref="TryAdmit"/> made for the allocation, not yet settled: it carries the
    /// device, the size, the role, the segment and the family.</param>
    /// <param name="allocation">The native object the allocation is released through: a <c>VkDeviceMemory</c>, an
    /// <c>ID3D12Heap</c> or an <c>ID3D12Resource</c>; must be non-zero and not already counted on the grant's
    /// device.</param>
    /// <exception cref="ArgumentNullException"><paramref name="grant"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="grant"/> is another ledger's, <paramref name="allocation"/>
    /// is zero, or the allocation is already counted on the device.</exception>
    /// <exception cref="InvalidOperationException">The grant was settled already, or its device is no longer
    /// registered.</exception>
    public void CountAllocated(GpuMemoryGrant grant, nint allocation) {
        ArgumentNullException.ThrowIfNull(argument: grant);
        if (!ReferenceEquals(objA: grant.Ledger, objB: this)) {
            throw new ArgumentException(message: "The grant was made by another ledger.", paramName: nameof(grant));
        }
        if (0 == allocation) {
            throw new ArgumentException(
                message: "A counted allocation needs its native object.",
                paramName: nameof(allocation)
            );
        }

        var device = grant.Device;
        var bytes = grant.Bytes;
        var role = grant.Role;
        var family = grant.Family;
        var segment = grant.Segment.Index;

        var deviceLocal = IsDeviceLocal(role: role);

        lock (m_gate) {
            var segments = SegmentsOfLocked(device: device);
            var placed = segments[segment];
            var row = RowOf(segment: placed);
            var named = ((family is null) ? null : FamilyOf(family: family, segment: placed));

            if (m_live.ContainsKey(key: (device, allocation))) {
                throw new ArgumentException(
                    message: $"The allocation 0x{allocation:X} is already counted on device 0x{device:X}.",
                    paramName: nameof(allocation)
                );
            }

            grant.Take();
            if (!m_live.TryAdd(
                key: (device, allocation),
                value: new Allocation(bytes: bytes, family: named, role: role, row: row)
            )) {
                throw new ArgumentException(
                    message: $"The allocation 0x{allocation:X} is already counted on device 0x{device:X}.",
                    paramName: nameof(allocation)
                );
            }

            m_counts.Add(
                amount: 1L,
                kind: Allocations
            );
            // The row is charged before the grant is settled, so the account never reads the block as neither held nor
            // promised.
            row.Allocate(bytes: bytes);
            m_owners?.Settle(admission: grant.Admission, segment: placed, settlement: GpuMemorySettlement.Counted);
            named?.Allocate(bytes: bytes);

            if (segments.ExpectedPool(role: role) != placed.Pool) {
                row.Disagreements++;
                m_counts.Add(
                    amount: 1L,
                    kind: PlacementDisagreements
                );
            }

            if (!deviceLocal) {
                m_hostVisibleHeld += bytes;
                m_counts.Add(
                    amount: bytes,
                    kind: HostVisibleAllocated
                );

                if (m_hostVisibleHeld > m_hostVisiblePeak) {
                    m_counts.Add(
                        amount: (m_hostVisibleHeld - m_hostVisiblePeak),
                        kind: HostVisiblePeak
                    );
                    m_hostVisiblePeak = m_hostVisibleHeld;
                }

                return;
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

            if (role == GpuMemoryRole.HostVisibleDeviceLocal) {
                m_apertureHeld += bytes;
                m_counts.Add(
                    amount: bytes,
                    kind: ApertureAllocated
                );

                if (m_apertureHeld > m_aperturePeak) {
                    m_counts.Add(
                        amount: (m_apertureHeld - m_aperturePeak),
                        kind: AperturePeak
                    );
                    m_aperturePeak = m_apertureHeld;
                }
            }
        }
    }
    /// <summary>Counts the release of an allocation <see cref="CountAllocated"/> counted, at the size and in the row it was
    /// counted with; an object never counted on the device (zero, or a swapchain image) counts nothing.</summary>
    /// <param name="device">The native device that made the allocation.</param>
    /// <param name="allocation">The native object being released.</param>
    /// <returns>Whether the object was a counted allocation of the device.</returns>
    public bool CountReleased(nint device, nint allocation) {
        lock (m_gate) {
            if (!m_live.Remove(
                key: (device, allocation),
                value: out var entry
            )) {
                return false;
            }

            entry.Row.Release(bytes: entry.Bytes);
            entry.Family?.Release(bytes: entry.Bytes);
            // A block freed with placements still in it (a device's teardown frees every block whatever a loss left
            // bound) takes their names with it, so no family names a byte its row no longer holds.
            if (entry.Placed != 0) {
                foreach (var (token, placement) in m_placements) {
                    if (ReferenceEquals(objA: placement.Allocation, objB: entry)) {
                        _ = m_placements.Remove(key: token);
                        placement.Family.Release(bytes: placement.Bytes);
                    }
                }
            }

            if (!IsDeviceLocal(role: entry.Role)) {
                m_hostVisibleHeld -= entry.Bytes;
                m_counts.Add(
                    amount: entry.Bytes,
                    kind: HostVisibleReleased
                );

                return true;
            }

            m_held -= entry.Bytes;
            m_counts.Add(
                amount: entry.Bytes,
                kind: Released
            );

            if (entry.Role == GpuMemoryRole.HostVisibleDeviceLocal) {
                m_apertureHeld -= entry.Bytes;
                m_counts.Add(
                    amount: entry.Bytes,
                    kind: ApertureReleased
                );
            }

            return true;
        }
    }
    /// <summary>Names the part of a shared block one placed resource holds to the resource's family, in the segment the
    /// block was placed in. The name adds nothing to the counts: it is a part of the block's segment row.</summary>
    /// <param name="device">The native device that counted the block.</param>
    /// <param name="allocation">The block's native object, counted with no family (<see cref="CountAllocated"/>).</param>
    /// <param name="bytes">The bytes the resource holds in the block: its placement's size.</param>
    /// <param name="family">The resource's family (<see cref="GpuObjectName.MemoryFamily"/>).</param>
    /// <returns>The placement's token, which its release hands <see cref="CountUnplaced"/>; never zero.</returns>
    /// <exception cref="ArgumentException"><paramref name="family"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="family"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    /// <exception cref="InvalidOperationException">The block is not counted on the device, a family names it whole, or
    /// its placements would name more bytes than it holds.</exception>
    public long CountPlaced(nint device, nint allocation, long bytes, string family) {
        ArgumentException.ThrowIfNullOrEmpty(argument: family);
        ArgumentOutOfRangeException.ThrowIfNegative(value: bytes);

        lock (m_gate) {
            if (!m_live.TryGetValue(key: (device, allocation), value: out var entry)) {
                throw new InvalidOperationException(message: $"{Name}: a placement names block 0x{allocation:X}, which device 0x{device:X} never counted.");
            }
            if (entry.Family is not null) {
                throw new InvalidOperationException(message: $"{Name}: block 0x{allocation:X} is named whole by {entry.Family.Family}; a dedicated allocation holds no placements.");
            }
            if ((entry.Placed + bytes) > entry.Bytes) {
                throw new InvalidOperationException(message: $"{Name}: placements in block 0x{allocation:X} would name {(entry.Placed + bytes)} of its {entry.Bytes} bytes.");
            }

            var named = FamilyOf(family: family, segment: entry.Row.Segment);
            var token = ++m_tokens;

            entry.Placed += bytes;
            named.Allocate(bytes: bytes);
            m_placements.Add(key: token, value: new Placement(Allocation: entry, Bytes: bytes, Family: named));

            return token;
        }
    }
    /// <summary>Ends the name a placement's <see cref="CountPlaced"/> made, as its resource is released. A placement
    /// whose block was freed first (a device's teardown) ended with the block, and counts nothing here.</summary>
    /// <param name="token">The token <see cref="CountPlaced"/> returned.</param>
    /// <returns>Whether the placement still named its bytes.</returns>
    public bool CountUnplaced(long token) {
        lock (m_gate) {
            if (!m_placements.Remove(key: token, value: out var placement)) {
                return false;
            }

            placement.Allocation.Placed -= placement.Bytes;
            placement.Family.Release(bytes: placement.Bytes);

            return true;
        }
    }
    /// <summary>Ends a device's entries and its segments' registration at its teardown. Every allocation still counted
    /// on it was leaked by an owner that never released it: the entries are dropped, their bytes stay counted as held in
    /// their rows, and the teardown is refused with each one named.</summary>
    /// <param name="device">The native device being torn down.</param>
    /// <exception cref="InvalidOperationException">An allocation is still counted on <paramref name="device"/>; the
    /// message lists each one and its size.</exception>
    public void EndDevice(nint device) {
        List<(nint Allocation, long Bytes)>? leaked = null;

        lock (m_gate) {
            _ = m_devices.Remove(key: device);

            foreach (var ((owner, allocation), entry) in m_live) {
                if (owner == device) {
                    (leaked ??= []).Add(item: (allocation, entry.Bytes));
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
    /// <param name="kind">One of this source's kinds (<see cref="WorkKinds"/>).</param>
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

    // Hands back a grant whose allocation was not made: returned unattempted, or refused by the device.
    internal void Settle(GpuMemoryGrant grant, GpuMemorySettlement settlement) {
        if (settlement == GpuMemorySettlement.DeviceRefused) {
            _ = Interlocked.Increment(location: ref m_deviceRefusals);
            m_counts.Add(amount: 1L, kind: DeviceRefusalCount);
        }
        m_owners?.Settle(admission: grant.Admission, segment: grant.Segment, settlement: settlement);
    }

    // Under m_gate.
    private GpuMemorySegments SegmentsOfLocked(nint device) => (m_devices.TryGetValue(key: device, value: out var segments)
        ? segments
        : throw new InvalidOperationException(message: $"{Name}: device 0x{device:X} registered no memory segments; its backend registers them (BeginDevice) when it creates the device."));
    // Under m_gate: the segment's row, created with its observer the first time any device registers the segment.
    private SegmentRow RowOf(GpuMemorySegment segment) {
        if (!m_rows.TryGetValue(key: (segment.Index, segment.Pool), value: out var row)) {
            row = new SegmentRow(observer: m_owners?.OfSegment(segment: segment), segment: segment);
            m_rows.Add(key: (segment.Index, segment.Pool), value: row);
        }

        row.Segment = segment;

        return row;
    }
    // Under m_gate: a family's row in a segment, created with its observer the first time the family is placed there.
    private FamilyRow FamilyOf(string family, GpuMemorySegment segment) {
        if (!m_families.TryGetValue(key: (family, segment.Index, segment.Pool), value: out var row)) {
            row = new FamilyRow(family: family, observer: m_owners?.OfFamily(family: family, segment: segment), segment: RowOf(segment: segment));
            m_families.Add(key: (family, segment.Index, segment.Pool), value: row);
        }

        return row;
    }

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class Counts {
        internal static readonly WorkKind[] Kinds = [Allocated, Released, Peak, ApertureAllocated, ApertureReleased, AperturePeak, HostVisibleAllocated, HostVisibleReleased, HostVisiblePeak, Allocations, PlacementDisagreements, AdmissionRefusals, OverLimitGrants, DeviceRefusalCount];
    }
    // One counted allocation: its size, role and segment row, the family that names it whole, or else the bytes its
    // placements name. Written only under the ledger's lock.
    private sealed class Allocation(long bytes, GpuMemoryRole role, SegmentRow row, FamilyRow? family) {
        public long Bytes { get; } = bytes;
        public FamilyRow? Family { get; } = family;
        public GpuMemoryRole Role { get; } = role;
        public SegmentRow Row { get; } = row;

        public long Placed;
    }
    // One resource's placement in a shared block, named to its family.
    private readonly record struct Placement(Allocation Allocation, long Bytes, FamilyRow Family);
    // One row's device bytes: held now, the most held at once, and allocated and released over the process's life,
    // written only under the ledger's lock, which is also where its observer is called. A row named inside another
    // (a family's inside its segment's) adds what it holds to that row's Named.
    private abstract class HeldRow(IAllocationObserver? observer, SegmentRow? namedIn) {
        public long Allocated;
        public long Held;
        public long Peak;
        public long Released;

        public void Allocate(long bytes) {
            Allocated += bytes;
            Held += bytes;
            Peak = Math.Max(val1: Peak, val2: Held);
            if (namedIn is not null) { namedIn.Named += bytes; }
            observer?.Change(backing: MemoryBacking.DeviceBlocks, bytes: bytes);
        }
        public void Release(long bytes) {
            Released += bytes;
            Held -= bytes;
            if (namedIn is not null) { namedIn.Named -= bytes; }
            observer?.Change(backing: MemoryBacking.DeviceBlocks, bytes: -bytes);
        }
    }
    // One segment's row: the physical row of its device blocks.
    private sealed class SegmentRow(GpuMemorySegment segment, IAllocationObserver? observer) : HeldRow(namedIn: null, observer: observer) {
        public GpuMemorySegment Segment = segment;
        public long Disagreements;
        public long Named;
    }
    // One family's row in one segment, a part of that segment's row.
    private sealed class FamilyRow(string family, SegmentRow segment, IAllocationObserver? observer) : HeldRow(namedIn: segment, observer: observer) {
        public string Family { get; } = family;
        public SegmentRow Parent { get; } = segment;

        public GpuMemorySegment Segment => Parent.Segment;
    }
}
/// <summary>One segment's row of a backend's device memory ledger, in bytes.</summary>
/// <param name="Segment">The segment, with the pool its bytes are charged to.</param>
/// <param name="Held">The bytes counted allocations placed in the segment hold now.</param>
/// <param name="Named">The part of <paramref name="Held"/> the families name (<see cref="GpuFamilyLedger"/>), never more
/// than it.</param>
/// <param name="Peak">The most bytes they held at once.</param>
/// <param name="Allocated">The bytes allocated in the segment over the process's life.</param>
/// <param name="Released">The bytes released from the segment over the process's life.</param>
/// <param name="Disagreements">The allocations placed in the segment whose role names the other pool, on a discrete
/// adapter.</param>
public readonly record struct GpuSegmentLedger(GpuMemorySegment Segment, long Held, long Named, long Peak, long Allocated, long Released, long Disagreements) {
    /// <summary>Gets the held bytes no family names: room inside shared blocks and emptied blocks kept for reuse.</summary>
    public long Residual => (Held - Named);
}
/// <summary>One family's row of a backend's device memory ledger in one segment, in bytes: a part of that segment's
/// row.</summary>
/// <param name="Family">The family (<see cref="GpuObjectName.MemoryFamily"/>).</param>
/// <param name="Segment">The segment its resources were placed in.</param>
/// <param name="Held">The bytes its resources hold there now.</param>
/// <param name="Peak">The most they held at once.</param>
/// <param name="Allocated">The bytes its resources were given there over the process's life.</param>
/// <param name="Released">The bytes its resources gave back there over the process's life.</param>
public readonly record struct GpuFamilyLedger(string Family, GpuMemorySegment Segment, long Held, long Peak, long Allocated, long Released);
