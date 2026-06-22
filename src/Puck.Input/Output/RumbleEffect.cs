namespace Puck.Input.Output;

/// <summary>
/// A dual-motor rumble request. <paramref name="LowFrequency"/> drives the large/low-frequency motor and
/// <paramref name="HighFrequency"/> the small/high-frequency motor, each a normalized 0..1 intensity. The
/// effect plays for <paramref name="DurationMilliseconds"/>, after which the backend returns the motors to
/// rest (a duration of zero stops rumble immediately).
/// </summary>
/// <param name="LowFrequency">The low-frequency motor intensity, 0..1.</param>
/// <param name="HighFrequency">The high-frequency motor intensity, 0..1.</param>
/// <param name="DurationMilliseconds">How long the effect plays before the motors return to rest.</param>
public readonly record struct RumbleEffect(float LowFrequency, float HighFrequency, uint DurationMilliseconds) {
    /// <summary>A request that stops all rumble.</summary>
    public static RumbleEffect Off => new(LowFrequency: 0f, HighFrequency: 0f, DurationMilliseconds: 0u);
}
