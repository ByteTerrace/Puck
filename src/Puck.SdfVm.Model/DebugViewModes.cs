namespace Puck.SdfVm;

/// <summary>
/// The SDF debug view modes, by name and index. The index <em>is</em> the mode value the pass block's debug mode carries,
/// and <see cref="SdfIsaHlsl"/> writes the count and every mode's index into the kernels (<c>SDF_DEBUG_VIEW_COUNT</c>,
/// <c>SDF_DEBUG_VIEW_NORMALS</c>, ...), so a mode is appended here and the kernels' switches read it by name.
/// </summary>
public static class DebugViewModes {
    /// <summary>The mode names, indexed by mode value (0 = final image).</summary>
    public static readonly string[] Names = [
        "off",
        "depth",
        "normals",
        "raydir",
        "material-id",
        "visibility",
        "motion",
        "sky-cost",
        "ao",
        "shadow",
        "indirect",
        "rates",
        "rate-flash",
        "colliders",
        "contacts",
        "bloom",
    ];
    /// <summary>The <c>motion</c> view's mode value, which reads the previous view and transform tables.</summary>
    public static readonly int Motion = Array.IndexOf(array: Names, value: "motion");
    /// <summary>The camera's scatter alone, before the display curve.</summary>
    public static readonly int Bloom = Array.IndexOf(array: Names, value: "bloom");
    /// <summary>The sky-cost diagnostic, whose RGB shows evaluations, hashes and texture loads.</summary>
    public static readonly int SkyCost = Array.IndexOf(array: Names, value: "sky-cost");
    /// <summary>The tier view: the final image with every item a <c>world.rates</c> lever holds tinted by the tier it
    /// updates at (<see cref="SdfUpdateRateCode"/>).</summary>
    public static readonly int Rates = Array.IndexOf(array: Names, value: "rates");
    /// <summary>The tier view that tints a rated item only on the frames it updates, so its cadence shows as a
    /// flicker.</summary>
    public static readonly int RateFlash = Array.IndexOf(array: Names, value: "rate-flash");

    /// <summary>Whether diagnostics annotate a normally resolved scene without replacing its shading.</summary>
    /// <param name="mode">The debug mode.</param>
    /// <returns>True for the untouched scene and evidence-preserving diagnostics.</returns>
    public static bool PreservesScene(int mode) => ((mode == 0) || (Name(mode: mode) is "colliders" or "contacts" or "bloom"));

    /// <summary>Gets the number of debug view modes.</summary>
    public static int Count => Names.Length;

    /// <summary>Returns whether a mode is one of the tier views, which read each rated item's
    /// <see cref="SdfUpdateRateCode"/>.</summary>
    /// <param name="mode">The mode value.</param>
    /// <returns><see langword="true"/> for <see cref="Rates"/> and <see cref="RateFlash"/>.</returns>
    public static bool ShowsRates(int mode) => ((mode == Rates) || (mode == RateFlash));
    /// <summary>Returns the name of a mode, or <c>"off"</c> when out of range.</summary>
    /// <param name="mode">The mode value.</param>
    /// <returns>The mode name.</returns>
    public static string Name(int mode) {
        return (((mode >= 0) && (mode < Names.Length))
            ? Names[mode]
            : "off"
        );
    }
    /// <summary>Parses a mode name (case-insensitive) to its value.</summary>
    /// <param name="name">The mode name.</param>
    /// <param name="mode">The resolved mode value when found.</param>
    /// <returns><see langword="true"/> when the name is a known mode.</returns>
    public static bool TryParse(string name, out int mode) {
        for (var index = 0; (index < Names.Length); index++) {
            if (string.Equals(
                a: Names[index],
                b: name,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                mode = index;

                return true;
            }
        }

        mode = 0;

        return false;
    }
}
