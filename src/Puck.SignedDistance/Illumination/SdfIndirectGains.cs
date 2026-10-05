using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>Finite source gains in [0, 1], applied at source admission and once per reflected feedback hop.</summary>
/// <param name="Lights">Explicit diffuse lights.</param>
/// <param name="Emission">Material emission.</param>
/// <param name="Screens">Acquired screen-face emission.</param>
/// <param name="Sky">Certified world-exit radiance.</param>
/// <param name="Feedback">Previous complete-bank reflection.</param>
public readonly record struct SdfIndirectGains(float Lights, float Emission, float Screens, float Sky, float Feedback) {
    /// <summary>Gets the physical unit source gains.</summary>
    public static SdfIndirectGains One { get; } = new(1f, 1f, 1f, 1f, 1f);

    /// <summary>Gets the enabled categories. A zero gain is explicitly disabled.</summary>
    public SdfIndirectSources Sources => (Lights > 0 ? SdfIndirectSources.Direct : 0) |
        (Emission > 0 ? SdfIndirectSources.Emission : 0) | (Screens > 0 ? SdfIndirectSources.Screens : 0) |
        (Sky > 0 ? SdfIndirectSources.Sky : 0) | (Feedback > 0 ? SdfIndirectSources.Feedback : 0);

    /// <summary>Refuses values outside the finite unit domain before packing a frame.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A source gain is not finite or lies outside [0, 1].</exception>
    public void Validate() {
        Unit(Lights, nameof(Lights)); Unit(Emission, nameof(Emission)); Unit(Screens, nameof(Screens));
        Unit(Sky, nameof(Sky)); Unit(Feedback, nameof(Feedback));
    }

    internal static void Unit(float value, string name) {
        if (!float.IsFinite(value) || value < 0f || value > 1f) { throw new ArgumentOutOfRangeException(name, value, "Indirect gains lie in [0, 1]."); }
    }
}

/// <summary>Receiver-only indirect controls; they never alter cached incoming source contributions.</summary>
/// <param name="Intensity">Final indirect diffuse gain in [0, 1].</param>
/// <param name="Tint">Final indirect diffuse color, each channel in [0, 1].</param>
/// <param name="Contact">Existing ambient-occlusion attenuation from none at zero to full at one.</param>
public readonly record struct SdfIndirectApplication(float Intensity, Vector3 Tint, float Contact) {
    /// <summary>Gets unit intensity and tint with the existing full ambient-occlusion attenuation.</summary>
    public static SdfIndirectApplication Default { get; } = new(1f, Vector3.One, 1f);

    /// <summary>Refuses values outside the finite unit domain before packing a frame.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A gain or tint channel is not finite or lies outside [0, 1].</exception>
    public void Validate() {
        SdfIndirectGains.Unit(Intensity, nameof(Intensity)); SdfIndirectGains.Unit(Contact, nameof(Contact));
        SdfIndirectGains.Unit(Tint.X, nameof(Tint)); SdfIndirectGains.Unit(Tint.Y, nameof(Tint)); SdfIndirectGains.Unit(Tint.Z, nameof(Tint));
    }
}
