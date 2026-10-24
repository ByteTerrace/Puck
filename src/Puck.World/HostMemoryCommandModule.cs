using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using Puck.Commands;

namespace Puck.World;

/// <summary>
/// The host process's memory read-back: <c>host.memory</c> echoes the process's working set and private bytes beside
/// the managed heap the last garbage collection left (its size, committed and fragmented bytes, and each generation's
/// size: gen0, gen1, gen2, the large and the pinned object heaps), so the managed share of a large working set is read
/// apart from native, mapped and driver memory. <c>host.memory collect</c> first runs a full blocking collection that
/// compacts the large object heap, so the heap it reports is what is still reachable. Presentation-independent; it
/// reads the process, never the simulation.
/// </summary>
internal sealed class HostMemoryCommandModule : ICommandModule {
    private static string Mebibytes(long bytes) =>
        (bytes / (1L << 20)).ToString(provider: CultureInfo.InvariantCulture);

    // One line: the process, then the managed heap as the last collection left it, in MiB.
    private static string Describe() {
        using var process = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo(kind: GCKind.Any);
        var generations = info.GenerationInfo;
        var names = new[] { "gen0", "gen1", "gen2", "loh", "poh" };
        var parts = new string[names.Length];

        for (var index = 0; (index < names.Length); index++) {
            parts[index] = $"{names[index]}={((index < generations.Length) ? Mebibytes(bytes: generations[index].SizeAfterBytes) : "0")}";
        }

        var heap = string.Join(separator: ' ', value: parts);

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[host.memory: process ws={Mebibytes(bytes: process.WorkingSet64)} peak-ws={Mebibytes(bytes: process.PeakWorkingSet64)} private={Mebibytes(bytes: process.PrivateMemorySize64)} MiB | managed heap={Mebibytes(bytes: info.HeapSizeBytes)} committed={Mebibytes(bytes: info.TotalCommittedBytes)} fragmented={Mebibytes(bytes: info.FragmentedBytes)} {heap} MiB pinned-objects={info.PinnedObjectsCount} collections gen0={GC.CollectionCount(generation: 0)} gen1={GC.CollectionCount(generation: 1)} gen2={GC.CollectionCount(generation: 2)} last={info.Generation}{(info.Compacted ? " compacted" : string.Empty)}]"
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            description: "Reads the host process's memory: host.memory echoes the working set, its peak and the private bytes, then the managed heap the last garbage collection left (size, committed, fragmented, and gen0, gen1, gen2, the large and pinned object heaps), in MiB, with the collection counts. host.memory collect first runs a full blocking collection that compacts the large object heap, so the heap read is what is still reachable.",
            handler: (_, args) => {
                if (args.Count == 0) {
                    return new CommandResult(Output: Describe());
                }
                if ((args.Count == 1) && args.Is(index: 0, value: "collect")) {
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(
                        blocking: true,
                        compacting: true,
                        generation: GC.MaxGeneration,
                        mode: GCCollectionMode.Forced
                    );

                    return new CommandResult(Output: Describe());
                }

                return CommandResult.Error(output: "[host.memory: expects no argument, or collect]");
            },
            name: "host.memory"
        );
    }
}
