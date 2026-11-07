using System.Globalization;
using System.Text;
using Puck.Vulkan.Bindings;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Reads a pipeline's driver executable statistics through <c>VK_KHR_pipeline_executable_properties</c> (registers per
/// thread, local and shared memory, instruction counts, whatever the driver reports) and writes them as one
/// <c>[pipeline-statistics]</c> line per executable on standard error, so a kernel's register or spill change reads from
/// the log without a GPU capture. The driver keeps statistics only for a pipeline created with
/// <see cref="CaptureStatisticsBit"/>; <see cref="VulkanGpuPipelineFactory"/> sets it while the device names its objects
/// (debug layers or a capture tool), the one diagnostic mode in which it also reports.
/// </summary>
public static unsafe class VulkanPipelineStatistics {
    /// <summary><c>VK_PIPELINE_CREATE_CAPTURE_STATISTICS_BIT_KHR</c>.</summary>
    public const uint CaptureStatisticsBit = 0x00000040;
    private const uint FormatBool32 = 0;
    private const uint FormatInt64 = 1;
    private const uint FormatUint64 = 2;
    private const uint FormatFloat64 = 3;
    private const uint StructureTypePipelineExecutableInfo = 1000269003;
    private const uint StructureTypePipelineExecutableProperties = 1000269002;
    private const uint StructureTypePipelineExecutableStatistic = 1000269004;
    private const uint StructureTypePipelineInfo = 1000269001;

    /// <summary>Gets whether <paramref name="device"/> can report statistics: it enabled
    /// <c>VK_KHR_pipeline_executable_properties</c> and resolved both entry points.</summary>
    /// <param name="device">The command table of the logical device.</param>
    /// <returns><see langword="true"/> when both entry points are bound.</returns>
    public static bool IsAvailable(VulkanDeviceCommands device) =>
        ((device is not null) && (device.GetPipelineExecutablePropertiesKhr != null) && (device.GetPipelineExecutableStatisticsKhr != null));

    /// <summary>Writes one line per executable of <paramref name="pipelineHandle"/>: its name and every statistic the
    /// driver reports for it. Writes nothing for a device without the extension or a pipeline whose query fails.</summary>
    /// <param name="device">The command table of the logical device that created the pipeline.</param>
    /// <param name="pipelineHandle">The native <c>VkPipeline</c> handle, created with <see cref="CaptureStatisticsBit"/>.</param>
    /// <param name="name">The pipeline's name, as the creator named it.</param>
    public static void Report(VulkanDeviceCommands device, nint pipelineHandle, string name) {
        if (!IsAvailable(device: device) || (pipelineHandle == 0)) {
            return;
        }

        var info = new VkPipelineInfoKhr {
            Pipeline = pipelineHandle,
            SType = StructureTypePipelineInfo,
        };
        var executableCount = 0u;

        if ((device.GetPipelineExecutablePropertiesKhr(device.Handle, &info, &executableCount, null) != VkResult.Success) || (executableCount == 0u)) {
            return;
        }

        var properties = new VkPipelineExecutablePropertiesKhr[executableCount];

        for (var index = 0; (index < properties.Length); index++) {
            properties[index].SType = StructureTypePipelineExecutableProperties;
        }

        fixed (VkPipelineExecutablePropertiesKhr* propertiesPointer = properties) {
            if (device.GetPipelineExecutablePropertiesKhr(device.Handle, &info, &executableCount, propertiesPointer) != VkResult.Success) {
                return;
            }
        }

        for (var executable = 0u; (executable < executableCount); executable++) {
            var executableInfo = new VkPipelineExecutableInfoKhr {
                ExecutableIndex = executable,
                Pipeline = pipelineHandle,
                SType = StructureTypePipelineExecutableInfo,
            };
            var statisticCount = 0u;

            if ((device.GetPipelineExecutableStatisticsKhr(device.Handle, &executableInfo, &statisticCount, null) != VkResult.Success) || (statisticCount == 0u)) {
                continue;
            }

            var statistics = new VkPipelineExecutableStatisticKhr[statisticCount];

            for (var index = 0; (index < statistics.Length); index++) {
                statistics[index].SType = StructureTypePipelineExecutableStatistic;
            }

            var line = new StringBuilder();

            fixed (VkPipelineExecutableStatisticKhr* statisticsPointer = statistics) {
                if (device.GetPipelineExecutableStatisticsKhr(device.Handle, &executableInfo, &statisticCount, statisticsPointer) != VkResult.Success) {
                    continue;
                }

                fixed (VkPipelineExecutablePropertiesKhr* executableProperties = &properties[executable]) {
                    line.Append(value: "[pipeline-statistics] ")
                        .Append(value: name)
                        .Append(value: " (")
                        .Append(value: Utf8(text: executableProperties->Name))
                        .Append(value: ", subgroup ")
                        .Append(value: executableProperties->SubgroupSize.ToString(provider: CultureInfo.InvariantCulture))
                        .Append(value: "):");
                }

                for (var index = 0u; (index < statisticCount); index++) {
                    var statistic = &statisticsPointer[index];

                    line.Append(value: ' ')
                        .Append(value: Utf8(text: statistic->Name))
                        .Append(value: '=')
                        .Append(value: ValueOf(format: statistic->Format, value: statistic->Value));
                    line.Append(value: (index + 1u < statisticCount) ? ";" : string.Empty);
                }
            }

            Console.Error.WriteLine(value: line.ToString());
        }
    }

    private static string ValueOf(uint format, ulong value) => format switch {
        FormatBool32 => (((uint)value) != 0u) ? "true" : "false",
        FormatInt64 => ((long)value).ToString(provider: CultureInfo.InvariantCulture),
        FormatUint64 => value.ToString(provider: CultureInfo.InvariantCulture),
        FormatFloat64 => BitConverter.UInt64BitsToDouble(value: value).ToString(provider: CultureInfo.InvariantCulture),
        _ => value.ToString(provider: CultureInfo.InvariantCulture),
    };

    private static string Utf8(byte* text) => (System.Runtime.InteropServices.Marshal.PtrToStringUTF8(ptr: (nint)text) ?? string.Empty);
}
