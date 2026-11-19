namespace Puck.SignedDistance;

/// <summary>
/// A world's display transform (<c>render.tone</c>): the curve each view's display pass puts its exposed scene light
/// through before the sRGB encode, as Uchimura's toe, linear section and shoulder (<c>Shared/display-transform.hlsli</c>,
/// <c>puckToneCurve</c>). Every constant is the author's; the engine's neutral default, <see cref="None"/>, applies no
/// curve, so exposed light one reaches SDR white and brighter light is headroom an HDR display shows and an SDR display
/// clips.
/// </summary>
/// <param name="Peak">The value the shoulder rolls into, in exposed scene light; zero is no curve.</param>
/// <param name="Contrast">The linear section's slope.</param>
/// <param name="LinearStart">The exposed value where the toe gives way to the linear section.</param>
/// <param name="LinearLength">The share of the range above <paramref name="LinearStart"/> the linear section spans before
/// the shoulder.</param>
/// <param name="Toe">The toe's power: above one it darkens the shadows, below one it lifts them.</param>
public readonly record struct SdfToneCurve(float Peak, float Contrast, float LinearStart, float LinearLength, float Toe) {
    /// <summary>Gets the neutral display transform, which applies no curve.</summary>
    public static SdfToneCurve None => default;
    /// <summary>Gets whether this applies no curve.</summary>
    public bool IsNone => !(Peak > 0f);
}
