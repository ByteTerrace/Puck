using System.Globalization;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// The record a removed Direct3D 12 device leaves through device removed extended data (DRED), as <c>[d3d12-dred]</c>
/// lines: the removal reason, every command list whose breadcrumb trail stopped before its end with the operations
/// around the first one the GPU did not finish and the PIX-event labels it ran under, and the page fault that removed
/// the device with the allocations at and recently freed near its address. <see cref="Read"/> takes the record from a
/// device; <see cref="Format"/> writes one.
/// </summary>
public static partial class DirectXRemovalReport {
    /// <summary>The prefix of every removal-record line.</summary>
    public const string LinePrefix = "[d3d12-dred]";
    /// <summary>The finished operations shown before a trail's first unfinished one.</summary>
    public const int OperationsBefore = 12;
    /// <summary>The operations shown from a trail's first unfinished one on, that one included.</summary>
    public const int OperationsFrom = 4;
    /// <summary>The finished command lists named in the record; any more are counted.</summary>
    public const int FinishedListsNamed = 8;

    // The longest linked list read from a record; DRED keeps far fewer nodes, and the bound stops a corrupt record.
    private const int MaxNodes = 4096;
    private const string OperationPrefix = "D3D12_AUTO_BREADCRUMB_OP_";
    private const string AllocationPrefix = "D3D12_DRED_ALLOCATION_TYPE_";

    /// <summary>Writes a removal record as lines, each starting with <see cref="LinePrefix"/>.</summary>
    /// <param name="reason">The device's removal reason (<c>ID3D12Device::GetDeviceRemovedReason</c>).</param>
    /// <param name="trails">The breadcrumb trails, oldest first, or <see langword="null"/> when DRED kept none.</param>
    /// <param name="pageFault">The page fault, or <see langword="null"/> when DRED recorded none.</param>
    /// <returns>The record's lines: a headline, then each unfinished trail, the finished ones, and the page fault.</returns>
    public static IReadOnlyList<string> Format(int reason, IReadOnlyList<DirectXBreadcrumbTrail>? trails, DirectXPageFault? pageFault) {
        var lines = new List<string>();

        if (trails is null) {
            lines.Add(item: Line(text: $"device removed, reason 0x{reason:X8}; no breadcrumb trail (DRED was off, or the runtime kept none)"));
        } else {
            var unfinished = trails.Where(predicate: static trail => trail.Completed < trail.Operations.Count).ToArray();
            var finished = trails.Where(predicate: static trail => trail.Completed >= trail.Operations.Count).ToArray();

            lines.Add(item: Line(text: $"device removed, reason 0x{reason:X8}; {trails.Count} command lists in the breadcrumb trail, {unfinished.Length} unfinished"));

            foreach (var trail in unfinished) {
                WriteUnfinished(lines: lines, trail: trail);
            }

            if (finished.Length > 0) {
                var named = string.Join(separator: ", ", values: finished.Take(count: FinishedListsNamed).Select(selector: static trail => $"'{trail.CommandList}'"));
                var more = ((finished.Length > FinishedListsNamed) ? $" and {finished.Length - FinishedListsNamed} more" : string.Empty);

                lines.Add(item: Line(text: $"finished: {named}{more}"));
            }
        }

        if (pageFault is null) {
            lines.Add(item: Line(text: "no page fault recorded"));
        } else {
            lines.Add(item: Line(text: string.Create(provider: CultureInfo.InvariantCulture, handler: $"page fault at 0x{pageFault.Address:X16}")));

            foreach (var allocation in pageFault.Existing) {
                lines.Add(item: Line(text: $"  at the address: '{allocation.Name}' ({Name(type: allocation.Type)})"));
            }

            foreach (var allocation in pageFault.RecentlyFreed) {
                lines.Add(item: Line(text: $"  recently freed there: '{allocation.Name}' ({Name(type: allocation.Type)})"));
            }

            if ((pageFault.Existing.Count == 0) && (pageFault.RecentlyFreed.Count == 0)) {
                lines.Add(item: Line(text: "  no allocation, live or recently freed, lies at the address"));
            }
        }

        return lines;
    }
    // A trail's first unfinished operation, the PIX-event labels open at it, and the operations around it.
    private static void WriteUnfinished(List<string> lines, DirectXBreadcrumbTrail trail) {
        var completed = trail.Completed;
        var contexts = trail.Contexts.ToDictionary(keySelector: static context => context.Index, elementSelector: static context => context.Text);
        var open = new Stack<string>();

        for (var index = 0; (index < completed); index++) {
            switch (trail.Operations[index]) {
                case D3D12_AUTO_BREADCRUMB_OP.D3D12_AUTO_BREADCRUMB_OP_BEGINEVENT:
                    open.Push(item: (contexts.TryGetValue(key: index, value: out var label) ? label : "(unlabelled event)"));
                    break;
                case D3D12_AUTO_BREADCRUMB_OP.D3D12_AUTO_BREADCRUMB_OP_ENDEVENT:
                    _ = open.TryPop(result: out _);
                    break;
            }
        }

        var path = ((open.Count == 0) ? "no open event" : string.Join(separator: " > ", values: open.Reverse()));

        lines.Add(item: Line(text: $"queue '{trail.Queue}' list '{trail.CommandList}': {completed} of {trail.Operations.Count} operations finished; the GPU stopped in {path}"));

        for (var index = Math.Max(val1: 0, val2: (completed - OperationsBefore)); (index < Math.Min(val1: trail.Operations.Count, val2: (completed + OperationsFrom))); index++) {
            var mark = ((index < completed) ? "   " : ((index == completed) ? ">> " : "   "));
            var state = ((index < completed) ? "done" : "open");
            var label = (contexts.TryGetValue(key: index, value: out var text) ? $" '{text}'" : string.Empty);

            lines.Add(item: Line(text: string.Create(provider: CultureInfo.InvariantCulture, handler: $"  {mark}[{index}] {state} {Name(operation: trail.Operations[index])}{label}")));
        }
    }
    private static string Line(string text) => $"{LinePrefix} {text}";
    private static string Name(D3D12_AUTO_BREADCRUMB_OP operation) {
        var name = operation.ToString();

        return (name.StartsWith(value: OperationPrefix, comparisonType: StringComparison.Ordinal) ? name[OperationPrefix.Length..].ToLowerInvariant() : name);
    }
    private static string Name(D3D12_DRED_ALLOCATION_TYPE type) {
        var name = type.ToString();

        return (name.StartsWith(value: AllocationPrefix, comparisonType: StringComparison.Ordinal) ? name[AllocationPrefix.Length..].ToLowerInvariant() : name);
    }
}
/// <summary>One command list's DRED breadcrumb trail.</summary>
/// <param name="Queue">The name of the queue it ran on, or <c>(unnamed)</c>.</param>
/// <param name="CommandList">The command list's name, or <c>(unnamed)</c>.</param>
/// <param name="Completed">The number of its operations the GPU finished, from its first.</param>
/// <param name="Operations">Every operation the list recorded, in order.</param>
/// <param name="Contexts">The PIX-event labels DRED kept, each at the index of the operation that set it.</param>
public sealed record DirectXBreadcrumbTrail(string Queue, string CommandList, int Completed, IReadOnlyList<D3D12_AUTO_BREADCRUMB_OP> Operations, IReadOnlyList<DirectXBreadcrumbContext> Contexts);
/// <summary>A PIX-event label DRED kept beside a breadcrumb.</summary>
/// <param name="Index">The index of the operation, in its trail, that set the label.</param>
/// <param name="Text">The label.</param>
public readonly record struct DirectXBreadcrumbContext(int Index, string Text);
/// <summary>The page fault DRED recorded on a removed device.</summary>
/// <param name="Address">The faulting GPU virtual address.</param>
/// <param name="Existing">The allocations live at the address.</param>
/// <param name="RecentlyFreed">The allocations recently freed at the address.</param>
public sealed record DirectXPageFault(ulong Address, IReadOnlyList<DirectXDredAllocation> Existing, IReadOnlyList<DirectXDredAllocation> RecentlyFreed);
/// <summary>An allocation a DRED page-fault record names.</summary>
/// <param name="Name">The allocation's debug name, or <c>(unnamed)</c>.</param>
/// <param name="Type">What kind of object it is.</param>
public readonly record struct DirectXDredAllocation(string Name, D3D12_DRED_ALLOCATION_TYPE Type);
