namespace Puck.Vulkan.Messages;

/// <summary>
/// A single pipeline executable statistic in display-ready form: the executable it belongs to, its name,
/// and its value formatted as text.
/// </summary>
/// <param name="ExecutableName">The name of the pipeline executable the statistic belongs to.</param>
/// <param name="Name">The name of the statistic.</param>
/// <param name="Value">The statistic's value, formatted as a string according to its native type.</param>
public readonly record struct VulkanPipelineExecutableStatistic(
    string ExecutableName,
    string Name,
    string Value
);
