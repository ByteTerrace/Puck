using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Presentation-only cel style: the engine half of a style a material shades with, which marks the material
/// styled and carries its ink. The banding is the material's shade program (<see cref="SdfMaterial.Shade"/>; the
/// standard package's <c>cel</c> program), never engine code; the style keeps only what a per-pixel program cannot
/// express, the outline that reads neighbouring depth and normals. A world's default style (<c>SdfFrame.Toon</c>)
/// applies to every material that authors none; a material's own (<see cref="SdfMaterial.Toon"/>) replaces it for that
/// material alone. Colours are linear RGB, widths are output pixels, distances are metres. The engine holds no look:
/// every style is authored (the standard package's <c>styles.puck</c>), and the default value is off.</summary>
public readonly record struct SdfToon {
    /// <summary>Gets whether a material shading with this style is styled: inked, and continuous while the live
    /// <c>world.toon off</c> suppresses every style.</summary>
    public bool Enabled { get; init; }
    /// <summary>Gets the outline radius at close range, in output pixels; zero draws no outline.</summary>
    public float OutlineWidth { get; init; }
    /// <summary>Gets the distance, in metres, at which the outline radius halves.</summary>
    public float OutlineDistance { get; init; }
    /// <summary>Gets the normal cosine below which a crease is inked.</summary>
    public float OutlineNormal { get; init; }
    /// <summary>Gets the linear colour multiplying an inked surface.</summary>
    public Vector3 OutlineTint { get; init; }
    /// <summary>Gets whether a world's default style also suppresses every material's own style, so the whole view
    /// shades continuously and uninked: the live <c>world.toon off</c>, which compares continuous against cel in one view.
    /// Only a world's default reads it; a material's own style ignores it.</summary>
    public bool Suppressed { get; init; }

    /// <summary>Gets the style's mode word as a world default writes it (<c>shade/sdf-toon.hlsli</c>): one when enabled
    /// and zero when not, less two (minus one for a default authored off, minus two for one authored on) while
    /// <see cref="Suppressed"/>.</summary>
    public float Mode => (Suppressed ? (Enabled ? -2f : -1f) : (Enabled ? 1f : 0f));

    /// <summary>Writes the style's two kernel rows, laid out as the pass block's toon members and a material's rows 22 and
    /// 25: outline (mode, width, halving distance, crease cosine) and ink (tint, <paramref name="inkW"/>).</summary>
    /// <param name="mode">The mode word: <see cref="Mode"/> for a world's default, or a material's own encoding (one for
    /// on, minus one for off).</param>
    /// <param name="inkW">The ink row's fourth component.</param>
    /// <param name="rows">The span receiving the two rows; at least two long.</param>
    public void WriteRows(float mode, float inkW, Span<Vector4> rows) {
        rows[0] = new(mode, OutlineWidth, OutlineDistance, OutlineNormal);
        rows[1] = new(OutlineTint, inkW);
    }
    /// <summary>Gets whether every value lies in its admitted range (<see cref="Refusals"/> is empty).</summary>
    public bool IsValid => !Refusals().Any();

    /// <summary>Lists each value outside its admitted range, named by its document field (<c>outlineWidth</c>,
    /// <c>outlineTint[1]</c>, ...).</summary>
    /// <returns>One message per refused value; empty when every value is admitted.</returns>
    public IEnumerable<string> Refusals() {
        var ranges = new (string Name, float Value, float Min, float Max)[] {
            ("outlineWidth", OutlineWidth, 0f, 4f), ("outlineDistance", OutlineDistance, .1f, 1000f),
            ("outlineNormal", OutlineNormal, 0f, .95f),
            ("outlineTint[0]", OutlineTint.X, 0f, 1f), ("outlineTint[1]", OutlineTint.Y, 0f, 1f), ("outlineTint[2]", OutlineTint.Z, 0f, 1f),
        };

        foreach (var (name, value, min, max) in ranges) {
            if (!float.IsFinite(f: value) || (value < min) || (value > max)) {
                yield return $"{name} {value} lies outside [{min}, {max}]";
            }
        }
    }
}
