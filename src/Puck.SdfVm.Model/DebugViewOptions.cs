namespace Puck.SdfVm;

/// <summary>
/// What a debug view reads beside its mode (<see cref="DebugViewModes"/>), carried in every pass block as the
/// <c>debugOptions</c> value. Only the proximity view (<see cref="DebugViewModes.Proximity"/>) reads it: every surface is
/// ringed every <see cref="Band"/> metres of its distance to the nearest other object, and glows where another object's
/// solid lies deeper than <see cref="Tolerance"/> behind it, so solids meant to touch (a foot on a floor, a cabinet against
/// a wall) ring to a white contact line without glowing; a moving rigid copy (a body and what it carries) takes part only
/// with <see cref="Bodies"/>.
/// </summary>
/// <param name="Tolerance">The overlap, in metres, below which no surface glows; zero or more.</param>
/// <param name="Bodies">Whether moving rigid copies (bodies, and the rows riding them) and the seat's own character take
/// part in the proximity view.</param>
/// <param name="Band">The distance, in metres, between the proximity view's rings; positive.</param>
public readonly record struct DebugViewOptions(float Tolerance, bool Bodies, float Band) {
    /// <summary>The overlap a contact between two baked surfaces may read without being drawn: three millimetres.</summary>
    public const float DefaultTolerance = 0.003f;
    /// <summary>The distance between the proximity view's rings when none is named: five centimetres.</summary>
    public const float DefaultBand = 0.05f;
    /// <summary>Gets the options every view starts with: <see cref="DefaultTolerance"/>, bodies left out,
    /// <see cref="DefaultBand"/>.</summary>
    public static DebugViewOptions Default { get; } = new(Tolerance: DefaultTolerance, Bodies: false, Band: DefaultBand);
}
