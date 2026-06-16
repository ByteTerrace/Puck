using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;

namespace Puck.Vulkan;

/// <summary>Reads compiled-shader statistics via VK_KHR_pipeline_executable_properties; on
/// NVIDIA the set includes "Register Count" (the per-thread VGPR allocation that sets the
/// occupancy cliff). The query entry points resolve to null unless the device enabled the
/// extension, so every method degrades to a safe no-op. Diagnostic-only.</summary>
public unsafe sealed class VulkanNativePipelineStatisticsApi : IVulkanPipelineStatisticsApi {
    // Values verified against the Vulkan SDK 1.4 header (vulkan_core.h).
    private const uint StructureTypePipelineInfoKhr = 1000269001;
    private const int MaxDescriptionSize = 256;
    private const uint StructureTypePipelineExecutableInfoKhr = 1000269003;
    private const uint StructureTypePipelineExecutablePropertiesKhr = 1000269002;
    private const uint StructureTypePipelineExecutableStatisticKhr = 1000269004;

    private readonly Lock m_syncRoot = new();
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> m_getDeviceProcAddr;

    private struct DevicePointers {
        public delegate* unmanaged[Cdecl]<nint, VkPipelineInfoKhr*, uint*, VkPipelineExecutablePropertiesKhr*, VkResult> GetPipelineExecutableProperties;
        public delegate* unmanaged[Cdecl]<nint, VkPipelineExecutableInfoKhr*, uint*, VkPipelineExecutableStatisticKhr*, VkResult> GetPipelineExecutableStatistics;
    }

    private readonly ConcurrentDictionary<nint, DevicePointers> m_pointers = new();

    /// <inheritdoc/>
    public bool IsSupported(nint deviceHandle) {
        if (0 == deviceHandle) {
            return false;
        }

        var pointers = GetPointers(deviceHandle: deviceHandle);

        return (
            (pointers.GetPipelineExecutableProperties is not null) &&
            (pointers.GetPipelineExecutableStatistics is not null)
        );
    }
    /// <inheritdoc/>
    public IReadOnlyList<VulkanPipelineExecutableStatistic> QueryStatistics(nint deviceHandle, nint pipelineHandle) {
        if (
            (0 == deviceHandle) ||
            (0 == pipelineHandle)
        ) {
            return [];
        }

        var pointers = GetPointers(deviceHandle: deviceHandle);

        if (
            (pointers.GetPipelineExecutableProperties is null) ||
            (pointers.GetPipelineExecutableStatistics is null)
        ) {
            return [];
        }

        var pipelineInfo = new VkPipelineInfoKhr {
            Pipeline = pipelineHandle,
            SType = StructureTypePipelineInfoKhr,
        };
        var executableCount = 0U;

        if (
            (VkResult.Success != pointers.GetPipelineExecutableProperties(
                deviceHandle,
                &pipelineInfo,
                &executableCount,
                (VkPipelineExecutablePropertiesKhr*)null
            )) ||
            (0 == executableCount)
        ) {
            return [];
        }

        var results = new List<VulkanPipelineExecutableStatistic>();
        var properties = new VkPipelineExecutablePropertiesKhr[executableCount];

        fixed (VkPipelineExecutablePropertiesKhr* propertiesPointer = properties) {
            for (var index = 0u; (index < executableCount); index++) {
                propertiesPointer[index].SType = StructureTypePipelineExecutablePropertiesKhr;
                propertiesPointer[index].PNext = 0;
            }

            var propertiesResult = pointers.GetPipelineExecutableProperties(
                deviceHandle,
                &pipelineInfo,
                &executableCount,
                propertiesPointer
            );

            if (
                (VkResult.Success != propertiesResult) &&
                (VkResult.Incomplete != propertiesResult)
            ) {
                return [];
            }

            for (var executableIndex = 0u; (executableIndex < executableCount); executableIndex++) {
                var executableName = ReadFixedUtf8(namePointer: propertiesPointer[executableIndex].Name);
                var executableInfo = new VkPipelineExecutableInfoKhr {
                    ExecutableIndex = executableIndex,
                    Pipeline = pipelineHandle,
                    SType = StructureTypePipelineExecutableInfoKhr,
                };
                var statisticCount = 0U;

                if (
                    (VkResult.Success != pointers.GetPipelineExecutableStatistics(
                        deviceHandle,
                        &executableInfo,
                        &statisticCount,
                        (VkPipelineExecutableStatisticKhr*)null
                    )) ||
                    (0 == statisticCount)
                ) {
                    continue;
                }

                var statistics = new VkPipelineExecutableStatisticKhr[statisticCount];

                fixed (VkPipelineExecutableStatisticKhr* statisticsPointer = statistics) {
                    for (var index = 0u; (index < statisticCount); index++) {
                        statisticsPointer[index].SType = StructureTypePipelineExecutableStatisticKhr;
                        statisticsPointer[index].PNext = 0;
                    }

                    var statisticsResult = pointers.GetPipelineExecutableStatistics(
                        deviceHandle,
                        &executableInfo,
                        &statisticCount,
                        statisticsPointer
                    );

                    if (
                        (VkResult.Success != statisticsResult) &&
                        (VkResult.Incomplete != statisticsResult)
                    ) {
                        continue;
                    }

                    for (var index = 0u; (index < statisticCount); index++) {
                        results.Add(item: new VulkanPipelineExecutableStatistic(
                            ExecutableName: executableName,
                            Name: ReadFixedUtf8(namePointer: statisticsPointer[index].Name),
                            Value: FormatStatisticValue(
                                format: statisticsPointer[index].Format,
                                rawValue: statisticsPointer[index].Value
                            )
                        ));
                    }
                }
            }
        }

        return results;
    }

    private static string ReadFixedUtf8(byte* namePointer) {
        var span = new ReadOnlySpan<byte>(
            length: MaxDescriptionSize,
            pointer: namePointer
        );
        var terminator = span.IndexOf(value: (byte)0);

        return Encoding.UTF8.GetString(bytes: span[..((terminator < 0)
            ? MaxDescriptionSize
            : terminator)]);
    }

    // VkPipelineExecutableStatisticValueKHR is an 8-byte union; the format selects how the
    // raw bits are read (0 = bool32, 1 = int64, 2 = uint64, 3 = float64).
    private static string FormatStatisticValue(uint format, ulong rawValue) {
        return format switch {
            0 => ((0 != rawValue)
                ? "true"
                : "false"),
            1 => ((long)rawValue).ToString(provider: CultureInfo.InvariantCulture),
            2 => rawValue.ToString(provider: CultureInfo.InvariantCulture),
            3 => BitConverter.Int64BitsToDouble(value: (long)rawValue).ToString(
                format: "0.###",
                provider: CultureInfo.InvariantCulture
            ),
            _ => rawValue.ToString(provider: CultureInfo.InvariantCulture)
        };
    }
    private DevicePointers GetPointers(nint deviceHandle) {
        if (m_pointers.TryGetValue(
            key: deviceHandle,
            value: out var pointers
        )) {
            return pointers;
        }

        var getAddr = GetDeviceProcAddr();
        DevicePointers pNew = default;

        fixed (byte* pName = "vkGetPipelineExecutablePropertiesKHR"u8) {
            pNew.GetPipelineExecutableProperties = (delegate* unmanaged[Cdecl]<nint, VkPipelineInfoKhr*, uint*, VkPipelineExecutablePropertiesKhr*, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        fixed (byte* pName = "vkGetPipelineExecutableStatisticsKHR"u8) {
            pNew.GetPipelineExecutableStatistics = (delegate* unmanaged[Cdecl]<nint, VkPipelineExecutableInfoKhr*, uint*, VkPipelineExecutableStatisticKhr*, VkResult>)getAddr(
                deviceHandle,
                pName
            );
        }
        m_pointers[deviceHandle] = pNew;
        return pNew;
    }
    private delegate* unmanaged[Cdecl]<nint, byte*, nint> GetDeviceProcAddr() {
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
