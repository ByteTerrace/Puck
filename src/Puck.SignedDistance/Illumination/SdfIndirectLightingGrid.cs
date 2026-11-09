using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>A fixed grid for indirect-light captures, independent of earlier captures. Zero leaves a component
/// continuous. Direct illumination uses the original frame lights.</summary>
/// <param name="DirectionDegrees">Directional-light azimuth and elevation step in degrees; zero disables snapping.</param>
/// <param name="Radiance">Fractional step per power-of-two range, independently per colour channel. Applied to
/// directional colour and weight, the sky radiance each probe ray carries and the sky peak; zero disables snapping.</param>
public readonly record struct SdfIndirectLightingGrid(float DirectionDegrees, float Radiance) {
    /// <summary>Whether either grid is authored.</summary>
    public bool Enabled => DirectionDegrees > 0 || Radiance > 0;

    /// <summary>Refuses nonfinite, negative or wider-than-domain steps.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Direction exceeds 180 degrees, radiance exceeds one, or a
    /// member is negative or nonfinite.</exception>
    public void Validate() {
        if (!float.IsFinite(DirectionDegrees) || DirectionDegrees < 0 || DirectionDegrees > 180) { throw new ArgumentOutOfRangeException(nameof(DirectionDegrees)); }
        if (!float.IsFinite(Radiance) || Radiance < 0 || Radiance > 1) { throw new ArgumentOutOfRangeException(nameof(Radiance)); }
    }

    /// <summary>Returns a directional on the fixed angular and photometric grids; other kinds are unchanged.</summary>
    /// <param name="light">The frame's light.</param>
    /// <returns>The light used by both the capture key and its shading.</returns>
    public SdfLight Snap(SdfLight light) {
        Validate();
        if (light.Kind != SdfLightKind.Directional || !Enabled) { return light; }
        if (DirectionDegrees > 0) {
            var direction = SdfLights.UnitDirection(light.Direction);
            var step = (double)DirectionDegrees * Math.PI / 180;
            var azimuth = Math.Round(Math.Atan2(direction.Z, direction.X) / step) * step;
            var elevation = Math.Clamp(Math.Round(Math.Asin(Math.Clamp((double)direction.Y, -1, 1)) / step) * step, -Math.PI / 2, Math.PI / 2);
            var radius = Math.Cos(elevation);
            light.Direction = SdfLights.UnitDirection(new Vector3((float)(radius * Math.Cos(azimuth)), (float)Math.Sin(elevation), (float)(radius * Math.Sin(azimuth))));
        }
        if (Radiance > 0) {
            Span<Vector3> color = stackalloc Vector3[1];
            color[0] = light.Color;
            SnapRadiance(color);
            light.Color = color[0];
            light.Weight = SnapScalar(light.Weight);
        }
        return light;
    }

    /// <summary>Snaps signed radiance values using a fixed quantum per channel, derived from that channel's
    /// current largest value's power-of-two range. Near-zero values share that quantum.</summary>
    /// <param name="values">The current capture's values, replaced in place.</param>
    public void SnapRadiance(Span<Vector3> values) {
        Validate();
        if (Radiance == 0) { return; }
        var largest = Vector3.Zero;
        foreach (var value in values) { largest = Vector3.Max(largest, Vector3.Abs(value)); }
        var x = Quantum(largest.X);
        var y = Quantum(largest.Y);
        var z = Quantum(largest.Z);
        for (var index = 0; index < values.Length; index++) {
            var value = values[index];
            values[index] = new Vector3(SnapValue(value.X, x), SnapValue(value.Y, y), SnapValue(value.Z, z));
        }
    }

    /// <summary>Snaps one radiometric value on its fixed power-of-two range.</summary>
    /// <param name="value">The current capture's value.</param>
    /// <returns>The snapped value, or the input for a zero step.</returns>
    public float SnapScalar(float value) {
        Validate();
        return Radiance == 0 ? value : SnapValue(value, Quantum(MathF.Abs(value)));
    }
    private double Quantum(float largest) => largest == 0 ? 0 : Math.ScaleB((double)Radiance, Math.ILogB((double)largest));
    private static float SnapValue(float value, double quantum) => quantum == 0 ? value : (float)(Math.Round(value / quantum) * quantum);
}
