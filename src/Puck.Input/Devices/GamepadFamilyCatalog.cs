namespace Puck.Input.Devices;

/// <summary>The declared <see cref="GamepadType"/> vocabulary, derived from the enum itself — the icon badge-override
/// family-name hook resolves here.</summary>
public static class GamepadFamilyCatalog {
    /// <summary>Gets the number of declared non-<see cref="GamepadType.Unknown"/> families — the true cardinality a
    /// per-badge family-override ceiling equals.</summary>
    public static int Count { get; } = ComputeCount();

    private static int ComputeCount() {
        var count = 0;

        foreach (var value in Enum.GetValues<GamepadType>()) {
            if (value != GamepadType.Unknown) {
                count++;
            }
        }

        return count;
    }

    /// <summary>Returns whether a name is a declared, non-<see cref="GamepadType.Unknown"/> family, by exact
    /// (case-sensitive) member name.</summary>
    /// <param name="name">The candidate member name.</param>
    /// <remarks><see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> alone would accept a numeric spelling
    /// (<c>"1"</c>, <c>"01"</c>, <c>"+1"</c>); requiring the parsed value's canonical name to round-trip back to the
    /// exact input admits only the declared member spellings.</remarks>
    public static bool IsKnownName(string name) => (
        Enum.TryParse<GamepadType>(
            ignoreCase: false,
            result: out var family,
            value: name
        ) &&
        (family != GamepadType.Unknown) &&
        (Enum.GetName(value: family) == name)
    );
}
