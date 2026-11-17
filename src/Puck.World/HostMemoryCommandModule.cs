using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.World;

/// <summary>
/// The host process's memory read-back: <c>host.memory</c> echoes the process's working set and private bytes beside
/// the managed heap the last garbage collection left (its size, committed and fragmented bytes, and each generation's
/// size: gen0, gen1, gen2, the large and the pinned object heaps) and the bytes every thread has allocated so far, so the managed share of a large working set is read
/// apart from native, mapped and driver memory, then the process uptime and what the JIT has compiled so far (methods,
/// IL bytes, compilation time), which read at boot give its first-use JIT cost. Between them it measures the process
/// account and prints each pool's physical rows, which alone add, with the parts their owners name and their residuals
/// (<see cref="MemoryAccount.ReadPools"/>), and the device memory the owner families hold. <c>host.memory heap</c> names
/// the large object heap's tenants from a suspended walk (<see cref="ManagedHeapWalk"/>). <c>host.memory collect</c> first runs a
/// full blocking collection that compacts the large object heap, so the heap it reports is what is still reachable.
/// Presentation-independent; it reads the process, never the simulation.
/// </summary>
internal sealed class HostMemoryCommandModule(IAllocator allocator, IEnumerable<IWorkCounterSource> sources) : ICommandModule {
    private readonly IWorkCounterSource[] m_memorySources = [.. sources.Where(predicate: static source => WorkCounterReport.Matches(name: source.Name, filter: "memory")).OrderBy(static source => source.Name, StringComparer.Ordinal)];

    private string DescribeOwners(int limit) {
        var snapshot = MemoryAccount.Shared.ReadOwners();
        var account = snapshot.Account;
        var owners = snapshot.Owners.OrderByDescending(keySelector: static owner => owner.Owned)
            .ThenByDescending(keySelector: static owner => owner.Reserved).ThenBy(static owner => owner.Name, StringComparer.Ordinal).ToArray();
        var shown = ((limit == 0) ? owners.Length : Math.Min(val1: limit, val2: owners.Length));
        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"[host.memory: charged/reserved owners shown={shown} total={owners.Length} | account bytes owned={account.Owned} live={account.Live} cached={account.Cached} retirement={account.Retirement} metadata={account.Metadata} reserved={account.Reserved} budget={account.Budget} headroom={account.Headroom}\n");
        for (var index = 0; (index < shown); index++) {
            var owner = owners[index];

            text.Append(CultureInfo.InvariantCulture, $"owner {JsonSerializer.Serialize(owner.Name)} category={owner.Category} pool={PoolName(pool: owner.Pool)} admitted={(owner.Observed ? "no" : "yes")} {(owner.Row ? "row=device-blocks" : $"names native-allocator={owner.Rows.NativeAllocator} native-direct={owner.Rows.NativeDirect} managed={owner.Rows.Managed} device-blocks={owner.Rows.DeviceBlocks}")} owned={owner.Owned} reserved={owner.Reserved} peak-owned={owner.PeakOwned} reservation-peak={owner.ReservationPeak} disposed={owner.Disposed.ToString().ToLowerInvariant()} class={MemoryClasses.NameOf(memoryClass: owner.Class)}{(owner.Forced ? " forced=yes" : string.Empty)}\n");
        }
        var omittedOwned = 0L;
        var omittedReserved = 0L;

        for (var index = shown; (index < owners.Length); index++) {
            omittedOwned += owners[index].Owned;
            omittedReserved += owners[index].Reserved;
        }
        text.Append(CultureInfo.InvariantCulture, $"omitted owners={(owners.Length - shown)} owned={omittedOwned} reserved={omittedReserved}\n");
        foreach (var source in m_memorySources) { WorkCounterReport.AppendSection(builder: text, source: source); }
        text.Append(value: "Backend counters are separate observations; host-visible-device-local bytes are a subset of device-local bytes, and host-visible bytes are disjoint from both. An owner names part of the rows it lists; a row=device-blocks owner is a segment's physical row. The pools' physical rows, which alone add, are in host.memory.]");
        return text.ToString();
    }
    private static string Mebibytes(long bytes) =>
        (bytes / (1L << 20)).ToString(provider: CultureInfo.InvariantCulture);
    private static string PoolName(PhysicalMemory pool) => ((pool == PhysicalMemory.Device) ? "device" : "host");
    private static string Bytes(long? bytes) => ((bytes is { } value) ? value.ToString(provider: CultureInfo.InvariantCulture) : "none");
    private static string Bytes(ulong? bytes) => ((bytes is { } value) ? value.ToString(provider: CultureInfo.InvariantCulture) : "none");
    // The most family rows a reading prints; the rest are summed on one line.
    private const int FamilyRows = 16;

    // One pool and its physical rows: each row's bytes, the part its owners name and its residual.
    private static void AppendPool(StringBuilder text, MemoryPoolReading pool, string note) {
        var admission = pool.Admission;

        text.Append(CultureInfo.InvariantCulture, $" | pool {PoolName(pool: pool.Pool)} physical={pool.Physical} complete={(pool.Complete ? "yes" : "no")} budget={Bytes(pool.Budget)} spend-limit={Bytes(pool.SpendLimit)} held={admission.Held} peak={admission.Peak} retired={admission.Retired} promised={admission.Promised} room={Bytes(admission.Room)} over-limit={admission.OverLimit} forced={admission.Forced} forced-jobs={admission.ForcedJobs} over-limit-grants={admission.OverLimitGrants} over-limit-granted-bytes={admission.OverLimitBytes} refusals={admission.Refusals} device-refusals={admission.DeviceRefusals} pressure-trims={admission.PressureTrims} floors={admission.Floor} admitted={pool.Admitted} reserved={pool.Reserved}{note}");
        text.Append(CultureInfo.InvariantCulture, $" | classes {PoolName(pool: pool.Pool)}");
        for (var index = 0; (index < MemoryClasses.Count); index++) {
            text.Append(CultureInfo.InvariantCulture, $" {MemoryClasses.NameOf(memoryClass: ((MemoryClass)index))}={admission.ClassHeld[index]}+{admission.ClassPromised[index]}");
        }
        foreach (var row in pool.Rows) {
            text.Append(CultureInfo.InvariantCulture, $" | row {PoolName(pool: row.Pool)}.{row.Name} bytes={Bytes(row.Bytes)} named={row.Named} residual={Bytes(row.Residual)} ({ResidualName(row: row)})");
        }
    }
    // What a row's residual is.
    private static string ResidualName(MemoryRowReading row) => row.Backing switch {
        MemoryBacking.DeviceBlocks => "residual: room inside shared blocks and emptied blocks kept for reuse",
        MemoryBacking.NativeAllocator => "residual: allocator slack, metadata and unnamed native payloads",
        MemoryBacking.NativeDirect => "the owners' charges are the row",
        MemoryBacking.Managed => "residual: unnamed managed tenants, kept pool arrays and fragmentation",
        _ => "the walk's private bytes less the allocator, native-direct and managed rows: code and loader heaps, stacks, driver memory",
    };
    // The pools, their physical rows and residuals, the host sample, the device reading the account last recorded, and
    // each segment's API figures beside the bytes the backend's ledger placed in it; a segment's usage past its ledger row
    // is driver, swapchain and uncounted memory.
    private string DescribePools() {
        MemoryAccount.Shared.Measure(sample: HostMemorySample.Take(allocator: allocator, walk: true));
        var pools = MemoryAccount.Shared.ReadPools();
        var reading = pools.DeviceReading;
        var unified = (reading.Layout?.UnifiedMemory ?? false);
        var works = m_memorySources.OfType<GpuDeviceMemoryWork>().ToArray();
        var ledgers = works.SelectMany(selector: static work => work.ReadSegments().Select(selector: row => (work.Backend, Row: row))).ToArray();
        var families = works.SelectMany(selector: static work => work.ReadFamilies().Select(selector: row => (work.Backend, Row: row)))
            .OrderByDescending(keySelector: static entry => entry.Row.Held).ThenBy(keySelector: static entry => entry.Row.Family, comparer: StringComparer.Ordinal).ToArray();
        var text = new StringBuilder();
        var sample = pools.Sample;

        AppendPool(note: (unified ? " unified=yes (the device's segments lie in this pool and are counted once)" : string.Empty), pool: pools.Host, text: text);
        if (pools.Device is { Present: true } device) {
            AppendPool(note: string.Empty, pool: device, text: text);
        } else {
            text.Append(CultureInfo.InvariantCulture, $" | pool device absent ({(unified ? "unified memory: one pool" : $"device reading {reading.State.ToString().ToLowerInvariant()}")})");
        }
        text.Append(CultureInfo.InvariantCulture, $" | sample age={((long?)pools.SampleAge?.TotalMilliseconds)?.ToString(provider: CultureInfo.InvariantCulture) ?? "none"}ms managed-collection={(sample?.ManagedCollection.ToString(provider: CultureInfo.InvariantCulture) ?? "none")} negative-residuals={pools.NegativeResiduals}");
        if (sample?.Walk is { } walk) {
            text.Append(CultureInfo.InvariantCulture, $" | walk private={walk.PrivateBytes} ({(walk.PrivateCommitted ? "committed" : "resident anonymous and swap")}) mapped={walk.MappedBytes} image={walk.ImageBytes} regions={walk.Regions}; host rows in it: native-allocator, native-direct, managed and runtime-and-driver; the driver may also map device blocks into it");
        } else {
            text.Append(value: " | walk none (this platform cannot be walked)");
        }
        text.Append(CultureInfo.InvariantCulture, $" | device reading={reading.State.ToString().ToLowerInvariant()} serial={reading.Serial} estimated={(reading.Estimated ? "yes" : "no")}");
        for (var index = 0; (index < reading.Segments.Count); index++) {
            var segment = reading.Segments[index];
            var limit = ((index < pools.SegmentLimits.Count) ? pools.SegmentLimits[index].SpendLimit : (long?)null);
            var ledger = ledgers.Where(predicate: entry => (entry.Row.Segment == segment.Segment)).Select(selector: static entry => entry.Row).FirstOrDefault();
            var beyond = ((segment.Usage is { } usage) ? (long?)(((long)usage) - ledger.Held) : null);

            text.Append(CultureInfo.InvariantCulture, $" | segment {segment.Segment.Name} pool={PoolName(pool: segment.Segment.Pool)} budget={segment.Budget} spend-limit={Bytes(limit)} usage={Bytes(segment.Usage)} available-for-reservation={Bytes(segment.AvailableForReservation)} reservation={Bytes(segment.CurrentReservation)} ledger-held={ledger.Held} usage-beyond-ledger={Bytes(beyond)}");
        }
        foreach (var segment in pools.SegmentAdmissions) {
            text.Append(CultureInfo.InvariantCulture, $" | admission {segment.Backend}.{segment.Segment.Name} pool={PoolName(pool: segment.Segment.Pool)} limit={Bytes(segment.Limit)} refused-limit={Bytes(segment.RefusedLimit)} held={segment.Held} peak={segment.Peak} promised={segment.Granted}");
        }
        foreach (var (backend, row) in ledgers) {
            text.Append(CultureInfo.InvariantCulture, $" | ledger {backend}.{row.Segment.Name} pool={PoolName(pool: row.Segment.Pool)} held={row.Held} named={row.Named} residual={row.Residual} peak={row.Peak} allocated={row.Allocated} released={row.Released} placement-disagreements={row.Disagreements}");
        }
        for (var index = 0; (index < Math.Min(val1: FamilyRows, val2: families.Length)); index++) {
            var (backend, row) = families[index];

            text.Append(CultureInfo.InvariantCulture, $" | family {backend}.{row.Segment.Name} {row.Family} held={row.Held} peak={row.Peak} allocated={row.Allocated} released={row.Released}");
        }
        if (families.Length > FamilyRows) {
            var rest = families.AsSpan(start: FamilyRows);
            long held = 0, allocated = 0, released = 0;

            foreach (var (_, row) in rest) { held += row.Held; allocated += row.Allocated; released += row.Released; }
            text.Append(CultureInfo.InvariantCulture, $" | families omitted={rest.Length} held={held} allocated={allocated} released={released}");
        }

        return text.ToString();
    }
    // The large object heap's tenants of at least minimumMebibytes, from a suspended walk of the managed heap.
    private static string DescribeHeap(int minimumMebibytes) {
        var minimum = (((long)minimumMebibytes) << 20);
        var clock = Stopwatch.StartNew();
        var walk = ManagedHeapWalk.Run(minimumBytes: minimum);
        var text = new StringBuilder();
        var named = walk.Tenants.Sum(selector: static tenant => tenant.Bytes);

        text.Append(CultureInfo.InvariantCulture, $"[host.memory heap: collection={walk.Collection} live-objects={walk.Nodes} | large objects (at least {ManagedHeapWalk.LargeObjectBytes} bytes) objects={walk.Objects} bytes={walk.Bytes} | tenants of at least {minimumMebibytes} MiB: {walk.Tenants.Count} holding {named} bytes | walk-ms={clock.ElapsedMilliseconds}\n");
        foreach (var tenant in walk.Tenants) {
            text.Append(CultureInfo.InvariantCulture, $"tenant {JsonSerializer.Serialize(tenant.Type)} held-by={JsonSerializer.Serialize(tenant.Holder)} objects={tenant.Objects} bytes={tenant.Bytes}\n");
        }
        text.Append(value: "The runtime wrote the heap during one blocking full collection with every managed thread suspended; the holder is the type of the first referrer found, or the kind of root.]");

        return text.ToString();
    }
    // One line: the process, then the managed heap as the last collection left it, in MiB.
    private string Describe() {
        using var process = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo(kind: GCKind.Any);
        var generations = info.GenerationInfo;
        var names = new[] { "gen0", "gen1", "gen2", "loh", "poh" };
        var parts = new string[names.Length];

        for (var index = 0; (index < names.Length); index++) {
            parts[index] = $"{names[index]}={((index < generations.Length) ? Mebibytes(bytes: generations[index].SizeAfterBytes) : "0")}";
        }

        var heap = string.Join(separator: ' ', value: parts);
        var uptime = (DateTime.Now - process.StartTime);
        var account = MemoryAccount.Shared.Read();
        var native = ((allocator as IAllocatorStatistics)?.ReadStatistics() ?? default);
        var accounting = string.Create(CultureInfo.InvariantCulture, $" | account bytes live={account.Live} reserved={account.Reserved} cached={account.Cached} retirement={account.Retirement} metadata={account.Metadata} budget={account.Budget} headroom={account.Headroom} allocator-committed={native.Committed} allocator-reserved={native.Reserved} managed-committed={info.TotalCommittedBytes} process-private={process.PrivateMemorySize64} deferred-trims={MemoryAccount.Shared.DeferredTrims}");

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[host.memory: process ws={Mebibytes(bytes: process.WorkingSet64)} peak-ws={Mebibytes(bytes: process.PeakWorkingSet64)} private={Mebibytes(bytes: process.PrivateMemorySize64)} MiB uptime={((long)uptime.TotalMilliseconds)}ms | managed heap={Mebibytes(bytes: info.HeapSizeBytes)} committed={Mebibytes(bytes: info.TotalCommittedBytes)} fragmented={Mebibytes(bytes: info.FragmentedBytes)} {heap} MiB pinned-objects={info.PinnedObjectsCount} allocated-bytes={GC.GetTotalAllocatedBytes(precise: false)} collections gen0={GC.CollectionCount(generation: 0)} gen1={GC.CollectionCount(generation: 1)} gen2={GC.CollectionCount(generation: 2)} last={info.Generation}{(info.Compacted ? " compacted" : string.Empty)}{accounting}{DescribePools()} | jit methods={System.Runtime.JitInfo.GetCompiledMethodCount()} il={Mebibytes(bytes: System.Runtime.JitInfo.GetCompiledILBytes())} MiB compile={((long)System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds)}ms]"
        );
    }

    private static readonly CommandForms HostMemoryForms = CommandForms.Of("host.memory", "owners [limit]", "heap [min-mib]", "collect", "budget [host|device] <bytes|auto>");
    // The size, in MiB, past which a large-object-heap tenant is named when host.memory heap is not given one.
    private const int HeapTenantMebibytes = 4;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: $"Reads process working set/private bytes, uptime and managed heap generations in MiB, the bytes every thread has allocated so far (a reader diffs two reads), then the process account's live, reserved, cached, retirement and metadata bytes, budget/headroom, and allocator committed/reserved bytes, then measures the account (the allocator's committed bytes, the collector's and a page walk of the process, which also reads every sampled owner) and prints each pool by physical memory (host always; a discrete adapter's device memory, named absent on unified memory or without a device) with its physical total, budget, spend limit, the bytes it holds (held, their peak, and the part replaced objects still hold until their readers finish), the bytes it has promised, its room, what stands past the limit, the forced job's bytes and count, the allocations admitted past the limit because their class cannot give way, the refusals, the device's own refusals and the trims pressure fired, then its held and promised bytes by class (fixed, workspace, floors, support, elastic, caches), and its physical rows, which alone add: device-blocks (the ledgers' blocks placed in the pool), and in host memory native-allocator, native-direct (native storage outside the allocator, such as region host copies), managed, and runtime-and-driver (the walk's private bytes less the other host rows), each with the bytes its owners name and its residual; then the sample's age, the collection the managed figure describes and the residuals sampling skew made negative, then the walk's private, mapped and image bytes, the device memory reading the account last recorded (state, serial, whether estimated) and for each segment its API budget, usage and reservation figures beside the bytes the backend ledger placed in it, then each ledger segment's admission (its limit, the limit a device refusal left, and its held, peak and promised bytes; retired bytes are stated by their holders, which name a pool, so they read on the pool), then each ledger segment row with its held bytes, the part the owner families name and the residual, its peak, allocated and released bytes and placement disagreements, then the {FamilyRows} families holding most device memory (owner without its instance, then the part) per segment with their held, peak, allocated and released bytes and the rest summed, then the JIT's compiled-method count, IL bytes and compilation time so far; with an argument it takes the forms {HostMemoryForms.Spelling}. Reservations are future admission promises, not allocated bytes. owners reads one atomic charged/reserved-owner census (default 20 largest rows, 0 all), including categories, the rows each owner names, actual peaks, future promises and disposed owners whose storage is still held; omitted totals reconcile the displayed rows. Registered memory.* backend counters follow as separate observations, with aperture bytes already included in device-local bytes. heap pauses the process while the operating system snapshots it, then walks the snapshot's managed heap and lists the large object heap's totals and its tenants of at least min-mib MiB (default {HeapTenantMebibytes}) by type and first holder (the referring type and field, or the root kind); it reads every reference on the heap, so it takes seconds on a large heap and the World waits for it. collect trims cache claims, compacts the LOH and asks the native allocator to collect (under --debug-allocator, checking every quarantined block first); active consumers retain their storage, and deferred-trims counts cache trims still waiting for their owner thread (a residency services its own on its next frame), so a reading taken right after collect can still hold their storage. budget lowers a pool's budget or restores the derived one with auto: host memory when no pool is named, and device stands a figure in for the sum of the device segments' spend limits; a lowered budget trims the caches, nothing held is released, an admission that gives way is refused past it, and a bake group that does not fit waits while others of its bake are admitted and is admitted alone, as the process's one forced job, once none is, so a bake always settles.",
            forms: HostMemoryForms,
            handler: (_, args) => {
                if ((args.Count is 1 or 2) && args.Is(index: 0, value: "heap")) {
                    var minimum = HeapTenantMebibytes;

                    if ((args.Count == 2) && (!args.TryInt(index: 1, value: out minimum) || (minimum < 0))) {
                        return CommandResult.Error(output: "[host.memory: heap expects an optional nonnegative tenant size in MiB]");
                    }
                    return new CommandResult(Output: DescribeHeap(minimumMebibytes: minimum));
                }
                if ((args.Count is 1 or 2) && args.Is(index: 0, value: "owners")) {
                    var limit = 20;

                    if ((args.Count == 2) && (!args.TryInt(index: 1, value: out limit) || (limit < 0))) {
                        return CommandResult.Error(output: "[host.memory: owners expects an optional nonnegative row limit; 0 lists all charged/reserved owners]");
                    }
                    return new CommandResult(Output: DescribeOwners(limit: limit));
                }
                if ((args.Count is 2 or 3) && args.Is(index: 0, value: "budget")) {
                    var pool = (((args.Count == 3) && args.Is(index: 1, value: "device")) ? PhysicalMemory.Device : PhysicalMemory.Host);
                    var value = (args.Count - 1);

                    if ((args.Count == 2) || args.Is(index: 1, value: "device") || args.Is(index: 1, value: "host")) {
                        if (args.Is(index: value, value: "auto")) { MemoryAccount.Shared.SetBudget(bytes: null, pool: pool); return new CommandResult(Output: Describe()); }
                        if (long.TryParse(args[value], NumberStyles.None, CultureInfo.InvariantCulture, out var budget)) {
                            MemoryAccount.Shared.SetBudget(bytes: budget, pool: pool);
                            return new CommandResult(Output: Describe());
                        }
                    }
                }
                if (args.Count == 0) {
                    return new CommandResult(Output: Describe());
                }
                if ((args.Count == 1) && args.Is(index: 0, value: "collect")) {
                    MemoryAccount.Shared.TrimCaches();
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(
                        blocking: true,
                        compacting: true,
                        generation: GC.MaxGeneration,
                        mode: GCCollectionMode.Forced
                    );
                    (allocator as IAllocatorStatistics)?.Collect();

                    return new CommandResult(Output: Describe());
                }

                return HostMemoryForms.Refuse(args: in args);
            },
            name: "host.memory"
        );
    }
}
