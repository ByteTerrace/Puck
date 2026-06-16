namespace Puck.Vulkan;

/// <summary>
/// The queue family indices chosen for a device: one that supports graphics and one that supports
/// presentation. The two may be the same family.
/// </summary>
public readonly record struct VulkanQueueFamilySelection {
    /// <summary>Gets the index of the graphics-capable queue family.</summary>
    public uint GraphicsFamilyIndex { get; }
    /// <summary>Gets the index of the present-capable queue family.</summary>
    public uint PresentFamilyIndex { get; }
    /// <summary>Gets a value indicating whether a single queue family serves both graphics and presentation.</summary>
    public bool UsesSingleQueueFamily => (GraphicsFamilyIndex == PresentFamilyIndex);

    /// <summary>Initializes a new instance of the <see cref="VulkanQueueFamilySelection"/> struct.</summary>
    /// <param name="graphicsFamilyIndex">The index of the graphics-capable queue family.</param>
    /// <param name="presentFamilyIndex">The index of the present-capable queue family.</param>
    public VulkanQueueFamilySelection(uint graphicsFamilyIndex, uint presentFamilyIndex) {
        GraphicsFamilyIndex = graphicsFamilyIndex;
        PresentFamilyIndex = presentFamilyIndex;
    }
}
