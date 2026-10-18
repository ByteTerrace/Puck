using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.System.Com;

namespace Puck.DirectX;

public static partial class DirectXRemovalReport {
    /// <summary>Reads a removed device's DRED record and writes it (<see cref="Format"/>). A device whose runtime has no
    /// DRED interface, or whose queries fail, reports only its reason and why.</summary>
    /// <param name="device">The removed device.</param>
    /// <param name="reason">The device's removal reason.</param>
    /// <returns>The record's lines.</returns>
    [SupportedOSPlatform("windows10.0.10240")]
    public static unsafe IReadOnlyList<string> Read(ID3D12Device* device, int reason) {
        var dredIid = ID3D12DeviceRemovedExtendedData1.IID_Guid;

        if (((IUnknown*)device)->QueryInterface(
            ppvObject: out var dredObject,
            riid: in dredIid
        ).Failed) {
            return [Line(text: $"device removed, reason 0x{reason:X8}; the runtime has no DRED record (ID3D12DeviceRemovedExtendedData1)")];
        }

        var dred = ((ID3D12DeviceRemovedExtendedData1*)dredObject);

        try {
            return Format(
                pageFault: ReadPageFault(dred: dred),
                reason: reason,
                trails: ReadTrails(dred: dred)
            );
        } finally {
            _ = ((IUnknown*)dredObject)->Release();
        }
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private static unsafe List<DirectXBreadcrumbTrail>? ReadTrails(ID3D12DeviceRemovedExtendedData1* dred) {
        D3D12_DRED_AUTO_BREADCRUMBS_OUTPUT1 output;

        try {
            dred->GetAutoBreadcrumbsOutput1(pOutput: &output);
        } catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or InvalidOperationException) {
            return null;
        }

        var trails = new List<DirectXBreadcrumbTrail>();
        var node = output.pHeadAutoBreadcrumbNode;

        for (var count = 0; ((node is not null) && (count < MaxNodes)); count++, node = node->pNext) {
            var operations = new D3D12_AUTO_BREADCRUMB_OP[node->BreadcrumbCount];

            for (var index = 0; (index < operations.Length); index++) {
                operations[index] = node->pCommandHistory[index];
            }

            var contexts = new List<DirectXBreadcrumbContext>();

            for (var index = 0; (index < node->BreadcrumbContextsCount); index++) {
                var context = node->pBreadcrumbContexts[index];

                contexts.Add(item: new DirectXBreadcrumbContext(Index: ((int)context.BreadcrumbIndex), Text: Text(value: context.pContextString)));
            }

            trails.Add(item: new DirectXBreadcrumbTrail(
                CommandList: Text(value: node->pCommandListDebugNameW, fallback: node->pCommandListDebugNameA),
                Completed: ((node->pLastBreadcrumbValue is null) ? 0 : ((int)Math.Min(val1: *node->pLastBreadcrumbValue, val2: node->BreadcrumbCount))),
                Contexts: contexts,
                Operations: operations,
                Queue: Text(value: node->pCommandQueueDebugNameW, fallback: node->pCommandQueueDebugNameA)
            ));
        }

        return trails;
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private static unsafe DirectXPageFault? ReadPageFault(ID3D12DeviceRemovedExtendedData1* dred) {
        D3D12_DRED_PAGE_FAULT_OUTPUT1 output;

        try {
            dred->GetPageFaultAllocationOutput1(pOutput: &output);
        } catch (Exception exception) when (exception is COMException or UnauthorizedAccessException or InvalidOperationException) {
            return null;
        }

        return ((output.PageFaultVA == 0)
            ? null
            : new DirectXPageFault(
                Address: output.PageFaultVA,
                Existing: Allocations(node: output.pHeadExistingAllocationNode),
                RecentlyFreed: Allocations(node: output.pHeadRecentFreedAllocationNode)
            ));
    }
    private static unsafe List<DirectXDredAllocation> Allocations(D3D12_DRED_ALLOCATION_NODE1* node) {
        var allocations = new List<DirectXDredAllocation>();

        for (var count = 0; ((node is not null) && (count < MaxNodes)); count++, node = node->pNext) {
            allocations.Add(item: new DirectXDredAllocation(Name: Text(value: node->ObjectNameW, fallback: node->ObjectNameA), Type: node->AllocationType));
        }

        return allocations;
    }
    private static unsafe string Text(PCWSTR value, byte* fallback = null) {
        if (value.Value is not null) {
            return new string(value: value.Value);
        }

        return ((fallback is not null) ? new string(value: ((sbyte*)fallback)) : "(unnamed)");
    }
}
