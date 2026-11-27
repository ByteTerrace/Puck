namespace Puck.SdfVm;

/// <summary>
/// What a debug view reads beside its mode (<see cref="DebugViewModes"/>), carried in every pass block as the
/// <c>debugOptions</c> value. Only the overlap view (<see cref="DebugViewModes.Overlaps"/>) reads it today: a surface glows
/// where the first overlap behind it along the pixel's ray runs deeper than <see cref="Tolerance"/>, so solids meant to
/// touch (a foot on a floor, a cabinet against a wall) stay dark, and a moving rigid copy (a body and what it carries)
/// takes part only with <see cref="Bodies"/>.
/// </summary>
/// <param name="Tolerance">The overlap, in world units along the pixel's ray, below which no surface glows; zero or
/// more.</param>
/// <param name="Bodies">Whether moving rigid copies (bodies, and the rows riding them) take part in the overlap view.</param>
public readonly record struct DebugViewOptions(float Tolerance, bool Bodies) {
    /// <summary>The overlap a contact between two baked surfaces may read without being drawn: three millimetres.</summary>
    public const float DefaultTolerance = 0.003f;
    /// <summary>Gets the options every view starts with: <see cref="DefaultTolerance"/>, bodies left out.</summary>
    public static DebugViewOptions Default { get; } = new(Tolerance: DefaultTolerance, Bodies: false);
}
