namespace Puck.Input.Output;

/// <summary>
/// A trigger (impulse) rumble request for controllers with trigger motors (Xbox One/Series). Each side is a
/// normalized 0..1 intensity. Plays for <paramref name="DurationMilliseconds"/>.
/// </summary>
/// <param name="Left">The left trigger motor intensity, 0..1.</param>
/// <param name="Right">The right trigger motor intensity, 0..1.</param>
/// <param name="DurationMilliseconds">How long the effect plays before the motors return to rest.</param>
public readonly record struct TriggerRumbleEffect(float Left, float Right, uint DurationMilliseconds);
