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
    private const int MaxDescriptionSize = 256;
    private const uint StructureTypePipelineExecutableInfoKhr = 1000269003;
    private const uint StructureTypePipelineExecutablePropertiesKhr = 1000269002;
    private const uint StructureTypePipelineExecutableStatisticKhr = 1000269004;
    // Values verified against the Vulkan SDK 1.4 header (vulkan_core.h).
    private const uint StructureTypePipelineInfoKhr = 1000269001;

    // VkPipelineExecutableStatisticValueKHR is an 8-byte union; the format selects how the
    // raw bits are read (0 = bool32, 1 = int64, 2 = uint64, 3 = float64).
    private static string FormatStatisticValue(uint format, ulong rawValue) {
        return format switch {
            0 => ((0 != rawValue)
            ? "true"
            : "false"),
            1 => ((long)rawValue).ToString(provider: CultureInfo.InvariantCulture),
            2 => rawValue.ToString(provider: CultureInfo.InvariantCulture),
            3 => BitConverter.Int64BitsToDouble(value: ((long)rawValue)).ToString(
            format: "0.###",
            provider: CultureInfo.InvariantCulture
        ),
            _ => rawValue.ToString(provider: CultureInfo.InvariantCulture)
        };
    }
    private static string ReadFixedUtf8(byte* namePointer) {
        var span = new ReadOnlySpan<byte>(
            length: MaxDescriptionSize,
            pointer: namePointer
        );
        var terminator = span.IndexOf(value: ((byte)0));

        return Encoding.UTF8.GetString(bytes: span[..((terminator < 0)
            ? MaxDescriptionSize
            : terminator)]);
    }

    /// <inheritdoc/>
    public bool IsSupported(VulkanDeviceCommands device) {
        if (device is null) {
            return false;
        }

        return (
            (device.GetPipelineExecutablePropertiesKhr is not null) &&
            (device.GetPipelineExecutableStatisticsKhr is not null)
        );
    }
    /// <inheritdoc/>
    public IReadOnlyList<VulkanPipelineExecutableStatistic> QueryStatistics(VulkanDeviceCommands device, nint pipelineHandle) {
        if (
            (device is null) ||
            (0 == pipelineHandle)
        ) {
            return [];
        }

        if (
            (device.GetPipelineExecutablePropertiesKhr is null) ||
            (device.GetPipelineExecutableStatisticsKhr is null)
        ) {
            return [];
        }

        var pipelineInfo = new VkPipelineInfoKhr {
            Pipeline = pipelineHandle,
            SType = StructureTypePipelineInfoKhr,
        };
        var executableCount = 0U;

        if (
            (VkResult.Success != device.GetPipelineExecutablePropertiesKhr(
            device.Handle,
            &pipelineInfo,
            &executableCount,
            ((VkPipelineExecutablePropertiesKhr*)null)
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

            var propertiesResult = device.GetPipelineExecutablePropertiesKhr(
                device.Handle,
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
                    (VkResult.Success != device.GetPipelineExecutableStatisticsKhr(
                    device.Handle,
                    &executableInfo,
                    &statisticCount,
                    ((VkPipelineExecutableStatisticKhr*)null)
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

                    var statisticsResult = device.GetPipelineExecutableStatisticsKhr(
                        device.Handle,
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
}
