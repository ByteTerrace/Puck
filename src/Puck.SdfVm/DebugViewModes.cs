namespace Puck.SdfVm;

/// <summary>
/// The SDF debug view modes, by name and index. The index <em>is</em> the mode value packed into the camera
/// push constant and decoded by the shader's <c>switch</c>, so the order here must match the shader — KEEP IN SYNC
/// with <c>DebugViewModeCount</c>/<c>DebugViewModeNormals</c> and the <c>viewMode</c> switch in
/// <c>src/Puck.SdfVm/Assets/Shaders/Sdf/sdf-world.hlsli</c> when adding or reordering modes.
/// </summary>
public static class DebugViewModes {
    private const string CommandPrefix = "debug.view.";

    /// <summary>The mode names, indexed by mode value (0 = final image).</summary>
    public static readonly string[] Names = [
        "off",
        "depth",
        "normals",
        "raydir",
        "material-id",
        "iteration-count",
        "termination",
        "slice",
        "mask",
        "overshoot",
        "evals",
    ];

    /// <summary>Gets the number of debug view modes.</summary>
    public static int Count => Names.Length;

    /// <summary>Returns the digital command name that directly selects a debug mode.</summary>
    /// <param name="mode">The mode value.</param>
    /// <returns>The command name.</returns>
    public static string Command(int mode) => (CommandPrefix + Name(mode: mode));

    /// <summary>Returns the name of a mode, or <c>"off"</c> when out of range.</summary>
    /// <param name="mode">The mode value.</param>
    /// <returns>The mode name.</returns>
    public static string Name(int mode) {
        return (((mode >= 0) && (mode < Names.Length))
            ? Names[mode]
            : "off");
    }
    /// <summary>Parses a mode name (case-insensitive) to its value.</summary>
    /// <param name="name">The mode name.</param>
    /// <param name="mode">The resolved mode value when found.</param>
    /// <returns><see langword="true"/> when the name is a known mode.</returns>
    public static bool TryParse(string name, out int mode) {
        for (var index = 0; (index < Names.Length); index++) {
            if (string.Equals(a: Names[index], b: name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                mode = index;

                return true;
            }
        }

        mode = 0;

        return false;
    }
}
