namespace Puck.Demo;

/// <summary>
/// A producer whose SDF debug view mode can be set — the seam the demo's <c>debug.view</c> command and the
/// parity harness drive without depending on the concrete producer.
/// </summary>
internal interface IDebugViewTarget {
    /// <summary>Gets or sets the active debug view mode (see <see cref="DebugViewModes"/>); 0 is the final image.</summary>
    int DebugMode { get; set; }
}

/// <summary>
/// The SDF debug view modes, by name and index. The index <em>is</em> the mode value packed into the camera
/// push constant and decoded by the shader's <c>switch</c>, so the order here must match the shader.
/// </summary>
internal static class DebugViewModes {
    /// <summary>The mode names, indexed by mode value (0 = final image).</summary>
    public static readonly string[] Names = [
        "off",
        "depth",
        "normals",
        "raydir",
        "material-id",
        "iteration-count",
    ];

    /// <summary>Gets the number of debug view modes.</summary>
    public static int Count => Names.Length;

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
