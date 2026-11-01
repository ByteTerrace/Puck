using System.Diagnostics;
using System.Globalization;
using System.Runtime;
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
internal sealed class HostMemoryCommandModule(IAllocator allocator) : ICommandModule {
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
        var accounting = string.Create(CultureInfo.InvariantCulture, $" | account bytes live={account.Live} reserved={account.Reserved} cached={account.Cached} retirement={account.Retirement} metadata={account.Metadata} budget={account.Budget} headroom={account.Headroom} allocator-committed={native.Committed} allocator-reserved={native.Reserved} managed-committed={info.TotalCommittedBytes} process-private={process.PrivateMemorySize64}");

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[host.memory: process ws={Mebibytes(bytes: process.WorkingSet64)} peak-ws={Mebibytes(bytes: process.PeakWorkingSet64)} private={Mebibytes(bytes: process.PrivateMemorySize64)} MiB uptime={(long)uptime.TotalMilliseconds}ms | managed heap={Mebibytes(bytes: info.HeapSizeBytes)} committed={Mebibytes(bytes: info.TotalCommittedBytes)} fragmented={Mebibytes(bytes: info.FragmentedBytes)} {heap} MiB pinned-objects={info.PinnedObjectsCount} collections gen0={GC.CollectionCount(generation: 0)} gen1={GC.CollectionCount(generation: 1)} gen2={GC.CollectionCount(generation: 2)} last={info.Generation}{(info.Compacted ? " compacted" : string.Empty)}{accounting} | jit methods={System.Runtime.JitInfo.GetCompiledMethodCount()} il={Mebibytes(bytes: System.Runtime.JitInfo.GetCompiledILBytes())} MiB compile={(long)System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds}ms]"
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: "Reads process working set/private bytes, uptime and managed heap generations in MiB, then the process account's live, reserved, cached, retirement and metadata bytes, budget/headroom, and allocator committed/reserved bytes, then the JIT's compiled-method count, IL bytes and compilation time so far. Reservations are future admission promises, not allocated bytes. host.memory collect trims cache claims, compacts the LOH and asks the native allocator to collect; active consumers retain their storage. host.memory budget <bytes|auto> lowers the background admission budget or restores the machine-derived budget; existing owners keep their allocations and new bakes defer until they fit.",
            handler: (_, args) => {
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

                return CommandResult.Error(output: "[host.memory: expects no argument, collect, or budget <bytes|auto>]");
            },
            name: "host.memory"
        );
    }
}
