using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>How a cel style treats a light's shadow visibility beside the band it steps the light's cosine with.</summary>
public enum SdfToonBand {
    /// <summary>The visibility multiplies the band as the cascades' filter left it: <c>step(cosine) × visibility</c>, so
    /// a cast shadow keeps its soft penumbra.</summary>
    Visibility = 0,
    /// <summary>The visibility is stepped too: <c>step(cosine) × step(visibility)</c>, so a cast shadow is a held shape.
    /// The visibility's step is never sharper than the style's softness, a pixel, or what the shadow cascade resolves at
    /// the receiver, so a coarse cascade texel keeps its penumbra rather than printing the texel grid.</summary>
    Combined = 1,
}

/// <summary>Presentation-only cel shading: a style a material shades with. A world's default style
/// (<c>SdfFrame.Toon</c>) applies to every material that authors none; a material's own
/// (<see cref="SdfMaterial.Toon"/>) replaces it for that material alone. Colours are linear RGB, widths are output
/// pixels, distances are metres. The engine holds no look: every style is authored (the standard package's
/// <c>styles.puck</c>), and the default value is off.</summary>
public readonly record struct SdfToon {
    /// <summary>Whether a material shading with this style uses the cel response; a disabled style shades it
    /// continuously.</summary>
    public bool Enabled { get; init; }
    /// <summary>The diffuse cosine separating the shadow and lit bands.</summary>
    public float Threshold { get; init; }
    /// <summary>The full width of the soft band transition.</summary>
    public float Softness { get; init; }
    /// <summary>The share of each direct light, per linear RGB channel, that its shade side keeps (form and cast shadow
    /// alike) wherever the light reaches the surface's surroundings: the painted shadow colour. Sky and bounced light are
    /// not tinted.</summary>
    public Vector3 ShadowTint { get; init; }
    /// <summary>The outline radius at close range, in output pixels.</summary>
    public float OutlineWidth { get; init; }
    /// <summary>The distance at which the outline radius halves.</summary>
    public float OutlineDistance { get; init; }
    /// <summary>The normal cosine below which a crease is inked.</summary>
    public float OutlineNormal { get; init; }
    /// <summary>The linear colour multiplying an inked surface.</summary>
    public Vector3 OutlineTint { get; init; }
    /// <summary>The highlight's angular radius, as one minus its half-vector cosine.</summary>
    public float SpecularSize { get; init; }
    /// <summary>The exposure gain applied to reflected material light, in stops.</summary>
    public float Exposure { get; init; }
    /// <summary>The peak-preserving shoulder strength on reflected material light; zero disables compression.</summary>
    public float Shoulder { get; init; }
    /// <summary>How the band treats a light's shadow visibility.</summary>
    public SdfToonBand Band { get; init; }

    /// <summary>Gets the style's mode word as the kernels read it (<c>shade/sdf-toon.hlsli</c>): zero when disabled,
    /// one for <see cref="SdfToonBand.Visibility"/>, two for <see cref="SdfToonBand.Combined"/>.</summary>
    public float Mode => (Enabled ? ((Band == SdfToonBand.Combined) ? 2f : 1f) : 0f);

    /// <summary>Writes the style's four kernel rows, laid out as the pass block's toon members and a material's rows 22
    /// to 25: bands (mode, threshold, softness, specular size), shadow (tint, exposure gain), outline (width,
    /// halving distance, crease cosine, shoulder) and ink (tint, <paramref name="inkW"/>).</summary>
    /// <param name="mode">The mode word: <see cref="Mode"/> for a world's default, or a material's own encoding.</param>
    /// <param name="inkW">The ink row's fourth component.</param>
    /// <param name="rows">Receives the four rows; at least four long.</param>
    public void WriteRows(float mode, float inkW, Span<Vector4> rows) {
        rows[0] = new(mode, Threshold, Softness, SpecularSize);
        rows[1] = new(ShadowTint, MathF.Pow(x: 2f, y: Exposure));
        rows[2] = new(OutlineWidth, OutlineDistance, OutlineNormal, Shoulder);
        rows[3] = new(OutlineTint, inkW);
    }
    /// <summary>Gets whether every value lies in its admitted range (<see cref="Refusals"/> is empty).</summary>
    public bool IsValid => !Refusals().Any();

    /// <summary>Lists each value outside its admitted range, named by its document field (<c>threshold</c>,
    /// <c>shadowTint[1]</c>, ...).</summary>
    /// <returns>One message per refused value; empty when every value is admitted.</returns>
    public IEnumerable<string> Refusals() {
        var ranges = new (string Name, float Value, float Min, float Max)[] {
            ("threshold", Threshold, 0f, 1f), ("softness", Softness, .001f, .5f), ("outlineWidth", OutlineWidth, 0f, 4f),
            ("outlineDistance", OutlineDistance, .1f, 1000f), ("outlineNormal", OutlineNormal, 0f, .95f),
            ("specularSize", SpecularSize, .001f, .25f), ("exposure", Exposure, -4f, 4f), ("shoulder", Shoulder, 0f, 2f),
            ("shadowTint[0]", ShadowTint.X, 0f, 1f), ("shadowTint[1]", ShadowTint.Y, 0f, 1f), ("shadowTint[2]", ShadowTint.Z, 0f, 1f),
            ("outlineTint[0]", OutlineTint.X, 0f, 1f), ("outlineTint[1]", OutlineTint.Y, 0f, 1f), ("outlineTint[2]", OutlineTint.Z, 0f, 1f),
        };

        foreach (var (name, value, min, max) in ranges) {
            if (!float.IsFinite(f: value) || (value < min) || (value > max)) {
                yield return $"{name} {value} lies outside [{min}, {max}]";
            }
        }
        if (!Enum.IsDefined(value: Band)) {
            yield return $"band {Band} names no band";
        }
    }
}
