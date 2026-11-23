namespace Puck.SignedDistance;

/// <summary>Presentation-only style mark: the engine half of a style a material shades with, which says only that the
/// material is styled. The banding and the ink are the material's shade program (<see cref="SdfMaterial.Shade"/>; the
/// standard package's <c>cel</c> and <c>ink</c> programs), never engine code. A world's default (<c>SdfFrame.Toon</c>)
/// applies to every material that authors none; a material's own (<see cref="SdfMaterial.Toon"/>) replaces it for that
/// material alone. The engine holds no look: the default value is off.</summary>
public readonly record struct SdfToon {
    /// <summary>Gets whether a material shading with this style is styled: left alone by the built-in curvature terms,
    /// and continuous and uninked while the live <c>world.toon off</c> suppresses every style.</summary>
    public bool Enabled { get; init; }
    /// <summary>Gets whether a world's default style also suppresses every material's own style, so the whole view
    /// shades continuously and uninked: the live <c>world.toon off</c>, which compares continuous against cel in one view.
    /// Only a world's default reads it; a material's own style ignores it.</summary>
    public bool Suppressed { get; init; }

    /// <summary>Gets the style's mode word as a world default writes it (<c>shade/sdf-toon.hlsli</c>): one when enabled
    /// and zero when not, less two (minus one for a default authored off, minus two for one authored on) while
    /// <see cref="Suppressed"/>.</summary>
    public float Mode => (Suppressed ? (Enabled ? -2f : -1f) : (Enabled ? 1f : 0f));
}
