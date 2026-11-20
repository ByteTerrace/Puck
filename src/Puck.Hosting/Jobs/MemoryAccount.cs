using System.Diagnostics;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Abstractions.Waiting;
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
/// <summary>
/// Which allowance a host owner's storage draws on. A device profile's authored host bar
/// (<see cref="MemoryAccount.SetHostBar"/>) is a play budget: it bounds what a player's process holds while playing. Work
/// a player never does (a bake's workspaces, its readback, the store's writes of what it baked) draws on the machine's
/// measured headroom instead, as an engine cooks offline on a workstation and a player never pays the cook. A player's
/// build never bakes; if it ever did, its authoring work falls back to the play bar
/// (<see cref="MemoryAccount.SetAuthoringOnPlayBar"/>) and degrades there, never failing.
/// </summary>
public enum MemoryAllowance {
    /// <summary>The play working set: resident meshes, region copies, staging, caches, store reads, the lighting. Held to
    /// the authored host bar, the process's private bytes beyond every owner counted, and to the machine's room.</summary>
    Play,
    /// <summary>Authoring work: held to the machine's room alone (its free physical memory and commit room, as the
    /// account reads them, under three quarters of what it has installed), never to the play bar, unless the process is
    /// a player's.</summary>
    Authoring,
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
/// The host pool has two allowances (<see cref="MemoryAllowance"/>), fixed for each owner when it is created. Invariant:
/// play storage (every device grant, and every owner created as <see cref="MemoryAllowance.Play"/>) fits the authored
/// host bar on what play holds and has promised with the private bytes beyond every owner, and fits the machine's room;
/// authoring storage fits the machine's room alone, counting everything the pool holds and has promised. The one place
/// that enforces it is the account's room arithmetic (<c>Fits</c>, <c>FitsHostBar</c>, <c>FitsChargedHostBar</c>), which
/// every door reaches with the owner's allowance, and the allowance's held and promised bytes move only where an owner's
/// charge or peak moves (<c>Owner.RecordChange</c>, <c>Owner.SetPeak</c>). A player's build counts authoring as play
/// (<see cref="SetAuthoringOnPlayBar"/>).
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
    public static MemoryAccount Shared { get; } = CreateShared();

    private static MemoryAccount CreateShared() {
        var account = new MemoryAccount();
        RentedArrays.ObserveLargeCache(account.CreateOwner(name: "large-rented-array-cache", category: MemoryCategory.Cached));
        return account;
    }

    private const int Backings = 4;
    private const int Pools = 2;
    private const int Allowances = 2;

    private readonly Lock m_gate = new();
    // The admitted host owners' bytes by category.
    private readonly long[] m_bytes = new long[4];
    // The device blocks the ledgers' segment observers charge, by pool: the physical rows of device memory.
    private readonly long[] m_deviceBlocks = new long[Pools];
    // What admitted owners and device families name, by pool and row (pool * Backings + backing).
    private readonly long[] m_named = new long[(Pools * Backings)];
    // The admitted host owners' bytes and promises by allowance and class (OwnerIndexOf), and the reserving device
    // families' by pool and class.
    private readonly long[] m_ownerHeld = new long[(Allowances * MemoryClasses.Count)];
    private readonly long[] m_ownerPromised = new long[(Allowances * MemoryClasses.Count)];
    private readonly long[] m_familyHeld = new long[(Pools * MemoryClasses.Count)];
    private readonly long[] m_familyPromised = new long[(Pools * MemoryClasses.Count)];
    private readonly HashSet<Owner> m_owners = [];
    // The device memory each owner's families hold and are promised, by pool and owner (GpuObjectName.MemoryOwnerOf).
    private readonly Dictionary<(PhysicalMemory Pool, string Owner), FamilyEntry> m_families = [];
    private readonly Dictionary<(string Backend, int Index, PhysicalMemory Pool), SegmentState> m_segments = [];
    private readonly PoolState[] m_pools = [new(), new()];
    // Serializes Measure, so each sampled owner's delta is taken from the value it last recorded.
    private readonly Lock m_sampleGate = new();
    private readonly Lock m_availableGate = new();
    private readonly List<SampledOwner> m_sampled = [];
    // When the sampled owners were last read by the frame's service (ServiceSampledOwners).
    private long m_sampledOwnersAt;

    private readonly Func<long?> m_readAvailable;
    private readonly Func<long> m_clock;

    // Pulsed when a pool may have gained room: what an owner waiting for room sleeps on (Owner.TryReserveWithin). Never
    // held while the account's lock is taken.
    private readonly object m_room = new();

    // The owners asking or sleeping in TryReserveWithin; with none, a release pulses nothing.
    private int m_roomWaiters;
    private long m_reserved;
    private long? m_budget;
    private long? m_hostBar;
    // Sampled private bytes beyond the host pool's holdings at the sample. They reduce admission room and are never added
    // to the physical rows (which already name parts of the same memory). Play's part is the whole while no authoring
    // work holds or promises anything; while it does, it is no more than the last reading taken with authoring idle
    // (m_idleBeyond), and the rest is authoring's: the garbage and slack a bake leaves name no owner, and no admission can
    // tell whose they are, so the growth while authoring runs is counted as authoring's. Only the machine's limit, which
    // counts the whole, holds that part.
    private long m_privateBeyondHeld;
    private long m_playBeyondHeld;
    private long? m_idleBeyond;
    private long HostBudget => Math.Min(m_hostBar ?? DefaultBudget, m_budget ?? DefaultBudget);
    // Whether a player's build counts authoring owners (MemoryAllowance.Authoring) as play.
    private bool m_authoringOnPlayBar;
    private long m_negativeResiduals;

    private GpuMemoryReading m_device = GpuMemoryReading.Absent;
    private MemorySegmentLimit[] m_segmentLimits = [];

    private HostMemorySample? m_sample;
    private long m_sampledAt;
    // The machine's free physical memory as last read, what the host pool held at that reading, and when; null where
    // the host reports none.
    private long? m_available;
    private long m_availableHeld;
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
    /// <param name="availablePhysicalBytes">Reads the machine's free memory, in bytes, or <see langword="null"/> where the
    /// host reports none; <see langword="null"/> reads <see cref="HostMemory.AdmissibleBytes"/>.</param>
    /// <param name="installedPhysicalBytes">The machine's installed physical memory, in bytes, which the budget and the
    /// headroom derive from; <see langword="null"/> reads the host's.</param>
    /// <param name="clock">Reads the time, in <see cref="Stopwatch"/> ticks, which paces the free-memory reading, the
    /// pressure trim and a refused segment's probe; <see langword="null"/> reads
    /// <see cref="Stopwatch.GetTimestamp"/>.</param>
    public MemoryAccount(Func<long?>? availablePhysicalBytes = null, long? installedPhysicalBytes = null, Func<long>? clock = null) {
        var installed = (installedPhysicalBytes ?? (HostMemory.InstalledPhysicalBytes() ?? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));

        m_clock = (clock ?? Stopwatch.GetTimestamp);
        m_readAvailable = (availablePhysicalBytes ?? HostMemory.AdmissibleBytes);
        DefaultBudget = DefaultBudgetOf(installedPhysicalBytes: installed);
        Headroom = Math.Clamp(max: (512L << 20), min: (16L << 20), value: (installed / 32));
    }

    /// <summary>Gets how long a reading of the machine's free physical memory stands before an admission reads it
    /// again, in <see cref="Stopwatch"/> ticks: a quarter of a second.</summary>
    public static long AvailableInterval { get; } = (Stopwatch.Frequency / 4);
    /// <summary>Gets the shortest time between two trims pressure fires (<see cref="ServicePressure"/>), in
    /// <see cref="Stopwatch"/> ticks: one second.</summary>
    public static long PressureInterval { get; } = Stopwatch.Frequency;
    /// <summary>Gets how long a segment's limit stands at what it held when the device first refused an allocation in
    /// it, in <see cref="Stopwatch"/> ticks, before the account lets one allocation probe it again: one second. Each
    /// refusal that follows without an allocation succeeding between doubles the wait, up to
    /// <see cref="MostRefusalProbeInterval"/>; an allocation the device makes resets it.</summary>
    public static long RefusalProbeInterval { get; } = Stopwatch.Frequency;
    /// <summary>Gets the longest a refused segment waits between probes, in <see cref="Stopwatch"/> ticks: sixty-four
    /// seconds.</summary>
    public static long MostRefusalProbeInterval { get; } = (64L * Stopwatch.Frequency);
    /// <summary>Gets the longest an owner waiting for room sleeps before it asks again
    /// (<see cref="Owner.TryReserveWithin"/>): a twentieth of a second.</summary>
    public static TimeSpan RoomSlice { get; } = TimeSpan.FromMilliseconds(milliseconds: 50);

    /// <summary>Requests that cache owners detach idle claims. Active consumer claims are unaffected.</summary>
    public event Action? TrimRequested;

    /// <summary>Asks participating caches to release their claims, outside the account lock. A cache whose claims belong
    /// to another thread defers the release to that thread (<see cref="DeferTrim"/>), so some storage can outlive the
    /// call; <see cref="DeferredTrims"/> counts those still waiting.</summary>
    public void TrimCaches() {
        TrimRequested?.Invoke();
        Puck.Abstractions.Memory.RentedArrays.TrimLarge();
    }

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
    /// <summary>Returns the host budget a process's account derives on a machine of a given size: three quarters of its
    /// installed memory, the rest left to the system and other programs. A host that admits Puck processes before they
    /// start (the CLI's admission) expects none to plan past it.</summary>
    /// <param name="installedPhysicalBytes">The machine's installed physical memory, in bytes.</param>
    /// <returns>The budget, in bytes.</returns>
    public static long DefaultBudgetOf(long installedPhysicalBytes) => Math.Max(val1: 0, val2: ((installedPhysicalBytes * 3) / 4));
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
    /// <summary>Selects the profile's host bar, the play budget: play admissions (<see cref="MemoryAllowance.Play"/>)
    /// count what play owners hold and are promised with the process's private resident bytes beyond every owner against
    /// it; fixed play storage, the forced job and progress floors obey it on what play owners hold and are promised alone
    /// (<see cref="Owner.TryReserveForced"/>). Authoring owners never count against it unless the process is a player's
    /// (<see cref="SetAuthoringOnPlayBar"/>). Selecting a profile restores its budget after an operator override.</summary>
    /// <param name="bytes">The authored bar in bytes, or null to return to machine-derived admission.</param>
    public void SetHostBar(long? bytes) {
        if (bytes is < 0) { throw new ArgumentOutOfRangeException(nameof(bytes)); }
        bool lowered;
        lock (m_availableGate) {
            lock (m_gate) {
                var before = HostBudget;
                m_hostBar = bytes;
                m_budget = null;
                Volatile.Write(ref m_availableAt, 0);
                if (bytes is null) {
                    m_privateBeyondHeld = 0L;
                    m_playBeyondHeld = 0L;
                    m_idleBeyond = null;
                }
                lowered = HostBudget < before;
            }
            RefreshAvailable();
        }
        if (lowered) { TrimCaches(); }
    }
    /// <summary>Sets whether authoring owners (<see cref="MemoryAllowance.Authoring"/>) count as play: a player's build
    /// sets it, since it never bakes and, if it ever did, its work must degrade under the play bar rather than draw on
    /// headroom the player never offered. Otherwise authoring draws on the machine's room alone.</summary>
    /// <param name="onPlayBar">Whether authoring storage is held to the play bar.</param>
    public void SetAuthoringOnPlayBar(bool onPlayBar) {
        lock (m_gate) {
            m_authoringOnPlayBar = onPlayBar;
            NotePressure(pool: PhysicalMemory.Host);
        }
    }
    /// <summary>Lowers a pool's budget, or restores the value it derives. The budget is a cap, never a raise: the host
    /// pool's is at most <see cref="DefaultBudget"/>, and a discrete adapter's pool limit is the smaller of the value and
    /// the sum of its segments' spend limits (the value alone where no reading names a segment). A device profile's
    /// memory bar is this cap. Nothing held is released: a budget below what a pool holds refuses the next admission
    /// that gives way, and a lowered budget asks the caches to release their claims.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="bytes">The budget, in bytes; <see langword="null"/> restores the derived budget.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bytes"/> is negative.</exception>
    public void SetBudget(PhysicalMemory pool, long? bytes) {
        if (bytes is < 0) { throw new ArgumentOutOfRangeException(paramName: nameof(bytes)); }
        bool lowered;

        lock (m_gate) {
            if (pool == PhysicalMemory.Host) {
                var before = HostBudget;

                m_budget = ((bytes is { } value) ? Math.Min(val1: value, val2: DefaultBudget) : null);
                lowered = HostBudget < before;
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
        lock (m_gate) { return new(m_bytes[0], m_reserved, m_bytes[1], m_bytes[2], m_bytes[3], HostBudget, Headroom); }
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
    /// <param name="allowance">The allowance its storage draws on for the owner's life: play unless the work is one a
    /// player never does.</param>
    /// <returns>The owner.</returns>
    public Owner CreateOwner(string name, MemoryCategory category = MemoryCategory.Live, MemoryClass memoryClass = MemoryClass.Fixed, MemoryAllowance allowance = MemoryAllowance.Play) => new(account: this, allowance: allowance, category: category, family: null, kind: OwnerKind.Admitted, memoryClass: memoryClass, name: name, pool: PhysicalMemory.Host);
    /// <summary>Creates a sampled owner: an owner whose bytes are a figure its subsystem derives, such as the payload of
    /// a structure that never reports its growth, read each time the account is measured (<see cref="Measure"/>) and
    /// charged in <paramref name="backing"/> from then until the next measure: admission counts it as of that
    /// reading.</summary>
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
                var budget = ((long)Math.Min(val1: segment.Budget, val2: ((ulong)long.MaxValue)));

                limits[index] = new(Segment: segment.Segment, Budget: budget, SpendLimit: SpendLimitOf(budget: budget));
            }
            m_device = reading;
            m_segmentLimits = limits;
            foreach (var state in m_segments.Values) {
                if ((state.RefusedLimit is not null) && (RecordedLimit(segment: state.Segment) is { } recorded) && (recorded.Budget != state.RefusedBudget)) {
                    state.RefusedLimit = null;
                    state.RefusedWait = 0L;
                }
            }
            NotePressure(pool: PhysicalMemory.Device);
            NotePressure(pool: PhysicalMemory.Host);
        }
    }
    /// <summary>Fires the trim pressure asks for: when an admission was refused, a device refused an allocation or a
    /// limit fell under what its pool holds since the last one, asks the caches to release their claims
    /// (<see cref="TrimCaches"/>), at most once every <see cref="PressureInterval"/>. The host calls it on its frame
    /// thread, at the top of a frame, so no trim runs inside an admission.</summary>
    /// <returns>Whether a trim was fired.</returns>
    public bool ServicePressure() {
        if (Volatile.Read(location: ref m_pressure) == 0) { return false; }

        var now = m_clock();

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
            SampleOwners();
            lock (m_gate) {
                m_sample = sample;
                m_sampledAt = Stopwatch.GetTimestamp();
            }
        }
    }
    /// <summary>Reads every sampled owner (<see cref="CreateSampledOwner"/>) again once its last read is
    /// <see cref="AvailableInterval"/> old, so admission counts what those subsystems hold between measures rather than
    /// only after one. The host calls it on its frame thread at the top of a frame, holding no lock a sampled owner's read
    /// could take; nothing inside an admission reads them, since a read may take its subsystem's lock.</summary>
    public void ServiceSampledOwners() {
        var now = m_clock();
        var last = Volatile.Read(location: ref m_sampledOwnersAt);

        if ((last != 0L) && ((now - last) < AvailableInterval)) { return; }
        lock (m_sampleGate) {
            Volatile.Write(location: ref m_sampledOwnersAt, value: Math.Max(val1: 1L, val2: now));
            SampleOwners();
        }
    }
    // Under m_sampleGate: reads every sampled owner and records its bytes.
    private void SampleOwners() {
        foreach (var sampled in m_sampled) { sampled.Sample(); }
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
    /// <summary>Reads what one pool's play admission holds it to, without allocating: its limit and the bytes play holds
    /// and has promised in it (<see cref="MemoryAllowance.Play"/>; in host memory, authoring's bytes are left out and its
    /// share of the machine is taken from the limit).</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="limit">The pool's play limit, in bytes.</param>
    /// <param name="held">The bytes play holds in the pool.</param>
    /// <param name="promised">The bytes play has promised in the pool.</param>
    /// <returns><see langword="false"/> where nothing bounds the pool, and <paramref name="limit"/> reads zero.</returns>
    public bool TryReadTotals(PhysicalMemory pool, out long limit, out long held, out long promised) {
        RefreshAvailable();
        lock (m_gate) {
            held = PlayHeldOf(pool: pool);
            promised = PlayPromisedOf(pool: pool);
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
            var budget = HostBudget;
            var deviceLimits = m_segmentLimits.Where(predicate: static limit => (limit.Segment.Pool == PhysicalMemory.Device)).ToArray();
            var sample = m_sample;
            var age = ((sample is null) ? (TimeSpan?)null : Stopwatch.GetElapsedTime(startingTimestamp: m_sampledAt));
            var direct = Named(backing: MemoryBacking.NativeDirect, pool: PhysicalMemory.Host);
            var hostRows = new List<MemoryRowReading> {
                Row(backing: MemoryBacking.DeviceBlocks, bytes: m_deviceBlocks[((int)PhysicalMemory.Host)], measured: false, name: "device-blocks", pool: PhysicalMemory.Host),
                Row(backing: MemoryBacking.NativeAllocator, bytes: sample?.AllocatorCommitted, measured: true, name: "native-allocator", pool: PhysicalMemory.Host),
                Row(backing: MemoryBacking.NativeDirect, bytes: direct, measured: false, name: "native-direct", pool: PhysicalMemory.Host),
                Row(backing: MemoryBacking.Managed, bytes: sample?.ManagedCommitted, measured: true, name: "managed", pool: PhysicalMemory.Host),
            };
            // What the walk's private bytes hold beyond the rows that lie in them: the runtime's code and loader heaps,
            // stacks and the driver's private memory. The host's write-combined and uncached views of graphics
            // allocations are left out: the ledgers count those blocks in the device-blocks rows, and a driver that maps
            // device memory into the process would otherwise charge the device's blocks to host memory a second time.
            long? runtime = (((sample?.Walk is { } walk) && (sample.AllocatorCommitted is { } allocator))
                ? ((walk.PrivateBytes - walk.DriverMappedBytes) - ((allocator + direct) + sample.ManagedCommitted))
                : null);

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
                Budget: ((deviceLimits.Length == 0) ? m_pools[((int)PhysicalMemory.Device)].BudgetOverride : Math.Min(val1: deviceLimits.Sum(selector: static limit => limit.Budget), val2: (m_pools[((int)PhysicalMemory.Device)].BudgetOverride ?? long.MaxValue))),
                Pool: PhysicalMemory.Device,
                Present: ((deviceLimits.Length != 0) || (deviceBlocks != 0)),
                Reserved: 0L,
                Rows: [Row(backing: MemoryBacking.DeviceBlocks, bytes: deviceBlocks, measured: false, name: "device-blocks", pool: PhysicalMemory.Device)]
            );
            var segments = new List<MemorySegmentAdmission>(capacity: m_segments.Count);

            foreach (var ((backend, _, _), state) in m_segments) {
                segments.Add(item: new(Backend: backend, Granted: state.Granted, Held: state.Owner.HeldLocked, Limit: EffectiveLimit(state: state), Peak: state.Owner.PeakLocked, RefusedLimit: RefusedLimitOf(state: state), Segment: state.Segment));
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
        var now = m_clock();
        var last = Volatile.Read(location: ref m_availableAt);

        if ((last != 0) && ((now - last) < AvailableInterval)) { return; }

        lock (m_availableGate) {
            if ((m_availableAt != 0) && ((now - m_availableAt) < AvailableInterval)) { return; }
            long heldBefore;
            long hostOwnersBefore;
            lock (m_gate) {
                heldBefore = HeldOf(PhysicalMemory.Host);
                hostOwnersBefore = m_bytes.Sum();
            }
            var available = m_readAvailable();
            var process = m_hostBar is null ? null : HostMemory.PrivateResidentBytes(processId: Environment.ProcessId);
            lock (m_gate) {
                m_available = available;
                // Do not credit an allocation that raced the sample as if it were already in that reading.
                m_availableHeld = Math.Min(heldBefore, HeldOf(PhysicalMemory.Host));
                // The process's private resident pages (HostMemory.PrivateResidentBytes): commit no page backs yet, such
                // as a driver's mirror of device memory or a collector's committed reserve, occupies no host memory and is
                // left out. The host-placed device blocks lie in it (the driver's write-combined views of upload and
                // readback heaps are resident private pages, as a walk's driver-mapped resident figure shows) and the pool
                // already holds them, so they are taken out with the owners rather than counted twice.
                if (process is { } bytes) { RecordBeyond(beyond: Math.Max(0L, ((bytes - Math.Min(hostOwnersBefore, m_bytes.Sum())) - m_deviceBlocks[((int)PhysicalMemory.Host)]))); }
                Volatile.Write(location: ref m_availableAt, value: Math.Max(val1: 1L, val2: now));
                NotePressure(pool: PhysicalMemory.Host);
            }
        }
    }
    // Under m_gate: records the private bytes beyond the pool's holdings and play's part of them (m_playBeyondHeld).
    private void RecordBeyond(long beyond) {
        m_privateBeyondHeld = beyond;
        if (m_authoringOnPlayBar || ((AuthoringSum(bytes: m_ownerHeld) + AuthoringSum(bytes: m_ownerPromised)) == 0L)) {
            m_idleBeyond = beyond;
            m_playBeyondHeld = beyond;
            return;
        }
        m_playBeyondHeld = ((m_idleBeyond is { } idle) ? Math.Min(val1: beyond, val2: idle) : beyond);
    }
    // Under m_gate.
    private long Named(PhysicalMemory pool, MemoryBacking backing) => m_named[((((int)pool) * Backings) + ((int)backing))];
    private static int ClassIndex(PhysicalMemory pool, MemoryClass memoryClass) => ((((int)pool) * MemoryClasses.Count) + ((int)memoryClass));
    // Under m_gate: a row and what its owners name in it; a residual skew makes negative is counted.
    private MemoryRowReading Row(string name, PhysicalMemory pool, MemoryBacking backing, long? bytes, bool measured) {
        var named = Named(backing: backing, pool: pool);

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
    // Under m_gate: what authoring owners hold and have promised apart from play; nothing in a player's build, whose
    // authoring counts as play.
    private long AuthoringHeld => (m_authoringOnPlayBar ? 0L : AuthoringSum(bytes: m_ownerHeld));
    private long AuthoringPromised => (m_authoringOnPlayBar ? 0L : AuthoringSum(bytes: m_ownerPromised));
    private static long AuthoringSum(long[] bytes) {
        var sum = 0L;

        for (var index = 0; (index < MemoryClasses.Count); index++) { sum += bytes[OwnerIndexOf(allowance: MemoryAllowance.Authoring, memoryClass: ((MemoryClass)index))]; }

        return sum;
    }
    // An owner's index in the by-allowance-and-class arrays.
    private static int OwnerIndexOf(MemoryAllowance allowance, MemoryClass memoryClass) => ((((int)allowance) * MemoryClasses.Count) + ((int)memoryClass));
    // Under m_gate: whether an owner's allowance draws on the machine's room apart from the play bar.
    private bool Apart(MemoryAllowance allowance) => ((allowance == MemoryAllowance.Authoring) && !m_authoringOnPlayBar);
    // Under m_gate: what play holds and has promised in a pool: everything but authoring's.
    private long PlayHeldOf(PhysicalMemory pool) => (HeldOf(pool: pool) - ((pool == PhysicalMemory.Host) ? AuthoringHeld : 0L));
    private long PlayPromisedOf(PhysicalMemory pool) => (PromisedOf(pool: pool) - ((pool == PhysicalMemory.Host) ? AuthoringPromised : 0L));
    // Under m_gate: what the whole host pool (play and authoring) may hold and promise before the headroom: the budget an
    // operator set or the machine-derived one, less the private bytes beyond the owners, and with a free-memory reading no
    // more than what is free with what the pool held at that reading (the free memory already leaves out the bytes beyond
    // the owners, so they are not taken from it again).
    private long MachineBudget() {
        var budget = ((m_budget ?? DefaultBudget) - m_privateBeyondHeld);

        return ((m_available is { } available) ? Math.Min(val1: budget, val2: available.AddSaturating(right: m_availableHeld)) : budget);
    }
    // Under m_gate: the machine's budget less the headroom.
    private long MachineLimit() => Math.Max(val1: 0L, val2: (MachineBudget() - Headroom));
    // Under m_gate: a pool's spend limit, or null where nothing bounds it (a device pool no reading names). The host
    // pool's is play's: the authored bar less play's part of the private bytes beyond the owners and the headroom, and no
    // more than the machine's limit leaves once authoring's bytes are taken out.
    private long? LimitOf(PhysicalMemory pool) {
        if (pool == PhysicalMemory.Host) {
            var play = (MachineLimit() - (AuthoringHeld + AuthoringPromised));

            if (m_hostBar is { } bar) { play = Math.Min(val1: play, val2: ((Math.Min(val1: bar, val2: (m_budget ?? DefaultBudget)) - m_playBeyondHeld) - Headroom)); }

            return Math.Max(val1: 0L, val2: play);
        }
        long? limit = null;

        foreach (var recorded in m_segmentLimits) {
            if (recorded.Segment.Pool != pool) { continue; }

            var spend = recorded.SpendLimit;

            foreach (var state in m_segments.Values) {
                if ((state.Segment == recorded.Segment) && (RefusedLimitOf(state: state) is { } refused)) { spend = Math.Min(val1: spend, val2: refused); }
            }
            limit = ((limit ?? 0L) + spend);
        }

        // A cap lowers the limit the segments' readings derive and bounds a pool no reading names.
        return (m_pools[((int)pool)].BudgetOverride is { } cap) ? Math.Min(val1: cap, val2: (limit ?? cap)) : limit;
    }
    // Under m_gate: the room a pool has left for a new promise; null where nothing bounds it. Negative when the pool
    // holds and has promised more than its limit.
    // A play room, of a pool's play limit: what play holds and has promised is taken from it.
    private long? RoomOf(PhysicalMemory pool) => ((LimitOf(pool: pool) is { } limit) ? ((limit - PlayHeldOf(pool: pool)) - PlayPromisedOf(pool: pool)) : null);
    // Under m_gate: whether a pool has room for extra bytes of promise in an allowance: play's room, or for authoring the
    // machine's limit less everything the host pool holds and has promised.
    private bool Fits(PhysicalMemory pool, long extra, MemoryAllowance allowance = MemoryAllowance.Play) {
        if (extra <= 0L) { return true; }
        if ((pool == PhysicalMemory.Host) && Apart(allowance: allowance)) { return (extra <= ((MachineLimit() - HeldOf(pool: pool)) - PromisedOf(pool: pool))); }

        return ((RoomOf(pool: pool) is not { } room) || (extra <= room));
    }

    // Ordinary admission leaves headroom. Storage that gives way may spend that reserve when it acquires, but no promise
    // or acquisition that gives way can pass an explicit host bar on what play holds, the process's private bytes beyond
    // its owners counted; authoring storage, apart from play, can pass no more than the machine's budget on everything the
    // pool holds.
    private bool FitsHostBar(PhysicalMemory pool, long extra, MemoryAllowance allowance = MemoryAllowance.Play) {
        if ((pool != PhysicalMemory.Host) || (extra <= 0L)) { return true; }
        if (Apart(allowance: allowance)) { return (extra <= ((MachineBudget() - HeldOf(pool: pool)) - PromisedOf(pool: pool))); }

        return ((m_hostBar is not { } bar) || (extra <= (((Math.Min(val1: bar, val2: DefaultBudget) - m_playBeyondHeld) - PlayHeldOf(pool: pool)) - PlayPromisedOf(pool: pool))));
    }
    // Under m_gate: whether extra bytes of promise fit the authored host bar counting only what play's owners hold and are
    // promised. The private bytes beyond them (m_playBeyondHeld: the collector's and allocator's slack, the runtime,
    // the driver, storage no owner names) are a sampled reading no admission can shrink, and can stand past the bar for as
    // long as work runs; what cannot give way (fixed storage, a reservation's floor) and what work needs to progress at
    // all (the forced job, an owner's progress floor) are judged without them, so the presentation keeps its tables and
    // work still settles, one job at a time, while every byte the owners name stays under the bar. Authoring storage,
    // apart from play, is judged the same way against the machine's budget on everything the pool's owners hold.
    private bool FitsChargedHostBar(PhysicalMemory pool, long extra, MemoryAllowance allowance = MemoryAllowance.Play) {
        if ((pool != PhysicalMemory.Host) || (extra <= 0L)) { return true; }
        if (Apart(allowance: allowance)) { return (extra <= (((m_budget ?? DefaultBudget) - HeldOf(pool: pool)) - PromisedOf(pool: pool))); }

        return ((m_hostBar is not { } bar) || (extra <= ((Math.Min(val1: bar, val2: DefaultBudget) - PlayHeldOf(pool: pool)) - PlayPromisedOf(pool: pool))));
    }
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
            : ((state.Segment.Bytes != 0UL) ? SpendLimitOf(budget: ((long)Math.Min(val1: state.Segment.Bytes, val2: ((ulong)long.MaxValue)))) : null));

        return ((RefusedLimitOf(state: state) is { } refused) ? Math.Min(val1: (limit ?? long.MaxValue), val2: refused) : limit);
    }
    // Under m_gate: the limit a device refusal left on a segment, or null once it has stood for the segment's wait: the
    // next allocation is then admitted on the reading alone and probes the device. A probe the device refuses puts the
    // limit back with twice the wait (Settle); one it makes ends the refusal.
    private long? RefusedLimitOf(SegmentState state) {
        if ((state.RefusedLimit is not null) && ((m_clock() - state.RefusedAt) >= state.RefusedWait)) { state.RefusedLimit = null; }

        return state.RefusedLimit;
    }
    // Wakes the owners waiting for room; called with the account's lock held or not, never the reverse.
    private void SignalRoom() {
        if (Volatile.Read(location: ref m_roomWaiters) == 0) { return; }
        lock (m_room) { Monitor.PulseAll(obj: m_room); }
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
    // Bytes a grant drew from the reservation stay pending until the owner's resources name them, so a second grant
    // made before the first is counted and named finds them spent.
    private void ChangeFamily(FamilyEntry entry, long held, long peak, long pending = 0L) {
        var before = entry.Promise;

        entry.Held += held;
        entry.Peak += peak;
        entry.Pending = Math.Max(val1: 0L, val2: ((entry.Pending + pending) - Math.Max(val1: 0L, val2: held)));

        var promise = (entry.Promise - before);

        if ((promise < 0L) || (held < 0L)) { SignalRoom(); }

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
            // Play's bytes by class; a player's build counts authoring's as play.
            var play = OwnerIndexOf(allowance: MemoryAllowance.Play, memoryClass: ((MemoryClass)index));
            var authoring = OwnerIndexOf(allowance: MemoryAllowance.Authoring, memoryClass: ((MemoryClass)index));

            held[index] = (family + ((pool == PhysicalMemory.Host) ? (m_ownerHeld[play] + (m_authoringOnPlayBar ? m_ownerHeld[authoring] : 0L)) : 0L));
            promised[index] = (m_familyPromised[ClassIndex(memoryClass: ((MemoryClass)index), pool: pool)] + ((pool == PhysicalMemory.Host) ? (m_ownerPromised[play] + (m_authoringOnPlayBar ? m_ownerPromised[authoring] : 0L)) : 0L));
        }
        // The device blocks no reserving family names are fixed: targets, tables, the room inside shared blocks.
        held[((int)MemoryClass.Fixed)] += (m_deviceBlocks[((int)pool)] - families);
        promised[((int)MemoryClass.Fixed)] += state.Granted;

        var host = (pool == PhysicalMemory.Host);

        return new(
            ClassHeld: held,
            ClassPromised: promised,
            DeviceRefusals: state.DeviceRefusals,
            Floor: state.Floor,
            Forced: (((pool == PhysicalMemory.Host) && (m_forced is not null)) ? m_forcedBytes : 0L),
            ForcedJobs: ((pool == PhysicalMemory.Host) ? m_forcedJobs : 0L),
            Held: PlayHeldOf(pool: pool),
            Limit: LimitOf(pool: pool),
            OverLimitBytes: state.OverLimitBytes,
            OverLimitGrants: state.OverLimitGrants,
            Peak: state.Peak,
            Pool: pool,
            PressureTrims: m_pressureTrims,
            Promised: PlayPromisedOf(pool: pool),
            Refusals: state.Refusals,
            Retired: (state.Retiring + (host ? m_bytes[((int)MemoryCategory.Retirement)] : 0L))
        ) {
            AuthoringHeld = (host ? AuthoringHeld : 0L),
            AuthoringOnPlayBar = (host && m_authoringOnPlayBar),
            AuthoringPromised = (host ? AuthoringPromised : 0L),
            LastRefusedOwner = state.LastRefusedOwner,
            MachineLimit = (host ? MachineLimit() : null),
            PlayBeyondHeld = (host ? m_playBeyondHeld : 0L),
            PrivateBeyondHeld = (host ? m_privateBeyondHeld : 0L),
        };
    }
    // Under m_gate: counts a refused admission. A refusal is admission working, not pressure: pressure is a limit that
    // fell under what its pool holds, a device that refused, or storage admitted past a limit.
    private void Refused(PhysicalMemory pool, string owner, bool trim = true) {
        var state = m_pools[(int)pool];
        state.Refusals++;
        state.LastRefusedOwner = owner;
        if (trim && pool == PhysicalMemory.Host && m_hostBar is not null && (m_ownerHeld[OwnerIndexOf(allowance: MemoryAllowance.Play, memoryClass: MemoryClass.Cached)] > 0L)) {
            m_pressure = 1;
        }
    }
    // Under m_gate: marks pressure when what a pool holds and has promised past its limit, beyond the forced job, has
    // grown since it was last read. A pool that stands over a limit it cannot get back under asks for one trim, not one
    // at every reading; one that falls back and passes it again asks again.
    private void NotePressure(PhysicalMemory pool) {
        var state = m_pools[((int)pool)];
        var forced = (((pool == PhysicalMemory.Host) && (m_forced is not null)) ? m_forcedBytes : 0L);
        var over = ((LimitOf(pool: pool) is { } limit)
            ? Math.Max(val1: 0L, val2: (((PlayHeldOf(pool: pool) + PlayPromisedOf(pool: pool)) - forced) - limit))
            : 0L);

        // Authoring apart from play passes only the machine's limit, on everything the pool holds.
        if (pool == PhysicalMemory.Host) { over = Math.Max(val1: over, val2: (((HeldOf(pool: pool) + PromisedOf(pool: pool)) - forced) - MachineLimit())); }

        if (over > state.PressureOver) { m_pressure = 1; }
        state.PressureOver = over;
    }
    // Under m_gate: keeps the most a pool has held at once.
    private void RaisePeak(PhysicalMemory pool) {
        var state = m_pools[((int)pool)];

        state.Peak = Math.Max(val1: state.Peak, val2: HeldOf(pool: pool));
    }
    // The ledger's door: whether a device allocation may be made, and the promise it stands as until it settles.
    private GpuMemoryAdmission Admit(string backend, GpuMemorySegment segment, long bytes, string? payer, long payerBytes, bool givesWay) {
        RefreshAvailable();
        lock (m_gate) {
            var pool = segment.Pool;
            var entry = (((payer is not null) && m_families.TryGetValue(key: (pool, GpuObjectName.MemoryOwnerOf(family: payer)), value: out var found)) ? found : null);
            // What the payer's reservation already promised is not promised again, and is drawn from it here, under
            // the lock, so no other grant is given the same credit.
            var credit = Math.Min(val1: payerBytes, val2: (entry?.Promise ?? 0L));
            var net = (bytes - credit);
            var state = m_pools[((int)pool)];
            var segmentState = (m_segments.TryGetValue(key: (backend, segment.Index, pool), value: out var known) ? known : null);
            var fits = Fits(extra: net, pool: pool);

            if (fits && (segmentState is not null) && (EffectiveLimit(state: segmentState) is { } limit)) {
                fits = ((bytes <= 0L) || (((segmentState.Owner.HeldLocked + segmentState.Granted) + bytes) <= limit));
            }

            var verdict = GpuMemoryVerdict.Granted;

            if (!fits) {
                // An owner's storage gives way only past the floor its reservations state: what it cannot run without is
                // admitted like fixed storage. The floor is measured in the owner's own bytes, so it covers a resource
                // within it whatever the block the device allocates to place it: a small resource's shared block is
                // many times the resource, and judged by the block no floor would ever admit one.
                if (!FitsChargedHostBar(extra: net, pool: pool) || givesWay || ((entry is { Classified: true }) && MemoryClasses.GivesWay(memoryClass: entry.Class) && ((entry.Held + Math.Min(val1: payerBytes, val2: bytes)) > entry.Floor))) {
                    Refused(pool: pool, owner: payer ?? segment.Name);

                    return new(Bytes: bytes, Net: net, Payer: null, Verdict: GpuMemoryVerdict.Refused);
                }
                verdict = GpuMemoryVerdict.OverLimit;
                state.OverLimitGrants++;
                state.OverLimitBytes += net;
                m_pressure = 1;
            }
            state.Granted += bytes;
            if (segmentState is not null) { segmentState.Granted += bytes; }
            if (credit > 0L) { ChangeFamily(entry: entry!, held: 0L, peak: 0L, pending: credit); }

            return new(Bytes: bytes, Net: net, Payer: ((credit > 0L) ? payer : null), Verdict: verdict);
        }
    }
    private void Settle(string backend, GpuMemorySegment segment, GpuMemoryAdmission admission, GpuMemorySettlement settlement) {
        lock (m_gate) {
            var state = m_pools[((int)segment.Pool)];

            state.Granted -= admission.Bytes;
            // A grant that was not counted gives its credit back to the reservation; a counted one keeps it pending
            // until the owner's resources name the bytes.
            if ((settlement != GpuMemorySettlement.Counted) && (admission.Payer is { } payer) && m_families.TryGetValue(key: (segment.Pool, GpuObjectName.MemoryOwnerOf(family: payer)), value: out var entry)) {
                ChangeFamily(entry: entry, held: 0L, peak: 0L, pending: -Math.Min(val1: entry.Pending, val2: (admission.Bytes - admission.Net)));
            }
            SignalRoom();
            if (!m_segments.TryGetValue(key: (backend, segment.Index, segment.Pool), value: out var segmentState)) { return; }
            segmentState.Granted -= admission.Bytes;
            if (settlement == GpuMemorySettlement.Counted) {
                // The device made an allocation with no refusal standing: the next refusal waits the first interval.
                if (segmentState.RefusedLimit is null) { segmentState.RefusedWait = 0L; }

                return;
            }
            if (settlement != GpuMemorySettlement.DeviceRefused) { return; }
            // The device said no under the limit the reading gave: the segment holds what it can until its budget is
            // read differently or the refusal has stood its wait, which doubles while refusals follow one another.
            segmentState.RefusedLimit = segmentState.Owner.HeldLocked;
            segmentState.RefusedBudget = (RecordedLimit(segment: segment)?.Budget ?? -1L);
            segmentState.RefusedAt = m_clock();
            segmentState.RefusedWait = ((segmentState.RefusedWait == 0L) ? RefusalProbeInterval : Math.Min(val1: (2L * segmentState.RefusedWait), val2: MostRefusalProbeInterval));
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
        // What the pool held and had promised past its limit when pressure last read it.
        public long PressureOver;
        public long Refusals;
        public string? LastRefusedOwner;
        public long Retiring;
    }
    // One ledger segment's admission state: its row's owner, the grants in flight in it, and the limit a device refusal
    // left with the budget it was refused under.
    private sealed class SegmentState(GpuMemorySegment segment, Owner owner) {
        public long Granted;

        public Owner Owner { get; } = owner;

        // When the device last refused an allocation in the segment, and how long that refusal stands before a probe.
        public long RefusedAt;
        public long RefusedWait;
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
        // The bytes grants drew from the reservation that its resources have not named yet.
        public long Pending;

        public PhysicalMemory Pool { get; } = pool;

        public long Promise => Math.Max(val1: 0L, val2: ((Peak - Held) - Pending));
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
        private readonly MemoryAllowance m_allowance;

        private readonly long[] m_rows = new long[Backings];

        private MemoryCategory m_category;
        private long m_bytes;
        private long m_reservationPeak;

        // The peak the owner was last refused, until a reservation of it is held; -1 for none.
        private long m_refusedPeak = -1L;

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
        /// <summary>Gets the most bytes the owner's storage has held at once over its lifetime.</summary>
        public long HeldPeak {
            get {
                lock (m_account.m_gate) { return m_ownedPeak; }
            }
        }

        internal Owner(MemoryAccount account, string name, MemoryCategory category, PhysicalMemory pool, OwnerKind kind, MemoryClass memoryClass, FamilyEntry? family, MemoryAllowance allowance = MemoryAllowance.Play) { m_account = account; Name = name; m_category = category; m_pool = pool; m_kind = kind; m_class = memoryClass; m_family = family; m_allowance = allowance; }

        /// <summary>Gets the allowance the owner's storage draws on.</summary>
        public MemoryAllowance Allowance => m_allowance;

        // Under the account's lock.
        internal long HeldLocked => m_bytes;
        // The owner's index in the account's by-allowance-and-class arrays.
        private int OwnerIndex => OwnerIndexOf(allowance: m_allowance, memoryClass: EffectiveClass);
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

            MovePromise(change: change);
            Track();
            if (change < 0L) { m_account.SignalRoom(); }
        }
        // Under the account's lock: the one place an admitted owner's promise moves the account's, its class's and its
        // allowance's promises.
        private void MovePromise(long change) {
            m_account.m_reserved += change;
            m_account.m_ownerPromised[OwnerIndex] += change;
        }

        internal MemoryOwnerReading Read() {
            lock (m_account.m_gate) {
                return new(Name, m_category, m_bytes, Promise, m_ownedPeak, m_reservationPeak, m_closed, m_pool, (m_kind != OwnerKind.Admitted), (m_kind == OwnerKind.Segment), new MemoryRowBytes(DeviceBlocks: m_rows[((int)MemoryBacking.DeviceBlocks)], Managed: m_rows[((int)MemoryBacking.Managed)], NativeAllocator: m_rows[((int)MemoryBacking.NativeAllocator)], NativeDirect: m_rows[((int)MemoryBacking.NativeDirect)]), EffectiveClass, ReferenceEquals(objA: m_account.m_forced, objB: this)) { Allowance = m_allowance };
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
        public bool TryReserve(long peak) => Reserve(peak: peak, progress: false);
        // The one door of TryReserve and TryReserveProgress: a reservation within the pool's room, or, for a progress
        // floor, within the host bar on what the owners hold and are promised, counted as an over-limit grant.
        private bool Reserve(long peak, bool progress) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            m_account.RefreshAvailable();
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);
                ThrowIfNotAdmitted();

                var extra = (Math.Max(val1: 0L, val2: (peak - m_bytes)) - Promise);

                if (!m_account.Fits(allowance: m_allowance, extra: extra, pool: PhysicalMemory.Host)) {
                    if (!progress || !m_account.FitsChargedHostBar(allowance: m_allowance, extra: extra, pool: PhysicalMemory.Host)) { return RefusePeak(peak); }

                    var state = m_account.m_pools[((int)PhysicalMemory.Host)];

                    state.OverLimitGrants++;
                    state.OverLimitBytes += extra;
                    m_account.m_pressure = 1;
                }
                m_refusedPeak = -1L;
                SetPeak(peak: peak);
                return true;
            }
        }
        // Under the account lock. Repeat requests for the same refused peak are a single wait.
        private bool RefusePeak(long peak) {
            if (m_refusedPeak != peak) {
                m_account.Refused(pool: PhysicalMemory.Host, owner: Name);
                m_refusedPeak = peak;
            }
            return false;
        }
        /// <summary>Reserves the owner's peak as the process's one forced job when the authored host bar permits it,
        /// counting only what the host pool's owners hold and are promised: the private bytes beyond them are left out,
        /// so the forced job settles while the process stands over its bar for storage no owner names. One owner in the
        /// process holds the allowance at a time, from this call until its reservation is released
        /// (<see cref="ReleaseReservation"/>) or it is disposed; the bytes forced are counted
        /// (<see cref="MemoryAdmissionReading.Forced"/>).</summary>
        /// <param name="peak">The most bytes the owner's storage holds at once while the work runs.</param>
        /// <returns><see langword="true"/> when the reservation is held; <see langword="false"/> when another owner holds
        /// the forced allowance or the requested peak, with what the owners hold and are promised, exceeds the host
        /// bar.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
        /// <exception cref="InvalidOperationException">The owner is a device family or a segment's row.</exception>
        public bool TryReserveForced(long peak) {
            ArgumentOutOfRangeException.ThrowIfNegative(peak);
            m_account.RefreshAvailable();
            lock (m_account.m_gate) {
                ObjectDisposedException.ThrowIf(condition: m_closed, instance: this);
                ThrowIfNotAdmitted();
                if ((m_account.m_forced is { } holder) && !ReferenceEquals(objA: holder, objB: this)) { return false; }
                if (!m_account.FitsChargedHostBar(allowance: m_allowance, extra: (Math.Max(val1: 0L, val2: (peak - m_bytes)) - Promise), pool: PhysicalMemory.Host)) { return RefusePeak(peak); }
                if (m_account.m_forced is null) { m_account.m_forcedJobs++; }
                m_account.m_forced = this;
                m_account.m_forcedBytes = peak;
                m_refusedPeak = -1L;
                SetPeak(peak: peak);
                return true;
            }
        }
        /// <summary>Reserves storage that work the account admitted needs to progress at all, such as a mesh bake's
        /// readback, which answers the groups it admitted: granted when it fits the pool's room
        /// (<see cref="TryReserve"/>), and otherwise when it fits the authored host bar counting only what the owners hold
        /// and are promised, as the forced job is judged (<see cref="TryReserveForced"/>). A grant past the room is
        /// counted as an over-limit grant and asks the caches to release their claims, and no owner holds an allowance:
        /// any number of owners may hold a progress floor.</summary>
        /// <param name="peak">The most bytes the owner's storage holds at once while the work runs.</param>
        /// <returns><see langword="true"/> when the reservation is held.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
        /// <exception cref="InvalidOperationException">The owner is a device family or a segment's row.</exception>
        public bool TryReserveProgress(long peak) => Reserve(peak: peak, progress: true);
        /// <summary>Releases the future promise after work ends, and the forced allowance if the owner holds it; any
        /// still-owned storage remains accounted.</summary>
        public void ReleaseReservation() {
            lock (m_account.m_gate) {
                if (m_kind == OwnerKind.Admitted) { SetPeak(peak: 0); }
                if (ReferenceEquals(objA: m_account.m_forced, objB: this)) {
                    m_account.m_forced = null;
                    m_account.m_forcedBytes = 0;
                    m_account.SignalRoom();
                }
            }
        }
        /// <summary>Reserves the owner's simultaneous peak for transient work that holds nothing else, waiting for room
        /// when the pool has none: it is admitted when the reservation fits (<see cref="TryReserve"/>) or, with no other
        /// forced job in the process, as the forced job (<see cref="TryReserveForced"/>), and otherwise sleeps on the
        /// host until a pool gains room or <paramref name="patience"/> runs out, asking again at least every
        /// <see cref="RoomSlice"/>. A caller that is refused does without: its work is a cache's, and a miss is derived
        /// again by work that is admitted on its own terms. The wait is bounded because a waiter can hold a worker the
        /// forced job needs.</summary>
        /// <param name="peak">The most bytes the owner's storage holds at once while the work runs.</param>
        /// <param name="patience">How long the owner waits for room before it does without.</param>
        /// <param name="reason">Why the calling thread waits, for the host's wait records.</param>
        /// <returns><see langword="true"/> when the reservation is held; <see langword="false"/> when the patience ran
        /// out, and nothing was reserved.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="peak"/> is negative.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed.</exception>
        /// <exception cref="InvalidOperationException">The owner is a device family or a segment's row.</exception>
        public bool TryReserveWithin(long peak, TimeSpan patience, in WaitReason reason) {
            if (TryReserve(peak: peak) || TryReserveForced(peak: peak)) { return true; }

            var deadline = WaitDeadline.In(budget: patience);

            // Counted as a waiter before each ask, so a release after the ask pulses. One made between the ask and the
            // sleep is missed, and the slice bounds what that costs.
            _ = Interlocked.Increment(location: ref m_account.m_roomWaiters);
            try {
                while (true) {
                    if (TryReserve(peak: peak) || TryReserveForced(peak: peak)) { return true; }
                    if (deadline.HasPassed) { return false; }

                    var slice = WaitDeadline.In(budget: RoomSlice);
                    WaitOutcome outcome;

                    lock (m_account.m_room) {
                        outcome = HostWaits.OnCondition(deadline: ((slice.Timestamp < deadline.Timestamp) ? slice : deadline), monitor: m_account.m_room, reason: in reason);
                    }
                    // A pulse and a slice that ran out both ask again; only the patience running out ends the wait,
                    // after one last ask.
                    if ((outcome == WaitOutcome.DeadlinePassed) && deadline.HasPassed) { return (TryReserve(peak: peak) || TryReserveForced(peak: peak)); }
                }
            } finally {
                _ = Interlocked.Decrement(location: ref m_account.m_roomWaiters);
            }
        }
        /// <inheritdoc/>
        public bool TryAcquire(long bytes, MemoryBacking backing) {
            ArgumentOutOfRangeException.ThrowIfNegative(bytes);
            m_account.RefreshAvailable();
            lock (m_account.m_gate) {
                ThrowIfNotAdmitted();
                var extra = bytes - Promise;
                // Storage that cannot give way is judged on what the owners name alone (FitsChargedHostBar).
                var fitsBar = (MemoryClasses.GivesWay(memoryClass: EffectiveClass)
                    ? m_account.FitsHostBar(allowance: m_allowance, extra: extra, pool: PhysicalMemory.Host)
                    : m_account.FitsChargedHostBar(allowance: m_allowance, extra: extra, pool: PhysicalMemory.Host));

                if (!fitsBar ||
                    (EffectiveClass == MemoryClass.Cached && !m_account.Fits(allowance: m_allowance, extra: extra, pool: PhysicalMemory.Host))) {
                    m_account.Refused(PhysicalMemory.Host, Name, trim: EffectiveClass != MemoryClass.Cached);
                    return false;
                }
                RecordChange(bytes, backing);
                return true;
            }
        }
        /// <summary>Allocates a managed array after charging its payload. The owner releases the charge when its
        /// claim on the array ends.</summary>
        /// <typeparam name="T">The array element.</typeparam>
        /// <param name="length">The number of elements.</param>
        /// <returns>The new array.</returns>
        public T[] AllocateArray<T>(int length) {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            var bytes = (long)length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
            ((IAllocationObserver)this).Acquire(bytes, MemoryBacking.Managed);
            try { return new T[length]; }
            catch { Change(-bytes, MemoryBacking.Managed); throw; }
        }
        /// <inheritdoc/>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="backing"/> is a row this owner cannot hold:
        /// device blocks charged to a host owner, or a host row charged to a device family or a segment.</exception>
        /// <exception cref="InvalidOperationException">The change would release more bytes than the owner holds, overall
        /// or in <paramref name="backing"/>.</exception>
        /// <exception cref="ObjectDisposedException">The owner is disposed and the change acquires bytes.</exception>
        public void Change(long bytes, MemoryBacking backing) {
            if (m_kind == OwnerKind.Admitted && bytes > 0) {
                ((IAllocationObserver)this).Acquire(bytes, backing);
                return;
            }
            RecordChange(bytes, backing);
        }

        // Physical accounting after a grant, or an observation of storage that already exists.
        internal void RecordChange(long bytes, MemoryBacking backing) {
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
                if (bytes < 0L) { m_account.SignalRoom(); }
                switch (m_kind) {
                    case OwnerKind.Segment:
                        m_account.m_deviceBlocks[((int)m_pool)] += bytes;
                        m_account.RaisePeak(pool: m_pool);
                        break;
                    case OwnerKind.Admitted:
                        m_account.m_bytes[((int)m_category)] += bytes;
                        m_account.m_named[((((int)m_pool) * Backings) + ((int)backing))] += bytes;
                        m_account.m_ownerHeld[OwnerIndex] += bytes;
                        MovePromise(change: (Promise - promise));
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
                m_account.m_ownerHeld[OwnerIndex] -= m_bytes;
                m_account.m_ownerPromised[OwnerIndex] -= promise;
                m_category = category;
                m_account.m_bytes[((int)m_category)] += m_bytes;
                m_account.m_ownerHeld[OwnerIndex] += m_bytes;
                m_account.m_ownerPromised[OwnerIndex] += promise;
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

                // A reservation within its floor holds what the holder cannot run without, judged on what the owners name
                // alone; past the floor it gives way, and the private bytes beyond the owners count too.
                var fitsBar = ((peak > floor) ? m_account.FitsHostBar(m_entry.Pool, extra) : m_account.FitsChargedHostBar(extra: extra, pool: m_entry.Pool));

                if (!fitsBar || ((peak > floor) && !m_account.Fits(extra: extra, pool: m_entry.Pool))) {
                    m_account.Refused(pool: m_entry.Pool, owner: Owner);
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

            owner.RecordChange(backing: backing, bytes: (bytes - m_recorded));
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
        public event Action? TrimRequested {
            add => account.TrimRequested += value;
            remove => account.TrimRequested -= value;
        }

        public GpuMemoryAdmission Admit(GpuMemorySegment segment, long bytes, string? payer, long payerBytes, bool givesWay) =>
            account.Admit(backend: backend, bytes: bytes, givesWay: givesWay, payer: payer, payerBytes: payerBytes, segment: segment);
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
