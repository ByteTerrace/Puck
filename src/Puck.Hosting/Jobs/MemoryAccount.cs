using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;

namespace Puck.Hosting.Jobs;

/// <summary>The disjoint categories of owned bytes. An alias never adds another charge.</summary>
public enum MemoryCategory {
    /// <summary>Storage an active consumer holds.</summary>
    Live,
    /// <summary>Storage only a reclaimable cache claim holds.</summary>
    Cached,
    /// <summary>Storage of work that was retired and is waiting for its last borrower.</summary>
    Retirement,
    /// <summary>Bookkeeping storage beside the payloads it describes.</summary>
    Metadata,
}
/// <summary>A consistent reading of owned storage and future admission promises, in bytes.</summary>
public readonly record struct MemoryReading(long Live, long Reserved, long Cached, long Retirement, long Metadata, long Budget, long Headroom) {
    /// <summary>Gets physical bytes owned by participating subsystems; reservations are not physical allocations.</summary>
    public long Owned => checked((((Live + Cached) + Retirement) + Metadata));
}
/// <summary>
/// The process's admission authority and byte ledger. Owners report storage, never buffer addresses or references.
/// A reservation promises only this account's unused budget. The machine's free physical memory is an observation the
/// caller reads outside the account and passes in: it paces admission, and a caller that has nothing admitted admits
/// its next job regardless (<see cref="Owner.Reserve"/>), so a reading never holds work back forever.
/// <para>
/// The account carries pools (<see cref="PhysicalMemory"/>: host and device memory on a discrete adapter, the host's
/// alone on unified memory), each read as its physical rows (<see cref="ReadPools"/>): the device blocks the backends'
/// ledgers placed in it, and in host memory the native allocator's committed bytes, native storage outside the
/// allocator, the collector's committed bytes, and the runtime's and driver's private memory, which a page walk of the
/// process leaves once the other rows are taken away. The rows are disjoint and are the only figures that add. Every
/// charge names the row its storage lies in (<see cref="MemoryBacking"/>), so an owner names a part of exactly one row,
/// and each row prints its residual, what it holds beyond the parts its owners name. The device blocks are charged by
/// the ledgers' segment observers (<see cref="DeviceOwners"/>), and the measured rows are recorded samples
/// (<see cref="Measure"/>), each with its age; nothing here calls the collector, the allocator or the driver.
/// </para>
/// <para>
/// An admitted owner (<see cref="CreateOwner"/>) charges host memory and is what <see cref="Owner.TryReserve"/>
/// subtracts from the budget. An observed owner (<see cref="CreateObservedOwner"/>, <see cref="CreateSampledOwner"/>,
/// and the device families of <see cref="DeviceOwners"/>) names part of its row and stays outside that sum, on a
/// discrete adapter and on unified memory alike, so admission answers exactly as it would without it.
/// <see cref="Record"/> takes a device's memory reading and records each segment's budget and spend limit; it fires no
/// trim and changes no admission.
/// </para>
/// </summary>
public sealed class MemoryAccount {
    /// <summary>Gets the process account shared by mesh libraries, residencies and jobs.</summary>
    public static MemoryAccount Shared { get; } = new();

    private const int Backings = 4;

    private readonly Lock m_gate = new();
    private readonly long[] m_bytes = new long[4];
    // The device blocks the ledgers' segment observers charge, by pool: the physical rows of device memory.
    private readonly long[] m_deviceBlocks = new long[2];
    // What admitted and observed owners name, by pool and row (pool * Backings + backing).
    private readonly long[] m_named = new long[2 * Backings];
    private readonly HashSet<Owner> m_owners = [];
    // Serializes Measure, so each sampled owner's delta is taken from the value it last recorded.
    private readonly Lock m_sampleGate = new();
    private readonly List<SampledOwner> m_sampled = [];

    private long m_reserved;
    private long? m_budget;
    private long m_negativeResiduals;
    private GpuMemoryReading m_device = GpuMemoryReading.Absent;
    private MemorySegmentLimit[] m_segmentLimits = [];
    private HostMemorySample? m_sample;
    private long m_sampledAt;

    /// <summary>Requests that cache owners detach idle claims. Active consumer claims are unaffected.</summary>
    public event Action? TrimRequested;

    /// <summary>Asks participating caches to release their claims, outside the account lock. A cache whose claims belong
    /// to another thread defers the release to that thread (<see cref="DeferTrim"/>), so some storage can outlive the
    /// call; <see cref="DeferredTrims"/> counts those still waiting.</summary>
    public void TrimCaches() => TrimRequested?.Invoke();

    private int m_deferredTrims;

    /// <summary>Gets the trims a cache owner deferred to its own thread that have not run yet.</summary>
    public int DeferredTrims => Volatile.Read(location: ref m_deferredTrims);

    /// <summary>Records a trim request a cache owner will service on its own thread; it calls
    /// <see cref="CompleteDeferredTrim"/> once it has.</summary>
    public void DeferTrim() => _ = Interlocked.Increment(location: ref m_deferredTrims);
    /// <summary>Records that a deferred trim has run, or that its owner was disposed with it still waiting.</summary>
    public void CompleteDeferredTrim() => _ = Interlocked.Decrement(location: ref m_deferredTrims);

    /// <summary>Gets the machine-derived budget, leaving failure, cancellation and allocator headroom.</summary>
    public long DefaultBudget { get; } = Math.Max(val1: 0, val2: (((HostMemory.InstalledPhysicalBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) * 3) / 4));
    /// <summary>Gets the unspendable failure and allocator reserve.</summary>
    public long Headroom { get; } = Math.Clamp(((HostMemory.InstalledPhysicalBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes) / 32), (16L << 20), (512L << 20));

    /// <summary>Gets the default cache allowance, governed by the same authority as job admission.</summary>
    public long CacheBudget => Math.Clamp((DefaultBudget / 12), (16L << 20), (512L << 20));

    /// <summary>Lowers the admission budget, or restores the machine-derived value. Existing owners keep their storage;
    /// a budget lowered below the one before asks the caches to release their claims (<see cref="TrimCaches"/>).</summary>
    /// <param name="bytes">The budget, in bytes, at most <see cref="DefaultBudget"/>; <see langword="null"/> restores the
    /// machine-derived budget.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public void SetBudget(long? bytes) {
        if (bytes is < 0) { throw new ArgumentOutOfRangeException(paramName: nameof(bytes)); }
        bool lowered;

        lock (m_gate) {
            var before = (m_budget ?? DefaultBudget);

            m_budget = ((bytes is { } value) ? Math.Min(val1: value, val2: DefaultBudget) : null);
            lowered = ((m_budget ?? DefaultBudget) < before);
        }
        if (lowered) { TrimCaches(); }
    }
    /// <summary>Reads all categories atomically.</summary>
    /// <returns>The reading.</returns>
    public MemoryReading Read() {
        lock (m_gate) { return new(m_bytes[0], m_reserved, m_bytes[1], m_bytes[2], m_bytes[3], (m_budget ?? DefaultBudget), Headroom); }
    }
    /// <summary>Reads the account and every owner holding charged bytes or a reservation under the same lock.
    /// Empty idle owners are absent; disposed owners remain until their storage releases its last byte.</summary>
    /// <returns>A detached snapshot whose rows reconcile to its account categories and reservations.</returns>
    public MemoryOwnerSnapshot ReadOwners() {
        lock (m_gate) {
            var owners = new MemoryOwnerReading[m_owners.Count];
            var index = 0;

            foreach (var owner in m_owners) { owners[index++] = owner.Read(); }
            return new(Account: Read(), Owners: owners);
        }
    }
    /// <summary>Creates an explicit admitted owner, which charges host memory and is counted by
    /// <see cref="Owner.TryReserve"/>. It must outlive every allocation that reports to it.</summary>
    /// <param name="name">The owner's name, for diagnostics.</param>
    /// <param name="category">The category its charges count in until it is reclassified.</param>
    /// <returns>The owner.</returns>
    public Owner CreateOwner(string name, MemoryCategory category = MemoryCategory.Live) => new(account: this, category: category, kind: OwnerKind.Admitted, name: name, pool: PhysicalMemory.Host);
    /// <summary>Creates an observed owner of host storage: it names part of the row each of its charges names, outside the
    /// sum <see cref="Owner.TryReserve"/> subtracts, and cannot reserve. It must outlive every allocation that reports to
    /// it.</summary>
    /// <param name="name">The owner's name, for diagnostics.</param>
    /// <returns>The owner.</returns>
    public Owner CreateObservedOwner(string name) => new(account: this, category: MemoryCategory.Live, kind: OwnerKind.Observed, name: name, pool: PhysicalMemory.Host);
    /// <summary>Creates a sampled owner: an observed owner whose bytes are a figure its subsystem derives, such as the
    /// payload of a structure that never reports its growth, read each time the account is measured
    /// (<see cref="Measure"/>) and named in <paramref name="backing"/> from then until the next measure.</summary>
    /// <param name="name">The owner's name, for diagnostics.</param>
    /// <param name="backing">The host row its bytes lie in; not <see cref="MemoryBacking.DeviceBlocks"/>.</param>
    /// <param name="read">Returns the bytes the subsystem holds now, at least zero; called outside the account's lock on
    /// the thread that measures, so it must be safe to call from any thread.</param>
    /// <returns>The registration; disposing it releases its bytes and stops the reads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="backing"/> is
    /// <see cref="MemoryBacking.DeviceBlocks"/> or not a defined value.</exception>
    public IDisposable CreateSampledOwner(string name, MemoryBacking backing, Func<long> read) {
        ArgumentNullException.ThrowIfNull(argument: read);
        if (backing is not (MemoryBacking.NativeAllocator or MemoryBacking.NativeDirect or MemoryBacking.Managed)) {
            throw new ArgumentOutOfRangeException(actualValue: backing, message: "A sampled owner names a host row.", paramName: nameof(backing));
        }

        var sampled = new SampledOwner(account: this, backing: backing, owner: CreateObservedOwner(name: name), read: read);

        lock (m_sampleGate) { m_sampled.Add(item: sampled); }

        return sampled;
    }
    /// <summary>Returns what a backend's device memory ledger (<see cref="GpuDeviceMemoryWork"/>) reports to: for each
    /// segment the owner that is its physical row of device blocks, named <c>gpu.&lt;backend&gt;.&lt;segment&gt;</c>, and
    /// for each family and segment an observed owner that names the family's part of that row, named
    /// <c>gpu.&lt;backend&gt;.&lt;segment&gt;:&lt;family&gt;</c>, both in the segment's pool.</summary>
    /// <param name="backend">The backend's name, as its ledger is keyed (<c>vulkan</c>, <c>directx</c>).</param>
    /// <returns>The owners.</returns>
    public IGpuMemoryOwners DeviceOwners(string backend) => new LedgerOwners(account: this, backend: backend);
    /// <summary>Records a device's memory reading: the reading itself and each segment's budget and spend limit, the
    /// budget less the larger of a tenth of it and <see cref="Headroom"/>. It fires no trim and changes no admission.
    /// Recording the reading already held does nothing.</summary>
    /// <param name="reading">The device's reading (<see cref="IGpuDeviceContext.MemoryReading"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="reading"/> is <see langword="null"/>.</exception>
    public void Record(GpuMemoryReading reading) {
        ArgumentNullException.ThrowIfNull(argument: reading);
        lock (m_gate) {
            if (ReferenceEquals(objA: reading, objB: m_device)) { return; }
            var limits = new MemorySegmentLimit[reading.Segments.Count];

            for (var index = 0; (index < limits.Length); index++) {
                var segment = reading.Segments[index];
                var budget = ((long)Math.Min(val1: segment.Budget, val2: (ulong)long.MaxValue));

                limits[index] = new(Segment: segment.Segment, Budget: budget, SpendLimit: Math.Max(val1: 0L, val2: (budget - Math.Max(val1: (budget / 10), val2: Headroom))));
            }
            m_device = reading;
            m_segmentLimits = limits;
        }
    }
    /// <summary>Records a sample of the measured host rows and reads every sampled owner
    /// (<see cref="CreateSampledOwner"/>), so the rows and the parts named in them are read together. The sampled owners'
    /// reads run outside the account's lock; the sample and their bytes are recorded under it.</summary>
    /// <param name="sample">The sample, taken by the caller (<see cref="HostMemorySample.Take"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="sample"/> is <see langword="null"/>.</exception>
    public void Measure(HostMemorySample sample) {
        ArgumentNullException.ThrowIfNull(argument: sample);
        lock (m_sampleGate) {
            foreach (var sampled in m_sampled) { sampled.Sample(); }
            lock (m_gate) {
                m_sample = sample;
                m_sampledAt = Stopwatch.GetTimestamp();
            }
        }
    }
    /// <summary>Reads the pools, their physical rows with the parts their owners name, the recorded device reading and
    /// its segments' limits under the account's lock. A residual that sampling skew makes negative reads as nothing and is
    /// counted (<see cref="MemoryPoolsReading.NegativeResiduals"/>).</summary>
    /// <returns>The pools' reading. The device pool is present when a recorded reading of a discrete adapter names a
    /// segment in it or the ledgers placed a block in it.</returns>
    public MemoryPoolsReading ReadPools() {
        lock (m_gate) {
            var budget = (m_budget ?? DefaultBudget);
            var deviceLimits = m_segmentLimits.Where(predicate: static limit => (limit.Segment.Pool == PhysicalMemory.Device)).ToArray();
            var sample = m_sample;
            var age = ((sample is null) ? (TimeSpan?)null : Stopwatch.GetElapsedTime(startingTimestamp: m_sampledAt));
            var direct = Named(pool: PhysicalMemory.Host, backing: MemoryBacking.NativeDirect);
            var hostRows = new List<MemoryRowReading> {
                Row(backing: MemoryBacking.DeviceBlocks, bytes: m_deviceBlocks[((int)PhysicalMemory.Host)], measured: false, name: "device-blocks", pool: PhysicalMemory.Host),
                Row(backing: MemoryBacking.NativeAllocator, bytes: sample?.AllocatorCommitted, measured: true, name: "native-allocator", pool: PhysicalMemory.Host),
                Row(backing: MemoryBacking.NativeDirect, bytes: direct, measured: false, name: "native-direct", pool: PhysicalMemory.Host),
                Row(backing: MemoryBacking.Managed, bytes: sample?.ManagedCommitted, measured: true, name: "managed", pool: PhysicalMemory.Host),
            };
            // What the walk's private bytes hold beyond the rows that lie in them: the runtime's code and loader heaps,
            // stacks and the driver's private memory. Device blocks are left in it: whether a driver maps them into the
            // process's private bytes is the driver's, which the walk cannot tell.
            long? runtime = ((sample?.Walk is { } walk) && (sample.AllocatorCommitted is { } allocator))
                ? (walk.PrivateBytes - ((allocator + direct) + sample.ManagedCommitted))
                : null;

            if (runtime is < 0) { m_negativeResiduals++; runtime = 0; }
            hostRows.Add(item: new MemoryRowReading(Backing: null, Bytes: runtime, Measured: true, Name: "runtime-and-driver", Named: 0, Pool: PhysicalMemory.Host));

            var deviceBlocks = m_deviceBlocks[((int)PhysicalMemory.Device)];
            var host = new MemoryPoolReading(
                Admitted: m_bytes.Sum(),
                Budget: budget,
                Pool: PhysicalMemory.Host,
                Present: true,
                Reserved: m_reserved,
                Rows: hostRows,
                SpendLimit: Math.Max(val1: 0L, val2: (budget - Headroom))
            );
            var device = new MemoryPoolReading(
                Admitted: 0L,
                Budget: ((deviceLimits.Length == 0) ? null : deviceLimits.Sum(selector: static limit => limit.Budget)),
                Pool: PhysicalMemory.Device,
                Present: ((deviceLimits.Length != 0) || (deviceBlocks != 0)),
                Reserved: 0L,
                Rows: [Row(backing: MemoryBacking.DeviceBlocks, bytes: deviceBlocks, measured: false, name: "device-blocks", pool: PhysicalMemory.Device)],
                SpendLimit: ((deviceLimits.Length == 0) ? null : deviceLimits.Sum(selector: static limit => limit.SpendLimit))
            );

            return new(Device: device, DeviceReading: m_device, Host: host, NegativeResiduals: m_negativeResiduals, Sample: sample, SampleAge: age, SegmentLimits: m_segmentLimits);
        }
    }
    // Under m_gate.
    private long Named(PhysicalMemory pool, MemoryBacking backing) => m_named[((((int)pool) * Backings) + ((int)backing))];
    // Under m_gate: a row and what its owners name in it; a residual skew makes negative is counted.
    private MemoryRowReading Row(string name, PhysicalMemory pool, MemoryBacking backing, long? bytes, bool measured) {
        var named = Named(pool: pool, backing: backing);

        if ((bytes is { } held) && (held < named)) { m_negativeResiduals++; }

        return new(Backing: backing, Bytes: bytes, Measured: measured, Name: name, Named: named, Pool: pool);
    }

    internal enum OwnerKind {
        // Charges host memory, counted by TryReserve.
        Admitted,
        // Names part of a row, outside admission.
        Observed,
        // A ledger segment's physical row of device blocks.
        Segment,
    }

    /// <summary>One lifecycle's allocation totals and simultaneous-peak reservation, with no buffer registry. Each charge
    /// names the row its storage lies in: an admitted owner's and a host observed owner's lie in host rows
    /// (<see cref="MemoryBacking.NativeAllocator"/>, <see cref="MemoryBacking.NativeDirect"/>,
    /// <see cref="MemoryBacking.Managed"/>), or a host segment's device blocks for an observed one; a device pool's owner
    /// charges only <see cref="MemoryBacking.DeviceBlocks"/>.</summary>
    public sealed class Owner : IAllocationObserver, IDisposable {
        private readonly MemoryAccount m_account;
        private readonly PhysicalMemory m_pool;
        private readonly OwnerKind m_kind;
        private readonly long[] m_rows = new long[Backings];

        private MemoryCategory m_category;
        private long m_bytes;
        private long m_reservationPeak;
        private long m_ownedPeak;
        private bool m_closed;
        private bool m_tracked;

        /// <summary>Gets the owner for diagnostics.</summary>
        public string Name { get; }
        /// <summary>Gets the bytes the owner's storage holds now.</summary>
        public long Bytes {
            get {
                lock (m_account.m_gate) { return m_bytes; }
            }
        }

        internal Owner(MemoryAccount account, string name, MemoryCategory category, PhysicalMemory pool, OwnerKind kind) { m_account = account; Name = name; m_category = category; m_pool = pool; m_kind = kind; }

        private long Promise => Math.Max(val1: 0, val2: (m_reservationPeak - m_bytes));

        // Called only by the existing owner mutation doors, under the account lock. Empty owners need no account
        // reference; a disposed-but-charged owner stays visible until the storage's actual release reaches Change.
        private void Track() {
            var charged = ((m_bytes != 0) || (Promise != 0));

            if (charged == m_tracked) { return; }
            if (charged) { _ = m_account.m_owners.Add(item: this); } else { _ = m_account.m_owners.Remove(item: this); }
            m_tracked = charged;
        }

        internal MemoryOwnerReading Read() {
            lock (m_account.m_gate) {
                return new(Name, m_category, m_bytes, Promise, m_ownedPeak, m_reservationPeak, m_closed, m_pool, (m_kind != OwnerKind.Admitted), (m_kind == OwnerKind.Segment), new MemoryRowBytes(DeviceBlocks: m_rows[((int)MemoryBacking.DeviceBlocks)], Managed: m_rows[((int)MemoryBacking.Managed)], NativeAllocator: m_rows[((int)MemoryBacking.NativeAllocator)], NativeDirect: m_rows[((int)MemoryBacking.NativeDirect)]));
            }
        }

        /// <summary>Reserves the owner's simultaneous peak before its work starts, or lowers a reservation already held;
        /// false means defer without holding partial resources.</summary>
        /// <param name="peak">The most bytes the owner's storage holds at once while the work runs.</param>
        /// <param name="available">The machine's free physical memory, in bytes, as the caller read it outside the
        /// account (<see cref="HostMemory.AvailablePhysicalBytes"/>), or <see langword="null"/> to pace by the budget
        /// alone.</param>
        /// <returns><see langword="true"/> when the reservation is held.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
        /// <exception cref="InvalidOperationException">The owner is not admitted (<see cref="CreateObservedOwner"/>).</exception>
        public bool TryReserve(long peak, long? available = null) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);
                ThrowIfObserved();
                var extra = (Math.Max(val1: 0, val2: (peak - m_bytes)) - Promise);
                var spare = Math.Max(val1: 0, val2: ((((m_account.m_budget ?? m_account.DefaultBudget) - m_account.Headroom) - m_account.m_bytes.Sum()) - m_account.m_reserved));

                if (available is { } free) { spare = Math.Min(val1: spare, val2: Math.Max(val1: 0, val2: ((free - m_account.Headroom) - m_account.m_reserved))); }
                if ((extra > 0) && (extra > spare)) { return false; }
                m_account.m_reserved += extra;
                m_reservationPeak = peak;
                Track();
                return true;
            }
        }
        /// <summary>Reserves the owner's simultaneous peak whatever the budget holds, for the one job a caller must
        /// admit so that its work progresses when nothing else of it is admitted.</summary>
        /// <param name="peak">The most bytes the owner's storage holds at once while the work runs.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
        /// <exception cref="InvalidOperationException">The owner is not admitted (<see cref="CreateObservedOwner"/>).</exception>
        public void Reserve(long peak) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);
                ThrowIfObserved();
                m_account.m_reserved += (Math.Max(val1: 0, val2: (peak - m_bytes)) - Promise);
                m_reservationPeak = peak;
                Track();
            }
        }
        /// <summary>Releases the future promise after work ends; any still-owned storage remains accounted.</summary>
        public void ReleaseReservation() {
            lock (m_account.m_gate) { m_account.m_reserved -= Promise; m_reservationPeak = 0; Track(); }
        }
        /// <inheritdoc/>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="backing"/> is a row this owner's pool and kind
        /// cannot hold: device blocks charged to an admitted owner, or a host row charged to a device pool's owner or a
        /// segment's.</exception>
        /// <exception cref="InvalidOperationException">The change would release more bytes than the owner holds, overall
        /// or in <paramref name="backing"/>.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed and the change acquires bytes.</exception>
        public void Change(long bytes, MemoryBacking backing) {
            var deviceBlocks = (backing == MemoryBacking.DeviceBlocks);
            var holds = m_kind switch {
                OwnerKind.Segment => deviceBlocks,
                OwnerKind.Admitted => !deviceBlocks,
                _ => (deviceBlocks || (m_pool == PhysicalMemory.Host)),
            };

            if (!Enum.IsDefined(value: backing) || !holds) {
                throw new ArgumentOutOfRangeException(actualValue: backing, message: $"Memory owner {Name} cannot hold {backing} storage.", paramName: nameof(backing));
            }

            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: (m_closed && (bytes > 0)), instance: this);
                var next = checked((m_bytes + bytes));
                var row = checked((m_rows[((int)backing)] + bytes));

                if ((next < 0) || (row < 0)) { throw new InvalidOperationException(message: $"Memory owner {Name} released more {backing} bytes than it owns."); }
                m_account.m_reserved -= Promise;
                m_bytes = next;
                m_rows[((int)backing)] = row;
                m_ownedPeak = Math.Max(val1: m_ownedPeak, val2: next);
                switch (m_kind) {
                    case OwnerKind.Segment:
                        m_account.m_deviceBlocks[((int)m_pool)] += bytes;
                        break;
                    case OwnerKind.Admitted:
                        m_account.m_bytes[((int)m_category)] += bytes;
                        m_account.m_named[((((int)m_pool) * Backings) + ((int)backing))] += bytes;
                        break;
                    default:
                        m_account.m_named[((((int)m_pool) * Backings) + ((int)backing))] += bytes;
                        break;
                }
                m_account.m_reserved += Promise;
                Track();
            }
        }
        /// <summary>Moves the existing charge between categories without charging aliases.</summary>
        /// <param name="category">The category the owner's charges count in from now on.</param>
        public void Classify(MemoryCategory category) {
            lock (m_account.m_gate) {
                if (category == m_category) { return; }
                if (m_kind != OwnerKind.Admitted) { m_category = category; return; }
                m_account.m_bytes[((int)m_category)] -= m_bytes;
                m_category = category;
                m_account.m_bytes[((int)m_category)] += m_bytes;
            }
        }
        // An observed owner stands outside admission, so it neither reserves nor is reserved for.
        private void ThrowIfObserved() {
            if (m_kind != OwnerKind.Admitted) { throw new InvalidOperationException(message: $"Memory owner {Name} is observed: it names part of a row outside admission and reserves nothing."); }
        }
        /// <inheritdoc/>
        public void Dispose() {
            lock (m_account.m_gate) {
                ReleaseReservation();
                m_closed = true;
                // Charges are released by the storage's actual disposer, never inferred from an owner's shutdown.
            }
        }
    }

    // A sampled owner's registration: its owner, the row it names and the figure last recorded.
    private sealed class SampledOwner(MemoryAccount account, Owner owner, MemoryBacking backing, Func<long> read) : IDisposable {
        private long m_recorded;
        private bool m_disposed;

        // Under the account's sample gate.
        public void Sample() {
            var bytes = Math.Max(val1: 0L, val2: read());

            owner.Change(backing: backing, bytes: (bytes - m_recorded));
            m_recorded = bytes;
        }
        public void Dispose() {
            lock (account.m_sampleGate) {
                if (m_disposed) { return; }
                m_disposed = true;
                _ = account.m_sampled.Remove(item: this);
                owner.Change(backing: backing, bytes: -m_recorded);
                m_recorded = 0;
                owner.Dispose();
            }
        }
    }

    // The owners a backend's ledger reports to: a segment owner per segment, an observed owner per family and segment.
    private sealed class LedgerOwners(MemoryAccount account, string backend) : IGpuMemoryOwners {
        public IAllocationObserver OfSegment(GpuMemorySegment segment) =>
            new Owner(account: account, category: MemoryCategory.Live, kind: OwnerKind.Segment, name: $"gpu.{backend}.{segment.Name}", pool: segment.Pool);
        public IAllocationObserver OfFamily(GpuMemorySegment segment, string family) =>
            new Owner(account: account, category: MemoryCategory.Live, kind: OwnerKind.Observed, name: $"gpu.{backend}.{segment.Name}:{family}", pool: segment.Pool);
    }
}
