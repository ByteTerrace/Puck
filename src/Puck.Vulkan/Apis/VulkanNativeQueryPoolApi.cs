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

    /// <inheritdoc/>
    public VkResult CreateTimestampPool(nint deviceHandle, uint queryCount, out nint queryPoolHandle) {
        VulkanArgument.RequireHandle(
            handle: deviceHandle,
            handleDescription: "logical-device",
            paramName: nameof(deviceHandle)
        );

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

    private DevicePointers GetPointers(nint deviceHandle) {
        return m_pointers.GetOrAdd(
            key: deviceHandle,
            valueFactory: static handle => new DevicePointers {
                CreateQueryPool = (delegate* unmanaged[Cdecl]<nint, in VkQueryPoolCreateInfo, nint, out nint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCreateQueryPool"u8),
                DestroyQueryPool = (delegate* unmanaged[Cdecl]<nint, nint, nint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkDestroyQueryPool"u8),
                CmdResetQueryPool = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCmdResetQueryPool"u8),
                CmdWriteTimestamp = (delegate* unmanaged[Cdecl]<nint, uint, nint, uint, void>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkCmdWriteTimestamp"u8),
                GetQueryPoolResults = (delegate* unmanaged[Cdecl]<nint, nint, uint, uint, nuint, nint, ulong, uint, VkResult>)VulkanProcResolver.ResolveDeviceProc(deviceHandle: handle, functionName: "vkGetQueryPoolResults"u8),
            }
        );
    }
}
