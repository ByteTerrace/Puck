namespace Puck.Input.Output;

/// <summary>One band of an HD-rumble actuator: a carrier frequency and its amplitude.</summary>
/// <param name="FrequencyHertz">The carrier frequency, in hertz, or zero for the actuator's default carrier (160 Hz
/// for a low band and 320 Hz for a high band on a Switch Pro). The Switch Pro reaches 41 to 626 Hz on its low band
/// and 82 to 1253 Hz on its high band and clamps a frequency outside them.</param>
/// <param name="Amplitude">The normalized amplitude, 0..1; clamped, with a non-finite value at rest.</param>
public readonly record struct HdRumbleBand(float FrequencyHertz, float Amplitude);
/// <summary>The two bands one HD-rumble actuator plays at once.</summary>
/// <param name="Low">The low band.</param>
/// <param name="High">The high band.</param>
public readonly record struct HdRumbleSide(HdRumbleBand Low, HdRumbleBand High) {
    /// <summary>Gets an actuator at rest.</summary>
    public static HdRumbleSide Rest => default;
    /// <summary>Gets the larger of the two band amplitudes.</summary>
    public float PeakAmplitude => MathF.Max(
        x: Low.Amplitude,
        y: High.Amplitude
    );
}
/// <summary>
/// An HD-rumble request for controllers whose actuators play arbitrary frequencies (the Switch Pro's linear
/// resonant actuators): two bands per side, each with its own carrier and amplitude. The effect plays for
/// <paramref name="DurationMilliseconds"/>, after which the actuators return to rest; a duration of zero stops
/// them immediately. A controller with ordinary motors receives each side's band amplitudes as its low and high
/// motor intensities.
/// </summary>
/// <param name="Left">The left actuator.</param>
/// <param name="Right">The right actuator.</param>
/// <param name="DurationMilliseconds">How long the effect plays before the actuators return to rest.</param>
public readonly record struct HdRumbleEffect(HdRumbleSide Left, HdRumbleSide Right, uint DurationMilliseconds);
