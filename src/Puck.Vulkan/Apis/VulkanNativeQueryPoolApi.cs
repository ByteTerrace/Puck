using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;

namespace Puck.Vulkan;

/// <summary>
/// The native implementation of <see cref="IVulkanQueryPoolApi"/>, marshaling to the query-pool and
/// timestamp entry points resolved from the Vulkan loader.
/// </summary>
public unsafe sealed class VulkanNativeQueryPoolApi : IVulkanQueryPoolApi {
    private const uint QueryResult64Bit = 0x00000001;
    private const uint QueryResultWaitBit = 0x00000002;
    private const uint QueryTypeTimestamp = 2;
    private const uint StructureTypeQueryPoolCreateInfo = 11;

    private readonly Lock m_syncRoot = new();
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    /// <inheritdoc/>
    public VkResult CreateTimestampPool(nint deviceHandle, uint queryCount, out nint queryPoolHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Vulkan logical-device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        if (0 == queryCount) {
            throw new ArgumentException(
                message: "Timestamp query count must be non-zero.",
                paramName: nameof(queryCount)
            );
        }

        var createQueryPool = GetPointers(deviceHandle: deviceHandle).CreateQueryPool;
        var createInfo = new VkQueryPoolCreateInfo {
            QueryCount = queryCount,
            QueryType = QueryTypeTimestamp,
            SType = StructureTypeQueryPoolCreateInfo,
        };

        return createQueryPool(
            deviceHandle,
            in createInfo,
            0,
            out queryPoolHandle
        );
    }
    /// <inheritdoc/>
    public void DestroyQueryPool(nint deviceHandle, nint queryPoolHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == queryPoolHandle)
        ) {
            return;
        }

        var destroyQueryPool = GetPointers(deviceHandle: deviceHandle).DestroyQueryPool;

        destroyQueryPool(
            deviceHandle,
            queryPoolHandle,
            0
        );
    }
    /// <inheritdoc/>
    public void CmdResetQueryPool(nint deviceHandle, nint commandBufferHandle, nint queryPoolHandle, uint firstQuery, uint queryCount) {
        if (
            (0 == deviceHandle) ||
            (0 == commandBufferHandle) ||
            (0 == queryPoolHandle)
        ) {
            throw new ArgumentException(message: "Vulkan device, command-buffer, and query-pool handles must be non-zero.");
        }

        var cmdResetQueryPool = GetPointers(deviceHandle: deviceHandle).CmdResetQueryPool;

        cmdResetQueryPool(
            commandBufferHandle,
            queryPoolHandle,
            firstQuery,
            queryCount
        );
    }
    /// <inheritdoc/>
    public void CmdWriteTimestamp(nint deviceHandle, nint commandBufferHandle, uint pipelineStage, nint queryPoolHandle, uint query) {
        if (
            (0 == deviceHandle) ||
            (0 == commandBufferHandle) ||
            (0 == queryPoolHandle)
        ) {
            throw new ArgumentException(message: "Vulkan device, command-buffer, and query-pool handles must be non-zero.");
        }

        var cmdWriteTimestamp = GetPointers(deviceHandle: deviceHandle).CmdWriteTimestamp;

        cmdWriteTimestamp(
            commandBufferHandle,
            pipelineStage,
            queryPoolHandle,
            query
        );
    }
    /// <inheritdoc/>
    public VkResult GetTimestampResults(nint deviceHandle, nint queryPoolHandle, uint firstQuery, uint queryCount, Span<ulong> results) {
        if (
            (0 == deviceHandle) ||
            (0 == queryPoolHandle)
        ) {
            throw new ArgumentException(message: "Vulkan device and query-pool handles must be non-zero.");
        }

        if (0 == queryCount) {
            return VkResult.Success;
        }

        if ((uint)results.Length < queryCount) {
            throw new ArgumentException(
                message: "Result span is smaller than the requested query count.",
                paramName: nameof(results)
            );
        }

        var getQueryPoolResults = GetPointers(deviceHandle: deviceHandle).GetQueryPoolResults;

        fixed (ulong* pData = results) {
            return getQueryPoolResults(
                deviceHandle,
                queryPoolHandle,
                firstQuery,
                queryCount,
                (nuint)(queryCount * (uint)sizeof(ulong)),
                (nint)pData,
                (ulong)sizeof(ulong),
                QueryResult64Bit | QueryResultWaitBit
            );
        }
    }

    private unsafe struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, in VkQueryPoolCreateInfo, nint, out nint, VkResult> CreateQueryPool;
        public delegate* unmanaged[Cdecl]<nint, nint, nint, void> DestroyQueryPool;
        public delegate* unmanaged[Cdecl]<nint, nint, uint, uint, void> CmdResetQueryPool;
        public delegate* unmanaged[Cdecl]<nint, uint, nint, uint, void> CmdWriteTimestamp;
        public delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nuint, nint, ulong, uint, VkResult> GetQueryPoolResults;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, DevicePointers> m_pointers = new();

    private unsafe DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }
        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkCreateQueryPool"u8) {
            pNew.CreateQueryPool = (delegate* unmanaged[Cdecl]<nint, in VkQueryPoolCreateInfo, nint, out nint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkDestroyQueryPool"u8) {
            pNew.DestroyQueryPool = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdResetQueryPool"u8) {
            pNew.CmdResetQueryPool = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkCmdWriteTimestamp"u8) {
            pNew.CmdWriteTimestamp = (delegate* unmanaged[Cdecl]<nint, uint, nint, uint, void>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetQueryPoolResults"u8) {
            pNew.GetQueryPoolResults = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nuint, nint, ulong, uint, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private unsafe delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
        lock (m_syncRoot) {
            if (m_getDeviceProcAddr is not null) {
                return m_getDeviceProcAddr;
            }
            var export = VulkanNativeLibrary.GetExport(functionName: "vkGetDeviceProcAddr");

            m_getDeviceProcAddr = (delegate* unmanaged[Cdecl]<nint, byte*, nint>)export;
            return m_getDeviceProcAddr;
        }
    }
}
