using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports a single compile-time statistic about a pipeline executable (for example a register count): its
/// name, description, value format, and value.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): the trailing VkPipelineExecutableStatisticValueKHR union (b32 / i64 / u64 / f64, all 8 B) is
/// bound as a single ulong Value. Layout and size (544 B) match the C struct exactly.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPipelineExecutableStatisticKhr {
    private const int MaxDescriptionSize = 256;

    /// <summary>The type of this structure, as a <c>VkStructureType</c> value (<c>VK_STRUCTURE_TYPE_PIPELINE_EXECUTABLE_STATISTIC_KHR</c>).</summary>
    public uint SType;
    /// <summary>A pointer to a structure extending this one, or <see langword="null"/>.</summary>
    public nint PNext;
    /// <summary>A short human-readable name for the statistic, as a null-terminated UTF-8 string in a fixed 256-byte buffer.</summary>
    public fixed byte Name[MaxDescriptionSize];
    /// <summary>A human-readable description of the statistic, as a null-terminated UTF-8 string in a fixed 256-byte buffer.</summary>
    public fixed byte Description[MaxDescriptionSize];
    /// <summary>How <see cref="Value"/> should be interpreted, as a <c>VkPipelineExecutableStatisticFormatKHR</c> value.</summary>
    public uint Format;
    /// <summary>The statistic's value, reinterpreted according to <see cref="Format"/> (the original <c>VkPipelineExecutableStatisticValueKHR</c> union is bound as a single 8-byte value).</summary>
    public ulong Value;
}
