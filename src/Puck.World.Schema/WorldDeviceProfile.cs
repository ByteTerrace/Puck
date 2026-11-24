using Puck.Abstractions.Documents;
using System.Text.Json.Serialization;
using Puck.Abstractions.Presentation;

namespace Puck.World;

/// <summary>
/// One graphics device's profile: the quality table it runs and the memory it may use. A profile is data a world (the
/// standard package, for the shipped devices) authors in <c>render.devices</c>; the engine selects one by the adapter it
/// runs on (<see cref="WorldDeviceProfiles.Select"/>) or by name, applies its table through the same levers
/// <c>world.quality</c> writes, and admits memory against its bars. No device name or figure is engine code.
/// </summary>
/// <param name="Name">The profile's name, unique within the world, which <c>world.device-profile</c> and a benchmark
/// run select by.</param>
/// <param name="Match">The text the driver's adapter name must contain, ignoring case, for the engine to select the
/// profile on that adapter.</param>
/// <param name="Quality">The quality tier the profile runs as (<see cref="QualityTiers.Names"/>): the world's
/// <c>render</c> preset of that tier is its table unless <paramref name="Table"/> authors the device's own, and the tier's
/// indirect default applies where the table names none.</param>
/// <param name="DeviceMemoryMiB">The most device memory the process admits on a discrete adapter, in mebibytes, where
/// that is below the adapter's own budget less a tenth; absent leaves the device pool at the adapter's reading.</param>
/// <param name="HostMemoryMiB">The most host memory the process admits, in mebibytes, where that is below the machine's
/// budget; absent leaves it. A unified-memory device states its one bar here, since its device segments lie in the host
/// pool.</param>
/// <param name="Table">The device's own quality table: the levers it runs in place of the world's <c>render</c> preset
/// of <paramref name="Quality"/>, so each device meets its bar by the path it enables (its render scale, its distant
/// shadow map's texels, its indirect tier, which sizes the irradiance volume and its caches). Absent runs the tier's
/// preset.</param>
/// <param name="FrameBar">The device's frame bar: the window size its frame time is judged at and the rate it must hold
/// there. A host started with <c>--frame-bar</c> opens its window at this size, and a benchmark run judges its iterations
/// against this rate's period. Absent where the world states no bar for the device.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDeviceProfile(
    [property: DocumentText("a device profile's name, which a host selects the profile by (WorldDeviceProfiles.Find)")] string Name,
    [property: DocumentText("a device match pattern")] string Match,
    QualityTier Quality,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DeviceMemoryMiB = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HostMemoryMiB = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldQualityPreset? Table = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldFrameBar? FrameBar = null
) {
    /// <summary>Gets the device pool's memory bar in bytes, or <see langword="null"/> when the profile states none.</summary>
    [JsonIgnore]
    public long? DeviceMemoryBytes => (DeviceMemoryMiB is { } mebibytes) ? ((long)mebibytes << 20) : null;
    /// <summary>Gets the host pool's memory bar in bytes, or <see langword="null"/> when the profile states none.</summary>
    [JsonIgnore]
    public long? HostMemoryBytes => (HostMemoryMiB is { } mebibytes) ? ((long)mebibytes << 20) : null;

    /// <summary>Returns the table the profile runs: its own (<see cref="Table"/>), or the world's preset of its tier.</summary>
    /// <param name="render">The world's render defaults.</param>
    /// <returns>The table, or <see langword="null"/> where the profile authors none and the world no preset of its tier.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="render"/> is <see langword="null"/>.</exception>
    public WorldQualityPreset? TableIn(WorldRenderDefaults render) {
        ArgumentNullException.ThrowIfNull(argument: render);

        return (Table ?? render.Preset(tier: Quality));
    }
    /// <summary>Gets the name of the table the profile runs: its own name where it authors its own table, else its tier's.</summary>
    [JsonIgnore]
    public string TableName => ((Table is null) ? QualityTiers.Name(tier: Quality) : Name);
}
/// <summary>A device's frame bar: the window size its frame time is read at and the presented rate it must hold there.</summary>
/// <param name="Width">The window's client width in pixels.</param>
/// <param name="Height">The window's client height in pixels.</param>
/// <param name="Hertz">The presented frames a second the device must hold at that size.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldFrameBar(int Width, int Height, int Hertz) {
    /// <summary>Gets the period of the bar's rate, in milliseconds: the work past which an iteration misses the bar.</summary>
    [JsonIgnore]
    public double PeriodMilliseconds => (1000.0 / Hertz);
}
/// <summary>Selects a <see cref="WorldDeviceProfile"/> from a world's <c>render.devices</c>.</summary>
public static class WorldDeviceProfiles {
    /// <summary>Returns the first profile whose <see cref="WorldDeviceProfile.Match"/> the adapter's name contains,
    /// ignoring case.</summary>
    /// <param name="render">The world's render defaults.</param>
    /// <param name="adapterName">The adapter's name as the driver reports it; <see langword="null"/> where no device is
    /// open.</param>
    /// <returns>The profile, or <see langword="null"/> when no device is open or none matches.</returns>
    public static WorldDeviceProfile? Select(WorldRenderDefaults render, string? adapterName) {
        ArgumentNullException.ThrowIfNull(argument: render);
        if (string.IsNullOrEmpty(value: adapterName)) { return null; }
        foreach (var profile in render.Devices ?? []) {
            if (adapterName.Contains(value: profile.Match, comparisonType: StringComparison.OrdinalIgnoreCase)) { return profile; }
        }

        return null;
    }
    /// <summary>Returns the profile whose frame bar a host opens its window at: the named one, or, for <c>auto</c>, the one
    /// the adapter selects.</summary>
    /// <param name="render">The world's render defaults.</param>
    /// <param name="request">A profile's name, or <c>auto</c> for the one <paramref name="adapterName"/> selects.</param>
    /// <param name="adapterName">The adapter's name, read before any device opens; <see langword="null"/> where it cannot
    /// be read.</param>
    /// <param name="refusal">Why no frame bar applies, naming the profiles that author one; <see langword="null"/> when one
    /// does.</param>
    /// <returns>The profile, whose <see cref="WorldDeviceProfile.FrameBar"/> is set, or <see langword="null"/> with
    /// <paramref name="refusal"/> set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="render"/> or <paramref name="request"/> is <see langword="null"/>.</exception>
    public static WorldDeviceProfile? FrameBarOf(WorldRenderDefaults render, string request, string? adapterName, out string? refusal) {
        ArgumentNullException.ThrowIfNull(argument: render);
        ArgumentNullException.ThrowIfNull(argument: request);

        var named = string.Equals(a: request, b: "auto", comparisonType: StringComparison.Ordinal) ? null : request;
        var profile = ((named is null) ? Select(adapterName: adapterName, render: render) : Find(name: named, render: render));
        var authored = string.Join(separator: ", ", values: (render.Devices ?? []).Where(predicate: static candidate => candidate.FrameBar is not null).Select(selector: static candidate => candidate.Name)) is { Length: > 0 } names ? names : "none";

        refusal = (profile, named) switch {
            (null, null) => $"no device profile matches the adapter '{adapterName ?? "(unread)"}'; name one of the profiles that author a frame bar ({authored})",
            (null, _) => $"the world authors no device profile '{named}'; the profiles that author a frame bar are {authored}",
            ({ FrameBar: null }, _) => $"the device profile '{profile.Name}' authors no frame bar; the profiles that do are {authored}",
            _ => null,
        };

        return ((refusal is null) ? profile : null);
    }
    /// <summary>Returns the profile of a name.</summary>
    /// <param name="render">The world's render defaults.</param>
    /// <param name="name">The profile's name, exactly as authored.</param>
    /// <returns>The profile, or <see langword="null"/> when the world authors none of that name.</returns>
    public static WorldDeviceProfile? Find(WorldRenderDefaults render, string name) {
        ArgumentNullException.ThrowIfNull(argument: render);
        foreach (var profile in render.Devices ?? []) {
            if (string.Equals(a: profile.Name, b: name, comparisonType: StringComparison.Ordinal)) { return profile; }
        }

        return null;
    }
}
