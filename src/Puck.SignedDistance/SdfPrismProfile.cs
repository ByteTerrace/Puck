namespace Puck.SignedDistance;

/// <summary>The cross-section of a prism, in its local XY plane.</summary>
public enum SdfPrismProfileKind {
    /// <summary>A trapezoid controlled by the shape's taper.</summary>
    Trapezoid,
    /// <summary>A rectangle with independently controlled corner rounding.</summary>
    RoundedRectangle,
    /// <summary>A regular polygon, stretched by the authored X/Y scale.</summary>
    Polygon,
    /// <summary>An exact elliptical profile, extruded with flat front and rear caps.</summary>
    Ellipse,
}

/// <summary>Authored profile controls for a solid prism. The profile is extruded along local Z.</summary>
/// <param name="Kind">The profile family.</param>
/// <param name="CornerRadius">RoundedRectangle corner radius as a fraction of its smaller half-extent, in [0, 1].</param>
/// <param name="Sides">Polygon side count, from 3 through 32.</param>
public sealed record SdfPrismProfile(SdfPrismProfileKind Kind, float CornerRadius = 0.15f, int Sides = 6) {
    /// <summary>Whether all controls are finite and within the supported intervals.</summary>
    /// <returns>True for a supported profile with valid controls.</returns>
    public bool IsValid() => Enum.IsDefined(Kind) && float.IsFinite(CornerRadius) && CornerRadius >= 0f && CornerRadius <= 1f && Sides >= 3 && Sides <= 32;
}
