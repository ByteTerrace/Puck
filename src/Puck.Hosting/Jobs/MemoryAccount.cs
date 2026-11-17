using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Maths;

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
/// <para>
/// The account carries pools (<see cref="PhysicalMemory"/>: host and device memory on a discrete adapter, the host's
/// alone on unified memory). Each pool has a spend limit, the bytes it holds and the bytes it has promised, and one
/// arithmetic admits against it: an admission is granted when what the pool holds and has promised, with the admission
/// added, stays under the limit. Three doors ask it, and nothing else adds a promise: a host owner's
/// <see cref="Owner.TryReserve"/>, a device reservation's <see cref="DeviceReservation.TryReserve"/>, and the device
/// memory ledger's grant before each native allocation (<see cref="DeviceOwners"/>,
/// <see cref="IGpuMemoryOwners.Admit"/>), which also holds the allocation to its segment's own limit. Two admissions
/// pass a limit, and both are counted: the process's one forced job (<see cref="Owner.TryReserveForced"/>), so work
/// always settles, and storage whose class cannot give way (<see cref="MemoryClasses.GivesWay"/>), since refusing it
/// would end the presentation.
/// </para>
/// <para>
/// A pool holds its admitted owners' bytes (host memory) and the device blocks the backends' ledgers placed in it. The
/// host pool's limit is the budget, or the machine's free physical memory with what the pool already holds when that
/// is smaller, less <see cref="Headroom"/>; the account reads the free memory itself, at most every
/// <see cref="AvailableInterval"/>. A discrete adapter's pool is limited by its segments: each segment's limit is its
/// recorded budget less the larger of a tenth of it and the headroom (<see cref="Record"/>), lowered to what the segment
/// holds when the device refuses an allocation there until the budget next changes.
/// </para>
/// <para>
/// Each pool is also read as its physical rows (<see cref="ReadPools"/>): the device blocks, and in host memory the
/// native allocator's committed bytes, native storage outside the allocator, the collector's committed bytes, and the
/// runtime's and driver's private memory, which a page walk of the process leaves once the other rows are taken away.
/// The rows are disjoint and are the only figures that add. Every charge names the row its storage lies in
/// (<see cref="MemoryBacking"/>), and each row prints its residual, what it holds beyond the parts its owners name. A
/// device family (<see cref="DeviceOwners"/>) names part of its segment's row and adds nothing; the measured rows are
/// recorded samples (<see cref="Measure"/>), and nothing here calls the collector, the allocator or the driver.
/// </para>
/// </summary>
public sealed class MemoryAccount {
    /// <summary>Gets the process account shared by mesh libraries, residencies and jobs.</summary>
    public static MemoryAccount Shared { get; } = new();

    private const int Backings = 4;
    private const int Pools = 2;

    private readonly Lock m_gate = new();
    // The admitted host owners' bytes by category.
    private readonly long[] m_bytes = new long[4];
    // The device blocks the ledgers' segment observers charge, by pool: the physical rows of device memory.
    private readonly long[] m_deviceBlocks = new long[Pools];
    // What admitted owners and device families name, by pool and row (pool * Backings + backing).
    private readonly long[] m_named = new long[Pools * Backings];
    // The admitted host owners' bytes and promises by class, and the reserving device families' by pool and class.
    private readonly long[] m_ownerHeld = new long[MemoryClasses.Count];
    private readonly long[] m_ownerPromised = new long[MemoryClasses.Count];
    private readonly long[] m_familyHeld = new long[Pools * MemoryClasses.Count];
    private readonly long[] m_familyPromised = new long[Pools * MemoryClasses.Count];
    private readonly HashSet<Owner> m_owners = [];
    // The device memory each owner's families hold and are promised, by pool and owner (GpuObjectName.MemoryOwnerOf).
    private readonly Dictionary<(PhysicalMemory Pool, string Owner), FamilyEntry> m_families = [];
    private readonly Dictionary<(string Backend, int Index, PhysicalMemory Pool), SegmentState> m_segments = [];
    private readonly PoolState[] m_pools = [new(), new()];
    // Serializes Measure and Sample, so each sampled owner's delta is taken from the value it last recorded.
    private readonly Lock m_sampleGate = new();
    private readonly List<SampledOwner> m_sampled = [];
    private readonly Func<long?> m_readAvailable;

    private long m_reserved;
    private long? m_budget;
    private long m_negativeResiduals;
    private GpuMemoryReading m_device = GpuMemoryReading.Absent;
    private MemorySegmentLimit[] m_segmentLimits = [];
    private HostMemorySample? m_sample;
    private long m_sampledAt;
    private long m_ownersSampledAt;
    // The machine's free physical memory as last read, and when; null where the host reports none.
    private long? m_available;
    private long m_availableAt;
    // The owner holding the process's one forced admission, the bytes it was forced with, and the forced admissions made.
    private Owner? m_forced;
    private long m_forcedBytes;
    private long m_forcedJobs;
    // Whether an admission was refused or a limit fell under what its pool holds since the last trim, and when that
    // trim ran.
    private int m_pressure;
    private long m_pressureAt;
    private long m_pressureTrims;

    /// <summary>Initializes a new instance of the <see cref="MemoryAccount"/> class.</summary>
    /// <param name="availablePhysicalBytes">Reads the machine's free physical memory, in bytes, or
    /// <see langword="null"/> where the host reports none; <see langword="null"/> reads
    /// <see cref="HostMemory.AvailablePhysicalBytes"/>.</param>
    /// <param name="installedPhysicalBytes">The machine's installed physical memory, in bytes, which the budget and the
    /// headroom derive from; <see langword="null"/> reads the host's.</param>
    public MemoryAccount(Func<long?>? availablePhysicalBytes = null, long? installedPhysicalBytes = null) {
        var installed = (installedPhysicalBytes ?? HostMemory.InstalledPhysicalBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

        m_readAvailable = (availablePhysicalBytes ?? HostMemory.AvailablePhysicalBytes);
        DefaultBudget = Math.Max(val1: 0, val2: ((installed * 3) / 4));
        Headroom = Math.Clamp((installed / 32), (16L << 20), (512L << 20));
    }

    /// <summary>Gets how long a reading of the machine's free physical memory stands before an admission reads it
    /// again, in <see cref="Stopwatch"/> ticks: a quarter of a second.</summary>
    public static long AvailableInterval { get; } = (Stopwatch.Frequency / 4);
    /// <summary>Gets the shortest time between two trims pressure fires (<see cref="ServicePressure"/>), in
    /// <see cref="Stopwatch"/> ticks: one second.</summary>
    public static long PressureInterval { get; } = Stopwatch.Frequency;

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
    public long DefaultBudget { get; }
    /// <summary>Gets the unspendable failure and allocator reserve.</summary>
    public long Headroom { get; }

    /// <summary>Gets the default cache allowance, governed by the same authority as job admission.</summary>
    public long CacheBudget => Math.Clamp((DefaultBudget / 12), (16L << 20), (512L << 20));

    /// <summary>Lowers the host pool's budget, or restores the machine-derived value. Existing owners keep their
    /// storage; a budget lowered below the one before asks the caches to release their claims
    /// (<see cref="TrimCaches"/>).</summary>
    /// <param name="bytes">The budget, in bytes, at most <see cref="DefaultBudget"/>; <see langword="null"/> restores the
    /// machine-derived budget.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public void SetBudget(long? bytes) => SetBudget(bytes: bytes, pool: PhysicalMemory.Host);
    /// <summary>Lowers a pool's budget, or restores the value it derives. The host pool's budget is at most
    /// <see cref="DefaultBudget"/>; a discrete adapter's pool takes the value in place of the sum of its segments' spend
    /// limits, so a reading can be stood in for by hand. Nothing held is released: a budget below what a pool holds
    /// refuses the next admission that gives way, and a lowered budget asks the caches to release their claims.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="bytes">The budget, in bytes; <see langword="null"/> restores the derived budget.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public void SetBudget(PhysicalMemory pool, long? bytes) {
        if (bytes is < 0) { throw new ArgumentOutOfRangeException(paramName: nameof(bytes)); }
        bool lowered;

        lock (m_gate) {
            if (pool == PhysicalMemory.Host) {
                var before = (m_budget ?? DefaultBudget);

                m_budget = ((bytes is { } value) ? Math.Min(val1: value, val2: DefaultBudget) : null);
                lowered = ((m_budget ?? DefaultBudget) < before);
            } else {
                var before = LimitOf(pool: pool);

                m_pools[((int)pool)].BudgetOverride = bytes;
                lowered = ((LimitOf(pool: pool) is { } after) && ((before is not { } was) || (after < was)));
            }
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
    /// <summary>Creates an owner of host storage, which charges host memory and is counted by every admission. It must
    /// outlive every allocation that reports to it.</summary>
    /// <param name="name">The owner's name, for diagnostics.</param>
    /// <param name="category">The category its charges count in until it is reclassified.</param>
    /// <param name="memoryClass">What its storage is for; storage in the <see cref="MemoryCategory.Cached"/> category
    /// counts as <see cref="MemoryClass.Cached"/> whatever this says.</param>
    /// <returns>The owner.</returns>
    public Owner CreateOwner(string name, MemoryCategory category = MemoryCategory.Live, MemoryClass memoryClass = MemoryClass.Fixed) => new(account: this, category: category, family: null, kind: OwnerKind.Admitted, memoryClass: memoryClass, name: name, pool: PhysicalMemory.Host);
    /// <summary>Creates a sampled owner: an owner whose bytes are a figure its subsystem derives, such as the payload of
    /// a structure that never reports its growth, read each time the account is measured (<see cref="Measure"/>) or
    /// samples its owners (<see cref="Sample"/>) and charged in <paramref name="backing"/> from then until the next
    /// read.</summary>
    /// <param name="name">The owner's name, for diagnostics.</param>
    /// <param name="backing">The host row its bytes lie in; not <see cref="MemoryBacking.DeviceBlocks"/>.</param>
    /// <param name="read">Returns the bytes the subsystem holds now, at least zero; called outside the account's lock on
    /// the thread that measures, so it must be safe to call from any thread.</param>
    /// <param name="memoryClass">What its storage is for.</param>
    /// <returns>The registration; disposing it releases its bytes and stops the reads.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="read"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="backing"/> is
    /// <see cref="MemoryBacking.DeviceBlocks"/> or not a defined value.</exception>
    public IDisposable CreateSampledOwner(string name, MemoryBacking backing, Func<long> read, MemoryClass memoryClass = MemoryClass.Fixed) {
        ArgumentNullException.ThrowIfNull(argument: read);
        if (backing is not (MemoryBacking.NativeAllocator or MemoryBacking.NativeDirect or MemoryBacking.Managed)) {
            throw new ArgumentOutOfRangeException(actualValue: backing, message: "A sampled owner names a host row.", paramName: nameof(backing));
        }

        var sampled = new SampledOwner(account: this, backing: backing, owner: CreateOwner(memoryClass: memoryClass, name: name), read: read);

        lock (m_sampleGate) { m_sampled.Add(item: sampled); }

        return sampled;
    }
    /// <summary>Creates a reservation of device memory for the resources one owner creates: the peak those resources
    /// hold at once, promised in <paramref name="pool"/> before any of them is allocated. The owner's resources consume
    /// its promise as the ledger names them (<see cref="GpuDeviceMemoryWork.CountPlaced"/>, or a dedicated allocation's
    /// family), so a byte reserved and then allocated is counted once.</summary>
    /// <param name="owner">The owner the resources are created under: the text of their names' owner without its
    /// instance suffixes (<see cref="GpuObjectName.MemoryOwner"/>), which every part and instance of the owner shares.</param>
    /// <param name="pool">The pool the resources are expected in (<see cref="GpuMemorySegments.ExpectedPool(bool, GpuMemoryRole)"/>).</param>
    /// <param name="memoryClass">What the storage is for, which decides what a full pool answers the owner's
    /// allocations (<see cref="MemoryClasses.GivesWay"/>). Every reservation of one owner in one pool states the same
    /// class.</param>
    /// <returns>The reservation, holding nothing until <see cref="DeviceReservation.TryReserve"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="owner"/> is empty or names a part, or the owner is reserved
    /// in the pool under another class.</exception>
    public DeviceReservation ReserveDevice(string owner, PhysicalMemory pool, MemoryClass memoryClass) {
        ArgumentException.ThrowIfNullOrEmpty(argument: owner);
        if (owner.Contains(value: '/')) {
            throw new ArgumentException(message: $"A device reservation names an owner, not a family: {owner}.", paramName: nameof(owner));
        }
        lock (m_gate) {
            var entry = FamilyOf(family: owner, pool: pool);

            if (entry.Classified && (entry.Class != memoryClass)) {
                throw new ArgumentException(message: $"Device memory owner {owner} is reserved as {entry.Class}; an owner has one class in a pool.", paramName: nameof(memoryClass));
            }
            if (!entry.Classified) {
                entry.Class = memoryClass;
                entry.Classified = true;
                m_familyHeld[ClassIndex(memoryClass: memoryClass, pool: pool)] += entry.Held;
            }

            return new DeviceReservation(account: this, entry: entry);
        }
    }
    /// <summary>Returns what a backend's device memory ledger (<see cref="GpuDeviceMemoryWork"/>) reports to and asks:
    /// the admission door (<see cref="IGpuMemoryOwners.Admit"/>), for each segment the owner that is its physical row
    /// of device blocks, named <c>gpu.&lt;backend&gt;.&lt;segment&gt;</c>, and for each family and segment an owner that
    /// names the family's part of that row, named <c>gpu.&lt;backend&gt;.&lt;segment&gt;:&lt;family&gt;</c>, both in the
    /// segment's pool.</summary>
    /// <param name="backend">The backend's name, as its ledger is keyed (<c>vulkan</c>, <c>directx</c>).</param>
    /// <returns>The owners.</returns>
    public IGpuMemoryOwners DeviceOwners(string backend) => new LedgerOwners(account: this, backend: backend);
    /// <summary>Records a device's memory reading: the reading itself and each segment's budget and spend limit, the
    /// budget less the larger of a tenth of it and <see cref="Headroom"/>. A segment's limit lowered by a device refusal
    /// stands until its budget changes. A limit that falls under what its pool holds and has promised marks pressure
    /// (<see cref="ServicePressure"/>). Recording the reading already held does nothing.</summary>
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

                limits[index] = new(Segment: segment.Segment, Budget: budget, SpendLimit: SpendLimitOf(budget: budget));
            }
            m_device = reading;
            m_segmentLimits = limits;
            foreach (var state in m_segments.Values) {
                if ((state.RefusedLimit is not null) && (RecordedLimit(segment: state.Segment) is { } recorded) && (recorded.Budget != state.RefusedBudget)) { state.RefusedLimit = null; }
            }
            if (Pressed(pool: PhysicalMemory.Device) || Pressed(pool: PhysicalMemory.Host)) { m_pressure = 1; }
        }
    }
    /// <summary>Fires the trim pressure asks for: when an admission was refused, a device refused an allocation or a
    /// limit fell under what its pool holds since the last one, asks the caches to release their claims
    /// (<see cref="TrimCaches"/>), at most once every <see cref="PressureInterval"/>. The host calls it on its frame
    /// thread, at the top of a frame, so no trim runs inside an admission.</summary>
    /// <returns>Whether a trim was fired.</returns>
    public bool ServicePressure() {
        if (Volatile.Read(location: ref m_pressure) == 0) { return false; }

        var now = Stopwatch.GetTimestamp();

        lock (m_gate) {
            if ((m_pressureAt != 0) && ((now - m_pressureAt) < PressureInterval)) { return false; }
            m_pressure = 0;
            m_pressureAt = now;
            m_pressureTrims++;
        }
        TrimCaches();

        return true;
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
                m_ownersSampledAt = m_sampledAt;
            }
        }
    }
    /// <summary>Reads every sampled owner (<see cref="CreateSampledOwner"/>) when the last read is a second old, so the
    /// bytes they charge follow what their subsystems hold. The host calls it on its frame thread, at the top of a
    /// frame.</summary>
    public void Sample() {
        var now = Stopwatch.GetTimestamp();

        if ((Volatile.Read(location: ref m_ownersSampledAt) is var last) && (last != 0) && ((now - last) < Stopwatch.Frequency)) { return; }
        lock (m_sampleGate) {
            foreach (var sampled in m_sampled) { sampled.Sample(); }
            Volatile.Write(location: ref m_ownersSampledAt, value: now);
        }
    }
    /// <summary>Records the bytes of one class a presentation's selector holds its demand to in a pool, which the
    /// pools' reading carries (<see cref="MemoryAdmissionReading.Floor"/>): the floors, whose device bytes the host passes
    /// to the device's own reservation.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="bytes">The floors' bytes in the pool.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public void RecordFloor(PhysicalMemory pool, long bytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: bytes);
        lock (m_gate) { m_pools[((int)pool)].Floor = bytes; }
    }
    /// <summary>Gets the floors' bytes last recorded for a pool (<see cref="RecordFloor"/>).</summary>
    /// <param name="pool">The pool.</param>
    /// <returns>The bytes.</returns>
    public long FloorOf(PhysicalMemory pool) {
        lock (m_gate) { return m_pools[((int)pool)].Floor; }
    }
    /// <summary>Reads what one pool's admission holds it to, without allocating: its limit and the bytes it holds and
    /// has promised.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="limit">The pool's limit, in bytes.</param>
    /// <param name="held">The bytes the pool holds.</param>
    /// <param name="promised">The bytes the pool has promised.</param>
    /// <returns><see langword="false"/> where nothing bounds the pool, and <paramref name="limit"/> reads zero.</returns>
    public bool TryReadTotals(PhysicalMemory pool, out long limit, out long held, out long promised) {
        RefreshAvailable();
        lock (m_gate) {
            held = HeldOf(pool: pool);
            promised = PromisedOf(pool: pool);
            limit = (LimitOf(pool: pool) ?? 0L);

            return (LimitOf(pool: pool) is not null);
        }
    }
    /// <summary>Reads one pool's admission figures under the account's lock: its limit, what it holds and has promised,
    /// and both by class.</summary>
    /// <param name="pool">The pool.</param>
    /// <returns>The figures.</returns>
    public MemoryAdmissionReading ReadAdmission(PhysicalMemory pool) {
        RefreshAvailable();
        lock (m_gate) { return AdmissionOf(pool: pool); }
    }
    /// <summary>Reads the pools, their admission figures, their physical rows with the parts their owners name, the
    /// recorded device reading and its segments' limits under the account's lock. A residual that sampling skew makes
    /// negative reads as nothing and is counted (<see cref="MemoryPoolsReading.NegativeResiduals"/>).</summary>
    /// <returns>The pools' reading. The device pool is present when a recorded reading of a discrete adapter names a
    /// segment in it or the ledgers placed a block in it.</returns>
    public MemoryPoolsReading ReadPools() {
        RefreshAvailable();
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
                Admission: AdmissionOf(pool: PhysicalMemory.Host),
                Admitted: m_bytes.Sum(),
                Budget: budget,
                Pool: PhysicalMemory.Host,
                Present: true,
                Reserved: m_reserved,
                Rows: hostRows
            );
            var device = new MemoryPoolReading(
                Admission: AdmissionOf(pool: PhysicalMemory.Device),
                Admitted: 0L,
                Budget: (m_pools[((int)PhysicalMemory.Device)].BudgetOverride ?? ((deviceLimits.Length == 0) ? null : deviceLimits.Sum(selector: static limit => limit.Budget))),
                Pool: PhysicalMemory.Device,
                Present: ((deviceLimits.Length != 0) || (deviceBlocks != 0)),
                Reserved: 0L,
                Rows: [Row(backing: MemoryBacking.DeviceBlocks, bytes: deviceBlocks, measured: false, name: "device-blocks", pool: PhysicalMemory.Device)]
            );
            var segments = new List<MemorySegmentAdmission>(capacity: m_segments.Count);

            foreach (var ((backend, _, _), state) in m_segments) {
                segments.Add(item: new(Backend: backend, Granted: state.Granted, Held: state.Owner.HeldLocked, Limit: EffectiveLimit(state: state), Peak: state.Owner.PeakLocked, RefusedLimit: state.RefusedLimit, Segment: state.Segment));
            }
            segments.Sort(comparison: static (left, right) => ((string.CompareOrdinal(strA: left.Backend, strB: right.Backend) is var order and not 0) ? order : left.Segment.Index.CompareTo(value: right.Segment.Index)));

            return new(Device: device, DeviceReading: m_device, Host: host, NegativeResiduals: m_negativeResiduals, Sample: sample, SampleAge: age, SegmentAdmissions: segments, SegmentLimits: m_segmentLimits);
        }
    }

    // The budget less the larger of a tenth of it and the headroom: what may be spent of a segment's budget.
    private long SpendLimitOf(long budget) => Math.Max(val1: 0L, val2: (budget - Math.Max(val1: (budget / 10), val2: Headroom)));
    // Reads the machine's free physical memory again once the last reading is AvailableInterval old, outside the
    // account's lock, so the host pool's limit follows the machine without an admission calling the host each time.
    private void RefreshAvailable() {
        var now = Stopwatch.GetTimestamp();
        var last = Volatile.Read(location: ref m_availableAt);

        if ((last != 0) && ((now - last) < AvailableInterval)) { return; }

        var available = m_readAvailable();

        lock (m_gate) {
            m_available = available;
            Volatile.Write(location: ref m_availableAt, value: Math.Max(val1: 1L, val2: now));
            if (Pressed(pool: PhysicalMemory.Host)) { m_pressure = 1; }
        }
    }
    // Under m_gate.
    private long Named(PhysicalMemory pool, MemoryBacking backing) => m_named[((((int)pool) * Backings) + ((int)backing))];
    private static int ClassIndex(PhysicalMemory pool, MemoryClass memoryClass) => ((((int)pool) * MemoryClasses.Count) + ((int)memoryClass));
    // Under m_gate: a row and what its owners name in it; a residual skew makes negative is counted.
    private MemoryRowReading Row(string name, PhysicalMemory pool, MemoryBacking backing, long? bytes, bool measured) {
        var named = Named(pool: pool, backing: backing);

        if ((bytes is { } held) && (held < named)) { m_negativeResiduals++; }

        return new(Backing: backing, Bytes: bytes, Measured: measured, Name: name, Named: named, Pool: pool);
    }
    // Under m_gate: what a pool holds, the bytes every admission counts against its limit.
    private long HeldOf(PhysicalMemory pool) => (m_deviceBlocks[((int)pool)] + ((pool == PhysicalMemory.Host) ? m_bytes.Sum() : 0L));
    // Under m_gate: what a pool has promised: its host owners' reservations, its device families' and the grants whose
    // native allocation has not been counted yet.
    private long PromisedOf(PhysicalMemory pool) {
        var state = m_pools[((int)pool)];

        return ((state.Granted + state.FamilyPromised) + ((pool == PhysicalMemory.Host) ? m_reserved : 0L));
    }
    // Under m_gate: a pool's spend limit, or null where nothing bounds it (a device pool no reading names).
    private long? LimitOf(PhysicalMemory pool) {
        if (pool == PhysicalMemory.Host) {
            var budget = (m_budget ?? DefaultBudget);

            if (m_available is { } available) { budget = Math.Min(val1: budget, val2: available.AddSaturating(right: HeldOf(pool: pool))); }

            return Math.Max(val1: 0L, val2: (budget - Headroom));
        }
        if (m_pools[((int)pool)].BudgetOverride is { } forced) { return forced; }

        long? limit = null;

        foreach (var recorded in m_segmentLimits) {
            if (recorded.Segment.Pool != pool) { continue; }

            var spend = recorded.SpendLimit;

            foreach (var state in m_segments.Values) {
                if ((state.Segment == recorded.Segment) && (state.RefusedLimit is { } refused)) { spend = Math.Min(val1: spend, val2: refused); }
            }
            limit = ((limit ?? 0L) + spend);
        }

        return limit;
    }
    // Under m_gate: the room a pool has left for a new promise; null where nothing bounds it. Negative when the pool
    // holds and has promised more than its limit.
    private long? RoomOf(PhysicalMemory pool) => ((LimitOf(pool: pool) is { } limit) ? ((limit - HeldOf(pool: pool)) - PromisedOf(pool: pool)) : null);
    // Under m_gate: whether a pool has room for extra bytes of promise.
    private bool Fits(PhysicalMemory pool, long extra) => ((extra <= 0L) || (RoomOf(pool: pool) is not { } room) || (extra <= room));
    // Under m_gate.
    private MemorySegmentLimit? RecordedLimit(GpuMemorySegment segment) {
        foreach (var recorded in m_segmentLimits) {
            if (recorded.Segment == segment) { return recorded; }
        }

        return null;
    }
    // Under m_gate: a segment's limit: its recorded spend limit, or its size's where no reading names it yet, held to
    // what it held when the device last refused an allocation in it; null where nothing bounds it.
    private long? EffectiveLimit(SegmentState state) {
        long? limit = ((RecordedLimit(segment: state.Segment) is { } recorded)
            ? recorded.SpendLimit
            : ((state.Segment.Bytes != 0UL) ? SpendLimitOf(budget: ((long)Math.Min(val1: state.Segment.Bytes, val2: (ulong)long.MaxValue))) : null));

        return ((state.RefusedLimit is { } refused) ? Math.Min(val1: (limit ?? long.MaxValue), val2: refused) : limit);
    }
    // Under m_gate: the entry of the owner a family belongs to.
    private FamilyEntry FamilyOf(PhysicalMemory pool, string family) {
        var owner = GpuObjectName.MemoryOwnerOf(family: family);

        if (!m_families.TryGetValue(key: (pool, owner), value: out var entry)) {
            entry = new FamilyEntry(family: owner, pool: pool);
            m_families.Add(key: (pool, owner), value: entry);
        }

        return entry;
    }
    // Under m_gate: moves a family's held bytes or reserved peak, keeping the pool's and the class's promises with it.
    private void ChangeFamily(FamilyEntry entry, long held, long peak) {
        var before = entry.Promise;

        entry.Held += held;
        entry.Peak += peak;

        var promise = (entry.Promise - before);

        m_pools[((int)entry.Pool)].FamilyPromised += promise;
        if (entry.Classified) {
            var index = ClassIndex(memoryClass: entry.Class, pool: entry.Pool);

            m_familyHeld[index] += held;
            m_familyPromised[index] += promise;
        }
    }
    // Under m_gate.
    private MemoryAdmissionReading AdmissionOf(PhysicalMemory pool) {
        var state = m_pools[((int)pool)];
        var held = new long[MemoryClasses.Count];
        var promised = new long[MemoryClasses.Count];
        var families = 0L;

        for (var index = 0; (index < MemoryClasses.Count); index++) {
            var family = m_familyHeld[ClassIndex(memoryClass: ((MemoryClass)index), pool: pool)];

            families += family;
            held[index] = (family + ((pool == PhysicalMemory.Host) ? m_ownerHeld[index] : 0L));
            promised[index] = (m_familyPromised[ClassIndex(memoryClass: ((MemoryClass)index), pool: pool)] + ((pool == PhysicalMemory.Host) ? m_ownerPromised[index] : 0L));
        }
        // The device blocks no reserving family names are fixed: targets, tables, the room inside shared blocks.
        held[((int)MemoryClass.Fixed)] += (m_deviceBlocks[((int)pool)] - families);
        promised[((int)MemoryClass.Fixed)] += state.Granted;

        var total = HeldOf(pool: pool);

        return new(
            ClassHeld: held,
            ClassPromised: promised,
            DeviceRefusals: state.DeviceRefusals,
            Floor: state.Floor,
            Forced: (((pool == PhysicalMemory.Host) && (m_forced is not null)) ? m_forcedBytes : 0L),
            ForcedJobs: ((pool == PhysicalMemory.Host) ? m_forcedJobs : 0L),
            Held: total,
            Limit: LimitOf(pool: pool),
            OverLimitBytes: state.OverLimitBytes,
            OverLimitGrants: state.OverLimitGrants,
            Peak: state.Peak,
            Pool: pool,
            PressureTrims: m_pressureTrims,
            Promised: PromisedOf(pool: pool),
            Refusals: state.Refusals,
            Retired: (state.Retiring + ((pool == PhysicalMemory.Host) ? m_bytes[((int)MemoryCategory.Retirement)] : 0L))
        );
    }
    // Under m_gate: counts a refused admission. A refusal is admission working, not pressure: pressure is a limit that
    // fell under what its pool holds, a device that refused, or storage admitted past a limit.
    private void Refused(PhysicalMemory pool) => m_pools[((int)pool)].Refusals++;
    // Under m_gate: whether a pool holds and has promised more than its limit beyond the forced job.
    private bool Pressed(PhysicalMemory pool) => ((LimitOf(pool: pool) is { } limit) && (((HeldOf(pool: pool) + PromisedOf(pool: pool)) - (((pool == PhysicalMemory.Host) && (m_forced is not null)) ? m_forcedBytes : 0L)) > limit));
    // Under m_gate: keeps the most a pool has held at once.
    private void RaisePeak(PhysicalMemory pool) {
        var state = m_pools[((int)pool)];

        state.Peak = Math.Max(val1: state.Peak, val2: HeldOf(pool: pool));
    }
    // The ledger's door: whether a device allocation may be made, and the promise it stands as until it settles.
    private GpuMemoryAdmission Admit(string backend, GpuMemorySegment segment, long bytes, string? payer, long payerBytes) {
        RefreshAvailable();
        lock (m_gate) {
            var pool = segment.Pool;
            var entry = (((payer is not null) && m_families.TryGetValue(key: (pool, GpuObjectName.MemoryOwnerOf(family: payer)), value: out var found)) ? found : null);
            // What the payer's reservation already promised is not promised again.
            var net = (bytes - Math.Min(val1: payerBytes, val2: (entry?.Promise ?? 0L)));
            var state = m_pools[((int)pool)];
            var segmentState = (m_segments.TryGetValue(key: (backend, segment.Index, pool), value: out var known) ? known : null);
            var fits = Fits(extra: net, pool: pool);

            if (fits && (segmentState is not null) && (EffectiveLimit(state: segmentState) is { } limit)) {
                fits = ((bytes <= 0L) || ((segmentState.Owner.HeldLocked + segmentState.Granted + bytes) <= limit));
            }

            var verdict = GpuMemoryVerdict.Granted;

            if (!fits) {
                // An owner's storage gives way only past the floor its reservations state: what it cannot run without is
                // admitted like fixed storage.
                if ((entry is { Classified: true }) && MemoryClasses.GivesWay(memoryClass: entry.Class) && ((entry.Held + bytes) > entry.Floor)) {
                    Refused(pool: pool);

                    return new(Bytes: bytes, Net: net, Verdict: GpuMemoryVerdict.Refused);
                }
                verdict = GpuMemoryVerdict.OverLimit;
                state.OverLimitGrants++;
                state.OverLimitBytes += net;
                m_pressure = 1;
            }
            state.Granted += net;
            if (segmentState is not null) { segmentState.Granted += bytes; }

            return new(Bytes: bytes, Net: net, Verdict: verdict);
        }
    }
    private void Settle(string backend, GpuMemorySegment segment, GpuMemoryAdmission admission, GpuMemorySettlement settlement) {
        lock (m_gate) {
            var state = m_pools[((int)segment.Pool)];

            state.Granted -= admission.Net;
            if (!m_segments.TryGetValue(key: (backend, segment.Index, segment.Pool), value: out var segmentState)) { return; }
            segmentState.Granted -= admission.Bytes;
            if (settlement != GpuMemorySettlement.DeviceRefused) { return; }
            // The device said no under the limit the reading gave: the segment holds what it can until its budget is
            // read differently.
            segmentState.RefusedLimit = segmentState.Owner.HeldLocked;
            segmentState.RefusedBudget = (RecordedLimit(segment: segment)?.Budget ?? -1L);
            state.DeviceRefusals++;
            m_pressure = 1;
        }
    }

    internal enum OwnerKind {
        // Charges host memory, counted by every admission.
        Admitted,
        // Names part of a segment's row of device blocks, and consumes its family's reservation.
        Family,
        // A ledger segment's physical row of device blocks.
        Segment,
    }

    // One pool's admission state beyond what its owners hold. Written under the account's lock.
    private sealed class PoolState {
        public long? BudgetOverride;
        public long DeviceRefusals;
        public long FamilyPromised;
        public long Floor;
        public long Granted;
        public long OverLimitBytes;
        public long OverLimitGrants;
        public long Peak;
        public long Refusals;
        public long Retiring;
    }
    // One ledger segment's admission state: its row's owner, the grants in flight in it, and the limit a device refusal
    // left with the budget it was refused under.
    private sealed class SegmentState(GpuMemorySegment segment, Owner owner) {
        public long Granted;
        public Owner Owner { get; } = owner;
        public long RefusedBudget;
        public long? RefusedLimit;
        public GpuMemorySegment Segment { get; } = segment;
    }
    // One owner's device memory in one pool, across its families, segments and backends: what its resources hold, the
    // peak its reservations promise together, and its class once a reservation states one.
    internal sealed class FamilyEntry(PhysicalMemory pool, string family) {
        public MemoryClass Class;
        public bool Classified;
        public string Family { get; } = family;
        // The bytes its reservations state it cannot run without, together.
        public long Floor;
        public long Held;
        public long Peak;
        public PhysicalMemory Pool { get; } = pool;
        public long Promise => Math.Max(val1: 0L, val2: (Peak - Held));
    }

    /// <summary>One lifecycle's allocation totals and simultaneous-peak reservation, with no buffer registry. Each charge
    /// names the row its storage lies in: a host owner's lie in host rows
    /// (<see cref="MemoryBacking.NativeAllocator"/>, <see cref="MemoryBacking.NativeDirect"/>,
    /// <see cref="MemoryBacking.Managed"/>); a device family's and a segment's are
    /// <see cref="MemoryBacking.DeviceBlocks"/>.</summary>
    public sealed class Owner : IAllocationObserver, IDisposable {
        private readonly MemoryAccount m_account;
        private readonly PhysicalMemory m_pool;
        private readonly OwnerKind m_kind;
        private readonly MemoryClass m_class;
        private readonly FamilyEntry? m_family;
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

        internal Owner(MemoryAccount account, string name, MemoryCategory category, PhysicalMemory pool, OwnerKind kind, MemoryClass memoryClass, FamilyEntry? family) { m_account = account; Name = name; m_category = category; m_pool = pool; m_kind = kind; m_class = memoryClass; m_family = family; }

        // Under the account's lock.
        internal long HeldLocked => m_bytes;
        internal long PeakLocked => m_ownedPeak;

        private long Promise => Math.Max(val1: 0, val2: (m_reservationPeak - m_bytes));
        // The class the owner's bytes count in: cached storage is the cache class whatever the owner was created as.
        private MemoryClass EffectiveClass => ((m_category == MemoryCategory.Cached) ? MemoryClass.Cached : m_class);

        // Called only by the existing owner mutation doors, under the account lock. Empty owners need no account
        // reference; a disposed-but-charged owner stays visible until the storage's actual release reaches Change.
        private void Track() {
            var charged = ((m_bytes != 0) || (Promise != 0));

            if (charged == m_tracked) { return; }
            if (charged) { _ = m_account.m_owners.Add(item: this); } else { _ = m_account.m_owners.Remove(item: this); }
            m_tracked = charged;
        }
        // Under the account's lock: sets the reserved peak, moving the account's and the class's promises with it.
        private void SetPeak(long peak) {
            var before = Promise;

            m_reservationPeak = peak;

            var change = (Promise - before);

            m_account.m_reserved += change;
            m_account.m_ownerPromised[((int)EffectiveClass)] += change;
            Track();
        }

        internal MemoryOwnerReading Read() {
            lock (m_account.m_gate) {
                return new(Name, m_category, m_bytes, Promise, m_ownedPeak, m_reservationPeak, m_closed, m_pool, (m_kind != OwnerKind.Admitted), (m_kind == OwnerKind.Segment), new MemoryRowBytes(DeviceBlocks: m_rows[((int)MemoryBacking.DeviceBlocks)], Managed: m_rows[((int)MemoryBacking.Managed)], NativeAllocator: m_rows[((int)MemoryBacking.NativeAllocator)], NativeDirect: m_rows[((int)MemoryBacking.NativeDirect)]), EffectiveClass, ReferenceEquals(objA: m_account.m_forced, objB: this));
            }
        }

        /// <summary>Reserves the owner's simultaneous peak before its work starts, or lowers a reservation already held;
        /// false means defer without holding partial resources. The promise is taken under the account's lock, so two
        /// owners cannot be promised the same room.</summary>
        /// <param name="peak">The most bytes the owner's storage holds at once while the work runs.</param>
        /// <returns><see langword="true"/> when the reservation is held.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
        /// <exception cref="InvalidOperationException">The owner is a device family or a segment's row.</exception>
        public bool TryReserve(long peak) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            m_account.RefreshAvailable();
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);
                ThrowIfNotAdmitted();
                if (!m_account.Fits(extra: (Math.Max(val1: 0, val2: (peak - m_bytes)) - Promise), pool: PhysicalMemory.Host)) {
                    m_account.Refused(pool: PhysicalMemory.Host);
                    return false;
                }
                SetPeak(peak: peak);
                return true;
            }
        }
        /// <summary>Reserves the owner's simultaneous peak whatever the pool holds, as the process's one forced job: the
        /// job a caller with nothing admitted must run so that its work settles. One owner in the process holds the
        /// allowance at a time, from this call until its reservation is released (<see cref="ReleaseReservation"/>) or
        /// it is disposed; the bytes forced are counted (<see cref="MemoryAdmissionReading.Forced"/>).</summary>
        /// <param name="peak">The most bytes the owner's storage holds at once while the work runs.</param>
        /// <returns><see langword="true"/> when the reservation is held; <see langword="false"/> when another owner holds
        /// the forced allowance, and nothing was reserved.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
        /// <exception cref="InvalidOperationException">The owner is a device family or a segment's row.</exception>
        public bool TryReserveForced(long peak) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);
                ThrowIfNotAdmitted();
                if ((m_account.m_forced is { } holder) && !ReferenceEquals(objA: holder, objB: this)) { return false; }
                if (m_account.m_forced is null) { m_account.m_forcedJobs++; }
                m_account.m_forced = this;
                m_account.m_forcedBytes = peak;
                SetPeak(peak: peak);
                return true;
            }
        }
        /// <summary>Releases the future promise after work ends, and the forced allowance if the owner holds it; any
        /// still-owned storage remains accounted.</summary>
        public void ReleaseReservation() {
            lock (m_account.m_gate) {
                if (m_kind == OwnerKind.Admitted) { SetPeak(peak: 0); }
                if (ReferenceEquals(objA: m_account.m_forced, objB: this)) {
                    m_account.m_forced = null;
                    m_account.m_forcedBytes = 0;
                }
            }
        }
        /// <inheritdoc/>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="backing"/> is a row this owner cannot hold:
        /// device blocks charged to a host owner, or a host row charged to a device family or a segment.</exception>
        /// <exception cref="InvalidOperationException">The change would release more bytes than the owner holds, overall
        /// or in <paramref name="backing"/>.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed and the change acquires bytes.</exception>
        public void Change(long bytes, MemoryBacking backing) {
            var deviceBlocks = (backing == MemoryBacking.DeviceBlocks);

            if (!Enum.IsDefined(value: backing) || (deviceBlocks == (m_kind == OwnerKind.Admitted))) {
                throw new ArgumentOutOfRangeException(actualValue: backing, message: $"Memory owner {Name} cannot hold {backing} storage.", paramName: nameof(backing));
            }

            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: (m_closed && (bytes > 0)), instance: this);
                var next = checked((m_bytes + bytes));
                var row = checked((m_rows[((int)backing)] + bytes));

                if ((next < 0) || (row < 0)) { throw new InvalidOperationException(message: $"Memory owner {Name} released more {backing} bytes than it owns."); }

                var promise = Promise;

                m_bytes = next;
                m_rows[((int)backing)] = row;
                m_ownedPeak = Math.Max(val1: m_ownedPeak, val2: next);
                switch (m_kind) {
                    case OwnerKind.Segment:
                        m_account.m_deviceBlocks[((int)m_pool)] += bytes;
                        m_account.RaisePeak(pool: m_pool);
                        break;
                    case OwnerKind.Admitted:
                        m_account.m_bytes[((int)m_category)] += bytes;
                        m_account.m_named[((((int)m_pool) * Backings) + ((int)backing))] += bytes;
                        m_account.m_ownerHeld[((int)EffectiveClass)] += bytes;
                        m_account.m_reserved += (Promise - promise);
                        m_account.m_ownerPromised[((int)EffectiveClass)] += (Promise - promise);
                        m_account.RaisePeak(pool: m_pool);
                        break;
                    default:
                        m_account.m_named[((((int)m_pool) * Backings) + ((int)backing))] += bytes;
                        m_account.ChangeFamily(entry: m_family!, held: bytes, peak: 0L);
                        break;
                }
                Track();
            }
        }
        /// <summary>Moves the existing charge between categories without charging aliases.</summary>
        /// <param name="category">The category the owner's charges count in from now on.</param>
        public void Classify(MemoryCategory category) {
            lock (m_account.m_gate) {
                if (category == m_category) { return; }
                if (m_kind != OwnerKind.Admitted) { m_category = category; return; }

                var promise = Promise;

                m_account.m_bytes[((int)m_category)] -= m_bytes;
                m_account.m_ownerHeld[((int)EffectiveClass)] -= m_bytes;
                m_account.m_ownerPromised[((int)EffectiveClass)] -= promise;
                m_category = category;
                m_account.m_bytes[((int)m_category)] += m_bytes;
                m_account.m_ownerHeld[((int)EffectiveClass)] += m_bytes;
                m_account.m_ownerPromised[((int)EffectiveClass)] += promise;
            }
        }
        // A device family names part of a row and a segment is a row: neither reserves; a family's reservations are
        // DeviceReservation's.
        private void ThrowIfNotAdmitted() {
            if (m_kind != OwnerKind.Admitted) { throw new InvalidOperationException(message: $"Memory owner {Name} names device blocks; device memory is reserved through MemoryAccount.ReserveDevice."); }
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

    /// <summary>
    /// One holder's reservation of device memory for the resources an owner creates in a pool
    /// (<see cref="ReserveDevice"/>): the most bytes those resources hold at once. The owner's promise is what its
    /// reservations' peaks together exceed what its resources hold, so an allocation the reservation covers adds
    /// nothing to what the pool has promised. A growth by replacement reserves the old and the new object together and
    /// lowers the reservation once the old one is released. The exactness stops at the owner: every instance of an
    /// owner shares one entry, so a holder that keeps more than its own peak hides as much of another instance's
    /// promise.
    /// </summary>
    public sealed class DeviceReservation : IDisposable {
        private readonly MemoryAccount m_account;
        private readonly FamilyEntry m_entry;

        private long m_peak;
        private long m_floor;
        private long m_retiring;
        private bool m_closed;

        internal DeviceReservation(MemoryAccount account, FamilyEntry entry) { m_account = account; m_entry = entry; }

        /// <summary>Gets what the reservation's storage is for.</summary>
        public MemoryClass Class => m_entry.Class;
        /// <summary>Gets the owner whose resources consume the reservation.</summary>
        public string Owner => m_entry.Family;
        /// <summary>Gets the peak reserved now, in bytes.</summary>
        public long Peak {
            get {
                lock (m_account.m_gate) { return m_peak; }
            }
        }
        /// <summary>Gets the pool the reservation promises in.</summary>
        public PhysicalMemory Pool => m_entry.Pool;
        /// <summary>Gets the most bytes this reservation's peak may be raised by now: the pool's room, or
        /// <see cref="long.MaxValue"/> where nothing bounds the pool; zero when the pool is full.</summary>
        public long Room {
            get {
                m_account.RefreshAvailable();
                lock (m_account.m_gate) { return ((m_account.RoomOf(pool: m_entry.Pool) is { } room) ? Math.Max(val1: 0L, val2: room) : long.MaxValue); }
            }
        }

        /// <summary>Reserves the peak the holder's resources hold at once, or lowers a reservation already held. A raise
        /// past <paramref name="floor"/> that the pool has no room for is refused and changes nothing, whatever the
        /// class. A peak within the floor is reserved whatever the pool holds: the floor is what the holder cannot run
        /// without, a pool with no room for it reads over its limit (<see cref="MemoryAdmissionReading.OverLimit"/>)
        /// instead of ending the holder, and the owner's allocations within its floors are admitted as fixed storage
        /// is.</summary>
        /// <param name="peak">The most bytes the holder's resources hold at once.</param>
        /// <param name="floor">The part of <paramref name="peak"/> the holder cannot run without, in bytes; zero for
        /// none.</param>
        /// <returns><see langword="true"/> when the reservation is held.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> or <paramref name="floor"/> is
        /// negative.</exception>
        /// <exception cref="ObjectDisposedException">The reservation is disposed.</exception>
        public bool TryReserve(long peak, long floor = 0L) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            ArgumentOutOfRangeException.ThrowIfNegative(floor);
            m_account.RefreshAvailable();
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);

                var extra = (Math.Max(val1: 0L, val2: ((m_entry.Peak + (peak - m_peak)) - m_entry.Held)) - m_entry.Promise);

                if ((peak > floor) && !m_account.Fits(extra: extra, pool: m_entry.Pool)) {
                    m_account.Refused(pool: m_entry.Pool);
                    return false;
                }
                if ((extra > 0L) && !m_account.Fits(extra: extra, pool: m_entry.Pool)) { m_account.m_pressure = 1; }
                m_account.ChangeFamily(entry: m_entry, held: 0L, peak: (peak - m_peak));
                m_entry.Floor += (Math.Min(val1: floor, val2: peak) - m_floor);
                m_floor = Math.Min(val1: floor, val2: peak);
                m_peak = peak;
                return true;
            }
        }
        /// <summary>States how many of the bytes the holder's resources hold belong to objects it has replaced and
        /// released to their last readers: they stay held until their fences pass, and the pool's reading names them
        /// apart (<see cref="MemoryAdmissionReading.Retired"/>).</summary>
        /// <param name="bytes">The bytes waiting on a fence, at least zero.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
        public void Retiring(long bytes) {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            lock (m_account.m_gate) {
                m_account.m_pools[((int)m_entry.Pool)].Retiring += (bytes - m_retiring);
                m_retiring = bytes;
            }
        }
        /// <inheritdoc/>
        public void Dispose() {
            lock (m_account.m_gate) {
                if (m_closed) { return; }
                m_account.ChangeFamily(entry: m_entry, held: 0L, peak: -m_peak);
                m_account.m_pools[((int)m_entry.Pool)].Retiring -= m_retiring;
                m_entry.Floor -= m_floor;
                m_floor = 0;
                m_peak = 0;
                m_retiring = 0;
                m_closed = true;
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

    // What a backend's ledger asks and reports to: the admission door, a segment owner per segment and a family owner
    // per family and segment.
    private sealed class LedgerOwners(MemoryAccount account, string backend) : IGpuMemoryOwners {
        public GpuMemoryAdmission Admit(GpuMemorySegment segment, long bytes, string? payer, long payerBytes) =>
            account.Admit(backend: backend, bytes: bytes, payer: payer, payerBytes: payerBytes, segment: segment);
        public void Settle(GpuMemorySegment segment, GpuMemoryAdmission admission, GpuMemorySettlement settlement) =>
            account.Settle(admission: admission, backend: backend, segment: segment, settlement: settlement);
        public IAllocationObserver OfSegment(GpuMemorySegment segment) {
            var owner = new Owner(account: account, category: MemoryCategory.Live, family: null, kind: OwnerKind.Segment, memoryClass: MemoryClass.Fixed, name: $"gpu.{backend}.{segment.Name}", pool: segment.Pool);

            lock (account.m_gate) { account.m_segments[(backend, segment.Index, segment.Pool)] = new SegmentState(owner: owner, segment: segment); }

            return owner;
        }
        public IAllocationObserver OfFamily(GpuMemorySegment segment, string family) {
            lock (account.m_gate) {
                return new Owner(account: account, category: MemoryCategory.Live, family: account.FamilyOf(family: family, pool: segment.Pool), kind: OwnerKind.Family, memoryClass: MemoryClass.Fixed, name: $"gpu.{backend}.{segment.Name}:{family}", pool: segment.Pool);
            }
        }
    }
}
