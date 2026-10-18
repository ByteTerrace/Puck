using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Laws for the record a removed Direct3D 12 device leaves (<see cref="DirectXRemovalReport.Format"/>): the
/// headline names the reason and how many command lists were unfinished; each unfinished list names the PIX-event labels
/// open at its first unfinished operation and shows the operations around it; finished lists are named; the page fault
/// names the allocations at its address. Built from managed records, so no device is involved.</summary>
public sealed class DirectXRemovalReportLawTests {
    private const D3D12_AUTO_BREADCRUMB_OP Begin = D3D12_AUTO_BREADCRUMB_OP.D3D12_AUTO_BREADCRUMB_OP_BEGINEVENT;
    private const D3D12_AUTO_BREADCRUMB_OP Dispatch = D3D12_AUTO_BREADCRUMB_OP.D3D12_AUTO_BREADCRUMB_OP_DISPATCH;
    private const D3D12_AUTO_BREADCRUMB_OP End = D3D12_AUTO_BREADCRUMB_OP.D3D12_AUTO_BREADCRUMB_OP_ENDEVENT;

    [Fact]
    public void AnUnfinishedTrailNamesTheEventsOpenAtItsFirstUnfinishedOperation() {
        var trail = new DirectXBreadcrumbTrail(
            CommandList: "sdf.world/frame[1]",
            Completed: 5,
            Contexts: [new(Index: 0, Text: "world"), new(Index: 1, Text: "shadow"), new(Index: 4, Text: "mesh")],
            Operations: [Begin, Begin, Dispatch, End, Begin, Dispatch, End, End],
            Queue: "direct"
        );
        var lines = DirectXRemovalReport.Format(
            pageFault: null,
            reason: unchecked((int)0x887A0005),
            trails: [trail, trail with { CommandList = "overlay", Completed = 3, Operations = [Begin, Dispatch, End] }]
        );

        Assert.All(collection: lines, action: static line => Assert.StartsWith(expectedStartString: DirectXRemovalReport.LinePrefix, actualString: line));
        Assert.Equal(
            actual: lines[0],
            expected: "[d3d12-dred] device removed, reason 0x887A0005; 2 command lists in the breadcrumb trail, 1 unfinished"
        );
        Assert.Equal(
            actual: lines[1],
            expected: "[d3d12-dred] queue 'direct' list 'sdf.world/frame[1]': 5 of 8 operations finished; the GPU stopped in world > mesh"
        );
        Assert.Contains(expected: "[d3d12-dred]   >> [5] open dispatch", collection: lines);
        Assert.Contains(expected: "[d3d12-dred]      [4] done beginevent 'mesh'", collection: lines);
        Assert.Contains(expected: "[d3d12-dred]      [0] done beginevent 'world'", collection: lines);
        Assert.Contains(expected: "[d3d12-dred]      [7] open endevent", collection: lines);
        Assert.Contains(expected: "[d3d12-dred] finished: 'overlay'", collection: lines);
        Assert.Equal(actual: lines[^1], expected: "[d3d12-dred] no page fault recorded");
    }
    [Fact]
    public void ATrailStoppedBeforeAnyEventNamesNoneAndCountsFinishedListsBeyondTheNamed() {
        var finished = Enumerable.Range(start: 0, count: (DirectXRemovalReport.FinishedListsNamed + 2))
            .Select(selector: static index => new DirectXBreadcrumbTrail(CommandList: $"list{index}", Completed: 1, Contexts: [], Operations: [Dispatch], Queue: "q"))
            .ToArray();
        var lines = DirectXRemovalReport.Format(
            pageFault: null,
            reason: 1,
            trails: [new DirectXBreadcrumbTrail(CommandList: "(unnamed)", Completed: 0, Contexts: [], Operations: [Begin, Dispatch], Queue: "q"), .. finished]
        );

        Assert.Contains(expected: "[d3d12-dred] queue 'q' list '(unnamed)': 0 of 2 operations finished; the GPU stopped in no open event", collection: lines);
        Assert.Contains(expected: "[d3d12-dred]   >> [0] open beginevent", collection: lines);
        Assert.EndsWith(expectedEndString: "'list7' and 2 more", actualString: lines.Single(predicate: static line => line.Contains(value: "finished: ", comparisonType: StringComparison.Ordinal)));
    }
    [Fact]
    public void ARecordWithoutTrailsSaysSoAndAPageFaultNamesItsAllocations() {
        var lines = DirectXRemovalReport.Format(
            pageFault: new DirectXPageFault(
                Address: 0x1234_5678_9ABCUL,
                Existing: [new(Name: "sdf.world/program[1]", Type: D3D12_DRED_ALLOCATION_TYPE.D3D12_DRED_ALLOCATION_TYPE_RESOURCE)],
                RecentlyFreed: [new(Name: "overlay/pass", Type: D3D12_DRED_ALLOCATION_TYPE.D3D12_DRED_ALLOCATION_TYPE_PIPELINE_STATE)]
            ),
            reason: 7,
            trails: null
        );

        Assert.Equal(
            actual: lines,
            expected: [
                "[d3d12-dred] device removed, reason 0x00000007; no breadcrumb trail (DRED was off, or the runtime kept none)",
                "[d3d12-dred] page fault at 0x0000123456789ABC",
                "[d3d12-dred]   at the address: 'sdf.world/program[1]' (resource)",
                "[d3d12-dred]   recently freed there: 'overlay/pass' (pipeline_state)",
            ]
        );
        Assert.Equal(
            actual: DirectXRemovalReport.Format(pageFault: new DirectXPageFault(Address: 16UL, Existing: [], RecentlyFreed: []), reason: 7, trails: null)[^1],
            expected: "[d3d12-dred]   no allocation, live or recently freed, lies at the address"
        );
    }
    [Fact]
    public void AnOperationOrAllocationTheRuntimeAddedIsWrittenAsItsNumber() {
        var lines = DirectXRemovalReport.Format(
            pageFault: new DirectXPageFault(Address: 16UL, Existing: [new(Name: "x", Type: ((D3D12_DRED_ALLOCATION_TYPE)999))], RecentlyFreed: []),
            reason: 7,
            trails: [new DirectXBreadcrumbTrail(CommandList: "x", Completed: 0, Contexts: [], Operations: [((D3D12_AUTO_BREADCRUMB_OP)999)], Queue: "q")]
        );

        Assert.Contains(expected: "[d3d12-dred]   >> [0] open 999", collection: lines);
        Assert.Contains(expected: "[d3d12-dred]   at the address: 'x' (999)", collection: lines);
    }
}
