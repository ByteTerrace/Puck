using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.SdfVm;

/// <summary>What one residency's bake reports of a detail decision (<see cref="SdfResidencyGrant.Decide"/>).</summary>
/// <param name="Over">Whether the bytes the residency's draws name pass its budget, a finer tier waits within one
/// replacement of it, or its mesh region refuses a mesh the draws name. A pressing report presses the one bias at the
/// next decision, whichever residency makes it, unless the pool's limit has risen since.</param>
/// <param name="AnySpare">Whether the residency still has a group drawing a finer tier than its demand asks, landing a
/// coarser one or waiting its hold: detail it can give up before every demand is coarsened.</param>
/// <param name="CanLower">Whether the residency would stay under its budget with every demand half a rung finer, with
/// nothing owed, in flight or landing.</param>
/// <param name="Elapsed">The frames since the residency's last decision.</param>
public readonly record struct SdfDetailReport(bool Over, bool AnySpare, bool CanLower, int Elapsed);
/// <summary>
/// A presentation's selector: the one place the residencies of a device share its memory and its background GPU time,
/// in place of a share each. It holds the one detail bias every residency's bake coarsens its demand by
/// (<see cref="SdfMeshTier.Biased"/>), the budget each bake holds its meshes to, the reservation each mesh region grows
/// under, and the one budget the residencies' background GPU work draws on (<see cref="Work"/>).
/// <para>
/// The sum it holds is the process account's (<see cref="MemoryAccount"/>): in the pool the mesh regions lie in, what
/// every class holds and has promised stays under the pool's limit. What the limit leaves the regions, less room for the
/// largest replacement a region that grows by copy holds beside its buffer until the old one's readers finish (a mapped
/// region, <see cref="GpuRegion.Mapped"/>, grows and shrinks in place and holds none), is the elastic budget; <see cref="SdfMeshTier.BudgetEighths"/> of it is what the bakes fill. A residency's budget is
/// what its own draws name and its finer tiers in flight are expected to add, with an equal part of the room left, so
/// the budgets together are the whole and no two residencies are promised the same room; when the residencies together
/// name more than the budget, each gives up its part of the excess. The bias rises half a rung when the budget has
/// pressed with no residency holding detail it can still give up, and is taken back once every residency would fit half
/// a rung finer: one bias, so an over-budget picture is coarser by the same number of pixels in every world it shows.
/// </para>
/// <para>
/// Bytes are enforced by the account, never here: a region grows only after its grant reserved what it holds once grown,
/// the old and the new buffer together for a region that grows by copy (<see cref="SdfResidencyGrant.ReserveGrowth"/>), and the device's ledger admits the allocation
/// against that reservation. The selector's budgets steer detail so that a reservation is rarely refused. With no
/// device reading the pool has no limit and every budget is unbounded.
/// </para>
/// <para>
/// Invariant: storage a region's draws can do without (a product waiting beside the mesh it replaces, a mesh no list
/// names) never takes the region past its residency's share of the elastic budget, whatever room the pool shows
/// (<see cref="SdfResidencyGrant.HeldLimitBytes"/>): the shares together are the elastic budget, so the room a view
/// about to open needs is never filled with storage nothing gives back. <see cref="SdfResidencyGrant.ReserveGrowth"/>
/// is the one place a region's limits are made, and the region's arena the one place they refuse a placement
/// (<see cref="SdfMeshRegionArena.SetLimit"/>). The guarantee stops at what a draw names: a drawn mesh and a kept
/// floor are placed wherever the pool has room, since refusing either leaves a hole, and a region stands over its
/// share until the budget has coarsened its drawn meshes. The device memory held for a mapped region's chunks that no chunk is mapped to
/// (<see cref="GpuRegion.MappedSpareBytes"/>) is the regions' too: only a mapped region can take it.
/// </para>
/// <para>Thread-safe: the frame thread registers, reports and decides; any thread reads the bias and the line.</para>
/// </summary>
public sealed class SdfResidencySelector {
    /// <summary>The owner every mesh region's buffers are named under (<see cref="GpuObjectName.Owner"/>), apart from
    /// the tables' other objects, so the account reserves and classes the regions on their own.</summary>
    public const string RegionOwner = "sdf.mesh-region";
    /// <summary>The owner every mesh region's staging buffers are named under: transfer storage, which the account
    /// neither reserves nor classes with the regions, so a flush that stages more than the last is never refused as a
    /// region's growth is. On unified memory it lies in the pool the regions do, where it is fixed storage: admitted,
    /// counted, and past a full pool's limit counted as that.</summary>
    public const string RegionStagingOwner = "sdf.mesh-region-staging";
    /// <summary>The owner a background GPU job's buffers are created under (<see cref="SdfGpuWork"/>'s slots: points,
    /// samples, hits, readbacks): workspace, reserved before it is allocated and refused when its pool has no room,
    /// apart from the one job a service cannot progress without.</summary>
    public const string WorkOwner = "sdf.gpu-work";

    [SideTable(reason: "one selector per device, holding that device's residency state")]
    private static readonly ConditionalWeakTable<IGpuDeviceContext, SdfResidencySelector> Selectors = [];
    // How long a residency's report counts toward the bias after it was made, in seconds; a residency no frame prepares
    // stops steering it.
    private const double FreshSeconds = 2d;
    private readonly Lock m_gate = new();
    private readonly List<SdfResidencyGrant> m_grants = [];

    private readonly MemoryAccount m_account;

    private int m_bias;
    private int m_biasRaised;
    private int m_biasLowered;
    private int m_biasPressed;
    private int m_biasQuiet;

    private int m_biasWait = SdfMeshTier.BiasQuietFrames;
    private int m_biasSinceLowered = int.MaxValue;

    private long m_growthRefusals;
    // The device bytes held for mapped chunks that no chunk is mapped to, as a mapped region last reported them.
    private long m_spare;
    // Every report's serial, and the serial of the report the latest decision was made on: a report counts toward one
    // decision only (Decide).
    private long m_reports;
    private long m_decidedSerial;

    /// <summary>Initializes a new instance of the <see cref="SdfResidencySelector"/> class.</summary>
    /// <param name="account">The account the selector reads its pool from and reserves the regions in.</param>
    /// <param name="pool">The pool the mesh regions lie in
    /// (<see cref="GpuMemorySegments.ExpectedPool(bool, GpuMemoryRole)"/> of a device-local buffer).</param>
    /// <param name="time">The clock the budget and the reports' freshness read (<see cref="IGpuDeviceContext.Time"/>);
    /// <see langword="null"/> reads <see cref="TimeProvider.System"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="account"/> is <see langword="null"/>.</exception>
    public SdfResidencySelector(MemoryAccount account, PhysicalMemory pool, TimeProvider? time = null) {
        ArgumentNullException.ThrowIfNull(argument: account);

        m_account = account;
        Pool = pool;
        Time = (time ?? TimeProvider.System);
        Work = new SdfGpuWorkBudget(time: Time);
    }

    /// <summary>Gets the half rungs every residency's bake coarsens its demand by. Safe to read from any thread.</summary>
    public int Bias => Volatile.Read(location: ref m_bias);
    /// <summary>Gets the pool the mesh regions lie in.</summary>
    public PhysicalMemory Pool { get; }
    /// <summary>Gets the bytes the pool the mesh regions lie in holds and has promised past its limit, which follows the
    /// system's budget for the process less the usage the ledgers do not hold (<see cref="MemoryAccount.Record"/>), and a
    /// profile's bar; zero while the pool fits its limit or nothing bounds it. Past it, storage the account admitted
    /// because it cannot give way stands where the system may move memory out of the device behind it, and the
    /// presentation gives back what it can do without (<see cref="SdfWorldPasses.BeginFrame"/>).</summary>
    public long Excess => (m_account.TryReadTotals(held: out var held, limit: out var limit, pool: Pool, promised: out var promised)
        ? Math.Max(val1: 0L, val2: ((held + promised) - limit))
        : 0L);

    /// <summary>Gets the clock the presentation's background work is metered by: its budget, its services' waits and its
    /// lighting's ages.</summary>
    public TimeProvider Time { get; }
    /// <summary>Gets the one budget of GPU time and upload bytes the presentation's background GPU work draws on.</summary>
    public SdfGpuWorkBudget Work { get; }
    /// <summary>Gets the device's bottom-level trace structures, which every residency's lighting claims by geometry.</summary>
    internal SdfMeshTraceBottoms TraceBottoms { get; } = new();

    /// <summary>Returns a device's selector, the same one for every residency on the device, reading the process
    /// account.</summary>
    /// <param name="device">The device.</param>
    /// <returns>The selector.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is <see langword="null"/>.</exception>
    public static SdfResidencySelector Of(IGpuDeviceContext device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return Selectors.GetValue(key: device, createValueCallback: static device => new SdfResidencySelector(
            account: MemoryAccount.Shared,
            pool: GpuMemorySegments.ExpectedPool(role: GpuMemoryRole.DeviceLocal, unifiedMemory: device.MemoryProfile.UnifiedMemory),
            time: device.Time
        ));
    }
    /// <summary>Returns a reservation for what one background GPU service's jobs hold on the device: workspace under
    /// <see cref="WorkOwner"/>, in the pool the mesh regions lie in for its device-local buffers and in the host's for
    /// its host-visible ones.</summary>
    /// <returns>The reservation, holding nothing; disposing it releases it.</returns>
    public SdfGpuWorkHold ReserveWork() => new(
        device: m_account.ReserveDevice(memoryClass: MemoryClass.Workspace, owner: WorkOwner, pool: Pool),
        host: m_account.ReserveDevice(memoryClass: MemoryClass.Workspace, owner: WorkOwner, pool: PhysicalMemory.Host)
    );
    /// <summary>Registers a residency: its grant, which budgets its detail. The residency's meshes lie in the
    /// presentation's one region (<see cref="AttachStore"/>), which holds the reservation.</summary>
    /// <param name="name">The residency's name, for the selector's line.</param>
    /// <returns>The grant; disposing it takes it out of the sum.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public SdfResidencyGrant Register(string name) {
        ArgumentNullException.ThrowIfNull(argument: name);

        var grant = new SdfResidencyGrant(name: name, reservation: null, selector: this);

        lock (m_gate) { m_grants.Add(item: grant); }

        return grant;
    }
    // Registers the presentation's mesh region: the grant that holds its reservation.
    internal SdfResidencyGrant RegisterRegion() {
        var grant = new SdfResidencyGrant(name: "mesh-store", reservation: m_account.ReserveDevice(memoryClass: MemoryClass.Elastic, owner: RegionOwner, pool: Pool), selector: this);

        lock (m_gate) { m_region = grant; }

        return grant;
    }

    private SdfMeshRegionStore? m_store;
    // The region's grant while a store stands, and the bytes the residencies' names hold in it, each mesh once.
    private SdfResidencyGrant? m_region;
    private long m_regionNamed;

    /// <summary>Gets the presentation's mesh region, or <see langword="null"/> while no residency is attached.</summary>
    public SdfMeshRegionStore? Store { get { lock (m_gate) { return m_store; } } }

    /// <summary>Attaches a residency's tables to the presentation's one mesh region, creating the region for the first:
    /// the one door a residency's meshes reach device memory through, so a mesh any number of residencies name is
    /// placed once. A residency that records with another region-copy pipeline than the region's (its kernels were
    /// built again) gets a region of its own generation: the old one stays with the residencies still attached to
    /// it.</summary>
    /// <param name="device">The device.</param>
    /// <param name="copyPipeline">The device's region-copy pipeline the tables record with.</param>
    /// <param name="client">The tables.</param>
    /// <returns>The region the tables name their meshes in.</returns>
    internal SdfMeshRegionStore AttachStore(IGpuDeviceContext device, IGpuComputePipeline copyPipeline, SdfWorldTables client) {
        while (true) {
            SdfMeshRegionStore store;

            lock (m_gate) {
                if ((m_store is not { } held) || held.IsDisposed || !ReferenceEquals(objA: held.CopyPipeline, objB: copyPipeline)) {
                    held = new SdfMeshRegionStore(copyPipeline: copyPipeline, device: device, selector: this);
                    m_store = held;
                }
                store = held;
            }
            // The store's gate is never taken under the selector's: a staging holds the store's and asks the selector.
            // The store's last residency may leave between the two, which disposes it; the next turn makes another.
            if (store.TryAttach(client: client)) { return store; }
        }
    }
    /// <summary>Describes the sum the selector holds: the pool, its limit, held and promised bytes, each class's bytes,
    /// the elastic budget, the one bias with its steps, the growths the account refused, and each residency's grant
    /// beside what it names, the most its region may place and may hold, and what the region holds and has reserved. The floors and the support class are named and hold nothing until
    /// residency floors and observed use exist.</summary>
    /// <returns>One line of <c>key=value</c> fields.</returns>
    public string Describe() {
        var admission = m_account.ReadAdmission(pool: Pool);
        var line = new StringBuilder();

        lock (m_gate) {
            var usable = UsableOf(admission: admission);

            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $"selector pool={((Pool == PhysicalMemory.Device) ? "device" : "host")} residencies={m_grants.Count} limit={Bytes(bytes: admission.Limit)} held={admission.Held}B promised={admission.Promised}B over-limit={admission.OverLimit}B");
            // The regions are reserved as elastic storage whole; the draw records in them are the residencies' floors.
            var floors = FloorsOf();

            for (var index = 0; (index < MemoryClasses.Count); index++) {
                var bytes = ((admission.ClassHeld[index] + admission.ClassPromised[index]) + (((MemoryClass)index) switch {
                    MemoryClass.Floor => floors,
                    MemoryClass.Elastic => -floors,
                    _ => 0L,
                }));

                _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" {MemoryClasses.NameOf(memoryClass: ((MemoryClass)index))}={bytes}B");
            }
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" elastic-budget={Bytes(bytes: usable)} detail-bias={m_bias} bias-raised={m_biasRaised} bias-lowered={m_biasLowered} growth-refusals={m_growthRefusals}");

            var granted = 0L;

            foreach (var grant in m_grants) {
                var budget = BudgetOf(grant: grant, usable: usable);
                var share = Whole(budget: budget);

                if (share != long.MaxValue) { granted += share; }
                _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" | grant {grant.Name}={((share == long.MaxValue) ? "unbounded" : $"{share}B")} named={grant.Named}B filled={grant.Filled}B");
            }
            if (m_region is { } region) {
                _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" | region named={m_regionNamed}B region-limit={Bytes(bytes: ((region.LimitBytes == long.MaxValue) ? null : region.LimitBytes))} region-held-limit={Bytes(bytes: ((region.HeldLimitBytes == long.MaxValue) ? null : region.HeldLimitBytes))} region-held={region.Capacity}B region-reserved={region.Reserved}B region-retiring={region.Retiring}B region-spare={m_spare}B");
            }
            _ = line.Append(provider: CultureInfo.InvariantCulture, handler: $" | grants={((usable is null) ? "unbounded" : $"{granted}B")}");
        }

        return line.ToString();
    }
    /// <summary>Reads the bytes each registered residency is granted: its budget scaled from
    /// <see cref="SdfMeshTier.BudgetEighths"/> to the whole, <see cref="long.MaxValue"/> where nothing bounds the
    /// pool.</summary>
    /// <returns>The grants, in registration order, and the pool's limit.</returns>
    public (IReadOnlyList<long> Grants, long? Limit) ReadGrants() {
        var admission = m_account.ReadAdmission(pool: Pool);

        lock (m_gate) {
            var usable = UsableOf(admission: admission);
            var grants = new long[m_grants.Count];

            for (var index = 0; (index < grants.Length); index++) {
                var budget = BudgetOf(grant: m_grants[index], usable: usable);

                grants[index] = Whole(budget: budget);
            }

            return (grants, admission.Limit);
        }
    }

    private static string Bytes(long? bytes) => ((bytes is { } value) ? string.Create(provider: CultureInfo.InvariantCulture, handler: $"{value}B") : "unbounded");

    internal void Release(SdfResidencyGrant grant) {
        lock (m_gate) {
            _ = m_grants.Remove(item: grant);
            if (ReferenceEquals(objA: m_region, objB: grant)) { m_region = null; m_regionNamed = 0L; }
        }
        PublishFloors();
    }
    internal void NoteRegionNamed(SdfResidencyGrant grant, long bytes) {
        lock (m_gate) { if (ReferenceEquals(objA: m_region, objB: grant)) { m_regionNamed = bytes; } }
    }
    // The most bytes the region may place, which bounds every residency's budget.
    internal long RegionLimitBytes {
        get { lock (m_gate) { return (m_region?.LimitBytes ?? long.MaxValue); } }
    }
    internal void NoteSpare(long bytes) {
        lock (m_gate) { m_spare = Math.Max(val1: 0L, val2: bytes); }
    }
    internal void CountGrowthRefusal() {
        lock (m_gate) { m_growthRefusals++; }
    }
    // Records the residencies' floors in the account, which the host passes to the device's own reservation.
    internal void PublishFloors() {
        long floors;

        lock (m_gate) { floors = FloorsOf(); }
        m_account.RecordFloor(bytes: floors, pool: Pool);
    }

    // Under m_gate.
    private long FloorsOf() {
        var floors = 0L;

        foreach (var grant in m_grants) { floors += grant.FloorBytes; }

        return floors;
    }

    // A budget scaled from SdfMeshTier.BudgetEighths to the whole: the bytes the residency's region may hold.
    private static long Whole(long budget) => ((budget == long.MaxValue) ? long.MaxValue : ((budget / SdfMeshTier.BudgetEighths) * 8L));
    // The bytes a residency's mesh region may hold: its part of the elastic budget, whole; long.MaxValue where nothing
    // bounds the pool. The parts together are the elastic budget, so the regions together never hold more than the
    // limit leaves them once every region keeps to its part.
    internal long ShareOf(SdfResidencyGrant grant) {
        var bounded = m_account.TryReadTotals(held: out var held, limit: out var limit, pool: Pool, promised: out var promised);

        lock (m_gate) {
            // The region holds every residency's meshes: its share is the whole of what the limit leaves the regions.
            if (ReferenceEquals(objA: grant, objB: m_region)) { return (bounded ? UsableOf(held: held, limit: limit, promised: promised) : long.MaxValue); }

            return Whole(budget: BudgetOf(grant: grant, usable: (bounded ? UsableOf(held: held, limit: limit, promised: promised) : null)));
        }
    }
    // The bytes a bake may fill, for a residency that names and expects the given bytes.
    internal long Budget(SdfResidencyGrant grant, long named, long filled) {
        var bounded = m_account.TryReadTotals(held: out var held, limit: out var limit, pool: Pool, promised: out var promised);

        lock (m_gate) {
            grant.Named = named;
            grant.Filled = Math.Max(val1: named, val2: filled);

            return BudgetOf(grant: grant, usable: (bounded ? UsableOf(held: held, limit: limit, promised: promised) : null));
        }
    }

    // Under m_gate: what the limit leaves every mesh region together, less room for the largest replacement a region
    // that grows by copy would hold beside itself (a mapped region holds none); null where nothing bounds the pool. What
    // the regions have reserved is theirs already, so it is taken back out of what the pool holds and has promised.
    private long? UsableOf(MemoryAdmissionReading admission) => ((admission.Limit is { } limit)
        ? UsableOf(held: admission.Held, limit: limit, promised: admission.Promised)
        : null);
    private long UsableOf(long limit, long held, long promised) {
        var reserved = (m_region?.Reserved ?? 0L);
        var largest = ((m_region is { Mapped: false } region) ? region.Capacity : 0L);

        // The spare chunk memory is held, and only a mapped region takes it, so it is the regions' with what they reserved.
        return Math.Max(val1: 0L, val2: ((limit - Math.Max(val1: 0L, val2: (((held + promised) - reserved) - m_spare))) - largest));
    }
    // Under m_gate: one residency's budget: what it fills, and an equal part of the room the budget has left or its
    // part of the excess.
    private long BudgetOf(SdfResidencyGrant grant, long? usable) {
        if (usable is not { } bytes) { return long.MaxValue; }

        var budget = ((bytes / 8L) * SdfMeshTier.BudgetEighths);
        var filled = 0L;

        foreach (var other in m_grants) { filled += other.Filled; }

        var room = (budget - filled);

        if (room >= 0L) { return (grant.Filled + (room / Math.Max(val1: 1, val2: m_grants.Count))); }

        return Math.Max(val1: 0L, val2: (grant.Filled + ((long)((((double)room) * grant.Filled) / Math.Max(val1: 1L, val2: filled)))));
    }

    // Moves the one bias on the report of the residency that names the most among those reporting: it presses when the
    // residencies together name more than the budget holds or any fresh report presses (its draws past its own budget,
    // or a mesh its region refuses), rises half a rung once the budget has pressed for the hold with no residency holding
    // detail it can give up, and falls half a rung once every residency has fit a half rung finer for the quiet wait,
    // which doubles when a rise follows a fall closely.
    internal int Decide(SdfResidencyGrant grant, in SdfDetailReport report) {
        var now = Time.GetTimestamp();
        var fresh = ((long)(FreshSeconds * Time.TimestampFrequency));
        var bounded = m_account.TryReadTotals(held: out var held, limit: out var limit, pool: Pool, promised: out var promised);

        var limitNow = (bounded ? limit : long.MaxValue);

        lock (m_gate) {
            grant.Report = report;
            grant.ReportedAt = now;
            grant.ReportSerial = ++m_reports;
            grant.ReportLimit = limitNow;

            var primary = grant;
            // Whether the budget presses is read from the limit as it stands and what every residency names, from the
            // deciding residency's own report, and from each other residency's report made since the last decision
            // under a limit no higher than now: a residency whose own region refuses a mesh, or whose draws pass its own
            // budget, presses the one bias even when another residency names more and decides. Each such report
            // presses one decision; one made before the limit was raised (a cap lifted, a budget restored) presses
            // none, since it judged a budget that no longer stands.
            var over = report.Over;
            var anySpare = false;
            var canLower = true;
            var named = 0L;

            foreach (var other in m_grants) {
                named += other.Named;
                if ((other.ReportedAt == 0) || ((now - other.ReportedAt) > fresh)) { continue; }
                if (other.Named > primary.Named) { primary = other; }
                if ((other.ReportSerial > m_decidedSerial) && (other.ReportLimit >= limitNow)) { over |= other.Report.Over; }
                anySpare |= other.Report.AnySpare;
                canLower &= other.Report.CanLower;
            }
            // The residencies name one region, where a mesh several of them name lies once.
            if (m_region is not null) { named = Math.Min(val1: named, val2: m_regionNamed); }
            if (bounded) { over |= (named > ((UsableOf(held: held, limit: limit, promised: promised) / 8L) * SdfMeshTier.BudgetEighths)); }
            if (!ReferenceEquals(objA: primary, objB: grant)) { return m_bias; }
            m_decidedSerial = grant.ReportSerial;

            var elapsed = report.Elapsed;
            var bias = m_bias;

            if (over) {
                m_biasQuiet = 0;
                if (anySpare) {
                    m_biasPressed = 0;
                } else if (((m_biasPressed += elapsed) >= SdfMeshTier.HoldFrames) && (bias < SdfMeshTier.MaxBias)) {
                    Volatile.Write(location: ref m_bias, value: (bias + 1));
                    m_biasRaised++;
                    m_biasPressed = 0;
                    // A step up soon after a step down is a bounce: the next step down waits twice as long.
                    if (m_biasSinceLowered < m_biasWait) { m_biasWait = ((int)Math.Min(val1: (2L * m_biasWait), val2: (1L << 20))); }
                    m_biasSinceLowered = int.MaxValue;
                }
            } else {
                m_biasPressed = 0;
                if ((bias > 0) && canLower) {
                    if ((m_biasQuiet += elapsed) >= m_biasWait) {
                        Volatile.Write(location: ref m_bias, value: (bias - 1));
                        m_biasLowered++;
                        m_biasQuiet = 0;
                        m_biasSinceLowered = 0;
                    }
                } else {
                    m_biasQuiet = 0;
                }
            }
            if (m_biasSinceLowered < int.MaxValue) { m_biasSinceLowered = ((int)Math.Min(val1: (((long)m_biasSinceLowered) + elapsed), val2: (int.MaxValue - 1L))); }

            return m_bias;
        }
    }

    /// <summary>Gets the bias's steps up and down since the selector was made.</summary>
    public (int Raised, int Lowered) BiasSteps {
        get {
            lock (m_gate) { return (m_biasRaised, m_biasLowered); }
        }
    }
}
/// <summary>
/// One residency's part of its presentation's selector (<see cref="SdfResidencySelector.Register"/>): the reservation
/// its mesh region grows under, and the budget and bias its bake holds its detail to.
/// <para>
/// Invariant: the region's buffers are allocated only under the grant's reservation. A growth reserves what the region
/// holds once grown before any of it is allocated (<see cref="ReserveGrowth"/>, <see cref="Grown"/>): a mapped region's
/// chunks (<see cref="Mapped"/>), or the buffer that stands and the one that replaces it together. The reservation falls
/// to what stands once the replaced buffer's or the unmapped chunks' readers finish (<see cref="Released"/>). The region's staging is another owner's memory
/// (<see cref="SdfResidencySelector.RegionStagingOwner"/>), outside the reservation on every device.
/// </para>
/// Called on the frame thread, apart from the reads the selector's line makes.
/// </summary>
public sealed class SdfResidencyGrant : IDisposable {
    // The region's reservation; null for a residency's grant, which budgets detail and reserves nothing.
    private readonly MemoryAccount.DeviceReservation? m_reservation;
    private readonly SdfResidencySelector m_selector;

    private long m_capacity;
    private long m_limit = long.MaxValue;
    private long m_heldLimit = long.MaxValue;
    private long m_floor;
    private long m_retiring;
    private long m_reserved;
    private long m_releases;
    private bool m_disposed;

    internal SdfResidencyGrant(SdfResidencySelector selector, string name, MemoryAccount.DeviceReservation? reservation) {
        m_selector = selector;
        Name = name;
        m_reservation = reservation;
    }

    /// <summary>Gets the half rungs the residency's bake coarsens its demand by: the selector's one bias.</summary>
    public int Bias => m_selector.Bias;
    /// <summary>Gets or sets the residency's name, which the selector's line prints.</summary>
    public string Name { get; set; }
    /// <summary>Gets the selector the grant belongs to.</summary>
    public SdfResidencySelector Selector => m_selector;
    /// <summary>Gets or sets whether the residency's region is mapped (<see cref="GpuRegion.Mapped"/>): it grows and
    /// shrinks in place, so its reservation covers its own chunks alone and the selector leaves no room for a replacement
    /// beside it. Set once the region exists, before its first growth.</summary>
    public bool Mapped { get; set; }

    // The bytes the region's standing buffer holds, those of replaced buffers waiting on their readers, and the peak
    // reserved for both and the staging. Read by the selector under its gate.
    internal long Capacity => Volatile.Read(location: ref m_capacity);
    internal long FloorBytes => Volatile.Read(location: ref m_floor);
    internal long Reserved => Volatile.Read(location: ref m_reserved);
    internal long Retiring => Volatile.Read(location: ref m_retiring);

    // What the residency's draws name and what they are expected to name once its finer tiers land; written under the
    // selector's gate.
    internal long Named;
    internal long Filled;
    internal SdfDetailReport Report;
    internal long ReportedAt;
    // The report's serial among the selector's reports, and the pool limit it was made under (long.MaxValue unbounded).
    internal long ReportSerial;
    internal long ReportLimit;

    /// <summary>Records the bytes the region's first buffer holds and reserves them with its staging as the
    /// reservation's floor: storage a residency cannot draw without, reserved whatever the pool holds.</summary>
    /// <param name="capacityBytes">The bytes the region's buffer holds.</param>
    public void Created(long capacityBytes) {
        Volatile.Write(location: ref m_capacity, value: capacityBytes);
        ReserveStanding();
    }
    /// <summary>Records the bytes of the region the residency cannot draw without: its draw records. The selector adds
    /// them into the floors it names and passes to the device's own reservation.</summary>
    /// <param name="bytes">The bytes of the region's reserved draw records.</param>
    public void Floor(long bytes) {
        if (Volatile.Read(location: ref m_floor) == bytes) { return; }
        Volatile.Write(location: ref m_floor, value: bytes);
        m_selector.PublishFloors();
    }
    /// <summary>Reserves room for the region to grow before a list is planned into it, and returns the most bytes the
    /// region may place once it has (<see cref="LimitBytes"/>): the standing buffer's bytes when the pool has no room for
    /// a replacement beside it, and otherwise as much as the pool has room for, at most <paramref name="mostBytes"/>,
    /// with the mapped region's unbacked interior beside either. It also sets what the region may hold beyond the meshes
    /// its draws name (<see cref="HeldLimitBytes"/>). The reservation covers the standing buffer and a
    /// replacement of the returned size, so the growth that follows is admitted; <see cref="Grown"/> or
    /// <see cref="Kept"/> then lowers it to what was built.</summary>
    /// <param name="capacityBytes">The bytes the region's standing buffer holds (a mapped region's backed chunks).</param>
    /// <param name="mostBytes">The most bytes a region holds on any device.</param>
    /// <param name="unbackedBytes">The bytes inside a mapped region's size that hold no device memory (chunks an earlier
    /// fit gave back): address range a placement may use, whose backing the ledger admits when a placement needs it.
    /// Zero for a region that grows by replacement.</param>
    /// <param name="spareBytes">The device bytes already held for mapped chunks that no chunk is mapped to
    /// (<see cref="GpuRegion.MappedSpareBytes"/>): room a mapped region grows into with nothing allocated, which the
    /// pool counts as held and no other storage can take. Zero for a region that grows by replacement.</param>
    /// <returns>The most bytes the region may place, at least <paramref name="capacityBytes"/> and at most
    /// <paramref name="mostBytes"/>.</returns>
    public long ReserveGrowth(long capacityBytes, long mostBytes, long unbackedBytes = 0L, long spareBytes = 0L) {
        Volatile.Write(location: ref m_capacity, value: capacityBytes);
        if (Mapped) { m_selector.NoteSpare(bytes: spareBytes); }

        var room = Room;
        var grown = Math.Max(val1: capacityBytes, val2: (mostBytes - unbackedBytes));
        // A mapped region grows by the room and the spare chunk memory alone; any other region's replacement takes the
        // room beside what stands.
        var limit = Math.Min(val1: grown, val2: ((room == long.MaxValue) ? grown : (Mapped ? ((capacityBytes + room) + spareBytes) : room)));
        // Growth into the spare chunk memory allocates nothing, so only the growth past it is promised.
        var promised = Math.Max(val1: capacityBytes, val2: (limit - (Mapped ? spareBytes : 0L)));

        if (!((limit > capacityBytes) && Reserve(floor: Standing, peak: Beside(bytes: promised)))) {
            ReserveStanding();
            limit = capacityBytes;
        }
        // The room the pool shows now is not all the region's: the fixed storage of a view about to open takes it. What
        // the region may hold beyond the meshes its draws name is the residency's share of the elastic budget.
        Volatile.Write(location: ref m_heldLimit, value: Math.Min(val1: limit, val2: Share()));
        limit = Math.Min(val1: mostBytes, val2: (limit + unbackedBytes));
        Volatile.Write(location: ref m_limit, value: limit);

        return limit;
    }
    /// <summary>Holds the region to what it holds after a growth was refused, in place of <see cref="ReserveGrowth"/>:
    /// the reservation is lowered to what stands, <see cref="LimitBytes"/> becomes <paramref name="limitBytes"/> and
    /// <see cref="HeldLimitBytes"/> what the standing buffer holds or the residency's share when that is less, so
    /// the residency's budget presses its bake to coarsen until the named meshes fit what the region holds.</summary>
    /// <param name="limitBytes">The bytes the region holds, its unbacked interior included.</param>
    /// <returns><paramref name="limitBytes"/>.</returns>
    public long HoldStanding(long limitBytes) {
        ReserveStanding();
        Volatile.Write(location: ref m_heldLimit, value: Math.Min(val1: Capacity, val2: Share()));
        Volatile.Write(location: ref m_limit, value: limitBytes);

        return limitBytes;
    }
    // The residency's share of the elastic budget, never under the draw records it cannot draw without.
    private long Share() => Math.Max(val1: m_selector.ShareOf(grant: this), val2: FloorBytes);

    /// <summary>Gets the most device bytes the region may hold while it places storage its draws can do without (a
    /// product waiting beside the mesh it replaces), as <see cref="ReserveGrowth"/> last answered: the residency's share
    /// of the elastic budget, or less where the pool has no room for it; <see cref="long.MaxValue"/> before the first
    /// answer or where nothing bounds the pool. It can be less than the region holds, when the share fell since the
    /// region grew (a view opened and took the pool's room): the region then gives that storage back
    /// (<see cref="SdfMeshRegionArena.SetLimit"/>). The meshes the draws name are held to the same share by the
    /// residency's budget (<see cref="Budget"/>, <see cref="SdfMeshTier.BudgetEighths"/> of it), which coarsens them
    /// over frames, never by a refusal here.</summary>
    public long HeldLimitBytes => Volatile.Read(location: ref m_heldLimit);
    /// <summary>Gets the most bytes the reservation's peak may be raised by now (the pool's room), or
    /// <see cref="long.MaxValue"/> where nothing bounds the pool.</summary>
    public long Room => (m_reservation?.Room ?? long.MaxValue);
    /// <summary>Gets how many replaced buffers' releases the grant has recorded (<see cref="Released"/>), a count that
    /// only rises.</summary>
    public long Releases => Volatile.Read(location: ref m_releases);
    /// <summary>Gets the most bytes the region may place, as <see cref="ReserveGrowth"/> last answered:
    /// <see cref="long.MaxValue"/> before the first answer. The region's arena refuses a mesh past it, and the
    /// residency's budget (<see cref="Budget"/>) never passes <see cref="SdfMeshTier.BudgetEighths"/> of it, so the
    /// bake coarsens what the views can spare before the arena refuses.</summary>
    public long LimitBytes => Volatile.Read(location: ref m_limit);
    /// <summary>Records a growth or a shrink by replacement: the region's buffer of <paramref name="oldBytes"/> was
    /// replaced by one of <paramref name="newBytes"/> and waits on its readers. The reservation is lowered to the two
    /// together.</summary>
    /// <param name="oldBytes">The bytes of the replaced buffer.</param>
    /// <param name="newBytes">The bytes of the buffer that stands.</param>
    public void Grown(long oldBytes, long newBytes) {
        Volatile.Write(location: ref m_capacity, value: newBytes);
        Volatile.Write(location: ref m_retiring, value: (Retiring + oldBytes));
        m_reservation?.Retiring(bytes: Retiring);
        ReserveStanding();
    }
    /// <summary>Records that the region kept its buffer after <see cref="ReserveGrowth"/>: the list fit, or the
    /// replacement was refused. The reservation is lowered to what stands.</summary>
    /// <param name="refused">Whether a replacement was attempted and the account or the device refused it.</param>
    public void Kept(bool refused = false) {
        if (refused) { m_selector.CountGrowthRefusal(); }
        ReserveStanding();
    }
    /// <summary>Reserves a replacement of <paramref name="newBytes"/> beside the standing buffer, for a shrink, which
    /// holds both until the old one's readers finish.</summary>
    /// <param name="newBytes">The bytes of the smaller buffer.</param>
    /// <returns><see langword="true"/> when the replacement may be created; <see langword="false"/> leaves the region as
    /// it is.</returns>
    public bool TryReserveReplacement(long newBytes) => Reserve(floor: Standing, peak: Beside(bytes: newBytes));
    /// <summary>Reserves a region of <paramref name="newBytes"/> as the reservation's floor, whatever the pool holds: the
    /// growth the region's draw records need, which a residency cannot draw without; for a region that grows by
    /// replacement, beside the standing buffer. The ledger admits the growth past a limit and counts it.</summary>
    /// <param name="newBytes">The bytes of the region the draw records need.</param>
    public void ReserveFloorGrowth(long newBytes) => _ = Reserve(floor: Beside(bytes: newBytes), peak: Beside(bytes: newBytes));
    /// <summary>Records that a replaced buffer's readers finished and it was released: its bytes leave the
    /// reservation.</summary>
    /// <param name="bytes">The bytes of the released buffer.</param>
    public void Released(long bytes) {
        _ = Interlocked.Increment(location: ref m_releases);
        Volatile.Write(location: ref m_retiring, value: Math.Max(val1: 0L, val2: (Retiring - bytes)));
        if (m_disposed) { return; }
        m_reservation?.Retiring(bytes: Retiring);
        ReserveStanding();
    }
    /// <summary>Returns the bytes the residency's bake may fill: what its draws name and its finer tiers in flight are
    /// expected to add, and its part of the room the selector's budget has left, never past
    /// <see cref="SdfMeshTier.BudgetEighths"/> of the most its region may place (<see cref="LimitBytes"/>), so a list
    /// the region would refuse presses the budget and the bake coarsens before a mesh is left out;
    /// <see cref="long.MaxValue"/> where nothing bounds the pool or the region.</summary>
    /// <param name="named">The bytes the residency's draws name.</param>
    /// <param name="filled">The bytes they are expected to name once every finer replacement in flight has landed.</param>
    /// <returns>The budget, in bytes.</returns>
    public long Budget(long named, long filled) {
        var budget = m_selector.Budget(filled: filled, grant: this, named: named);
        var limit = m_selector.RegionLimitBytes;

        return ((limit == long.MaxValue) ? budget : Math.Min(val1: budget, val2: ((limit / 8L) * SdfMeshTier.BudgetEighths)));
    }
    /// <summary>Records the bytes the residencies' names hold in the region, each mesh once: what the selector's one
    /// bias is pressed by, in place of the residencies' own counts, which count a mesh several of them name more than
    /// once. The region's grant alone records it.</summary>
    /// <param name="bytes">The bytes the names hold.</param>
    public void RegionNamed(long bytes) => m_selector.NoteRegionNamed(bytes: bytes, grant: this);
    /// <summary>Reports a detail decision and returns the bias every demand is coarsened by from now on.</summary>
    /// <param name="report">What the residency's bake decided.</param>
    /// <returns>The selector's bias.</returns>
    public int Decide(in SdfDetailReport report) => m_selector.Decide(grant: this, report: in report);

    // What the region holds whatever the pool says: its standing buffer and the replaced ones waiting on their
    // readers. Reserved as the reservation's floor, so a full pool reads over its limit and the residency draws.
    private long Standing => (Capacity + Retiring);

    // What a region of the given bytes holds at once with what stands: a mapped region's chunks are its whole, beside the
    // chunks still waiting on their readers; any other region's replacement stands beside the buffer it replaces.
    private long Beside(long bytes) => (Mapped ? Math.Max(val1: Standing, val2: (Retiring + bytes)) : (Standing + bytes));

    private void ReserveStanding() => _ = Reserve(floor: Standing, peak: Standing);
    // Sets the reservation's peak; a raise past the floor that the pool has no room for changes nothing.
    private bool Reserve(long peak, long floor) {
        if (m_disposed || (m_reservation is null) || !m_reservation.TryReserve(floor: floor, peak: peak)) { return false; }
        Volatile.Write(location: ref m_reserved, value: peak);

        return true;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        m_selector.Release(grant: this);
        m_reservation?.Dispose();
        Volatile.Write(location: ref m_reserved, value: 0L);
    }
}
