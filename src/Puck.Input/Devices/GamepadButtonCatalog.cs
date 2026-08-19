using System.Numerics;

namespace Puck.Input.Devices;

/// <summary>The declared <see cref="GamepadButtons"/> vocabulary, derived from the enum itself so no consumer
/// restates a member count or name list — the binding-bar slot reservation and the schema-side name hook both
/// resolve here.</summary>
public static class GamepadButtonCatalog {
    /// <summary>Gets the number of declared single-flag buttons (<see cref="GamepadButtons.None"/> and any
    /// composite members excluded).</summary>
    public static int Count { get; } = ComputeCount();

    private static int ComputeCount() {
        var count = 0;

        foreach (var value in Enum.GetValues<GamepadButtons>()) {
            if (
                (value != GamepadButtons.None) &&
                (BitOperations.PopCount(value: ((uint)value)) == 1)
            ) {
                count++;
            }
        }

        return count;
    }

    /// <summary>Returns whether a name is a declared, non-<see cref="GamepadButtons.None"/> flag, by exact
    /// (case-sensitive) member name.</summary>
    /// <param name="name">The candidate member name.</param>
    public static bool IsKnownName(string name) => (
        Enum.TryParse<GamepadButtons>(
            value: name,
            ignoreCase: false,
            result: out var button
        ) &&
        (button != GamepadButtons.None) &&
        Enum.IsDefined(value: button)
    );
}
