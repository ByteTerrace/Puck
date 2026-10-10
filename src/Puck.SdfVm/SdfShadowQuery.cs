namespace Puck.SdfVm;

/// <summary>The CPU arithmetic reference for the soft-shadow query ceiling in sdf-occlusion.hlsli.</summary>
public static class SdfShadowQuery {
    /// <summary>Bounds a distance-only query without changing the march's stride, hit decision or visibility minimum.
    /// KEEP IN SYNC with softShadowVisibilityMarch, including its operation order and finite accumulator seed.</summary>
    /// <param name="stepCeiling">The current march step's upper limit.</param>
    /// <param name="traveled">Distance traveled along the shadow ray.</param>
    /// <param name="stepScale">The shading scale, including the receiver's gradient magnitude.</param>
    /// <param name="programStepScale">The program's positive final field multiplier.</param>
    /// <param name="sharpness">The light's positive penumbra sharpness.</param>
    /// <returns>The query ceiling before the program's final multiplier.</returns>
    public static float Ceiling(float stepCeiling, float traveled, float stepScale, float programStepScale, float sharpness) {
        var saturation = MathF.Max(x: MathF.Max(x: stepCeiling, y: (0.001f * stepScale)),
            y: ((traveled >= 0.12f) ? ((stepScale * traveled) / sharpness) : 0f));

        return MathF.Min(x: 1.0e9f, y: (saturation * (1.001f / programStepScale)));
    }
}
