using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Counting;
using Puck.Commands;
using Puck.Abstractions.Memory;
using Puck.Hosting.Jobs;

namespace Puck.World;

/// <summary>
/// The host process's memory read-back: <c>host.memory</c> echoes the process's working set and private bytes beside
/// the managed heap the last garbage collection left (its size, committed and fragmented bytes, and each generation's
/// size: gen0, gen1, gen2, the large and the pinned object heaps), so the managed share of a large working set is read
/// apart from native, mapped and driver memory, then the process uptime and what the JIT has compiled so far (methods,
/// IL bytes, compilation time), which read at boot give its first-use JIT cost. <c>host.memory collect</c> first runs a
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

            text.Append(CultureInfo.InvariantCulture, $"owner {JsonSerializer.Serialize(owner.Name)} category={owner.Category} owned={owner.Owned} reserved={owner.Reserved} peak-owned={owner.PeakOwned} reservation-peak={owner.ReservationPeak} disposed={owner.Disposed.ToString().ToLowerInvariant()}\n");
        }
        var omittedOwned = 0L;
        var omittedReserved = 0L;

        for (var index = shown; (index < owners.Length); index++) {
            omittedOwned += owners[index].Owned;
            omittedReserved += owners[index].Reserved;
        }
        text.Append(CultureInfo.InvariantCulture, $"omitted owners={(owners.Length - shown)} owned={omittedOwned} reserved={omittedReserved}\n");
        foreach (var source in m_memorySources) { WorkCounterReport.AppendSection(builder: text, source: source); }
        text.Append(value: "Backend counters are separate observations; host-visible-device-local bytes are a subset of device-local bytes. Process, managed, allocator, account and backend readings overlap; unified physical total is unavailable.]");
        return text.ToString();
    }
    private static string Mebibytes(long bytes) =>
        (bytes / (1L << 20)).ToString(provider: CultureInfo.InvariantCulture);
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
            handler: $"[host.memory: process ws={Mebibytes(bytes: process.WorkingSet64)} peak-ws={Mebibytes(bytes: process.PeakWorkingSet64)} private={Mebibytes(bytes: process.PrivateMemorySize64)} MiB uptime={((long)uptime.TotalMilliseconds)}ms | managed heap={Mebibytes(bytes: info.HeapSizeBytes)} committed={Mebibytes(bytes: info.TotalCommittedBytes)} fragmented={Mebibytes(bytes: info.FragmentedBytes)} {heap} MiB pinned-objects={info.PinnedObjectsCount} collections gen0={GC.CollectionCount(generation: 0)} gen1={GC.CollectionCount(generation: 1)} gen2={GC.CollectionCount(generation: 2)} last={info.Generation}{(info.Compacted ? " compacted" : string.Empty)}{accounting} | jit methods={System.Runtime.JitInfo.GetCompiledMethodCount()} il={Mebibytes(bytes: System.Runtime.JitInfo.GetCompiledILBytes())} MiB compile={((long)System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds)}ms]"
        );
    }

    private static readonly CommandForms HostMemoryForms = CommandForms.Of("host.memory", "owners [limit]", "collect", "budget <bytes|auto>");

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: $"Reads process working set/private bytes, uptime and managed heap generations in MiB, then the process account's live, reserved, cached, retirement and metadata bytes, budget/headroom, and allocator committed/reserved bytes, then the JIT's compiled-method count, IL bytes and compilation time so far; with an argument it takes the forms {HostMemoryForms.Spelling}. Reservations are future admission promises, not allocated bytes. owners reads one atomic charged/reserved-owner census (default 20 largest rows, 0 all), including categories, actual peaks, future promises and disposed owners whose storage is still held; omitted totals reconcile the displayed rows. Registered memory.* backend counters follow as separate observations, with aperture bytes already included in device-local bytes; these overlapping views do not establish a unified physical total. collect trims cache claims, compacts the LOH and asks the native allocator to collect (under --debug-allocator, checking every quarantined block first); active consumers retain their storage, and deferred-trims counts cache trims still waiting for their owner thread (a residency services its own on its next frame), so a reading taken right after collect can still hold their storage. budget lowers the background admission budget or restores the machine-derived budget, and a lowered budget trims the caches; existing owners keep their allocations, and a bake group that does not fit waits while others of its bake are admitted, and is admitted alone once none is, so a bake always settles.",
            forms: HostMemoryForms,
            handler: (_, args) => {
                if ((args.Count is 1 or 2) && args.Is(index: 0, value: "owners")) {
                    var limit = 20;

                    if ((args.Count == 2) && (!args.TryInt(index: 1, value: out limit) || (limit < 0))) {
                        return CommandResult.Error(output: "[host.memory: owners expects an optional nonnegative row limit; 0 lists all charged/reserved owners]");
                    }
                    return new CommandResult(Output: DescribeOwners(limit: limit));
                }
                if ((args.Count == 2) && args.Is(index: 0, value: "budget")) {
                    if (args.Is(index: 1, value: "auto")) { MemoryAccount.Shared.SetBudget(bytes: null); return new CommandResult(Output: Describe()); }
                    if (long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var budget)) {
                        MemoryAccount.Shared.SetBudget(bytes: budget);
                        return new CommandResult(Output: Describe());
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
