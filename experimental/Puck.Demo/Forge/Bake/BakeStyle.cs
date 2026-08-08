namespace Puck.Demo.Forge.Bake;

/// <summary>The dither modes a bake style may apply during quantization.</summary>
internal enum BakeDither {
    /// <summary>No dithering — flat fills quantize to flat fills (the clean poster look).</summary>
    None = 0,
    /// <summary>A 4×4 ordered Bayer pattern — deterministic, position-based (never RNG).</summary>
    Ordered4x4 = 1,
}

/// <summary>
/// One named bake recipe: the deterministic grading curve, dither, outline, and supersample choices that give a bake
/// its look. Styles are DATA — the pipeline never branches on a style name, only on these fields.
/// </summary>
/// <param name="Name">The style's name (the diagnostics label and the document's <c>bakeStyle</c> value).</param>
/// <param name="Dither">The dither mode applied at native resolution during quantization.</param>
/// <param name="DitherStrength">The dither amplitude in RGB555 LSBs (0..1 of one 5-bit step).</param>
/// <param name="Contrast">The contrast stretch about mid-grey (1 = none).</param>
/// <param name="Gamma">The gamma applied to Rec.709 luma after the contrast stretch (1 = none).</param>
/// <param name="SaturationBoost">The pre-fit saturation lift (CGB only; 0 = none).</param>
/// <param name="ExtractOutline">Whether to darken silhouette/edge pixels before the palette fit.</param>
/// <param name="OutlineThreshold">The normalized Sobel-luma magnitude a background edge must exceed.</param>
/// <param name="SupersampleFactor">The raster supersample per axis (the box-reduce factor back to native).</param>
/// <param name="MaxBackgroundPalettes">The style's background palette ceiling (min'd with the plan budget).</param>
/// <param name="MaxObjectPalettes">The style's object palette ceiling (min'd with the plan budget).</param>
internal sealed record BakeStyle(
    string Name,
    BakeDither Dither,
    float DitherStrength,
    float Contrast,
    float Gamma,
    float SaturationBoost,
    bool ExtractOutline,
    float OutlineThreshold,
    int SupersampleFactor,
    int MaxBackgroundPalettes,
    int MaxObjectPalettes
);

/// <summary>The built-in bake styles and the forgiving name resolver the document's <c>bakeStyle</c> knob rides.</summary>
internal static class BakeStyles {
    /// <summary>The CLASSIC style: no dither, a gentle contrast lift, and a darkened outline — the clean, readable
    /// hand-pixelled look.</summary>
    public static BakeStyle Classic { get; } = new(
        Contrast: 1.15f,
        Dither: BakeDither.None,
        DitherStrength: 0f,
        ExtractOutline: true,
        Gamma: 1.0f,
        MaxBackgroundPalettes: 8,
        MaxObjectPalettes: 8,
        Name: "classic",
        OutlineThreshold: 0.35f,
        SaturationBoost: 0f,
        SupersampleFactor: 2
    );

    /// <summary>The BOLD style: strong ordered dithering, punched contrast, a brightness lift, and a saturation
    /// boost — the chunky, textured demo-scene look.</summary>
    public static BakeStyle Bold { get; } = new(
        Contrast: 1.3f,
        Dither: BakeDither.Ordered4x4,
        DitherStrength: 0.6f,
        ExtractOutline: false,
        Gamma: 0.9f,
        MaxBackgroundPalettes: 8,
        MaxObjectPalettes: 8,
        Name: "bold",
        OutlineThreshold: 0.35f,
        SaturationBoost: 0.15f,
        SupersampleFactor: 4
    );

    /// <summary>Resolves a style by name (case-insensitive). An unknown or missing name falls back to
    /// <see cref="Classic"/> and reports why through <paramref name="diagnostic"/> — the caller surfaces it as a
    /// bake warning rather than failing the bake.</summary>
    /// <param name="name">The requested style name (a document's <c>bakeStyle</c>; null = classic).</param>
    /// <param name="diagnostic">A one-line note when the name did not resolve; null when it did.</param>
    /// <returns>The resolved style.</returns>
    public static BakeStyle Resolve(string? name, out string? diagnostic) {
        diagnostic = null;

        if ((name is null) || string.Equals(a: name, b: Classic.Name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return Classic;
        }

        if (string.Equals(a: name, b: Bold.Name, comparisonType: StringComparison.OrdinalIgnoreCase)) {
            return Bold;
        }

        diagnostic = $"unknown bake style '{name}' — using {Classic.Name}";

        return Classic;
    }
}
