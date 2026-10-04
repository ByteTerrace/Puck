namespace Puck.SignedDistance.Illumination;

/// <summary>The source of outgoing light at its last surface interaction. Continuations preserve these categories.</summary>
public enum IrradianceSource {
    /// <summary>Authored analytic light reflected at the hit.</summary>
    Direct,
    /// <summary>The preceding sweep's irradiance reflected at the hit.</summary>
    Feedback,
    /// <summary>The hit material's own emission.</summary>
    Emission,
    /// <summary>Environment light read at an exit.</summary>
    Sky,
    /// <summary>Screen light reflected at the hit.</summary>
    Screens,
}

/// <summary>Independently accumulated linear RGB contributions. Their sum is the lighting answer; no category is
/// inferred from that sum. A feedback interaction is attributed to feedback regardless of its earlier origin.</summary>
/// <param name="Direct">Reflected analytic light.</param>
/// <param name="Feedback">Reflected irradiance from the preceding sweep.</param>
/// <param name="Emission">Self-emission.</param>
/// <param name="Sky">Exit environment radiance.</param>
/// <param name="Screens">Reflected screen light.</param>
public readonly record struct IrradianceContributions(Double3 Direct, Double3 Feedback, Double3 Emission, Double3 Sky, Double3 Screens) {
    /// <summary>Gets the sum of the independently accumulated sources.</summary>
    public Double3 Total => Direct + Feedback + Emission + Sky + Screens;

    /// <summary>Returns the categories admitted by the rendered receiver, independently of its solve's source mask.</summary>
    /// <param name="sources">The final receiver selection.</param>
    /// <returns>The admitted incident irradiance.</returns>
    public Double3 Select(SdfIndirectSources sources) =>
        ((sources & SdfIndirectSources.Direct) != 0 ? Direct : Double3.Zero)
        + ((sources & SdfIndirectSources.Feedback) != 0 ? Feedback : Double3.Zero)
        + ((sources & SdfIndirectSources.Emission) != 0 ? Emission : Double3.Zero)
        + ((sources & SdfIndirectSources.Sky) != 0 ? Sky : Double3.Zero)
        + ((sources & SdfIndirectSources.Screens) != 0 ? Screens : Double3.Zero);

    /// <summary>Gets one source's contribution.</summary>
    /// <param name="source">The source category.</param>
    /// <returns>The category's linear RGB value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The category is undefined.</exception>
    public Double3 this[IrradianceSource source] => source switch {
        IrradianceSource.Direct => Direct,
        IrradianceSource.Feedback => Feedback,
        IrradianceSource.Emission => Emission,
        IrradianceSource.Sky => Sky,
        IrradianceSource.Screens => Screens,
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>Combines like sources without changing their provenance.</summary>
    /// <param name="left">The first contributions.</param>
    /// <param name="right">The second contributions.</param>
    /// <returns>The per-source sums.</returns>
    public static IrradianceContributions operator +(IrradianceContributions left, IrradianceContributions right) => new(
        left.Direct + right.Direct, left.Feedback + right.Feedback, left.Emission + right.Emission,
        left.Sky + right.Sky, left.Screens + right.Screens);

    /// <summary>Applies one transport weight to every source independently.</summary>
    /// <param name="value">The contributions.</param>
    /// <param name="weight">The transport weight.</param>
    /// <returns>The per-source weighted values.</returns>
    public static IrradianceContributions operator *(IrradianceContributions value, double weight) => new(
        value.Direct * weight, value.Feedback * weight, value.Emission * weight,
        value.Sky * weight, value.Screens * weight);
}
