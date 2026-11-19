namespace Puck.Abstractions.Gpu;

/// <summary>
/// The optional capabilities a kernel variant may depend on: what only some devices have, read from the device when it
/// is created and never from its name (<see cref="GpuDeviceCapabilities.Optional"/>). A device is created with each one
/// it reports unless the host withholds it (<see cref="GpuDeviceOptions.Withheld"/>), so a variant compiled for a
/// capability builds only on a device that has it (<see cref="GpuDeviceCapabilities.Has"/>), and the path it replaces
/// runs everywhere else. Each flag means one thing on both backends.
/// </summary>
[Flags]
public enum GpuOptionalCapabilities : uint {
    /// <summary>Nothing optional.</summary>
    None = 0U,
    /// <summary>A compute kernel traces rays through an acceleration structure with an inline ray query
    /// (<c>RayQuery</c>), and the device builds and compacts acceleration structures: Direct3D 12's raytracing tier 1.1 on an <c>ID3D12Device5</c>; Vulkan's
    /// <c>VK_KHR_acceleration_structure</c>, <c>VK_KHR_ray_query</c> and <c>VK_KHR_deferred_host_operations</c> with the
    /// <c>accelerationStructure</c>, <c>rayQuery</c> and <c>bufferDeviceAddress</c> features.</summary>
    RayQuery = (1U << 0),
}

/// <summary>The one spelling of each <see cref="GpuOptionalCapabilities"/> flag, shared by the capabilities readout and
/// the World's <c>--withhold-gpu</c> flag.</summary>
public static class GpuOptionalCapabilitiesSpelling {
    // Each flag's one spelling, in declaration order.
    private static readonly (GpuOptionalCapabilities Flag, string Name)[] Names = [
        (GpuOptionalCapabilities.RayQuery, "ray-query"),
    ];

    /// <summary>Gets every spelling, in declaration order.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Names.Select(selector: static entry => entry.Name)];

    /// <summary>Returns the flags as they print: each set flag's name joined by <c>+</c> in declaration order, or
    /// <c>none</c>.</summary>
    /// <param name="capabilities">The flags.</param>
    /// <returns>The spelling.</returns>
    public static string Of(GpuOptionalCapabilities capabilities) => FlagSpelling.Of(flags: capabilities, names: Names);
    /// <summary>Reads one flag's spelling.</summary>
    /// <param name="name">The spelling, such as <c>ray-query</c>.</param>
    /// <param name="capability">The flag it names.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> spells a flag.</returns>
    public static bool TryParse(string name, out GpuOptionalCapabilities capability) {
        foreach (var (flag, spelling) in Names) {
            if (string.Equals(a: spelling, b: name, comparisonType: StringComparison.Ordinal)) {
                capability = flag;

                return true;
            }
        }

        capability = GpuOptionalCapabilities.None;

        return false;
    }
}
