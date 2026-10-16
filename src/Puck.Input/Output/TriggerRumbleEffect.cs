namespace Puck.Input.Output;

/// <summary>
/// A trigger (impulse) rumble request for controllers with trigger motors (Xbox One/Series). Each side is a
/// normalized 0..1 intensity. Plays for <paramref name="DurationMilliseconds"/>; a duration of zero stops the
/// trigger motors immediately and leaves the body motors running.
/// </summary>
/// <param name="Left">The left trigger motor intensity, 0..1.</param>
/// <param name="Right">The right trigger motor intensity, 0..1.</param>
/// <param name="DurationMilliseconds">How long the effect plays before the trigger motors return to rest; zero stops them immediately.</param>
public readonly record struct TriggerRumbleEffect(float Left, float Right, uint DurationMilliseconds);
