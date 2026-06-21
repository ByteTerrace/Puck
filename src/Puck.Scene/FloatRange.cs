namespace Puck.Scene;

/// <summary>An inclusive <c>[Minimum, Maximum]</c> range of a scalar scene parameter.</summary>
/// <param name="Minimum">The inclusive lower bound.</param>
/// <param name="Maximum">The inclusive upper bound.</param>
public readonly record struct FloatRange(float Minimum, float Maximum) {
    /// <summary>Whether <paramref name="value"/> is finite and within <c>[Minimum, Maximum]</c>.</summary>
    /// <param name="value">The value to test.</param>
    /// <returns><see langword="true"/> when in range.</returns>
    public bool Contains(float value) {
        return (float.IsFinite(f: value) && (value >= Minimum) && (value <= Maximum));
    }
}
